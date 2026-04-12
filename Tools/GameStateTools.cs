using System.ComponentModel;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

[McpServerToolType]
public sealed class GameStateTools
{
    // MCP tool results get wrapped in a JSON array envelope that roughly doubles
    // the character count due to string escaping.  Stay under ~50K raw chars so
    // the envelope stays under the ~95K token limit.
    private const int MaxResponseChars = 50_000;

    private readonly BridgeClient _client;

    public GameStateTools(BridgeClient client)
    {
        _client = client;
    }

    [McpServerTool(Name = "get_player"), Description("Get player vitals (HP, ES, Mana), position, active buffs, and skills")]
    public async Task<string> GetPlayer()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("player"));
        return result.ToString();
    }

    [McpServerTool(Name = "get_area"), Description("Get current area info: zone name, level, and act")]
    public async Task<string> GetArea()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("area"));
        return result.ToString();
    }

    [McpServerTool(Name = "get_entities"), Description("Get nearby entities sorted by distance. Use range to control scan radius (default 200 grid units). Use filter to narrow by type: 'monsters' for alive hostiles, 'items' for ground drops, or omit for all.")]
    public async Task<string> GetEntities(
        [Description("Scan radius in grid units (default 200)")] int range = 200,
        [Description("Filter type: 'all', 'monsters', or 'items'")] string filter = "all")
    {
        await _client.EnsureConnectedAsync();
        var queryType = filter switch
        {
            "monsters" => "monsters",
            "items" => "items",
            _ => $"entities:{range}",
        };
        var result = await _client.SendRequestAsync("query", Param(queryType));
        return TruncateEntities(result);
    }

    [McpServerTool(Name = "get_npc_dialog"), Description("Get NPC dialog state: visibility, NPC name, dialog lines, lore talk flag")]
    public async Task<string> GetNpcDialog()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("npcdialog"));
        return result.ToString();
    }

    [McpServerTool(Name = "get_map_data"), Description("Get map stats, quest flags (Djinn/OrderOfThe/Faridun mechanics), and dialog depth")]
    public async Task<string> GetMapData()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("mapdata"));
        return result.ToString();
    }

    [McpServerTool(Name = "get_ui_panels"), Description("Get visibility state of all UI panels (NPC dialog, purchase, sell, trade, ritual, etc.) and visible children text")]
    public async Task<string> GetUiPanels()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("ui"));
        return result.ToString();
    }

    [McpServerTool(Name = "get_stash"), Description("Get all stash tabs with metadata: name, type, color, flags (premium/public/remove-only/hidden), affinity")]
    public async Task<string> GetStash()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("stash"));
        return result.ToString();
    }

    [McpServerTool(Name = "deep_scan"), Description("Deep component dump for entities matching a path filter. Returns all components: Render, Positioned, Animated, StateMachine, NPC, Life, Targetable, Chest, ObjectMagicProperties, MinimapIcon, Buffs, Stats, and effect components.")]
    public async Task<string> DeepScan(
        [Description("Filter string to match against entity Path (case-insensitive substring match)")] string filter,
        [Description("Scan radius in grid units (default 500)")] int range = 500)
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param($"deep:{filter}:{range}"));
        return TruncateEntities(result);
    }

    [McpServerTool(Name = "get_player_stats"), Description("Get the full player GameStat dictionary (500+ entries, untruncated). Use for detailed build analysis.")]
    public async Task<string> GetPlayerStats()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("playerstats"));
        return result.ToString();
    }

    [McpServerTool(Name = "get_all"), Description("Combined snapshot: full player (with skills, actor, rotation) + area + entities + NPC dialog + map data. Best tool for interactive game state overview.")]
    public async Task<string> GetAll()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("all"));
        return TruncateEntities(result);
    }

    [McpServerTool(Name = "get_bridge_status"), Description("Check bridge connection health: connected clients, pending requests")]
    public async Task<string> GetBridgeStatus()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("status", null);
        return result.ToString();
    }

    /// <summary>
    /// If the response exceeds the character budget, progressively remove
    /// entities from the end (furthest first, since they are distance-sorted)
    /// until it fits.  Adds a _truncated marker so the caller knows.
    /// </summary>
    private static string TruncateEntities(JToken result)
    {
        var json = result.ToString(Newtonsoft.Json.Formatting.None);
        if (json.Length <= MaxResponseChars)
            return json;

        if (result is not JObject obj || obj["entities"] is not JArray entities || entities.Count == 0)
            return json;

        var originalCount = entities.Count;
        while (json.Length > MaxResponseChars && entities.Count > 0)
        {
            entities.RemoveAt(entities.Count - 1);
            json = obj.ToString(Newtonsoft.Json.Formatting.None);
        }

        obj["_truncated"] = $"Showing {entities.Count} of {originalCount} entities (response too large). Use get_entities for full list.";
        return obj.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static JObject Param(string queryType) => new() { ["type"] = queryType };
}
