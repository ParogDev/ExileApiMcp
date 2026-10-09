using System.ComponentModel;
using System.Text.Json;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Tools;

/// <summary>
/// The control center's view of the server: what it offers, read from the registry by reflection (no hand-kept list),
/// so a new tool, resource or prompt shows up in the control center by existing.
/// </summary>
[McpServerToolType]
public static class ControlTools
{
    [McpServerTool(Name = "hud_catalog", Title = "Everything this server offers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(CatalogResult), IconSource = IconSet.ServerLight)]
    [Description("Every tool (with its family, annotations, icons, input schema and, for typed tools, output schema and the app it " +
                 "opens), every resource and template (and whether it is subscribable), and every prompt, plus which HUDs are up. " +
                 "For browsing the server and generating forms and result views (the control center uses it).")]
    public static CallToolResult HudCatalog(IOptions<McpServerOptions> options, BridgeRegistry bridges, IEnumerable<IResourceHub> hubs)
    {
        var o = options.Value;
        var hubList = hubs.ToList();
        var result = new CatalogResult
        {
            Server = new CatalogServer
            {
                Name = o.ServerInfo?.Name ?? "", Title = o.ServerInfo?.Title, Version = o.ServerInfo?.Version ?? "",
                Icons = Icons(o.ServerInfo?.Icons),
                GamesUp = bridges.Bridges.Where(b => b.LooksAvailable).Select(b => b.Game).ToList(),
            },
            Tools = (o.ToolCollection ?? []).Select(t =>
            {
                var p = t.ProtocolTool;
                return new CatalogTool
                {
                    Name = p.Name, Title = p.Title ?? p.Annotations?.Title, Description = p.Description,
                    Family = FamilyOf(t),
                    ReadOnly = p.Annotations?.ReadOnlyHint ?? false, Destructive = p.Annotations?.DestructiveHint ?? false,
                    Idempotent = p.Annotations?.IdempotentHint ?? false, OpenWorld = p.Annotations?.OpenWorldHint ?? false,
                    Icons = Icons(p.Icons),
                    InputSchema = p.InputSchema.ValueKind == JsonValueKind.Undefined ? null : p.InputSchema,
                    OutputSchema = p.OutputSchema is { ValueKind: not JsonValueKind.Undefined } os ? os : null,
                    AppUri = p.Meta?["ui"]?["resourceUri"]?.GetValue<string>() ?? p.Meta?["ui/resourceUri"]?.GetValue<string>(),
                };
            }).OrderBy(t => t.Family).ThenBy(t => t.Name).ToList(),
            Resources = (o.ResourceCollection ?? []).Select(r =>
            {
                var res = r.ProtocolResource; var tpl = r.ProtocolResourceTemplate;
                var uri = res?.Uri ?? tpl?.UriTemplate ?? "";
                return new CatalogResource
                {
                    Uri = res?.Uri, UriTemplate = res == null ? tpl?.UriTemplate : null,
                    Name = res?.Name ?? tpl?.Name ?? "", Title = res?.Title ?? tpl?.Title, Description = res?.Description ?? tpl?.Description,
                    MimeType = res?.MimeType ?? tpl?.MimeType, Icons = Icons(res?.Icons ?? tpl?.Icons),
                    // A template is subscribable when a hub serves its URI space (test with the game filled in).
                    Subscribable = hubList.Any(h => h.Handles(uri.Replace("{game}", "poe2"))),
                };
            }).OrderBy(r => r.Name).ToList(),
            Prompts = (o.PromptCollection ?? []).Select(pr => new CatalogPrompt
            {
                Name = pr.ProtocolPrompt.Name, Title = pr.ProtocolPrompt.Title, Description = pr.ProtocolPrompt.Description,
                Arguments = pr.ProtocolPrompt.Arguments?.Select(a => new CatalogPromptArgument { Name = a.Name, Description = a.Description, Required = a.Required == true }).ToList(),
            }).OrderBy(p => p.Name).ToList(),
        };
        var text = $"{result.Server.Title ?? result.Server.Name} {result.Server.Version}: {result.Tools.Count} tools in " +
                   $"{result.Tools.Select(t => t.Family).Distinct().Count()} families, {result.Resources.Count} resources " +
                   $"({result.Resources.Count(r => r.Subscribable)} subscribable), {result.Prompts.Count} prompts. HUDs up: " +
                   (result.Server.GamesUp.Count == 0 ? "none" : string.Join(", ", result.Server.GamesUp));
        return Dto.Result(result, text);
    }

    [McpServerTool(Name = "hud_settings", Title = "Settings of the HUD's plugins", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(SettingsResult), IconSource = IconSet.ServerLight)]
    [Description("The settings of every loaded HUD plugin (or one), found by reflection: path, label, group, kind (toggle, range, " +
                 "text, list, color, hotkey, button), value, limits or choices, and whether it is a permission setting or read-only. " +
                 "Secrets (session ids, tokens, connection strings) are never shown. Change one with hud_settings_set.")]
    public static async Task<CallToolResult> HudSettings(BridgeRegistry bridges,
        [Description("Only this plugin (as the HUD lists it, e.g. Whats An AI Bridge)")] string? plugin = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (_, r) = await bridges.CallAsync(game, "settings.describe", plugin == null ? new Newtonsoft.Json.Linq.JObject() : new() { ["plugin"] = plugin }, ct);
        var result = Dto.From<SettingsResult>(r);
        var sb = new System.Text.StringBuilder($"{result.Game}: {result.Plugins.Count} plugin(s)\n");
        foreach (var p in result.Plugins)
        {
            sb.AppendLine($"{p.Plugin}{(p.Enabled ? "" : " (disabled)")}: {p.Settings.Count} settings");
            foreach (var g in p.Settings.GroupBy(s => s.Group ?? ""))
                sb.AppendLine($"  {(g.Key.Length > 0 ? g.Key + ": " : "")}{string.Join(", ", g.Select(s => $"{s.Label} = {s.Value?.ToString() ?? "-"}{(s.Permission ? " [permission]" : "")}"))}");
        }
        return Dto.Result(result, sb.ToString());
    }

    [McpServerTool(Name = "hud_settings_set", Title = "Change a HUD plugin setting", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(SettingChangeResult), IconSource = IconSet.ServerLight)]
    [Description("Change one setting of a loaded HUD plugin (path and kinds from hud_settings): toggles take true/false, ranges a " +
                 "number within their limits, lists one of their choices, colours #RRGGBB(AA), text a string. The HUD saves it on " +
                 "exit like a menu change. Permission settings (Allow C# Scripts, Allow HUD Instrumentation, Allow Plugin Reload) " +
                 "can't be changed here: the user changes them in game or in the control center. Hotkeys are set in game.")]
    public static async Task<CallToolResult> HudSettingsSet(BridgeRegistry bridges,
        [Description("Plugin as the HUD lists it")] string plugin,
        [Description("Setting path from hud_settings (e.g. ShowAgentGuide, Section.Child)")] string path,
        [Description("New value: true/false, a number, a choice, #RRGGBB(AA) or text")] JsonElement value,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var v = Newtonsoft.Json.Linq.JToken.Parse(value.GetRawText());
        var (_, r) = await bridges.CallAsync(game, "settings.set", new Newtonsoft.Json.Linq.JObject { ["plugin"] = plugin, ["path"] = path, ["value"] = v }, ct);
        var result = Dto.From<SettingChangeResult>(r);
        return Dto.Result(result, $"{result.Plugin}: {result.Setting.Label} {result.Previous?.ToString() ?? "-"} -> {result.Setting.Value?.ToString() ?? "-"}");
    }

    [McpServerTool(Name = "show_control_center", Title = "Open the Hexile control center", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(CatalogResult), IconSource = IconSet.ServerLight)]
    [ModelContextProtocol.Extensions.Apps.McpAppUi(ResourceUri = Apps.ControlCenterApp.ResourceUri)]
    [McpMeta("ui/resourceUri", Apps.ControlCenterApp.ResourceUri)]
    [Description("Open the Hexile control center (clients that support MCP Apps): every tool with a form to try it, the HUD " +
                 "plugins' settings, observer layers and live views. Outside Claude it runs standalone at /app on this server " +
                 "(tools\\control-center.ps1 opens it). Other clients get the catalog, as hud_catalog.")]
    public static CallToolResult ShowControlCenter(IOptions<McpServerOptions> options, BridgeRegistry bridges, IEnumerable<IResourceHub> hubs)
        => HudCatalog(options, bridges, hubs);

    /// <summary>The tool's family: the class declaring it (ObserveTools -> Observe).</summary>
    private static string FamilyOf(McpServerTool t) => Families.Value.TryGetValue(t.ProtocolTool.Name, out var f) ? f : "Other";

    /// <summary>Tool name -> family, from every [McpServerTool] method in this assembly (scanned once).</summary>
    private static readonly Lazy<Dictionary<string, string>> Families = new(() =>
        typeof(ControlTools).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Select(m => (type, attr: (McpServerToolAttribute?)Attribute.GetCustomAttribute(m, typeof(McpServerToolAttribute)))))
            .Where(x => x.attr?.Name != null)
            .GroupBy(x => x.attr!.Name!)
            .ToDictionary(g => g.Key, g => g.First().type.Name is var n && n.EndsWith("Tools", StringComparison.Ordinal) ? n[..^5] : g.First().type.Name));

    private static List<CatalogIcon>? Icons(IList<Icon>? icons) =>
        icons?.Select(i => new CatalogIcon { Src = i.Source, MimeType = i.MimeType, Theme = i.Theme }).ToList();
}
