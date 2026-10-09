using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Hosting;

/// <summary>
/// Tool, resource and prompt attributes take one icon URI (IconSource); we put the light variant there. This pass, run
/// once after registration, turns every IconSet light icon into its light + dark pair (Icon.Theme), so hosts pick the
/// one for their background. The pairs come from IconSet by reflection (XLight and XDark consts), so a new icon needs
/// no wiring here.
/// </summary>
public static class IconThemes
{
    private static readonly Dictionary<string, IList<Icon>> Pairs = typeof(IconSet)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.Name.EndsWith("Light", StringComparison.Ordinal))
        .Select(f => (light: (string)f.GetRawConstantValue()!, dark: typeof(IconSet).GetField(f.Name[..^5] + "Dark")?.GetRawConstantValue() as string))
        .Where(p => p.dark != null)
        .ToDictionary(p => p.light, p => IconSet.Pair(p.light, p.dark!));

    public static void Apply(McpServerOptions o)
    {
        foreach (var t in o.ToolCollection ?? []) t.ProtocolTool.Icons = Themed(t.ProtocolTool.Icons);
        foreach (var r in o.ResourceCollection ?? [])
        {
            if (r.ProtocolResource is { } res) res.Icons = Themed(res.Icons);
            if (r.ProtocolResourceTemplate is { } tpl) tpl.Icons = Themed(tpl.Icons);
        }
        foreach (var p in o.PromptCollection ?? []) p.ProtocolPrompt.Icons = Themed(p.ProtocolPrompt.Icons);
    }

    private static IList<Icon>? Themed(IList<Icon>? icons) =>
        icons is [{ Theme: null } one] && Pairs.TryGetValue(one.Source, out var pair) ? pair : icons;
}
