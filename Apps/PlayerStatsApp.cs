using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Apps;

/// <summary>
/// The player-stats MCP App UI (ui:// resource, extension io.modelcontextprotocol/ui).
/// The HTML is a single self-contained file built from ui-src/ in a container and embedded in
/// this assembly as ui/player-stats.html. No external origins: the app talks to the server only
/// through the host (tools/call), never directly over the network.
/// </summary>
[McpServerResourceType]
public static class PlayerStatsApp
{
    public const string ResourceUri = "ui://exile/player-stats";

    [McpServerResource(UriTemplate = ResourceUri, Name = "player-stats-ui", Title = "Player stats", MimeType = McpApps.HtmlMimeType, IconSource = ExileApiMcp.Hosting.IconSet.PlayerStatsLight)]
    // JsonValue, not the (name, value) constructor: that one emits the JSON as a *string*
    // ("ui":"{\"prefersBorder\":true}"), and hosts expect _meta.ui to be an object.
    [McpMeta("ui", JsonValue = """{"prefersBorder":true}""")]
    [Description("Interactive player stats panel, synced with the in-game HUD panel.")]
    public static string GetUi() => LoadEmbedded("ui/player-stats.html") ?? Placeholder;

    internal static string? LoadEmbedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Shown until the real bundle is built (milestone 1, task 4).
    private const string Placeholder = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Player stats</title></head>
        <body style="font-family:system-ui;padding:16px">
          <h3>Player stats</h3>
          <p>The interactive panel hasn't been built into this server yet. The tool result above has the data.</p>
        </body></html>
        """;
}
