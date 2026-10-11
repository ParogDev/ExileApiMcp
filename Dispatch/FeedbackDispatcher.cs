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
/// exactly once (pubsub.claim stage handle, with a ttl so a dispatcher that dies doesn't hold it forever; a second
/// dispatcher, e.g. the draining worker of a swap, gets "claimed"), says "Claude is on it" on the card (feedback.reply
/// status claimed), writes the handler's brief and starts `claude -p --worktree route-f8-&lt;seq&gt;` in the main checkout
/// with the scrubbed environment, detached (outside this worker's process tree, so a swap or the supervisor's stop doesn't
/// kill it). When the handler exits, its final message goes to the card (pubsub.claim stage reply first, so it is said
/// once, and not at all when the handler already said done), and its worktree is removed when it holds nothing.
///
/// One handler at a time (they share route.live.json): later presses wait, and the card says so. Every link that can break
/// is said on the card and raised as incident feedback-dispatch:machine (CLI not found, not logged in, the session
/// failing to start), never dropped: the press stays queued (unclaimed) and is retried every minute.
/// State: handlers\dispatcher.json (MCP feedback_dispatcher) and handlers\&lt;seq&gt;\ (brief, output, stderr, state.json);
/// any worker picks up a running handler from its state.json (Adopt), whoever started it.
/// </summary>
public sealed class FeedbackDispatcher(BridgeRegistry bridges) : BackgroundService
{
    public const string Subscriber = "route-dispatcher", IncidentKey = "feedback-dispatch:machine";
    private static readonly TimeSpan PressMaxAge = TimeSpan.FromHours(2), HandlerMaxRun = TimeSpan.FromMinutes(40),
        RetryEvery = TimeSpan.FromMinutes(1), KeepAliveAfterBridge = TimeSpan.FromMinutes(20), AdoptEvery = TimeSpan.FromSeconds(10);

    private readonly string _game = Environment.GetEnvironmentVariable("HEXILE_DISPATCH_GAME") is { Length: > 0 } g ? g : "poe2";
    private readonly CallerIdentity _as = new("route-dispatcher", null, null);
    /// <summary>Who claims: the claim lapses (ttl) if this server dies before the handler ends; release names the holder.</summary>
    private static readonly string By = $"{Subscriber}@{Environment.ProcessId}";
    private long _cursor;
    private readonly Dictionary<long, JObject> _presses = new();         // presses waiting for a handler, by seq
    private readonly HashSet<long> _toldQueued = new(), _toldFailed = new();
    private readonly List<JObject> _replies = new();                     // recent feedback.reply events (the last answers)
    private readonly List<JObject> _trace = new();                       // recent route + feedback events, for briefs
    private DateTime _nextEnvCheck = DateTime.MinValue, _nextStart = DateTime.MinValue, _nextAdopt = DateTime.MinValue, _bridgeOkAt = DateTime.MinValue;
    private (string? Exe, bool LoggedIn, string? Why, string? Fix) _env;
    /// <summary>The sign-in command for the user's own PowerShell while the CLI is not logged in (refreshed with _env).</summary>
    private string? _loginCommand;
    private FeedbackHandlerState? _running;
    private string _status = "starting", _why = "";
    private bool _envRaised, _bridgeBrokenRaised;

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
        while (!stop.IsCancellationRequested)
        {
            try { await StepAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { _status = "broken"; _why = $"dispatcher step: {ex.Message.Split('\n')[0]}"; Console.Error.WriteLine($"[Dispatch] {_why}"); }
            WriteStatus();
            try { await Task.Delay(1000, stop); } catch (OperationCanceledException) { break; }
        }
        _status = "stopped"; _why = "this server is stopping (a newer build took over, or it idled out): the next one carries on";
        WriteStatus();
    });

    private async Task StepAsync(CancellationToken ct)
    {
        if (_running == null && DateTime.UtcNow >= _nextAdopt) { _nextAdopt = DateTime.UtcNow + AdoptEvery; Adopt(); }
        if (_running != null) await SuperviseAsync(ct);
        // The CLI and its login: checked at start (a broken link is on the board before the first press), then every
        // minute while broken and every 10 min while fine, press or not.
        if (DateTime.UtcNow >= _nextEnvCheck) await RefreshEnvironmentAsync(ct);
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
            (_status, _why) = _env.Why != null ? ("broken", EnvText()) : ("idle", $"no {_game} HUD bridge: {ex.Message.Split('\n')[0]}");
            await Task.Delay(4000, ct);
            return;
        }
        if (FeedbackKit.Refusal(r, "pubsub.events") is { } refusal)
        {
            _status = "broken"; _why = $"the {_game} bridge: {refusal}";
            if (!_bridgeBrokenRaised) { _bridgeBrokenRaised = true; RaiseIncident(_why, "Update Whats An AI Bridge in the main checkout (pull, submodule update), then hud_restart.", null); }
            await Task.Delay(5000, ct);
            return;
        }
        if (_bridgeBrokenRaised && _env.Why == null) { _bridgeBrokenRaised = false; IncidentStore.ResolveKeys([IncidentKey], $"the {_game} bridge has pub/sub"); }
        _bridgeOkAt = DateTime.UtcNow;
        var seq = r["seq"]?.Value<long>() ?? 0;
        if (seq < _cursor) { _cursor = 0; _presses.Clear(); return; }   // the bridge's log was reset: read it again
        foreach (var e in (r["events"] as JArray ?? []).OfType<JObject>())
        {
            _cursor = Math.Max(_cursor, e["seq"]!.Value<long>());
            Take(e);
        }
        (_status, _why) = _env.Why != null ? ("broken", EnvText()) : ("listening", "");
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
        var age = FeedbackKit.At(e["at"]) is { } at ? DateTimeOffset.UtcNow - at : TimeSpan.Zero;
        if (e["data"]?["Event"]?.ToString() == "works" || age > PressMaxAge || e["claims"]?["reply"] != null) return;   // nothing to do, too old, answered
        if (e["claims"]?["handle"] is JObject h && h["lapsed"]?.Value<bool>() != true) return;                         // a live handler has it
        _presses[seq] = e;
    }

    // ── Starting a handler ───────────────────────────────────────────

    private async Task StartNextAsync(CancellationToken ct)
    {
        if (_presses.Count == 0 || DateTime.UtcNow < _nextStart) return;
        var seq = _presses.Keys.Min();
        var press = _presses[seq];
        if (!await EnvironmentOkAsync(seq, ct)) return;

        var (_, c) = await bridges.CallAsync(_game, "pubsub.claim", new JObject
        {
            ["seq"] = seq, ["stage"] = "handle", ["by"] = By, ["ttlSec"] = (HandlerMaxRun + TimeSpan.FromMinutes(10)).TotalSeconds,
        }, ct);
        if (c["ok"]?.Value<bool>() != true)
        {
            _presses.Remove(seq);   // another dispatcher has it, or it left the bridge's memory
            Console.Error.WriteLine($"[Dispatch] #{seq} not ours: {c["message"] ?? c["error"]}");
            return;
        }
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

            var (pid, startTime) = await StartDetachedAsync(FeedbackKit.CmdArguments(_env.Exe!, seq, dir, mcpConfig, [LiveDir, repo]), repo, seq, ct);
            state.Pid = pid;
            state.Extra = new() { ["processStart"] = JsonSerializer.SerializeToElement(startTime) };
            state.Status = "running";
            state.Worktree = Path.Combine(repo, ".claude", "worktrees", FeedbackKit.Label(seq));
            _running = state;
            _presses.Remove(seq);
            SaveHandler(state);
            Console.Error.WriteLine($"[Dispatch] #{seq}: handler pid {pid} started ({_env.Exe})");
            IncidentStore.ResolveKeys([IncidentKey], $"a handler started for F8 press #{seq}");
            _toldFailed.Remove(seq);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (why, fix) = ex is HandlerStartException h ? (h.Message, h.Fix) : ($"starting the handler failed: {ex.Message}", (string?)null);
            state.Status = "failed"; state.Error = why; state.EndedAt = DateTimeOffset.UtcNow;
            SaveHandler(state);
            await ReleaseAsync(seq, why, ct);   // the press stays in _presses: retried after RetryEvery
            _nextStart = DateTime.UtcNow + RetryEvery;
            if (_toldFailed.Add(seq)) await FailAsync(seq, why + " (it is kept and retried every minute)", fix, ct);
        }
    }

    private sealed class HandlerStartException(string message, string? fix) : Exception(message) { public string? Fix { get; } = fix; }

    /// <summary>
    /// Starts the handler outside this worker's process tree: a PowerShell launcher starts cmd.exe (hidden, ShellExecute) and
    /// exits, printing the new pid. The supervisor kills a worker's whole tree when it stops it; the handler must outlive
    /// that, like the shared server outlives the agent that started it (tools\mcp-http-lib.ps1). The launcher inherits the
    /// scrubbed environment, and so does the handler.
    /// </summary>
    private static async Task<(int Pid, DateTime StartTime)> StartDetachedAsync(string cmdArguments, string workDir, long seq, CancellationToken ct)
    {
        var script = "$p = Start-Process -FilePath cmd.exe -WindowStyle Hidden -PassThru " +
                     $"-WorkingDirectory '{workDir.Replace("'", "''")}' -ArgumentList '{cmdArguments.Replace("'", "''")}'; " +
                     "[Console]::Out.WriteLine(\"$($p.Id) $($p.StartTime.ToUniversalTime().ToString('o'))\")";
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                                                          Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workDir,
        };
        foreach (var k in psi.Environment.Keys.Where(FeedbackKit.Scrub).ToList()) psi.Environment.Remove(k);
        psi.Environment["HEXILE_AGENT"] = FeedbackKit.Label(seq);
        psi.Environment["HEXILE_ROUTE_PRESS"] = seq.ToString();
        using var launcher = Process.Start(psi) ?? throw new HandlerStartException("powershell.exe (the handler's launcher) did not start", null);
        var stdout = launcher.StandardOutput.ReadToEndAsync(ct);
        var stderr = launcher.StandardError.ReadToEndAsync(ct);
        try { await launcher.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (TimeoutException) { try { launcher.Kill(true); } catch { } throw new HandlerStartException("the handler's launcher did not finish within 30 s", null); }
        var line = (await stdout).Trim().Split(' ');
        if (line.Length < 2 || !int.TryParse(line[0], out var pid) || !DateTime.TryParse(line[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var started))
            throw new HandlerStartException($"the handler's launcher failed: {FeedbackKit.Clip(await stderr, 300)}", null);
        return (pid, started.ToUniversalTime());
    }

    /// <summary>The CLI is there and logged in (checked at most once a minute while broken, every 10 min while fine).</summary>
    private async Task<bool> EnvironmentOkAsync(long seq, CancellationToken ct)
    {
        if (DateTime.UtcNow >= _nextEnvCheck) await RefreshEnvironmentAsync(ct);
        if (_env.Why == null) return true;
        _status = "broken"; _why = EnvText();
        if (_toldFailed.Add(seq)) await FailAsync(seq, _env.Why + " (it is kept and handled once that is fixed)", _env.Fix, ct);
        return false;
    }

    /// <summary>The user profile folder (not redirected inside the MSIX package, unlike %LOCALAPPDATA% can be).</summary>
    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static async Task<(string? Exe, bool LoggedIn, string? Why, string? Fix)> CheckEnvironmentAsync(CancellationToken ct)
    {
        string? exe = Environment.GetEnvironmentVariable("HEXILE_CLAUDE_EXE") is { Length: > 0 } x && File.Exists(x) ? x : null;
        string looked = "HEXILE_CLAUDE_EXE";
        if (exe == null)
            (exe, looked) = FeedbackKit.FindCli(FeedbackKit.CliRoots(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        if (exe == null)
            return (null, false, $"the Claude Code CLI was not found (looked in {looked})", "Open the Claude desktop app once (it installs Claude Code there), or set HEXILE_CLAUDE_EXE.");
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(exe, "auth status") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var k in psi.Environment.Keys.Where(FeedbackKit.Scrub).ToList()) psi.Environment.Remove(k);
            p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync(ct);
            var errors = p.StandardError.ReadToEndAsync(ct);   // read both: a full stderr pipe would block the CLI
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            using var doc = JsonDocument.Parse(await output);
            _ = await errors;
            if (doc.RootElement.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True) return (exe, true, null, null);
            return (exe, false, "the Claude Code CLI is not logged in, so it can't start a session on its own",
                $"Once, in PowerShell: {FeedbackKit.LoginCommand(exe, UserProfile)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (exe, false, $"`claude auth status` failed: {(ex is TimeoutException ? "no answer in 30 s" : ex.Message)}", null);
        }
        finally
        {
            if (p != null) { try { if (!p.HasExited) p.Kill(true); } catch { } p.Dispose(); }
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

    /// <summary>The newest handler a state.json says is running, whoever started it (an earlier worker of a swap, a server
    /// that restarted, a draining worker that started one late): watched from here on.</summary>
    private void Adopt()
    {
        try
        {
            if (!Directory.Exists(HandlersDir)) return;
            foreach (var f in Directory.EnumerateFiles(HandlersDir, "state.json", SearchOption.AllDirectories))
            {
                FeedbackHandlerState? s;
                try { s = JsonSerializer.Deserialize<FeedbackHandlerState>(File.ReadAllText(f), Dto.Options); } catch { continue; }
                if (s is { Status: "running" } && (_running == null || s.Seq > _running.Seq)) _running = s;
            }
            if (_running != null) Console.Error.WriteLine($"[Dispatch] watching handler #{_running.Seq} (pid {_running.Pid}), started {_running.StartedAt:HH:mm:ss}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Dispatch] adopting handlers: {ex.Message}"); }
    }

    /// <summary>The handler's process, only when the pid still belongs to it (same start time): Windows reuses pids.</summary>
    private static Process? HandlerProcess(FeedbackHandlerState h)
    {
        if (h.Pid is not int pid) return null;
        try
        {
            var p = Process.GetProcessById(pid);
            var started = h.Extra != null && h.Extra.TryGetValue("processStart", out var ps) && ps.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ps.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : (DateTime?)null;
            if (started == null || Math.Abs((p.StartTime.ToUniversalTime() - started.Value).TotalSeconds) > 2 || !p.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            { p.Dispose(); return null; }
            if (p.HasExited) { p.Dispose(); return null; }
            return p;
        }
        catch { return null; }
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var h = _running!;
        using (var proc = HandlerProcess(h))
        {
            if (proc != null)
            {
                if (h.StartedAt is { } started && DateTimeOffset.UtcNow - started > HandlerMaxRun)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    h.Error = $"the handler ran longer than {HandlerMaxRun.TotalMinutes:0} min and was stopped";
                }
                else return;   // still working
            }
        }

        var output = File.Exists(Path.Combine(h.Dir!, "output.json")) ? File.ReadAllText(Path.Combine(h.Dir!, "output.json")) : "";
        var (result, isError, sessionId, why) = FeedbackKit.ParseOutput(output);
        var failed = isError || h.Error != null;
        string? error = null;
        if (failed)
        {
            var err = File.Exists(Path.Combine(h.Dir!, "stderr.log")) ? FeedbackKit.Clip(File.ReadAllText(Path.Combine(h.Dir!, "stderr.log")), 300) : "";
            error = h.Error ?? why + (result != null ? $": {result}" : "") + (err.Length > 0 ? $" (stderr: {err})" : "");
        }

        // Exactly once across dispatchers: the reply stage is claimed before anything is said. A bridge call that fails
        // (the HUD restarting) throws out of here with _running kept: the next step tries again.
        var (_, c) = await bridges.CallAsync(_game, "pubsub.claim", new JObject { ["seq"] = h.Seq, ["stage"] = "reply", ["by"] = By }, ct);
        if (c["ok"]?.Value<bool>() == true)
        {
            // Read the replies fresh: the handler may have said done just before it exited (feedback_reply).
            var (_, rr) = await bridges.CallAsync(_game, "pubsub.events", new JObject { ["since"] = h.Seq, ["kinds"] = new JArray(FeedbackKit.ReplyKind), ["limit"] = 500 }, ct);
            var handlerSaidDone = (rr["events"] as JArray ?? []).Any(x => x["data"]?["ref"]?.Value<long>() == h.Seq && x["source"]?.ToString() != Subscriber
                                                                         && x["data"]?["status"]?.ToString() is "done" or "failed");
            if (failed) await FailAsync(h.Seq, $"its Claude session failed: {error}", $"Details: {h.Dir}", ct);
            else if (!handlerSaidDone)
                await ReplyAsync(h.Seq, "done", FeedbackKit.Clip(result, 400) is { Length: > 0 } text ? text : $"Done with #{h.Seq}.", ct);
            h.Replied = true;
        }
        h.EndedAt = DateTimeOffset.UtcNow;
        h.SessionId = sessionId;
        h.Result = result;
        h.Error = error;
        h.Status = failed ? "failed" : "done";
        _running = null;
        SaveHandler(h);
        RemoveWorktree(h);
    }

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
    }

    private string EnvText() => _env.Why + (_env.Fix != null ? $" ({_env.Fix})" : "");

    /// <summary>Checks the CLI and its login; a change to broken goes on the board (once, not again while already open), a
    /// change back resolves it.</summary>
    private async Task RefreshEnvironmentAsync(CancellationToken ct)
    {
        var wasBroken = _env.Why != null;
        try { _env = await CheckEnvironmentAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _env = (_env.Exe, false, $"checking the Claude Code CLI failed: {ex.Message}", null);
        }
        _loginCommand = _env.Exe != null && !_env.LoggedIn ? FeedbackKit.LoginCommand(_env.Exe, UserProfile) : null;
        _nextEnvCheck = DateTime.UtcNow + (_env.Why == null ? TimeSpan.FromMinutes(10) : RetryEvery);
        if (_env.Why != null && (!wasBroken || !_envRaised))
        {
            Console.Error.WriteLine($"[Dispatch] {EnvText()}");
            _envRaised = RaiseIncident(_env.Why, _env.Fix, null, onlyIfNew: true);
        }
        else if (_env.Why == null && (wasBroken || _envRaised))
        {
            _envRaised = false;
            IncidentStore.ResolveKeys([IncidentKey], "the Claude Code CLI is there and logged in");
        }
    }

    /// <summary>The one place feedback-dispatch:machine is raised. onlyIfNew: leave an open one alone (a restarted server
    /// must not re-notify every session). True when it is on the board.</summary>
    private static bool RaiseIncident(string why, string? fix, long? press, bool onlyIfNew = false)
    {
        try
        {
            if (onlyIfNew && IncidentLogic.OpenByKey(IncidentStore.Read(), IncidentKey) != null) return true;
            var draft = new IncidentDraft
            {
                Kind = "custom", Scope = "machine", Key = IncidentKey, Title = "F8 feedback presses can't reach a Claude session",
                Detail = (press is { } p ? $"Press #{p}: " : "") + $"{why}. The dispatcher (shared MCP HTTP server) keeps waiting presses and retries every minute.",
                Fix = fix,
                Fields = { ["auto"] = true, ["press"] = press },
            };
            IncidentStore.Raise(draft);
            return true;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Dispatch] raising {IncidentKey}: {ex.Message}"); return false; }
    }

    private async Task FailAsync(long seq, string why, string? fix, CancellationToken ct)
    {
        Console.Error.WriteLine($"[Dispatch] #{seq}: {why}");
        await ReplyAsync(seq, "failed", FeedbackKit.FailText(seq, why, fix), ct);
        RaiseIncident(why, fix, seq);
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
                try { if (File.Exists(f) && JsonSerializer.Deserialize<FeedbackHandlerState>(File.ReadAllText(f), Dto.Options) is { } s) recent.Add(s); } catch { }
            }
            var state = new FeedbackDispatcherState
            {
                Status = _status, Why = _why.Length > 0 ? _why : null, Host = SessionIdentity.Kind, Pid = Environment.ProcessId,
                Version = McpSetup.Version, UpdatedAt = DateTimeOffset.UtcNow, Cursor = _cursor, ClaudeExe = _env.Exe,
                LoggedIn = _env.Exe == null ? null : _env.LoggedIn,
                LoginCommand = _loginCommand,
                Repo = Repo(), HandlersDir = HandlersDir,
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
