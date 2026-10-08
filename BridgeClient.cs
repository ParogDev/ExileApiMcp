using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp;

/// <summary>
/// TCP client that connects to the HUD plugin's TCP bridge server.
/// Sends JSON-RPC 2.0 requests and correlates responses by ID.
/// Handles reconnection with exponential backoff.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JToken>> _pending = new();
    private long _nextId = 1;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly int _port;
    private readonly string _bridgeDir;
    private string? _authToken;

    public bool IsConnected => _client?.Connected == true;

    public BridgeClient(int port, string bridgeDir)
    {
        _port = port;
        _bridgeDir = bridgeDir;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // Read auth token
        var tokenPath = Path.Combine(_bridgeDir, "bridge-token.txt");
        if (File.Exists(tokenPath))
            _authToken = (await File.ReadAllTextAsync(tokenPath, ct)).Trim();

        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(10);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _client?.Dispose();
                _client = new TcpClient { NoDelay = true };
                await _client.ConnectAsync(System.Net.IPAddress.Loopback, _port, ct);

                var stream = _client.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                _writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token), _readCts.Token);

                // Verify with ping
                var pong = await SendRequestAsync("ping", null, ct);
                if (pong.ToString() == "pong")
                {
                    Console.Error.WriteLine($"[BridgeClient] Connected to plugin on port {_port}");
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[BridgeClient] Connection failed: {ex.Message}, retrying in {delay.TotalSeconds}s");
                Disconnect();
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, maxDelay.TotalSeconds));
            }
        }
    }

    public async Task<JToken> SendRequestAsync(string method, JObject? parameters, CancellationToken ct = default)
    {
        if (_writer == null || _client?.Connected != true)
            throw new InvalidOperationException("Not connected to plugin");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };

        if (parameters != null)
            request["params"] = parameters;

        // Attach auth token
        // Every bridge method requires the token (bridge protocol v2), including ping.
        if (_authToken != null)
            request["token"] = _authToken;

        var json = request.ToString(Formatting.None);

        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), ct);
        }
        finally
        {
            _writeLock.Release();
        }

        // Wait with timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"Request '{method}' timed out after 15 seconds");
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
            while (!ct.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var msg = JObject.Parse(line);
                    var id = msg["id"]?.Value<long>() ?? 0;

                    if (_pending.TryRemove(id, out var tcs))
                    {
                        var error = msg["error"];
                        if (error != null)
                            tcs.TrySetException(new Exception(error["message"]?.Value<string>() ?? "Unknown error"));
                        else
                            tcs.TrySetResult(msg["result"] ?? JValue.CreateNull());
                    }
                }
                catch (JsonException)
                {
                    // Malformed response, skip
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        // Connection lost - fail all pending requests
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetException(new IOException("Connection to plugin lost"));
        }
    }

    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;
        Disconnect();
        await ConnectAsync(ct);
    }

    private void Disconnect()
    {
        _readCts?.Cancel();
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _reader = null;
        _writer = null;
        _client = null;
    }

    public void Dispose()
    {
        _readCts?.Cancel();
        Disconnect();
        _readCts?.Dispose();
        _writeLock.Dispose();
    }
}
