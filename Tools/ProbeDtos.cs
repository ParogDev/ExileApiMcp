using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the probing tools (memory_correlate, memory_population, memory_snapshot, memory_compare), mirroring
// what they return today and ui-src/src/memory/types.ts. Fields the tools always write, even as null, are marked Never.

/// <summary>Where a label's own value is stored in every item.</summary>
public sealed class ProbeStoredAt
{
    public int Offset { get; set; }
    /// <summary>"8-bit" | "16-bit" | "32-bit" | "64-bit".</summary>
    public string Type { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ProbeCounterexampleItem
{
    public int Index { get; set; }
    /// <summary>The item's labels (property path -> value).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public Dictionary<string, JsonElement>? Labels { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A bit equal to a feature of a label (0 counterexamples), or a near miss (1-2).</summary>
public sealed class ProbeExplainedBit
{
    /// <summary>Absolute offset of the byte.</summary>
    public int Byte { get; set; }
    public int Bit { get; set; }
    /// <summary>"Affinity != 0", "NOT TabType == 0", "Affinity bit 5", "a function of TabType".</summary>
    [JsonPropertyName("equals")] public string EqualTo { get; set; } = "";
    public string? Evidence { get; set; }
    /// <summary>Categorical: the label values the bit is set for.</summary>
    public string? SetFor { get; set; }
    public int Counterexamples { get; set; }
    public List<ProbeCounterexampleItem>? CounterexampleItems { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>What one label explains in the byte range.</summary>
public sealed class ProbeLabelFinding
{
    public string Label { get; set; } = "";
    public int DistinctValues { get; set; }
    public List<ProbeExplainedBit> BitsExplained { get; set; } = [];
    public List<ProbeExplainedBit>? NearMisses { get; set; }
    public List<ProbeStoredAt> StoredAt { get; set; } = [];
    public string? TooLittleEvidence { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>memory_correlate: which bits each known property explains across a population.</summary>
public sealed class MemoryCorrelateResult
{
    public string Path { get; set; } = "";
    public int Offset { get; set; }
    public int Size { get; set; }
    public int Items { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Struct { get; set; }
    public List<ProbeLabelFinding> Findings { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ProbePopulationItem
{
    public int Index { get; set; }
    public string Address { get; set; } = "";
    /// <summary>Requested labels (property path -> value); null when none were asked for.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public Dictionary<string, JsonElement>? Labels { get; set; }
    /// <summary>"AA BB ..."</summary>
    public string Hex { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>memory_population: the same byte range from every item of a collection.</summary>
public sealed class MemoryPopulationResult
{
    public string Path { get; set; } = "";
    public int Offset { get; set; }
    public int Size { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Struct { get; set; }
    public int Count { get; set; }
    public List<ProbePopulationItem> Items { get; set; } = [];
    public string? Truncated { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>memory_snapshot: what was saved.</summary>
public sealed class MemorySnapshotResult
{
    public string Saved { get; set; } = "";
    /// <summary>collection | object.</summary>
    public string Kind { get; set; } = "";
    /// <summary>"N items" or "N bytes".</summary>
    public string What { get; set; } = "";
    public string Path { get; set; } = "";
    public string TakenAt { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Metadata of one snapshot in a comparison (null where an older snapshot lacks it).</summary>
public sealed class ProbeSnapshotInfo
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Name { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Path { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Kind { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Struct { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Offset { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Size { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public List<string>? Labels { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Game { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? TakenAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ProbeByteChange
{
    public int Off { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>Bits within the byte (0 = lowest) that went 0 -> 1 / 1 -> 0.</summary>
    public List<int> BitsOn { get; set; } = [];
    public List<int> BitsOff { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ProbeItemChange
{
    /// <summary>"Name=19 [21]" for collections, the path for single objects.</summary>
    public string Item { get; set; } = "";
    public List<ProbeByteChange> Bytes { get; set; } = [];
    /// <summary>Label -> "a -> b".</summary>
    public Dictionary<string, string>? Labels { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ProbeCompareStep
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public List<ProbeItemChange> Changes { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// memory_compare. With names: snapshots (the names, in order), snapshotInfo and steps. With no names: the saved
/// snapshots as {name, savedAt} objects (newest first), and nothing else.
/// </summary>
public sealed class MemoryCompareResult
{
    /// <summary>Names in experiment order (strings), or the listing ({name, savedAt} objects).</summary>
    public List<JsonElement> Snapshots { get; set; } = [];
    public List<ProbeSnapshotInfo>? SnapshotInfo { get; set; }
    public List<ProbeCompareStep>? Steps { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
