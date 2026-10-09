using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the static code tools (find_field_access, code_struct_layout), from the Ghidra copy of the exe.
// find_field_access mirrors FieldAccessResult in ui-src/src/memory/types.ts (the memory view's code panel parses it).
// Decompiled C stays text: excerpts are free-form.

public sealed class CodeFieldAccessTarget
{
    public int Offset { get; set; }
    public string Hex { get; set; } = "";
    public int? Bit { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A known field used to fingerprint the struct's code; skipped ones were too common to identify anything.</summary>
public sealed class CodeFieldAnchor
{
    public int Offset { get; set; }
    public string Field { get; set; } = "";
    public int? Accesses { get; set; }
    public string? Skipped { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One function of the struct that touches the target offset.</summary>
public sealed class CodeAccessFunction
{
    /// <summary>"FUN_141d4d020", or "?" when Ghidra has no function at the address.</summary>
    public string Function { get; set; } = "";
    public int Accesses { get; set; }
    /// <summary>Comma-joined kinds: "read,write".</summary>
    public string Kinds { get; set; } = "";
    /// <summary>How many anchor fields the same base register also touches.</summary>
    public int KnownFields { get; set; }
    public bool BitMatch { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One instruction touching the target offset.</summary>
public sealed class CodeFieldAccess
{
    public string Function { get; set; } = "";
    /// <summary>Hex without 0x: "141d4d1bf".</summary>
    public string Address { get; set; } = "";
    /// <summary>"TEST byte ptr [RDI + 0x3d], 0x40"</summary>
    public string Instruction { get; set; } = "";
    /// <summary>read | write | bit-test | set-bits | clear-bits | address-of.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Bytes; 0 = unknown (address-of).</summary>
    public int Width { get; set; }
    public string Base { get; set; } = "";
    /// <summary>Bits a mask / BT touches (for clear-bits: the bits cleared).</summary>
    public List<int>? Bits { get; set; }
    public bool? MatchesBit { get; set; }
    /// <summary>"+63 Affinity"</summary>
    public List<string> KnownFieldsAlsoAccessed { get; set; } = [];
    /// <summary>high | medium | low.</summary>
    public string Confidence { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A decompiled function: the lines around the target offset ("  ..." between runs), or the error.</summary>
public sealed class CodeDecompiledExcerpt
{
    public string Function { get; set; } = "";
    public string? Signature { get; set; }
    public string? Excerpt { get; set; }
    public int? LineCount { get; set; }
    public string? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>find_field_access: the struct's code that reads, writes or bit-tests a field.</summary>
public sealed class FieldAccessResult
{
    public string? Game { get; set; }
    public string Program { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Struct { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Path { get; set; }
    public CodeFieldAccessTarget Target { get; set; } = new();
    public List<CodeFieldAnchor> Anchors { get; set; } = [];
    public int MinKnown { get; set; }
    public int ProgramWideAccesses { get; set; }
    public List<CodeAccessFunction> Functions { get; set; } = [];
    public List<CodeFieldAccess> Accesses { get; set; } = [];
    public List<CodeDecompiledExcerpt> Decompiled { get; set; } = [];
    public string? Unanchored { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One offset a function accesses through the struct pointer, compared with the HUD's struct.</summary>
public sealed class CodeStructOffset
{
    public int Off { get; set; }
    public string Hex { get; set; } = "";
    /// <summary>Bytes, from casts or (de)serializer size arguments; absent when unknown.</summary>
    public int? Size { get; set; }
    /// <summary>The HUD fields overlapping it, "Name (+off, n B)", or null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Hud { get; set; }
    /// <summary>mapped | UNMAPPED | "mapped, code uses n B vs HUD m B" | ...; null without a HUD struct to compare.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Status { get; set; }
    /// <summary>The flag test of the enclosing if-block, e.g. "(+0x3d & 0x40)", or null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Gate { get; set; }
    /// <summary>Up to 3 decompiled lines that access it.</summary>
    public List<string> Lines { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>code_struct_layout: a struct's layout as one function uses it, against the HUD's struct.</summary>
public sealed class CodeStructLayoutResult
{
    public string Function { get; set; } = "";
    public string Program { get; set; } = "";
    /// <summary>The pointer variable holding the struct in the decompiled code.</summary>
    public string Base { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Struct { get; set; }
    public List<CodeStructOffset> Offsets { get; set; } = [];
    public List<string> HudFieldsNotTouched { get; set; } = [];
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
