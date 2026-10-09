using System.ComponentModel;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Apps;

/// <summary>
/// The data explorer MCP App: a tree over the live HUD object model, built from ui-src/ (data-explorer.html)
/// and embedded as ui/data-explorer.html. It calls explore_object / eval_path / watch_object through the host.
/// </summary>
[McpServerResourceType]
public static class DataExplorerApp
{
    public const string ResourceUri = "ui://exile/data-explorer";

    [McpServerResource(UriTemplate = ResourceUri, Name = "data-explorer-ui", Title = "Data explorer", MimeType = McpApps.HtmlMimeType, IconSource = ExileApiMcp.Hosting.IconSet.DataExplorerLight)]
    [McpMeta("ui", JsonValue = """{"prefersBorder":true}""")]
    [Description("Interactive explorer of the live HUD object model: expand objects and components, copy paths and plugin C#.")]
    public static string GetUi() => PlayerStatsApp.LoadEmbedded("ui/data-explorer.html") ?? Placeholder;

    private const string Placeholder = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Data explorer</title></head>
        <body style="font-family:system-ui;padding:16px">
          <h3>Data explorer</h3>
          <p>The explorer UI hasn't been built into this server yet. The tool result has the outline.</p>
        </body></html>
        """;
}
