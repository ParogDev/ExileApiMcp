using System.Net;
using System.Net.Sockets;

namespace ExileApiMcp.Supervisor;

/// <summary>
/// http: the public port (50910) is ours; each worker listens on a free loopback port, and every accepted connection is
/// piped byte for byte to the active worker (no HTTP parsing: the worker does Host/Origin/token checks as before). On a
/// swap, new connections go to the new worker and the old one is told to drain: it stops accepting, finishes the
/// requests it has (a blocking observe_wait included) and exits. Open connections keep their worker until they close.
/// </summary>
public sealed class HttpSupervisor(SupervisorOptions opts) : SupervisorBase(opts)
{
    private CancellationToken _ct;
    // --idle-exit: connections open right now (any worker, a blocking observe_wait included) and when the last one closed.
    // Counted under _idleLock at accept, so the idle decision and a new connection can't cross: once _stopping is set the
    // listener is closed (new callers get connection refused, and mcp-call starts a fresh server).
    private int _open;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private readonly object _idleLock = new();
    private bool _stopping;
    private TcpListener? _listener;

    public async Task<int> RunAsync()
    {
        using var cts = new CancellationTokenSource();
        _ct = cts.Token;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var listener = _listener = new TcpListener(IPAddress.Loopback, Opts.Port);
        try { listener.Start(); }
        catch (SocketException ex)
        {
            Log($"port {Opts.Port} is taken ({ex.SocketErrorCode}): another ExileApiMcp HTTP server runs already? (netstat -ano | findstr :{Opts.Port})");
            return 1;
        }
        if (!await SwapAsync(Choose(), "start", _ct) && !await SwapAsync(Builds.Local(Opts.FallbackDir), "start (fallback)", _ct))
        {
            Log("no server build starts; exiting");
            return 1;
        }
        Log($"HTTP on http://127.0.0.1:{Opts.Port}/mcp ({Mode})" + (Opts.IdleExit is { } idle ? $"; stops after {idle.TotalMinutes:0.##} min without a connection" : ""));
        _ = FollowDeploymentsAsync(_ct);
        if (Opts.IdleExit is { } limit) _ = StopWhenIdleAsync(limit, cts);
        try
        {
            while (!_ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(_ct);
                lock (_idleLock)
                {
                    if (_stopping) { client.Dispose(); break; }
                    _open++;
                }
                _ = Task.Run(() => PipeAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException && _stopping) { }   // closed by the idle stop
        finally
        {
            listener.Stop();
            Worker[] all;
            lock (Gate) all = Workers.ToArray();
            foreach (var w in all) w.CloseInput();
            foreach (var w in all) if (!w.Proc.WaitForExit(10000)) w.Kill();
            RemoveStatus();
        }
        return 0;
    }

    /// <summary>
    /// --idle-exit: stop once no connection has been open for <paramref name="limit"/>. A request in progress holds its
    /// connection, so a blocking wait (observe_wait, incident_wait, experiment_queue_wait) counts as activity for as long as
    /// it runs; so does an open control center (its push stream). The next tools\mcp-call.ps1 starts a fresh server, on
    /// the deployed build.
    /// </summary>
    private async Task StopWhenIdleAsync(TimeSpan limit, CancellationTokenSource cts)
    {
        var tick = TimeSpan.FromSeconds(Math.Clamp(limit.TotalSeconds / 4, 1, 15));
        while (!_ct.IsCancellationRequested)
        {
            try { await Task.Delay(tick, _ct); } catch (OperationCanceledException) { return; }
            TimeSpan idleFor;
            lock (_idleLock)
            {
                if (_open > 0) continue;
                idleFor = DateTime.UtcNow - new DateTime(_lastActivityTicks, DateTimeKind.Utc);
                if (idleFor < limit) continue;
                _stopping = true;
                _listener?.Stop();   // from here on, callers get connection refused (mcp-call then starts a new server)
            }
            Log($"no connection for {idleFor.TotalMinutes:0.#} min (--idle-exit {limit.TotalMinutes:0.##}): stopping; the next mcp-call starts it again");
            try { cts.Cancel(); } catch (ObjectDisposedException) { }   // RunAsync may already be done (the listener closed above)
            return;
        }
    }

    /// <summary>Pipes one accepted connection (already counted in _open by the accept loop).</summary>
    private async Task PipeAsync(TcpClient client)
    {
        try { await PipeToWorkerAsync(client); }
        finally
        {
            lock (_idleLock) { _lastActivityTicks = DateTime.UtcNow.Ticks; _open--; }
        }
    }

    private async Task PipeToWorkerAsync(TcpClient client)
    {
        using (client)
        {
            Worker? w = null;
            // A swap or restart in progress: hold the connection briefly instead of refusing it.
            for (var i = 0; i < 300 && !_ct.IsCancellationRequested; i++)
            {
                lock (Gate) if (Active is { State: "active", Port: not null } a) { w = a; Interlocked.Increment(ref w.Connections); break; }
                await Task.Delay(100);
            }
            if (w == null) return;
            try
            {
                using var upstream = new TcpClient { NoDelay = true };
                await upstream.ConnectAsync(IPAddress.Loopback, w.Port!.Value, _ct);
                client.NoDelay = true;
                var a = client.GetStream();
                var b = upstream.GetStream();
                // Either side closing ends both: half-open SSE streams must not hold a draining worker forever.
                await Task.WhenAny(a.CopyToAsync(b, _ct), b.CopyToAsync(a, _ct));
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { Interlocked.Decrement(ref w.Connections); }
        }
    }

    protected override async Task<Worker> LaunchAsync(BuildRef b, CancellationToken ct)
    {
        var port = FreePort();
        var args = new List<string> { "--http", "--port", port.ToString() };
        args.AddRange(Opts.WorkerArgs);
        var env = WorkerEnv(b);
        env["HEXILE_PUBLIC_PORT"] = Opts.Port.ToString();   // what it tells people to connect to
        var w = Worker.Start(b, args, env, port, (_, line) => Console.Error.WriteLine(line), x => HandleExit(x, _ => { }, _ct));
        lock (Gate) Workers.Add(w);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (w.Proc.HasExited) throw new InvalidOperationException($"it exited with code {SafeExitCode(w)}" + (w.LastStderr != null ? $": {w.LastStderr}" : ""));
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, ct);
                return w;
            }
            catch (SocketException) { await Task.Delay(200, ct); }
        }
        w.Kill();
        throw new InvalidOperationException("it did not listen within 30 s" + (w.LastStderr != null ? $" (last log line: {w.LastStderr})" : ""));
    }

    protected override void Retire(Worker old)
    {
        old.Send("drain");     // Program.cs (supervised http): stop accepting, finish what runs, exit
        old.CloseInput();
    }

    protected override void OnSwapped(Worker? now) { }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
