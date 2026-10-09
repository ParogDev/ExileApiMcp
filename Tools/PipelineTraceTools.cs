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
    [McpServerTool(Name = "pipeline_trace", Title = "Time the HUD's render pipeline", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
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
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        durationMs = Math.Clamp(durationMs, 500, 20_000);
        var (bridge, started) = await bridges.CallAsync(game, "pipeline.trace", new JObject { ["durationMs"] = durationMs, ["entities"] = entities }, ct);
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

    private static string Summary(JToken r)
    {
        if (r["status"]?.Value<string>() != "done") return r.ToString(Newtonsoft.Json.Formatting.None);
        string S(string key, string stat = "avg") => r[key]?[stat]?.ToString() ?? "-";
        string T(string key, string stat) => r["timelineMs"]?[key]?[stat]?.ToString() ?? "-";
        var sb = new StringBuilder();
        sb.AppendLine($"{r["frames"]} frames in {r["durationMs"]} ms: HUD {r["hudFps"]} fps, frame interval {S("frameIntervalMs")} ms (sd {S("frameIntervalMs", "sd")}, max {S("frameIntervalMs", "max")}), " +
                      $"frame work {S("updateMs")} ms (p95 {S("updateMs", "p95")}), Present {S("presentCallMs")} ms");
        sb.AppendLine($"Data age at Present: camera {S("cameraDataAgeAtPresentMs", "p50")} ms (p95 {S("cameraDataAgeAtPresentMs", "p95")}), " +
                      $"entities {S("entityDataAgeAtPresentMs", "p50")} ms (p95 {S("entityDataAgeAtPresentMs", "p95")}); camera-entity skew {S("cameraEntityDataSkewMs", "p50")} ms");
        sb.AppendLine($"Timeline (ms after frame start, p50): cache cycle {T("cacheCycleAfterFrameStart", "p50")}, camera read {T("cameraReadAfterFrameStart", "p50")}, " +
                      $"camera data fetched {T("cameraDataFetchedAfterFrameStart", "p50")}, entity data fetched {T("entityDataFetchedAfterFrameStart", "p50")}");
        if (r["pluginRenderMs"] is JObject plugins && plugins.Count > 0)
            sb.AppendLine("Plugin Render (avg / p95 ms): " + string.Join(", ", plugins.Properties().Take(6).Select(p => $"{p.Name} {p.Value["avg"]}/{p.Value["p95"]}")));
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
