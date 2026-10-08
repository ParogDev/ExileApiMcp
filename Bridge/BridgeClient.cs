using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Bridge;

/// <summary>
/// TCP client for one HUD's "Whats An AI Bridge" plugin (newline-delimited JSON-RPC 2.0).
/// The port and auth token are re-read from the bridge directory on every connect, so HUD
/// restarts (new token) and the bridge's port fallback (bridge-port.txt) are picked up.
/// Connects are bounded: a HUD that isn't running fails fast with a clear message instead of
/// hanging the tool call.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JToken>> _pending = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCts;
    private long _nextId;
    private string? _authToken;

    public BridgeClient(string game, string bridgeDir, int defaultPort)
    {
        Game = game;
        BridgeDir = bridgeDir;
        DefaultPort = defaultPort;
    }

    /// <summary>"poe1", "poe2", or "auto" for a legacy single BRIDGE_DIR configuration.</summary>
    public string Game { get; }

    public string BridgeDir { get; }

    public int DefaultPort { get; }

    public int? Port { get; private set; }

    // Socket.Connected only turns false after a failed operation, so a connection the HUD closed
    // (HUD restart) would still look alive; the read loop sets this when it sees the close.
    private volatile bool _remoteClosed;

    public bool IsConnected => _client?.Connected == true && _writer != null && !_remoteClosed;

    /// <summary>True when the bridge has written its token file, i.e. the HUD plugin is (or was) running.</summary>
    public bool LooksAvailable => File.Exists(Path.Combine(BridgeDir, "bridge-token.txt"));

    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;
        await _connectLock.WaitAsync(ct);
        try
        {
            if (IsConnected) return;
            await ConnectAsync(ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var tokenPath = Path.Combine(BridgeDir, "bridge-token.txt");
        if (!File.Exists(tokenPath))
            throw new BridgeUnavailableException(Game,
                $"no bridge token in {BridgeDir} - is the HUD running with 'Whats An AI Bridge' enabled?");

        _authToken = (await File.ReadAllTextAsync(tokenPath, ct)).Trim();
        var portPath = Path.Combine(BridgeDir, "bridge-port.txt");
        Port = File.Exists(portPath) && int.TryParse((await File.ReadAllTextAsync(portPath, ct)).Trim(), out var p)
            ? p
            : DefaultPort;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ConnectBudget);
        Exception? last = null;
        var delay = TimeSpan.FromMilliseconds(250);
        while (!budget.IsCancellationRequested)
        {
            try
            {
                Disconnect();
                _remoteClosed = false;
                _client = new TcpClient { NoDelay = true };
                await _client.ConnectAsync(System.Net.IPAddress.Loopback, Port.Value, budget.Token);
                var stream = _client.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                _writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                _readCts = new CancellationTokenSource();
                _ = Task.Run(() => ReadLoopAsync(_readCts.Token));

                var pong = await SendRequestAsync("ping", null, budget.Token);
                if (pong.ToString() == "pong")
                {
                    Console.Error.WriteLine($"[Bridge:{Game}] connected on 127.0.0.1:{Port}");
                    return;
                }
                last = new InvalidOperationException($"unexpected ping reply: {pong}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }

            Disconnect();
            try { await Task.Delay(delay, budget.Token); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
        }

        ct.ThrowIfCancellationRequested();
        Disconnect();
        throw new BridgeUnavailableException(Game,
            $"could not reach the bridge on 127.0.0.1:{Port} within {ConnectBudget.TotalSeconds:0}s ({last?.Message ?? "timeout"})");
    }

    /// <summary>Sends one JSON-RPC request. Bridge-level errors (JSON-RPC error objects) throw <see cref="BridgeException"/>.</summary>
    public async Task<JToken> SendRequestAsync(string method, JObject? parameters, CancellationToken ct = default)
    {
        var writer = _writer;
        if (writer == null || _client?.Connected != true)
            throw new BridgeUnavailableException(Game, "not connected");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters != null) request["params"] = parameters;
        // Every bridge method requires the token (bridge protocol v2), including ping.
        if (_authToken != null) request["token"] = _authToken;

        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                await writer.WriteLineAsync(request.ToString(Formatting.None).AsMemory(), ct);
            }
            catch (IOException ex)
            {
                // Nothing reached the bridge, so the caller may safely reconnect and resend.
                Disconnect();
                throw new BridgeNotSentException(Game, $"connection lost before sending ({ex.Message})");
            }
            finally
            {
                _writeLock.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                return await tcs.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new BridgeException(Game, $"request '{method}' timed out after {RequestTimeout.TotalSeconds:0}s");
            }
        }
        catch (IOException ex)
        {
            Disconnect();
            throw new BridgeUnavailableException(Game, $"connection lost ({ex.Message})");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _reader is { } reader)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var msg = JObject.Parse(line);
                    var id = msg["id"]?.Value<long>() ?? 0;
                    if (!_pending.TryRemove(id, out var tcs)) continue;
                    if (msg["error"] is { } error)
                        tcs.TrySetException(new BridgeException(Game, error["message"]?.Value<string>() ?? "unknown bridge error"));
                    else
                        tcs.TrySetResult(msg["result"] ?? JValue.CreateNull());
                }
                catch (JsonException)
                {
                    // Malformed line - skip it.
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        if (!ct.IsCancellationRequested) _remoteClosed = true;

        foreach (var kvp in _pending)
            if (_pending.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetException(new BridgeUnavailableException(Game, "connection to the HUD bridge was lost"));
    }

    private void Disconnect()
    {
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _reader = null;
        _writer = null;
        _client = null;
    }

    public void Dispose()
    {
        Disconnect();
        _writeLock.Dispose();
        _connectLock.Dispose();
    }
}

/// <summary>The bridge answered with an error, or a request timed out.</summary>
public class BridgeException(string game, string message) : Exception($"[{game}] {message}")
{
    public string Game { get; } = game;
}

/// <summary>The HUD bridge for a game is not reachable (HUD closed, plugin disabled, wrong dir).</summary>
public class BridgeUnavailableException(string game, string message) : BridgeException(game, message);

/// <summary>The connection failed before the request was written: it was not processed, so a resend is safe.</summary>
public sealed class BridgeNotSentException(string game, string message) : BridgeUnavailableException(game, message);
