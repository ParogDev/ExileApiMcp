using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the in-game agent guide (bridge guide.state / guide.set / guide.highlight). The bridge writes every
// state field, null when unset: those keep their explicit null on the wire ([JsonIgnore(Never)]).

/// <summary>
/// The guide card and its log (guide_state, and guide when it only read or logged). After guide changes the card the
/// result is the bridge's acknowledgement: only ok and the new rev.
/// </summary>
public sealed class GuideState
{
    public bool Ok { get; set; }
    public long Rev { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Title { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Instruction { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Step { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Steps { get; set; }
    /// <summary>waiting | detected | settling | captured | failed | info | done.</summary>
    public string? Status { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Detail { get; set; }
    /// <summary>The last log lines, oldest first.</summary>
    public List<GuideLogLine>? Log { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>guide.set's reply (a GuideState with only ok and rev).</summary>
public sealed class GuideAck
{
    public bool Ok { get; set; }
    public long Rev { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GuideLogLine
{
    /// <summary>HH:mm:ss (UTC).</summary>
    public string At { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// What highlight shows: the targets as resolved (found = boxes on screen now) and their boxes. After clear=true the
/// result is only {ok, cleared:true}.
/// </summary>
public sealed class HighlightState
{
    public bool Ok { get; set; }
    public bool? Cleared { get; set; }
    public long? Rev { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Title { get; set; }
    /// <summary>Sequences: the order of the current step.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Current { get; set; }
    /// <summary>When it disappears (durationSec), ISO 8601.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Until { get; set; }
    public List<HighlightTargetInfo>? Targets { get; set; }
    public List<HighlightBoxInfo>? Boxes { get; set; }
    /// <summary>Targets not on screen right now, null when all are.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>highlight clear=true's reply (a HighlightState with only ok and cleared).</summary>
public sealed class HighlightCleared
{
    public bool Ok { get; set; }
    public bool Cleared { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HighlightTargetInfo
{
    public int Index { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Item { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Path { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Text { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Within { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Action { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Panel { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public List<int>? Child { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public List<double>? Rect { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Label { get; set; }
    /// <summary>primary | secondary | context.</summary>
    public string Tier { get; set; } = "primary";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Order { get; set; }
    /// <summary>How many boxes on screen match this target now.</summary>
    public int Found { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HighlightBoxInfo
{
    /// <summary>Index into targets.</summary>
    public int Target { get; set; }
    /// <summary>Screen rect x, y, w, h.</summary>
    public List<double> Rect { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
