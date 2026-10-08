using System.ComponentModel;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Apps;

/// <summary>
/// The memory view MCP App: a struct's mapped fields laid over live bytes, unmapped ranges and structure candidates,
/// pointer following, change highlighting and bit views. Built from ui-src/ (memory-view.html) and embedded as
/// ui/memory-view.html. It calls memory_layout / memory_read / memory_where / watch_memory through the host.
/// </summary>
[McpServerResourceType]
public static class MemoryViewApp
{
    public const string ResourceUri = "ui://exile/memory-view";

    [McpServerResource(UriTemplate = ResourceUri, Name = "memory-view-ui", Title = "Memory view", MimeType = McpApps.HtmlMimeType)]
    [McpMeta("ui", JsonValue = """{"prefersBorder":true}""")]
    [Description("Interactive memory view: what the HUD maps in a struct vs live memory, unmapped structure, pointers, bit flags.")]
    public static string GetUi() => PlayerStatsApp.LoadEmbedded("ui/memory-view.html") ?? Placeholder;

    private const string Placeholder = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Memory view</title></head>
        <body style="font-family:system-ui;padding:16px">
          <h3>Memory view</h3>
          <p>The memory view UI hasn't been built into this server yet. The tool result has the outline.</p>
        </body></html>
        """;
}
