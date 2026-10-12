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
/// status claimed), writes the handler's brief and starts it in the main checkout with the scrubbed environment, outside
/// this worker's process tree (a swap or the supervisor's stop doesn't kill it), in worktree route-f8-&lt;seq&gt;:
/// - visible first: `claude --bg --remote-control "F8 #&lt;seq&gt;: &lt;note&gt; (&lt;area&gt;)"`, a background session the user sees
///   and can join in claude.ai/code and the Claude app (Remote Control). Its state comes from `claude agents --json`, its
///   URL and last answer from its transcript. Fire and forget: when its turn is done, the answer goes to the card and the
///   session is stopped (its transcript and Remote Control history stay);
/// - headless `claude -p` when the background session can't start (the card says which one runs).
/// Both start slim (FeedbackKit.SlimArguments): the standing brief as the system prompt, no CLAUDE.md or memory, only the
/// tools a route fix needs. The final message goes to the card once (pubsub.claim stage reply first, and not at all when
/// the handler already said done), and the worktree is removed when it holds no unmerged work. Before that answer, the
/// handler's merged fix is brought into the HUD (MainSync: pull the main checkout, update Whats A Route, reload_plugin):
/// handlers stop at "merged", since Claude Code's worktree isolation refuses every command aimed at the main checkout.
///
/// One handler at a time (they share route.live.json): later presses wait, and the card says so. Every link that can break
/// is said on the card and raised as incident feedback-dispatch:machine (CLI not found, not logged in, the session
/// failing to start), never dropped: the press stays queued (unclaimed) and is retried every minute.
/// State: handlers\dispatcher.json (MCP feedback_dispatcher) and handlers\&lt;seq&gt;\ (brief, output, stderr, state.json);
/// any worker picks up a running handler from its state.json (Adopt), whoever started it.
/// </summary>
public sealed class FeedbackDispatcher(BridgeRegistry bridges, IHostApplicationLifetime lifetime) : BackgroundService
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
    private (string? Exe, bool LoggedIn, string? Why, string? Fix, string? Login) _env;
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

    /// <summary>Retired: this worker was told to stop (ApplicationStopping). The host stops hosted services only after
    /// its open requests finish (a drained worker can wait up to an hour), and the old build's dispatcher must not take
    /// presses, supervise or write the status meanwhile: the new worker adopts a running handler from its state.json. A
    /// step already under way finishes (it is never cut between a claim and its state.json).</summary>
    private bool Retired => lifetime.ApplicationStopping.IsCancellationRequested;

    protected override Task ExecuteAsync(CancellationToken stop) => CallerIdentity.RunAsAsync(_as, async () =>
    {
        _ = KeepAliveAsync(stop);
        while (!stop.IsCancellationRequested && !Retired)
        {
            try { await StepAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { _status = "broken"; _why = $"dispatcher step: {ex.Message.Split('\n')[0]}"; Console.Error.WriteLine($"[Dispatch] {_why}"); }
            if (!Retired) WriteStatus();
            try { await Task.Delay(1000, stop); } catch (OperationCanceledException) { break; }
        }
        Console.Error.WriteLine("[Dispatch] stopped: this worker is retiring (a newer build took over, or it idled out); the next one carries on");
        if (!Retired)
        {
            _status = "stopped"; _why = "this server is stopping (it idled out, or its supervisor stopped): the next one carries on";
            WriteStatus();
        }
    });

    private async Task StepAsync(CancellationToken ct)
    {
        if (_running == null && DateTime.UtcNow >= _nextAdopt) { _nextAdopt = DateTime.UtcNow + AdoptEvery; Adopt(); }
        if (_running != null) await SuperviseAsync(ct);
        if (_env.Exe != null) await ReapAsync(ct);
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
            var standingBrief = Path.Combine(repo, "Plugins", "Whats A Route", "docs", "feedback-agent.md");
            File.WriteAllText(briefPath, FeedbackKit.Brief(press, trace, replies, standingBrief, LiveDir, previous));
            File.WriteAllText(Path.Combine(dir, "prompt.txt"), FeedbackKit.Prompt(seq, briefPath));
            var mcpConfig = Path.Combine(dir, "mcp.json");
            File.WriteAllText(mcpConfig, McpConfigJson(seq));
            var settings = Path.Combine(dir, "settings.json");
            File.WriteAllText(settings, FeedbackKit.SlimSettingsJson());
            // Inline for the background session; over ~16k chars it would crowd Windows' 32k command line, so it is left
            // out there and the prompt's pointer to the file (brief.md names it) does the job.
            var briefText = File.Exists(standingBrief) ? File.ReadAllText(standingBrief) : null;
            if (briefText is { Length: > 16000 }) Console.Error.WriteLine($"[Dispatch] #{seq}: the standing brief is {briefText.Length} chars: not inlined");
            var slim = FeedbackKit.SlimArguments(settings, standingBrief, briefText is { Length: > 16000 } ? null : briefText, asFile: false);
            foreach (var f in new[] { "output.json", "stderr.log" }) File.Delete(Path.Combine(dir, f));

            // Visible first: a background session with Remote Control, which the user sees (and can join) in Claude.
            // Headless `claude -p` when that can't start (or HEXILE_DISPATCH_HEADLESS=1), and the card says which ran.
            state.Name = FeedbackKit.DisplayName(press);
            string? fallback = Environment.GetEnvironmentVariable("HEXILE_DISPATCH_HEADLESS") == "1" ? "HEXILE_DISPATCH_HEADLESS=1 is set" : null;
            if (fallback == null)
            {
                try
                {
                    state.BackgroundId = await StartBackgroundAsync(FeedbackKit.BackgroundArguments(seq, state.Name, mcpConfig, [LiveDir, repo],
                        FeedbackKit.Prompt(seq, briefPath), slim), repo, seq, ct);
                    state.Mode = "remote-control";
                }
                // Only a clear refusal falls back: when the CLI may have started the session (a timeout, an answer without
                // an id), a headless run would be a second handler for the same press. That one fails and is retried.
                catch (BackgroundRefusedException ex) { fallback = $"Remote Control could not start: {ex.Message}"; }
            }
            if (state.Mode == null)
            {
                var (pid, startTime) = await StartDetachedAsync(FeedbackKit.CmdArguments(_env.Exe!, seq, dir, mcpConfig, [LiveDir, repo],
                    FeedbackKit.SlimArguments(settings, standingBrief, briefText, asFile: true)), repo, seq, ct);
                state.Pid = pid;
                state.Extra = new() { ["processStart"] = JsonSerializer.SerializeToElement(startTime) };
                state.Mode = "headless";
                state.FallbackReason = fallback;
            }
            state.Status = "running";
            state.Worktree = Path.Combine(repo, ".claude", "worktrees", FeedbackKit.Label(seq));
            _running = state;
            _presses.Remove(seq);
            SaveHandler(state);
            Console.Error.WriteLine($"[Dispatch] #{seq}: handler started, {state.Mode} ({(state.BackgroundId != null ? $"background session {state.BackgroundId}" : $"pid {state.Pid}")}, {_env.Exe})");
            await ReplyAsync(seq, "working", FeedbackKit.WhereText(seq, state.Mode, state.Name, state.FallbackReason), ct);
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

    /// <summary>
    /// Starts the handler as a background session (`claude --bg --remote-control`): the CLI's background service runs it
    /// (outside this worker's process tree, like the headless launcher's cmd.exe) and the CLI exits once it printed the
    /// session's short id. The session inherits this environment (scrubbed, plus the handler's label).
    /// </summary>
    private async Task<string> StartBackgroundAsync(IReadOnlyList<string> args, string workDir, long seq, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000;
        var (code, output, errors) = await RunCliAsync(args, workDir, TimeSpan.FromSeconds(90), ct, seq);
        var all = output + "\n" + errors;
        if (FeedbackKit.ParseBackgrounded(all) is { } id) return id;
        Console.Error.WriteLine($"[Dispatch] #{seq}: `claude --bg` exited {code}, its full output:\n{all.Trim()}");   // the card gets one line
        if (code != 0 && !all.Contains("backgrounded", StringComparison.OrdinalIgnoreCase))
            throw new BackgroundRefusedException($"`claude --bg` exited {code}: {FeedbackKit.CliLine(all)}");
        // The answer could not be read, but a session may run: find it by name before calling this a failure, or the
        // retry a minute later starts a second handler for the same press.
        try
        {
            var (_, list, _) = await RunCliAsync(["agents", "--json", "--all"], null, TimeSpan.FromSeconds(30), ct);
            if (FeedbackKit.FindAgentByName(list, args[args.ToList().IndexOf("--name") + 1], since) is { } found)
            {
                Console.Error.WriteLine($"[Dispatch] #{seq}: `claude --bg` printed no id it could read; found background session {found} by name");
                return found;
            }
        }
        catch (Exception ex) when (ex is HandlerStartException or FormatException or IOException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[Dispatch] #{seq}: looking the session up by name failed: {ex.Message}");
        }
        throw new HandlerStartException($"`claude --bg` exited {code} without a session id it could read (`claude agents` lists it if one started): {FeedbackKit.CliLine(all)}", null);
    }

    /// <summary>The CLI refused to start a background session (so nothing runs): the headless run may take the press.</summary>
    private sealed class BackgroundRefusedException(string message) : Exception(message);

    /// <summary>Runs the CLI with the scrubbed environment (and the handler's label when seq is given) and returns its exit
    /// code, stdout and stderr. The output is pumped, not read to the end: a background service the CLI starts may inherit
    /// the pipes and keep them open after the CLI exits. A CLI that hangs is killed after the timeout (itself only, never its
    /// tree: that could hold the background service and every session in it).</summary>
    private async Task<(int Code, string Output, string Errors)> RunCliAsync(IEnumerable<string> args, string? workDir, TimeSpan timeout, CancellationToken ct, long? seq = null)
    {
        var psi = new ProcessStartInfo(_env.Exe ?? throw new HandlerStartException("the Claude Code CLI was not found", null))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,   // the CLI writes UTF-8, not the console code page
            WorkingDirectory = workDir ?? UserProfile,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var k in psi.Environment.Keys.Where(FeedbackKit.Scrub).ToList()) psi.Environment.Remove(k);
        if (seq is { } s) { psi.Environment["HEXILE_AGENT"] = FeedbackKit.Label(s); psi.Environment["HEXILE_ROUTE_PRESS"] = s.ToString(); }
        using var p = Process.Start(psi) ?? throw new HandlerStartException("the Claude Code CLI did not start", null);
        p.StandardInput.Close();
        using var stopPumps = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var (o, e) = (new System.Text.StringBuilder(), new System.Text.StringBuilder());
        var pumps = Task.WhenAll(Pump(p.StandardOutput, o, stopPumps.Token), Pump(p.StandardError, e, stopPumps.Token));
        try { await p.WaitForExitAsync(ct).WaitAsync(timeout, ct); }
        catch (TimeoutException)
        {
            try { p.Kill(false); } catch { }
            stopPumps.Cancel();
            throw new HandlerStartException($"`claude {args.FirstOrDefault()}` did not finish within {timeout.TotalSeconds:0} s", null);
        }
        await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(3), ct));   // the rest of the output, unless an heir holds the pipe
        stopPumps.Cancel();
        lock (o) lock (e) return (p.ExitCode, o.ToString(), e.ToString());

        static async Task Pump(StreamReader r, System.Text.StringBuilder into, CancellationToken stop)
        {
            var buf = new char[4096];
            try
            {
                int n;
                while ((n = await r.ReadAsync(buf.AsMemory(), stop)) > 0) lock (into) into.Append(buf, 0, n);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
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

    private static async Task<(string? Exe, bool LoggedIn, string? Why, string? Fix, string? Login)> CheckEnvironmentAsync(CancellationToken ct)
    {
        string? exe = Environment.GetEnvironmentVariable("HEXILE_CLAUDE_EXE") is { Length: > 0 } x && File.Exists(x) ? x : null;
        string looked = "HEXILE_CLAUDE_EXE";
        if (exe == null)
            (exe, looked) = FeedbackKit.FindCli(FeedbackKit.CliRoots(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        if (exe == null)
            return (null, false, $"the Claude Code CLI was not found (looked in {looked})", "Open the Claude desktop app once (it installs Claude Code there), or set HEXILE_CLAUDE_EXE.", null);
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
            if (doc.RootElement.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True) return (exe, true, null, null, null);
            var login = FeedbackKit.LoginCommand(exe, UserProfile, Environment.GetEnvironmentVariable("LOCALAPPDATA"));
            return (exe, false, "the Claude Code CLI is not logged in, so it can't start a session on its own",
                $"Once, in PowerShell: {login}", login);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (exe, false, $"`claude auth status` failed: {(ex is TimeoutException ? "no answer in 30 s" : ex.Message)}", null, null);
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
        if (h.Mode == "remote-control") { await SuperviseBackgroundAsync(h, ct); return; }
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
        h.SessionId = sessionId;
        var sync = await SyncMainAsync(h, ct);
        await FinishAsync(h, result, failed, error, ct);
        await TellSyncFailedAsync(h, sync, ct);
        RemoveWorktree(h);
    }

    // ── Watching a visible (background, Remote Control) handler ──────

    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(5), ListGrace = TimeSpan.FromSeconds(45), DoneGrace = TimeSpan.FromSeconds(15), StopRetry = TimeSpan.FromMinutes(1);
    private DateTime _nextPoll = DateTime.MinValue, _nextReap = DateTime.MinValue;
    private readonly HashSet<long> _toldBlocked = new();
    private readonly Dictionary<long, DateTime> _doneSince = new();
    private readonly Dictionary<string, FeedbackKit.TranscriptState> _transcripts = new();

    /// <summary>
    /// A visible handler is an interactive session: it doesn't exit when its turn ends. `claude agents --json --all` says
    /// its state (working, blocked on a question, done with its turn) and whether its process is alive; its transcript has
    /// the claude.ai/code URL, the last answer and whether the turn has ended. Fire and forget: once both say the turn is
    /// over, the answer goes to the card and the session is stopped (`claude stop`: its transcript and Remote Control
    /// history stay), then its worktree is removed when it holds no unmerged work.
    /// </summary>
    private async Task SuperviseBackgroundAsync(FeedbackHandlerState h, CancellationToken ct)
    {
        if (DateTime.UtcNow < _nextPoll) return;
        _nextPoll = DateTime.UtcNow + PollEvery;
        var age = h.StartedAt is { } s ? DateTimeOffset.UtcNow - s : TimeSpan.Zero;
        (string? SessionId, string? State, bool Alive)? agent;
        try { agent = await AgentAsync(h.BackgroundId!, ct); }
        catch (CliException ex)
        {
            // The CLI didn't answer: not the session's fault. Asked again next poll; only the run limit ends it.
            Console.Error.WriteLine($"[Dispatch] #{h.Seq}: {ex.Message}");
            if (age <= HandlerMaxRun) return;
            agent = null;
        }
        if (agent is { SessionId: { } sid } && h.SessionId == null) { h.SessionId = sid; SaveHandler(h); }
        var t = Transcript(h.SessionId);
        if (t.Url != null && h.Url != t.Url)
        {
            h.Url = t.Url; SaveHandler(h);
            Console.Error.WriteLine($"[Dispatch] #{h.Seq}: Remote Control at {t.Url}");
        }

        string? error = null;
        if (age > HandlerMaxRun)
            error = $"the handler ran longer than {HandlerMaxRun.TotalMinutes:0} min and was stopped" + (agent?.State is { } st ? $" (it was {st})" : "");
        else if (agent == null)
        {
            if (age < ListGrace) return;   // the background service lists it within a few seconds
            error = $"its background session {h.BackgroundId} is no longer listed by `claude agents`";
        }
        else if (agent.Value.State == "blocked" && agent.Value.Alive)
        {
            if (_toldBlocked.Add(h.Seq))
                await ReplyAsync(h.Seq, "working", $"#{h.Seq} waits for an answer: open \"{h.Name}\" in Claude (claude.ai/code or the Claude app).", ct);
            return;
        }
        else if (agent.Value.State == "done" && (t.TurnEnded || !agent.Value.Alive || DoneFor(h.Seq) > DoneGrace)) { }   // its turn ended: the answer is written
        else if (!agent.Value.Alive)
            error = $"its session exited before finishing (state {agent.Value.State ?? "unknown"})";
        else return;   // still working (or done, and the transcript catches up within DoneGrace)

        _doneSince.Remove(h.Seq);
        var failed = error != null;
        h.StopAt = DateTimeOffset.UtcNow + StopRetry;   // ReapAsync stops it then if the stop below doesn't
        // A good answer goes out first (the player doesn't wait for the stop); a failed or over-time session is stopped
        // first, so the card's "was stopped" is true and nothing keeps working once the next press starts.
        if (failed) await StopSessionAsync(h, ct);
        // The merged fix goes into the HUD before the answer, so "fixed" on the card is true in game.
        var sync = await SyncMainAsync(h, ct);
        await FinishAsync(h, t.LastText, failed, error, ct);
        await TellSyncFailedAsync(h, sync, ct);
        if (!failed) await StopSessionAsync(h, ct);
        SaveHandler(h);
        if (h.Stopped) RemoveWorktree(h);
    }

    /// <summary>How long the CLI has listed this handler as done: its turn is over even when the transcript doesn't say
    /// so yet (a live background job has no pid to tell "done and idle" from "exited").</summary>
    private TimeSpan DoneFor(long seq)
    {
        if (!_doneSince.TryGetValue(seq, out var since)) _doneSince[seq] = since = DateTime.UtcNow;
        return DateTime.UtcNow - since;
    }

    /// <summary>Finished visible sessions a stop didn't end (the CLI failed): stopped again a minute later (the transcript
    /// stays), their worktree removed when it holds no unmerged work.</summary>
    private async Task ReapAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _nextReap || !Directory.Exists(HandlersDir)) return;
        _nextReap = DateTime.UtcNow + AdoptEvery;
        foreach (var f in Directory.EnumerateFiles(HandlersDir, "state.json", SearchOption.AllDirectories))
        {
            FeedbackHandlerState? h;
            try { h = JsonSerializer.Deserialize<FeedbackHandlerState>(File.ReadAllText(f), Dto.Options); } catch { continue; }
            if (h is not { Mode: "remote-control", Stopped: false, Status: "done" or "failed", StopAt: { } at } || at > DateTimeOffset.UtcNow || h.BackgroundId == null) continue;
            (string? SessionId, string? State, bool Alive)? agent;
            try { agent = await AgentAsync(h.BackgroundId, ct); }
            catch (CliException ex) { Console.Error.WriteLine($"[Dispatch] #{h.Seq}: {ex.Message}"); continue; }
            if (agent is { Alive: true }) await StopSessionAsync(h, ct);
            else h.Stopped = true;   // already ended (stopped by the user, or exited)
            if (!h.Stopped) h.StopAt = DateTimeOffset.UtcNow + StopRetry;
            SaveHandler(h);
            if (h.Stopped) RemoveWorktree(h);
        }
    }

    private async Task StopSessionAsync(FeedbackHandlerState h, CancellationToken ct)
    {
        if (h.BackgroundId == null || h.Stopped) return;
        try
        {
            var (code, output, errors) = await RunCliAsync(["stop", h.BackgroundId], null, TimeSpan.FromSeconds(60), ct);
            Console.Error.WriteLine($"[Dispatch] #{h.Seq}: claude stop {h.BackgroundId}: exit {code} {FeedbackKit.Clip(output + " " + errors, 160)}");
            h.Stopped = code == 0 || (await AgentAsync(h.BackgroundId, ct)) is null or { Alive: false };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"[Dispatch] #{h.Seq}: stopping {h.BackgroundId}: {ex.Message}"); }
    }

    /// <summary>The CLI didn't give an answer (failed, timed out, printed something else): ask again later.</summary>
    private sealed class CliException(string message) : Exception(message);

    /// <summary>The background session as `claude agents --json --all` lists it, or null when it isn't listed. Throws
    /// CliException when the CLI didn't answer with the list (not the same as "not listed").</summary>
    private async Task<(string? SessionId, string? State, bool Alive)?> AgentAsync(string id, CancellationToken ct)
    {
        try
        {
            var (_, output, _) = await RunCliAsync(["agents", "--json", "--all"], null, TimeSpan.FromSeconds(30), ct);
            return FeedbackKit.ParseAgent(output, id);
        }
        catch (Exception ex) when (ex is HandlerStartException or FormatException or IOException or System.ComponentModel.Win32Exception)
        {
            throw new CliException($"claude agents: {ex.Message}");
        }
    }

    /// <summary>The session's transcript, ~/.claude/projects/&lt;dir&gt;/&lt;session-id&gt;.jsonl (the dir is the worktree's
    /// path, so it is looked up by file name), read on from where the last poll stopped.</summary>
    private FeedbackKit.TranscriptState Transcript(string? sessionId)
    {
        if (sessionId == null) return new();
        if (!_transcripts.TryGetValue(sessionId, out var s)) _transcripts[sessionId] = s = new();
        try
        {
            if (s.Path == null)
            {
                var projects = Path.Combine(UserProfile, ".claude", "projects");
                s.Path = Directory.Exists(projects)
                    ? Directory.EnumerateDirectories(projects).Select(d => Path.Combine(d, sessionId + ".jsonl")).FirstOrDefault(File.Exists) : null;
                if (s.Path == null) return s;
            }
            using var fs = new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < s.Offset) s.Offset = 0;
            fs.Seek(s.Offset, SeekOrigin.Begin);
            var bytes = new byte[fs.Length - s.Offset];
            var read = 0;
            while (read < bytes.Length && fs.Read(bytes, read, bytes.Length - read) is var n and > 0) read += n;
            var end = Array.LastIndexOf(bytes, (byte)'\n', Math.Max(read - 1, 0));   // whole lines only: the last one may be half written
            if (read == 0 || end < 0) return s;
            s.Offset += end + 1;
            FeedbackKit.ParseTranscript(System.Text.Encoding.UTF8.GetString(bytes, 0, end + 1).Split('\n').Select(l => l.TrimEnd('\r')), s);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return s;
    }

    /// <summary>Says the handler's answer on the card (exactly once across dispatchers) and records how it ended.</summary>
    private async Task FinishAsync(FeedbackHandlerState h, string? result, bool failed, string? error, CancellationToken ct)
    {
        // Exactly once across dispatchers: the reply stage is claimed before anything is said. A bridge call that fails
        // (the HUD restarting) throws out of here with _running kept: the next step tries again.
        var (_, c) = await bridges.CallAsync(_game, "pubsub.claim", new JObject { ["seq"] = h.Seq, ["stage"] = "reply", ["by"] = By }, ct);
        if (c["ok"]?.Value<bool>() == true)
        {
            // Read the replies fresh: the handler may have said done just before it exited (feedback_reply).
            var (_, rr) = await bridges.CallAsync(_game, "pubsub.events", new JObject { ["since"] = h.Seq, ["kinds"] = new JArray(FeedbackKit.ReplyKind), ["limit"] = 500 }, ct);
            var handlerSaidDone = (rr["events"] as JArray ?? []).Any(x => x["data"]?["ref"]?.Value<long>() == h.Seq && x["source"]?.ToString() != Subscriber
                                                                         && x["data"]?["status"]?.ToString() is "done" or "failed");
            if (failed) await FailAsync(h.Seq, $"its Claude session failed: {error}", h.Url != null ? $"Its transcript: {h.Url}" : $"Details: {h.Dir}", ct);
            else if (!handlerSaidDone)
            {
                var text = FeedbackKit.Clip(result, h.Mode == "remote-control" ? 340 : 400) is { Length: > 0 } t ? t : $"Done with #{h.Seq}.";
                if (h.Mode == "remote-control") text += $" (Transcript: \"F8 #{h.Seq}\" in Claude.)";
                await ReplyAsync(h.Seq, "done", text, ct);
            }
            h.Replied = true;
        }
        h.EndedAt = DateTimeOffset.UtcNow;
        h.Result = result;
        h.Error = error;
        h.Status = failed ? "failed" : "done";
        _running = null;
        SaveHandler(h);
    }

    /// <summary>The handler's worktree, removed when it holds no unmerged work: no uncommitted changes, no submodule commits
    /// that are on no remote (the submodules' git data lives in the worktree and goes with it), and no commits whose
    /// changes origin/main doesn't hold. Otherwise kept for a person to look at.</summary>
    private void RemoveWorktree(FeedbackHandlerState h)
    {
        var wt = h.Worktree;
        var repo = Repo();
        if (wt == null || repo == null || !Directory.Exists(wt)) return;
        try
        {
            var dirty = Git(wt, "status --porcelain --ignore-submodules=none").Output.Trim();
            if (dirty.Length > 0) { Console.Error.WriteLine($"[Dispatch] #{h.Seq}: worktree {wt} kept (uncommitted changes)"); return; }
            // HEAD too: a commit on a detached HEAD (after `submodule update`) is on no branch. A local topic branch whose
            // remote branch was deleted on merge counts as unpushed: the brief has the handler delete it after the merge.
            var unpushed = Git(wt, "submodule foreach --quiet --recursive \"git log --oneline HEAD --branches --not --remotes\"");
            if (unpushed.Code != 0 || unpushed.Output.Trim().Length > 0)
            { Console.Error.WriteLine($"[Dispatch] #{h.Seq}: worktree {wt} kept (submodule commits on no remote)"); return; }
            // Unmerged work: commits whose changes origin/main doesn't hold. A squash merge leaves the branch's own commits
            // "ahead" though their content is on main: the files they touched are compared with origin/main instead.
            Git(repo, "fetch -q origin main");   // a merge the handler just made must count as merged (once per handler)
            var ahead = Git(wt, "log --oneline origin/main..HEAD").Output.Trim().Length > 0;
            var merged = !ahead;
            if (ahead && Git(wt, "merge-base origin/main HEAD") is { Code: 0 } mb)
            {
                var files = Git(wt, $"diff --name-only {mb.Output.Trim()} HEAD").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                merged = files.Length == 0 || Git(wt, "diff --quiet origin/main HEAD -- " + string.Join(' ', files.Select(f => $"\"{f}\""))).Code == 0;
            }
            if (!merged) { Console.Error.WriteLine($"[Dispatch] #{h.Seq}: worktree {wt} kept (commits not on origin/main)"); return; }
            var branch = Git(wt, "rev-parse --abbrev-ref HEAD").Output.Trim();
            // --force: checked clean above, and git refuses a worktree with checked-out submodules without it (a handler
            // initializes Plugins/Whats A Route). Ignored files (build output) go with it.
            var rm = Git(repo, $"worktree remove --force \"{wt}\"");
            // The CLI locks a --worktree it creates ("claude session route-f8-<seq> (pid N)") and an ended session can
            // leave the lock behind: unlocked only when that pid no longer runs, then removed.
            if (rm.Code != 0 && FeedbackKit.StaleLockPid(rm.Output) is { } lockPid && !ProcessRuns(lockPid))
            {
                Git(repo, $"worktree unlock \"{wt}\"");
                rm = Git(repo, $"worktree remove --force \"{wt}\"");
            }
            if (rm.Code != 0) { Console.Error.WriteLine($"[Dispatch] #{h.Seq}: git worktree remove refused: {rm.Output.Trim()}"); return; }
            Console.Error.WriteLine($"[Dispatch] #{h.Seq}: worktree {wt} removed");
            if (branch.Length > 0 && branch != "HEAD") Git(repo, $"branch -D \"{branch}\"");   // its work is on origin/main (checked above)
            // The branch the CLI made for the worktree, when the handler switched away from it: only if it holds nothing.
            var cliBranch = "worktree-" + FeedbackKit.Label(h.Seq);
            if (cliBranch != branch && Git(repo, $"merge-base --is-ancestor \"{cliBranch}\" origin/main").Code == 0) Git(repo, $"branch -D \"{cliBranch}\"");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Dispatch] #{h.Seq}: removing worktree: {ex.Message}"); }
    }

    private static bool ProcessRuns(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch { return true; }   // can't tell: leave the lock alone
    }

    internal static (int Code, string Output) Git(string dir, string args, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo("git", $"-C \"{dir}\" {args}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";   // a credential prompt would hang with no one to answer it
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, $"git {args.Split(' ')[0]} timed out after {timeoutMs / 1000} s"); }
        return (p.ExitCode, o.Result + e.Result);
    }

    // ── Bringing a handler's merged fix into the HUD ─────────────────

    /// <summary>Once per handler, when it ended: the main checkout pulled and Whats A Route reloaded when origin/main moved
    /// (MainSync). Handlers can't do it themselves (worktree isolation refuses commands aimed at the main checkout). Kept
    /// in state.json, so a step retried after a bridge failure doesn't run it again.</summary>
    private async Task<MainSync.Outcome?> SyncMainAsync(FeedbackHandlerState h, CancellationToken ct)
    {
        if (h.Sync != null) return h.SyncFailed == true ? new MainSync.Outcome(true, h.Sync, h.Sync, h.SyncFix) : null;
        if (Repo() is not { } repo) { h.Sync = "skipped: the main scaffolding checkout is unknown"; SaveHandler(h); return null; }
        MainSync.Outcome o;
        // The sync can take minutes (fetch, pull, a compile): the status file keeps being written meanwhile, or
        // feedback_dispatcher would call this dispatcher not running.
        (_status, _why) = ("syncing", $"bringing #{h.Seq}'s merged fix into the HUD (pull, submodule update, reload)");
        using var beat = new CancellationTokenSource();
        var beating = Task.Run(async () =>
        {
            while (!beat.IsCancellationRequested) { WriteStatus(); try { await Task.Delay(1000, beat.Token); } catch (OperationCanceledException) { } }
        });
        try { o = await Task.Run(() => MainSync.RunAsync(bridges, _game, repo, ct), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            o = new MainSync.Outcome(true, $"syncing the main checkout failed: {ex.Message.Split('\n')[0]}", $"syncing the main checkout failed: {ex.Message.Split('\n')[0]}");
        }
        finally { beat.Cancel(); await beating; }
        Console.Error.WriteLine($"[Dispatch] #{h.Seq}: sync: {o.Summary}");
        h.Sync = o.Summary;
        h.SyncFailed = o.Failed;
        h.SyncFix = o.Fix;
        SaveHandler(h);
        return o.Failed ? o : null;
    }

    /// <summary>A failed sync goes on the card after the handler's answer (so it is the line left showing), by the
    /// dispatcher that said the answer.</summary>
    private async Task TellSyncFailedAsync(FeedbackHandlerState h, MainSync.Outcome? failed, CancellationToken ct)
    {
        if (failed == null || !h.Replied) return;
        await ReplyAsync(h.Seq, "failed", MainSync.FailText(h.Seq, failed), ct);
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
            _env = (_env.Exe, false, $"checking the Claude Code CLI failed: {ex.Message}", null, null);
        }
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
            // An open one with another fix (an older build's) is updated, so the board never keeps a stale fix.
            if (onlyIfNew && IncidentLogic.OpenByKey(IncidentStore.Read(), IncidentKey) is { } open && open.Fix == fix) return true;
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
                LoginCommand = _env.Login,
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
        while (!stop.IsCancellationRequested && !Retired)
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
