using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExileApiMcp.Supervisor;

/// <summary>Command line: --fallback &lt;dir&gt; (the local build), --http [--port N]; everything else goes to the worker.</summary>
public sealed record SupervisorOptions(string FallbackDir, bool Http, int Port, bool Pinned, IReadOnlyList<string> WorkerArgs)
{
    public static SupervisorOptions Parse(string[] args)
    {
        string? fallback = null; var http = false; int? port = null; var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--fallback" when i + 1 < args.Length: fallback = args[++i]; break;
                case "--http": http = true; break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
                default: rest.Add(args[i]); break;
            }
        }
        if (fallback == null) throw new ArgumentException("--fallback <dir with ExileApiMcp.dll> is required (run.cmd passes it)");
        port ??= int.TryParse(Environment.GetEnvironmentVariable("MCP_HTTP_PORT"), out var envPort) ? envPort : 50910;
        var pinned = Environment.GetEnvironmentVariable("HEXILE_MCP_LOCAL") is "1" or "true";
        return new SupervisorOptions(Path.GetFullPath(fallback), http, port.Value, pinned, rest);
    }
}

/// <summary>
/// What both modes share: which build to run, following deployments (current.json, polled), the environment a worker gets,
/// and the status file other tools read (supervisors\&lt;pid&gt;.json: this session's server, its build, what drains).
/// </summary>
public abstract class SupervisorBase(SupervisorOptions opts)
{
    protected readonly SupervisorOptions Opts = opts;
    protected readonly object Gate = new();
    protected readonly List<Worker> Workers = new();
    protected Worker? Active;
    protected bool Swapping;
    private string? _failedDir, _lastWhy;
    private JsonObject? _lastSwap;
    private readonly List<DateTime> _crashes = new();
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly string _statusFile = Path.Combine(Builds.SupervisorsDir, $"{Environment.ProcessId}.json");
    protected static void Log(string s) => Console.Error.WriteLine($"[supervisor] {s}");

    protected string Mode => Opts.Pinned ? "local" : "supervised";

    /// <summary>The build a new worker should run now.</summary>
    protected BuildRef Choose()
    {
        if (Opts.Pinned) return Builds.Local(Opts.FallbackDir);
        return Builds.Deployed(Why) ?? Builds.Local(Opts.FallbackDir);
    }

    private void Why(string why) { if (why != _lastWhy) { _lastWhy = why; Log(why + "; running the local build"); } }

    protected Dictionary<string, string> WorkerEnv(BuildRef b) => new()
    {
        // The session stays one session to the HUD across swaps (Hosting/SessionIdentity.cs uses it for its id).
        ["HEXILE_SESSION_PID"] = Environment.ProcessId.ToString(),
        ["HEXILE_MCP_MODE"] = Mode,
        ["HEXILE_SUPERVISED"] = "1",
    };

    protected abstract Task<Worker> LaunchAsync(BuildRef b, CancellationToken ct);
    protected abstract void Retire(Worker old);
    /// <summary>Under Gate, after a swap or a failed one: hand queued calls to <paramref name="now"/> (null: no worker, fail them).</summary>
    protected abstract void OnSwapped(Worker? now);

    /// <summary>Polls current.json; a different build starts a new worker, and the old one drains.</summary>
    protected async Task FollowDeploymentsAsync(CancellationToken ct)
    {
        if (Opts.Pinned) return;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }
            var want = Builds.Deployed(Why);
            if (want == null) continue;
            bool go;
            lock (Gate) go = !Swapping && Active != null && !want.SameAs(Active.Build) && !string.Equals(want.Dir, _failedDir, StringComparison.OrdinalIgnoreCase);
            if (go) await SwapAsync(want, "deployed", ct);
        }
    }

    /// <summary>Start a worker on <paramref name="b"/>; once it answers, it takes new calls and the previous one drains.</summary>
    protected async Task<bool> SwapAsync(BuildRef b, string why, CancellationToken ct)
    {
        Worker? old;
        lock (Gate) { if (Swapping) return false; Swapping = true; old = Active; }
        var from = old?.Build.Describe;
        try
        {
            var nw = await LaunchAsync(b, ct);
            lock (Gate)
            {
                nw.State = "active";
                Active = nw;
                if (old is { State: "active" }) { old.State = "draining"; Retire(old); }
                Swapping = false;
                OnSwapped(nw);
            }
            _failedDir = null;
            _lastSwap = new JsonObject { ["at"] = DateTime.UtcNow.ToString("O"), ["why"] = why, ["from"] = from, ["to"] = b.Describe, ["ok"] = true };
            Log(old == null ? $"running {b.Describe}" : $"switched to {b.Describe} ({why}); the previous worker finishes its calls");
            WriteStatus();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _failedDir = b.Dir;
            _lastSwap = new JsonObject { ["at"] = DateTime.UtcNow.ToString("O"), ["why"] = why, ["from"] = from, ["to"] = b.Describe, ["ok"] = false, ["error"] = ex.Message };
            Log($"could not start {b.Describe}: {ex.Message}" + (old != null ? "; staying on " + from : ""));
            lock (Gate) { Swapping = false; OnSwapped(Active); }
            WriteStatus();
            return false;
        }
    }

    /// <summary>A worker exited. The active one is replaced (a crash loop stops after 3 in a minute: the calls get the reason).</summary>
    protected void HandleExit(Worker w, Action<Worker> failInFlight, CancellationToken ct)
    {
        bool restart;
        lock (Gate)
        {
            var wasActive = w == Active;
            w.State = "exited";
            Workers.Remove(w);
            failInFlight(w);
            if (wasActive) Active = null;
            var now = DateTime.UtcNow;
            _crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            if (wasActive) _crashes.Add(now);
            restart = wasActive && _crashes.Count < 3 && !ct.IsCancellationRequested;
        }
        var code = SafeExitCode(w);
        Log($"worker {w.Seq} ({w.Build.Describe}) exited with code {code}" + (code != 0 && w.LastStderr != null ? $"; its last log line: {w.LastStderr}" : ""));
        if (restart) _ = Task.Run(() => SwapAsync(Choose(), "restart after exit", ct));
        WriteStatus();
    }

    protected bool CrashLooping { get { lock (Gate) return Active == null && !Swapping && _crashes.Count >= 3; } }
    protected static int? SafeExitCode(Worker w) { try { return w.Proc.ExitCode; } catch (InvalidOperationException) { return null; } }

    protected void WriteStatus()
    {
        try
        {
            JsonObject Info(Worker w) => new()
            {
                ["seq"] = w.Seq, ["pid"] = w.Pid, ["state"] = w.State, ["dir"] = w.Build.Dir, ["version"] = w.Build.Version, ["sha"] = w.Build.Sha,
                ["source"] = w.Build.Source, ["inFlight"] = w.InFlight.Count, ["connections"] = w.Connections, ["port"] = w.Port,
                ["startedAt"] = w.StartedAt.ToString("O"),
            };
            JsonObject o;
            lock (Gate)
                o = new JsonObject
                {
                    ["pid"] = Environment.ProcessId, ["transport"] = Opts.Http ? "http" : "stdio", ["port"] = Opts.Http ? Opts.Port : null,
                    ["mode"] = Mode, ["cwd"] = Environment.CurrentDirectory, ["startedAt"] = _startedAt.ToString("O"),
                    ["workers"] = new JsonArray(Workers.Select(w => (JsonNode)Info(w)).ToArray()),
                    ["lastSwap"] = _lastSwap?.DeepClone(),
                };
            Directory.CreateDirectory(Builds.SupervisorsDir);
            var tmp = _statusFile + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _statusFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    protected void RemoveStatus() { try { File.Delete(_statusFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
