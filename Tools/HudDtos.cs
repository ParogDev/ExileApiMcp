using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the HUD dev tools: the offline API reference (hud_find_types, hud_type, hud_property_map,
// hud_api_diff, hud_plugin_lint), the dev loop (hud_plugins, hud_log) and the runtime layout (hud_runtime_layout).
// Fields the tools always write, even as null, are marked Never; a field that is sometimes absent and sometimes null is
// a JsonElement skipped only when absent (WhenWritingDefault), so both stay as they were on the wire.

// ── hud_find_types / hud_type ─────────────────────────────────────

/// <summary>A type (query search) or a member (member search) found in the HUD's assemblies.</summary>
public sealed class HudTypeMatch
{
    public string Game { get; set; } = "";
    /// <summary>Full name of the type (declaring type for member hits).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Type { get; set; }
    /// <summary>Type hits: class | struct | enum | interface | ...</summary>
    public string? Kind { get; set; }
    public string? Assembly { get; set; }
    public string? Base { get; set; }
    public bool? NonPublic { get; set; }
    /// <summary>Member hits: the member's name, kind (field | property | method | ...) and declaration.</summary>
    public string? Member { get; set; }
    public string? MemberKind { get; set; }
    public string? Declaration { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_find_types.</summary>
public sealed class HudFindTypesResult
{
    public int Total { get; set; }
    public List<HudTypeMatch> Results { get; set; } = [];
    public string? Truncated { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HudTypeField
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>[FieldOffset] as hex: "0x10".</summary>
    public string? Offset { get; set; }
    /// <summary>Set when the offset is implausible (a GameOffsets2 decoy).</summary>
    public string? Suspect { get; set; }
    public bool? Static { get; set; }
    public JsonElement? Const { get; set; }
    /// <summary>Non-public: private | internal | protected | ...</summary>
    public string? Access { get; set; }
    /// <summary>Inherited members: the base type declaring it.</summary>
    public string? DeclaringType { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HudTypeProperty
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>"get", "get;set", "get;private set"...</summary>
    public string? Accessors { get; set; }
    public bool? Static { get; set; }
    public string? Access { get; set; }
    public string? Indexer { get; set; }
    public string? DeclaringType { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HudTypeMethod
{
    public string Signature { get; set; } = "";
    public bool? Static { get; set; }
    public bool? Abstract { get; set; }
    public bool? Virtual { get; set; }
    public string? Access { get; set; }
    public string? DeclaringType { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One HUD type with its members (what hud_type shows; lists are omitted when empty).</summary>
public sealed class HudTypeDetail
{
    public string Game { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Type { get; set; }
    public string? Kind { get; set; }
    public string? Assembly { get; set; }
    public string? Base { get; set; }
    public bool? NonPublic { get; set; }
    public List<string>? BaseChain { get; set; }
    public List<string>? Interfaces { get; set; }
    /// <summary>Enums: name -> value.</summary>
    public Dictionary<string, JsonElement>? Values { get; set; }
    public List<HudTypeField>? Fields { get; set; }
    public List<HudTypeProperty>? Properties { get; set; }
    /// <summary>Constructors ("new T(...)") and methods.</summary>
    public List<HudTypeMethod>? Methods { get; set; }
    public List<string>? NestedTypes { get; set; }
    public string? Truncated { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_type: the type in each HUD that has it.</summary>
public sealed class HudTypeResult
{
    public List<HudTypeDetail> Types { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// ── hud_property_map ──────────────────────────────────────────────

/// <summary>What one HUD property reads: an offsets struct field (struct, field, offset) or Read&lt;T&gt;(Address + n).</summary>
public sealed class PropertyMapRow
{
    public string Game { get; set; } = "";
    public string Type { get; set; } = "";
    public string Property { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Struct { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Field { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Offset { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Hex { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? ValueType { get; set; }
    /// <summary>How the getter reads it: struct field, read-at, ... or none.</summary>
    public string Via { get; set; } = "";
    /// <summary>via none: what the getter does instead.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Why { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_property_map: matching rows (total counts all, rows up to max). trace=true: the getter's IL ops in trace.</summary>
public sealed class PropertyMapResult
{
    public int Total { get; set; }
    public List<PropertyMapRow> Rows { get; set; } = [];
    /// <summary>trace=true only: the getter's IL ops and what each resolved to, per HUD.</summary>
    public List<string>? Trace { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// ── hud_runtime_layout ────────────────────────────────────────────

public sealed class RuntimeLayoutField
{
    /// <summary>Nested game structs flattened as a.b.</summary>
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>The offset the CLR uses, "0x10".</summary>
    public string Offset { get; set; } = "";
    public int Size { get; set; }
    /// <summary>The [FieldOffset] in the DLL metadata, null when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? MetadataOffset { get; set; }
    /// <summary>true when the metadata offset differs from the real one, else null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool? Decoy { get; set; }
    /// <summary>The field's value in the object's cached struct (with path), else null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Value { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A public property of the object matched to the struct field holding its value.</summary>
public sealed class RuntimeLayoutProperty
{
    public string Property { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>Several fields hold that value right now: "name@0xNN".</summary>
    public List<string>? Ambiguous { get; set; }
    public string? Field { get; set; }
    public string? Offset { get; set; }
    /// <summary>"same" or "changed since cached (live value)".</summary>
    public string? MemoryNow { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_runtime_layout: the CLR's real layout of an offsets struct, and (with path) property -> field.</summary>
public sealed class RuntimeLayoutResult
{
    public string Struct { get; set; } = "";
    /// <summary>"0x398".</summary>
    public string Size { get; set; } = "";
    public List<RuntimeLayoutField> Fields { get; set; } = [];
    public List<string>? Unmeasured { get; set; }
    public int? DecoyOffsets { get; set; }
    public string? Path { get; set; }
    public string? Object { get; set; }
    /// <summary>Where the cached struct was found, e.g. "Life._life.Value".</summary>
    public string? StructVia { get; set; }
    /// <summary>"N% of bytes".</summary>
    public string? StructMatchesMemoryAtAddress { get; set; }
    public List<RuntimeLayoutProperty>? Properties { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// ── hud_plugins / hud_log ─────────────────────────────────────────

/// <summary>The HUD's latest run from its log: {found: false} when there is none.</summary>
public sealed class HudRunInfo
{
    public bool? Found { get; set; }
    public string? StartedAt { get; set; }
    /// <summary>ISO time, or null while it runs (or after a crash: the log just stops). Absent with found: false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement ClosedAt { get; set; }
    public bool? Running { get; set; }
    /// <summary>ISO time of the last log line, or null. Absent with found: false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement LastLogAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HudErrorsTxtInfo
{
    public string Modified { get; set; } = "";
    /// <summary>Older than the last successful compile: left over, safe to delete.</summary>
    public bool Stale { get; set; }
    public string? Note { get; set; }
    public string? Text { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HudSourcePluginStatus
{
    public string Folder { get; set; } = "";
    /// <summary>The .csproj name, null when the folder has none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Project { get; set; }
    /// <summary>loaded | cached | failed | not-seen.</summary>
    public string Status { get; set; } = "";
    public string? At { get; set; }
    public string? Note { get; set; }
    /// <summary>The compiler output for failures.</summary>
    public string? Error { get; set; }
    public int? RuntimeErrors { get; set; }
    public int? Warnings { get; set; }
    public HudErrorsTxtInfo? ErrorsTxt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HudPluginsInstall
{
    public string Game { get; set; } = "";
    public string HudRoot { get; set; } = "";
    public HudRunInfo Run { get; set; } = new();
    public List<HudSourcePluginStatus> Plugins { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_plugins: per HUD install, each source plugin's compile status in the latest run.</summary>
public sealed class HudPluginsResult
{
    public List<HudPluginsInstall> Huds { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HudLogCounts
{
    public int Error { get; set; }
    public int Warning { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A distinct log message (repeats collapsed: count and the first time).</summary>
public sealed class HudLogEntry
{
    /// <summary>VRB | DBG | INF | WRN | ERR | FTL.</summary>
    public string Level { get; set; } = "";
    public string At { get; set; } = "";
    public string Message { get; set; } = "";
    public int? Count { get; set; }
    public string? FirstAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HudLogInstall
{
    public string Game { get; set; } = "";
    public HudRunInfo Run { get; set; } = new();
    /// <summary>Errors and warnings in the whole run.</summary>
    public HudLogCounts Counts { get; set; } = new();
    public int Matched { get; set; }
    public int Distinct { get; set; }
    public List<HudLogEntry> Entries { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_log: per HUD install, the latest run's log, newest last.</summary>
public sealed class HudLogResult
{
    public List<HudLogInstall> Huds { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// ── hud_api_diff ──────────────────────────────────────────────────

public sealed class HudApiChange
{
    /// <summary>type removed | member removed | signature changed | offset moved | ...</summary>
    public string Kind { get; set; } = "";
    public string Type { get; set; } = "";
    public string Detail { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Where our plugins or the MCP's knowledge use a changed name.</summary>
public sealed class HudApiImpact
{
    public string Name { get; set; } = "";
    public string Where { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HudApiDiffGame
{
    /// <summary>"game:build".</summary>
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public List<HudApiChange> Breaking { get; set; } = [];
    /// <summary>Non-breaking changes (additions=true lists them in the text).</summary>
    public int Additions { get; set; }
    public List<HudApiImpact> Impact { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_api_diff: the diff per game that has two snapshots (empty for list=true or a first snapshot).</summary>
public sealed class ApiDiffResult
{
    public HudApiDiffGame? Poe1 { get; set; }
    public HudApiDiffGame? Poe2 { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// ── hud_plugin_lint ───────────────────────────────────────────────

/// <summary>An expensive HUD call site on a Tick/Render path.</summary>
public sealed class HudLintFinding
{
    public string Method { get; set; } = "";
    public string Call { get; set; } = "";
    public int Count { get; set; }
    /// <summary>In a loop or per-item lambda: cost x entity count.</summary>
    public bool InLoop { get; set; }
    /// <summary>Measured cost of the first call per entity per frame; 0 = allocates.</summary>
    public int CostNs { get; set; }
    public string Advice { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HudLintPlugin
{
    public string Game { get; set; } = "";
    public string Plugin { get; set; } = "";
    public List<HudLintFinding> Findings { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_plugin_lint: per compiled source plugin, its expensive call sites.</summary>
public sealed class PluginLintResult
{
    public List<HudLintPlugin> Plugins { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
