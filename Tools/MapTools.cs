using System.ComponentModel;
using ExileApiMcp.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>The current area's map as an image the model can look at (bridge map.image, from the Radar plugin).</summary>
[McpServerToolType]
public static class MapTools
{
    [McpServerTool(Name = "get_map_image", Title = "Area map (image)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(MapImageResult))]
    [Description("The current area's terrain map as a PNG image you can look at, with the player marked (red dot in a gold ring). " +
                 "Source (in 'source'): Radar's map when its build exposes one (PoE2), else the game's pathfinding grid drawn by the bridge " +
                 "(any game, no plugin needed); 'legend' says how to read it. Pixel = (grid - originGrid) * scale, " +
                 "so entity GridPos values from get_entities map onto it. Use for layout questions, pathing, or checking what a map plugin should draw.")]
    public static async Task<CallToolResult> GetMapImage(BridgeRegistry bridges,
        [Description("Crop to this many grid cells around the player (0 = whole area, trimmed to the terrain)")] int cropRadius = 0,
        [Description("Longest side of the image in pixels (64-2048, default 1024)")] int maxSize = 1024,
        [Description("Draw Radar's routes to its configured targets (Radar-sourced maps only)")] bool includeRoutes = false,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (_, result) = await bridges.CallAsync(game, "map.image", new JObject
        {
            ["cropRadius"] = cropRadius, ["maxSize"] = maxSize, ["includeRoutes"] = includeRoutes, ["markPlayer"] = true,
        }, ct);
        if (result is not JObject o || o["pngBase64"]?.Value<string>() is not { } b64)
            return TypedReply.Of<MapImageResult>(result);

        o.Remove("pngBase64");
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = o.ToString(Formatting.None) },
                ImageContentBlock.FromBytes(Convert.FromBase64String(b64), "image/png"),
            ],
            StructuredContent = Dto.Element(Dto.From<MapImageResult>(o)),
        };
    }
}
