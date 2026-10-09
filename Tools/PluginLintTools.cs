using System.ComponentModel;
using System.Text;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hud;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>hud_plugin_lint: offline IL check of compiled plugins for expensive HUD API calls on per-frame paths.</summary>
[McpServerToolType]
public static class PluginLintTools
{
    [McpServerTool(Name = "hud_plugin_lint", Title = "Expensive HUD API calls in a plugin's per-frame code (offline)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Static check of compiled plugins (offline: reads the DLL's IL, nothing runs, no HUD needed): every call to a
        known-expensive HUD member (Entity.DistancePlayer, Entity.Pos, Stats, Buffs, GetComponent, GameController.Entities,
        Camera.WorldToScreen...) in code reachable from Tick/Render, with its measured cost (first call per entity per
        frame) and whether it sits in a loop or a per-item lambda (cost x entity count). The cheaper equivalent is in
        each line; numbers from knowledge pack shared/api-costs. Use before profile_plugin to find likely hot spots, and
        on your own plugin before committing.
        """)]
    public static CallToolResult HudPluginLint(BridgeRegistry bridges,
        [Description("Plugin folder name (substring); omit for every compiled source plugin")] string? plugin = null,
        [Description("'poe1' or 'poe2'; omit for every HUD installed")] string? game = null)
    {
        var sb = new StringBuilder();
        var result = new JArray();
        foreach (var hud in HudDevTools.Installs(bridges, game))
            foreach (var src in hud.SourcePlugins().Where(p => plugin == null || p.Folder.Contains(plugin, StringComparison.OrdinalIgnoreCase)))
            {
                var dll = CompiledDll(hud, src);
                if (dll == null) { sb.AppendLine($"{hud.Game} {src.Folder}: no compiled DLL found (start the HUD once so it compiles)"); continue; }
                List<PluginLint.Finding> findings;
                try { findings = PluginLint.Lint(dll); }
                catch (Exception ex) { sb.AppendLine($"{hud.Game} {src.Folder}: could not read {Path.GetFileName(dll)}: {ex.Message}"); continue; }
                var hot = findings.Where(f => f.InLoop).ToList();
                sb.AppendLine($"{hud.Game} {src.Folder}: {findings.Count} expensive call site(s) on Tick/Render paths, {hot.Count} in loops/lambdas");
                foreach (var f in findings.Take(12))
                    sb.AppendLine($"  {(f.InLoop ? "LOOP" : "    ")} {f.Method}: {f.Call} x{f.Count} ({(f.CostNs > 0 ? $"~{f.CostNs:N0} ns each" : "allocates")}) -> {f.Advice}");
                if (findings.Count > 12) sb.AppendLine($"  ... +{findings.Count - 12} more (structuredContent)");
                result.Add(new JObject
                {
                    ["game"] = hud.Game, ["plugin"] = src.Folder,
                    ["findings"] = new JArray(findings.Select(f => new JObject
                    {
                        ["method"] = f.Method, ["call"] = f.Call, ["count"] = f.Count, ["inLoop"] = f.InLoop, ["costNs"] = f.CostNs, ["advice"] = f.Advice,
                    })),
                });
            }
        if (sb.Length == 0) sb.Append("No matching source plugins.");
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(new JObject { ["plugins"] = result }.ToString(Newtonsoft.Json.Formatting.None)),
        };
    }

    /// <summary>The HUD's compiled output of a source plugin: Plugins\Temp\&lt;folder&gt; (ExileCore2) or Plugins\Compiled\&lt;folder&gt; (ExileCore).</summary>
    private static string? CompiledDll(HudInstall hud, HudInstall.SourcePlugin src)
    {
        foreach (var sub in new[] { "Temp", "Compiled" })
        {
            var dir = Path.Combine(hud.Root, "Plugins", sub, src.Folder);
            if (!Directory.Exists(dir)) continue;
            var dlls = Directory.GetFiles(dir, "*.dll").Where(f => !Path.GetFileName(f).Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase)).ToList();
            var named = src.ProjectName != null ? dlls.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(src.ProjectName, StringComparison.OrdinalIgnoreCase)) : null;
            if ((named ?? dlls.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()) is { } d) return d;
        }
        return null;
    }
}
