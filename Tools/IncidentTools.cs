using System.ComponentModel;
using System.Diagnostics;
using ExileApiMcp.Hosting;
using ExileApiMcp.Incidents;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// The incident board (%LOCALAPPDATA%\Hexile\incidents.json, Incidents/IncidentStore.cs): machine-wide problems every
/// session should hear about at once (a bridge down and why, a HUD restart with its ETA, Windows reserving the bridge
/// ports, a deploy left in the main checkout), and each session's answer: ok, blocked or waiting, until when. Works
/// with the HUD and the bridge down. Shared with tools\incident.ps1 and the Claude Code hook (tools\incident-hook.*).
/// </summary>
[McpServerToolType]
public static class IncidentTools
{
    private const string IdDesc = "The incident's id (i-...) or its key (e.g. bridge-down:poe2, deploy:Whats A Route)";

    [McpServerTool(Name = "incidents", Title = "Incident board: what is broken for every session", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(IncidentsResult), IconSource = IconSet.ServerLight)]
    [Description("The machine-wide incident board shared by every session: open incidents (a HUD bridge down and the broken link, " +
                 "a HUD restart and its ETA, Windows reserving the bridge ports, a deploy left in the main checkout, anything an " +
                 "agent raised) and the ones resolved in the last 24 h, each with every session's response (ok | blocked | " +
                 "waiting, until when). Works with the HUD down. since=<rev> returns only what changed after that rev. When an " +
                 "incident affects your work, say so with incident_respond; wait for changes with incident_wait.")]
    public static CallToolResult Incidents(
        [Description("Only incidents changed after this board rev (from an earlier result)")] int? since = null,
        [Description("poe1 | poe2 | machine: only this scope (machine-wide incidents are always included)")] string? scope = null,
        [Description("Include incidents resolved in the last 24 h (default true)")] bool includeResolved = true)
    {
        DeployIncidents.Scan();
        var board = IncidentStore.Read();
        var list = board.Incidents
            .Where(i => i.IsOpen || (includeResolved && (i.ResolvedAt ?? i.RaisedAt ?? DateTime.MinValue) > DateTime.UtcNow - IncidentLogic.ShowResolved))
            .Where(i => scope == null || i.Scope == scope || i.Scope == "machine")
            .Where(i => since == null || (i.ChangedRev ?? int.MaxValue) > since)
            .OrderByDescending(i => i.IsOpen).ThenByDescending(i => i.RaisedAt).ToList();
        var result = new IncidentsResult
        {
            Rev = board.Rev, Since = since, Open = board.Incidents.Count(i => i.IsOpen), You = SessionIdentity.Label, File = IncidentStore.FilePath,
            Incidents = Views(list),
        };
        var text = $"Incident board rev {board.Rev}: {result.Open} open" + (since != null ? $", {list.Count} changed since rev {since}" : "") + "\n" + Lines(list);
        return Dto.Result(result, text);
    }

    [McpServerTool(Name = "incident_raise", Title = "Raise an incident every session hears about", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(IncidentActionResult), IconSource = IconSet.ServerLight)]
    [Description("Put a machine-wide problem on the incident board, so every session hears about it at once and plans around it: " +
                 "something that blocks other agents too (the HUD, the bridge, the MCP build, the main checkout, the machine). " +
                 "Name the broken link in detail, and say in fix what must happen and who must act. An open incident with the " +
                 "same key is updated, never duplicated (default key: kind:scope; custom: custom:<title in lower case>). Bridge outages, " +
                 "HUD restarts, port reservations and deploys are raised automatically: don't raise those by hand.")]
    public static CallToolResult IncidentRaise(
        [Description("bridge-down | hud-restart | deploy | mcp-down | ports-reserved | custom")] string kind,
        [Description("poe1 | poe2 | machine")] string scope,
        [Description("One line: what is wrong")] string title,
        [Description("What broke, named at the broken link (the file, port, process, member...)")] string? detail = null,
        [Description("What to do and who must act (the user, the owner, any session); omit if nothing")] string? fix = null,
        [Description("Minutes until it should be over (sets eta); omit when unknown")] double? etaMinutes = null,
        [Description("Dedupe key, when the default (kind:scope) would merge unrelated problems")] string? key = null)
    {
        var draft = new IncidentDraft
        {
            Kind = kind.Trim().ToLowerInvariant(), Scope = scope.Trim().ToLowerInvariant(), Title = title, Detail = detail, Fix = fix, Key = key,
            Eta = etaMinutes is { } m && m > 0 ? DateTime.UtcNow.AddMinutes(Math.Min(m, 7 * 24 * 60)) : null,
        };
        try
        {
            var (i, created) = IncidentStore.Raise(draft);
            return Action(created ? "raised" : "updated", i, created ? "Every session sees it now (incidents, incident_wait, the hook)." : "An open incident with this key existed: updated it.");
        }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "incident_respond", Title = "Say how an incident affects you", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(IncidentActionResult), IconSource = IconSet.ServerLight)]
    [Description("Answer an incident for your session, so the user and the other agents see who is affected: ok (it doesn't touch " +
                 "your work, or you planned around it), blocked (you can't continue until it's over) or waiting (you paused and " +
                 "will resume). minutes says for how long (until). One response per session: a new one replaces yours.")]
    public static CallToolResult IncidentRespond(
        [Description("ok | blocked | waiting")] string status,
        [Description(IdDesc)] string? id = null,
        [Description("Same as id, for clients that pass a key")] string? key = null,
        [Description("What you do meanwhile, or what you need, in a few words")] string? note = null,
        [Description("How long this holds, minutes (sets until)")] double? minutes = null,
        [Description("Your conversation's id when this server can't tell conversations apart (mcp_status this.identity.clientSession " +
                     "is null, e.g. the Claude desktop app's shared server): your Claude Code session id (CLAUDE_CODE_SESSION_ID in " +
                     "your shell) if you have one, else any short id you keep for the whole conversation. Ignored when the server " +
                     "knows your session.")] string? sessionTag = null)
    {
        var target = Target(id, key);
        try
        {
            // Without a client session id, every conversation of a shared server answers as one: the tag keys this one.
            var tag = SessionIdentity.ClientSession == null && !string.IsNullOrWhiteSpace(sessionTag) ? sessionTag.Trim() : null;
            if (tag is { Length: > 100 }) tag = tag[..100];
            var i = IncidentStore.Respond(target, status.Trim().ToLowerInvariant(), note,
                minutes is { } m && m > 0 ? DateTime.UtcNow.AddMinutes(Math.Min(m, 7 * 24 * 60)) : null,
                who: tag != null ? SessionIdentity.Distinct(SessionIdentity.Label, tag) : null, sessionId: tag);
            return Action("responded", i, null);
        }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "incident_resolve", Title = "Resolve an incident", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(IncidentActionResult), IconSource = IconSet.ServerLight)]
    [Description("Mark an incident resolved (listed for 24 h, kept 7 days). Resolve what you raised once it's over, or anything you " +
                 "verified is fixed; say how in note. Automatic incidents (bridge, restart, ports, deploys) resolve themselves " +
                 "when the bridge answers, the ports are free or the deploy is restored.")]
    public static CallToolResult IncidentResolve(
        [Description(IdDesc)] string? id = null,
        [Description("Same as id, for clients that pass a key")] string? key = null,
        [Description("How it was resolved / what you verified")] string? note = null)
    {
        var target = Target(id, key);
        try
        {
            var before = IncidentLogic.Find(IncidentStore.Read(), target);
            var i = IncidentStore.Resolve(target, note, throwIfMissing: true)!;
            return Action(before?.IsOpen == false ? "unchanged" : "resolved", i, before?.IsOpen == false ? $"It was resolved already by {before.ResolvedBy}." : null);
        }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "incident_wait", Title = "Wait for the incident board to change", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(IncidentWaitResult), IconSource = IconSet.ServerLight)]
    [Description("Block until the incident board's rev differs from since, then return what changed: push, not poll (a file watcher " +
                 "on the board). Returns at once when it already differs. Run it in the background through tools\\mcp-call.ps1 " +
                 "(-TimeoutSec above timeoutSec) to resume as soon as an outage is over, e.g. a HUD restart's incident resolves.")]
    public static async Task<CallToolResult> IncidentWait(IncidentWatcher watcher, IProgress<ProgressNotificationValue> progress,
        [Description("The board rev you last saw (incidents / an earlier incident_wait)")] int since,
        [Description("Longest wait, seconds (1-3600, default 300; a progress notification every 15 s while it waits)")] int timeoutSec = 300,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, 3600));
        // Waited in 15 s slices, each ending with a progress notification: the client sees the wait is alive (and what is open).
        var changed = false;
        while (!changed && sw.Elapsed < limit)
        {
            var slice = limit - sw.Elapsed < TimeSpan.FromSeconds(15) ? limit - sw.Elapsed : TimeSpan.FromSeconds(15);
            changed = await watcher.WaitAsync(since, slice, ct);
            if (!changed && sw.Elapsed < limit)
                progress?.Report(new ProgressNotificationValue
                {
                    Progress = (float)sw.Elapsed.TotalSeconds, Total = (float)limit.TotalSeconds,
                    Message = $"Board unchanged at rev {since}, {(int)sw.Elapsed.TotalSeconds} of {(int)limit.TotalSeconds} s: " +
                              (IncidentStore.Read().Incidents.Where(i => i.IsOpen).Select(i => $"[{i.Kind}] {i.Title}").FirstOrDefault() ?? "nothing open"),
                });
        }
        var board = IncidentStore.Read();
        var list = changed ? board.Incidents.Where(i => (i.ChangedRev ?? int.MaxValue) > since).OrderByDescending(i => i.IsOpen).ToList() : [];
        var result = new IncidentWaitResult
        {
            Changed = changed, Since = since, Rev = board.Rev, WaitedSec = Math.Round(sw.Elapsed.TotalSeconds, 1), Incidents = Views(list),
            Note = changed ? (board.Rev < since ? "The board's rev went back (the file was recreated): read it whole with incidents." : null)
                : $"No change within {(int)limit.TotalSeconds} s; call again with since={board.Rev}.",
        };
        var text = changed
            ? $"Board changed: rev {since} -> {board.Rev} after {result.WaitedSec} s\n{Lines(list)}"
            : $"No change in {result.WaitedSec} s (rev {board.Rev}).";
        return Dto.Result(result, text);
    }

    // ── Helpers ──

    private static string Target(string? id, string? key) =>
        (string.IsNullOrWhiteSpace(id) ? key : id) is { Length: > 0 } t ? t.Trim() : throw new McpException("Pass id (or key) of the incident: see incidents.");

    private static CallToolResult Action(string action, Incident i, string? note)
    {
        var board = IncidentStore.Read();
        var r = new IncidentActionResult { Ok = true, Action = action, Rev = board.Rev, Incident = View(i, IncidentStore.ReadHeartbeats()), Note = note };
        return Dto.Result(r, $"{action}: {IncidentLogic.OneLine(i, DateTime.UtcNow)}" + (note != null ? "\n" + note : ""));
    }

    /// <summary>A stored incident as its tool contract, with the deploy annotations (heartbeat, orphaned).</summary>
    public static IncidentView View(Incident i, Dictionary<string, DateTime> beats)
    {
        var o = JsonConvert.DeserializeObject<JObject>(IncidentJson.SerializeCompact(i), IncidentJson.Settings)!;
        DeployIncidents.Annotate(o, i, beats);
        if (o["responses"] is JArray rs)
        {
            var names = IncidentLogic.ResponseNames(i);
            for (var k = 0; k < rs.Count && k < names.Count; k++) if (rs[k] is JObject r) r["display"] = names[k];
        }
        return TypedReply.Parse<IncidentView>(o);
    }

    public static List<IncidentView> Views(IEnumerable<Incident> list)
    {
        var beats = IncidentStore.ReadHeartbeats();
        return list.Select(i => View(i, beats)).ToList();
    }

    /// <summary>One line per incident plus one per response, for tool text.</summary>
    public static string Lines(IEnumerable<Incident> list)
    {
        var now = DateTime.UtcNow;
        var beats = IncidentStore.ReadHeartbeats();
        var lines = new List<string>();
        foreach (var i in list)
        {
            var orphan = i.Kind == "deploy" && i.IsOpen && DeployIncidents.Judge(i, beats.TryGetValue(i.Id, out var b) ? b : null, DeployIncidents.PidAlive, now, DeployIncidents.SameOwnerServer) is { Orphaned: true } j
                ? $" ORPHANED: {j.Why}." : "";
            lines.Add(i.IsOpen
                ? $"- OPEN {IncidentLogic.OneLine(i, now)}{orphan}"
                : $"- resolved {IncidentLogic.Ago(i.ResolvedAt, now)} by {i.ResolvedBy}: [{i.Kind}] {i.Title}" + (i.Get("resolution") is { } n ? $" ({n})" : ""));
            if (i.IsOpen && !string.IsNullOrWhiteSpace(i.Detail)) lines.Add($"    {i.Detail}");
            var names = IncidentLogic.ResponseNames(i);   // "Label (2)" when two sessions share a label
            foreach (var (r, k) in i.Responses.Select((r, k) => (r, k)).Where(_ => i.IsOpen))
                lines.Add($"    {names[k]}: {r.Status}{(r.Until is { } u ? $" until {u:HH:mm}Z" : "")}{(r.Note != null ? $" - {r.Note}" : "")}");
            if (i.IsOpen && i.Kind == "outage" && OutageLogic.Info(i) is { } o)
                lines.Add($"    outage phase {o.Phase}" + (o.Answer != null ? $", card answer {o.Answer}" : "") +
                          (o.Participants.Count > 0 ? ": " + string.Join(", ", o.Participants.Values.Select(p => $"{p.Display ?? p.Label} {p.State}{(p.Doing != null ? $" ({p.Doing})" : "")}")) : ""));
        }
        return lines.Count == 0 ? "(nothing)" : string.Join("\n", lines);
    }
}

/// <summary>The board as a subscribable resource: subscriptions/listen on exile://incidents pushes resources/updated on every change.</summary>
[McpServerResourceType]
public static class IncidentResources
{
    [McpServerResource(UriTemplate = IncidentHub.Uri, Name = "incidents", Title = "Incident board", MimeType = "application/json", IconSource = IconSet.ServerLight)]
    [Description("The machine-wide incident board (IncidentsResult: open incidents and those resolved in the last 24 h, with every session's response). Subscribable: updated on every change.")]
    public static string Board()
    {
        var board = IncidentStore.Read();
        var result = new IncidentsResult
        {
            Rev = board.Rev, Open = board.Incidents.Count(i => i.IsOpen), You = SessionIdentity.Label, File = IncidentStore.FilePath,
            Incidents = IncidentTools.Views(board.Incidents.Where(i => i.IsOpen || (i.ResolvedAt ?? DateTime.MinValue) > DateTime.UtcNow - IncidentLogic.ShowResolved).OrderByDescending(i => i.IsOpen).ThenByDescending(i => i.RaisedAt)),
        };
        return System.Text.Json.JsonSerializer.Serialize(result, Dto.Options);
    }
}
