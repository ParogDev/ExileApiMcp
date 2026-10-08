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
        2. Edit the source. The HUD does NOT recompile on save. Call reload_plugin plugin="{plugin}": it
           recompiles and reloads just this plugin in the running HUD (like its menu Reload button) and returns
           ok/error plus whatever the plugin logged on load. A local `dotnet build` of the plugin first catches
           compile errors without pausing the HUD. Only a brand-new plugin folder or a change to the bridge
           plugin itself needs a full HUD restart (scaffolding repo: tools/restart-hud.ps1 -Game poe1|poe2).
        3. On error, fix what "error" says (compiler diagnostics with file and line) and reload again.
           hud_plugins shows the same status from disk if the bridge is down.
        4. Check runtime: reload_plugin's loggedSinceReload, then hud_log level="error" plugin="{plugin}" after
           exercising the feature in game.
        5. Verify behaviour live, cheapest first: the get_* tools for state, eval_path for one value,
           describe_type / hud_type to discover members (hud_type works offline and sees non-public members;
           namespaces differ: ExileCore on poe1, ExileCore2 on poe2), deep_scan only when you need whole entities.
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

    [McpServerPrompt(Name = "probe_memory", Title = "Find what a memory field means, with evidence")]
    [Description("Reach an empirical answer about unknown or suspect memory (a new member, a flag bit, a moved field): " +
                 "observe, check across a population, run a one-variable experiment, hunt counterexamples, confirm in Ghidra.")]
    public static string ProbeMemory(
        [Description("Walker path to the object (or a collection of them), e.g. GameController.IngameState.ServerData.PlayerStashTabs")] string path,
        [Description("What you want to find out, e.g. 'which bit marks a tab with an affinity'")] string question,
        [Description("'poe1' or 'poe2' (optional)")] string? game = null) => $"""
        Answer "{question}" for `{path}`{GameText(game)} with evidence: counts and counterexamples, not one sample.
        Everything here only reads the game. Changes in game are the user's to make: ask for exactly one at a time.

        1. Observe. memory_layout path=<one object> (add extend=64 to see past the struct) shows what the HUD maps,
           the unmapped ranges and the structure-looking data in them. Form a hypothesis: offset, width, meaning.
        2. Population. If `{path}` is (or has) a collection, run memory_correlate on it with labels for the properties
           you already know (e.g. ["TabType","Affinity","Name"]). Accept a bit only with 0 counterexamples and at
           least 3 items on each side. Read the near-misses: their counterexample items often name the real rule.
           Results within a label's own storage are expected.
        3. Experiment. memory_snapshot name=baseline (collections: labels=["Name", ...] so items match across
           snapshots). Ask the user to change ONE thing (and to confirm it in game if the game needs that), then
           memory_snapshot name=on. Ask them to undo it, then memory_snapshot name=off. memory_compare
           names=[baseline,on,off]: the answer flips on and back, and nothing else does. For fast state, use
           watch_memory while they act.
        4. Counterexamples. Think of states the population lacks (several flags at once, empty, max) and test them
           the same way before generalising.
        5. Confirm in code when it matters. Results carry 'ghidra' addresses. A vtable at +0 leads to the
           constructor (ghidra get_xrefs_to, then decompile_function), which writes every member in order. Code that
           tests the bit names its meaning.
        6. Report the finding with its evidence (counts, which experiment, counterexamples tried) and add it to the
           knowledge pack shared/memory-mapping if it's new. Remind the user of any in-game changes to undo.
        """;

    private static string GameText(string? game) => string.IsNullOrWhiteSpace(game) ? "" : $" ({game})";
}
