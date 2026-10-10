using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the session tools (bridge session.* / lease.* / restart.*): who is connected to a HUD, what each
// is doing, and the state of restart requests. Every type keeps unknown fields in Extra.

/// <summary>hud_sessions: the "who does what" view of one HUD.</summary>
public sealed class SessionsView
{
    public bool Ok { get; set; }
    /// <summary>This server's session id as the bridge knows it.</summary>
    public string? You { get; set; }
    /// <summary>Seconds a free restart still waits on the card with Not now (bridge setting).</summary>
    public int? HoldSec { get; set; }
    public List<SessionInfo>? Sessions { get; set; }
    public List<LeaseInfo>? Leases { get; set; }
    /// <summary>Everything a restart would interrupt right now (implicit activity plus explicit leases).</summary>
    public List<BlockerInfo>? Blockers { get; set; }
    public List<RestartInfo>? Restarts { get; set; }
    /// <summary>Who holds the in-game card (the attention queue's floor), when someone does.</summary>
    public AttentionFloorInfo? Floor { get; set; }
    public long? AttentionSeq { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SessionInfo
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>What the in-game UI calls it: never a system folder, no worktree hash tail, unique among the connected.</summary>
    public string? Name { get; set; }
    public string? Branch { get; set; }
    public string? Cwd { get; set; }
    public int? Pid { get; set; }
    /// <summary>mcp | mcp-http | script | ...</summary>
    public string? Kind { get; set; }
    public bool Connected { get; set; }
    public DateTimeOffset? HelloAt { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public int? Leases { get; set; }
    /// <summary>What this session is doing right now, in words.</summary>
    public List<string>? Doing { get; set; }
    /// <summary>The who-dot colour, 0-5 (violet, rose, sky, orange, sand, slate).</summary>
    public int? Hue { get; set; }
    /// <summary>Its attention items: what it waits to show the player, and where each stands (0 = on the card).</summary>
    public List<AttentionQueueEntry>? Queued { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LeaseInfo
{
    public bool? Ok { get; set; }
    public string Id { get; set; } = "";
    /// <summary>perf | pilot | record | reload | other.</summary>
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Session { get; set; }
    public string? Who { get; set; }
    public DateTimeOffset? Since { get; set; }
    public DateTimeOffset? Until { get; set; }
    public int? SecondsLeft { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LeaseReleased
{
    public bool Ok { get; set; }
    public bool Released { get; set; }
    public string? Id { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BlockerInfo
{
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Who { get; set; }
    /// <summary>The requester's own activity.</summary>
    public bool? Yours { get; set; }
    public DateTimeOffset? Until { get; set; }
    public int? SecondsLeft { get; set; }
    public string? Lease { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RestartInfo
{
    public bool? Ok { get; set; }
    public string Id { get; set; } = "";
    /// <summary>waiting | go | merged | denied | cancelled | expired.</summary>
    public string Status { get; set; } = "";
    public string? Who { get; set; }
    public string? Session { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? At { get; set; }
    /// <summary>Who decided: auto | user | a session label.</summary>
    public string? By { get; set; }
    /// <summary>merged: the request that carries this one.</summary>
    public string? Into { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? HoldUntil { get; set; }
    public int? SecondsToGo { get; set; }
    public List<BlockerInfo>? Blockers { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_restart: what the coordinated restart did.</summary>
public sealed class RestartResult
{
    public string Game { get; set; } = "";
    /// <summary>restarted | stopped | denied | blocked | merged | cancelled | error | timeout.</summary>
    public string Status { get; set; } = "";
    public bool Ok { get; set; }
    public int ExitCode { get; set; }
    public string Who { get; set; } = "";
    public string? Reason { get; set; }
    /// <summary>What tools\restart-hud.ps1 printed (coordination, stop, start, focus).</summary>
    public List<string> Output { get; set; } = [];
    /// <summary>Seconds until the new HUD's bridge answered again (null: not back within the wait).</summary>
    public double? BackAfterSec { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
