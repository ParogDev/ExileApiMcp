using System.ComponentModel;
using System.Diagnostics;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Several agents on one HUD (bridge session.* / lease.* / restart.*, Shared\Sessions.cs in the bridge): who is connected
/// and what each is doing, leases that keep a restart away from a running test, and the coordinated HUD restart. This
/// server identifies itself on every connect (Hosting/SessionIdentity.cs), so the bridge already knows who asks; these
/// tools expose the rest. The restart itself is tools\restart-hud.ps1 in the scaffolding repo, which asks the bridge
/// first and waits for the in-game answer; hud_restart runs it and relays what it printed.
/// </summary>
[McpServerToolType]
public static class SessionTools
{
    [McpServerTool(Name = "hud_sessions", Title = "Who is connected to the HUD and what they do", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(SessionsView))]
    [Description("The other agents on this HUD (Claude Code sessions in worktrees, Claude Desktop, scripts): their labels, what " +
                 "each is doing right now (measuring, recording, piloting the user through a step, compiling), explicit leases, " +
                 "pending HUD restart requests with what blocks them, who holds the in-game card and what each session waits to show " +
                 "the player (its attention-queue items with their place in line; exile://attention/{game}/queue is the live view), " +
                 "plus the open incidents on the machine-wide board (bridge down, restart, ports, deploys incl. orphaned ones) with " +
                 "each session's response. Check it before a HUD restart or a long measurement " +
                 "when you share the HUD; 'you' is your own session id.")]
    public static async Task<CallToolResult> HudSessions(BridgeRegistry bridges,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (listBridge, r) = await bridges.CallAsync(game, "session.list", new JObject(), ct);
        if (r is not JObject o || o["sessions"] == null)
            throw new McpException("This HUD's bridge has no sessions yet: update What's an AI Bridge and restart the HUD.");
        var text = SessionsText(o);
        // The bridge's measurement patches (Instrumentation.cs): what is patched now, and anything left behind.
        if (o["instrumentation"] is JObject instr)
        {
            text += "\n" + InstrumentationText(instr);
            Incidents.InstrumentIncidents.Check(listBridge.Game == "auto" ? game : listBridge.Game, instr);
        }
        var view = Dto.From<SessionsView>(o);
        // How this server's own label was decided (roots adopted or not, and why).
        if (SessionIdentityInfo.Current() is { } idn)
        {
            view.Identity = idn;
            text += $"\nThis server's label '{idn.Label}' ({idn.Source}): {idn.Note}";
        }
        // The incident board: what is broken for everyone right now, and how each session answered (Incidents/).
        // A board problem never costs the session list: it is named in the text instead.
        try
        {
            Incidents.DeployIncidents.Scan();
            var open = Incidents.IncidentStore.Read().Incidents.Where(i => i.IsOpen).OrderByDescending(i => i.RaisedAt).ToList();
            view.Incidents = IncidentTools.Views(open);
            text += open.Count == 0 ? "\nIncident board: nothing open." : $"\nIncident board ({open.Count} open; incident_respond to answer):\n{IncidentTools.Lines(open)}";
        }
        catch (Exception ex) { text += $"\nIncident board unreadable ({Incidents.IncidentStore.FilePath}): {ex.GetType().Name}: {ex.Message}"; }
        return Dto.Result(view, text);
    }

    /// <summary>One or two lines: the measurements patching the HUD now, and what is patched with none running (should be nothing).</summary>
    internal static string InstrumentationText(JObject instr)
    {
        var active = (instr["active"] as JArray ?? []).OfType<JObject>().ToList();
        var line = active.Count == 0 ? "HUD patches: none (no measurement running)"
            : "HUD patches: " + string.Join("; ", active.Select(a => $"{a["kind"]} {a["id"]} by {a["who"] ?? "?"} ({a["label"]}): {a["methods"]} methods, ends within {a["secondsLeft"]} s"));
        var left = instr["leftover"]?["count"]?.Value<int>() ?? 0;
        if (left > 0) line += $"\nLEFT PATCHED with no measurement: {left} methods ({string.Join(", ", (instr["leftover"]?["methods"] as JArray ?? []).Take(5))}) - reload_plugin them";
        if (instr["lastProblem"] is JObject p) line += $"\nLast instrumentation problem ({p["at"]}): {p["kind"]}: {p["text"]}";
        return line;
    }

    /// <summary>A bridge work[] array as typed items (unreadable entries skipped: the text never fails over them).</summary>
    private static List<AgentWork> Work(JToken? arr)
    {
        var list = new List<AgentWork>();
        foreach (var w in (arr as JArray ?? []).OfType<JObject>())
            try { list.Add(Dto.From<AgentWork>(w)); } catch (Exception ex) when (ex is System.Text.Json.JsonException or McpException) { }
        return list;
    }

    private static string SessionsText(JObject o)
    {
        var lines = new List<string>();
        if (o["agents"]?.Type == JTokenType.Integer) lines.Add($"Agents on this HUD (the in-game chip): {o["agents"]}");
        foreach (var s in o["sessions"] as JArray ?? [])
        {
            var doing = string.Join("; ", (s["doing"] as JArray ?? []).Select(d => d.ToString()));
            // The server build it runs (bridge session.hello mcp; absent from older servers and bridges).
            var mcp = s["mcp"] is JObject m ? $" (mcp {m["version"]}{(m["mode"]?.ToString() is { } mode && mode != "supervised" ? $", {mode}" : "")})" : "";
            // Newer bridges say who the chip counts: present agents (*), stale shell callers (~, idle 10+ min), the shared
            // server's own connections (infra). Older ones: connected (*) or not (-).
            var connected = s["connected"]?.Value<bool>() == true;
            var stale = s["stale"]?.Value<bool>() == true;
            var infra = s["infra"]?.Value<bool>() == true;
            var state = !connected ? "disconnected" : stale ? $"stale (no call for {(s["idleSec"]?.Value<int>() ?? 0) / 60} min)" : "idle";
            lines.Add($"{(!connected ? "-" : stale ? "~" : "*")} {s["label"]}{(s["id"]?.ToString() == o["you"]?.ToString() ? " (you)" : "")}{(infra ? " (shared server, not an agent)" : "")}" +
                      $"{(s["branch"] != null && s["branch"]?.ToString() != s["label"]?.ToString() ? $" [{s["branch"]}]" : "")}{mcp}: {(doing.Length > 0 ? doing : state)}");
            // Its attention items: what it waits to show the player (position 0 = on the card now).
            foreach (var q in (s["queued"] as JArray ?? []).OfType<JObject>())
                lines.Add($"    {(q["position"]?.Value<int>() == 0 ? "on the card" : $"#{q["position"]} in line")}: {q["kind"]} {q["title"]} ({q["id"]}{(q["mergedInto"] != null ? $", shares {q["mergedInto"]}" : "")})");
            // Its rows on the agents board (bridge work items).
            foreach (var w in Work(s["work"])) lines.Add($"    work: {AgentStatusTools.WorkLine(w)}");
        }
        if (Work(o["work"]) is { Count: > 0 } loose) lines.Add("Work with no session yet: " + string.Join("; ", loose.Select(AgentStatusTools.WorkLine)));
        if (Work(o["recent"]) is { Count: > 0 } recent)
            lines.Add("Recent: " + string.Join("; ", recent.Take(6).Select(w => $"{(w.Seq is { } q ? $"F8 #{q}" : w.Title ?? w.Key)} {w.Stage}" +
                                                                               (w.UpdatedAt is { } at ? $" {(int)(DateTimeOffset.UtcNow - at).TotalMinutes} min ago" : ""))));
        if (o["mcpRollout"] is JObject ro)
        {
            static string Names(JToken? t) => string.Join(", ", (t as JArray ?? []).Select(x => x.ToString()));
            var behind = Names(ro["behind"]); var unsup = Names(ro["unsupervised"]);
            lines.Add($"MCP {ro["deployedVersion"]} ({ro["deployedSha"]}) deployed: {ro["onDeployed"]} of {ro["total"]} on it" +
                      (behind.Length > 0 ? $"; swapping: {behind}" : "") + (Names(ro["local"]) is { Length: > 0 } loc ? $"; pinned local: {loc}" : "") +
                      (unsup.Length > 0 ? $"; need a session restart to follow deploys: {unsup}" : ""));
        }
        if (o["floor"] is JObject floor) lines.Add($"The card (floor): {floor["who"]}'s {floor["kind"]}: {floor["title"]}");
        var blockers = (o["blockers"] as JArray ?? []).OfType<JObject>().ToList();
        if (blockers.Count > 0)
            lines.Add("A restart would interrupt: " + string.Join("; ", blockers.Select(b => $"{(b["who"] != null ? b["who"] + "'s " : "")}{b["label"]}" + (b["secondsLeft"] != null ? $" ({b["secondsLeft"]} s left)" : ""))));
        else lines.Add("Nothing running that a restart would interrupt.");
        foreach (var r in (o["restarts"] as JArray ?? []).OfType<JObject>().Where(r => r["status"]?.ToString() is "waiting" or "go"))
            lines.Add($"Restart {r["id"]} by {r["who"]}: {r["status"]}" + (r["reason"] != null ? $" ({r["reason"]})" : "") + (r["secondsToGo"] != null ? $", in {r["secondsToGo"]} s" : ""));
        return string.Join("\n", lines);
    }

    [McpServerTool(Name = "hud_lease", Title = "Keep a HUD restart away from your work", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(LeaseInfo))]
    [Description("Take, renew or release a lease that blocks HUD restarts by other agents while you run something a restart would " +
                 "ruin: a performance comparison across several tools, a piloted test with the user, a long recording. The HUD's " +
                 "own activity (pipeline_trace, profile_plugin, reload_plugin, await_change, queued steps, flows, recordings) " +
                 "is covered without a lease; use this for anything else, and release it when done (it expires after ttlSec " +
                 "anyway). Refused with restart_pending while a restart is already granted: wait for the HUD to come back.")]
    public static async Task<CallToolResult> HudLease(BridgeRegistry bridges,
        [Description("acquire | renew | release")] string action,
        [Description("acquire: perf | pilot | record | reload | other")] string kind = "other",
        [Description("acquire: what you are doing, in words, e.g. 'perf comparison of Whats A Route before/after' (shown to the user and to the agent that wants to restart)")] string? label = null,
        [Description("How long the lease lasts without a renew, seconds (5-3600, default 120)")] int ttlSec = 120,
        [Description("renew / release: the lease id")] string? id = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var a = action.Trim().ToLowerInvariant();
        switch (a)
        {
            case "acquire":
            {
                if (string.IsNullOrWhiteSpace(label)) throw new McpException("acquire needs label: what you are doing.");
                var (_, r) = await bridges.CallAsync(game, "lease.acquire", new JObject { ["kind"] = kind, ["label"] = label, ["ttlSec"] = ttlSec }, ct);
                return TypedReply.Of<LeaseInfo>(r);
            }
            case "renew":
            {
                if (string.IsNullOrWhiteSpace(id)) throw new McpException("renew needs id.");
                var (_, r) = await bridges.CallAsync(game, "lease.renew", new JObject { ["id"] = id, ["ttlSec"] = ttlSec }, ct);
                return TypedReply.Of<LeaseInfo>(r);
            }
            case "release":
            {
                if (string.IsNullOrWhiteSpace(id)) throw new McpException("release needs id.");
                var (_, r) = await bridges.CallAsync(game, "lease.release", new JObject { ["id"] = id }, ct);
                return TypedReply.Of<LeaseReleased>(r);
            }
            default:
                throw new McpException("action must be acquire, renew or release.");
        }
    }

    [McpServerTool(Name = "hud_restart", Title = "Restart the HUD, coordinated with the other agents", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(RestartResult))]
    [Description("Restart (or stop) a game's HUD through tools\\restart-hud.ps1, asking the HUD's bridge first: the restart waits " +
                 "while another agent's measurement, recording, piloted step or compile runs (it goes ahead when they finish), " +
                 "and a free restart still shows a short countdown on the in-game card with Not now. The user can press Restart " +
                 "now (over the blockers) or Not now (denied: ask in chat, or retry later). If another agent is restarting " +
                 "already, this merges into it and waits for the HUD to come back (back = the bridge answers and the game is in). " +
                 "Needed after editing Whats An AI Bridge " +
                 "itself, adding a plugin folder, or a HUD core update; everything else is reload_plugin. Blocks up to waitSec " +
                 "(run it through tools\\mcp-call.ps1 in the background for long waits). force=true skips the coordination: " +
                 "only when the user asked for that. While the user's planned restart (an outage incident) is open, an agent " +
                 "restart is refused with held_by_outage. mode=user announces the user's own restart of game + HUD (a patch, " +
                 "a crash): it raises the outage incident, shows the in-game card (who is busy, who is ready) and returns at " +
                 "once with {outageId, phase, busy, ready}. The HUD stops only on the user's Restart now (an 8 s countdown " +
                 "with Cancel first); the incident resolves (back) when the game is in again, or on Not now / Cancel (cancelled). " +
                 "Agent mode reports hudAliveSec / bridgeAfterSec / inGameAfterSec, seconds after the new HUD process started. " +
                 "Your own leases (hud_lease, also from before you entered another worktree) never block your own restart: they " +
                 "are released for it (ownLeases) and taken again if the HUD isn't restarted. The restart doesn't depend on this " +
                 "call: a caller that stops waiting (a client timeout) doesn't stop it, and a second call for the same game joins it.")]
    public static async Task<CallToolResult> HudRestart(BridgeRegistry bridges,
        [Description("Why, in a few words (shown on the card and in the guide log), e.g. 'bridge change: sessions'")] string reason,
        [Description("poe1 | poe2 (optional when only one HUD is configured as running)")] string? game = null,
        [Description("How long to wait for blockers and the user, seconds (0-3600, default 600; 0 = fail at once when blocked)")] int waitSec = 600,
        [Description("Stop the HUD without starting it again")] bool stopOnly = false,
        [Description("Skip the coordination (interrupts whatever runs): only when the user asked for it")] bool force = false,
        [Description("agent (default): this agent restarts the HUD now, coordinated. user: the user restarts game + HUD by hand " +
                     "(announce, hold for busy agents, stop the HUD, resolve when the game is back); returns at once")] string mode = "agent",
        CancellationToken ct = default)
    {
        var bridge = bridges.Resolve(game);
        var g = bridge.Game == "auto" ? (game ?? throw new McpException("Pass game: poe1 or poe2.")) : bridge.Game;
        var m = (mode ?? "agent").Trim().ToLowerInvariant();
        if (m is not ("agent" or "user")) throw new McpException($"mode must be agent or user, not '{mode}'.");
        if (m == "user") return await UserRestart(bridges, g, reason, ct);
        waitSec = Math.Clamp(waitSec, 0, 3600);

        // The user's planned restart holds every agent restart until the game is back (contract: held_by_outage).
        if (HeldBy(g) is { } held) return Held(g, reason, held);

        // The restart runs detached from this request: a caller that stops waiting (mcp-call.ps1's client timeout, a
        // cancelled tool call) no longer kills restart-hud.ps1 halfway through a restart, and a second call for the same
        // game while one runs in this server joins it instead of starting another.
        Task<AgentRestartOutcome> run;
        bool joined;
        lock (InFlightLock)
        {
            joined = InFlight.TryGetValue(g, out var cur) && !cur.IsCompleted;
            run = joined ? cur! : Task.Run(() => AgentRestartAsync(bridges, bridge, g, reason, waitSec, stopOnly, force));
            if (!joined) InFlight[g] = run;
        }
        var waitStart = DateTime.UtcNow;
        try
        {
            var outcome = await run.WaitAsync(ct);
            return outcome.ToCall(joined ? $"Joined the {g} restart already running in this server ({outcome.Result.Who}: {outcome.Result.Reason}). " : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"[hud_restart] {g}: the caller stopped waiting after {(DateTime.UtcNow - waitStart).TotalSeconds:0} s (client timeout?): the restart carries on, its result is logged here");
            if (!joined)
                _ = run.ContinueWith(t => Console.Error.WriteLine(t.IsCompletedSuccessfully
                    ? $"[hud_restart] {g}: {t.Result.Result.Status} (exit {t.Result.Result.ExitCode}) after its caller left"
                    : $"[hud_restart] {g}: failed after its caller left: {t.Exception?.GetBaseException().Message}"), TaskScheduler.Default);
            throw;
        }
    }

    private static readonly object InFlightLock = new();
    /// <summary>The agent restart running in this server per game (a finished task is replaced by the next call).</summary>
    private static readonly Dictionary<string, Task<AgentRestartOutcome>> InFlight = new(StringComparer.OrdinalIgnoreCase);

    private sealed record AgentRestartOutcome(RestartResult Result, string Text)
    {
        public CallToolResult ToCall(string? prefix)
        {
            var call = Dto.Result(Result, (prefix ?? "") + Text);
            if (Result.ExitCode != 0) call.IsError = true;
            return call;
        }
    }

    /// <summary>The longest an agent restart may take end to end: the script's own limit (waitSec + 240 s), the wait for the
    /// HUD to come back (<see cref="HudAvailabilityLogic.StartupStuckSec"/> + 60 s for the game), and slack.</summary>
    public static TimeSpan AgentRestartBudget(int waitSec) => TimeSpan.FromSeconds(waitSec + 240 + HudAvailabilityLogic.StartupStuckSec + 120);

    /// <summary>One agent restart, start to back, independent of any caller's cancellation (bounded by <see cref="AgentRestartBudget"/>).</summary>
    private static async Task<AgentRestartOutcome> AgentRestartAsync(BridgeRegistry bridges, BridgeClient bridge, string g, string reason, int waitSec, bool stopOnly, bool force)
    {
        using var budget = new CancellationTokenSource(AgentRestartBudget(waitSec));
        var ct = budget.Token;
        var sw = Stopwatch.StartNew();
        // This session's own leases would block its own restart (the script asks as its own bridge session, ps:<label>):
        // release them first (a restart ends them anyway) and take them again if the HUD isn't restarted after all.
        var own = force ? new OwnLeaseRelease([], []) : await ReleaseOwnLeasesAsync(bridges, g, ct);
        // Once the script has the go and starts stopping the HUD (restart-state.json phase "stopping"), put the restart on the
        // incident board with an ETA, so every session hears of it at once (Incidents/IncidentAuto.cs).
        var callStart = DateTime.UtcNow;
        // The HUD process before the restart: the new one is a process started after it (WaitBackAsync times from its start).
        var hudAtCall = Incidents.BridgeDiagnosis.FindHud(HudDirOf(bridge)).StartedAt;
        Incidents.Incident? incident = null;
        using var watchStop = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            while (!watchStop.IsCancellationRequested && incident == null)
            {
                incident = Incidents.IncidentAuto.CheckRestartState(bridge, callStart);
                try { await Task.Delay(1000, watchStop.Token); } catch (OperationCanceledException) { }
            }
        });
        int code;
        List<string> output;
        try { (code, output) = await RunRestartScriptAsync(g, SessionIdentity.Label, reason, waitSec, stopOnly, force, "agent", null, ct); }
        catch (McpException ex) when (own.Released.Count > 0)
        {
            // The script gave no result: whether the HUD restarted is unknown, so the leases aren't taken again; say so.
            throw new McpException($"{ex.Message} This session's own lease(s) {string.Join(", ", own.Released.Select(l => l.Id))} were released for the restart and not taken again.");
        }
        finally
        {
            watchStop.Cancel();
            await watch;
        }
        incident ??= Incidents.IncidentAuto.CheckRestartState(bridge, callStart);
        // Not restarted (UAC declined, Not now, still blocked by others, held): the HUD still runs, so the leases this call
        // released are taken again (new ids, the same kind, label and time left).
        if (own.Released.Count > 0 && code is 1 or 2 or 5 or 6 or 8)
            await ReacquireLeasesAsync(bridges, g, own, ct);
        output.InsertRange(0, own.Lines());

        var status = code switch
        {
            0 => stopOnly ? "stopped" : "restarted",
            2 => "cancelled", 3 => "error", 4 => "error",
            5 => "denied", 6 => "blocked", 7 => "merged", 8 => "held_by_outage", 9 => "bridge_not_back",
            _ => "error",
        };
        var result = new RestartResult { Game = g, Mode = "agent", Status = status, Ok = code == 0, ExitCode = code, Who = SessionIdentity.Label, Reason = reason, Output = output,
                                         OwnLeases = own.Released.Count > 0 ? own.Released : null };
        if (code == 0 && !stopOnly)
        {
            // The new HUD writes a new token; our client reconnects (and re-identifies) on the next call. Back = the bridge
            // answers AND hud-state.json says the game is in (no file: an older bridge, back on the answer).
            await WaitBackAsync(bridges, bridge, g, result, hudAtCall, ownScript: true, ct);
            result.Note = result.InGameAfterSec != null
                ? $"The HUD is back: process alive {Secs(result.HudAliveSec)}, bridge listener answered {Secs(result.BridgeAfterSec)}, game in {Secs(result.InGameAfterSec)} after the HUD started. " +
                  "Plugins compiled at startup: check hud_plugins for the one you changed."
                : result.BridgeAfterSec != null
                    ? $"The HUD restarted and its bridge listener answers ({Secs(result.BridgeAfterSec)} after the HUD started), but the game isn't in yet (phase {result.Phase ?? "?"}): calls answer 'not now' until it is."
                    : $"The HUD process is alive but its bridge listener hasn't answered within {HudAvailabilityLogic.StartupStuckSec:0} s of the start (usually ~{HudAvailabilityLogic.StartupUsualSec:0} s, up to ~{HudAvailabilityLogic.StartupMaxSec:0} s): check hud_log and bridge_status.";
        }
        else if (code == 7)
        {
            await WaitBackAsync(bridges, bridge, g, result, hudAtCall, ownScript: false, ct);
            result.Note = result.InGameAfterSec != null ? $"Another agent restarted the HUD; it is back (bridge {Secs(result.BridgeAfterSec)}, game in {Secs(result.InGameAfterSec)} after the HUD started)."
                : result.BridgeAfterSec != null ? $"Another agent restarted the HUD; the bridge listener answers but the game isn't in yet (phase {result.Phase ?? "?"})."
                : "Another agent was restarting the HUD; its bridge listener isn't answering yet (bridge_status).";
        }
        else if (code == 8)
        {
            result.HeldBy = HeldBy(g);
            result.Error = "held_by_outage";
            result.Note = result.HeldBy != null ? HeldText(result.HeldBy) : "restart-hud.ps1 refused: held by the user's planned restart (an outage incident); see output.";
        }
        else result.Note = status switch
        {
            "denied" => "The user pressed Not now on the in-game card. Say what you need the restart for in chat, or retry later.",
            "blocked" => "Still blocked when the wait ran out (see output for whose work). Retry later with a longer waitSec, or ask the user.",
            "bridge_not_back" => "The HUD process restarted but its new bridge didn't answer its hello within 150 s of the HUD start: bridge-down or ports-reserved is on the incident board (see output). Check hud_log and bridge_status.",
            "cancelled" => "The UAC prompt was declined (no prompt-free task installed: tools\\install-hud-restart-tasks.ps1).",
            _ => "See output; <HUD>\\restart-hud.log has the elevated half's lines.",
        };
        // Close the board's restart incident: back, or not restarted at all. A HUD that doesn't come back keeps it open, and
        // the incident monitor re-diagnoses it (HUD not running, bridge not listening...) or resolves it once the game is in.
        try
        {
            if (code == 0 && !stopOnly && result.InGameAfterSec != null)
                Incidents.IncidentStore.ResolveKeys(Incidents.IncidentAuto.BridgeKeys(g), $"the {g} HUD is back and the game is in, {result.InGameAfterSec} s after the HUD started");
            // Not restarted at all (Loader.exe missing, UAC declined, Not now, still blocked, held): the HUD was never stopped.
            // A restart that failed after stopping (3, 4) leaves the HUD possibly down: its incident stays open.
            else if (code is 1 or 2 or 5 or 6 or 8 && incident != null)
                Incidents.IncidentStore.Resolve(incident.Id, $"restart {status}: {result.Note}");
            var key = stopOnly ? $"bridge-down:{g}" : $"hud-restart:{g}";
            if (Incidents.IncidentLogic.Find(Incidents.IncidentStore.Read(), key) is { } inc && inc.RaisedAt >= callStart.AddSeconds(-1))
                result.Incident = IncidentTools.View(inc, Incidents.IncidentStore.ReadHeartbeats());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { output.Add($"incident board not updated: {ex.Message}"); }
        var text = $"{g}: {status} (exit {code}, {sw.Elapsed.TotalSeconds:0} s)" + ((result.HudAliveSec ?? result.BridgeAfterSec ?? result.InGameAfterSec) is not null
                       ? $", HUD alive {Secs(result.HudAliveSec)} / bridge {Secs(result.BridgeAfterSec)} / in game {Secs(result.InGameAfterSec)} after it started" : "") +
                   (result.Note != null && code is 0 or 7 or 8 ? "\n" + result.Note : "") + "\n" + string.Join("\n", output);
        return new AgentRestartOutcome(result, text);
    }

    // ── Own leases (a session's lease never blocks its own restart) ──

    /// <summary>The leases one call released for its restart, and what happened to them.</summary>
    private sealed record OwnLeaseRelease(List<RestartLease> Released, List<string> Notes)
    {
        public IEnumerable<string> Lines() =>
            Released.Select(l => $"own lease {l.Id} ({l.Kind}: {l.Label}) released for this restart" +
                                 (l.ReacquiredAs != null ? $", taken again as {l.ReacquiredAs} (not restarted)" : l.ReacquireError != null ? $", NOT taken again: {l.ReacquireError}" : ""))
                .Concat(Notes);
    }

    /// <summary>
    /// The bridge session ids that are this caller (pure; tests call it): the id the bridge knows this connection by
    /// (session.list "you"), this server's id for the caller, and on the shared HTTP server every caller with the same client
    /// session id (the Claude Code session, X-Hexile-Session): a session that entered another worktree is another label and
    /// cwd, so another bridge session, with its earlier leases still on the first one. Never matched by label alone.
    /// </summary>
    public static HashSet<string> OwnSessionIds(string? you, string? current, IEnumerable<string>? sameClient)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in new[] { you, current }.Concat(sameClient ?? []))
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        return ids;
    }

    /// <summary>The leases (session.list leases[]) held by one of <paramref name="ownSessionIds"/> (pure; tests call it).</summary>
    public static List<RestartLease> OwnLeases(JToken? leases, IReadOnlySet<string> ownSessionIds) =>
        (leases as JArray ?? []).OfType<JObject>()
            .Where(l => l["session"]?.ToString() is { Length: > 0 } s && ownSessionIds.Contains(s) && l["id"]?.ToString() is { Length: > 0 })
            .Select(l => new RestartLease
            {
                Id = l["id"]!.ToString(), Kind = l["kind"]?.ToString() ?? "other", Label = l["label"]?.ToString() ?? "", Session = l["session"]!.ToString(),
                SecondsLeft = l["secondsLeft"]?.Type == JTokenType.Integer ? l["secondsLeft"]!.Value<int>() : null,
            }).ToList();

    /// <summary>Releases this caller's own leases on <paramref name="g"/>'s HUD. Never throws: a problem is a note.</summary>
    private static async Task<OwnLeaseRelease> ReleaseOwnLeasesAsync(BridgeRegistry bridges, string g, CancellationToken ct)
    {
        var r = new OwnLeaseRelease([], []);
        try
        {
            var (_, list) = await bridges.CallAsync(g, "session.list", new JObject(), ct);
            var ids = OwnSessionIds(list?["you"]?.ToString(), SessionIdentity.Id, bridges.SessionIdsOfClient(SessionIdentity.ServerSession));
            foreach (var l in OwnLeases(list?["leases"], ids))
            {
                try
                {
                    await bridges.CallAsync(g, "lease.release", new JObject { ["id"] = l.Id }, ct);
                    r.Released.Add(l);
                }
                catch (Exception ex) when (ex is McpException or BridgeException) { r.Notes.Add($"own lease {l.Id} could not be released ({ex.Message}): it may block this restart"); }
            }
        }
        catch (Exception ex) when (ex is McpException or BridgeException) { r.Notes.Add($"own leases not checked (session.list: {ex.Message}): one of yours may block this restart"); }
        return r;
    }

    /// <summary>Takes the released leases again (same kind, label and time left, at least 30 s). Never throws.</summary>
    private static async Task ReacquireLeasesAsync(BridgeRegistry bridges, string g, OwnLeaseRelease own, CancellationToken ct)
    {
        foreach (var l in own.Released)
        {
            try
            {
                var (_, a) = await bridges.CallAsync(g, "lease.acquire", new JObject { ["kind"] = l.Kind, ["label"] = l.Label, ["ttlSec"] = Math.Clamp(l.SecondsLeft ?? 120, 30, 3600) }, ct);
                l.ReacquiredAs = a?["id"]?.ToString();
                if (l.ReacquiredAs == null) l.ReacquireError = a?["error"]?.ToString() ?? "no lease id in the reply";
            }
            catch (Exception ex) when (ex is McpException or BridgeException) { l.ReacquireError = ex.Message; }
        }
    }

    private static string HudDirOf(BridgeClient bridge) =>
        Path.GetDirectoryName(Path.GetFullPath(bridge.BridgeDir).TrimEnd('\\', '/')) ?? bridge.BridgeDir;

    private static string Secs(double? s) => s is { } x ? $"{x:0.#} s" : "?";

    /// <summary>A file the new bridge hasn't written yet answers for this long after its listener is up before we take the
    /// listener alone as back (a bridge that writes no hud-state.json).</summary>
    private static readonly TimeSpan NewFileGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits until the new HUD is back: its bridge LISTENER answers (ping, answered on the listener thread: BridgeAfterSec)
    /// AND hud-state.json says the game is in (InGameAfterSec; no file, or no file newer than the process after
    /// <see cref="NewFileGrace"/>: the listener suffices, as with older bridges). "The process is alive" (HudAliveSec: the
    /// script's alive check, or our first sight of the process for a merged restart) is not back: the listener comes
    /// ~15-100 s after the start, after ExileCore2's preload and the plugin compile. All three are seconds after the new
    /// HUD process started (its StartTime; the script's end when unknown). The listener gets up to
    /// <see cref="HudAvailabilityLogic.StartupStuckSec"/> after the start, then the game 60 s more from its answer.
    /// </summary>
    private static async Task WaitBackAsync(BridgeRegistry bridges, BridgeClient bridge, string g, RestartResult result, DateTime? previousStart, bool ownScript, CancellationToken ct)
    {
        var hudDir = HudDirOf(bridge);
        var scriptEnd = DateTime.UtcNow;
        // Our own script exits 0 only after its alive check: the process is alive at its end.
        DateTime? hudStart = null, aliveAt = ownScript ? scriptEnd : null, bridgeAt = null, inGameAt = null;
        var looked = false;
        var deadline = scriptEnd + TimeSpan.FromSeconds(HudAvailabilityLogic.StartupStuckSec);
        if (!ownScript) await Task.Delay(TimeSpan.FromSeconds(3), ct);   // a merged restart: let the old HUD go first
        while (DateTime.UtcNow < deadline)
        {
            if (hudStart == null && (!looked || aliveAt == null))
            {
                looked = true;
                // running null: the process list can't say (access); take it as alive and let the listener decide.
                var (running, _, started) = Incidents.BridgeDiagnosis.FindHud(hudDir);
                if (running != false && (previousStart == null || started == null || started > previousStart.Value.AddSeconds(1)))
                {
                    hudStart = started;
                    aliveAt ??= DateTime.UtcNow;
                    if (started is { } st) deadline = st + TimeSpan.FromSeconds(HudAvailabilityLogic.StartupStuckSec);
                }
            }
            if (aliveAt != null)
            {
                try
                {
                    await bridges.CallAsync(g, "ping", new JObject(), ct);
                    bridgeAt ??= DateTime.UtcNow;
                    var s = HudStateReader.Read(bridge.BridgeDir);
                    result.Phase = s?.Phase;
                    // The previous HUD's file (last heartbeat before this process started): the new bridge hasn't written yet.
                    var oldFile = s != null && hudStart != null && (s.Heartbeat is not { } hb || hb < hudStart.Value);
                    var back = oldFile ? DateTime.UtcNow - bridgeAt.Value > NewFileGrace : Incidents.IncidentAuto.BackVerdict(s, g, DateTime.UtcNow).Resolve;
                    if (back) { inGameAt = DateTime.UtcNow; break; }
                    // The listener answers from the menu or a load: give the game 60 s more from now.
                    if (deadline < DateTime.UtcNow + TimeSpan.FromSeconds(60)) deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                }
                catch (McpException) { }
            }
            await Task.Delay(1000, ct);
        }
        var origin = hudStart ?? scriptEnd;
        static double? Since(DateTime? at, DateTime origin) => at is { } t ? Math.Round(Math.Max(0, (t - origin).TotalSeconds), 1) : null;
        result.HudStartedAt = hudStart;
        result.HudAliveSec = Since(aliveAt, origin);
        result.BridgeAfterSec = Since(bridgeAt, origin);
        result.InGameAfterSec = Since(inGameAt, origin);
        result.BackAfterSec = result.InGameAfterSec;
    }

    /// <summary>The open outage of this game that holds agent restarts, as the typed refusal; null when none.</summary>
    public static OutageHold? HeldBy(string game)
    {
        try
        {
            var i = Incidents.OutageLogic.OpenFor(Incidents.IncidentStore.Read(), game);
            if (i == null) return null;
            var info = Incidents.OutageLogic.Info(i);
            return new OutageHold { Id = i.Id, Phase = info?.Phase ?? "?", Game = game, Reason = info?.Reason, RequestedBy = info?.RequestedByLabel ?? i.RaisedBy };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string HeldText(OutageHold h) =>
        $"Held by the user's planned restart ({h.Id}, phase {h.Phase}{(h.Reason != null ? $": {h.Reason}" : "")}, announced by {h.RequestedBy}): " +
        "agent restarts wait until the game is back in. incident_wait tells you when it resolves; then retry.";

    private static CallToolResult Held(string g, string reason, OutageHold held)
    {
        var result = new RestartResult
        {
            Game = g, Mode = "agent", Status = "held_by_outage", Ok = false, ExitCode = 8, Who = SessionIdentity.Label, Reason = reason,
            Error = "held_by_outage", HeldBy = held, Note = HeldText(held),
        };
        var call = Dto.Result(result, $"{g}: held_by_outage\n{result.Note}");
        call.IsError = true;
        return call;
    }

    /// <summary>mode=user: raise (or join) the outage; a new one shows the card and starts the runner here. Answers at once.</summary>
    private static async Task<CallToolResult> UserRestart(BridgeRegistry bridges, string g, string reason, CancellationToken ct)
    {
        var (inc, created) = Incidents.IncidentStore.Update(f =>
            Incidents.OutageLogic.Raise(f, g, reason, SessionIdentity.Id, SessionIdentity.Label, SessionIdentity.Pid, DateTime.UtcNow));
        var info = Incidents.OutageLogic.Info(inc) ?? new Incidents.OutageInfo { Game = g, Reason = reason };
        var notes = new List<string>();
        if (created && await Incidents.OutageRunner.SetCardAsync(bridges, g, inc.Id, info.Phase, info.Reason, info.RequestedByLabel, false, ct) is { } cardNote) notes.Add(cardNote);
        var (busy, ready, answer, readinessNote) = await Incidents.OutageRunner.ReadinessAsync(bridges, g, inc.Id, info.RequestedBy, ct);
        if (readinessNote != null) notes.Add(readinessNote);
        Incidents.IncidentStore.Update(f => Incidents.IncidentLogic.Find(f, inc.Id) is { IsOpen: true } i && Incidents.OutageLogic.SetParticipants(i, answer, busy, ready, DateTime.UtcNow));
        // Join only when it was already announced: its owner's runner drives it (or a monitor adopts it once that owner is gone).
        if (created && Incidents.OutageRunner.Start(bridges, g, inc.Id) is { } startNote) notes.Add($"runner not started here: {startNote}");
        static RestartOutageSession View(Incidents.OutageSession s) => new() { Session = s.Session, Label = s.Label, Doing = s.Doing, Lease = s.Lease };
        var result = new RestartResult
        {
            Game = g, Mode = "user", Status = created ? "announced" : "already_announced", Ok = true, ExitCode = 0, Who = SessionIdentity.Label, Reason = info.Reason ?? reason,
            OutageId = inc.Id, Phase = info.Phase, Busy = busy.Select(View).ToList(), Ready = ready.Select(View).ToList(),
            Note = (created ? $"Outage {inc.Id} announced: the in-game card tells the user. " : $"The user's restart {inc.Id} was already announced (phase {info.Phase}). ") +
                   (busy.Count > 0 ? $"Busy: {string.Join(", ", busy.Select(b => b.Label + (b.Doing != null ? $" ({b.Doing})" : "")))} (the card shows them to the user). "
                                   : "Nobody is busy (the card says so). ") +
                   $"The HUD stops only when the user presses Restart now on the card: then a {Incidents.OutageLogic.StopCountdown.TotalSeconds:0} s countdown with Cancel, " +
                   "then the stop; the user restarts game + HUD by hand and the incident resolves (back) once the game is in, or cancelled on Not now / Cancel. " +
                   "The bridge posts every outage toast itself: don't guide.log or toast the stop, the load or the menu. " +
                   "Wait with incident_wait." + (notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : ""),
        };
        result.Output = notes;
        return Dto.Result(result, $"{g}: {result.Status} ({inc.Id}, phase {info.Phase})\n{result.Note}");
    }

    /// <summary>
    /// Runs tools\restart-hud.ps1 -Game g -Who who -Reason reason -WaitSec n [-StopOnly] [-Force] [-Kind kind] [-Outage id]
    /// and returns its exit code and output. -Kind and -Outage go only to a script that declares them (an older checkout).
    /// </summary>
    public static async Task<(int Code, List<string> Output)> RunRestartScriptAsync(string game, string who, string reason, int waitSec,
        bool stopOnly, bool force, string kind, string? outageId, CancellationToken ct)
    {
        var repo = SessionIdentity.RepoRoot() ?? throw new McpException("tools\\restart-hud.ps1 not found: this server isn't running from the scaffolding repo (HEXILE_REPO unset).");
        var script = Path.Combine(repo, "tools", "restart-hud.ps1");
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = repo,
            RedirectStandardInput = true,   // closed below: the script's children must not inherit our (always open) stdin
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Game", game, "-Who", who, "-Reason", reason, "-WaitSec", waitSec.ToString() })
            psi.ArgumentList.Add(arg);
        if (stopOnly) psi.ArgumentList.Add("-StopOnly");
        if (force) psi.ArgumentList.Add("-Force");
        var declared = ScriptParams(script);
        if (declared.Contains("Kind")) { psi.ArgumentList.Add("-Kind"); psi.ArgumentList.Add(kind); }
        if (outageId != null && declared.Contains("Outage")) { psi.ArgumentList.Add("-Outage"); psi.ArgumentList.Add(outageId); }

        using var proc = Process.Start(psi) ?? throw new McpException("Could not start powershell.exe.");
        proc.StandardInput.Close();
        // The pipes are read to the end whatever happens to the caller, so the script never blocks on a full pipe.
        var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        // The restart itself after the coordination: stop (grace 15 s + kill 5 s), the task or UAC prompt, then the bridge
        // wait, 150 s from the new HUD process's start (exit 9 when it runs out), with slack for the UAC prompt.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(waitSec + 240));
        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try { await proc.WaitForExitAsync(either.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only our own limit kills the script. A cancelled caller leaves it running: killing it halfway through a restart
            // (the 2026-10-13 timeouts: mcp-call's 30 s client timeout did exactly that) can leave the HUD stopped.
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new McpException($"restart-hud.ps1 gave no result within {waitSec + 240} s (waitSec {waitSec} + 240 s for the restart itself): it was stopped.");
        }
        var output = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var err = await stderr;
        if (!string.IsNullOrWhiteSpace(err)) output.Add("stderr: " + err.Trim());
        return (proc.ExitCode, output);
    }

    /// <summary>The parameters restart-hud.ps1 declares among those added later (Kind, Outage): "[...]$Name" in its text.</summary>
    private static HashSet<string> ScriptParams(string script)
    {
        try
        {
            var text = File.ReadAllText(script);
            return new[] { "Kind", "Outage" }.Where(n => System.Text.RegularExpressions.Regex.IsMatch(text, @"\]\s*\$" + n + @"\b")).ToHashSet();
        }
        catch (IOException) { return []; }
    }
}
