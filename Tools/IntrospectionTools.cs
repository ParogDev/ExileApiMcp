using System.ComponentModel;
using ExileApiMcp.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Read-only reflection over the running HUD (bridge methods hud.plugin_perf / hud.plugin_settings /
/// hud.bridge_methods): what plugins cost per frame, how they are configured right now, and which
/// cross-plugin API (PluginBridge) they expose.
/// </summary>
[McpServerToolType]
public static class IntrospectionTools
{
    private const string G = BridgeRegistry.GameParamDescription;

    [McpServerTool(Name = "hud_plugin_perf", Title = "Per-plugin frame cost", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(PluginPerfResult))]
    [Description("Every loaded HUD plugin's Tick (logic) and Render (drawing) cost from the HUD's own per-plugin counters: " +
                 "average, max and last milliseconds, most expensive first. Use it to find frame-time hogs or to check " +
                 "a plugin change didn't make it slower (compare before/after a reload_plugin).")]
    public static async Task<CallToolResult> HudPluginPerf(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        TypedReply.Of<PluginPerfResult>((await bridges.CallAsync(game, "hud.plugin_perf", null, ct)).Result);

    [McpServerTool(Name = "hud_plugin_settings", Title = "A plugin's live settings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(PluginSettingsResult))]
    [Description("A loaded plugin's current settings, read live from its ISettings object: each node with its kind " +
                 "(ToggleNode, RangeNode with min/max, ListNode with values, ColorNode, HotkeyNode, ...) and value, nested " +
                 "groups as objects. Values under secret-looking names (token, key, session, password...) are redacted. " +
                 "Read-only; the HUD saves settings itself - never edit its config files while it runs.")]
    public static async Task<CallToolResult> HudPluginSettings(BridgeRegistry bridges,
        [Description("Plugin display name, e.g. 'Radar' or 'Whats An Azmeri Wisp' (see hud_plugin_perf)")] string plugin,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        TypedReply.Of<PluginSettingsResult>((await bridges.CallAsync(game, "hud.plugin_settings", new JObject { ["name"] = plugin }, ct)).Result);

    [McpServerTool(Name = "plugin_bridge_methods", Title = "Cross-plugin API (PluginBridge)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(BridgeMethodsResult))]
    [Description("Everything loaded plugins registered on the HUD's PluginBridge - the cross-plugin API - with typed signatures " +
                 "and the declaring class, e.g. Radar.GetMapSvg (String (Boolean includeRoutes)) or NinjaPrice.GetValue (Double (Entity e)). " +
                 "Plugins consume them with GameController.PluginBridge.GetMethod<TDelegate>(name); agents can try them with run_csharp.")]
    public static async Task<CallToolResult> PluginBridgeMethods(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        TypedReply.Of<BridgeMethodsResult>((await bridges.CallAsync(game, "hud.bridge_methods", null, ct)).Result);
}
