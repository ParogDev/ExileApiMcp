using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExileApiMcp.Supervisor;

/// <summary>
/// stdio: the client's pipe ends here and stays open while workers come and go. Messages are newline-delimited JSON-RPC.
///   client request    -> the active worker (queued while a swap or a restart is under way); its id is remembered as owed
///   client response   -> the worker whose server-to-client request it answers (those ids are rewritten, see below)
///   client notification -> every live worker; notifications/cancelled only to the worker owning that request
///   worker response   -> the client; the worker owes one call less, and a draining worker with none left is closed
///   worker request    -> the client, id rewritten to "sup&lt;n&gt;" so two workers' ids can't collide
///   worker notification -> the client (a draining worker's progress for its own calls included)
/// A new worker gets the client's initialize (and notifications/initialized) replayed; its answer stays here. A client
/// without initialize (stateless MCP 2026-07-28: every request carries its own _meta) needs none; a ping proves the worker
/// answers. After a swap the client is told tools/prompts/resources may have changed. A worker that dies mid-call fails
/// those calls with a message naming the exit, and the supervisor starts another.
/// </summary>
public sealed class StdioSupervisor(SupervisorOptions opts) : SupervisorBase(opts)
{
    private readonly Queue<string> _pending = new();
    private readonly Dictionary<string, (Worker W, JsonNode? Id)> _owner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Worker W, JsonNode? Id)> _serverRequests = new(StringComparer.Ordinal);
    private readonly object _outLock = new();
    private readonly Stream _out = Console.OpenStandardOutput();
    private string? _initLine, _initKey, _initializedLine;
    private JsonObject? _serverCaps;
    private int _serverSeq;
    private CancellationToken _ct;

    public async Task<int> RunAsync()
    {
        using var cts = new CancellationTokenSource();
        _ct = cts.Token;
        if (!await SwapAsync(Choose(), "start", _ct))
        {
            // The deployed build didn't start: the local one must (run.cmd just built it).
            var local = Builds.Local(Opts.FallbackDir);
            if (!await SwapAsync(local, "start (fallback)", _ct)) { Log("no server build starts; exiting"); return 1; }
        }
        _ = FollowDeploymentsAsync(_ct);

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? line;
        while ((line = await stdin.ReadLineAsync()) != null)
            if (line.Length > 0) OnClientLine(line);

        // The client is gone: workers see their stdin end and stop.
        cts.Cancel();
        Worker[] all;
        lock (Gate) all = Workers.ToArray();
        foreach (var w in all) w.CloseInput();
        foreach (var w in all) if (!w.Proc.WaitForExit(5000)) w.Kill();
        RemoveStatus();
        return 0;
    }

    private static string Key(JsonNode? id) => id?.ToJsonString() ?? "null";

    private void OnClientLine(string line)
    {
        JsonObject? msg = null;
        try { msg = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { }
        lock (Gate)
        {
            if (msg == null) { ToActiveOrQueue(line, null, null); return; }   // the server answers the parse error
            var method = msg["method"]?.GetValue<string>();
            var hasId = msg.TryGetPropertyValue("id", out var id) && id != null;
            if (method != null && hasId)
            {
                if (method == "initialize") { _initLine = line; _initKey = Key(id); }
                ToActiveOrQueue(line, Key(id), id!.DeepClone());
            }
            else if (method != null)
            {
                if (method == "notifications/initialized") _initializedLine = line;
                if (method == "notifications/cancelled" && _owner.TryGetValue(Key(msg["params"]?["requestId"]), out var o)) { o.W.Send(line); return; }
                if (Swapping || Active == null) { _pending.Enqueue(line); return; }
                foreach (var w in Workers.Where(w => w.State is "active" or "draining")) w.Send(line);
            }
            else if (hasId && _serverRequests.Remove(Key(id), out var req))
            {
                msg["id"] = req.Id?.DeepClone();
                req.W.Send(msg.ToJsonString());
            }
        }
    }

    /// <summary>Under Gate.</summary>
    private void ToActiveOrQueue(string line, string? key, JsonNode? id)
    {
        if (Swapping || Active == null)
        {
            if (CrashLooping && key != null) { WriteClient(Error(id, "the ExileApiMcp server keeps exiting at start (3 times in a minute); see the server log, fix the build, then deploy again")); return; }
            _pending.Enqueue(line);
            return;
        }
        if (key != null) { Active.InFlight.Add(key); _owner[key] = (Active, id); }
        if (!Active.Send(line) && key != null) { Active.InFlight.Remove(key); _owner.Remove(key); WriteClient(Error(id, "the ExileApiMcp server's input closed; call again")); }
    }

    private void OnWorkerLine(Worker w, string line)
    {
        JsonObject? msg = null;
        try { msg = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { }
        if (msg == null) { WriteClient(line); return; }
        var method = msg["method"]?.GetValue<string>();
        var hasId = msg.TryGetPropertyValue("id", out var id) && id != null;
        if (method == null && hasId)
        {
            var key = Key(id);
            if (w.ProbeKey == key) { w.ProbeReply.TrySetResult(line); return; }
            lock (Gate)
            {
                w.InFlight.Remove(key);
                _owner.Remove(key);
                if (key == _initKey && msg["result"]?["capabilities"] is JsonObject caps) _serverCaps = (JsonObject)caps.DeepClone();
                if (w.State == "draining" && w.InFlight.Count == 0) w.CloseInput();
            }
            WriteClient(line);
            return;
        }
        if (method != null && hasId)
        {
            lock (Gate)
            {
                var sid = $"sup{++_serverSeq}";
                _serverRequests[Key(JsonValue.Create(sid))] = (w, id!.DeepClone());
                msg["id"] = sid;
            }
            WriteClient(msg.ToJsonString());
            return;
        }
        WriteClient(line);
    }

    protected override async Task<Worker> LaunchAsync(BuildRef b, CancellationToken ct)
    {
        var w = Worker.Start(b, Opts.WorkerArgs, WorkerEnv(b), null, OnWorkerLine, x => HandleExit(x, FailInFlight, _ct));
        lock (Gate) Workers.Add(w);
        try
        {
            string probe;
            string? initialized;
            lock (Gate) { probe = _initLine ?? ""; initialized = _initializedLine; }
            var probeId = $"sup-probe-{w.Seq}";
            w.ProbeKey = Key(JsonValue.Create(probeId));
            JsonObject msg;
            if (probe.Length > 0) { msg = (JsonObject)JsonNode.Parse(probe)!; msg["id"] = probeId; }
            else msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = probeId, ["method"] = "ping" };   // any answer, even an error, proves it listens
            w.Send(msg.ToJsonString());
            var reply = await w.ProbeReply.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (probe.Length > 0 && JsonNode.Parse(reply)?["error"] is JsonNode err) throw new InvalidOperationException($"it refused the replayed initialize: {err.ToJsonString()}");
            if (initialized != null) w.Send(initialized);
            return w;
        }
        catch (TimeoutException)
        {
            w.Kill();
            throw new InvalidOperationException("it did not answer within 30 s" + (w.LastStderr != null ? $" (last log line: {w.LastStderr})" : ""));
        }
        catch
        {
            w.Kill();
            throw;
        }
    }

    protected override void Retire(Worker old)
    {
        if (old.InFlight.Count == 0) old.CloseInput();
    }

    protected override void OnSwapped(Worker? now)
    {
        if (now != null && now.Seq != _lastSwappedTo) { _lastSwappedTo = now.Seq; _launched++; }
        while (_pending.Count > 0)
        {
            var line = _pending.Dequeue();
            JsonObject? msg = null;
            try { msg = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { }
            var id = msg != null && msg.TryGetPropertyValue("id", out var i) ? i : null;
            var isRequest = msg?["method"] != null && id != null;
            if (now == null) { if (isRequest) WriteClient(Error(id, "no ExileApiMcp server is running (it failed to start; see the server log)")); continue; }
            if (isRequest) { var key = Key(id); now.InFlight.Add(key); _owner[key] = (now, id!.DeepClone()); }
            now.Send(line);
        }
        // A replacement (not the first worker, and only once per worker): the new build may list other tools, prompts
        // or resources, so a client that initialized re-reads them.
        if (now != null && _initLine != null && _launched > 1 && _notifiedFor != now.Seq)
        {
            _notifiedFor = now.Seq;
            foreach (var kind in new[] { "tools", "prompts", "resources" })
                if (_serverCaps?[kind]?["listChanged"]?.GetValue<bool>() == true || kind == "tools")
                    WriteClient(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = $"notifications/{kind}/list_changed" }.ToJsonString());
        }
    }

    private int _launched, _lastSwappedTo, _notifiedFor;

    /// <summary>Under Gate (HandleExit): calls a dead worker owed get an error naming why, so the agent can call again.</summary>
    private void FailInFlight(Worker w)
    {
        foreach (var key in w.InFlight.ToList())
            if (_owner.Remove(key, out var o))
                WriteClient(Error(o.Id, $"the ExileApiMcp server exited during this call (code {SafeExitCode(w)}{(w.LastStderr != null ? $", last log line: {w.LastStderr}" : "")}); call again"));
        w.InFlight.Clear();
        foreach (var k in _serverRequests.Where(kv => kv.Value.W == w).Select(kv => kv.Key).ToList()) _serverRequests.Remove(k);
    }

    private static string Error(JsonNode? id, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = -32603, ["message"] = message } }.ToJsonString();

    private void WriteClient(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_outLock)
        {
            try { _out.Write(bytes); _out.Flush(); }
            catch (IOException) { }   // the client is gone: stdin ends next and we exit
        }
    }
}
