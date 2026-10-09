using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ExileApiMcp.Apps;
using ExileApiMcp.Bridge;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Player stats and the shared stats view state. The HUD plugin owns the state (pins, filter,
/// category, selection, sort) and versions it with "rev"; the in-HUD panel, the MCP App and agents
/// all read and change the same state through these tools, so every surface stays in sync.
/// Stats are identified by Stats.dat key (e.g. "fire_damage_resistance_%"), stable across patches
/// and games.
/// </summary>
[McpServerToolType]
public static class StatsTools
{
    private const string G = BridgeRegistry.GameParamDescription;
    private const string KeyDesc = "Stats.dat key, e.g. 'fire_damage_resistance_%' (see stats_page)";
    private const string RevDesc = "Optional optimistic-concurrency check: the rev you last saw. If the state changed since, " +
                                   "nothing is applied and error 'rev_mismatch' is returned with the current state.";
    private const string Categories = "'all', 'vitals', 'resistances', 'defense', 'offense', 'charges', 'movement', 'other'";

    // ── Showing ──────────────────────────────────────────────────────

    [McpServerTool(Name = "show_player_stats", Title = "Show player stats", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ShowPlayerStatsResult), IconSource = ExileApiMcp.Hosting.IconSet.PlayerStatsLight)]
    [McpAppUi(ResourceUri = PlayerStatsApp.ResourceUri)]
    // Legacy flat key, alongside the nested _meta.ui.resourceUri, as the official ext-apps servers send it
    // (registerAppTool): hosts built against older MCP Apps drafts look for this one.
    [McpMeta("ui/resourceUri", PlayerStatsApp.ResourceUri)]
    [Description("Show the player's stats: vitals, resistances, pinned stats and a searchable stat table. Opens an " +
                 "interactive panel in clients that support MCP Apps (synced with the in-game HUD panel); other clients " +
                 "get a text summary.")]
    public static async Task<CallToolResult> ShowPlayerStats(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default)
    {
        var (bridge, uiState) = await bridges.CallAsync(game, "stats.ui_state", null, ct);
        var (_, resists) = await bridges.CallAsync(bridge.Game == "auto" ? game : bridge.Game, "stats.page",
            new JObject { ["category"] = "resistances", ["pageSize"] = 50, ["filter"] = "" }, ct);

        var summary = new JObject
        {
            ["game"] = uiState["game"],
            ["rev"] = uiState["rev"],
            ["inGame"] = uiState["inGame"],
            ["vitals"] = uiState["vitals"],
            ["state"] = uiState["state"],
            ["pinned"] = resists["pinned"],
            ["resistances"] = resists["items"],
            ["categories"] = resists["categories"],
        };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = DescribeForText(summary) }],
            StructuredContent = Dto.Element(TypedReply.Parse<ShowPlayerStatsResult>(summary)),
        };
    }

    private static string DescribeForText(JObject s)
    {
        var sb = new StringBuilder();
        sb.Append($"{s["game"]?.ToString().ToUpperInvariant()} player stats (state rev {s["rev"]}).");
        if (s["vitals"] is JObject v)
            sb.Append($" Life {v["hp"]}/{v["maxHp"]}, ES {v["es"]}/{v["maxEs"]}, Mana {v["mana"]}/{v["maxMana"]}.");
        if (s["resistances"] is JArray r)
        {
            var main = r.Where(x => x["key"]?.ToString() is { } k && !k.StartsWith("base_") && !k.StartsWith("uncapped_")
                                    && !k.Contains("number_of")).Take(8);
            sb.Append(" Resistances: ").Append(string.Join(", ", main.Select(x => $"{x["key"]}={x["value"]}"))).Append('.');
        }
        if (s["pinned"] is JArray { Count: > 0 } pins)
            sb.Append(" Pinned: ").Append(string.Join(", ", pins.Select(x => $"{x["key"]}={x["value"]}"))).Append('.');
        sb.Append(" Use stats_page / get_stat for details.");
        return sb.ToString();
    }

    // ── Reading ──────────────────────────────────────────────────────

    [McpServerTool(Name = "stats_ui_state", Title = "Stats view state (poll)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(StatsUiStateResult))]
    // Visible to the model too: Claude Desktop's local bridge only routes tools whose visibility includes
    // "model", so an app-only tool is unreachable from the panel there ("Unable to reach exileapi").
    [McpAppUi(ResourceUri = PlayerStatsApp.ResourceUri, Visibility = [McpUiToolVisibility.Model, McpUiToolVisibility.App])]
    [McpMeta("ui/resourceUri", PlayerStatsApp.ResourceUri)]
    [Description("Shared stats view state plus live vitals; the stats panel polls it. With sinceRev equal to the " +
                 "current rev, returns only {rev, unchanged:true, vitals}. Agents usually want stats_page / get_stat instead.")]
    public static async Task<CallToolResult> StatsUiState(BridgeRegistry bridges,
        [Description("The rev you already have; omit to always get the full state")] long? sinceRev = null,
        [Description(G)] string? game = null, CancellationToken ct = default)
    {
        var p = new JObject();
        if (sinceRev.HasValue) p["sinceRev"] = sinceRev.Value;
        return TypedReply.Of<StatsUiStateResult>((await bridges.CallAsync(game, "stats.ui_state", p, ct)).Result);
    }

    [McpServerTool(Name = "stats_page", Title = "Player stats (filtered page)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(StatsPageResult), IconSource = ExileApiMcp.Hosting.IconSet.PlayerStatsLight)]
    [Description("A page of the player's current stats: Stats.dat key, value, in-game text and category, plus " +
                 "per-category counts and all pinned stats. Arguments override the shared filter/category/sort for this " +
                 "read only (they don't change what the HUD shows - use set_stats_filter for that).")]
    public static async Task<CallToolResult> StatsPage(BridgeRegistry bridges,
        [Description("Substring matched against stat key and in-game text, e.g. 'fire' or 'critical'")] string? filter = null,
        [Description("Category: " + Categories)] string? category = null,
        [Description("Only pinned stats")] bool pinnedOnly = false,
        [Description("0-based page")] int page = 0,
        [Description("Items per page (1-200, default 50)")] int pageSize = 50,
        [Description("'category' (default), 'key' or 'value'")] string? sortBy = null,
        [Description("Sort descending")] bool? sortDesc = null,
        [Description(G)] string? game = null, CancellationToken ct = default)
    {
        var p = new JObject { ["pinnedOnly"] = pinnedOnly, ["page"] = page, ["pageSize"] = pageSize };
        if (filter != null) p["filter"] = filter;
        if (category != null) p["category"] = category;
        if (sortBy != null) p["sortBy"] = sortBy;
        if (sortDesc != null) p["sortDesc"] = sortDesc;
        return TypedReply.Of<StatsPageResult>((await bridges.CallAsync(game, "stats.page", p, ct)).Result);
    }

    [McpServerTool(Name = "get_stat", Title = "One stat", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(StatDetailResult))]
    [Description("One stat by Stats.dat key: current value and in-game text (or present:false when the player lacks it), " +
                 "Stats.dat record type and weapon-local flag, pinned state. Errors with unknown_stat for keys this game build doesn't have.")]
    public static async Task<CallToolResult> GetStat(BridgeRegistry bridges, [Description(KeyDesc)] string key,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        TypedReply.Of<StatDetailResult>((await bridges.CallAsync(game, "stats.get", new JObject { ["key"] = key }, ct)).Result);

    // ── Changing the shared view (visible to the user in the HUD and the app) ──

    [McpServerTool(Name = "set_stat_pinned", Title = "Pin / unpin a stat", Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(StatsMutationResult))]
    [Description("Pin or unpin a stat in the shared stats view (shows in the HUD panel and the app). Idempotent.")]
    public static async Task<CallToolResult> SetStatPinned(BridgeRegistry bridges, [Description(KeyDesc)] string key,
        [Description("true to pin, false to unpin")] bool pinned = true,
        [Description(RevDesc)] long? expectedRev = null,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        await Mutate(bridges, game, "stats.set_pinned", new JObject { ["key"] = key, ["pinned"] = pinned }, expectedRev, ct);

    [McpServerTool(Name = "set_stats_filter", Title = "Set stats filter", Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(StatsMutationResult))]
    [Description("Set the shared stats view's search text and/or category (what the user sees in the HUD panel and the app).")]
    public static async Task<CallToolResult> SetStatsFilter(BridgeRegistry bridges,
        [Description("Search text; empty string clears it; omit to keep")] string? text = null,
        [Description("Category: " + Categories + "; omit to keep")] string? category = null,
        [Description(RevDesc)] long? expectedRev = null,
        [Description(G)] string? game = null, CancellationToken ct = default)
    {
        var p = new JObject();
        if (text != null) p["text"] = text;
        if (category != null) p["category"] = category;
        return await Mutate(bridges, game, "stats.set_filter", p, expectedRev, ct);
    }

    [McpServerTool(Name = "select_stat", Title = "Select a stat", Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(StatsMutationResult))]
    [Description("Highlight one stat in the shared view (e.g. to point the user at it), or clear the selection with no key.")]
    public static async Task<CallToolResult> SelectStat(BridgeRegistry bridges,
        [Description(KeyDesc + "; omit to clear")] string? key = null,
        [Description(RevDesc)] long? expectedRev = null,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        await Mutate(bridges, game, "stats.select", new JObject { ["key"] = key }, expectedRev, ct);

    [McpServerTool(Name = "set_stats_view", Title = "Set stats view options", Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(StatsMutationResult))]
    [Description("Set the shared view's sort and whether the in-HUD stats panel is open.")]
    public static async Task<CallToolResult> SetStatsView(BridgeRegistry bridges,
        [Description("'category', 'key' or 'value'; omit to keep")] string? sortBy = null,
        [Description("Sort descending; omit to keep")] bool? sortDesc = null,
        [Description("Open/close the in-HUD panel; omit to keep")] bool? panelOpen = null,
        [Description(RevDesc)] long? expectedRev = null,
        [Description(G)] string? game = null, CancellationToken ct = default)
    {
        var p = new JObject();
        if (sortBy != null) p["sortBy"] = sortBy;
        if (sortDesc != null) p["sortDesc"] = sortDesc;
        if (panelOpen != null) p["panelOpen"] = panelOpen;
        return await Mutate(bridges, game, "stats.set_view", p, expectedRev, ct);
    }

    private static async Task<CallToolResult> Mutate(BridgeRegistry bridges, string? game, string method, JObject p,
        long? expectedRev, CancellationToken ct)
    {
        if (expectedRev.HasValue) p["expectedRev"] = expectedRev.Value;
        return TypedReply.Of<StatsMutationResult>((await bridges.CallAsync(game, method, p, ct)).Result);
    }
}
