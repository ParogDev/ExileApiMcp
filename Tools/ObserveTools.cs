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
                 "level, entity (new metadata path prefix), server (a changed byte range of server-sent state: off, name or null, old, new), server.noisy. Every event has at (UTC) and t/frame (one clock per HUD run). Pass the returned seq as since next time; observe_timeline lines the layers up.")]
    public static async Task<CallToolResult> ObserveEvents(BridgeRegistry bridges,
        [Description("Only events after this sequence number")] long since = 0,
        [Description("Only these kinds: ui | area | level | entity | server | server.noisy")] string[]? kinds = null,
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
        [Description("Wake for these kinds (default: ui-new, area, level). ui-new = an unmapped panel opening for the first time; ui = every panel change; area; level; entity")] string[]? kinds = null,
        [Description("Wake once at least this many noteworthy events are waiting (default 1)")] int minEvents = 1,
        [Description("Max wait, seconds (5-3600, default 1800)")] int timeoutSec = 1800,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSec, 5, 3600));
        var wide = kinds is { Length: > 0 } ? kinds.ToHashSet() : null;
        bool Noteworthy(JObject e) => wide != null
            ? wide.Contains(e["kind"]!.ToString()) || (wide.Contains("ui-new") && e["kind"]!.ToString() == "ui" && e["firstSeen"]?.Value<bool>() == true)
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

    [McpServerTool(Name = "observe_server_map", Title = "Which server-sent bytes changed, mapped or not", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("The observer's server layer (on while observe is on): every offset of the client's copy of server-sent state " +
                 "(ServerData by default) that changed since observing started, with the HUD's name for it (null = unmapped), " +
                 "how many times and how often, first/last time and last bytes. The worklist for mapping: an unmapped offset " +
                 "that changes rarely is an event to name (observe_timeline offset=<it> shows what happens at the same moments); " +
                 "one that changes constantly is a live value (positions, timers).")]
    public static async Task<CallToolResult> ObserveServerMap(BridgeRegistry bridges,
        [Description("Only unmapped offsets (no HUD name)")] bool unmappedOnly = false,
        [Description("Ignore offsets that changed fewer times")] long minChanges = 1,
        [Description("changes (most first) | recent | offset")] string sort = "changes",
        [Description("Max offsets (1-1000, default 60)")] int limit = 60,
        [Description("Watch target label or path (default all)")] string? target = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var p = new JObject { ["unmappedOnly"] = unmappedOnly, ["minChanges"] = minChanges, ["sort"] = sort, ["limit"] = Math.Clamp(limit, 1, 1000) };
        if (target != null) p["target"] = target;
        var (_, r) = await bridges.CallAsync(game, "observe.server_map", p, ct);
        if (r["targets"] == null && r["error"] == null)
            throw new McpException("This HUD's bridge has no server layer yet: update What's an AI Bridge and restart the HUD.");
        var sb = new StringBuilder();
        foreach (var t in r["targets"] as JArray ?? [])
        {
            sb.AppendLine($"{t["target"]}: {t["bytes"]} bytes watched, {t["offsetsChanged"]} offsets changed");
            foreach (var o in t["offsets"] as JArray ?? [])
                sb.AppendLine($"  {o["off"],-8} {(o["name"]?.Type is null or JTokenType.Null ? "(unmapped)" : o["name"]!.ToString()),-28} x{o["changes"]} ({o["perMinute"]}/min) last {o["last"]}{(o["logged"]?.Value<bool>() == false ? "  [noisy block: counted, not in the journal]" : "")}");
        }
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.Length == 0 ? "Nothing changed yet (is observe on?)." : sb.ToString().TrimEnd() }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(r.ToString(Newtonsoft.Json.Formatting.None)),
        };
    }

    [McpServerTool(Name = "observe_timeline", Title = "What happened at the same moment, across layers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Cross-reference the observer's layers (server = server-sent state changing, ui = panels, area, level, entity) " +
                 "on one time axis, from the journal on disk (all sessions, not just the 1000 in memory). Two modes: " +
                 "around=<seq> lists every event within windowMs of that event, ordered, with the offset from it; " +
                 "offset=<0x...> takes each logged change of that server offset and counts which other events happened within " +
                 "windowMs of it - consistent companions (a panel opening, an area change, another offset) are what that offset " +
                 "means. Read-only.")]
    public static Task<CallToolResult> ObserveTimeline(BridgeRegistry bridges,
        [Description("Sequence number of the event to centre on (from observe_events / observe_server_map / a previous timeline)")] long? around = null,
        [Description("Server offset to cross-reference, e.g. 0x2250 (with target if several are watched)")] string? offset = null,
        [Description("Half-width of the window in ms (10-60000, default 1000)")] int windowMs = 1000,
        [Description("Only these kinds in the output, e.g. ui, area, server")] string[]? kinds = null,
        [Description("Most recent journal lines to read (1000-500000, default 100000)")] int scan = 100_000,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null)
    {
        var bridge = bridges.Resolve(game);
        var path = Path.Combine(bridge.BridgeDir, "observe", "journal.jsonl");
        if (!File.Exists(path)) throw new McpException($"No observer journal at {Path.Combine("<bridge dir>", "observe", "journal.jsonl")}: turn observation on (observe action=start).");
        windowMs = Math.Clamp(windowMs, 10, 60_000);
        var events = ReadJournalTail(path, Math.Clamp(scan, 1000, 500_000));
        var keep = kinds is { Length: > 0 } ? kinds.ToHashSet() : null;
        var win = TimeSpan.FromMilliseconds(windowMs);
        var sb = new StringBuilder();
        var o = new JObject { ["journalEvents"] = events.Count, ["windowMs"] = windowMs };

        if (offset != null)
        {
            var off = offset.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? offset : "0x" + offset;
            var hits = events.Where(e => e.kind == "server" && string.Equals(e.json["off"]?.ToString(), off, StringComparison.OrdinalIgnoreCase)).ToList();
            o["offset"] = off; o["changes"] = hits.Count;
            if (hits.Count == 0)
            {
                sb.Append($"No logged change of {off} in the last {events.Count} journal events (a noisy block is counted, not logged: see observe_server_map).");
                return Task.FromResult(Done(sb, o));
            }
            // For each change, the set of distinct companions in the window (counted once per change).
            var tally = new Dictionary<string, (int n, double sumDtMs)>();
            foreach (var h in hits)
            {
                var seen = new HashSet<string>();
                foreach (var e in Near(events, h.at, win))
                {
                    if (e.seq == h.seq || (keep != null && !keep.Contains(e.kind))) continue;
                    var key = Companion(e);
                    if (key == $"server {off}" || !seen.Add(key)) continue;
                    var dt = (e.at - h.at).TotalMilliseconds;
                    tally[key] = tally.TryGetValue(key, out var t) ? (t.n + 1, t.sumDtMs + dt) : (1, dt);
                }
            }
            sb.AppendLine($"{off} ({(hits[0].json["name"]?.Type is null or JTokenType.Null ? "unmapped" : hits[0].json["name"]!.ToString())}) changed {hits.Count} times in the journal; within {windowMs} ms of those changes:");
            var rows = tally.OrderByDescending(kv => kv.Value.n).Take(40).ToList();
            foreach (var (key, (n, sum)) in rows)
                sb.AppendLine($"  {n}/{hits.Count}  {key}  (avg {Math.Round(sum / n):+0;-0;0} ms)");
            if (rows.Count == 0) sb.AppendLine("  nothing else: it changes on its own (server-pushed, or a value the client updates by itself).");
            sb.Append("Changes: " + string.Join(", ", hits.TakeLast(8).Select(h => $"#{h.seq} {h.json["old"]}->{h.json["new"]}")));
            o["companions"] = new JArray(rows.Select(kv => new JObject { ["event"] = kv.Key, ["count"] = kv.Value.n, ["avgDtMs"] = Math.Round(kv.Value.sumDtMs / kv.Value.n, 1) }));
            o["recentChanges"] = new JArray(hits.TakeLast(20).Select(h => h.json));
            return Task.FromResult(Done(sb, o));
        }

        var centre = around is { } s ? events.FirstOrDefault(e => e.seq == s) : events.LastOrDefault();
        if (centre.json == null) throw new McpException(around is { } a ? $"Event #{a} is not in the last {events.Count} journal events." : "The journal is empty.");
        var list = Near(events, centre.at, win).Where(e => keep == null || keep.Contains(e.kind) || e.seq == centre.seq).ToList();
        sb.AppendLine($"{list.Count} events within {windowMs} ms of #{centre.seq} ({centre.at:HH:mm:ss.fff} UTC):");
        foreach (var e in list.Take(200))
            sb.AppendLine($"  {(e.at - centre.at).TotalMilliseconds,7:+0;-0;0} ms  #{e.seq,-7} {Line(e)}{(e.seq == centre.seq ? "   <==" : "")}");
        if (list.Count > 200) sb.AppendLine($"  ... {list.Count - 200} more (narrow windowMs or kinds)");
        o["centre"] = centre.seq;
        o["events"] = new JArray(list.Take(500).Select(e => e.json));
        return Task.FromResult(Done(sb, o));
    }

    private readonly record struct JEvent(long seq, DateTime at, string kind, JObject json);

    /// <summary>The last n lines of the journal, parsed (bad lines skipped), in order.</summary>
    private static List<JEvent> ReadJournalTail(string path, int n)
    {
        var q = new Queue<string>(n);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var rd = new StreamReader(fs))
            for (string? line; (line = rd.ReadLine()) != null;) { if (q.Count == n) q.Dequeue(); q.Enqueue(line); }
        var list = new List<JEvent>(q.Count);
        foreach (var line in q)
        {
            try
            {
                var j = JObject.Parse(line);
                if (j["at"]?.Type != JTokenType.Date && !DateTime.TryParse(j["at"]?.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out _)) continue;
                var at = j["at"]!.Type == JTokenType.Date ? j["at"]!.Value<DateTime>().ToUniversalTime()
                    : DateTime.Parse(j["at"]!.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                list.Add(new JEvent(j["seq"]?.Value<long>() ?? 0, at, j["kind"]?.ToString() ?? "?", j));
            }
            catch { }
        }
        return list;
    }

    /// <summary>Events within win of at. The journal is in time order, so binary-search the start.</summary>
    private static IEnumerable<JEvent> Near(List<JEvent> events, DateTime at, TimeSpan win)
    {
        int lo = 0, hi = events.Count;
        var from = at - win;
        while (lo < hi) { var mid = (lo + hi) / 2; if (events[mid].at < from) lo = mid + 1; else hi = mid; }
        for (var i = lo; i < events.Count && events[i].at <= at + win; i++) yield return events[i];
    }

    /// <summary>What an event is, without its values: the key companions are counted by.</summary>
    private static string Companion(JEvent e) => e.kind switch
    {
        "server" => $"server {e.json["off"]}" + (e.json["name"]?.Type is null or JTokenType.Null ? "" : $" {e.json["name"]}"),
        "ui" => $"ui [{e.json["index"]}] {(e.json["visible"]?.Value<bool>() == true ? "opened" : "closed")} {e.json["mapped"]?.ToString() ?? "unmapped"}",
        "area" => "area change",
        "level" => "level up",
        _ => e.kind,
    };

    private static string Line(JEvent e) => e.kind switch
    {
        "server" => $"server {e.json["off"]} {(e.json["name"]?.Type is null or JTokenType.Null ? "(unmapped)" : e.json["name"]!.ToString())} {e.json["old"]} -> {e.json["new"]}" +
                    (e.json["i32"] != null ? $"  i32 {e.json["i32"]}" : e.json["i64"] != null ? $"  i64 {e.json["i64"]}" : ""),
        "server.noisy" => $"server {e.json["off"]}+256 went noisy",
        "ui" => $"ui [{e.json["index"]}] {(e.json["visible"]?.Value<bool>() == true ? "opened" : "closed")} {e.json["mapped"]?.ToString() ?? "UNMAPPED"}",
        "area" => $"area {e.json["from"]} -> {e.json["to"]}",
        "level" => $"level {e.json["from"]} -> {e.json["to"]}",
        "entity" => $"entity {e.json["type"]}",
        _ => e.kind,
    };

    private static CallToolResult Done(StringBuilder sb, JObject o) => new()
    {
        Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
        StructuredContent = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(o.ToString(Newtonsoft.Json.Formatting.None)),
    };

    private static CallToolResult Summarise(JToken r)
    {
        var sb = new StringBuilder();
        var events = (r["events"] as JArray ?? []).OfType<JObject>().ToList();
        // New entity kinds come in bursts (a town lists dozens of NPC variants): one line per area, not one per kind.
        foreach (var g in events.Where(e => e["kind"]?.ToString() == "entity").GroupBy(e => e["area"]?.ToString() ?? "?"))
            sb.AppendLine($"#{g.First()["seq"]}..#{g.Last()["seq"]} {g.Count()} new entity kind(s) in {g.Key}: " +
                          string.Join(", ", g.Take(8).Select(e => e["type"]!.ToString().Replace("Metadata/", ""))) + (g.Count() > 8 ? ", ..." : ""));
        foreach (var e in events.Where(e => e["kind"]?.ToString() != "entity"))
        {
            var kind = e["kind"]?.ToString();
            var mapped = e["mapped"]?.Type is null or JTokenType.Null ? "UNMAPPED" : e["mapped"]!.ToString();
            sb.AppendLine(kind switch
            {
                "ui" => $"#{e["seq"]} ui [{e["index"]}] {(e["visible"]?.Value<bool>() == true ? "opened" : "closed")} {mapped}" +
                        (e["firstSeen"]?.Value<bool>() == true ? " (first time)" : "") +
                        (e["texts"] is JArray t && t.Count > 0 ? $" texts: {string.Join(" | ", t.Take(4))}" : ""),
                "area" => $"#{e["seq"]} area {e["from"]} -> {e["to"]}",
                "level" => $"#{e["seq"]} level {e["from"]} -> {e["to"]} in {e["area"]}",
                "entity" => $"#{e["seq"]} entity {e["type"]} ({e["entityType"]})",
                "server" => $"#{e["seq"]} server {e["off"]} {(e["name"]?.Type is null or JTokenType.Null ? "(unmapped)" : e["name"]!.ToString())} {e["old"]} -> {e["new"]}",
                "server.noisy" => $"#{e["seq"]} server block {e["off"]}+256 is noisy: counted in observe_server_map, not logged",
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
