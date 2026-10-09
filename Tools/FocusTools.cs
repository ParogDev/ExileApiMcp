using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Measurements need the game in front (bridge Shared/MeasureFocus.cs): bring it forward when the user agrees, and read
/// the game's own settings that change what a measurement sees.
/// </summary>
[McpServerToolType]
public static class FocusTools
{
    [McpServerTool(Name = "focus_game", Title = "Bring the game window to the front", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(FocusResult), IconSource = IconSet.HudPerformanceLight)]
    [Description("Bring the game window to the front (restore it if minimized). Window manager only: no key or mouse events. It takes " +
                 "focus from whatever the user is doing, so call it only when they asked for it or agreed (e.g. before a measurement: " +
                 "traces and profiles report foreground.share, and numbers taken with the game behind another window aren't " +
                 "representative). Windows sometimes refuses; then ask the user to click the game.")]
    public static async Task<CallToolResult> FocusGame(BridgeRegistry bridges,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null, CancellationToken ct = default)
    {
        var (_, r) = await bridges.CallAsync(game, "focus.game", new JObject(), ct);
        var result = Dto.From<FocusResult>(r);
        return Dto.Result(result, result.Message ?? (result.InFront ? "The game is in front." : "The game isn't in front."));
    }

    [McpServerTool(Name = "game_config", Title = "The game's settings that affect measurements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(GameConfigResult), IconSource = IconSet.HudPerformanceLight)]
    [Description("The game's display settings that change what a measurement sees: the background framerate cap (on by default: " +
                 "30 fps whenever the game isn't the foreground window), foreground caps, vsync, window mode, renderer. Read from the " +
                 "game's config in the profile of the user running it; only these keys, never the file.")]
    public static async Task<CallToolResult> GameConfig(BridgeRegistry bridges,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null, CancellationToken ct = default)
    {
        var (_, r) = await bridges.CallAsync(game, "game.config", new JObject(), ct);
        var result = Dto.From<GameConfigResult>(r);
        var text = $"{result.File} ({result.Profile}, via {result.FoundVia}): " +
                   string.Join(", ", (result.Settings ?? []).Select(kv => $"{kv.Key}={kv.Value}")) + (result.Note != null ? $"\n{result.Note}" : "");
        return Dto.Result(result, text);
    }
}

public sealed class FocusResult
{
    public bool Ok { get; set; }
    public bool WasInFront { get; set; }
    public bool InFront { get; set; }
    public string? Message { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GameConfigResult
{
    public string File { get; set; } = "";
    /// <summary>"this user" or "another user" (the game runs as a separate account).</summary>
    public string? Profile { get; set; }
    public string? FoundVia { get; set; }
    public DateTimeOffset? LastWrite { get; set; }
    /// <summary>Allowlisted keys only, values as the game writes them.</summary>
    public Dictionary<string, string>? Settings { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
