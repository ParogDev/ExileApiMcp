using System.ComponentModel;
using System.Text;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Passive learning (bridge observe.*): while the user plays, the HUD notes UI panels opening/closing (and whether the HUD
/// maps them), area and level changes and new entity kinds - read-only, never input. Agents wake on events with
/// observe_wait instead of polling, map what is new, and keep findings/knowledge up to date. Method: knowledge pack
/// shared/passive-learning.
/// </summary>
[McpServerToolType]
public static class ObserveTools
{
    [McpServerTool(Name = "observe", Title = "Passive observation on/off", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Turn the HUD's passive observation on or off, or read its status (action=status). While on, the bridge notes " +
                 "top-level UI panels opening/closing (with the HUD property mapping them, or none: those are the mapping " +
                 "targets, saved with a byte snapshot and their first texts), area and level changes, and new entity kinds. " +
                 "Read-only, never input; the state survives HUD restarts. Tell the user before turning it on.")]
    public static async Task<CallToolResult> Observe(BridgeRegistry bridges,
        [Description("start | stop | status")] string action = "status",
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var method = action switch { "start" => "observe.start", "stop" => "observe.stop", _ => "observe.status" };
        var (_, r) = await bridges.CallAsync(game, method, new JObject(), ct);
        Need(r);
        return ToolResults.Json(r);
    }

    [McpServerTool(Name = "observe_events", Title = "What happened while the user played", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Events noted by passive observation after sequence number since (0 = all in memory, up to 1000): ui (index, " +
                 "address, visible, mapped property or null, texts and base64 snapshot for unmapped panels, firstSeen), area, " +
                 "level, entity (new metadata path prefix). Pass the returned seq as since next time.")]
    public static async Task<CallToolResult> ObserveEvents(BridgeRegistry bridges,
        [Description("Only events after this sequence number")] long since = 0,
        [Description("Only these kinds: ui | area | level | entity")] string[]? kinds = null,
        [Description("Max events (1-500, default 100)")] int limit = 100,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var p = new JObject { ["since"] = since, ["limit"] = limit };
        if (kinds is { Length: > 0 }) p["kinds"] = new JArray(kinds);
        var (_, r) = await bridges.CallAsync(game, "observe.events", p, ct);
        Need(r);
        return Summarise(r);
    }

    [McpServerTool(Name = "observe_wait", Title = "Wake when something new happens in game", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Block until passive observation has noteworthy events after since, then return them. Noteworthy by default: a " +
                 "panel the HUD does not map opening for the first time, an area change, a level up (set kinds to widen). Run it " +
                 "in the background (Claude Code: tools\\mcp-call.ps1 observe_wait since=<seq> -TimeoutSec 3700 as a background " +
                 "task) so you wake when there is something to learn, instead of polling. Returns waiting:true on timeout.")]
    public static async Task<CallToolResult> ObserveWait(BridgeRegistry bridges,
        [Description("Sequence number already handled (from the last observe_* result)")] long since = 0,
        [Description("Wake for these kinds (default: first-seen unmapped panels, area, level). 'ui' = every panel change")] string[]? kinds = null,
        [Description("Wake once at least this many noteworthy events are waiting (default 1)")] int minEvents = 1,
        [Description("Max wait, seconds (5-3600, default 1800)")] int timeoutSec = 1800,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSec, 5, 3600));
        var wide = kinds is { Length: > 0 } ? kinds.ToHashSet() : null;
        bool Noteworthy(JObject e) => wide != null
            ? wide.Contains(e["kind"]!.ToString())
            : e["kind"]!.ToString() switch { "area" or "level" => true, "ui" => e["firstSeen"]?.Value<bool>() == true, _ => false };
        while (true)
        {
            JToken r;
            try { (_, r) = await bridges.CallAsync(game, "observe.events", new JObject { ["since"] = since, ["limit"] = 500 }, ct); }
            catch (McpException) when (DateTime.UtcNow < until) { await Task.Delay(5000, ct); continue; }   // HUD restarting
            Need(r);
            if (r["enabled"]?.Value<bool>() != true)
                return ToolResults.Json(new JObject { ["enabled"] = false, ["note"] = "Observation is off (observe action=start)." });
            var events = (r["events"] as JArray ?? []).OfType<JObject>().ToList();
            if (events.Count(Noteworthy) >= Math.Max(1, minEvents)) return Summarise(r);
            if (DateTime.UtcNow >= until)
                return ToolResults.Json(new JObject { ["waiting"] = true, ["seq"] = r["seq"], ["pending"] = events.Count,
                    ["note"] = "Nothing noteworthy yet. Call observe_wait again (pass the same since to keep the pending events)." });
            await Task.Delay(3000, ct);
        }
    }

    private static CallToolResult Summarise(JToken r)
    {
        var sb = new StringBuilder();
        foreach (var e in (r["events"] as JArray ?? []).OfType<JObject>())
        {
            var kind = e["kind"]?.ToString();
            sb.AppendLine(kind switch
            {
                "ui" => $"#{e["seq"]} ui [{e["index"]}] {(e["visible"]?.Value<bool>() == true ? "opened" : "closed")} {e["mapped"]?.ToString() ?? "UNMAPPED"}" +
                        (e["firstSeen"]?.Value<bool>() == true ? " (first time)" : "") +
                        (e["texts"] is JArray t && t.Count > 0 ? $" texts: {string.Join(" | ", t.Take(4))}" : ""),
                "area" => $"#{e["seq"]} area {e["from"]} -> {e["to"]}",
                "level" => $"#{e["seq"]} level {e["from"]} -> {e["to"]} in {e["area"]}",
                "entity" => $"#{e["seq"]} entity {e["type"]} ({e["entityType"]})",
                _ => $"#{e["seq"]} {kind}",
            });
        }
        if (sb.Length == 0) sb.AppendLine("No events.");
        sb.Append($"seq={r["seq"]} (pass as since next time). Snapshots of unmapped panels are in structuredContent (base64, 512 bytes at the panel's address).");
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString() }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(r.ToString(Newtonsoft.Json.Formatting.None)),
        };
    }

    private static void Need(JToken? r)
    {
        if (r is not JObject o || (o["ok"] == null && o["error"] == null))
            throw new McpException("This HUD's bridge plugin has no passive observation yet: update What's an AI Bridge and restart the HUD.");
    }
}
