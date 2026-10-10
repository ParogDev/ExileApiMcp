using ExileApiMcp.Bridge;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Hosting;

/// <summary>Server identity, instructions and capabilities shared by the stdio and HTTP hosts.</summary>
internal static class McpSetup
{
    public const string Version = "3.62.0";

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

        explore_object maps live HUD data: start at 'GameController' and follow paths. Each line is
        name: type = preview, and entities list their components. Read the C# accessor for plugin code from its
        result rather than guessing member names. find_in_object answers "where does this value live?" (value=356) or
        "which members mention X?" (name=resist). show_data_explorer opens the same tree for the user.
        memory_layout shows what the HUD maps in an object's struct vs live memory, and what it doesn't: unmapped
        ranges and structure-looking data in them. watch_memory finds the bytes and bits that change when the user does
        something. Their 'ghidra' addresses go straight to the ghidra MCP (vtable -> xrefs -> constructor). Knowledge
        pack shared/memory-mapping has the method. show_memory_view opens it for the user. Read-only.
        Not sure which tool fits? Knowledge pack shared/tool-map maps questions to tools.
        "Why is the HUD slow / laggy / not drawing?": start with hud_health_report (one call, ends with the next step).
        Overlay accuracy and HUD performance (a drawing that lags or wobbles, a plugin that costs frame time): pipeline_trace,
        profile_plugin, overlay_accuracy, render_lab; method and measured findings in knowledge pack shared/render-fidelity.
        Writing or optimising plugin code: knowledge pack shared/api-costs has measured ns per HUD API call and the cheaper equivalents.
        Work WITH the user. When an answer depends on game state they can change (what a field means, whether a mapping
        holds, what an action does), don't guess: run a guided experiment (prompt guided_experiment). Ask for one action
        per step, shown in the game itself (await_change with instruction drives the HUD's agent guide card: waiting ->
        captured / failed), repeat it 2-3 times, then experiment_summary - what changes every time is the evidence.
        Users find this engaging and it settles questions that static reading can't. Never send input yourself.
        When you ask for a click, also show where: pass highlight (items by name, UI element paths, screen areas; tiers
        primary|secondary|context; ordered sequences) or call the highlight tool.
        For multi-step tasks (open a dialog, tick, confirm) use guide_flow (recipes in Knowledge/flows.json): the HUD shows
        the next best action from the game state and re-plans when the user navigates away.
        If the user may not be at the game right now, queue the step instead (experiment_queue): it waits on the in-game
        card until they press Start, the HUD records it without you, and experiment_queue_status collects it later.
        Don't wait for the user to say they are done: run experiment_queue_wait in the background and continue when it returns.
        Passive learning while the user plays: observe action=start (tell them; read-only), then observe_wait in the
        background wakes you when an unmapped panel opens or the area or level changes - map it, record findings,
        repeat. Knowledge pack shared/passive-learning.
        Several agents may share one HUD (Claude Code sessions in worktrees, Claude Desktop, scripts). This server identifies
        itself to the HUD by its working directory's git branch (or HEXILE_AGENT), so the guide card and log say who asked.
        hud_sessions shows who else is connected and what they are doing. Restart the HUD only through hud_restart: it waits
        while another agent's measurement, recording, piloted step or compile runs, shows the request on the in-game card
        (Restart now / Not now), and merges with a restart already under way. Never run the restart script directly. Before
        a long measurement or a piloted test that spans several tool calls, hud_lease acquire keeps other agents' restarts
        away until you release it; the tools' own activity is covered without one.
        experiment_presets has ready-made stash experiments. Knowledge pack shared/working-with-users says how to word
        an instruction and what goes on the card, in detail and in chat. The Memory View's Experiments tab
        (show_memory_view) lets the user run a preset themselves or follow your run step by step.
        Before asking the user, try what needs nobody: game_data (the game's data tables, e.g. the names behind an id or
        bit; row n is usually id n), find_in_game_data (which table column holds a set of ids seen in memory),
        memory_correlate over a whole collection, find_field_access + code_struct_layout
        (the struct as the game's code reads it, diffed with the HUD's). Ask for an in-game action only for what those
        can't show.
        find_field_access explains a field from the game's code: the functions that read, write or bit-test it,
        decompiled from the Ghidra copy (static; never the running game). Needs Ghidra headless running.
        eval_path / describe_type walk the live HUD object model by reflection (namespaces differ per game).
        watch_object samples a path over time and reports only the fields that changed: run it while the user
        does something in game to find which field reflects it.
        get_map_image returns the area map as an image (player marked) - look at it for layout and pathing questions.
        hud_plugin_perf / hud_plugin_settings / plugin_bridge_methods read the running HUD by reflection: what each
        plugin costs per frame, how it is configured right now, and the cross-plugin API it exposes.
        knowledge has short verified packs (dev loop, data map, PoE1 vs PoE2 data differences, PoE2 API differences
        and decoy offsets, player stats):
        read the relevant one before guessing how the HUD behaves.
        run_csharp runs a C# script inside the HUD when you need logic, not just a value (the user must enable
        it in the bridge settings). Inspect only: never send input or write memory.
        hud_find_types / hud_type are the offline API reference: every type and member (non-public too) of
        the HUD's DLLs, with the game and HUD closed. Start there before guessing a member name; on PoE2
        it is the only API reference (no source). GameOffsets2 offsets are decoys (flagged "suspect").

        Plugin development: hud_plugins says whether the HUD compiled and loaded each source plugin (with
        compiler errors), hud_log shows the HUD's log since it started. Both read the HUD folders on disk, so
        they work with the game closed. The HUD compiles source plugins at startup, not on save: after an
        edit call reload_plugin, which recompiles that one plugin in the running HUD and reports the result.
        Prompts plugin_dev_loop and investigate_stat describe the full workflows.
        All tools are read-only toward the game; none send input.
        """;

    /// <summary>The most telling argument of a call (path / expression / key...), shortened, for the guide log.</summary>
    /// <summary>Ask the client for its roots once (2 s budget); a client without roots, or stateless HTTP, is not asked again.</summary>
    private static async Task AdoptClientRootsAsync(McpServer server)
    {
        try
        {
            if (SessionIdentity.Kind != "mcp" || server.ClientCapabilities?.Roots == null) { SessionIdentity.RootsUnavailable(); return; }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var r = await server.RequestRootsAsync(new ListRootsRequestParams(), cts.Token);
            if (!SessionIdentity.AdoptRoots(r.Roots.Select(x => x.Uri)))
                Console.Error.WriteLine($"[Session] none of the client's {r.Roots.Count} root(s) is a git checkout; keeping '{SessionIdentity.Label}'");
        }
        catch (Exception ex)
        {
            SessionIdentity.RootsUnavailable();
            Console.Error.WriteLine($"[Session] roots/list failed ({ex.Message}); keeping '{SessionIdentity.Label}'");
        }
    }

    private static string CallHint(IDictionary<string, System.Text.Json.JsonElement>? args)
    {
        if (args == null) return "";
        foreach (var k in new[] { "path", "expression", "key", "plugin", "id", "offset", "address", "name" })
            if (args.TryGetValue(k, out var v) && v.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number)
            {
                var s = v.ToString();
                if (s.StartsWith("GameController.", StringComparison.Ordinal)) s = s["GameController.".Length..];
                if (s.Length > 60) s = "..." + s[^57..];
                return $" {s}";
            }
        return "";
    }

    /// <summary>The failure an unhandled tool exception stands for: the exception chain's types and messages (no stack:
    /// it can carry build paths). A binding failure (JsonException, "Path: $") doesn't name its argument, so the arguments
    /// are checked against the tool's inputSchema and the ones whose JSON kind doesn't fit are named.</summary>
    internal static string DescribeFailure(string tool, Exception ex, IDictionary<string, JsonElement>? args, JsonElement? inputSchema)
    {
        while (ex is System.Reflection.TargetInvocationException or AggregateException && ex.InnerException != null) ex = ex.InnerException;
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 4; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        var mismatches = SchemaMismatches(args, inputSchema);
        var hint = mismatches.Count > 0 ? $". Wrong argument type: {string.Join("; ", mismatches)}"
            : ex is JsonException or ArgumentException or FormatException or InvalidCastException
                ? " (often an argument of the wrong type: check the call against the tool's inputSchema)" : "";
        return $"{tool} failed: {string.Join(" <- ", parts).TrimEnd('.')}{hint}";
    }

    /// <summary>"'kinds' expects array|null, got string" for each argument whose JSON kind its schema type doesn't allow.</summary>
    internal static List<string> SchemaMismatches(IDictionary<string, JsonElement>? args, JsonElement? inputSchema)
    {
        var found = new List<string>();
        if (args == null || inputSchema is not { ValueKind: JsonValueKind.Object } schema
            || !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return found;
        foreach (var (key, value) in args)
        {
            if (!props.TryGetProperty(key, out var p) || !p.TryGetProperty("type", out var t)) continue;
            var allowed = t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [t.GetString() ?? ""];
            var got = value.ValueKind switch
            {
                JsonValueKind.String => "string", JsonValueKind.Array => "array", JsonValueKind.Object => "object",
                JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Null or JsonValueKind.Undefined => "null",
                _ => value.TryGetInt64(out _) ? "integer" : "number",
            };
            if (!allowed.Contains(got) && !(got == "integer" && allowed.Contains("number")))
                found.Add($"'{key}' expects {string.Join("|", allowed)}, got {got} {Clip(value.GetRawText())}");
        }
        return found;
    }

    private static string Clip(string s) => s.Length > 40 ? s[..37] + "..." : s;

    public static IMcpServerBuilder AddExileApiMcp(this IServiceCollection services)
    {
        services.AddSingleton<BridgeRegistry>();
        services.AddSingleton<ObserveHub>();
        services.AddSingleton<PerfHub>();
        // Every hub serves its URI space to subscriptions/listen (Hosting/Subscriptions.cs).
        services.AddSingleton<IResourceHub>(sp => sp.GetRequiredService<ObserveHub>());
        services.AddSingleton<IResourceHub>(sp => sp.GetRequiredService<PerfHub>());
        // Light/dark icon pairs for everything registered with an IconSet icon (Hosting/IconThemes.cs).
        services.PostConfigure<McpServerOptions>(IconThemes.Apply);
        return services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "ExileApi MCP", Title = "Path of Exile HUD", Version = Version, Icons = IconSet.Server };
                o.ServerInstructions = Instructions;
                // Resource subscriptions are delivered on subscriptions/listen streams (Hosting/Subscriptions.cs).
                o.Capabilities ??= new ServerCapabilities();
                o.Capabilities.Resources ??= new ResourcesCapability();
                o.Capabilities.Resources.Subscribe = true;
            })
            .WithSubscriptionsListenHandler(Subscriptions.Listen)
            .WithRequestFilters(f => f.AddCallToolFilter(next => async (request, ct) =>
            {
                // One stderr line per call, which clients such as Claude Desktop keep in their per-server log:
                // the only record of what an MCP App actually called. Successful polls are skipped (1/s).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var name = request.Params?.Name ?? "?";
                // Started outside any repo (the desktop app starts user-scope servers in System32): learn the session's
                // project from the client's roots once, before anything tells the HUD who we are (SessionIdentity).
                if (SessionIdentity.NeedsRoots) await AdoptClientRootsAsync(request.Server);
                // The in-game guide's log shows what the agent is doing (best effort, fire and forget).
                // Polls and the experiment tools stay out of it: await_change writes its own lines, and the Memory View's
                // experiment runner re-reads the record and presets while it follows along.
                if (name is not ("stats_ui_state" or "guide" or "await_change" or "experiment_summary" or "experiment_presets" or "experiment_status" or "experiment_step_start" or "experiment_step_cancel" or "experiment_queue_status" or "guide_state" or "bridge_status" or "observe_wait" or "observe_events" or "hud_sessions")
                    && request.Services?.GetService(typeof(BridgeRegistry)) is BridgeRegistry bridges)
                    _ = ExileApiMcp.Tools.GuideTools.LogAsync(bridges, null, $"{SessionIdentity.Label}: {name}{CallHint(request.Params?.Arguments)}", "agent", CancellationToken.None);
                try
                {
                    var result = await next(request, ct);
                    if (result.IsError == true || name != "stats_ui_state")
                        Console.Error.WriteLine($"[call] {name} {sw.ElapsedMilliseconds}ms{(result.IsError == true ? " isError" : "")}");
                    return result;
                }
                catch (Exception ex) when (ex is McpException || ct.IsCancellationRequested)
                {
                    // McpException: the SDK already shows its message. Cancellation: nobody is listening.
                    Console.Error.WriteLine($"[call] {name} {sw.ElapsedMilliseconds}ms failed: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    // Anything else the SDK would turn into a bare "An error occurred invoking '<name>'." (argument binding
                    // included): say what broke instead, so the caller can fix the call or report the real failure.
                    Console.Error.WriteLine($"[call] {name} {sw.ElapsedMilliseconds}ms failed: {ex}");
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = DescribeFailure(name, ex, request.Params?.Arguments, (request.MatchedPrimitive as McpServerTool)?.ProtocolTool.InputSchema) }] };
                }
            }))
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly()
            .WithMcpApps();
    }
}
