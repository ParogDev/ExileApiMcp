using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of route_agent (research/f8-agent-ux.md 4, 8.2, section 11): the route.agent command this server
// publishes (PascalCase, F8Wire.Options) and the route.agent.progress events Whats A Route sends back.

/// <summary>One step of an agent route, as route_agent takes it and route.agent carries it (spec AgentRouteStepDto).</summary>
public sealed class AgentRouteStepArg
{
    [Description("A route target, as route.json writes it: checkpoint, waypoint, transition:X, tile:X, radar:X, entity:X, click:X, item:X, npc:X. Empty: a thing to do with no place (done.kind Manual or Agent)")]
    public string Target { get; set; } = "";
    [Description("What the card says, plain words, <= 40 chars ('Go to the lever room')")]
    public string Label { get; set; } = "";
    [Description("A second line, <= 120 chars ('so I can see its state'); with done.kind Manual it says what to do before 'Did it'")]
    public string? Note { get; set; }
    [Description("When the step is done (default Near with a target, Manual without)")]
    public AgentRouteDoneRule? Done { get; set; }
    [Description("Optional: a guide.highlight target object ({item | path | text | panel+child | rect, label?, action?}) shown when the player is within withinCells of the goal; dies with the step")]
    public JsonElement? Highlight { get; set; }
    [Description("Cells from the goal at which the highlight fires (default 25)")]
    public int? WithinCells { get; set; }
}

/// <summary>A step's done condition (spec DoneRuleDto; Agent and Dead per section 11).</summary>
public sealed class AgentRouteDoneRule
{
    [Description("Near (within 'within' cells for 1 s) | Click (the target changed or despawned near the player) | Area (entering value, an area pattern) | Flag (quest flag value set) | Dead (monster path part value dead) | Manual (the player presses Did it) | Agent (you mark it: route_agent action=step)")]
    public string Kind { get; set; } = "Near";
    [Description("Flag: the quest flag name; Area: the area pattern; Dead: the monster path part")]
    public string? Value { get; set; }
    [Description("Near: cells (default 25)")]
    public int? Within { get; set; }
}

/// <summary>The route.agent event's data (spec AgentRouteDto; Owner / OwnerName / Hue are stamped by the bridge).</summary>
public sealed class AgentRouteCommand
{
    /// <summary>set | end | step</summary>
    public string Op { get; set; } = "set";
    public string? Id { get; set; }
    /// <summary>The F8 press this answers.</summary>
    public long? Ref { get; set; }
    public string? Title { get; set; }
    public string? Reason { get; set; }
    public int? TtlSec { get; set; }
    /// <summary>Resume | Hold</summary>
    public string? HandBack { get; set; }
    public List<AgentRouteStepArg>? Steps { get; set; }
    /// <summary>step: which step (-1 or absent: the current one).</summary>
    public int? StepIndex { get; set; }
    /// <summary>end / step: an optional one-line toast.</summary>
    public string? Text { get; set; }
}

/// <summary>route_agent's result: the route's id, the event sequence to wait after, and its progress events.</summary>
public sealed class AgentRouteResult
{
    public string Id { get; set; } = "";
    /// <summary>The last event sequence seen: pass it as since to wait for what comes next.</summary>
    public long Seq { get; set; }
    /// <summary>set: Whats A Route is subscribed to route.agent (null: a bridge build that doesn't say).</summary>
    public bool? RouteListening { get; set; }
    public List<AgentRouteProgress> Events { get; set; } = [];
    /// <summary>How the route ended, once it did: done | expired | ended | replaced | owner-gone.</summary>
    public string? Ended { get; set; }
    /// <summary>The player pressed Back to route and has not resumed it (it stays one click away for 10 min).</summary>
    public bool Parked { get; set; }
    public bool Waiting { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One route.agent.progress event (spec AgentRouteProgressDto plus StepCount, Label, By, Manual, Owner).</summary>
public sealed class AgentRouteProgress
{
    public long Seq { get; set; }
    /// <summary>started | arrived | step-done | paused | resumed | left | done | expired | replaced | ended | owner-gone | queued</summary>
    public string Event { get; set; } = "";
    public long? Ref { get; set; }
    public int StepIndex { get; set; }
    public int StepCount { get; set; }
    public string? Label { get; set; }
    /// <summary>How the step was done: the done kind (Near, Flag, Manual, Agent...).</summary>
    public string? By { get; set; }
    /// <summary>The current step waits on the player's Did it.</summary>
    public bool? Manual { get; set; }
    public string? Owner { get; set; }
    public string? Area { get; set; }
    public int[]? PlayerGrid { get; set; }
    public string? Why { get; set; }
    public DateTime? At { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
