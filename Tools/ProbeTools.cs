using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Empirical probing of memory: evidence instead of guesses.
///   memory_correlate   across a population (every stash tab, every entity...), which bits/values are fully explained
///                      by a known property - with counts and counterexamples - and where a known value is stored.
///   memory_snapshot    save a region (or a whole collection's regions) under a name, on disk.
///   memory_compare     diff named snapshots step by step (baseline -> change one thing -> change it back).
/// Snapshots live in %LOCALAPPDATA%\ExileApiMcp\snapshots (the server keeps no state in memory).
/// </summary>
[McpServerToolType]
public static class ProbeTools
{
    /// <summary>Items needed on each side of a binary split before a match counts as evidence.</summary>
    private const int MinSupport = 3;

    private static readonly string SnapshotDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExileApiMcp", "snapshots");

    // ── Correlate ────────────────────────────────────────────────────

    [McpServerTool(Name = "memory_correlate", Title = "Correlate memory bits with known properties", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Test hypotheses about unknown bytes across a whole population instead of one sample. Reads the same byte " +
                 "range from every item of a collection (e.g. GameController.IngameState.ServerData.PlayerStashTabs) and, for " +
                 "each known property (labels, e.g. Affinity, TabType, Name), reports: bits that equal the property's " +
                 "non-zero-ness or one of its bits, or are fully determined by its value - with counts and counterexamples " +
                 "(near-misses with <= 2 counterexamples are listed separately); and offsets where the property's value " +
                 "itself is stored. A perfect match over many varied items is evidence; few items or one-sided counts are not.")]
    public static async Task<CallToolResult> MemoryCorrelate(BridgeRegistry bridges,
        [Description("Walker path to a collection of memory objects")] string path,
        [Description("Known per-item properties to test against (dotted property paths), e.g. [\"Affinity\",\"TabType\"]")] string[] labels,
        [Description("Start of the range inside each item, bytes")] int offset = 0,
        [Description("Bytes per item (default: the struct size the HUD reads)")] int size = 0,
        [Description("Max items (default 500)")] int limit = 500,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        if (labels.Length == 0) throw new McpException("Pass at least one label (a known property of each item) to correlate against.");
        var population = await Collect(bridges, game, path, offset, size, limit, labels, ct);
        var items = population["items"] as JArray ?? [];
        if (items.Count < 2) throw new McpException($"Only {items.Count} readable items under '{path}'; a correlation needs a population.");

        var data = items.Select(i => Convert.FromBase64String(i["data"]!.ToString())).ToList();
        int width = data.Min(d => d.Length);
        var report = new JObject { ["path"] = path, ["offset"] = offset, ["size"] = width, ["items"] = items.Count, ["struct"] = population["struct"] };
        var findings = new JArray();
        var sb = new StringBuilder();
        sb.AppendLine($"{items.Count} items of {path}, bytes +{offset}..+{offset + width - 1}{(population["struct"] != null ? $" ({population["struct"]})" : "")}");

        foreach (var label in labels)
        {
            var values = items.Select(i => i["labels"]?[label]).ToList();
            var features = Features(label, values);
            var labelFinding = new JObject { ["label"] = label, ["distinctValues"] = values.Select(v => v?.ToString()).Distinct().Count() };
            var perfect = new JArray();
            var near = new JArray();
            int weak = 0;
            var stored = StoredAt(values, data, offset, width);
            // Bytes holding the label itself: every bit there "matches" trivially.
            var ownBytes = new HashSet<int>(stored.OfType<JObject>().SelectMany(s =>
                Enumerable.Range(s["offset"]!.Value<int>() - offset, int.Parse(s["type"]!.ToString().Split('-')[0]) / 8)));
            for (int bit = 0; bit < width * 8; bit++)
            {
                if (ownBytes.Contains(bit / 8)) continue;
                var bits = data.Select(d => (d[bit / 8] >> (bit % 8) & 1) == 1).ToArray();
                int ones = bits.Count(b => b);
                if (ones == 0 || ones == bits.Length) continue; // constant: says nothing
                foreach (var (name, f) in features)
                {
                    int same = 0;
                    for (int i = 0; i < bits.Length; i++) if (bits[i] == f[i]) same++;
                    int miss = Math.Min(bits.Length - same, same);
                    bool inverted = same < bits.Length - same;
                    if (miss > 2) continue;
                    int fTrue = f.Count(x => x);
                    // Evidence needs both sides: a match backed by 1-2 items is coincidence-prone (text, pointers).
                    if (Math.Min(fTrue, f.Length - fTrue) < MinSupport) { if (miss == 0) weak++; continue; }
                    var row = new JObject
                    {
                        ["byte"] = offset + bit / 8, ["bit"] = bit % 8, ["equals"] = (inverted ? "NOT " : "") + name,
                        ["evidence"] = $"{fTrue} items where {name} vs {f.Length - fTrue} where not",
                        ["counterexamples"] = miss,
                    };
                    if (miss > 0)
                        row["counterexampleItems"] = new JArray(Enumerable.Range(0, bits.Length)
                            .Where(i => (bits[i] == f[i]) == inverted).Take(2)
                            .Select(i => new JObject { ["index"] = items[i]["index"], ["labels"] = items[i]["labels"] }));
                    (miss == 0 ? perfect : near).Add(row);
                }
                // Categorical: the bit is a function of the label's value (each value -> one bit value).
                if (labelFinding["distinctValues"]!.Value<int>() is > 2 and <= 40)
                {
                    var byValue = values.Select((v, i) => (v: v?.ToString() ?? "null", b: bits[i])).GroupBy(x => x.v).ToList();
                    // Only meaningful when the groups have several members each and both bit values occur in many items.
                    bool enough = byValue.Count(g => g.Count() >= 2) * 2 >= byValue.Count && Math.Min(ones, bits.Length - ones) >= MinSupport;
                    if (enough && byValue.All(g => g.Select(x => x.b).Distinct().Count() == 1) && !perfect.Any(p => p["byte"]!.Value<int>() == offset + bit / 8 && p["bit"]!.Value<int>() == bit % 8))
                        perfect.Add(new JObject
                        {
                            ["byte"] = offset + bit / 8, ["bit"] = bit % 8, ["equals"] = $"a function of {label}",
                            ["setFor"] = string.Join(", ", byValue.Where(g => g.First().b).Select(g => g.Key).Take(12)),
                            ["counterexamples"] = 0,
                        });
                }
            }
            labelFinding["bitsExplained"] = perfect;
            if (near.Count > 0) labelFinding["nearMisses"] = near;
            labelFinding["storedAt"] = stored;
            if (weak > 0) labelFinding["tooLittleEvidence"] = $"{weak} exact matches backed by fewer than {MinSupport} items on one side (not listed)";
            findings.Add(labelFinding);

            sb.AppendLine().Append($"{label} ({labelFinding["distinctValues"]} distinct values):").AppendLine();
            sb.AppendLine(stored.Count > 0 ? $"  value stored at: {string.Join(", ", stored.Select(s => $"+{s["offset"]} ({s["type"]})"))}" : "  value not stored in this range as a 1/2/4/8-byte integer");
            foreach (var p in perfect.OfType<JObject>())
                sb.AppendLine($"  +{p["byte"]} bit {p["bit"]} == {p["equals"]}  [{p["evidence"] ?? $"set for: {p["setFor"]}"}; 0 counterexamples]");
            foreach (var n in near.OfType<JObject>())
                sb.AppendLine($"  +{n["byte"]} bit {n["bit"]} ~ {n["equals"]}  [{n["counterexamples"]} counterexample(s): {n["counterexampleItems"]?.ToString(Formatting.None)}]");
            if (perfect.Count == 0 && near.Count == 0) sb.AppendLine("  no bit in range is explained by it (with enough evidence)");
            if (weak > 0) sb.AppendLine($"  ({labelFinding["tooLittleEvidence"]})");
        }
        report["findings"] = findings;
        sb.AppendLine().Append("Bits that repeat the label's own storage are expected. Evidence is strong when both sides of the count are large; " +
                               "confirm a finding with a one-variable experiment (memory_snapshot -> change one thing -> memory_snapshot -> memory_compare).");
        return Result(sb.ToString(), report);
    }

    /// <summary>Binary features of a label: non-zero/true, each set bit of a mask, and equality with each common value.</summary>
    private static List<(string name, bool[] f)> Features(string label, List<JToken?> values)
    {
        var list = new List<(string, bool[])>();
        var nums = values.Select(v => v?.Type is JTokenType.Integer ? v.Value<long>() : (long?)null).ToList();
        if (nums.All(n => n != null))
        {
            var n = nums.Select(x => x!.Value).ToArray();
            list.Add(($"{label} != 0", n.Select(x => x != 0).ToArray()));
            // Masks: values with several bits set somewhere -> each bit is a feature.
            if (n.Any(x => System.Numerics.BitOperations.PopCount((ulong)x) > 1) || n.Distinct().Count() > 2)
                for (int b = 0; b < 64; b++)
                {
                    var f = n.Select(x => (x >> b & 1) == 1).ToArray();
                    if (f.Any(x => x) && !f.All(x => x)) list.Add(($"{label} bit {b}", f));
                }
        }
        else if (values.All(v => v?.Type == JTokenType.Boolean))
            list.Add((label, values.Select(v => v!.Value<bool>()).ToArray()));
        else
            list.Add(($"{label} is empty", values.Select(v => string.IsNullOrEmpty(v?.ToString())).ToArray()));
        // Common values (categorical): "== X" for values held by 2+ items.
        foreach (var g in values.GroupBy(v => v?.ToString() ?? "null").Where(g => g.Count() >= 2).Take(20))
            list.Add(($"{label} == {g.Key}", values.Select(v => (v?.ToString() ?? "null") == g.Key).ToArray()));
        return list;
    }

    /// <summary>Offsets where an integer label's value is stored (1/2/4/8 bytes, little endian) in every item.</summary>
    private static JArray StoredAt(List<JToken?> values, List<byte[]> data, int offset, int width)
    {
        var hits = new JArray();
        if (!values.All(v => v?.Type == JTokenType.Integer) || values.Select(v => v!.Value<long>()).Distinct().Count() < 2) return hits;
        var n = values.Select(v => v!.Value<long>()).ToArray();
        foreach (var w in new[] { 1, 2, 4, 8 })
            for (int o = 0; o + w <= width; o++)
            {
                bool all = true;
                for (int i = 0; i < data.Count && all; i++)
                {
                    long v = w switch { 1 => data[i][o], 2 => BitConverter.ToUInt16(data[i], o), 4 => BitConverter.ToUInt32(data[i], o), _ => BitConverter.ToInt64(data[i], o) };
                    long expect = w == 8 ? n[i] : n[i] & ((1L << (w * 8)) - 1);
                    all = v == expect && (w == 8 || (n[i] >> (w * 8)) == 0);
                }
                if (all) hits.Add(new JObject { ["offset"] = offset + o, ["type"] = $"{w * 8}-bit" });
            }
        // Keep the widest match per offset.
        return new JArray(hits.OfType<JObject>().GroupBy(h => h["offset"]!.Value<int>()).Select(g => g.Last()));
    }

    [McpServerTool(Name = "memory_population", Title = "Raw bytes of every item in a collection", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("The same byte range from every item of a collection, with per-item labels, as hex - the raw data behind " +
                 "memory_correlate, for viewing it as a grid (items x bytes) or checking a hypothesis by eye. Prefer " +
                 "memory_correlate for conclusions.")]
    public static async Task<CallToolResult> MemoryPopulation(BridgeRegistry bridges,
        [Description("Walker path to a collection of memory objects")] string path,
        [Description("Per-item properties to include (dotted property paths), e.g. [\"Name\",\"Affinity\",\"TabType\"]")] string[]? labels = null,
        [Description("Start of the range inside each item, bytes")] int offset = 0,
        [Description("Bytes per item (default: the struct size the HUD reads; max 1024)")] int size = 0,
        [Description("Max items (default 200)")] int limit = 200,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var population = await Collect(bridges, game, path, offset, size, limit, labels ?? [], ct);
        var items = new JArray(((JArray)population["items"]!).OfType<JObject>().Select(i => new JObject
        {
            ["index"] = i["index"], ["address"] = i["address"], ["labels"] = i["labels"],
            ["hex"] = BitConverter.ToString(Convert.FromBase64String(i["data"]!.ToString())).Replace("-", " "),
        }));
        var o = new JObject
        {
            ["path"] = path, ["offset"] = offset, ["size"] = population["size"], ["struct"] = population["struct"],
            ["count"] = items.Count, ["items"] = items,
        };
        if (population["truncated"] != null) o["truncated"] = population["truncated"];
        var sb = new StringBuilder($"{items.Count} items of {path}, bytes +{offset}..+{offset + population["size"]!.Value<int>() - 1}\n");
        foreach (var i in items.OfType<JObject>().Take(40))
            sb.Append($"  [{i["index"]}] {i["labels"]?.ToString(Formatting.None)} {i["hex"]}\n");
        if (items.Count > 40) sb.Append($"  ... {items.Count - 40} more (structuredContent has all)");
        return Result(sb.ToString().TrimEnd(), o);
    }

    // ── Snapshots ────────────────────────────────────────────────────

    [McpServerTool(Name = "memory_snapshot", Title = "Save a named memory snapshot", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Save a region - or, for a collection path, the same region of every item - under a name, for controlled " +
                 "experiments: snapshot 'baseline', ask the user to change exactly one thing, snapshot 'after', then " +
                 "memory_compare. Labels (e.g. Name) identify collection items across snapshots even if the list reorders. " +
                 "Saved on disk (%LOCALAPPDATA%\\ExileApiMcp\\snapshots); only affects that folder.")]
    public static async Task<CallToolResult> MemorySnapshot(BridgeRegistry bridges,
        [Description("Snapshot name, e.g. 'baseline', 'mercenary-on'")] string name,
        [Description("Walker path: a memory object, or a collection of them")] string path,
        [Description("Start of the range, bytes")] int offset = 0,
        [Description("Bytes (default: the struct size the HUD reads; 256 for a single object without one)")] int size = 0,
        [Description("For collections: per-item properties to save and to match items by (first one is the key), e.g. [\"Name\",\"Affinity\"]")] string[]? labels = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        if (!Regex.IsMatch(name, @"^[\w.-]{1,64}$")) throw new McpException("name: letters, digits, '-', '_' or '.', up to 64 characters.");
        JObject snap;
        var (bridge, probe) = await bridges.CallAsync(game, "memory.collect", new JObject { ["path"] = path, ["offset"] = offset, ["size"] = size, ["limit"] = 2000, ["labels"] = new JArray(labels ?? []) }, ct);
        if (probe is JObject coll && coll["error"] == null && coll["items"] != null)
            snap = new JObject { ["kind"] = "collection", ["items"] = coll["items"], ["size"] = coll["size"], ["struct"] = coll["struct"] };
        else
        {
            var p = new JObject { ["path"] = path, ["offset"] = offset, ["size"] = size > 0 ? size : 256, ["classify"] = false };
            if (size <= 0)
            {
                var (_, layout) = await bridges.CallAsync(game, "memory.layout", new JObject { ["path"] = path }, ct);
                if (layout is JObject l && l["structSize"] != null) p["size"] = l["structSize"]!.Value<int>() - offset;
            }
            var (_, r) = await bridges.CallAsync(game, "memory.read", p, ct);
            if (r is not JObject ro || ro["error"] != null) return ToolResults.Json(r);
            snap = new JObject { ["kind"] = "object", ["address"] = ro["address"], ["size"] = ro["size"], ["data"] = ro["data"] };
        }
        snap["name"] = name;
        snap["path"] = path;
        snap["offset"] = offset;
        snap["game"] = bridge.Game;
        snap["labels"] = new JArray(labels ?? []);
        snap["takenAt"] = DateTimeOffset.Now.ToString("O");
        Directory.CreateDirectory(SnapshotDir);
        await File.WriteAllTextAsync(Path.Combine(SnapshotDir, name + ".json"), snap.ToString(Formatting.None), ct);
        var count = snap["items"] is JArray a ? $"{a.Count} items" : $"{snap["size"]} bytes";
        return ToolResults.Json(new JObject { ["saved"] = name, ["kind"] = snap["kind"], ["what"] = count, ["path"] = path, ["takenAt"] = snap["takenAt"] });
    }

    [McpServerTool(Name = "memory_compare", Title = "Compare memory snapshots", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Diff named snapshots in order (e.g. baseline, mercenary-on, mercenary-off): for each step, which items and " +
                 "bytes changed, the bits that flipped (numbered from the byte's offset), and how labels changed alongside. " +
                 "A bit that flips on the 'on' step and back on the 'off' step, while nothing else does, is the answer. " +
                 "Pass no names to list saved snapshots.")]
    public static CallToolResult MemoryCompare(
        [Description("Snapshot names in experiment order (2 or more); empty to list snapshots")] string[]? names = null)
    {
        Directory.CreateDirectory(SnapshotDir);
        if (names == null || names.Length == 0)
        {
            var list = new JArray(new DirectoryInfo(SnapshotDir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTime).Take(50)
                .Select(f => new JObject { ["name"] = Path.GetFileNameWithoutExtension(f.Name), ["savedAt"] = f.LastWriteTime.ToString("O") }));
            return ToolResults.Json(new JObject { ["snapshots"] = list });
        }
        if (names.Length < 2) throw new McpException("Pass 2 or more snapshot names, in order.");
        var snaps = names.Select(n =>
        {
            var file = Path.Combine(SnapshotDir, n + ".json");
            if (!File.Exists(file)) throw new McpException($"No snapshot '{n}'. Call memory_compare with no names to list them.");
            return JObject.Parse(File.ReadAllText(file));
        }).ToList();

        var sb = new StringBuilder();
        var steps = new JArray();
        int offset = snaps[0]["offset"]?.Value<int>() ?? 0;
        for (int s = 1; s < snaps.Count; s++)
        {
            var a = snaps[s - 1]; var b = snaps[s];
            var step = new JObject { ["from"] = a["name"], ["to"] = b["name"] };
            var changes = new JArray();
            foreach (var (key, da, db, la, lb) in Pairs(a, b))
            {
                var bytes = new JArray();
                for (int i = 0; i < Math.Min(da.Length, db.Length); i++)
                {
                    if (da[i] == db[i]) continue;
                    var x = da[i] ^ db[i];
                    bytes.Add(new JObject
                    {
                        ["off"] = offset + i, ["from"] = da[i].ToString("X2"), ["to"] = db[i].ToString("X2"),
                        ["bitsOn"] = new JArray(Enumerable.Range(0, 8).Where(k => (x >> k & 1) == 1 && (db[i] >> k & 1) == 1)),
                        ["bitsOff"] = new JArray(Enumerable.Range(0, 8).Where(k => (x >> k & 1) == 1 && (da[i] >> k & 1) == 1)),
                    });
                }
                var labelChanges = new JObject();
                if (la != null && lb != null)
                    foreach (var p in lb.Properties())
                        if (!JToken.DeepEquals(la[p.Name], p.Value)) labelChanges[p.Name] = $"{la[p.Name]} -> {p.Value}";
                if (bytes.Count == 0 && labelChanges.Count == 0) continue;
                var row = new JObject { ["item"] = key, ["bytes"] = bytes };
                if (labelChanges.Count > 0) row["labels"] = labelChanges;
                changes.Add(row);
            }
            step["changes"] = changes;
            steps.Add(step);

            sb.AppendLine($"{a["name"]} -> {b["name"]}: {(changes.Count == 0 ? "no change" : $"{changes.Count} item(s) changed")}");
            foreach (var c in changes.OfType<JObject>().Take(30))
            {
                sb.Append("  ").Append(c["item"]).Append(':');
                foreach (var bt in ((JArray)c["bytes"]!).OfType<JObject>().Take(12))
                {
                    sb.Append($" +{bt["off"]} {bt["from"]}->{bt["to"]}");
                    var on = (JArray)bt["bitsOn"]!; var off = (JArray)bt["bitsOff"]!;
                    if (on.Count > 0) sb.Append($" (on {string.Join(",", on)})");
                    if (off.Count > 0) sb.Append($" (off {string.Join(",", off)})");
                }
                if (c["labels"] is JObject lc) sb.Append("  | ").Append(string.Join("; ", lc.Properties().Select(p => $"{p.Name}: {p.Value}")));
                sb.AppendLine();
            }
        }
        sb.Append("Bits are numbered within each byte (0 = lowest); a field bit N lives in byte off + N/8 of the field.");
        return Result(sb.ToString(), new JObject { ["snapshots"] = new JArray(names), ["steps"] = steps });
    }

    /// <summary>Matching regions of two snapshots: by the first label (e.g. Name) for collections, else by index.</summary>
    private static IEnumerable<(string key, byte[] a, byte[] b, JObject? la, JObject? lb)> Pairs(JObject a, JObject b)
    {
        if (a["kind"]?.ToString() != "collection" || b["kind"]?.ToString() != "collection")
        {
            if (a["data"] != null && b["data"] != null)
                yield return (a["path"]?.ToString() ?? "object", Convert.FromBase64String(a["data"]!.ToString()), Convert.FromBase64String(b["data"]!.ToString()), null, null);
            yield break;
        }
        var keyLabel = (a["labels"] as JArray)?.FirstOrDefault()?.ToString();
        string Key(JToken item) => keyLabel != null && item["labels"]?[keyLabel] is { } k ? $"{keyLabel}={k} [{item["index"]}]" : $"[{item["index"]}]";
        // Key value plus its occurrence number, so duplicate keys (two tabs named "10") pair up in order.
        Dictionary<string, JToken> Keyed(JArray items)
        {
            var seen = new Dictionary<string, int>();
            var d = new Dictionary<string, JToken>();
            foreach (var it in items)
            {
                var k = keyLabel != null && it["labels"]?[keyLabel] is { } kv ? kv.ToString() : it["index"]!.ToString();
                seen[k] = seen.TryGetValue(k, out var n) ? n + 1 : 0;
                d[$"{k}#{seen[k]}"] = it;
            }
            return d;
        }
        var bItems = Keyed((JArray)b["items"]!);
        foreach (var (k, ia) in Keyed((JArray)a["items"]!))
            if (bItems.TryGetValue(k, out var ib))
                yield return (Key(ia), Convert.FromBase64String(ia["data"]!.ToString()), Convert.FromBase64String(ib["data"]!.ToString()),
                    ia["labels"] as JObject, ib["labels"] as JObject);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task<JObject> Collect(BridgeRegistry bridges, string? game, string path, int offset, int size, int limit, string[] labels, CancellationToken ct)
    {
        var (_, r) = await bridges.CallAsync(game, "memory.collect", new JObject
        {
            ["path"] = path, ["offset"] = offset, ["size"] = size, ["limit"] = limit, ["labels"] = new JArray(labels),
        }, ct);
        if (r is not JObject o || (o["error"] == null && o["items"] == null))
            throw new McpException("This HUD's bridge plugin has no memory.collect yet: update What's an AI Bridge and restart the HUD.");
        if (o["error"] != null) throw new McpException($"{o["error"]}: {o["message"]}");
        return o;
    }

    private static CallToolResult Result(string outline, JObject full) => new()
    {
        Content = [new TextContentBlock { Text = outline }],
        StructuredContent = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(full.ToString(Formatting.None)),
    };
}
