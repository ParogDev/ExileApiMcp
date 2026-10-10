using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Tools;

/// <summary>
/// observe_series: one unit's values over time, rebuilt from the observer journal (every logged change carries old and
/// new), and what it is: a toggle, a few states, a counter, a timer or a continuous value. Then the "memory_correlate
/// over time" step: which other units change at the same moments and hold the same value, the same step, or a fixed
/// multiple of it. An unmapped server offset whose value always equals the life layer's CurHP is CurHP's server copy.
/// </summary>
public static partial class ObserveTools
{
    [McpServerTool(Name = "observe_series", Title = "One unit's values over time, and what moves with it", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(SeriesResult), IconSource = IconSet.TimelineLight)]
    [Description("The value series of one observer unit (layer + unit, e.g. layer=server unit=0x2368), rebuilt from the journal on disk: " +
                 "how many changes, distinct values and the most common ones, the typical step and interval, and its shape (toggle, " +
                 "states, counter, timer, continuous, text). Then relations: other numeric units that changed within windowMs of it " +
                 "and held the same value, the same step, or a constant ratio of its step - an unmapped offset equal to a known stat or " +
                 "life value is named by it. Struct offsets are numeric when 4 or 8 bytes changed (i32/i64). Read-only.")]
    public static Task<CallToolResult> ObserveSeries(BridgeRegistry bridges,
        [Description("Layer of the unit (default server)")] string layer = "server",
        [Description("Unit: a struct offset (0x2368), property, dictionary key or each-unit (MainInventory1.Inventory.Hash)")] string unit = "",
        [Description("Window for relations, ms each side (10-10000, default 300)")] int windowMs = 300,
        [Description("Most recent journal lines to read (1000-500000, default 200000)")] int scan = 200_000,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null)
    {
        if (string.IsNullOrWhiteSpace(unit)) throw new McpException("unit is required (see observe_layer_map for a layer's units).");
        var bridge = bridges.Resolve(game);
        var path = Path.Combine(bridge.BridgeDir, "observe", "journal.jsonl");
        if (!File.Exists(path)) throw new McpException("No observer journal yet: turn observation on (observe action=start).");
        var events = ReadJournalTail(path, Math.Clamp(scan, 1000, 500_000));
        var win = TimeSpan.FromMilliseconds(Math.Clamp(windowMs, 10, 10_000));
        var hits = events.Where(e => e.Kind == "layer" && e.Layer == layer && string.Equals(e.Unit, unit, StringComparison.OrdinalIgnoreCase)).ToList();
        var r = new SeriesResult { Layer = layer, Unit = unit, JournalEvents = events.Count, Changes = hits.Count, WindowMs = (int)win.TotalMilliseconds };
        if (hits.Count == 0)
            return Task.FromResult(Typed(r, $"No logged change of {layer} {unit} in the last {events.Count} journal events (a noisy unit is counted, " +
                                            "not logged: see observe_layer_map; a layer only logs while observing is on)."));
        r.Name = hits[^1].Name;

        // Struct units: one width for the whole series (i32 when every change fits an aligned 4, else the aligned i64).
        var wide = hits.Any(h => h.I32 == null && h.I64 != null);
        var nums = hits.Select(h => (h, v: Num(h, wide))).ToList();
        r.Numeric = nums.Count(x => x.v != null) * 10 >= nums.Count * 8;
        if (r.Numeric) nums = nums.Where(x => x.v != null).ToList();
        r.Points = nums.TakeLast(200).Select(x => new SeriesPoint { At = x.h.At, Seq = x.h.Seq, Value = x.h.New, Number = x.v }).ToList();
        // Struct changes log only the bytes that changed (1 byte one time, 2 the next): count the aligned number instead.
        var values = nums.Select(x => SideText(x.h, true, wide) ?? x.h.New ?? "(removed)").ToList();
        r.Distinct = values.Distinct().Count();
        r.TopValues = values.GroupBy(v => v).OrderByDescending(g => g.Count()).Take(10).Select(g => new SeriesValueCount { Value = g.Key, Count = g.Count() }).ToList();
        var gaps = hits.Zip(hits.Skip(1), (a, b) => (b.At - a.At).TotalMilliseconds).Where(ms => ms > 0).OrderBy(ms => ms).ToList();
        if (gaps.Count > 0)
        {
            r.IntervalMedianMs = Math.Round(gaps[gaps.Count / 2], 1);
            var mean = gaps.Average();
            r.IntervalRegular = gaps.Count >= 4 && Math.Sqrt(gaps.Average(g => (g - mean) * (g - mean))) / mean < 0.2;
        }
        List<double> steps = [];
        if (r.Numeric)
        {
            var vs = nums.Select(x => x.v!.Value).ToList();
            r.Min = vs.Min(); r.Max = vs.Max();
            steps = nums.Select(x => x.v!.Value - (NumOld(x.h, wide) ?? double.NaN)).Where(d => !double.IsNaN(d)).ToList();
            if (steps.Count > 0) r.StepTypical = steps.GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key;
        }
        r.Shape = !r.Numeric ? (r.Distinct <= 8 ? "states" : "text")
            : r.Distinct <= 2 ? "toggle"
            : steps.Count >= 3 && steps.All(d => d == steps[0]) && steps[0] != 0 ? (r.IntervalRegular == true ? "timer" : "counter")
            : r.Distinct <= 8 ? "states"
            : "continuous";

        // Relations: for each change, every other unit's change nearest in time within the window (a mirror changes a
        // few ms apart, and the window may hold its previous change too).
        var self = hits[0].Key();
        var tally = new Dictionary<string, Relation>();
        var nearest = new Dictionary<string, (ObserveEvent e, double dt)>();
        foreach (var (h, v) in nums)
        {
            nearest.Clear();
            foreach (var e in Near(events, h.At, win))
            {
                if (e.Kind != "layer" || e.Seq == h.Seq) continue;
                var k = e.Key();
                if (k == self) continue;
                var dt = Math.Abs((e.At - h.At).TotalMilliseconds);
                if (!nearest.TryGetValue(k, out var b) || dt < b.dt) nearest[k] = (e, dt);
            }
            foreach (var (k, (e, _)) in nearest)
            {
                var rel = tally.TryGetValue(k, out var x) ? x : tally[k] = new Relation { Event = k, Layer = e.Layer, Unit = e.Unit, Name = e.Name };
                rel.Together++;
                if (v is not { } tv || Num(e, wide) is not { } ev) continue;
                rel.Numeric++;
                // Compared as text: i64 values don't survive a double.
                if (SideText(h, true, wide) is { } a && a == SideText(e, true, wide)) rel.Equal++;
                var ts = tv - (NumOld(h, wide) ?? double.NaN); var es = ev - (NumOld(e, wide) ?? double.NaN);
                if (double.IsNaN(ts) || double.IsNaN(es) || es == 0) continue;
                if (Math.Abs(ts - es) < 1e-9) rel.SameStep++;
                rel.Ratios.Add(ts / es);
            }
        }
        r.Relations = tally.Values
            .Where(x => x.Together * 2 >= nums.Count || x.Equal * 2 >= nums.Count)
            .Select(x => x.Finish(nums.Count))
            .OrderByDescending(x => x.Strength).ThenByDescending(x => x.Together).Take(15).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"{layer} {unit}{(r.Name != null ? $" ({r.Name})" : layer == "server" ? " (unmapped)" : "")}: {hits.Count} changes, {r.Distinct} distinct values, shape {r.Shape}"
                      + (r.Numeric ? $", range {r.Min}..{r.Max}" + (r.StepTypical is { } st ? $", typical step {st:+0.###;-0.###}" : "") : ""));
        if (r.IntervalMedianMs is { } im) sb.AppendLine($"  every ~{im} ms (median){(r.IntervalRegular == true ? ", regular: a timer or tick" : "")}");
        sb.AppendLine("  most common: " + string.Join(", ", r.TopValues.Take(6).Select(t => $"{t.Value} x{t.Count}")));
        if (r.Relations.Count == 0) sb.AppendLine($"  nothing changes with it consistently within {r.WindowMs} ms.");
        foreach (var x in r.Relations)
            sb.AppendLine($"  {x.Together}/{nums.Count} together  {x.Event}" + (x.Relation != null ? $"  -> {x.Relation} ({x.Holds}/{x.Numeric})" : ""));
        sb.Append("Last: " + string.Join(", ", hits.TakeLast(8).Select(h => $"#{h.Seq} {h.Old}->{h.New}")));
        return Task.FromResult(Typed(r, sb.ToString()));
    }

    /// <summary>The new (old) value as a number: i32/i64 for struct offsets, else the value text.</summary>
    private static double? Num(ObserveEvent e, bool wide) => Side(e, after: true, wide);
    private static double? NumOld(ObserveEvent e, bool wide) => Side(e, after: false, wide);

    private static double? Side(ObserveEvent e, bool after, bool wide) =>
        SideText(e, after, wide) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>One side of a change as written in the journal (exact, unlike its double).</summary>
    private static string? SideText(ObserveEvent e, bool after, bool wide)
    {
        if (wide && e.Mode == "struct" && e.I64 == null) return null;   // an older journal line without the aligned i64: not comparable
        var pair = wide ? e.I64 : e.I32 ?? e.I64;
        if (pair != null)
        {
            var parts = pair.Split("->", StringSplitOptions.TrimEntries);
            return parts.Length == 2 ? parts[after ? 1 : 0] : null;
        }
        if (e.Mode == "struct") return null;   // a longer range of bytes: not one number
        return after ? e.New : e.Old;
    }

    private sealed class Relation
    {
        public string Event = "";
        public string? Layer, Unit, Name;
        public int Together, Numeric, Equal, SameStep;
        public List<double> Ratios = [];

        public SeriesRelation Finish(int changes)
        {
            string? relation = null; var holds = 0;
            if (Numeric > 0 && Equal * 10 >= Numeric * 8) { relation = "same value"; holds = Equal; }
            else if (Numeric > 0 && SameStep * 10 >= Numeric * 8) { relation = "same step"; holds = SameStep; }
            else if (Ratios.Count >= 3)
            {
                var k = Ratios.GroupBy(x => Math.Round(x, 3)).OrderByDescending(g => g.Count()).First();
                if (k.Count() * 10 >= Ratios.Count * 8) { relation = $"step x{k.Key.ToString(CultureInfo.InvariantCulture)}"; holds = k.Count(); }
            }
            var strength = (double)Together / changes * (relation != null ? 1 + (double)holds / Math.Max(1, Numeric) : 1);
            return new SeriesRelation { Event = Event, Layer = Layer, Unit = Unit, Name = Name, Together = Together, Numeric = Numeric, Relation = relation, Holds = holds, Strength = Math.Round(strength, 3) };
        }
    }
}

public sealed class SeriesResult
{
    public string Layer { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>The HUD's name for the unit (struct offsets), null when unmapped.</summary>
    public string? Name { get; set; }
    public int JournalEvents { get; set; }
    public int Changes { get; set; }
    public int WindowMs { get; set; }
    /// <summary>Every logged value is a number (struct: 4 or 8 bytes changed).</summary>
    public bool Numeric { get; set; }
    public int Distinct { get; set; }
    /// <summary>toggle | states | counter | timer | continuous | text.</summary>
    public string Shape { get; set; } = "";
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>The most common change (new - old).</summary>
    public double? StepTypical { get; set; }
    public double? IntervalMedianMs { get; set; }
    /// <summary>The intervals vary by under 20%: a timer or tick, not an event.</summary>
    public bool? IntervalRegular { get; set; }
    public List<SeriesValueCount> TopValues { get; set; } = [];
    /// <summary>Other units that changed with it (at least half the time, or equal at least half the time).</summary>
    public List<SeriesRelation> Relations { get; set; } = [];
    /// <summary>The last 200 changes.</summary>
    public List<SeriesPoint> Points { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SeriesValueCount
{
    public string Value { get; set; } = "";
    public int Count { get; set; }
}

public sealed class SeriesPoint
{
    public DateTimeOffset At { get; set; }
    public long Seq { get; set; }
    public string? Value { get; set; }
    public double? Number { get; set; }
}

public sealed class SeriesRelation
{
    /// <summary>The other unit, as observe_timeline names companions.</summary>
    public string Event { get; set; } = "";
    public string? Layer { get; set; }
    public string? Unit { get; set; }
    public string? Name { get; set; }
    /// <summary>Changes of this unit with the other one within the window.</summary>
    public int Together { get; set; }
    /// <summary>Of those, how many had numbers on both sides.</summary>
    public int Numeric { get; set; }
    /// <summary>same value | same step | step xK, when it holds in 80% of the numeric pairs; null otherwise.</summary>
    public string? Relation { get; set; }
    public int Holds { get; set; }
    public double Strength { get; set; }
}
