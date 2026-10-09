using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the memory tools (memory_layout, memory_read, memory_where, watch_memory). They mirror the bridge's
// MemoryInspector replies and ui-src/src/memory/types.ts, which the memory view app parses: same names, same nesting.
// Optional fields are omitted when empty; a field the bridge always writes, even as null, is marked Never so the null
// stays on the wire. Unknown fields pass through in Extra.

/// <summary>What an 8-byte value is, from the bridge's classifier: shared by fields, candidates, read slots and memory_where.</summary>
public abstract class MemoryClassified
{
    /// <summary>zero | module | vtable | heap | data-row | float | int | text | bad-pointer (candidates: std::vector | self | ...).</summary>
    public string? Kind { get; set; }
    /// <summary>Module pointers: module name, section, RVA and the Ghidra address (imageBase + rva).</summary>
    public string? Module { get; set; }
    public string? Section { get; set; }
    public string? Rva { get; set; }
    public string? Ghidra { get; set; }
    /// <summary>vtable with RTTI: the class name.</summary>
    public string? Rtti { get; set; }
    /// <summary>First virtual method of a vtable (Ghidra address).</summary>
    public string? FirstMethod { get; set; }
    /// <summary>Heap pointers: what the target looks like, "object (vtable 0x...)", "text" or a data row.</summary>
    public string? Points { get; set; }
    public string? Text { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MemoryHexRow
{
    public int Off { get; set; }
    /// <summary>"AA BB CC ..."</summary>
    public string Bytes { get; set; } = "";
    public string Ascii { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MemoryModuleInfo
{
    public string? Name { get; set; }
    public string? Base { get; set; }
    public string? Size { get; set; }
    public string? ImageBase { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One field of the HUD's struct laid over live memory.</summary>
public sealed class MemoryLayoutField : MemoryClassified
{
    public int Off { get; set; }
    public int Size { get; set; }
    /// <summary>Dotted for nested structs: "Health.Max".</summary>
    public string Name { get; set; } = "";
    /// <summary>.NET type name: Int32, Single, Int64, Byte...</summary>
    public string Type { get; set; } = "";
    public string? Bytes { get; set; }
    /// <summary>The decoded value: a number for small integers, else text (floats, hex pointers, vectors).</summary>
    public JsonElement? Value { get; set; }
    /// <summary>ok | unusual | suspicious | invalid | unread.</summary>
    public string? Check { get; set; }
    public string? Why { get; set; }
    /// <summary>Set bits of flag-like integer fields.</summary>
    public List<int>? Bits { get; set; }
    /// <summary>HUD properties whose getters read this offset (hud_property_map), added by the server.</summary>
    public List<string>? Hud { get; set; }
}

public sealed class MemoryLayoutGap
{
    public int Off { get; set; }
    public int Size { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Structure-looking slot inside an unmapped range.</summary>
public sealed class MemoryLayoutCandidate : MemoryClassified
{
    public int Off { get; set; }
    public int Size { get; set; }
    public string? Value { get; set; }
    public string? Detail { get; set; }
    /// <summary>std::vector: the First pointer.</summary>
    public string? First { get; set; }
}

/// <summary>memory_layout: the HUD's struct for an object overlaid on live memory.</summary>
public sealed class MemoryLayoutResult
{
    public string Address { get; set; } = "";
    public string? Struct { get; set; }
    public int StructSize { get; set; }
    /// <summary>Where the struct came from, e.g. "Life._life (the struct the HUD reads)".</summary>
    public string? Source { get; set; }
    /// <summary>The HUD type of the object (null when overlaying a type on an address).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Object { get; set; }
    public string? Game { get; set; }
    public string? Summary { get; set; }
    public List<MemoryLayoutField> Fields { get; set; } = [];
    public List<MemoryLayoutGap> Gaps { get; set; } = [];
    public List<MemoryLayoutCandidate> Candidates { get; set; } = [];
    public List<MemoryHexRow> Hex { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One classified 8-byte slot of memory_read.</summary>
public sealed class MemoryReadSlot : MemoryClassified
{
    public int Off { get; set; }
    public string Hex { get; set; } = "";
    /// <summary>int / float / bad-pointer: the decoded value(s) as text.</summary>
    public string? Value { get; set; }
    public List<int>? Bits { get; set; }
}

/// <summary>memory_read: a region as classified slots plus a hex dump.</summary>
public sealed class MemoryReadResult
{
    public string Address { get; set; } = "";
    public int Size { get; set; }
    /// <summary>The path or address the read started from.</summary>
    public string? Origin { get; set; }
    public string? Region { get; set; }
    /// <summary>Start before offset was added (only with an offset).</summary>
    public string? Base { get; set; }
    public MemoryModuleInfo? Module { get; set; }
    public List<MemoryReadSlot> Slots { get; set; } = [];
    public List<MemoryHexRow> Hex { get; set; } = [];
    public string? Game { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>memory_where: what an address is (classified like a slot) and the region and module around it.</summary>
public sealed class MemoryWhereResult : MemoryClassified
{
    public string Address { get; set; } = "";
    public string? Hex { get; set; }
    public string? Value { get; set; }
    public List<int>? Bits { get; set; }
    public string? Region { get; set; }
    public MemoryModuleInfo? ModuleInfo { get; set; }
}

/// <summary>A run of adjacent bytes that changed while watch_memory sampled.</summary>
public sealed class MemoryChangedRange
{
    public int Off { get; set; }
    public int Size { get; set; }
    /// <summary>Overlapping struct field names, "(unmapped)" when a struct is known, null for raw reads.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Field { get; set; }
    public int Changes { get; set; }
    public string First { get; set; } = "";
    public string Last { get; set; } = "";
    /// <summary>Bits that flipped (absent when more than 16).</summary>
    public List<int>? BitsFlipped { get; set; }
    /// <summary>"field Affinity (+63)" or "+300": what bitsFlipped counts from (null when more than 16 bits flipped).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? BitsRelativeTo { get; set; }
    public long FirstChangeAtMs { get; set; }
    public long LastChangeAtMs { get; set; }
    public bool? Noisy { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>watch_memory: which bytes changed while sampling a region.</summary>
public sealed class WatchMemoryResult
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Address { get; set; }
    public int Size { get; set; }
    public int Samples { get; set; }
    public long DurationMs { get; set; }
    /// <summary>The HUD struct whose field names label the ranges (null for raw addresses).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Struct { get; set; }
    public List<MemoryChangedRange> ChangedRanges { get; set; } = [];
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
