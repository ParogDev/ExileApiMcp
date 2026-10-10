using System.ComponentModel;
using System.Text;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hud;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>hud_health_report: one call that answers "why is the HUD slow / laggy / not drawing?".</summary>
[McpServerToolType]
public static class HealthReportTools
{
    [McpServerTool(Name = "hud_health_report", Title = "Why is the HUD slow, laggy or not drawing?", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(HealthReport), IconSource = ExileApiMcp.Hosting.IconSet.HudPerformanceLight)]
    [Description("""
        One-call diagnosis of the running HUD, cheapest checks first:
        1. desktop: is the display on and the game in front (else the overlay isn't visible at all);
        2. a 3 s pipeline_trace: fps and frame-interval jitter, frame work split into plugins vs HUD core, GC
           (allocation rate, pauses), and the costliest plugins by time and by allocation;
        3. hud_plugin_lint hot spots for the top plugins.
        Ends with the next tool to run (profile_plugin on the worst plugin, overlay_accuracy for drawing lag).
        Needs 'Allow HUD Instrumentation' for step 2.
        """)]
    public static async Task<CallToolResult> HudHealthReport(BridgeRegistry bridges,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        [Description("Also return the trace's per-frame series (trace.series) for a frame timeline")] bool series = false,
        CancellationToken ct = default)
    {
        var (o, text) = await Build(bridges, game, series, ct);
        return Dto.Result(Dto.From<HealthReport>(o), text);
    }

    /// <summary>The report as JSON (the HealthReport contract) and text: the tools and PerfHub share it.</summary>
    internal static async Task<(JObject report, string text)> Build(BridgeRegistry bridges, string? game, bool series, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var o = new JObject();
        var desk = DesktopState.Read();
        o["desktop"] = desk;
        sb.AppendLine(desk["warning"] is { } w ? $"Screen: {w}" : "Screen: display on, game in front.");

        JToken r;
        var t0 = DateTime.UtcNow;
        try
        {
            var (bridge, started) = await bridges.CallAsync(game, "pipeline.trace", new JObject { ["durationMs"] = 3000, ["entities"] = 0, ["series"] = series }, ct);
            var id = started["id"]?.Value<string>();
            if (id == null) { sb.AppendLine($"Trace: {started["message"] ?? started["error"]}"); return (o, sb.ToString()); }
            var g = bridge.Game == "auto" ? game : bridge.Game;
            await Task.Delay(3300, ct);
            r = started;
            for (var i = 0; i < 20 && r["status"]?.Value<string>() != "done"; i++)
            {
                try { (_, r) = await bridges.CallAsync(g, "pipeline.trace_result", new JObject { ["id"] = id }, ct); } catch (McpException) { }
                if (r["status"]?.Value<string>() != "done") await Task.Delay(250, ct);
            }
            if (r["status"]?.Value<string>() != "done" && HudRestartState.Since(bridges, g, t0) is { } cut) throw new HudRestartedException(cut, "The health report's trace");
        }
        catch (HudRestartedException ex)
        {
            o["interrupted"] = ex.Restart.Json();
            sb.AppendLine($"Trace: {ex.Message} Run hud_health_report again once the HUD is back.");
            return (o, sb.ToString());
        }
        catch (McpException ex) when (HudRestartState.Since(bridges, game, t0) is { } cut)
        {
            o["interrupted"] = cut.Json();
            sb.AppendLine($"Trace: {cut.Text("The health report's trace")}. Run hud_health_report again once the HUD is back.");
            return (o, sb.ToString());
        }
        catch (McpException ex) { sb.AppendLine($"Trace: bridge unreachable ({ex.Message})"); return (o, sb.ToString()); }
        o["trace"] = r;

        string S(string k, string s = "avg") => r[k]?[s]?.ToString() ?? "-";
        var fps = r["hudFps"]?.Value<double>() ?? 0;
        sb.AppendLine($"Frames: {fps} fps, interval {S("frameIntervalMs")} ms (max {S("frameIntervalMs", "max")}, sd {S("frameIntervalMs", "sd")}); work {S("updateMs")} ms = plugins {S("pluginsMs")} + HUD core {S("coreMs")}");
        var findings = new List<string>();
        // A measurement with the game in the background measures a hidden overlay and, often, a game capped to 30 fps.
        if (r["foreground"]?["share"]?.Value<double?>() is { } share && share < 0.95)
        {
            string? cap = null;
            try
            {
                var (_, cfg) = await bridges.CallAsync(game, "game.config", new JObject(), ct);
                if (cfg["settings"]?["background_framerate_limit_enabled"]?.ToString() == "true")
                    cap = $" The game caps itself to {cfg["settings"]?["background_framerate_limit"]} fps in the background (Options > Graphics).";
            }
            catch (McpException) { }
            sb.AppendLine($"Foreground: the game was in front {share:P0} of the trace.");
            findings.Add($"the game wasn't in front for {(1 - share):P0} of the trace, so the overlay was hidden and these numbers aren't representative.{cap} " +
                         "Ask the user to keep the game in front and run it again (focus_game brings it forward if they agree).");
        }
        if (r["frameIntervalMs"]?["max"]?.Value<double>() is { } mx && fps > 0 && mx > 1.5 * 1000 / fps)
            findings.Add($"frame spikes up to {mx:F0} ms (expected {1000 / fps:F0}): GC pauses or a heavy plugin frame");
        if (r["gc"] is JObject gc)
        {
            sb.AppendLine($"GC: {gc["allocMBPerSecond"]} MB/s allocated ({gc["fetchedMBPerSecond"]} MB/s fetched from the game), {gc["gen0"]} gen0 / {gc["gen1"]} gen1 / {gc["gen2"]} gen2 in 3 s, {gc["pauseMsTotal"]} ms paused");
            // The page cache returns its pages to ArrayPool<byte>.Shared every frame; the pool keeps only arraysPerSize of them.
            var keeps = gc["sharedArrayPool"]?["arraysPerSize"]?.Value<int?>();
            var cycled = gc["fetchedPagesPerFrame"]?.Value<double?>();
            if (keeps is { } k && cycled is { } c && c > k && gc["allocMBPerSecond"]?.Value<double>() > 50)
                findings.Add($"the shared ArrayPool keeps {k} pages per size but the page cache cycles ~{c:F0} per frame, so most become garbage: " +
                             "DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXARRAYSPERPARTITION=256 (decimal) halves total GC pause but makes each pause longer; the cure is a HUD-side pool of long-lived pages (scaffolding research/hud-gc.md)");
            else if (gc["sharedArrayPool"]?["broken"]?.Value<string>() is { } broken)
                findings.Add($"cannot check the ArrayPool limits: {broken}");
            if (gc["pauseMsTotal"]?.Value<double>() > 60) findings.Add($"GC pauses {gc["pauseMsTotal"]} ms per 3 s: see research/hud-gc.md (page cache churn) and plugin allocation below");
        }
        var plugins = new List<(string name, double tick, double render, double alloc)>();
        foreach (var p in (r["pluginRenderMs"] as JObject)?.Properties() ?? [])
            plugins.Add((p.Name, r["pluginTickMs"]?[p.Name]?["avg"]?.Value<double>() ?? 0, p.Value["avg"]?.Value<double>() ?? 0,
                (r["pluginAllocKBPerFrame"]?["render"]?[p.Name]?.Value<double>() ?? 0) + (r["pluginAllocKBPerFrame"]?["tick"]?[p.Name]?.Value<double>() ?? 0)));
        foreach (var p in (r["pluginTickMs"] as JObject)?.Properties() ?? [])
            if (plugins.All(x => x.name != p.Name)) plugins.Add((p.Name, p.Value["avg"]?.Value<double>() ?? 0, 0, r["pluginAllocKBPerFrame"]?["tick"]?[p.Name]?.Value<double>() ?? 0));
        var top = plugins.OrderByDescending(p => p.tick + p.render).Take(5).ToList();
        // Structured for apps: every traced plugin, costliest first, and the actions the findings suggest.
        o["plugins"] = new JArray(plugins.OrderByDescending(p => p.tick + p.render).Select(p => new JObject
        {
            ["name"] = p.name, ["tickMs"] = Math.Round(p.tick, 3), ["renderMs"] = Math.Round(p.render, 3), ["allocKBPerFrame"] = Math.Round(p.alloc, 1),
        }));
        var actions = new JArray();
        var lint = new JArray();
        if (top.Count > 0)
        {
            sb.AppendLine("Costliest plugins (ms per frame Tick+Render, KB allocated per frame):");
            foreach (var p in top) sb.AppendLine($"  {p.name,-24} {p.tick + p.render,6:F3} ms  {p.alloc,7:F1} KB");
        }
        var worstAlloc = plugins.OrderByDescending(p => p.alloc).FirstOrDefault();
        if (worstAlloc.alloc > 50) findings.Add($"{worstAlloc.name} allocates {worstAlloc.alloc:F0} KB per frame: profile_plugin name=\"{worstAlloc.name}\" (topAlloc)");

        // Lint the two costliest plugins by name (folder names have spaces; type names don't: match loosely).
        var hud = HudDevTools.Installs(bridges, game).FirstOrDefault();
        if (hud != null)
            foreach (var p in top.Take(2))
            {
                var src = hud.SourcePlugins().FirstOrDefault(s => Key(s.Folder) == Key(p.name) || Key(s.ProjectName ?? "") == Key(p.name));
                var dll = src == null ? null : FindDll(hud, src);
                if (dll == null) continue;
                actions.Add(Action($"Lint {src!.Folder}", "hud_plugin_lint", new JObject { ["plugin"] = src.Folder }));
                try
                {
                    var hot = PluginLint.Lint(dll).Where(f => f.InLoop && f.CostNs > 0).OrderByDescending(f => f.CostNs * f.Count).Take(3).ToList();
                    if (hot.Count > 0) sb.AppendLine($"Lint {src!.Folder}: " + string.Join("; ", hot.Select(f => $"{f.Method} {f.Call} in a loop (~{f.CostNs:N0} ns)")));
                    foreach (var f in hot)
                        lint.Add(new JObject { ["plugin"] = src!.Folder, ["method"] = f.Method, ["call"] = f.Call, ["count"] = f.Count, ["costNs"] = f.CostNs, ["advice"] = f.Advice });
                }
                catch { }
            }
        if (top.FirstOrDefault() is { name: not null } worst && worst.tick + worst.render > 0.3)
            findings.Add($"{worst.name} costs {worst.tick + worst.render:F2} ms per frame: profile_plugin name=\"{worst.name}\" (prompt optimize_plugin)");
        sb.AppendLine(findings.Count == 0 ? "Nothing stands out. For drawings that lag or wobble: overlay_accuracy and knowledge pack shared/render-fidelity." : "Next:\n  - " + string.Join("\n  - ", findings));
        o["findings"] = new JArray(findings);
        o["lint"] = lint;
        if (worstAlloc.alloc > 50) actions.Add(Action($"Profile {worstAlloc.name}'s allocation", "profile_plugin", new JObject { ["name"] = worstAlloc.name }));
        if (top.FirstOrDefault() is { name: not null } w0 && w0.tick + w0.render > 0.3 && w0.name != worstAlloc.name)
            actions.Add(Action($"Profile {w0.name}", "profile_plugin", new JObject { ["name"] = w0.name }));
        actions.Add(Action("Check drawing lag", "overlay_accuracy", new JObject()));
        o["actions"] = actions;
        return (o, sb.ToString());
    }

    private static JObject Action(string label, string tool, JObject args) => new() { ["label"] = label, ["tool"] = tool, ["args"] = args };

    [McpServerTool(Name = "show_hud_performance", Title = "Open the HUD performance panel", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(HealthReport), IconSource = ExileApiMcp.Hosting.IconSet.HudPerformanceLight)]
    [ModelContextProtocol.Extensions.Apps.McpAppUi(ResourceUri = Apps.HudPerformanceApp.ResourceUri)]
    [McpMeta("ui/resourceUri", Apps.HudPerformanceApp.ResourceUri)]
    [Description("Open a live panel of the running HUD's performance (clients that support MCP Apps): a frame timeline with GC " +
                 "pauses marked, the frame work split into plugins and HUD core, the costliest plugins by time and by " +
                 "allocation, and the findings with one-click next steps (profile_plugin, hud_plugin_lint). Other clients get " +
                 "the same text as hud_health_report. Needs 'Allow HUD Instrumentation'.")]
    public static Task<CallToolResult> ShowHudPerformance(BridgeRegistry bridges,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null, CancellationToken ct = default)
        => HudHealthReport(bridges, game, series: true, ct);

    private static string Key(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? FindDll(HudInstall hud, HudInstall.SourcePlugin src)
    {
        foreach (var sub in new[] { "Temp", "Compiled" })
        {
            var dir = Path.Combine(hud.Root, "Plugins", sub, src.Folder);
            if (!Directory.Exists(dir)) continue;
            var d = Directory.GetFiles(dir, "*.dll").Where(f => !Path.GetFileName(f).Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            if (d != null) return d;
        }
        return null;
    }
}
