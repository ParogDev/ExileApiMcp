using System.ComponentModel;
using ExileApiMcp.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>Live game-state reads from the HUD bridge. All read-only.</summary>
[McpServerToolType]
public static class GameStateTools
{
    private const string G = BridgeRegistry.GameParamDescription;

    [McpServerTool(Name = "get_player", Title = "Player state", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Player vitals (life, energy shield, mana), grid position, active buffs, and skills.")]
    public static async Task<CallToolResult> GetPlayer(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, "player", ct)).Result);

    [McpServerTool(Name = "get_area", Title = "Current area", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Current area: zone name, area level, and act.")]
    public static async Task<CallToolResult> GetArea(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, "area", ct)).Result);

    [McpServerTool(Name = "get_entities", Title = "Nearby entities", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Nearby entities sorted by distance. 'filter' narrows to 'monsters' (alive hostiles) or 'items' (ground drops).")]
    public static async Task<CallToolResult> GetEntities(BridgeRegistry bridges,
        [Description("Scan radius in grid units (default 200)")] int range = 200,
        [Description("'all', 'monsters', or 'items'")] string filter = "all",
        [Description(G)] string? game = null, CancellationToken ct = default)
    {
        var type = filter switch { "monsters" => "monsters", "items" => "items", _ => $"entities:{range}" };
        var (_, result) = await bridges.QueryAsync(game, type, ct);
        return ToolResults.JsonTruncatingEntities(result, "Use a smaller range or deep_scan with a path filter.");
    }

    [McpServerTool(Name = "deep_scan", Title = "Deep entity scan", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Full component dump (Render, Positioned, Animated, StateMachine, NPC, Life, Targetable, Chest, " +
                 "ObjectMagicProperties, MinimapIcon, Buffs, Stats, effects) for entities whose metadata path contains 'filter'.")]
    public static async Task<CallToolResult> DeepScan(BridgeRegistry bridges,
        [Description("Case-insensitive substring of the entity metadata path, e.g. 'Monsters/Spirit'")] string filter,
        [Description("Scan radius in grid units (default 500)")] int range = 500,
        [Description(G)] string? game = null, CancellationToken ct = default)
    {
        var (_, result) = await bridges.QueryAsync(game, $"deep:{filter}:{range}", ct);
        return ToolResults.JsonTruncatingEntities(result, "Narrow the filter or range.");
    }

    [McpServerTool(Name = "get_npc_dialog", Title = "NPC dialog", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("NPC dialog state: visibility, NPC name, dialog lines, lore-talk flag.")]
    public static async Task<CallToolResult> GetNpcDialog(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, "npcdialog", ct)).Result);

    [McpServerTool(Name = "get_map_data", Title = "Map data", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Map stats and selected quest flags.")]
    public static async Task<CallToolResult> GetMapData(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, "mapdata", ct)).Result);

    [McpServerTool(Name = "get_ui_panels", Title = "UI panels", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Visibility of game UI panels and visible child text. Panels a game doesn't have are omitted and listed in 'unsupported'.")]
    public static async Task<CallToolResult> GetUiPanels(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, "ui", ct)).Result);

    [McpServerTool(Name = "get_stash", Title = "Stash tabs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("All stash tabs with name, type, color and flags.")]
    public static async Task<CallToolResult> GetStash(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, "stash", ct)).Result);

    [McpServerTool(Name = "get_player_stats_raw", Title = "Raw player stat dictionary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("The full raw GameStat dictionary (enum name -> value, 200-500+ entries). Prefer stats_page / get_stat, " +
                 "which add Stats.dat keys, in-game text and categories.")]
    public static async Task<CallToolResult> GetPlayerStatsRaw(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, "playerstats", ct)).Result);

    [McpServerTool(Name = "get_all", Title = "Game state overview", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Combined snapshot: player (skills, actor), area, nearby entities, NPC dialog and map data.")]
    public static async Task<CallToolResult> GetAll(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default)
    {
        var (_, result) = await bridges.QueryAsync(game, "all", ct);
        return ToolResults.JsonTruncatingEntities(result, "Use get_entities for the full list.");
    }
}

/// <summary>Reflection-based introspection of the HUD's object model (read-only).</summary>
[McpServerToolType]
public static class EvalTools
{
    private const string G = BridgeRegistry.GameParamDescription;

    [McpServerTool(Name = "eval_path", Title = "Evaluate object path", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Evaluate a dotted path over the HUD object graph, starting at 'GameController'. Supports properties, " +
                 "fields, GetComponent<T>(), [index], [\"key\"], GetChildAtIndex(N), ToString(). Read-only, public members, " +
                 "150 ms budget. Namespaces differ per game (ExileCore vs ExileCore2) - use describe_type to explore.")]
    public static async Task<CallToolResult> EvalPath(BridgeRegistry bridges,
        [Description("e.g. GameController.Player.GetComponent<Life>().CurHP")] string expression,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, $"eval:{expression}", ct)).Result);

    [McpServerTool(Name = "describe_type", Title = "Describe type at path", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the public properties and methods of the object at a path (e.g. 'GameController.Player'). " +
                 "Use before eval_path to discover what exists in this game's HUD.")]
    public static async Task<CallToolResult> DescribeType(BridgeRegistry bridges,
        [Description("Dotted path starting with GameController")] string expression,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, $"describe:{expression}", ct)).Result);
}

/// <summary>Which HUD bridges exist, which are up, and what they can do.</summary>
[McpServerToolType]
public static class BridgeTools
{
    [McpServerTool(Name = "bridge_status", Title = "HUD bridge status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Status of every configured HUD bridge (PoE1 / PoE2): reachable or not, port, and the bridge's hello " +
                 "(game, HUD build, protocol version, features this game's HUD cannot provide). Call this first when unsure which game is running.")]
    public static async Task<CallToolResult> BridgeStatus(BridgeRegistry bridges, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(bridges.Bridges.Select(async b =>
        {
            var entry = new JObject { ["game"] = b.Game, ["bridgeDir"] = b.BridgeDir };
            if (!b.LooksAvailable)
            {
                entry["status"] = "not running";
                return entry;
            }
            try
            {
                await b.EnsureConnectedAsync(ct);
                entry["status"] = "connected";
                entry["port"] = b.Port;
                entry["hello"] = await b.SendRequestAsync("query", new JObject { ["type"] = "hello" }, ct);
            }
            catch (BridgeException ex)
            {
                entry["status"] = "unreachable";
                entry["error"] = ex.Message;
            }
            return entry;
        }));
        return ToolResults.Json(new JObject { ["bridges"] = new JArray(results) });
    }
}
