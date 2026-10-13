using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the F8 progress and agents-board family (research/f8-agent-ux.md sections 3, 5, 8): the extended
// feedback.reply data, the agents' work items (bridge work.set / work.list / session.list work[]), and agent_status.
// Wire DTOs (what goes out to the bridge) are PascalCase (F8Wire.Options); tool results camelCase (Dto.Options) as always.

/// <summary>JSON options for what this server publishes to the bridge and the plugins: PascalCase (the plugins' DTOs,
/// spec 8.2), nulls omitted.</summary>
public static class F8Wire
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A wire DTO as the Newtonsoft object the bridge client sends.</summary>
    public static Newtonsoft.Json.Linq.JObject ToJObject<T>(T value)
    {
        using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(JsonSerializer.Serialize(value, Options))) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
        return Newtonsoft.Json.Linq.JObject.Load(reader);
    }
}

/// <summary>A pull request an F8 item or a session's work is waiting on (spec 8.2 PrRefDto).</summary>
public sealed class F8PrRef
{
    [Description("owner/name, e.g. ParogDev/WhatsARoute")]
    public string? Repo { get; set; }
    public string? Url { get; set; }
    public int Number { get; set; }
    [Description("pending | green | red")]
    public string? Checks { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// The data of a feedback.reply event as this server publishes it. Old readers (Route builds before the compose panel, the
/// dispatcher's own history, feedback_sessions) read the lowercase ref / status / text; the spec's readers (the bridge's
/// work items, the new Route) read PascalCase Ref / Status / Text and the new fields. Both casings go out with the same
/// values: Newtonsoft's ToObject is case-insensitive, JObject indexers are not.
/// </summary>
public sealed class F8ReplyData
{
    [JsonPropertyName("ref")] public long RefLegacy { get; set; }
    [JsonPropertyName("status")] public string StatusLegacy { get; set; } = "";
    [JsonPropertyName("text")] public string? TextLegacy { get; set; }
    public long Ref { get; set; }
    /// <summary>Legacy: queued | claimed | working | done | failed (derived from Stage, C5 reversed).</summary>
    public string Status { get; set; } = "";
    public string? Text { get; set; }
    /// <summary>F8Stage: Pressed | Queued | Claimed | Reading | Investigating | Fixing | LiveFix | Reviewing | Merging | Deploying | Verifying | Done | Failed | Dropped.</summary>
    public string? Stage { get; set; }
    /// <summary>None | Session | Ci | Review | OtherAgent | Restart (Player is derived by the bridge, never sent).</summary>
    public string? WaitingOn { get; set; }
    public string? Detail { get; set; }
    public string? Ask { get; set; }
    public F8PrRef? Pr { get; set; }
    public DateTime? At { get; set; }
}

/// <summary>One agent work item (bridge work.set / work.list, session.list work[] and recent[]; spec 8.2 AgentWorkDto).</summary>
public sealed class AgentWork
{
    /// <summary>"f8:&lt;seq&gt;" or the session id.</summary>
    public string? Key { get; set; }
    /// <summary>F8 | Task</summary>
    public string? Kind { get; set; }
    public long? Seq { get; set; }
    public string? Owner { get; set; }
    public string? OwnerName { get; set; }
    public int? Hue { get; set; }
    public string? Title { get; set; }
    public string? Stage { get; set; }
    public string? WaitingOn { get; set; }
    public string? Ask { get; set; }
    public string? Text { get; set; }
    public string? Detail { get; set; }
    public F8PrRef? Pr { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? StageAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<AgentWorkStage>? History { get; set; }
    public bool? Final { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AgentWorkStage
{
    public string? Stage { get; set; }
    public DateTimeOffset? At { get; set; }
    public string? Text { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>What agent_status sends (bridge work.set, spec 8.3): only the fields given.</summary>
public sealed class AgentWorkSet
{
    public string? Key { get; set; }
    public string? Kind { get; set; }
    public string? Title { get; set; }
    public string? Stage { get; set; }
    public string? Text { get; set; }
    public string? Detail { get; set; }
    public F8PrRef? Pr { get; set; }
    public long? Ref { get; set; }
}

/// <summary>agent_status: the item as the bridge keeps it now.</summary>
public sealed class AgentStatusResult
{
    public bool Ok { get; set; }
    public AgentWork? Item { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
