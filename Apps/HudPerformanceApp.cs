using System.ComponentModel;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Apps;

/// <summary>
/// The HUD performance MCP App: a frame timeline (GC pauses marked), the work split and the costliest plugins, built
/// from ui-src/ (hud-performance.html) and embedded as ui/hud-performance.html. It calls hud_health_report (series),
/// profile_plugin and hud_plugin_lint through the host.
/// </summary>
[McpServerResourceType]
public static class HudPerformanceApp
{
    public const string ResourceUri = "ui://exile/hud-performance";

    [McpServerResource(UriTemplate = ResourceUri, Name = "hud-performance-ui", Title = "HUD performance", MimeType = McpApps.HtmlMimeType)]
    [McpMeta("ui", JsonValue = """{"prefersBorder":true}""")]
    [Description("Live HUD performance: frame timeline with GC pauses, plugins vs core, costliest plugins, next steps.")]
    public static string GetUi() => PlayerStatsApp.LoadEmbedded("ui/hud-performance.html") ?? Placeholder;

    private const string Placeholder = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>HUD performance</title></head>
        <body style="font-family:system-ui;padding:16px">
          <h3>HUD performance</h3>
          <p>The performance UI hasn't been built into this server yet. The tool result has the report.</p>
        </body></html>
        """;
}
