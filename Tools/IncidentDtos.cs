using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the incident board tools (incidents, incident_raise, incident_respond, incident_resolve,
// incident_wait) and the exile://incidents resource. They mirror %LOCALAPPDATA%\Hexile\incidents.json (Incidents/
// IncidentModel.cs), which tools\incident.ps1 and the Claude Code hook share; fields any writer adds pass through Extra.

/// <summary>One incident: something that affects every session (bridge down, HUD restart, ports reserved, a deploy...).</summary>
public sealed class IncidentView
{
    /// <summary>i-yyyyMMddHHmmss-xxxx</summary>
    public string Id { get; set; } = "";
    /// <summary>bridge-down | hud-restart | deploy | mcp-down | ports-reserved | custom</summary>
    public string Kind { get; set; } = "";
    /// <summary>poe1 | poe2 | machine</summary>
    public string Scope { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>What broke, named at the broken link.</summary>
    public string? Detail { get; set; }
    /// <summary>What to do, and who must act (null: nothing to do).</summary>
    public string? Fix { get; set; }
    /// <summary>Session label of the raiser.</summary>
    public string RaisedBy { get; set; } = "";
    public string? RaisedAt { get; set; }
    /// <summary>When it should be over (ISO UTC), null when unknown.</summary>
    public string? Eta { get; set; }
    /// <summary>open | resolved</summary>
    public string State { get; set; } = "";
    public string? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    /// <summary>How it was resolved (the resolve note).</summary>
    public string? Resolution { get; set; }
    /// <summary>Set when an open incident was updated by key: when, and by whom.</summary>
    public string? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    /// <summary>Dedupe key (e.g. bridge-down:poe2, deploy:Whats A Route): raising an open key updates it.</summary>
    public string Key { get; set; } = "";
    /// <summary>Each session's answer: ok, blocked or waiting, until when.</summary>
    public List<IncidentResponseView> Responses { get; set; } = [];
    /// <summary>The board rev at this incident's last change.</summary>
    public int? Rev { get; set; }
    /// <summary>bridge-down: restarting | hud-stopped | hud-not-running | token-missing | not-listening | not-answering.</summary>
    public string? Cause { get; set; }
    /// <summary>deploy: the owning session's process.</summary>
    public int? OwnerPid { get; set; }
    /// <summary>deploy: the owner's last heartbeat (every 30 s while its server runs).</summary>
    public string? HeartbeatAt { get; set; }
    /// <summary>deploy: owner gone or silent for 10 min. Any session may restore it or deploy over it without force.</summary>
    public bool? Orphaned { get; set; }
    /// <summary>deploy: why it is (not) orphaned, in words.</summary>
    public string? OwnerState { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class IncidentResponseView
{
    public string Who { get; set; } = "";
    /// <summary>The answering session's id (absent on responses written before ids existed).</summary>
    public string? SessionId { get; set; }
    /// <summary>Who, unique among the responses ("Claude Desktop (2)" when two sessions share a label).</summary>
    public string? Display { get; set; }
    /// <summary>ok | blocked | waiting</summary>
    public string Status { get; set; } = "";
    public string? Note { get; set; }
    /// <summary>How long the status holds (ISO UTC), e.g. blocked until the restart is over.</summary>
    public string? Until { get; set; }
    public string? At { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>incidents / exile://incidents: the board (open plus resolved in the last 24 h, or what changed since a rev).</summary>
public sealed class IncidentsResult
{
    /// <summary>The board's rev now; pass it as since to incidents or incident_wait.</summary>
    public int Rev { get; set; }
    public int? Since { get; set; }
    public int Open { get; set; }
    public List<IncidentView> Incidents { get; set; } = [];
    /// <summary>This session's label, as raisedBy / who show it.</summary>
    public string You { get; set; } = "";
    public string File { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>incident_raise / incident_respond / incident_resolve.</summary>
public sealed class IncidentActionResult
{
    public bool Ok { get; set; }
    /// <summary>raised | updated | responded | resolved | unchanged</summary>
    public string Action { get; set; } = "";
    public int Rev { get; set; }
    public IncidentView? Incident { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>incident_wait: returned as soon as the board's rev moved past since (or at the timeout).</summary>
public sealed class IncidentWaitResult
{
    public bool Changed { get; set; }
    public int Since { get; set; }
    public int Rev { get; set; }
    public double WaitedSec { get; set; }
    /// <summary>The incidents changed after since (all open ones when a writer without per-incident revs changed the board).</summary>
    public List<IncidentView> Incidents { get; set; } = [];
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
