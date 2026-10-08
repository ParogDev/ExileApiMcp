using ExileApiMcp.Bridge;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Hosting;

/// <summary>Server identity, instructions and capabilities shared by the stdio and HTTP hosts.</summary>
internal static class McpSetup
{
    public const string Version = "3.3.0";

    private const string Instructions = """
        Live game state from Path of Exile HUD overlays, for developing and debugging HUD plugins.
        Two games are supported, each through its own HUD: PoE1 (ExileApi / ExileCore, net10) and PoE2
        (ExileCore2, net8). Usually only one HUD runs at a time; pass game: 'poe1' | 'poe2' when both do.
        Call bridge_status first if unsure which game is up - its hello lists what that game's HUD cannot
        provide (fields for those are omitted, never faked).

        Player stats are keyed by Stats.dat key (e.g. 'fire_damage_resistance_%'), which is stable across
        patches and games. The stats *view* (pins, filter, selection, sort) is shared with the user: the
        in-game HUD panel and the interactive app show the same state, so set_stat_pinned / select_stat /
        set_stats_filter are visible to the user - use them to point at what you are discussing.
        show_player_stats opens that view as an interactive panel where the client supports MCP Apps; while it
        is open, the user's selected and pinned stats arrive in your context, so "this stat" means the selection.

        eval_path / describe_type walk the live HUD object model by reflection (namespaces differ per game).
        hud_find_types / hud_type are the offline API reference: every type and member (non-public too) of
        the HUD's DLLs, with the game and HUD closed. Start there before guessing a member name; on PoE2
        it is the only API reference (no source). GameOffsets2 offsets are decoys (flagged "suspect").

        Plugin development: hud_plugins says whether the HUD compiled and loaded each source plugin (with
        compiler errors), hud_log shows the HUD's log since it started. Both read the HUD folders on disk, so
        they work with the game closed. The HUD compiles source plugins when it starts (not on save), so
        after an edit and a HUD restart, check hud_plugins instead of asking the user. Prompts
        plugin_dev_loop and investigate_stat describe the full workflows.
        All tools are read-only toward the game; none send input.
        """;

    public static IMcpServerBuilder AddExileApiMcp(this IServiceCollection services)
    {
        services.AddSingleton<BridgeRegistry>();
        return services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "ExileApi MCP", Title = "Path of Exile HUD", Version = Version };
                o.ServerInstructions = Instructions;
            })
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly()
            .WithMcpApps();
    }
}
