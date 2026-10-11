using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// feedback_sessions and exile://feedback/sessions: every F8 press of Whats A Route with the Claude session the dispatcher
// started for it (Dispatch/FeedbackSessions.cs reads handlers\<seq>\state.json, output.json and brief.md, merges the
// bridge's press and reply events when a HUD answers). Mirrored by ui-src/src/control/types.ts (FeedbackSession*);
// change both together. Unknown fields of state.json pass through in Extra: a newer dispatcher adds fields (a display
// name, a Remote Control URL, a mode) that this build shows when they are there.

/// <summary>feedback_sessions: the dispatcher at a glance and the presses, newest first.</summary>
public sealed class FeedbackSessionsResult
{
    public FeedbackDispatcherSummary Dispatcher { get; set; } = new();
    public List<FeedbackSession> Sessions { get; set; } = [];
    /// <summary>How many presses there are in all (handler folders plus queued), before limit.</summary>
    public int Total { get; set; }
    public string? HandlersDir { get; set; }
    /// <summary>Whether the bridge's press and reply events were merged in ("poe2: n presses, m replies") or why not.</summary>
    public string? Bridge { get; set; }
    public DateTimeOffset At { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The F8 dispatcher as the view shows it: alive or not, listening or broken, with the fix.</summary>
public sealed class FeedbackDispatcherSummary
{
    /// <summary>listening | idle | broken | stopped (what the dispatcher wrote) | not-running (its file is older than 15 s) | never-ran (no file).</summary>
    public string Status { get; set; } = "never-ran";
    /// <summary>The dispatcher's updatedAt is within 15 s: a dispatcher is running.</summary>
    public bool Alive { get; set; }
    public string? Why { get; set; }
    /// <summary>What fixes a broken or absent dispatcher, when known.</summary>
    public string? Fix { get; set; }
    public double? UpdatedAgoSec { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Version { get; set; }
    public int? Pid { get; set; }
    public string? Host { get; set; }
    public string? ClaudeExe { get; set; }
    public bool? LoggedIn { get; set; }
    public string? LoginCommand { get; set; }
    public string? Repo { get; set; }
    /// <summary>Presses waiting for a handler (unclaimed, or queued behind the running one).</summary>
    public List<long> Waiting { get; set; } = [];
    /// <summary>The seq of the running handler, when one runs.</summary>
    public long? Running { get; set; }
    /// <summary>Everything else dispatcher.json carries (a newer dispatcher's fields), minus what is typed above.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One F8 press and its session.</summary>
public sealed class FeedbackSession
{
    public long Seq { get; set; }
    /// <summary>queued | starting | running | done | failed | works (an "it works" press: no session is started for it).</summary>
    public string Status { get; set; } = "";
    /// <summary>The session's display name when the dispatcher gave it one (a newer dispatcher: "F8 #seq: ...").</summary>
    public string? Name { get; set; }
    /// <summary>A link to the session (a claude.ai/code Remote Control URL) when the dispatcher recorded one.</summary>
    public string? Url { get; set; }
    /// <summary>How the handler runs (headless, remote-control, ...) when the dispatcher recorded it.</summary>
    public string? Mode { get; set; }
    /// <summary>The press's verdict: note | works | lost | ... (the plugin's Event).</summary>
    public string? Verdict { get; set; }
    /// <summary>What the player typed, when they typed something.</summary>
    public string? Note { get; set; }
    public string? Area { get; set; }
    public string? AreaName { get; set; }
    /// <summary>The route step at the press: its label and target spec.</summary>
    public string? Label { get; set; }
    public string? Target { get; set; }
    public DateTimeOffset? PressedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Seconds from the handler's start to its end (or to now while it runs).</summary>
    public double? DurationSec { get; set; }
    /// <summary>The answer shown on the card (the session's final message).</summary>
    public string? Result { get; set; }
    public string? Error { get; set; }
    /// <summary>The dispatcher told the card (the handler may have said done itself: see replies).</summary>
    public bool Replied { get; set; }
    public int? Pid { get; set; }
    public string? SessionId { get; set; }
    /// <summary>The session's transcript (%USERPROFILE%\.claude\projects\&lt;worktree slug&gt;\&lt;sessionId&gt;.jsonl) and whether it is there.</summary>
    public string? TranscriptPath { get; set; }
    public bool? TranscriptExists { get; set; }
    public string? Worktree { get; set; }
    /// <summary>The worktree still exists (it is removed after a run that changed nothing).</summary>
    public bool? WorktreeExists { get; set; }
    public string? Dir { get; set; }
    /// <summary>From the CLI's JSON result: cost, usage, turns, the model, the API time.</summary>
    public double? CostUsd { get; set; }
    public FeedbackUsage? Usage { get; set; }
    public int? Turns { get; set; }
    public string? Model { get; set; }
    public double? ApiMs { get; set; }
    /// <summary>What was said above the route card about this press (the bridge's feedback.reply events), oldest first.</summary>
    public List<FeedbackReply> Replies { get; set; } = [];
    /// <summary>brief.md, only when one press is asked for (feedback_sessions seq=).</summary>
    public string? Brief { get; set; }
    /// <summary>stderr.log, only when one press is asked for; null when empty.</summary>
    public string? Stderr { get; set; }
    /// <summary>Fields of state.json this build doesn't type (a newer dispatcher's).</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class FeedbackUsage
{
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CacheReadTokens { get; set; }
    public long? CacheCreationTokens { get; set; }
    public long? ThinkingTokens { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class FeedbackReply
{
    public long Seq { get; set; }
    public DateTimeOffset? At { get; set; }
    /// <summary>claimed | queued | working | done | failed</summary>
    public string? Status { get; set; }
    public string? Text { get; set; }
    /// <summary>route-dispatcher, or the handler's label (route-f8-&lt;seq&gt;) when it spoke itself.</summary>
    public string? Source { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
