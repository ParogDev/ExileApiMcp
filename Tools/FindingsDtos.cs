using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of findings and verify_finding. A finding is an entry of Knowledge/findings.json, passed through as
// written there; the memory view's findings tab parses these (ui-src/src/memory/types.ts: FindingsResult, VerifyResult).

/// <summary>A finding's status on one game.</summary>
public sealed class FindingGame
{
    /// <summary>verified | differs | unverified | n/a.</summary>
    public string? Status { get; set; }
    public string? Date { get; set; }
    /// <summary>"+61 bit 6", "+63 (32-bit)", a function in a program, or a bit table.</summary>
    public string? Where { get; set; }
    public string? Evidence { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>How verify_finding re-checks a finding. Fields depend on kind; byGame overrides them per game.</summary>
public sealed class FindingCheck
{
    /// <summary>correlate | stored | code | data | eval | manual.</summary>
    public string? Kind { get; set; }
    public string? Path { get; set; }
    public string? Label { get; set; }
    /// <summary>correlate/eval: the expected text; data: row index -> text the row contains.</summary>
    public JsonElement? Expect { get; set; }
    /// <summary>code: field offset (number or "0x..") and bit.</summary>
    public JsonElement? Offset { get; set; }
    public int? Bit { get; set; }
    /// <summary>code: texts the decompiled excerpt must contain.</summary>
    public List<string>? ExpectAll { get; set; }
    /// <summary>data: the data table.</summary>
    public string? File { get; set; }
    public string? Expression { get; set; }
    public Dictionary<string, string>? ExpressionByGame { get; set; }
    /// <summary>Per-game overrides of the check's fields.</summary>
    public Dictionary<string, JsonElement>? ByGame { get; set; }
    /// <summary>manual: the experiment to run.</summary>
    public string? How { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class FindingEntry
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>"ServerStashTab (ServerStashTabOffsets)", "Life (LifeComponentOffsets)"...</summary>
    public string? Subject { get; set; }
    /// <summary>Status per game: poe1 / poe2.</summary>
    public Dictionary<string, FindingGame>? Games { get; set; }
    public FindingCheck? Check { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>findings: the registry, filtered, plus what to re-check on the requested game.</summary>
public sealed class FindingsResult
{
    public string? About { get; set; }
    public List<FindingEntry> Findings { get; set; } = [];
    /// <summary>Verified on another game, unverified on the requested one.</summary>
    public List<string>? ToCheck { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>verify_finding: the verdict of a finding's check on the live game, or (manual checks) the experiment to run.</summary>
public sealed class VerifyFindingResult
{
    public string Id { get; set; } = "";
    public string Game { get; set; } = "";
    /// <summary>The check's kind (manual for checks that need the user).</summary>
    public string? Kind { get; set; }
    /// <summary>What findings.json records for this game (null when nothing).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public FindingGame? Recorded { get; set; }
    /// <summary>pass | moved | differs | fail.</summary>
    public string? Verdict { get; set; }
    public string? Where { get; set; }
    public string? Evidence { get; set; }
    /// <summary>The status entry to record in findings.json.</summary>
    public FindingGame? Record { get; set; }
    public string? Next { get; set; }
    /// <summary>Manual checks: the experiment to run.</summary>
    public string? HowToVerify { get; set; }
    /// <summary>correlate / stored checks: memory_correlate's finding for the label.</summary>
    public ProbeLabelFinding? Correlate { get; set; }
    /// <summary>code checks: find_field_access's functions.</summary>
    public List<CodeAccessFunction>? Functions { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
