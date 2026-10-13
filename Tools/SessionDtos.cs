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
    /// <summary>How many agents the in-game chip counts: present sessions that are not infra (null from older bridges).</summary>
    public int? Agents { get; set; }
    public List<SessionInfo>? Sessions { get; set; }
    public List<LeaseInfo>? Leases { get; set; }
    /// <summary>Everything a restart would interrupt right now (implicit activity plus explicit leases).</summary>
    public List<BlockerInfo>? Blockers { get; set; }
    public List<RestartInfo>? Restarts { get; set; }
    /// <summary>Who holds the in-game card (the attention queue's floor), when someone does.</summary>
    public AttentionFloorInfo? Floor { get; set; }
    public long? AttentionSeq { get; set; }
    /// <summary>Open incidents on the machine-wide board (this server adds them; the bridge doesn't know the board), with each session's response.</summary>
    public List<IncidentView>? Incidents { get; set; }
    /// <summary>How this server's own label was decided (stdio servers; null for a shared server's caller, named by its headers).</summary>
    public SessionIdentityInfo? Identity { get; set; }
    /// <summary>Active work items with no session row of their own (an F8 press before its handler said hello), when the bridge lists them.</summary>
    public List<AgentWork>? Work { get; set; }
    /// <summary>Items that ended in the last 10 min (Done / Failed / Dropped), newest first.</summary>
    public List<AgentWork>? Recent { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>How a server's label was decided (Hosting/SessionIdentity.cs DecideRoots): never a guess from roots.</summary>
public sealed class SessionIdentityInfo
{
    /// <summary>env (HEXILE_AGENT) | cwd (the branch of the folder it was started in) | roots (one other checkout the client named) | folder (outside any checkout).</summary>
    public string Source { get; set; } = "";
    /// <summary>Why: what the roots said and why they were or weren't adopted.</summary>
    public string Note { get; set; } = "";
    /// <summary>The client's roots as it gave them (null: not asked).</summary>
    public List<string>? Roots { get; set; }
    /// <summary>The client's name and version (MCP clientInfo).</summary>
    public string? Client { get; set; }
    public string Label { get; set; } = "";
    /// <summary>deploy_plugin's checkout when the roots named one (else it uses the checkout the server runs from).</summary>
    public string? SessionRoot { get; set; }

    public static SessionIdentityInfo? Current() => Hosting.CallerIdentity.Current != null ? null : new()
    {
        Source = Hosting.SessionIdentity.Identity.Source, Note = Hosting.SessionIdentity.Identity.Note,
        Roots = Hosting.SessionIdentity.Identity.Roots?.ToList(), Client = Hosting.SessionIdentity.Identity.Client,
        Label = Hosting.SessionIdentity.Label, SessionRoot = Hosting.SessionIdentity.SessionRoot,
    };
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
    /// <summary>Counted as an agent on this HUD: connected, and for an mcp-http caller seen in the last 10 min (or a call or lease open).</summary>
    public bool? Present { get; set; }
    /// <summary>Connected but idle: an mcp-http caller with no call for 10 min (its connection outlives it on the shared server).</summary>
    public bool? Stale { get; set; }
    /// <summary>The shared server's own connection (control center, route-dispatcher), not an agent.</summary>
    public bool? Infra { get; set; }
    /// <summary>Seconds since it last spoke to the bridge.</summary>
    public int? IdleSec { get; set; }
    public DateTimeOffset? HelloAt { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public int? Leases { get; set; }
    /// <summary>What this session is doing right now, in words.</summary>
    public List<string>? Doing { get; set; }
    /// <summary>The who-dot colour, 0-5 (violet, rose, sky, orange, sand, slate).</summary>
    public int? Hue { get; set; }
    /// <summary>Its attention items: what it waits to show the player, and where each stands (0 = on the card).</summary>
    public List<AttentionQueueEntry>? Queued { get; set; }
    /// <summary>Its work items on the agents board (bridge work.set; F8 items it owns), when the bridge has them.</summary>
    public List<AgentWork>? Work { get; set; }
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
    /// <summary>agent | user.</summary>
    public string? Mode { get; set; }
    /// <summary>restarted | stopped | denied | blocked | merged | cancelled | error | timeout | held_by_outage; mode=user: announced | already_announced.</summary>
    public string Status { get; set; } = "";
    public bool Ok { get; set; }
    public int ExitCode { get; set; }
    public string Who { get; set; } = "";
    public string? Reason { get; set; }
    /// <summary>What tools\restart-hud.ps1 printed (coordination, stop, start, focus).</summary>
    public List<string> Output { get; set; } = [];
    /// <summary>When the new HUD process started (Loader.exe StartTime, UTC): the origin of the three *Sec times below
    /// (null: unknown, then they count from the restart script's end).</summary>
    public DateTime? HudStartedAt { get; set; }
    /// <summary>Seconds after the start until the HUD process was confirmed alive (restart-hud.ps1's alive check, ~8 s). Alive is NOT back.</summary>
    public double? HudAliveSec { get; set; }
    /// <summary>Seconds after the start until the new bridge's listener answered (ping); usually ~35 s, up to ~100 s. Null: not within the wait.</summary>
    public double? BridgeAfterSec { get; set; }
    /// <summary>Seconds after the start until back: the listener answers AND hud-state.json says the game is in (no file
    /// from the new bridge: the listener alone). Null: not back within the wait.</summary>
    public double? InGameAfterSec { get; set; }
    /// <summary>Same as <see cref="InGameAfterSec"/> (kept for older readers).</summary>
    public double? BackAfterSec { get; set; }
    /// <summary>hud-state.json's phase when last read; mode=user: the outage's phase (announced | holding | down | waitingGame | back | cancelled).</summary>
    public string? Phase { get; set; }
    public string? Note { get; set; }
    /// <summary>The restart's incident on the board (hud-restart, or bridge-down for a stop), raised when the HUD started stopping.</summary>
    public IncidentView? Incident { get; set; }
    /// <summary>held_by_outage when the user's planned restart refused this one.</summary>
    public string? Error { get; set; }
    /// <summary>The outage that holds this restart (Error held_by_outage).</summary>
    public OutageHold? HeldBy { get; set; }
    /// <summary>mode=user: the outage incident's id.</summary>
    public string? OutageId { get; set; }
    /// <summary>mode=user: sessions doing something, holding a lease or with an attention item: the HUD stops when they finish (or on Restart now).</summary>
    public List<RestartOutageSession>? Busy { get; set; }
    /// <summary>mode=user: every other connected session.</summary>
    public List<RestartOutageSession>? Ready { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The typed refusal while the user's planned restart is open: {error: held_by_outage, id, phase}.</summary>
public sealed class OutageHold
{
    public string Error { get; set; } = "held_by_outage";
    /// <summary>The outage incident's id.</summary>
    public string Id { get; set; } = "";
    /// <summary>announced | holding | down | waitingGame.</summary>
    public string Phase { get; set; } = "";
    public string? Game { get; set; }
    public string? Reason { get; set; }
    public string? RequestedBy { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One session in an outage's readiness: busy (doing, lease, attention item) or ready.</summary>
public sealed class RestartOutageSession
{
    public string Session { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Doing { get; set; }
    public string? Lease { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
