using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Passive learning and the cross-layer timeline (bridge observe.*): while the user plays, the HUD records layers
/// (runtime specs: server-sent state, stats, anything at a walker path), UI panels, area and level changes and new entity
/// kinds, read-only, on one clock. Results are typed contracts (ObserveDtos.cs) with output schemas; the same data is
/// served as subscribable resources (ObserveResources.cs) that push updates over subscriptions/listen.
/// Method: knowledge pack shared/passive-learning.
/// </summary>
[McpServerToolType]
public static partial class ObserveTools
{
    [McpServerTool(Name = "observe", Title = "Passive observation on/off", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, IconSource = IconSet.TimelineLight)]
    [Description("Turn the HUD's passive observation on or off, or read its status (action=status). While on, the bridge records " +
                 "its layers (observe_layers: server-sent state and stats by default), top-level UI panels opening/closing (with the " +
                 "HUD property mapping them, or none, at 10 Hz), area and level changes, new entity kinds, the HUD's own hiccups (hud: " +
                 "frame spikes with their GC share, plugin reloads) and what agents asked of the HUD or the user (agent: guide, " +
                 "highlight, experiment, reload, settings calls). Read-only, never input; the " +
                 "state survives HUD restarts. Tell the user before turning it on.")]
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

    [McpServerTool(Name = "observe_events", Title = "What happened while the user played", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ObserveEventsResult), IconSource = IconSet.TimelineLight)]
    [Description("Events recorded by passive observation after sequence number since (0 = all in memory, up to 1000), as typed " +
                 "ObserveEvent: layer (layer, mode, unit, name or null when unmapped, old, new, plus off/len/i32/i64 for struct " +
                 "and delta/change for props/dict/list/each), layer.noisy, ui, area, level, entity, hud (cause spike: intervalMs, gcMs, " +
                 "gen0-2; cause reload: plugin, ok, durationMs), agent (method, params). Each has seq, at (UTC), t and frame. " +
                 "Pass the returned seq as since next time. To be told when there are new ones instead of asking, subscribe to " +
                 "the resource exile://observe/{game}/events.")]
    public static async Task<CallToolResult> ObserveEvents(BridgeRegistry bridges,
        [Description("Only events after this sequence number")] long since = 0,
        [Description("Only these kinds: layer | layer.noisy | ui | area | level | entity | hud | agent")] string[]? kinds = null,
        [Description("Only events of these layers (e.g. server, stats)")] string[]? layers = null,
        [Description("Max events (1-500, default 100)")] int limit = 100,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (_, r) = await bridges.CallAsync(game, "observe.events", new JObject { ["since"] = since, ["limit"] = 500 }, ct);
        Need(r);
        var result = Dto.From<ObserveEventsResult>(r);
        result.Events = result.Events.Select(e => e.Normalized())
            .Where(e => (kinds is not { Length: > 0 } || kinds.Contains(e.Kind)) && (layers is not { Length: > 0 } || (e.Layer != null && layers.Contains(e.Layer))))
            .Take(Math.Clamp(limit, 1, 500)).ToList();
        return Typed(result, Summary(result));
    }

    [McpServerTool(Name = "observe_wait", Title = "Wake when something new happens in game", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ObserveEventsResult), IconSource = IconSet.TimelineLight)]
    [Description("Block until passive observation has noteworthy events after since, then return them. Noteworthy by default: a " +
                 "panel the HUD does not map opening for the first time, an area change, a level up (set kinds to widen, e.g. layer). " +
                 "Run it in the background (Claude Code: tools\\mcp-call.ps1 observe_wait since=<seq> -TimeoutSec 3700 as a background " +
                 "task). Clients with subscriptions/listen can subscribe to exile://observe/{game}/events instead. Returns no events " +
                 "on timeout (extra.waiting = true).")]
    public static async Task<CallToolResult> ObserveWait(BridgeRegistry bridges,
        [Description("Sequence number already handled (from the last observe_* result)")] long since = 0,
        [Description("Wake for these kinds (default: ui-new, area, level). ui-new = an unmapped panel opening for the first time; ui = every panel change; area; level; entity; layer; hud; agent")] string[]? kinds = null,
        [Description("Wake once at least this many noteworthy events are waiting (default 1)")] int minEvents = 1,
        [Description("Max wait, seconds (5-3600, default 1800)")] int timeoutSec = 1800,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSec, 5, 3600));
        var wide = kinds is { Length: > 0 } ? kinds.ToHashSet() : null;
        bool Noteworthy(ObserveEvent e) => wide != null
            ? wide.Contains(e.Kind) || (wide.Contains("ui-new") && e.Kind == "ui" && e.FirstSeen == true)
            : e.Kind switch { "area" or "level" => true, "ui" => e.FirstSeen == true, _ => false };
        while (true)
        {
            JToken r;
            try { (_, r) = await bridges.CallAsync(game, "observe.events", new JObject { ["since"] = since, ["limit"] = 500 }, ct); }
            catch (McpException) when (DateTime.UtcNow < until) { await Task.Delay(5000, ct); continue; }   // HUD restarting
            Need(r);
            var result = Dto.From<ObserveEventsResult>(r);
            if (!result.Enabled)
                return Typed(result, "Observation is off (observe action=start).");
            result.Events = result.Events.Select(e => e.Normalized()).ToList();
            if (result.Events.Count(Noteworthy) >= Math.Max(1, minEvents)) return Typed(result, Summary(result));
            if (DateTime.UtcNow >= until)
            {
                var pending = result.Events.Count;
                result.Events = [];
                result.Extra = new() { ["waiting"] = JsonSerializer.SerializeToElement(true), ["pending"] = JsonSerializer.SerializeToElement(pending) };
                return Typed(result, $"Nothing noteworthy yet ({pending} other events). Call observe_wait again with the same since to keep them.");
            }
            await Task.Delay(3000, ct);
        }
    }

    [McpServerTool(Name = "observe_layers", Title = "What the observer watches (layers as specs)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(LayersResult), IconSource = IconSet.TimelineLight)]
    [Description("List, add or remove observer layers. A layer is a spec, not code: any walker path (as in eval_path / explore_object) " +
                 "watched in one of five modes - struct (the object's cached offsets struct read raw and diffed, each changed range " +
                 "named by the runtime layout or null when the HUD doesn't map it: for server-sent state), props (its scalar " +
                 "properties), dict (a dictionary's keys and values, e.g. Stats.StatDictionary), list (a collection's items added and " +
                 "removed, by key, default Address), each (a collection, props = a few dotted sub-paths per item keyed by key: one " +
                 "layer over all the player's inventories). Defaults: server (ServerData, struct), stats (StatDictionary, dict), life " +
                 "(Life, props), buffs (BuffsList, list by Name), inventories (PlayerInventories, each: Inventory.Hash and ItemCount by TypeId). " +
                 "set preflights the path and mode in game and names the broken link. Specs persist in the HUD.")]
    public static async Task<CallToolResult> ObserveLayers(BridgeRegistry bridges,
        [Description("list | set | remove")] string action = "list",
        [Description("Layer id (set / remove), e.g. buffs")] string? id = null,
        [Description("set: walker path starting at GameController, e.g. GameController.Player.GetComponent<Buffs>().BuffsList")] string? path = null,
        [Description("set: struct | props | dict | list | each")] string? mode = null,
        [Description("set: samples per second (0.2-30, default 4)")] double hz = 4,
        [Description("set: false to keep the spec but pause it")] bool enabled = true,
        [Description("set, list / each mode: the item property identifying an item (each: dotted sub-path; default Address)")] string? key = null,
        [Description("set, each mode: values to watch on every item, as dotted sub-paths, e.g. [\"Inventory.Hash\", \"Inventory.ItemCount\"]")] string[]? props = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        JToken r;
        switch (action)
        {
            case "set":
                if (id == null || path == null || mode == null) throw new McpException("set needs id, path and mode.");
                var spec = new JObject { ["id"] = id, ["path"] = path, ["mode"] = mode, ["hz"] = hz, ["enabled"] = enabled };
                if (key != null) spec["key"] = key;
                if (props is { Length: > 0 }) spec["props"] = new JArray(props);
                (_, r) = await bridges.CallAsync(game, "observe.layer_set", spec, ct);
                break;
            case "remove":
                if (id == null) throw new McpException("remove needs id.");
                (_, r) = await bridges.CallAsync(game, "observe.layer_remove", new JObject { ["id"] = id }, ct);
                break;
            default:
                (_, r) = await bridges.CallAsync(game, "observe.layers", new JObject(), ct);
                break;
        }
        NeedLayers(r);
        var result = Dto.From<LayersResult>(r);
        if (action != "list")
        {
            var (_, all) = await bridges.CallAsync(game, "observe.layers", new JObject(), ct);
            var listed = Dto.From<LayersResult>(all);
            result.Modes = listed.Modes; result.Layers = listed.Layers;
        }
        var sb = new StringBuilder();
        if (result.Layer != null) sb.AppendLine($"Set {result.Layer.Id} ({result.Preflight}).");
        if (result.Removed != null) sb.AppendLine($"Removed {result.Removed}.");
        foreach (var l in result.Layers)
            sb.AppendLine($"{l.Spec.Id,-10} {l.Spec.Mode,-6} {l.Spec.Hz,4} Hz  {l.Spec.Path}  events {l.Events}, units {l.UnitsChanged}, {l.CostMs} ms/tick"
                          + (l.Broken != null ? $"  BROKEN: {l.Broken}" : l.NotNow != null ? $"  (not now: {l.NotNow})" : "") + (l.Spec.Enabled ? "" : "  (paused)"));
        return Typed(result, sb.ToString());
    }

    [McpServerTool(Name = "observe_layer_map", Title = "What changed in a layer, mapped or not", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(LayerMapResult), IconSource = IconSet.TimelineLight)]
    [Description("Every unit of one observer layer that changed since observing started (struct: offsets with the HUD's name or null " +
                 "when unmapped; props: properties; dict: keys; list: items), with how many times and how often, first/last time " +
                 "and last value. The mapping worklist: an unmapped offset that changes rarely is an event to name (observe_timeline " +
                 "layer=<it> unit=<offset> shows what happens at the same moments); one that changes constantly is a live value. " +
                 "Subscribable as exile://observe/{game}/layers/{layer}.")]
    public static async Task<CallToolResult> ObserveLayerMap(BridgeRegistry bridges,
        [Description("Layer id (default server)")] string layer = "server",
        [Description("struct layers: only offsets the HUD doesn't name")] bool unmappedOnly = false,
        [Description("Ignore units that changed fewer times")] long minChanges = 1,
        [Description("changes (most first) | recent | unit")] string sort = "changes",
        [Description("Max units (1-1000, default 60)")] int limit = 60,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (_, r) = await bridges.CallAsync(game, "observe.layer_map",
            new JObject { ["layer"] = layer, ["unmappedOnly"] = unmappedOnly, ["minChanges"] = minChanges, ["sort"] = sort, ["limit"] = Math.Clamp(limit, 1, 1000) }, ct);
        NeedLayers(r);
        var result = Dto.From<LayerMapResult>(r);
        var sb = new StringBuilder($"{result.Layer.Spec.Id} ({result.Layer.Spec.Mode}, {result.Layer.Spec.Path}): {result.Layer.UnitsChanged} units changed\n");
        foreach (var u in result.Units)
            sb.AppendLine($"  {u.Unit,-10} {u.Name ?? (result.Layer.Spec.Mode == "struct" ? "(unmapped)" : ""),-28} x{u.Changes} ({u.PerMinute}/min) last {u.Last}{(u.Logged ? "" : "  [noisy: counted, not in the journal]")}");
        return Typed(result, sb.ToString());
    }

    [McpServerTool(Name = "observe_timeline", Title = "What happened at the same moment, across layers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(TimelineResult), IconSource = IconSet.TimelineLight)]
    [Description("Cross-reference the observer's layers and events on one time axis, from the journal on disk (all sessions, not just " +
                 "the 1000 in memory). Two modes: around=<seq> lists every event within windowMs of that event, ordered, with the " +
                 "offset from it; layer + unit (e.g. layer=server unit=0x2368, or layer=stats unit=<stat>) takes each logged change of " +
                 "that unit and counts which other events happened within windowMs of it - consistent companions (a panel opening, " +
                 "an area change, a stat, another offset) are what the unit means. Read-only.")]
    public static Task<CallToolResult> ObserveTimeline(BridgeRegistry bridges,
        [Description("Sequence number of the event to centre on (default: the latest)")] long? around = null,
        [Description("Layer of the unit to cross-reference (default server)")] string layer = "server",
        [Description("Unit to cross-reference: a struct offset (0x2368), property, dictionary key or item id")] string? unit = null,
        [Description("Half-width of the window in ms (10-60000, default 1000)")] int windowMs = 1000,
        [Description("Only these kinds in the output, e.g. layer, ui, area")] string[]? kinds = null,
        [Description("Most recent journal lines to read (1000-500000, default 100000)")] int scan = 100_000,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null)
    {
        var bridge = bridges.Resolve(game);
        var path = Path.Combine(bridge.BridgeDir, "observe", "journal.jsonl");
        if (!File.Exists(path)) throw new McpException("No observer journal yet: turn observation on (observe action=start).");
        windowMs = Math.Clamp(windowMs, 10, 60_000);
        var events = ReadJournalTail(path, Math.Clamp(scan, 1000, 500_000));
        var keep = kinds is { Length: > 0 } ? kinds.ToHashSet() : null;
        var win = TimeSpan.FromMilliseconds(windowMs);
        var sb = new StringBuilder();
        var result = new TimelineResult { JournalEvents = events.Count, WindowMs = windowMs };

        if (unit != null)
        {
            var hits = events.Where(e => e.Kind == "layer" && e.Layer == layer && string.Equals(e.Unit, unit, StringComparison.OrdinalIgnoreCase)).ToList();
            result.Layer = layer; result.Unit = unit; result.Changes = hits.Count;
            if (hits.Count == 0)
                return Task.FromResult(Typed(result, $"No logged change of {layer} {unit} in the last {events.Count} journal events (a noisy unit is counted, not logged: see observe_layer_map)."));
            var tally = new Dictionary<string, (int n, double sumDtMs)>();
            var self = hits[0].Key();
            foreach (var h in hits)
            {
                var seen = new HashSet<string>();
                foreach (var e in Near(events, h.At, win))
                {
                    if (e.Seq == h.Seq || (keep != null && !keep.Contains(e.Kind))) continue;
                    var k = e.Key();
                    if (k == self || !seen.Add(k)) continue;
                    var dt = (e.At - h.At).TotalMilliseconds;
                    tally[k] = tally.TryGetValue(k, out var t) ? (t.n + 1, t.sumDtMs + dt) : (1, dt);
                }
            }
            var rows = tally.OrderByDescending(kv => kv.Value.n).Take(40).ToList();
            result.Companions = rows.Select(kv => new TimelineCompanion { Event = kv.Key, Count = kv.Value.n, AvgDtMs = Math.Round(kv.Value.sumDtMs / kv.Value.n, 1) }).ToList();
            result.RecentChanges = hits.TakeLast(20).ToList();
            sb.AppendLine($"{self} changed {hits.Count} times in the journal; within {windowMs} ms of those changes:");
            foreach (var c in result.Companions) sb.AppendLine($"  {c.Count}/{hits.Count}  {c.Event}  (avg {Math.Round(c.AvgDtMs):+0;-0;0} ms)");
            if (rows.Count == 0) sb.AppendLine("  nothing else: it changes on its own (server-pushed, or a value the client updates by itself).");
            sb.Append("Changes: " + string.Join(", ", hits.TakeLast(8).Select(h => $"#{h.Seq} {h.Old}->{h.New}")));
            return Task.FromResult(Typed(result, sb.ToString()));
        }

        var centre = around is { } s ? events.FirstOrDefault(e => e.Seq == s) : events.LastOrDefault();
        if (centre == null) throw new McpException(around is { } a ? $"Event #{a} is not in the last {events.Count} journal events." : "The journal is empty.");
        var list = Near(events, centre.At, win).Where(e => keep == null || keep.Contains(e.Kind) || e.Seq == centre.Seq).ToList();
        result.Centre = centre.Seq;
        result.Events = list.Take(500).ToList();
        sb.AppendLine($"{list.Count} events within {windowMs} ms of #{centre.Seq} ({centre.At:HH:mm:ss.fff} UTC):");
        foreach (var e in list.Take(200))
            sb.AppendLine($"  {(e.At - centre.At).TotalMilliseconds,7:+0;-0;0} ms  #{e.Seq,-7} {e.Line()}{(e.Seq == centre.Seq ? "   <==" : "")}");
        if (list.Count > 200) sb.AppendLine($"  ... {list.Count - 200} more (narrow windowMs or kinds)");
        return Task.FromResult(Typed(result, sb.ToString()));
    }

    /// <summary>
    /// The last n journal lines as typed events (bad lines skipped), in order. Reads from the end (at most ~1 KB per
    /// wanted line) instead of the whole file, and takes the rest from journal.1.jsonl (the bridge rotates the journal
    /// at 128 MB) when the current one is short.
    /// </summary>
    private static List<ObserveEvent> ReadJournalTail(string path, int n)
    {
        var q = new Queue<string>(n);
        var rotated = Path.Combine(Path.GetDirectoryName(path)!, "journal.1.jsonl");
        foreach (var file in new[] { rotated, path })
        {
            if (!File.Exists(file)) continue;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var from = Math.Max(0, fs.Length - (long)n * 1024);
            fs.Seek(from, SeekOrigin.Begin);
            using var rd = new StreamReader(fs);
            if (from > 0) rd.ReadLine();   // a partial line
            for (string? line; (line = rd.ReadLine()) != null;) { if (q.Count == n) q.Dequeue(); q.Enqueue(line); }
        }
        var list = new List<ObserveEvent>(q.Count);
        foreach (var line in q)
            try { if (JsonSerializer.Deserialize<ObserveEvent>(line, Dto.Options) is { } e) list.Add(e.Normalized()); } catch (JsonException) { }
        return list;
    }

    /// <summary>Events within win of at. The journal is in time order, so binary-search the start.</summary>
    private static IEnumerable<ObserveEvent> Near(List<ObserveEvent> events, DateTimeOffset at, TimeSpan win)
    {
        int lo = 0, hi = events.Count;
        var from = at - win;
        while (lo < hi) { var mid = (lo + hi) / 2; if (events[mid].At < from) lo = mid + 1; else hi = mid; }
        for (var i = lo; i < events.Count && events[i].At <= at + win; i++) yield return events[i];
    }

    private static string Summary(ObserveEventsResult r)
    {
        var sb = new StringBuilder();
        // New entity kinds come in bursts (a town lists dozens of NPC variants): one line per area, not one per kind.
        foreach (var g in r.Events.Where(e => e.Kind == "entity").GroupBy(e => e.Area ?? "?"))
            sb.AppendLine($"#{g.First().Seq}..#{g.Last().Seq} {g.Count()} new entity kind(s) in {g.Key}: " +
                          string.Join(", ", g.Take(8).Select(e => (e.Type ?? "").Replace("Metadata/", ""))) + (g.Count() > 8 ? ", ..." : ""));
        foreach (var e in r.Events.Where(e => e.Kind != "entity")) sb.AppendLine($"#{e.Seq} {e.Line()}");
        if (sb.Length == 0) sb.AppendLine("No events.");
        sb.Append($"seq={r.Seq} (pass as since next time).");
        return sb.ToString();
    }

    /// <summary>Typed structured content (matches the tool's output schema) plus a text rendering.</summary>
    private static CallToolResult Typed<T>(T value, string text) => new()
    {
        Content = [new TextContentBlock { Text = text.TrimEnd() }],
        StructuredContent = Dto.Element(value),
    };

    private static void Need(JToken? r)
    {
        if (r is not JObject o || (o["ok"] == null && o["error"] == null))
            throw new McpException("This HUD's bridge plugin has no passive observation yet: update What's an AI Bridge and restart the HUD.");
    }

    private static void NeedLayers(JToken r)
    {
        if (r["error"]?.ToString() == "unknown_method" || (r["ok"] == null && r["error"] == null))
            throw new McpException("This HUD's bridge has no observer layers yet: update What's an AI Bridge and restart the HUD.");
    }
}
