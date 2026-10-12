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
        }
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
                 "already, this merges into it and waits for the HUD to come back. Needed after editing Whats An AI Bridge " +
                 "itself, adding a plugin folder, or a HUD core update; everything else is reload_plugin. Blocks up to waitSec " +
                 "(run it through tools\\mcp-call.ps1 in the background for long waits). force=true skips the coordination: " +
                 "only when the user asked for that.")]
    public static async Task<CallToolResult> HudRestart(BridgeRegistry bridges,
        [Description("Why, in a few words (shown on the card and in the guide log), e.g. 'bridge change: sessions'")] string reason,
        [Description("poe1 | poe2 (optional when only one HUD is configured as running)")] string? game = null,
        [Description("How long to wait for blockers and the user, seconds (0-3600, default 600; 0 = fail at once when blocked)")] int waitSec = 600,
        [Description("Stop the HUD without starting it again")] bool stopOnly = false,
        [Description("Skip the coordination (interrupts whatever runs): only when the user asked for it")] bool force = false,
        CancellationToken ct = default)
    {
        var bridge = bridges.Resolve(game);
        var g = bridge.Game == "auto" ? (game ?? throw new McpException("Pass game: poe1 or poe2.")) : bridge.Game;
        var repo = SessionIdentity.RepoRoot() ?? throw new McpException("tools\\restart-hud.ps1 not found: this server isn't running from the scaffolding repo (HEXILE_REPO unset).");
        var script = Path.Combine(repo, "tools", "restart-hud.ps1");
        waitSec = Math.Clamp(waitSec, 0, 3600);

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = repo,
            RedirectStandardInput = true,   // closed below: the script's children must not inherit our (always open) stdin
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Game", g, "-Who", SessionIdentity.Label, "-Reason", reason, "-WaitSec", waitSec.ToString() })
            psi.ArgumentList.Add(arg);
        if (stopOnly) psi.ArgumentList.Add("-StopOnly");
        if (force) psi.ArgumentList.Add("-Force");

        var output = new List<string>();
        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi) ?? throw new McpException("Could not start powershell.exe.");
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(waitSec + 180));   // the restart itself: grace 15 s, start, 8 s alive check, focus
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
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new McpException($"restart-hud.ps1 gave no result within {waitSec + 180} s.");
        }
        finally
        {
            watchStop.Cancel();
            await watch;
        }
        incident ??= Incidents.IncidentAuto.CheckRestartState(bridge, callStart);
        output.AddRange((await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')));
        var err = await stderr;
        if (!string.IsNullOrWhiteSpace(err)) output.Add("stderr: " + err.Trim());

        var code = proc.ExitCode;
        var status = code switch
        {
            0 => stopOnly ? "stopped" : "restarted",
            2 => "cancelled", 3 => "error", 4 => "error",
            5 => "denied", 6 => "blocked", 7 => "merged",
            _ => "error",
        };
        var result = new RestartResult { Game = g, Status = status, Ok = code == 0, ExitCode = code, Who = SessionIdentity.Label, Reason = reason, Output = output };
        if (code == 0 && !stopOnly)
        {
            // The new HUD writes a new token; our client reconnects (and re-identifies) on the next call.
            var back = Stopwatch.StartNew();
            while (back.Elapsed < TimeSpan.FromSeconds(60))
            {
                try { await bridges.QueryAsync(g, "hello", ct); result.BackAfterSec = Math.Round(back.Elapsed.TotalSeconds, 1); break; }
                catch (McpException) { await Task.Delay(1000, ct); }
            }
            result.Note = result.BackAfterSec != null
                ? "The HUD is back and the bridge answers. Plugins compiled at startup: check hud_plugins for the one you changed."
                : "The HUD restarted but its bridge hasn't answered within 60 s: check hud_log and bridge_status.";
        }
        else if (code == 7)
        {
            var back = Stopwatch.StartNew();
            await Task.Delay(5000, ct);
            while (back.Elapsed < TimeSpan.FromSeconds(90))
            {
                try { await bridges.QueryAsync(g, "hello", ct); result.BackAfterSec = Math.Round(back.Elapsed.TotalSeconds, 1); break; }
                catch (McpException) { await Task.Delay(1000, ct); }
            }
            result.Note = result.BackAfterSec != null ? "Another agent restarted the HUD; it is back." : "Another agent was restarting the HUD; it isn't back yet (bridge_status).";
        }
        else result.Note = status switch
        {
            "denied" => "The user pressed Not now on the in-game card. Say what you need the restart for in chat, or retry later.",
            "blocked" => "Still blocked when the wait ran out (see output for whose work). Retry later with a longer waitSec, or ask the user.",
            "cancelled" => "The UAC prompt was declined (no prompt-free task installed: tools\\install-hud-restart-tasks.ps1).",
            _ => "See output; <HUD>\\restart-hud.log has the elevated half's lines.",
        };
        // Close the board's restart incident: back, or not restarted at all. A HUD that doesn't come back keeps it open, and
        // the incident monitor re-diagnoses it (HUD not running, bridge not listening...).
        try
        {
            if (code == 0 && !stopOnly && result.BackAfterSec != null)
                Incidents.IncidentStore.ResolveKeys(Incidents.IncidentAuto.BridgeKeys(g), $"the {g} HUD is back, bridge answered {result.BackAfterSec} s after the restart");
            // Not restarted at all (Loader.exe missing, UAC declined, Not now, still blocked): the HUD was never stopped.
            // A restart that failed after stopping (3, 4) leaves the HUD possibly down: its incident stays open.
            else if (code is 1 or 2 or 5 or 6 && incident != null)
                Incidents.IncidentStore.Resolve(incident.Id, $"restart {status}: {result.Note}");
            var key = stopOnly ? $"bridge-down:{g}" : $"hud-restart:{g}";
            if (Incidents.IncidentLogic.Find(Incidents.IncidentStore.Read(), key) is { } inc && inc.RaisedAt >= callStart.AddSeconds(-1))
                result.Incident = IncidentTools.View(inc, Incidents.IncidentStore.ReadHeartbeats());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { output.Add($"incident board not updated: {ex.Message}"); }
        var text = $"{g}: {status} (exit {code}, {sw.Elapsed.TotalSeconds:0} s)" + (result.BackAfterSec != null ? $", bridge back after {result.BackAfterSec} s" : "") + "\n" + string.Join("\n", output);
        var call = Dto.Result(result, text);
        if (code != 0) call.IsError = true;
        return call;
    }
}
