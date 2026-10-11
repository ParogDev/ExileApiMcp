using System.Diagnostics;
using System.Text.Json;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using ExileApiMcp.Incidents;
using ExileApiMcp.Tools;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Dispatch;

/// <summary>
/// The F8 feedback dispatcher: one fresh, short Claude Code session per press, no idle session.
///
/// Whats A Route publishes each F8 press as a "feedback" event through the AI Bridge (AIBridge.Publish; bridge
/// Shared/PubSub.cs). This service, hosted by the shared HTTP server (127.0.0.1:50910, which follows mcp_deploy and stays
/// up while a HUD is: see KeepAliveAsync), reads them (pubsub.events, as subscriber "route-dispatcher"), claims each one
/// exactly once (pubsub.claim stage handle: a second dispatcher, e.g. the draining worker of a swap, gets "claimed"),
/// says "Claude is on it" on the card (feedback.reply status claimed), writes the handler's brief and starts
/// `claude -p --worktree route-f8-&lt;seq&gt;` in the main checkout with the scrubbed environment. When the handler exits,
/// its final message goes to the card (pubsub.claim stage reply first, so it is said once), and its worktree is removed
/// when it holds nothing.
///
/// One handler at a time (they share route.live.json): later presses wait, and the card says so. Every link that can break
/// is said on the card and raised as incident feedback-dispatch:machine (CLI not found, not logged in, the session
/// failing to start), never dropped: a press stays unclaimed until a handler can start, and is retried every minute.
/// State for agents: handlers\dispatcher.json (MCP feedback_dispatcher) and handlers\&lt;seq&gt;\ (brief, output, stderr).
/// </summary>
public sealed class FeedbackDispatcher(BridgeRegistry bridges) : BackgroundService
{
    public const string Subscriber = "route-dispatcher", IncidentKey = "feedback-dispatch:machine";
    private static readonly TimeSpan PressMaxAge = TimeSpan.FromHours(2), HandlerMaxRun = TimeSpan.FromMinutes(40),
        RetryEvery = TimeSpan.FromMinutes(1), KeepAliveAfterBridge = TimeSpan.FromMinutes(20);

    private readonly string _game = Environment.GetEnvironmentVariable("HEXILE_DISPATCH_GAME") is { Length: > 0 } g ? g : "poe2";
    private readonly CallerIdentity _as = new("route-dispatcher", null, null);
    /// <summary>Who claims: the claim lapses (ttl) if this server dies before the handler ends; release names the holder.</summary>
    private static readonly string By = $"{Subscriber}@{Environment.ProcessId}";
    private long _cursor;
    private readonly Dictionary<long, JObject> _presses = new();         // unclaimed or waiting presses by seq
    private readonly HashSet<long> _toldQueued = new(), _toldFailed = new();
    private readonly List<JObject> _replies = new();                     // recent feedback.reply events (the last answers, done markers)
    private readonly List<JObject> _trace = new();                       // recent route + feedback events, for briefs
    private DateTime _nextEnvCheck = DateTime.MinValue, _bridgeOkAt = DateTime.MinValue;
    private (string? Exe, bool LoggedIn, string? Why, string? Fix) _env;
    private FeedbackHandlerState? _running;
    private Process? _proc;
    private string _status = "starting", _why = "";

    /// <summary>On the shared HTTP server only (HEXILE_DISPATCH=1 forces it on elsewhere, 0 off): one dispatcher per machine.</summary>
    public static bool Enabled(int publicPort) => Environment.GetEnvironmentVariable("HEXILE_DISPATCH") switch
    {
        "1" => true,
        "0" => false,
        _ => publicPort == 50910,
    };

    public static string LiveDir => Environment.GetEnvironmentVariable("HEXILE_ROUTE_LIVE") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexile", "WhatsARoute");

    public static string HandlersDir => Path.Combine(LiveDir, "handlers");
    public static string StatusFile => Path.Combine(HandlersDir, "dispatcher.json");

    protected override Task ExecuteAsync(CancellationToken stop) => CallerIdentity.RunAsAsync(_as, async () =>
    {
        _ = KeepAliveAsync(stop);
        Adopt();
        while (!stop.IsCancellationRequested)
        {
            try { await StepAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { _status = "broken"; _why = $"dispatcher step: {ex.Message}"; Console.Error.WriteLine($"[Dispatch] {_why}"); }
            WriteStatus();
            try { await Task.Delay(1000, stop); } catch (OperationCanceledException) { break; }
        }
        _status = "stopped"; _why = "this server is stopping (a newer build took over, or it idled out): the next one carries on";
        WriteStatus();
    });

    private async Task StepAsync(CancellationToken ct)
    {
        await SuperviseAsync(ct);
        JToken r;
        try
        {
            (_, r) = await bridges.CallAsync(_game, "pubsub.events", new JObject
            {
                ["since"] = _cursor, ["limit"] = 500, ["subscriber"] = Subscriber,
                ["kinds"] = new JArray(FeedbackKit.PressKind, FeedbackKit.ReplyKind, FeedbackKit.TraceKind),
            }, ct);
        }
        catch (ModelContextProtocol.McpException ex)
        {
            _status = "idle"; _why = $"no {_game} HUD bridge: {ex.Message.Split('\n')[0]}";
            await Task.Delay(4000, ct);
            return;
        }
        if (r["error"] != null && r["ok"] == null)
        {
            _status = "broken";
            _why = r["error"]?.ToString() == "unknown_method" || r["message"]?.ToString()?.Contains("pubsub") == true
                ? $"the {_game} bridge has no pubsub.events (a build before pub/sub: update Whats An AI Bridge and restart the HUD): {r["message"]}"
                : $"pubsub.events: {r["error"]}: {r["message"]}";
            await Task.Delay(5000, ct);
            return;
        }
        _bridgeOkAt = DateTime.UtcNow;
        var seq = r["seq"]?.Value<long>() ?? 0;
        if (seq < _cursor) { _cursor = 0; _presses.Clear(); return; }   // the bridge's log was reset: read it again
        foreach (var e in (r["events"] as JArray ?? []).OfType<JObject>())
        {
            _cursor = Math.Max(_cursor, e["seq"]!.Value<long>());
            Take(e);
        }
        _status = "listening"; _why = "";
        // Presses claimed elsewhere (another dispatcher) leave the waiting list on their next read; refresh claims of the waiting.
        if (_running == null) await StartNextAsync(ct);
        else await TellQueuedAsync(ct);
    }

    private void Take(JObject e)
    {
        var kind = e["kind"]?.ToString();
        var seq = e["seq"]!.Value<long>();
        if (kind == FeedbackKit.ReplyKind)
        {
            _replies.Add(e);
            if (_replies.Count > 50) _replies.RemoveAt(0);
            return;
        }
        _trace.Add(e);
        if (_trace.Count > 400) _trace.RemoveAt(0);
        if (kind != FeedbackKit.PressKind) return;
        var verdict = e["data"]?["Event"]?.ToString();
        var age = DateTimeOffset.TryParse(e["at"]?.ToString(), out var at) ? DateTimeOffset.UtcNow - at : TimeSpan.Zero;
        if (verdict == "works" || age > PressMaxAge || e["claims"]?["reply"] != null) return;   // answered
        if (e["claims"]?["handle"] is JObject h && h["lapsed"]?.Value<bool>() != true) return;              // a live handler has it
        _presses[seq] = e;
    }

    // ── Starting a handler ───────────────────────────────────────────

    private async Task StartNextAsync(CancellationToken ct)
    {
        if (_presses.Count == 0) return;
        var seq = _presses.Keys.Min();
        var press = _presses[seq];
        if (!await EnvironmentOkAsync(seq, ct)) return;

        var (_, c) = await bridges.CallAsync(_game, "pubsub.claim", new JObject { ["seq"] = seq, ["stage"] = "handle", ["by"] = By, ["ttlSec"] = (HandlerMaxRun + TimeSpan.FromMinutes(10)).TotalSeconds }, ct);
        _presses.Remove(seq);
        if (c["ok"]?.Value<bool>() != true) { Console.Error.WriteLine($"[Dispatch] #{seq} not ours: {c["message"] ?? c["error"]}"); return; }
        await ReplyAsync(seq, "claimed", $"Claude is on it (#{seq}): a fresh session is reading your note.", ct);

        var dir = Path.Combine(HandlersDir, seq.ToString());
        var state = new FeedbackHandlerState { Seq = seq, Status = "starting", StartedAt = DateTimeOffset.UtcNow, Dir = dir };
        try
        {
            var repo = Repo() ?? throw new HandlerStartException("the main scaffolding checkout is unknown (HEXILE_REPO is not set and the HUD's Whats A Route junction does not resolve)", "Start the shared server through tools\\mcp-call.ps1 (it sets HEXILE_REPO).");
            Directory.CreateDirectory(dir);
            var briefPath = Path.Combine(dir, "brief.md");
            var previous = _trace.Where(t => t["kind"]?.ToString() == FeedbackKit.PressKind && t["seq"]!.Value<long>() < seq).Select(t => (long?)t["seq"]!.Value<long>()).LastOrDefault();
            var trace = _trace.Where(t => t["kind"]?.ToString() == FeedbackKit.TraceKind && t["seq"]!.Value<long>() < seq && t["seq"]!.Value<long>() > (previous ?? 0)).TakeLast(80).ToList();
            var replies = _replies.Where(x => x["data"]?["status"]?.ToString() == "done").TakeLast(3).ToList();
            File.WriteAllText(briefPath, FeedbackKit.Brief(press, trace, replies, Path.Combine(repo, "Plugins", "Whats A Route", "docs", "feedback-agent.md"), LiveDir, previous));
            File.WriteAllText(Path.Combine(dir, "prompt.txt"), FeedbackKit.Prompt(seq, briefPath));
            var mcpConfig = Path.Combine(dir, "mcp.json");
            File.WriteAllText(mcpConfig, McpConfigJson(seq));
            foreach (var f in new[] { "output.json", "stderr.log" }) File.Delete(Path.Combine(dir, f));

            var psi = new ProcessStartInfo("cmd.exe", FeedbackKit.CmdArguments(_env.Exe!, seq, dir, mcpConfig, [LiveDir, repo]))
            {
                WorkingDirectory = repo, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var k in psi.Environment.Keys.Where(FeedbackKit.Scrub).ToList()) psi.Environment.Remove(k);
            psi.Environment["HEXILE_AGENT"] = FeedbackKit.Label(seq);
            psi.Environment["HEXILE_ROUTE_PRESS"] = seq.ToString();
            _proc = Process.Start(psi) ?? throw new HandlerStartException("cmd.exe did not start", null);
            state.Pid = _proc.Id;
            state.Status = "running";
            state.Worktree = Path.Combine(repo, ".claude", "worktrees", FeedbackKit.Label(seq));
            _running = state;
            SaveHandler(state);
            Console.Error.WriteLine($"[Dispatch] #{seq}: handler pid {state.Pid} started ({_env.Exe})");
            IncidentStore.ResolveKeys([IncidentKey], $"a handler started for F8 press #{seq}");
            _toldFailed.Remove(seq);
        }
        catch (Exception ex)
        {
            var (why, fix) = ex is HandlerStartException h ? (h.Message, h.Fix) : ($"starting the handler failed: {ex.Message}", (string?)null);
            state.Status = "failed"; state.Error = why; state.EndedAt = DateTimeOffset.UtcNow;
            SaveHandler(state);
            await ReleaseAsync(seq, why, ct);
            await FailAsync(seq, why, fix, ct);
        }
    }

    private sealed class HandlerStartException(string message, string? fix) : Exception(message) { public string? Fix { get; } = fix; }

    /// <summary>The CLI is there and logged in (checked at most once a minute while broken, every 10 min while fine).</summary>
    private async Task<bool> EnvironmentOkAsync(long seq, CancellationToken ct)
    {
        if (DateTime.UtcNow >= _nextEnvCheck)
        {
            _env = await CheckEnvironmentAsync(ct);
            _nextEnvCheck = DateTime.UtcNow + (_env.Why == null ? TimeSpan.FromMinutes(10) : RetryEvery);
        }
        if (_env.Why == null) return true;
        _status = "broken"; _why = _env.Why;
        if (_toldFailed.Add(seq)) await FailAsync(seq, _env.Why + " (it is kept and handled once that is fixed)", _env.Fix, ct);
        return false;
    }

    private static async Task<(string? Exe, bool LoggedIn, string? Why, string? Fix)> CheckEnvironmentAsync(CancellationToken ct)
    {
        string? exe = Environment.GetEnvironmentVariable("HEXILE_CLAUDE_EXE") is { Length: > 0 } x && File.Exists(x) ? x : null;
        string looked = "HEXILE_CLAUDE_EXE";
        if (exe == null)
            (exe, looked) = FeedbackKit.FindCli(FeedbackKit.CliRoots(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        if (exe == null)
            return (null, false, $"the Claude Code CLI was not found (looked in {looked})", "Open the Claude desktop app once (it installs Claude Code there), or set HEXILE_CLAUDE_EXE.");
        try
        {
            var psi = new ProcessStartInfo(exe, "auth status") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var k in psi.Environment.Keys.Where(FeedbackKit.Scrub).ToList()) psi.Environment.Remove(k);
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True) return (exe, true, null, null);
            return (exe, false, "the Claude Code CLI is not logged in, so it can't start a session on its own",
                $"Once, in a terminal: \"{exe}\" auth login");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (exe, false, $"`claude auth status` failed: {ex.Message}", null);
        }
    }

    /// <summary>The main scaffolding checkout: HEXILE_REPO (tools\mcp-http-lib.ps1 sets it), else the target of the HUD's
    /// Plugins\Source\Whats A Route junction (it points into the main checkout).</summary>
    private string? Repo()
    {
        if (Environment.GetEnvironmentVariable("HEXILE_REPO") is { Length: > 0 } r && Directory.Exists(Path.Combine(r, ".git"))) return r;
        try
        {
            var hud = Path.GetDirectoryName(bridges.Resolve(_game).BridgeDir.TrimEnd('\\'));
            var link = new DirectoryInfo(Path.Combine(hud!, "Plugins", "Source", "Whats A Route")).LinkTarget;
            var main = link == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(link.TrimEnd('\\')));
            return main != null && Directory.Exists(Path.Combine(main, ".git")) ? main : null;
        }
        catch { return null; }
    }

    private static string McpConfigJson(long seq)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("HEXILE_PUBLIC_PORT"), out var p) ? p : 50910;
        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["exileapi"] = new Dictionary<string, object>
                {
                    ["type"] = "http", ["url"] = $"http://127.0.0.1:{port}/mcp",
                    ["headers"] = new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer " + LocalHttpSecurity.LoadOrCreateToken(),
                        [CallerIdentity.AgentHeader] = FeedbackKit.Label(seq),
                    },
                },
            },
        };
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Watching a handler ───────────────────────────────────────────

    /// <summary>A handler a previous worker started (a deploy swapped it, or this server restarted) is watched on.</summary>
    private void Adopt()
    {
        try
        {
            if (!Directory.Exists(HandlersDir)) return;
            foreach (var f in Directory.EnumerateFiles(HandlersDir, "state.json", SearchOption.AllDirectories))
            {
                var s = JsonSerializer.Deserialize<FeedbackHandlerState>(File.ReadAllText(f), Dto.Options);
                if (s?.Status != "running") continue;
                if (_running == null || s.Seq > _running.Seq) _running = s;
            }
            if (_running?.Pid is int pid)
            {
                try { _proc = Process.GetProcessById(pid); } catch { _proc = null; }   // gone: SuperviseAsync reads its output
                Console.Error.WriteLine($"[Dispatch] watching handler #{_running.Seq} (pid {pid}) started by an earlier server");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Dispatch] adopting handlers: {ex.Message}"); }
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var h = _running;
        if (h == null) return;
        var alive = _proc is { } p && !SafeHasExited(p);
        if (alive && h.StartedAt is { } started && DateTimeOffset.UtcNow - started > HandlerMaxRun)
        {
            try { _proc!.Kill(entireProcessTree: true); } catch { }
            alive = false;
            h.Error = $"the handler ran longer than {HandlerMaxRun.TotalMinutes:0} min and was stopped";
        }
        if (alive) return;

        var output = File.Exists(Path.Combine(h.Dir!, "output.json")) ? File.ReadAllText(Path.Combine(h.Dir!, "output.json")) : "";
        var (result, isError, sessionId, why) = FeedbackKit.ParseOutput(output);
        h.EndedAt = DateTimeOffset.UtcNow;
        h.SessionId = sessionId;
        h.Result = result;
        if (isError || h.Error != null)
        {
            var err = File.Exists(Path.Combine(h.Dir!, "stderr.log")) ? FeedbackKit.Clip(File.ReadAllText(Path.Combine(h.Dir!, "stderr.log")), 300) : "";
            h.Error ??= why + (result != null ? $": {result}" : "") + (err.Length > 0 ? $" (stderr: {err})" : "");
            h.Status = "failed";
        }
        else h.Status = "done";
        _running = null; _proc = null;
        SaveHandler(h);

        // Exactly once across dispatchers: the reply stage is claimed before anything is said.
        var (_, c) = await bridges.CallAsync(_game, "pubsub.claim", new JObject { ["seq"] = h.Seq, ["stage"] = "reply", ["by"] = By }, ct);
        if (c["ok"]?.Value<bool>() == true)
        {
            var handlerSaidDone = _replies.Any(x => x["data"]?["ref"]?.Value<long>() == h.Seq && x["data"]?["status"]?.ToString() is "done" or "failed");
            if (h.Status == "failed")
            {
                await FailAsync(h.Seq, $"its Claude session failed: {h.Error}", $"Details: {h.Dir}", ct);
            }
            else if (!handlerSaidDone)
                await ReplyAsync(h.Seq, "done", FeedbackKit.Clip(result, 400) is { Length: > 0 } text ? text : $"Done with #{h.Seq}.", ct);
            h.Replied = true;
            SaveHandler(h);
        }
        RemoveWorktree(h);
    }

    private static bool SafeHasExited(Process p) { try { return p.HasExited; } catch { return true; } }

    /// <summary>The handler's worktree, when it holds nothing: no changes, no commits beyond origin/main. Otherwise kept.</summary>
    private void RemoveWorktree(FeedbackHandlerState h)
    {
        var wt = h.Worktree;
        var repo = Repo();
        if (wt == null || repo == null || !Directory.Exists(wt)) return;
        try
        {
            var dirty = Git(wt, "status --porcelain --ignore-submodules=none").Output.Trim();
            var ahead = Git(wt, "log --oneline origin/main..HEAD").Output.Trim();
            if (dirty.Length > 0 || ahead.Length > 0) { Console.Error.WriteLine($"[Dispatch] #{h.Seq}: worktree {wt} kept (changes or commits)"); return; }
            var branch = Git(wt, "rev-parse --abbrev-ref HEAD").Output.Trim();
            var rm = Git(repo, $"worktree remove \"{wt}\"");
            if (rm.Code != 0) { Console.Error.WriteLine($"[Dispatch] #{h.Seq}: git worktree remove refused: {rm.Output.Trim()}"); return; }
            if (branch.Length > 0 && branch != "HEAD" && Git(repo, $"merge-base --is-ancestor \"{branch}\" origin/main").Code == 0)
                Git(repo, $"branch -D \"{branch}\"");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Dispatch] #{h.Seq}: removing worktree: {ex.Message}"); }
    }

    private static (int Code, string Output) Git(string dir, string args)
    {
        var psi = new ProcessStartInfo("git", $"-C \"{dir}\" {args}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return (-1, "git timed out"); }
        return (p.ExitCode, o.Result + e.Result);
    }

    // ── Replies, failures, status ────────────────────────────────────

    private async Task TellQueuedAsync(CancellationToken ct)
    {
        foreach (var seq in _presses.Keys.Where(s => !_toldQueued.Contains(s)).ToList())
        {
            _toldQueued.Add(seq);
            await ReplyAsync(seq, "queued", $"Got #{seq}: Claude finishes #{_running?.Seq} first, then takes it.", ct);
        }
    }

    private async Task ReplyAsync(long seq, string status, string text, CancellationToken ct)
    {
        try
        {
            await bridges.CallAsync(_game, "pubsub.publish", new JObject
            {
                ["kind"] = FeedbackKit.ReplyKind, ["source"] = Subscriber,
                ["data"] = new JObject { ["ref"] = seq, ["status"] = status, ["text"] = text },
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"[Dispatch] reply to #{seq} failed: {ex.Message}"); }
    }

    private async Task ReleaseAsync(long seq, string reason, CancellationToken ct)
    {
        try { await bridges.CallAsync(_game, "pubsub.release", new JObject { ["seq"] = seq, ["stage"] = "handle", ["by"] = By, ["reason"] = reason }, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"[Dispatch] release #{seq}: {ex.Message}"); }
        _nextEnvCheck = DateTime.UtcNow + RetryEvery;   // the press is unclaimed again: retried after a minute, not every second
    }

    private async Task FailAsync(long seq, string why, string? fix, CancellationToken ct)
    {
        Console.Error.WriteLine($"[Dispatch] #{seq}: {why}");
        await ReplyAsync(seq, "failed", FeedbackKit.FailText(seq, why, fix), ct);
        try
        {
            IncidentStore.Raise(new IncidentDraft
            {
                Kind = "custom", Scope = "machine", Key = IncidentKey,
                Title = "F8 feedback presses can't reach a Claude session",
                Detail = $"Press #{seq}: {why}. The dispatcher (shared MCP HTTP server) keeps the press and retries every minute.",
                Fix = fix,
                Fields = { ["auto"] = true, ["press"] = seq },
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Dispatch] raising {IncidentKey}: {ex.Message}"); }
    }

    private static void SaveHandler(FeedbackHandlerState s)
    {
        try
        {
            Directory.CreateDirectory(s.Dir!);
            File.WriteAllText(Path.Combine(s.Dir!, "state.json"), JsonSerializer.Serialize(s, Dto.Options));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Dispatch] saving handler #{s.Seq}: {ex.Message}"); }
    }

    private void WriteStatus()
    {
        try
        {
            Directory.CreateDirectory(HandlersDir);
            var recent = new List<FeedbackHandlerState>();
            foreach (var d in Directory.EnumerateDirectories(HandlersDir).OrderByDescending(d => long.TryParse(Path.GetFileName(d), out var n) ? n : 0).Take(5))
            {
                var f = Path.Combine(d, "state.json");
                if (File.Exists(f) && JsonSerializer.Deserialize<FeedbackHandlerState>(File.ReadAllText(f), Dto.Options) is { } s) recent.Add(s);
            }
            var state = new FeedbackDispatcherState
            {
                Status = _status, Why = _why.Length > 0 ? _why : null, Host = SessionIdentity.Kind, Pid = Environment.ProcessId,
                Version = McpSetup.Version, UpdatedAt = DateTimeOffset.UtcNow, Cursor = _cursor, ClaudeExe = _env.Exe,
                LoggedIn = _env.Exe == null ? null : _env.LoggedIn, Repo = Repo(), HandlersDir = HandlersDir,
                Handlers = recent, Waiting = _presses.Keys.OrderBy(k => k).ToList(),
            };
            var tmp = StatusFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Dto.Options));
            File.Move(tmp, StatusFile, overwrite: true);
        }
        catch { }
    }

    // ── Keeping the shared server up while a HUD is ──────────────────

    /// <summary>
    /// The shared server stops after 20 min without a connection (Supervisor --idle-exit). A press must find a dispatcher,
    /// so while a HUD bridge answered in the last 20 min (or a handler runs) this holds one connection to the server's own
    /// public port: a request to /keepalive that the server answers after 5 min (or when it stops), then the next one.
    /// </summary>
    private async Task KeepAliveAsync(CancellationToken stop)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("HEXILE_PUBLIC_PORT"), out var p) ? p : 50910;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - _bridgeOkAt < KeepAliveAfterBridge || _running != null)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/keepalive?sec=300");
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LocalHttpSecurity.LoadOrCreateToken());
                    using var _ = await http.SendAsync(req, stop);
                }
                else await Task.Delay(10_000, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[Dispatch] keepalive: {ex.Message}"); try { await Task.Delay(10_000, stop); } catch { break; } }
        }
    }
}
