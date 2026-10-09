using System.ComponentModel;
using System.Text.Json;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Tools;

/// <summary>
/// Live performance without polling (Bridge/PerfHub.cs): the latest health report per game, refreshed on a cadence
/// while someone watches. Subscribers get resources/updated on exile://perf/{game}/report; MCP Apps (which can't
/// subscribe) call perf_watch, which returns as soon as a newer report exists.
/// </summary>
[McpServerToolType, McpServerResourceType]
public static class PerfTools
{
    [McpServerTool(Name = "perf_watch", Title = "Wait for the next HUD performance report", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(PerfSnapshot), IconSource = IconSet.HudPerformanceLight)]
    [Description("The latest hud_health_report (with the per-frame series) once it is newer than since: returns at once if it " +
                 "already is, otherwise waits for the next one (up to timeoutSec) and returns the latest either way (fresh = " +
                 "false on timeout). While anyone watches, the server re-traces every intervalSec (each trace patches the HUD " +
                 "for 3 s, so keep it at 15 s or more), and stops 30 s after the last watcher. Loop on it with since = the " +
                 "returned seq for a live view; clients with subscriptions/listen subscribe to exile://perf/{game}/report instead.")]
    public static async Task<CallToolResult> PerfWatch(BridgeRegistry bridges, PerfHub hub,
        [Description("The seq already shown (0 = give me what you have, running a report if there is none)")] long since = 0,
        [Description("Max wait in seconds (1-50, default 25)")] int timeoutSec = 25,
        [Description("Seconds between reports while watched (5-300, default 15)")] double? intervalSec = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var g = GameOf(bridges, game);
        var snap = await hub.WaitAsync(g, since, TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, 50)), intervalSec, ct);
        var text = snap.Report == null
            ? $"No report yet for {g} (the first trace is running or the bridge is unreachable). seq={snap.Seq}"
            : $"#{snap.Seq} {(snap.Fresh ? "new" : "no newer report yet")} ({snap.At:HH:mm:ss} UTC)\n{snap.Text}";
        snap.Text = null;   // the text content carries it
        return Dto.Result(snap, text);
    }

    [McpServerResource(UriTemplate = "exile://perf/{game}/report", Name = "perf-report", Title = "HUD performance report", MimeType = "application/json", IconSource = IconSet.HudPerformanceLight)]
    [Description("The latest HUD health report (PerfSnapshot, with the per-frame series). Subscribable: updated with each new " +
                 "report, every 15 s while subscribed (each report traces the HUD for 3 s).")]
    public static async Task<string> Report(PerfHub hub, string game, CancellationToken ct)
        => JsonSerializer.Serialize(await hub.LatestAsync(game, ct), Dto.Options);

    private static string GameOf(BridgeRegistry bridges, string? game)
    {
        var g = game?.Trim().ToLowerInvariant();
        if (g is "poe1" or "poe2") return g;
        var b = bridges.Resolve(game);
        return b.Game is "poe1" or "poe2" ? b.Game : throw new McpException("Pass game=poe1 or game=poe2: more than one HUD may be running.");
    }
}
