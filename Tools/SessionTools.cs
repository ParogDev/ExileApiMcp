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
                 "and pending HUD restart requests with what blocks them. Check it before a HUD restart or a long measurement " +
                 "when you share the HUD; 'you' is your own session id.")]
    public static async Task<CallToolResult> HudSessions(BridgeRegistry bridges,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (_, r) = await bridges.CallAsync(game, "session.list", new JObject(), ct);
        if (r is not JObject o || o["sessions"] == null)
            throw new McpException("This HUD's bridge has no sessions yet: update What's an AI Bridge and restart the HUD.");
        var text = SessionsText(o);
        return Dto.Result(Dto.From<SessionsView>(o), text);
    }

    private static string SessionsText(JObject o)
    {
        var lines = new List<string>();
        foreach (var s in o["sessions"] as JArray ?? [])
        {
            var doing = string.Join("; ", (s["doing"] as JArray ?? []).Select(d => d.ToString()));
            lines.Add($"{(s["connected"]?.Value<bool>() == true ? "*" : "-")} {s["label"]}{(s["id"]?.ToString() == o["you"]?.ToString() ? " (you)" : "")}" +
                      $"{(s["branch"] != null && s["branch"]?.ToString() != s["label"]?.ToString() ? $" [{s["branch"]}]" : "")}: {(doing.Length > 0 ? doing : s["connected"]?.Value<bool>() == true ? "idle" : "disconnected")}");
        }
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
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Game", g, "-Who", SessionIdentity.Label, "-Reason", reason, "-WaitSec", waitSec.ToString() })
            psi.ArgumentList.Add(arg);
        if (stopOnly) psi.ArgumentList.Add("-StopOnly");
        if (force) psi.ArgumentList.Add("-Force");

        var output = new List<string>();
        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi) ?? throw new McpException("Could not start powershell.exe.");
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
        var text = $"{g}: {status} (exit {code}, {sw.Elapsed.TotalSeconds:0} s)" + (result.BackAfterSec != null ? $", bridge back after {result.BackAfterSec} s" : "") + "\n" + string.Join("\n", output);
        var call = Dto.Result(result, text);
        if (code != 0) call.IsError = true;
        return call;
    }
}
