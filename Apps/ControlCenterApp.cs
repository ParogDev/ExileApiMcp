using System.ComponentModel;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Apps;

/// <summary>
/// The Hexile control center as an MCP App (ui-src/src/control, built to ui/control-center.html). The same page is
/// served standalone at /app (Hosting/ControlCenterRoutes.cs), where it speaks MCP over HTTP itself and gets push.
/// </summary>
[McpServerResourceType]
public static class ControlCenterApp
{
    public const string ResourceUri = "ui://exile/control-center";

    [McpServerResource(UriTemplate = ResourceUri, Name = "control-center-ui", Title = "Hexile control center", MimeType = McpApps.HtmlMimeType,
        IconSource = ExileApiMcp.Hosting.IconSet.ServerLight)]
    [McpMeta("ui", JsonValue = """{"prefersBorder":true}""")]
    [Description("Every tool, setting, observer layer and live view of the ExileApi MCP server in one place.")]
    public static string GetUi() => PlayerStatsApp.LoadEmbedded("ui/control-center.html") ?? Placeholder;

    private const string Placeholder = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Hexile control center</title></head>
        <body style="font-family:system-ui;padding:16px">
          <h3>Hexile control center</h3>
          <p>The control center UI hasn't been built into this server yet.</p>
        </body></html>
        """;
}
