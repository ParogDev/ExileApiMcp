using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contract of get_map_image: the image's metadata (the PNG itself is an image content block).

/// <summary>How to read the map image: its source, size, where the player is, and grid-to-pixel mapping.</summary>
public sealed class MapImageResult
{
    /// <summary>radar (Radar's map) | grid (the pathfinding grid drawn by the bridge).</summary>
    public string Source { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Area { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>The player's grid cell, null when not on the map.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public List<int>? PlayerGrid { get; set; }
    /// <summary>pixel = (grid - originGrid) * scale.</summary>
    public MapImageMapping Mapping { get; set; } = new();
    public string Legend { get; set; } = "";
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MapImageMapping
{
    public List<int> OriginGrid { get; set; } = [];
    public double Scale { get; set; }
    public List<int> AreaGrid { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
