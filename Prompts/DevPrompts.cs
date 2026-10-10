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
           plugin itself needs a full HUD restart: hud_restart reason="..." (it waits for other agents' tests and
           shows the request on the in-game card; never run the restart script yourself).
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

    [McpServerPrompt(Name = "optimize_plugin", Title = "Make a HUD plugin cheaper per frame, with measurements")]
    [Description("Find and fix what a plugin costs per frame: lint, trace, profile, fix, measure again.")]
    public static string OptimizePlugin(
        [Description("Plugin folder or project name, e.g. 'Whats An Azmeri Wisp'")] string plugin,
        [Description("'poe1' or 'poe2' (optional)")] string? game = null) => $"""
        Make the HUD plugin "{plugin}"{GameText(game)} cheaper per frame without changing what it shows. Measure
        before and after; report numbers, not impressions. Read knowledge pack shared/api-costs first (what HUD calls
        cost, and the cheaper equivalents).

        1. Static: hud_plugin_lint plugin="{plugin}". It lists expensive HUD calls on Tick/Render paths, with measured
           costs, and flags those in loops or per-item lambdas. These are candidates: on-demand code shows up too.
        2. Live cost: pipeline_trace (needs the bridge setting "Allow HUD Instrumentation"). Look at the plugin's
           Tick and Render ms per frame and at frame work (plugins vs HUD core). Do it where the plugin has work:
           a town with few entities hides per-entity costs.
        3. Where the time goes: profile_plugin name="{plugin}". It gives self and inclusive time per method. Self
           time that is high in a loop over entities usually means cold HUD calls (DistancePlayer ~2.9 us, Entity.Pos
           ~2.4 us, Stats ~25 us per entity per frame).
        4. Fix, in order of payoff:
           - Filter on cached members (Path, Type, IsValid, your own classification) before expensive ones.
           - Classify at 10-20 Hz and refresh positions every frame (Render.Pos on a held component).
           - Hold component references; take one Camera.Snapshot per frame.
           Keep behaviour identical; note any added latency, e.g. "detected up to 50 ms later".
        5. reload_plugin plugin="{plugin}" perf=true shows its Tick/Render cost before and after the reload. Repeat
           profile_plugin to confirm the hot method is gone.
        6. Report a before/after table (ms per frame, ms of CPU per second) and what each change traded.
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
        3. Experiment with the user (prompt guided_experiment). await_change with instruction="<one action>" puts
           the step on the HUD's agent guide card and captures before/after when they act. Ask for the change, then
           its undo: the answer flips on and back, and nothing else does. Repeat 2-3 times and read
           experiment_summary. For collections, memory_snapshot / memory_compare (labels=["Name", ...]) also work.
        4. Counterexamples. Think of states the population lacks (several flags at once, empty, max) and test them
           the same way before generalising.
        5. Explain it from the code. find_field_access offset=<off> bit=<bit> path=<one object> lists the game
           functions that read, write or bit-test the field (static, from Ghidra; never the running game), with
           decompiled excerpts. Look for:
           - serializers/deserializers: they name every member and its size, and show which flag gates which field;
           - UI code that turns the value into text;
           - writes: what sets it.
           Unmapped offsets the code reads next to yours are new fields: probe them the same way. The tool needs
           tools/ghidra-headless.ps1 running; the first query per struct takes minutes, then it is cached. A vtable
           at +0 also leads to the constructor (ghidra get_xrefs_to, then decompile_function).
        6. Report the finding with its evidence (counts, which experiment, counterexamples tried) and add it to the
           knowledge pack shared/memory-mapping if it's new. Remind the user of any in-game changes to undo.
        """;

    [McpServerPrompt(Name = "guided_experiment", Title = "Find something out together with the user, in game")]
    [Description("Answer a question about the game's data by running a guided experiment with the user: one in-game action " +
                 "per step, shown on the HUD's agent guide card, captured and repeated until the evidence is clear.")]
    public static string GuidedExperiment(
        [Description("What you want to find out, e.g. 'which memory follows switching stash tabs'")] string question,
        [Description("'poe1' or 'poe2' (optional)")] string? game = null) => $"""
        Find out "{question}"{GameText(game)} together with the user. They act in game and you observe; you never send input.
        The user reads the HUD's agent guide card, not the chat: knowledge shared/working-with-users is the contract for
        what you put on it. Read it once.

        1. Plan, short. Check experiment_presets for a ready-made one. Otherwise pick what to watch: value:<walker path>
           for HUD values, memory:<path>[:size] for an object's bytes (field names from the HUD's struct), collection:
           <path>[:Label] for every item of a list. Narrow objects only; whole UI elements flicker. Pick the actions: one
           per step, each with its undo (on/off, next/prev, to/from), with short kebab-case labels (next-tab, to-stash).
           Read each watched path once (eval_path / memory_layout) so a bad path never costs the user a step.
        2. Setup: if the experiment needs a window open or an item ready, put it on the card (guide status=info
           instruction="Open your stash on a tab with items" title="Experiment: <name>") and in chat. Then one or two
           chat lines: what the experiment shows and what they will do, how many times. Then start step 1 at once.
        3. Each step: await_change experiment=<name> label=<action> instruction="<one action, imperative, ~70 chars,
           with the key and the mouse target>" step=n steps=m watch=[...] timeoutMs=45000-60000. Count repeats as steps.
           The card shows DO THIS NOW, then CAPTURED with what changed, or TRY AGAIN. In chat, one line per capture;
           never narrate the waiting or repeat the instruction. Call the next await_change right after a capture.
           If the user isn't at the game right now, experiment_queue the steps instead (note = why, repeats = 2-3):
           they press Start on the in-game card when ready. Then run experiment_queue_wait in the background (it returns
           and collects the recordings when they finish) so you continue without them telling you they are done.
        4. Repeat each action 2-3 times. experiment_summary shows what changed EVERY time (the evidence) vs sometimes
           (side effects, or watches you added later). Item-dependent effects (stacks, slots) show up as "sometimes":
           ask what the user did when a result is surprising. Stop when the evidence is clear.
        5. On changed:false, check the cheap causes (panel open? window focused? does the value follow the action at
           all?) and narrow the watch if transientChanges is high; then the same label once more. A second miss means the
           watch is wrong, not the user: say so and change it.
        6. Watch for surprises: a HUD value that doesn't follow the action, or reads nonsense (a pointer where a count
           should be), is a mapping bug. Confirm it with a reliable neighbour (e.g. ServerInventory vs the UI element).
        7. Finish: guide status=done title="Experiment: <name>" instruction="<one-line result>". In chat: the result in
           one line, the evidence in one more, and the list of in-game changes to undo (an item left in the inventory,
           affinities moved, the stash on another tab), or "nothing to undo". Record new facts in findings.json with the
           experiment as evidence. The user can follow the whole run in the Memory View's Experiments tab (show_memory_view).
        """;

    private static string GameText(string? game) => string.IsNullOrWhiteSpace(game) ? "" : $" ({game})";
}
