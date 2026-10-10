using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the attention queue (bridge attention.*, Shared\AttentionQueue.cs in What's an AI Bridge): one agent
// session has the floor (the in-game card) at a time, the rest wait in line. They mirror the bridge's JSON; every type
// keeps unknown fields in Extra.

/// <summary>One choice of an ask.</summary>
public sealed class AttentionOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Hint { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The player's answer to an ask.</summary>
public sealed class AttentionAnswer
{
    public string OptionId { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTimeOffset? At { get; set; }
    /// <summary>Other sessions that asked the same question within 60 s: one card, one answer for all of them.</summary>
    public List<string>? SharedWith { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>attention.ask / request / result / cancel / release: one item and where it stands.</summary>
public sealed class AttentionItemInfo
{
    public bool? Ok { get; set; }
    public string Id { get; set; } = "";
    /// <summary>ask | step | flow.</summary>
    public string Kind { get; set; } = "";
    public string? Who { get; set; }
    public string? Session { get; set; }
    /// <summary>The owner's who-dot colour, 0-5 (violet, rose, sky, orange, sand, slate).</summary>
    public int? Hue { get; set; }
    public bool? Mine { get; set; }
    public string? Title { get; set; }
    /// <summary>queued | showing | answered | dismissed | expired | cancelled | done.</summary>
    public string Status { get; set; } = "";
    /// <summary>now | calm.</summary>
    public string? Urgency { get; set; }
    public string? Question { get; set; }
    public string? Detail { get; set; }
    public List<AttentionOption>? Options { get; set; }
    public AttentionAnswer? Answer { get; set; }
    /// <summary>0 = holds the floor; otherwise 1 + everything ahead (holder, waiting restart, earlier turns); -1 once ended.</summary>
    public int? Position { get; set; }
    /// <summary>Who is ahead, in order (the holder first).</summary>
    public List<string>? Ahead { get; set; }
    /// <summary>Why it waits beyond its turn: a restart, the fight, a calm moment, a Later's hold.</summary>
    public string? WaitingFor { get; set; }
    public int? LaterCount { get; set; }
    public DateTimeOffset? EligibleAt { get; set; }
    /// <summary>An identical ask of another session carries this one.</summary>
    public string? MergedInto { get; set; }
    /// <summary>Why it ended, as a code: later_x3 | unanswered | ttl | withdrawn | disconnected | user_dismissed | ...</summary>
    public string? Reason { get; set; }
    public string? Why { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? ShownAt { get; set; }
    /// <summary>When the player could actually see it (after the hand-over gap, outside combat).</summary>
    public DateTimeOffset? VisibleSince { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? TtlSec { get; set; }
    public int? ShowTtlSec { get; set; }
    public int? WaitedSec { get; set; }
    /// <summary>attention.release: who holds the floor now.</summary>
    public AttentionFloorInfo? Next { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Who holds the floor.</summary>
public sealed class AttentionFloorInfo
{
    [JsonPropertyName("itemId")] public string? ItemId { get; set; }
    public string? Id { get; set; }
    public string? SessionId { get; set; }
    public string? Who { get; set; }
    public int? Hue { get; set; }
    public string? Kind { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset? Since { get; set; }
    public DateTimeOffset? VisibleSince { get; set; }
    /// <summary>none | gap (hand-over) | combat | loading | menu.</summary>
    public string? Hidden { get; set; }
    public bool? Mine { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One row of the queue (the holder at position 0, a waiting restart, then the turns in order).</summary>
public sealed class AttentionQueueEntry
{
    public string Id { get; set; } = "";
    public string? Who { get; set; }
    public int? Hue { get; set; }
    /// <summary>ask | step | flow | restart.</summary>
    public string Kind { get; set; } = "";
    public string? Title { get; set; }
    public string? Status { get; set; }
    public int Position { get; set; }
    public int? WaitingSec { get; set; }
    public bool? Mine { get; set; }
    public string? Urgency { get; set; }
    public string? WaitingFor { get; set; }
    public int? LaterCount { get; set; }
    public DateTimeOffset? EligibleAt { get; set; }
    public string? MergedInto { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The player as the queue sees it.</summary>
public sealed class AttentionPlayerInfo
{
    /// <summary>playing | calm | combat | loading | menu.</summary>
    public string? State { get; set; }
    public bool Combat { get; set; }
    public bool Calm { get; set; }
    public bool Town { get; set; }
    public double? IdleSec { get; set; }
    public double? NoCombatSec { get; set; }
    public bool? BigPanel { get; set; }
    /// <summary>The player sense that broke (combat or input reads), when one did.</summary>
    public string? Broken { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>attention.state and exile://attention/{game}/queue: who has the floor and who waits.</summary>
public sealed class AttentionStateView
{
    public bool? Ok { get; set; }
    public string? Game { get; set; }
    /// <summary>Increases on every change: compare it to see whether anything moved.</summary>
    public long Seq { get; set; }
    /// <summary>This server's session id.</summary>
    public string? You { get; set; }
    public AttentionFloorInfo? Floor { get; set; }
    public List<AttentionQueueEntry> Queue { get; set; } = [];
    public JsonElement? Restart { get; set; }
    public AttentionPlayerInfo? Player { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>ask_user / ask_user_result: how the question went.</summary>
public sealed class AskUserResult
{
    /// <summary>answered | dismissed | expired | cancelled | pending (still waiting: call ask_user_result with id).</summary>
    public string Status { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Game { get; set; }
    public AttentionAnswer? Answer { get; set; }
    public int WaitedSec { get; set; }
    /// <summary>pending: where it stands now.</summary>
    public int? Position { get; set; }
    public List<string>? Ahead { get; set; }
    public string? WaitingFor { get; set; }
    public int? LaterCount { get; set; }
    public string? Reason { get; set; }
    public string? Note { get; set; }
    /// <summary>The bridge's item as it was last read.</summary>
    public AttentionItemInfo? Item { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
