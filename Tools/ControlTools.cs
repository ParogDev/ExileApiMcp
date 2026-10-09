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
