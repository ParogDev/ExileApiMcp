using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ExileApiMcp.Apps;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Read-only memory inspection over the bridge's memory.* methods: what the HUD maps in a struct and what it
/// doesn't, structure-looking data in the unmapped ranges, raw regions, and byte/bit changes over time.
/// Outlines for the model; full JSON in structuredContent for the memory view app.
/// </summary>
[McpServerToolType]
public static class MemoryTools
{
    private const string Target = "Walker path to a memory object (e.g. GameController.Player.GetComponent<Life>(), " +
                                  "GameController.IngameState.ServerData.PlayerStashTabs[0]) - its Address is used";
    private const string AddressDesc = "Absolute address instead of a path: number or \"0x...\"";

    [McpServerTool(Name = "memory_layout", Title = "Struct mapping vs live memory", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Overlay the offsets struct the HUD itself reads for an object (found by reflection - on PoE2 the real " +
                 "obfuscated struct, not the GameOffsets2 decoys) on live memory: each mapped field with offset, value, set bits " +
                 "for flag fields, and a sanity check (bad floats, pointers that don't point anywhere, broken std::vectors); the " +
                 "unmapped ranges between fields; and unmapped slots that look like structure the HUD doesn't map yet " +
                 "(std::vectors, pointers to objects with vtables, text, module pointers with Ghidra addresses). Use after a patch " +
                 "to check a mapping still holds, and to find new members. extend reads past the struct's end.")]
    public static async Task<CallToolResult> MemoryLayout(BridgeRegistry bridges,
        [Description(Target)] string? path = null,
        [Description(AddressDesc)] JsonElement? address = null,
        [Description("Struct type name to overlay instead of the HUD's own (e.g. GameOffsets.LifeComponentOffsets); required with address")] string? type = null,
        [Description("Bytes to read past the struct's declared end (0-2048)")] int extend = 0,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var p = Params(path, address);
        if (type != null) p["type"] = type;
        if (extend > 0) p["extend"] = Math.Min(extend, 2048);
        var (bridge, result) = await Call(bridges, game, "memory.layout", p, ct);
        if (result["error"] != null) return ToolResults.Json(result);
        result["game"] = bridge.Game;
        return Result(LayoutOutline(result), result);
    }

    [McpServerTool(Name = "memory_read", Title = "Read a memory region", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("A memory region as classified 8-byte slots: zero, module pointer (section, RVA, Ghidra address, vtable " +
                 "class if RTTI exists), heap pointer (and what it points at: an object with a vtable, text), float pair, int " +
                 "(set bits when flag-like), text - plus a hex dump. Follow pointers by reading their value as the next address.")]
    public static async Task<CallToolResult> MemoryRead(BridgeRegistry bridges,
        [Description(Target)] string? path = null,
        [Description(AddressDesc)] JsonElement? address = null,
        [Description("Offset added to the start, bytes")] long offset = 0,
        [Description("Bytes to read (8-4096, default 256)")] int size = 256,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var p = Params(path, address);
        p["offset"] = offset;
        p["size"] = size;
        var (bridge, result) = await Call(bridges, game, "memory.read", p, ct);
        if (result["error"] != null) return ToolResults.Json(result);
        result["game"] = bridge.Game;
        return Result(ReadOutline(result), result);
    }

    [McpServerTool(Name = "memory_where", Title = "What is at an address", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("What an address is: inside the game module (section, RVA, and the Ghidra address to pass to the ghidra " +
                 "MCP's decompile/xref tools) or a heap region, and what it points at.")]
    public static async Task<CallToolResult> MemoryWhere(BridgeRegistry bridges,
        [Description("Address: number or \"0x...\"")] JsonElement address,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (_, result) = await Call(bridges, game, "memory.where", Params(null, address), ct);
        return ToolResults.Json(result);
    }

    [McpServerTool(Name = "watch_memory", Title = "Watch memory for changes", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Sample a memory region repeatedly and report which bytes changed: ranges with first/last bytes, how often, " +
                 "and the bits that flipped - labelled with the HUD's field names when a path is given (unmapped changes are " +
                 "the interesting ones). Run it while the user does one thing in game (toggle a stash tab affinity, swap " +
                 "weapons) to find where that state lives, including bit flags.")]
    public static async Task<CallToolResult> WatchMemory(BridgeRegistry bridges,
        [Description(Target)] string? path = null,
        [Description(AddressDesc)] JsonElement? address = null,
        [Description("Offset added to the start, bytes")] long offset = 0,
        [Description("Bytes to watch (8-4096, default: the struct size for a path, else 256)")] int size = 0,
        [Description("How long to watch, ms (500-60000, default 5000)")] int durationMs = 5000,
        [Description("Sampling interval, ms (50-2000, default 100)")] int intervalMs = 100,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        durationMs = Math.Clamp(durationMs, 500, 60_000);
        intervalMs = Math.Clamp(intervalMs, 50, 2000);

        // Field names (and the default size) from the HUD's struct when watching a path.
        JObject? layout = null;
        if (path != null && offset == 0)
        {
            var (_, l) = await Call(bridges, game, "memory.layout", Params(path, null), ct);
            if (l["error"] == null) layout = l;
        }
        if (size <= 0) size = layout?["structSize"]?.Value<int>() ?? 256;
        size = Math.Clamp(size, 8, 4096);

        var p = Params(path, address);
        p["offset"] = offset;
        p["size"] = size;
        p["classify"] = false;
        var sw = Stopwatch.StartNew();
        byte[]? first = null, prev = null;
        var changes = new int[size];
        var lastChangeMs = new long[size];
        var firstChangeMs = new long[size];
        var flipped = new byte[size];
        int samples = 0;
        string? start = null;
        while (sw.ElapsedMilliseconds <= durationMs)
        {
            var t0 = sw.ElapsedMilliseconds;
            var (_, r) = await Call(bridges, game, "memory.read", p, ct);
            if (r["error"] != null) { if (samples == 0) return ToolResults.Json(r); break; }
            start ??= r["address"]?.ToString();
            var cur = Convert.FromBase64String(r["data"]!.ToString());
            samples++;
            first ??= cur;
            if (prev != null)
                for (int i = 0; i < Math.Min(cur.Length, prev.Length); i++)
                    if (cur[i] != prev[i])
                    {
                        if (changes[i]++ == 0) firstChangeMs[i] = t0;
                        lastChangeMs[i] = t0;
                        flipped[i] |= (byte)(cur[i] ^ prev[i]);
                    }
            prev = cur;
            var wait = intervalMs - (int)(sw.ElapsedMilliseconds - t0);
            if (wait > 0) await Task.Delay(wait, ct);
        }

        // Group adjacent changed bytes into ranges, then label them with the struct fields they overlap.
        var fields = (layout?["fields"] as JArray)?.OfType<JObject>().ToList() ?? [];
        var ranges = new JArray();
        for (int i = 0; i < size && first != null && prev != null; i++)
        {
            if (changes[i] == 0) continue;
            int j = i;
            while (j + 1 < size && changes[j + 1] > 0) j++;
            var len = j - i + 1;
            var overlappingFields = fields.Where(f => f["off"]!.Value<int>() <= j && f["off"]!.Value<int>() + f["size"]!.Value<int>() > i).ToList();
            var overlapping = overlappingFields.Select(f => f["name"]!.ToString()).ToList();
            // Bit numbers relative to the field when one field covers the range (Affinity bit 11 = value 1 << 11),
            // otherwise relative to the range's first byte.
            var bitBase = overlappingFields.Count == 1 ? overlappingFields[0]["off"]!.Value<int>() : i;
            var bits = new JArray();
            for (int k = i; k <= j; k++)
                for (int bit = 0; bit < 8; bit++)
                    if ((flipped[k] & (1 << bit)) != 0) bits.Add((k - bitBase) * 8 + bit);
            ranges.Add(new JObject
            {
                ["off"] = i, ["size"] = len,
                ["field"] = overlapping.Count > 0 ? string.Join(", ", overlapping) : layout != null ? "(unmapped)" : null,
                ["changes"] = Enumerable.Range(i, len).Max(k => changes[k]),
                ["first"] = BitConverter.ToString(first, i, len).Replace("-", " "),
                ["last"] = BitConverter.ToString(prev, i, len).Replace("-", " "),
                ["bitsFlipped"] = bits.Count <= 16 ? bits : null,
                ["bitsRelativeTo"] = bits.Count <= 16 ? (overlappingFields.Count == 1 ? $"field {overlapping[0]} (+{bitBase})" : $"+{i}") : null,
                ["firstChangeAtMs"] = Enumerable.Range(i, len).Min(k => changes[k] > 0 ? firstChangeMs[k] : long.MaxValue),
                ["lastChangeAtMs"] = Enumerable.Range(i, len).Max(k => lastChangeMs[k]),
                ["noisy"] = Enumerable.Range(i, len).Max(k => changes[k]) >= samples * 0.6 ? true : null,
            });
            i = j;
        }
        var o = new JObject
        {
            ["address"] = start, ["size"] = size, ["samples"] = samples, ["durationMs"] = sw.ElapsedMilliseconds,
            ["struct"] = layout?["struct"], ["changedRanges"] = ranges,
        };
        if (ranges.Count == 0) o["note"] = "Nothing changed. Ask the user to do the thing while this runs, or widen size/extend the region.";
        foreach (var r in ranges.OfType<JObject>()) foreach (var prop in r.Properties().Where(x => x.Value.Type == JTokenType.Null).ToList()) prop.Remove();
        return ToolResults.Json(o);
    }

    [McpServerTool(Name = "show_memory_view", Title = "Open the memory view", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [McpAppUi(ResourceUri = MemoryViewApp.ResourceUri)]
    [McpMeta("ui/resourceUri", MemoryViewApp.ResourceUri)]
    [Description("Open the interactive memory view (clients that support MCP Apps) at an object or address: the struct the " +
                 "HUD maps laid over live bytes, mapped vs unmapped ranges, structure candidates in the gaps, pointer following, " +
                 "live change highlighting and bit views, Ghidra addresses. Other clients get the memory_layout outline.")]
    public static Task<CallToolResult> ShowMemoryView(BridgeRegistry bridges,
        [Description(Target)] string? path = null,
        [Description(AddressDesc)] JsonElement? address = null,
        [Description("Struct type to overlay (default: the HUD's own for the object)")] string? type = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
        => path == null && address == null
            ? MemoryLayout(bridges, "GameController.Player.GetComponent<Life>()", null, null, 64, game, ct)
            : address != null && type == null
                ? MemoryRead(bridges, path, address, 0, 256, game, ct)
                : MemoryLayout(bridges, path, address, type, 64, game, ct);

    // ── Helpers ──────────────────────────────────────────────────────

    private static JObject Params(string? path, JsonElement? address)
    {
        var p = new JObject();
        if (address is { } a && a.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            p["address"] = a.ValueKind == JsonValueKind.Number ? a.GetInt64() : a.GetString();
        else if (!string.IsNullOrWhiteSpace(path)) p["path"] = path;
        else throw new McpException("Pass path (a walker path to a memory object) or address.");
        return p;
    }

    private static async Task<(BridgeClient bridge, JObject result)> Call(BridgeRegistry bridges, string? game, string method, JObject p, CancellationToken ct)
    {
        var (bridge, result) = await bridges.CallAsync(game, method, p, ct);
        if (result is not JObject o || (o["error"] == null && o["address"] == null))
            throw new McpException("This HUD's bridge plugin has no memory.* methods yet: update What's an AI Bridge and restart the HUD.");
        return (bridge, o);
    }

    private static CallToolResult Result(string outline, JObject full) => new()
    {
        Content = [new TextContentBlock { Text = outline }],
        StructuredContent = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(full.ToString(Formatting.None)),
    };

    internal static string LayoutOutline(JObject r)
    {
        var sb = new StringBuilder();
        sb.Append(r["struct"]).Append(" @ ").Append(r["address"]).Append("  (").Append(r["structSize"]).Append(" bytes; ").Append(r["source"]).AppendLine(")");
        sb.AppendLine(r["summary"]?.ToString());
        var rows = new List<(int off, string line)>();
        foreach (var f in (r["fields"] as JArray ?? []).OfType<JObject>())
        {
            var line = new StringBuilder($"  +{f["off"],-5} {f["name"]}: {f["type"]} = {f["value"]}");
            if (f["bits"] is JArray bits && bits.Count > 0) line.Append("  bits ").Append(string.Join(",", bits));
            if (f["points"] != null) line.Append("  -> ").Append(f["points"]);
            if (f["text"] != null) line.Append("  \"").Append(f["text"]).Append('"');
            if (f["ghidra"] != null) line.Append("  ghidra ").Append(f["ghidra"]);
            var check = f["check"]?.ToString();
            if (check is not ("ok" or null)) line.Append("  !").Append(check).Append(f["why"] != null ? $": {f["why"]}" : "");
            rows.Add((f["off"]!.Value<int>(), line.ToString()));
        }
        foreach (var g in (r["gaps"] as JArray ?? []).OfType<JObject>())
            rows.Add((g["off"]!.Value<int>(), $"  +{g["off"],-5} ~ unmapped {g["size"]} bytes"));
        foreach (var c in (r["candidates"] as JArray ?? []).OfType<JObject>())
        {
            var line = new StringBuilder($"  +{c["off"],-5}   ? {c["kind"]}");
            if (c["detail"] != null) line.Append(' ').Append(c["detail"]);
            else if (c["value"] != null) line.Append(' ').Append(c["value"]);
            if (c["points"] != null) line.Append(" -> ").Append(c["points"]);
            if (c["text"] != null) line.Append(" \"").Append(c["text"]).Append('"');
            if (c["rtti"] != null) line.Append(' ').Append(c["rtti"]);
            if (c["ghidra"] != null) line.Append("  ghidra ").Append(c["ghidra"]);
            rows.Add((c["off"]!.Value<int>(), line.ToString()));
        }
        // Stable: fields, then the gap that starts there, then candidates inside it.
        foreach (var (_, line) in rows.Select((x, i) => (x, i)).OrderBy(t => t.x.off).ThenBy(t => t.i).Select(t => t.x))
            sb.AppendLine(line);
        sb.Append("Legend: +off name: type = value (mapped by the HUD) | ~ unmapped range | ? structure-looking data inside it. ")
          .Append("Follow a pointer with memory_read address=<value>; decompile a ghidra address with the ghidra MCP.");
        return sb.ToString();
    }

    private static string ReadOutline(JObject r)
    {
        var sb = new StringBuilder();
        sb.Append(r["address"]).Append(" (").Append(r["size"]).Append(" bytes, ").Append(r["origin"]).Append("; ").Append(r["region"]).AppendLine(")");
        foreach (var s in (r["slots"] as JArray ?? []).OfType<JObject>())
        {
            var kind = s["kind"]?.ToString();
            if (kind == "zero") continue;
            sb.Append($"  +{s["off"],-5} {kind,-8} {s["hex"]}");
            if (s["value"] != null && kind is "int" or "float") sb.Append(" = ").Append(s["value"]);
            if (s["bits"] is JArray bits) sb.Append(" bits ").Append(string.Join(",", bits));
            if (s["section"] != null) sb.Append(' ').Append(s["section"]).Append(" rva ").Append(s["rva"]);
            if (s["ghidra"] != null) sb.Append(" ghidra ").Append(s["ghidra"]);
            if (s["rtti"] != null) sb.Append(' ').Append(s["rtti"]);
            if (s["points"] != null) sb.Append(" -> ").Append(s["points"]);
            if (s["text"] != null) sb.Append(" \"").Append(s["text"]).Append('"');
            sb.AppendLine();
        }
        sb.Append("Zero slots omitted. Follow a heap pointer with memory_read address=<hex>.");
        return sb.ToString();
    }
}
