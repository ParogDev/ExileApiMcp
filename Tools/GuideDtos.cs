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
    /// <summary>Your layer's key (your session id; flow / queue / anon for the HUD's own).</summary>
    public string? Layer { get; set; }
    /// <summary>Who set this layer (session label).</summary>
    public string? Who { get; set; }
    /// <summary>Pass as since to await_verdicts: answers after this call.</summary>
    public long? VerdictSeq { get; set; }
    /// <summary>Every agent's layer on screen, oldest first (the combined view).</summary>
    public List<HighlightLayerInfo>? Layers { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One agent's highlight layer, as the combined view lists it.</summary>
public sealed class HighlightLayerInfo
{
    /// <summary>The owner key: a session id, or flow / queue / anon.</summary>
    public string Layer { get; set; } = "";
    public string? Session { get; set; }
    public string? Who { get; set; }
    /// <summary>Yours (or a flow / queued step you started).</summary>
    public bool? Mine { get; set; }
    public long Rev { get; set; }
    public string? Title { get; set; }
    public int? Current { get; set; }
    public int Targets { get; set; }
    /// <summary>Boxes on screen now.</summary>
    public int Found { get; set; }
    public int Asked { get; set; }
    public int Pending { get; set; }
    public string? Until { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>highlight clear=true's reply: only your layer, or every layer with force (all, layers = whose were cleared).</summary>
public sealed class HighlightCleared
{
    public bool Ok { get; set; }
    public bool Cleared { get; set; }
    public string? Layer { get; set; }
    public bool? All { get; set; }
    public List<string>? Layers { get; set; }
    /// <summary>Other agents' layers that stay on screen, or the questions a clear-all removed.</summary>
    public string? Note { get; set; }
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
    /// <summary>The yes/no question shown next to the target (Yes / No / Not sure controls in game), null when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Ask { get; set; }
    /// <summary>The caller's correlation key for the verdict.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Key { get; set; }
    /// <summary>yes | no | skip once the user clicked; null while still asked (or not asked).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Answer { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? AnsweredAt { get; set; }
    /// <summary>How many boxes on screen match this target now.</summary>
    public int Found { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// The user's answers to asked highlight targets (verdicts, await_verdicts): the verdicts after since, the newest seq,
/// and what the current highlight is still asking.
/// </summary>
public sealed class VerdictsResult
{
    public bool Ok { get; set; }
    /// <summary>The newest verdict's seq (pass it as since next time).</summary>
    public long Seq { get; set; }
    /// <summary>The current highlight's rev; verdicts carry the rev they were given under.</summary>
    public long HighlightRev { get; set; }
    public List<VerdictInfo> Verdicts { get; set; } = [];
    /// <summary>The current highlight's asked targets, answered or not.</summary>
    public List<VerdictAsked> Asked { get; set; } = [];
    /// <summary>Asked targets of your highlight without an answer yet.</summary>
    public int Pending { get; set; }
    /// <summary>Unanswered questions on screen, every agent's.</summary>
    public int? PendingAll { get; set; }
    /// <summary>Your layer's key.</summary>
    public string? Layer { get; set; }
    /// <summary>Every agent's layer (the combined view).</summary>
    public List<HighlightLayerInfo>? Layers { get; set; }
    /// <summary>Where the HUD appends every verdict (verdicts.jsonl).</summary>
    public string? File { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One answer: which target was asked what, what the user clicked, and where the target was.</summary>
public sealed class VerdictInfo
{
    public long Seq { get; set; }
    /// <summary>The target's key, else hl&lt;rev&gt;.&lt;index&gt;.</summary>
    public string Id { get; set; } = "";
    public string? Key { get; set; }
    public string Ask { get; set; } = "";
    public string? Label { get; set; }
    /// <summary>yes | no | skip.</summary>
    public string Answer { get; set; } = "";
    /// <summary>UTC, ISO 8601.</summary>
    public string At { get; set; } = "";
    /// <summary>The target's first box when answered: x, y, w, h.</summary>
    public List<double>? Rect { get; set; }
    public string? Item { get; set; }
    public string? Path { get; set; }
    public string? Text { get; set; }
    public string? Panel { get; set; }
    public List<int>? Child { get; set; }
    public string? HighlightTitle { get; set; }
    public long HighlightRev { get; set; }
    /// <summary>The target's index in that highlight.</summary>
    public int Target { get; set; }
    /// <summary>The layer it was asked in, and the session that asked.</summary>
    public string? Layer { get; set; }
    public string? Session { get; set; }
    public string? Who { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class VerdictAsked
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Key { get; set; }
    public string Ask { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Label { get; set; }
    /// <summary>yes | no | skip, null while unanswered.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Answer { get; set; }
    /// <summary>The target resolves to a box on screen right now (the controls are visible).</summary>
    public bool OnScreen { get; set; }
    /// <summary>The layer it is in and that layer's rev (verdicts given under it carry the same rev).</summary>
    public string? Layer { get; set; }
    public long? HighlightRev { get; set; }
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
