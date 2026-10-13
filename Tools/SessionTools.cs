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
                 "a crash): it raises the outage incident, shows the in-game card, stops the HUD once nobody is busy (or on " +
                 "Restart now) and returns at once with {outageId, phase, busy, ready}; the incident resolves (back) when the " +
                 "game is in again, or on Not now (cancelled).")]
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

        var sw = Stopwatch.StartNew();
        // Once the script has the go and starts stopping the HUD (restart-state.json phase "stopping"), put the restart on the
        // incident board with an ETA, so every session hears of it at once (Incidents/IncidentAuto.cs).
        var callStart = DateTime.UtcNow;
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
        finally
        {
            watchStop.Cancel();
            await watch;
        }
        incident ??= Incidents.IncidentAuto.CheckRestartState(bridge, callStart);

        var status = code switch
        {
            0 => stopOnly ? "stopped" : "restarted",
            2 => "cancelled", 3 => "error", 4 => "error",
            5 => "denied", 6 => "blocked", 7 => "merged", 8 => "held_by_outage",
            _ => "error",
        };
        var result = new RestartResult { Game = g, Mode = "agent", Status = status, Ok = code == 0, ExitCode = code, Who = SessionIdentity.Label, Reason = reason, Output = output };
        if (code == 0 && !stopOnly)
        {
            // The new HUD writes a new token; our client reconnects (and re-identifies) on the next call. Back = the bridge
            // answers AND hud-state.json says the game is in (no file: an older bridge, back on the answer).
            await WaitBackAsync(bridges, bridge, g, result, TimeSpan.Zero, TimeSpan.FromSeconds(60), ct);
            result.Note = result.BackAfterSec != null
                ? "The HUD is back, the bridge answers and the game is in. Plugins compiled at startup: check hud_plugins for the one you changed."
                : result.BridgeAfterSec != null
                    ? $"The HUD restarted and its bridge answers ({result.BridgeAfterSec} s), but the game isn't in yet (phase {result.Phase ?? "?"}): calls answer 'not now' until it is."
                    : "The HUD restarted but its bridge hasn't answered within 60 s: check hud_log and bridge_status.";
        }
        else if (code == 7)
        {
            await WaitBackAsync(bridges, bridge, g, result, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(90), ct);
            result.Note = result.BackAfterSec != null ? "Another agent restarted the HUD; it is back and the game is in."
                : result.BridgeAfterSec != null ? $"Another agent restarted the HUD; the bridge answers but the game isn't in yet (phase {result.Phase ?? "?"})."
                : "Another agent was restarting the HUD; it isn't back yet (bridge_status).";
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
            "cancelled" => "The UAC prompt was declined (no prompt-free task installed: tools\\install-hud-restart-tasks.ps1).",
            _ => "See output; <HUD>\\restart-hud.log has the elevated half's lines.",
        };
        // Close the board's restart incident: back, or not restarted at all. A HUD that doesn't come back keeps it open, and
        // the incident monitor re-diagnoses it (HUD not running, bridge not listening...) or resolves it once the game is in.
        try
        {
            if (code == 0 && !stopOnly && result.BackAfterSec != null)
                Incidents.IncidentStore.ResolveKeys(Incidents.IncidentAuto.BridgeKeys(g), $"the {g} HUD is back and the game is in, {result.BackAfterSec} s after the restart");
            // Not restarted at all (Loader.exe missing, UAC declined, Not now, still blocked, held): the HUD was never stopped.
            // A restart that failed after stopping (3, 4) leaves the HUD possibly down: its incident stays open.
            else if (code is 1 or 2 or 5 or 6 or 8 && incident != null)
                Incidents.IncidentStore.Resolve(incident.Id, $"restart {status}: {result.Note}");
            var key = stopOnly ? $"bridge-down:{g}" : $"hud-restart:{g}";
            if (Incidents.IncidentLogic.Find(Incidents.IncidentStore.Read(), key) is { } inc && inc.RaisedAt >= callStart.AddSeconds(-1))
                result.Incident = IncidentTools.View(inc, Incidents.IncidentStore.ReadHeartbeats());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { output.Add($"incident board not updated: {ex.Message}"); }
        var text = $"{g}: {status} (exit {code}, {sw.Elapsed.TotalSeconds:0} s)" + (result.BackAfterSec != null ? $", back after {result.BackAfterSec} s" : "") +
                   (result.Note != null && code is 0 or 7 or 8 ? "\n" + result.Note : "") + "\n" + string.Join("\n", output);
        var call = Dto.Result(result, text);
        if (code != 0) call.IsError = true;
        return call;
    }

    /// <summary>
    /// Waits until the bridge answers (BridgeAfterSec) and hud-state.json says the game is in (BackAfterSec; no file or a
    /// stale one: back on the answer, as with older bridges). A bridge that answers from the menu gets up to 60 s more.
    /// </summary>
    private static async Task WaitBackAsync(BridgeRegistry bridges, BridgeClient bridge, string g, RestartResult result, TimeSpan first, TimeSpan budget, CancellationToken ct)
    {
        var back = Stopwatch.StartNew();
        var extended = false;
        if (first > TimeSpan.Zero) await Task.Delay(first, ct);
        while (back.Elapsed < budget)
        {
            try
            {
                await bridges.QueryAsync(g, "hello", ct);
                result.BridgeAfterSec ??= Math.Round(back.Elapsed.TotalSeconds, 1);
                var s = HudStateReader.Read(bridge.BridgeDir);
                result.Phase = s?.Phase;
                if (Incidents.IncidentAuto.BackVerdict(s, g, DateTime.UtcNow).Resolve) { result.BackAfterSec = Math.Round(back.Elapsed.TotalSeconds, 1); return; }
                // The bridge answers from the menu or a load: give the game 60 s more from now, once.
                if (!extended) { budget = TimeSpan.FromSeconds(Math.Max(budget.TotalSeconds, back.Elapsed.TotalSeconds + 60)); extended = true; }
            }
            catch (McpException) { }
            await Task.Delay(1000, ct);
        }
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

    /// <summary>mode=user: raise (or join) the outage, show the card, start the runner, answer at once.</summary>
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
        Incidents.OutageRunner.Start(bridges, g, inc.Id);
        static RestartOutageSession View(Incidents.OutageSession s) => new() { Session = s.Session, Label = s.Label, Doing = s.Doing, Lease = s.Lease };
        var result = new RestartResult
        {
            Game = g, Mode = "user", Status = created ? "announced" : "already_announced", Ok = true, ExitCode = 0, Who = SessionIdentity.Label, Reason = info.Reason ?? reason,
            OutageId = inc.Id, Phase = info.Phase, Busy = busy.Select(View).ToList(), Ready = ready.Select(View).ToList(),
            Note = (created ? $"Outage {inc.Id} announced: the in-game card tells the user. " : $"The user's restart {inc.Id} was already announced (phase {info.Phase}). ") +
                   (busy.Count > 0 ? $"Busy: {string.Join(", ", busy.Select(b => b.Label + (b.Doing != null ? $" ({b.Doing})" : "")))}; the HUD stops when they finish or the user presses Restart now. "
                                   : "Nobody is busy: the HUD stops within seconds. ") +
                   "The user then restarts game + HUD by hand; the incident resolves (back) once the game is in, or cancelled on Not now. " +
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
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(waitSec + 180));   // the restart itself: grace 15 s, start, 8 s alive check, focus
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new McpException($"restart-hud.ps1 gave no result within {waitSec + 180} s.");
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
