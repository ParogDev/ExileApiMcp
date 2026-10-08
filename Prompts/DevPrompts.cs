using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Prompts;

/// <summary>
/// Workflows as MCP prompts: the tool sequences that work, written down once, so any client's
/// agent can follow them instead of rediscovering them. Keep each one short and ordered by what
/// is cheapest and most informative first.
/// </summary>
[McpServerPromptType]
public static class DevPrompts
{
    [McpServerPrompt(Name = "plugin_dev_loop", Title = "Edit, compile and verify a HUD plugin")]
    [Description("The edit -> compile -> verify-live loop for a HUD source plugin, using this server's tools.")]
    public static string PluginDevLoop(
        [Description("Plugin folder or project name, e.g. 'Whats A Mirage'")] string plugin,
        [Description("'poe1' or 'poe2' (optional)")] string? game = null) => $"""
        Work on the HUD plugin "{plugin}"{GameText(game)} using this loop. Don't ask the user to check the HUD for you;
        these tools show what it did.

        1. Baseline: call hud_plugins with plugin="{plugin}" to see its status in the HUD's latest run, and
           hud_log with plugin="{plugin}" for runtime errors it already logs. An Errors.txt marked stale is history.
        2. Edit the source. The HUD does NOT recompile on save: source plugins are compiled when the HUD starts
           (or from the plugin's Reload button in the HUD menu). So restart the HUD (in the scaffolding repo:
           tools/restart-hud.ps1 -Game poe1|poe2) or ask the user to press Reload. Before that, a local
           `dotnet build` of the plugin catches compile errors in seconds without touching the HUD.
        3. Check the compile: call hud_plugins again. Expect status "loaded" (or "cached" when nothing
           changed) with a newer "at" than step 1.
           On "failed", fix what "error" says (compiler diagnostics with file and line) and repeat.
        4. Check runtime: hud_log level="error" plugin="{plugin}". New errors after "at" came from your change.
        5. Verify behaviour live, cheapest first: the get_* tools for state, eval_path for one value,
           describe_type to discover members (namespaces differ: ExileCore on poe1, ExileCore2 on poe2),
           deep_scan only when you need whole entities.
        6. When the change involves player stats, use select_stat / set_stat_pinned so the user sees in the
           HUD panel exactly the values you are reasoning about.
        Report what you verified and how (which tool showed what), not just that it should work.
        """;

    [McpServerPrompt(Name = "investigate_stat", Title = "Explain a player stat from live evidence")]
    [Description("Explain where a player stat's value comes from, using live data and the shared stats view.")]
    public static string InvestigateStat(
        [Description("Stats.dat key, e.g. 'fire_damage_resistance_%'")] string key,
        [Description("'poe1' or 'poe2' (optional)")] string? game = null) => $"""
        Explain the player stat `{key}`{GameText(game)} from evidence, not memory.

        1. get_stat key="{key}": value, in-game text, record type, and whether the player has it at all.
        2. stats_page with a filter on the stat's core words (e.g. "fire_damage_resistance" -> "fire") to find
           its relatives: base_*, uncapped_*, maximum_*, and *_from_* contributions. Values that explain each
           other usually sit together.
        3. select_stat key="{key}" and pin the relatives that matter (set_stat_pinned), so the user sees them in
           the in-game panel and the stats app while you explain.
        4. State the relationship as a hypothesis with numbers (e.g. "uncapped 81 - capped at maximum 75").
           Check it against live values. When the user can act in game, say what to do and re-read after.
        5. Absent stats count as 0. PoE2 resistances come in base_/plain/uncapped_ layers, and the max-res stats
           may be missing from the dictionary: say so rather than assuming the 75% default silently.
        """;

    private static string GameText(string? game) => string.IsNullOrWhiteSpace(game) ? "" : $" ({game})";
}
