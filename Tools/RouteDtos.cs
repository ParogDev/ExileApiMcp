using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// route_state: the live state of the PoE2 campaign navigation plugin "Whats A Route", read through its PluginBridge
// method WhatsARoute.GetState (Func<string> returning JSON). Mirrors the plugin's RouteStateDto; the plugin's DTO
// grows, so every class keeps unknown fields in Extra and the control center's Route page shows them under "More".

/// <summary>route_state: what the route plugin is doing right now.</summary>
public sealed class RouteStateResult
{
    public string? Game { get; set; }
    /// <summary>Raw area id (e.g. G1_2) and its display name.</summary>
    public string? Area { get; set; }
    public string? AreaName { get; set; }
    /// <summary>The plugin's route mode setting.</summary>
    public string? Mode { get; set; }
    /// <summary>Index into Steps of the current step; -1 when none.</summary>
    public int StepIndex { get; set; } = -1;
    public List<RouteStep> Steps { get; set; } = [];
    /// <summary>The current step's target spec and its resolved label.</summary>
    public string? Target { get; set; }
    public string? Label { get; set; }
    /// <summary>How many goal positions the target resolved to.</summary>
    public int Goals { get; set; }
    /// <summary>The first broken link when there is no path (target unresolved, terrain missing, unreachable).</summary>
    public string? Why { get; set; }
    /// <summary>The player's grid cell [x, y].</summary>
    public int[]? PlayerGrid { get; set; }
    /// <summary>The area's pathfinding grid size [w, h] in cells.</summary>
    public int[]? AreaSize { get; set; }
    public int PathCells { get; set; }
    public float PathWorldLength { get; set; }
    public int EdgePieces { get; set; }
    public RouteTimings Timings { get; set; } = new();
    /// <summary>The whole route in grid cells, every few cells, flat: x0, y0, x1, y1, ...</summary>
    public List<int> MapPath { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    /// <summary>Open quests as "QuestId:StateId".</summary>
    public List<string> ActiveQuests { get; set; } = [];
    public string? QuestObjective { get; set; }
    public string? TravelTo { get; set; }
    public bool TravelMapOpen { get; set; }
    public List<RouteTravelStop> TravelMapStops { get; set; } = [];
    /// <summary>The terrain tile (.tdt) under the player.</summary>
    public string? PlayerTile { get; set; }
    /// <summary>Places recorded in this area instance.</summary>
    public int Sightings { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RouteStep
{
    public string Target { get; set; } = "";
    public string? Label { get; set; }
    public string? DoneFlag { get; set; }
    public bool Done { get; set; }
    public bool Optional { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RouteTravelStop
{
    public int Index { get; set; }
    public string? DestId { get; set; }
    public string? DestName { get; set; }
    public bool Unlocked { get; set; }
    public bool Chosen { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The plugin's own timings, with the load they were measured under (Entities).</summary>
public sealed class RouteTimings
{
    public double GridBuildMs { get; set; }
    public double FieldBuildMs { get; set; }
    public int FieldCellsReached { get; set; }
    public double EdgeBuildMs { get; set; }
    public int EdgeContours { get; set; }
    /// <summary>Per frame, microseconds.</summary>
    public double TickUs { get; set; }
    public double RenderUs { get; set; }
    public double LastReplanUs { get; set; }
    /// <summary>Objective resolution (2 Hz), smoothed.</summary>
    public double ResolveUs { get; set; }
    /// <summary>Load condition the timings were taken under: valid entities right now.</summary>
    public int Entities { get; set; }
    /// <summary>Load condition: the non-monster entities the places walk matches (TargetResolver.Places).</summary>
    public int Places { get; set; }
    /// <summary>Gathering places and recording sightings (2 Hz, before resolving), smoothed, microseconds.</summary>
    public double SightingsScanUs { get; set; }
    /// <summary>Load condition: goal cells the objective resolved to (a Radar tile place can be hundreds).</summary>
    public int Goals { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
