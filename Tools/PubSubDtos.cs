using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the plugin pub/sub tools (pubsub_*, feedback_*): events plugins publish through the AI Bridge
// (AIBridge.Publish), claims, and the F8 feedback dispatcher's state. Unknown fields pass through in Extra.

/// <summary>One published event: {seq, kind, at, t, source, data, claims}.</summary>
public sealed class PubSubEvent
{
    public long Seq { get; set; }
    /// <summary>feedback (an F8 press) | route (Whats A Route's trace) | feedback.reply | pubsub.claim | pubsub.release | any kind a plugin publishes.</summary>
    public string Kind { get; set; } = "";
    public DateTimeOffset At { get; set; }
    /// <summary>ms on the bridge's observer clock of that HUD run (lines up with observe_* events).</summary>
    public double? T { get; set; }
    public string? Source { get; set; }
    public JsonElement? Data { get; set; }
    /// <summary>stage (handle, reply...) -> {by, at}: who claimed this event, exactly once per stage.</summary>
    public Dictionary<string, PubSubClaim>? Claims { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PubSubClaim
{
    public string? By { get; set; }
    public string? At { get; set; }
    public string? Stage { get; set; }
    public long? ClaimSeq { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>pubsub_events / pubsub_wait.</summary>
public sealed class PubSubEventsResult
{
    public bool Ok { get; set; }
    /// <summary>The newest sequence: pass it as since next time.</summary>
    public long Seq { get; set; }
    /// <summary>The oldest event the bridge still keeps in memory.</summary>
    public long? Oldest { get; set; }
    public List<PubSubEvent> Events { get; set; } = [];
    public string? File { get; set; }
    public string? FileError { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>pubsub_publish / feedback_reply: the event's sequence and who listens to its kind.</summary>
public sealed class PubSubPublishResult
{
    public bool Ok { get; set; }
    public long Seq { get; set; }
    public string? Kind { get; set; }
    public List<JsonElement> Subscribers { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The F8 dispatcher's state, as it writes it (handlers\dispatcher.json) and feedback_dispatcher returns it.</summary>
public sealed class FeedbackDispatcherState
{
    /// <summary>listening | idle (no HUD) | broken | stopped</summary>
    public string Status { get; set; } = "";
    public string? Why { get; set; }
    public string? Host { get; set; }
    public int Pid { get; set; }
    public string? Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Cursor { get; set; }
    public string? ClaudeExe { get; set; }
    public bool? LoggedIn { get; set; }
    public string? Repo { get; set; }
    public string? HandlersDir { get; set; }
    public List<FeedbackHandlerState> Handlers { get; set; } = [];
    /// <summary>Presses waiting: unclaimed, or claimed and queued behind a running handler.</summary>
    public List<long> Waiting { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One press's handler: a fresh headless Claude Code session in its own worktree.</summary>
public sealed class FeedbackHandlerState
{
    public long Seq { get; set; }
    /// <summary>starting | running | done | failed</summary>
    public string Status { get; set; } = "";
    public int? Pid { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Worktree { get; set; }
    public string? Dir { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public string? SessionId { get; set; }
    public bool Replied { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
