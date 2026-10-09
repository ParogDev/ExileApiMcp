using System.ComponentModel;
using System.Text;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>pipeline_trace: time the HUD's render pipeline (bridge pipeline.trace, Harmony + a memory-backend wrapper).</summary>
[McpServerToolType]
public static class PipelineTraceTools
{
    [McpServerTool(Name = "pipeline_trace", Title = "Time the HUD's render pipeline", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false, IconSource = ExileApiMcp.Hosting.IconSet.HudPerformanceLight)]
    [Description("""
        Measure how faithfully the HUD tracks the game, for a few seconds: HUD fps and frame-interval jitter, frame cost,
        Present; when the camera and the nearest players' positions are read and how old that data is when the frame
        reaches the screen (data age at Present), the camera-vs-entity data skew (a box drawn with one frame's camera
        around another frame's position), where in the frame each happens (timelineMs), and every plugin's Render cost.
        The HUD's own code is patched for the duration only (never protected methods, never the game) and restored.
        'calls' counts each hook: a hook that was patched but never hit, or a target in patches.refused, is the broken
        link to look at after a HUD update. Needs the bridge setting 'Allow HUD Instrumentation' (Dev Loop).
        Moving targets make the numbers meaningful: run it while players or monsters move and the camera pans.
        """)]
    public static async Task<CallToolResult> PipelineTrace(BridgeRegistry bridges,
        [Description("Trace length in ms (500-20000, default 4000)")] int durationMs = 4000,
        [Description("How many of the nearest players to watch (0-32, default 8)")] int entities = 8,
        [Description("Also return per-frame arrays (series: tMs, intervalMs, workMs, pluginsMs, gcPauseMs) for timelines; adds ~5 KB per second traced")] bool series = false,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        durationMs = Math.Clamp(durationMs, 500, 20_000);
        var (bridge, started) = await bridges.CallAsync(game, "pipeline.trace", new JObject { ["durationMs"] = durationMs, ["entities"] = entities, ["series"] = series }, ct);
        var id = started["id"]?.Value<string>();
        if (id == null) return ToolResults.Json(started);   // instrumentation_disabled / busy / harmony_unavailable
        var g = bridge.Game == "auto" ? game : bridge.Game;

        await Task.Delay(durationMs + 300, ct);
        JToken r = started;
        for (var i = 0; i < 20 && r["status"]?.Value<string>() == "running"; i++)
        {
            try { (_, r) = await bridges.CallAsync(g, "pipeline.trace_result", new JObject { ["id"] = id }, ct); }
            catch (McpException) { }
            if (r["status"]?.Value<string>() == "running") await Task.Delay(250, ct);
        }
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Summary(r) }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(r.ToString(Newtonsoft.Json.Formatting.None)),
        };
    }

    [McpServerTool(Name = "overlay_accuracy", Title = "How far HUD drawings are from the game", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("""
        Every HUD frame, project the nearest players twice: as plugins draw them (the HUD's cached camera and position)
        and from the camera matrix and position read fresh from game memory at that moment. Reports the pixel error
        (avg/p50/p95/max, frames over 1 px), split into the camera's and the position's share, plus how often each was
        stale. Offsets are found at start by matching the HUD's own values in fresh bytes (calibration in the result);
        projectionSelfCheckPx must be ~0. draw=true marks both on screen as 5 px squares (fresh: cyan, HUD: magenta;
        tools/fidelity finds them in screenshots). Read-only. Meaningful only while things move: ask the user to run
        around (camera panning).
        """)]
    public static async Task<CallToolResult> OverlayAccuracy(BridgeRegistry bridges,
        [Description("Run length in ms (500-60000, default 5000)")] int durationMs = 5000,
        [Description("How many of the nearest players (yours included) to track (1-32, default 8)")] int entities = 8,
        [Description("Mark both projections on screen")] bool draw = false,
        [Description("Track exactly this entity (an await_motion result's entityId)")] long? entityId = null,
        [Description("Track entities whose metadata path contains this (e.g. a static chest or NPC as an anchor) instead of the nearest players")] string? path = null,
        [Description("Draw the cyan (fresh) marker from the state this many ms ago (0-100), to align with the game image's own latency")] double delayMs = 0,
        [Description("Up to 3 more delays (ms) drawn as yellow, green and blue markers, so one camera pan measures several delays at once (tools/fidelity reports each)")] double[]? delays = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        durationMs = Math.Clamp(durationMs, 500, 60_000);
        var (bridge, started) = await bridges.CallAsync(game, "tracker.start", new JObject { ["durationMs"] = durationMs, ["entities"] = entities, ["draw"] = draw, ["path"] = path, ["entityId"] = entityId, ["delayMs"] = delayMs, ["delays"] = delays == null ? null : new JArray(delays) }, ct);
        var id = started["id"]?.Value<string>();
        if (id == null) return ToolResults.Json(started);
        var g = bridge.Game == "auto" ? game : bridge.Game;
        await Task.Delay(durationMs + 300, ct);
        JToken r = started;
        for (var i = 0; i < 20 && r["status"]?.Value<string>() == "running"; i++)
        {
            try { (_, r) = await bridges.CallAsync(g, "tracker.result", new JObject { ["id"] = id }, ct); }
            catch (McpException) { }
            if (r["status"]?.Value<string>() == "running") await Task.Delay(250, ct);
        }
        return ToolResults.Json(r);
    }

    [McpServerTool(Name = "await_motion", Title = "Wait until a player walks nearby", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("""
        Block until another player starts moving near the character (3 frames in a row above minSpeed world units/s;
        the local player is ignored), then return {entityId, name, speed, screen}. Use it to start overlay
        measurements on a moving target without anyone moving the character: pass entityId to overlay_accuracy and
        capture the screen around 'screen' right away (tools/fidelity). Read-only; the watch is a cheap per-frame check.
        """)]
    public static async Task<CallToolResult> AwaitMotion(BridgeRegistry bridges,
        [Description("How long to wait, seconds (1-3600, default 600)")] int timeoutSec = 600,
        [Description("Radius in world units (default 900, about a screen)")] float range = 900,
        [Description("Minimum speed in world units/s (default 200; a walk is ~300-450)")] float minSpeed = 200,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (bridge, armed) = await bridges.CallAsync(game, "motion.watch", new JObject { ["range"] = range, ["minSpeed"] = minSpeed }, ct);
        if (armed["error"] != null) return ToolResults.Json(armed);
        var g = bridge.Game == "auto" ? game : bridge.Game;
        var since = armed["seq"]?.Value<int>() ?? 0;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSec, 1, 3600));
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
                JToken s;
                try { (_, s) = await bridges.CallAsync(g, "motion.state", new JObject { ["since"] = since }, ct); }
                catch (McpException) { continue; }
                if (s["entityId"] != null) return ToolResults.Json(s);
            }
            return ToolResults.Json(new JObject { ["status"] = "timeout", ["message"] = $"No player walked within {range} units in {timeoutSec} s." });
        }
        finally
        {
            try { await bridges.CallAsync(g, "motion.watch", new JObject { ["on"] = false }, CancellationToken.None); } catch { }
        }
    }

    [McpServerTool(Name = "render_lab", Title = "Experimental world renderers (walls, path)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Turn the bridge's Render Lab on or off: renderers to compare with Radar and HealthBars during movement.
        walls: raycast wall contours around the player (rays over the walkability grid, re-cast per grid cell).
        path: a smoothed, glowing path to a target ('waypoint', 'transition', or an entity metadata path substring),
        planned with A* when the player changes cell and always starting at the player's live position.
        Both draw from fresh camera/position reads, time-aligned by delayMs (default 5, the game image's own latency
        measured with tools/fidelity). Omit everything to read the state. Draws on screen; nothing is sent to the game.
        """)]
    public static async Task<CallToolResult> RenderLab(BridgeRegistry bridges,
        [Description("Raycast wall highlight on/off")] bool? walls = null,
        [Description("Path to target on/off")] bool? path = null,
        [Description("HealthBars comparison markers on/off: a gold bracket at HealthBars' own anchor per nearby monster/player, from fresh time-aligned data")] bool? bars = null,
        [Description("'waypoint', 'transition', or an entity path substring")] string? target = null,
        [Description("Time alignment in ms (0-100)")] double? delayMs = null,
        [Description("Instead of toggling: compare Radar-style and lab path drawing offline on this area's grid (simulated walk to target): backwards starts, jaggedness, line jumps")] bool compare = false,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        if (compare)
        {
            var (_, c) = await bridges.CallAsync(game, "lab.compare_paths", new JObject { ["target"] = target }, ct);
            return ToolResults.Json(c);
        }
        var p = new JObject();
        if (walls != null) p["walls"] = walls; if (path != null) p["path"] = path; if (bars != null) p["bars"] = bars;
        if (target != null) p["target"] = target; if (delayMs != null) p["delayMs"] = delayMs;
        var (_, r) = await bridges.CallAsync(game, p.Count == 0 ? "lab.state" : "lab.set", p, ct);
        return ToolResults.Json(r);
    }

    [McpServerTool(Name = "profile_plugin", Title = "Where a plugin's frame time goes", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false, IconSource = ExileApiMcp.Hosting.IconSet.HudPerformanceLight)]
    [Description("""
        Method-level profile of one HUD plugin for a few seconds: Harmony wraps every method and instance constructor in its assembly (never
        protected stubs), keeps a per-thread call stack, and reports per method calls, self and inclusive time
        (ms per second of wall time, and us per call), then unpatches. Self = inclusive minus profiled callees, so HUD
        API calls a method makes count as its own time. Find the hot method first with pipeline_trace (pluginTickMs /
        pluginRenderMs), then profile that plugin. Needs the bridge setting 'Allow HUD Instrumentation'.
        Example result: Whats An Azmeri Wisp's Tick at 2.9 ms per frame -> a 20 Hz filtered scan, 33x less CPU.
        bridge_self_perf names the bridge step that costs; profile_plugin name="Whats An AI Bridge" method=<step> finds the method.
        """)]
    public static async Task<CallToolResult> ProfilePlugin(BridgeRegistry bridges,
        [Description("Plugin name as the HUD lists it (hud_plugins), e.g. 'Whats An Azmeri Wisp' (or empty with assembly)")] string name = "",
        [Description("Instead of a plugin: a loaded HUD assembly to profile part of, e.g. ExileCore (PoE1; PoE2's ExileCore2 is obfuscated and refuses patching)")] string? assembly = null,
        [Description("Type full-name substring to profile: required with assembly, optional for a plugin, e.g. EntityListWrapper")] string? filter = null,
        [Description("Method name substring to profile, e.g. Draw or Stats. Profiling the bridge itself (Whats An AI Bridge) needs method or filter; its profiler and trace code is never patched")] string? method = null,
        [Description("Profile length in ms (500-20000, default 4000)")] int durationMs = 4000,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        durationMs = Math.Clamp(durationMs, 500, 20_000);
        var (bridge, started) = await bridges.CallAsync(game, "profile.plugin", new JObject { ["name"] = name, ["durationMs"] = durationMs, ["assembly"] = assembly, ["filter"] = filter, ["method"] = method }, ct);
        var id = started["id"]?.Value<string>();
        if (id == null) return ToolResults.Json(started);
        var g = bridge.Game == "auto" ? game : bridge.Game;
        await Task.Delay(durationMs + 1000, ct);   // + patching time
        JToken r = started;
        for (var i = 0; i < 60 && r["status"]?.Value<string>() != "done"; i++)
        {
            try { (_, r) = await bridges.CallAsync(g, "profile.result", new JObject { ["id"] = id }, ct); }
            catch (McpException) { }
            if (r["status"]?.Value<string>() != "done") await Task.Delay(500, ct);
        }
        if (r["status"]?.Value<string>() != "done") return ToolResults.Json(r);
        var sb = new StringBuilder();
        sb.AppendLine($"{r["plugin"]}: {r["methodsPatched"]} methods for {r["durationMs"]} ms, {r["calls"]} calls, {r["selfTotalMsPerSecond"]} ms of CPU per second in its code");
        if (r["hookOverhead"] is JObject oh) sb.AppendLine($"(the profiler's own cost, {oh["usPerCall"]} us and {oh["bytesPerCall"]} B per call, is already subtracted)");
        sb.AppendLine("self ms/s | incl ms/s | us/call | calls | method");
        foreach (var m in r["top"] as JArray ?? [])
            sb.AppendLine($"{m["selfMsPerSecond"],9} | {m["inclMsPerSecond"],9} | {m["selfUsPerCall"],7} | {m["calls"],5} | {m["method"]}");
        if (r["topAlloc"] is JArray { Count: > 0 } ta)
        {
            sb.AppendLine($"Allocation: {r["allocTotalKBPerSecond"]} KB/s in its code (garbage = GC pauses at high fps). Top:");
            foreach (var m in ta.Take(8)) sb.AppendLine($"  {m["allocSelfKBPerSecond"],8} KB/s | {m["bytesPerCall"],7} B/call | {m["calls"],5} calls | {m["method"]}");
        }
        if (r["refused"] is JArray { Count: > 0 } refused) sb.AppendLine("Not profiled: " + string.Join("; ", refused));
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(r.ToString(Newtonsoft.Json.Formatting.None)),
        };
    }

    [McpServerTool(Name = "bridge_self_perf", Title = "The AI Bridge plugin's own cost per frame", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("The bridge plugin's own time (us) and allocation (bytes) per frame for each of its Render steps (stats panel, guide panel, observer, render lab...), averaged since the previous call; the call resets the counters. The profiler refuses to profile the bridge, so use this after changing the bridge's per-frame code: call once to reset, wait, call again.")]
    public static async Task<CallToolResult> BridgeSelfPerf(BridgeRegistry bridges,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null, CancellationToken ct = default)
    {
        var (_, r) = await bridges.CallAsync(game, "bridge.self_perf", null, ct);
        return ToolResults.Json(r);
    }

    private static string Summary(JToken r)
    {
        if (r["status"]?.Value<string>() != "done") return r.ToString(Newtonsoft.Json.Formatting.None);
        string S(string key, string stat = "avg") => r[key]?[stat]?.ToString() ?? "-";
        string T(string key, string stat) => r["timelineMs"]?[key]?[stat]?.ToString() ?? "-";
        var sb = new StringBuilder();
        sb.AppendLine($"{r["frames"]} frames in {r["durationMs"]} ms: HUD {r["hudFps"]} fps, frame interval {S("frameIntervalMs")} ms (sd {S("frameIntervalMs", "sd")}, max {S("frameIntervalMs", "max")}), " +
                      $"frame work {S("updateMs")} ms (p95 {S("updateMs", "p95")}): plugins {S("pluginsMs")} + HUD core {S("coreMs")}, Present {S("presentCallMs")} ms");
        sb.AppendLine($"Data age at Present: camera {S("cameraDataAgeAtPresentMs", "p50")} ms (p95 {S("cameraDataAgeAtPresentMs", "p95")}), " +
                      $"entities {S("entityDataAgeAtPresentMs", "p50")} ms (p95 {S("entityDataAgeAtPresentMs", "p95")}); camera-entity skew {S("cameraEntityDataSkewMs", "p50")} ms");
        sb.AppendLine($"Timeline (ms after frame start, p50): cache cycle {T("cacheCycleAfterFrameStart", "p50")}, camera read {T("cameraReadAfterFrameStart", "p50")}, " +
                      $"camera data fetched {T("cameraDataFetchedAfterFrameStart", "p50")}, entity data fetched {T("entityDataFetchedAfterFrameStart", "p50")}");
        if (r["pluginRenderMs"] is JObject plugins && plugins.Count > 0)
            sb.AppendLine("Plugin Render (avg / p95 ms): " + string.Join(", ", plugins.Properties().Take(6).Select(p => $"{p.Name} {p.Value["avg"]}/{p.Value["p95"]}")));
        if (r["pluginTickMs"] is JObject ticks && ticks.Count > 0)
            sb.AppendLine("Plugin Tick (avg / p95 ms): " + string.Join(", ", ticks.Properties().Take(6).Select(p => $"{p.Name} {p.Value["avg"]}/{p.Value["p95"]}")));
        if (r["gc"] is JObject gc)
            sb.AppendLine($"GC: {gc["allocMBPerSecond"]} MB/s allocated ({gc["fetchedMBPerSecond"]} MB/s fetched from the game), gen0 {gc["gen0"]} / gen1 {gc["gen1"]} / gen2 {gc["gen2"]}, pauses {gc["pauseMsTotal"]} ms total");
        if (r["pluginAllocKBPerFrame"]?["render"] is JObject ar && ar.Count > 0)
            sb.AppendLine("Plugin allocation (KB per frame, Render): " + string.Join(", ", ar.Properties().Take(6).Select(p => $"{p.Name} {p.Value}")));
        var watch = r["watch"];
        sb.AppendLine($"Watched: camera {(watch?["camera"]?.Value<bool>() == true ? "yes" : "NO")}, {(watch?["entities"] as JArray)?.Count ?? 0} player(s)");
        if (r["patches"]?["refused"] is JArray { Count: > 0 } refused)
            sb.AppendLine("Not instrumented: " + string.Join("; ", refused.Select(x => x.ToString())));
        if (r["calls"] is JObject calls && calls.Properties().Where(p => p.Value.Type == JTokenType.Integer && p.Value.Value<long>() == 0).Select(p => p.Name).ToList() is { Count: > 0 } zero)
            sb.AppendLine("Hooks never hit: " + string.Join(", ", zero) + " (that link moved, or nothing to watch)");
        sb.Append("Full numbers in structuredContent.");
        return sb.ToString();
    }
}
