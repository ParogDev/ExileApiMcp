using System.ComponentModel;
using System.Text;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hud;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>hud_plugin_lint: offline IL check of compiled plugins for expensive HUD API calls on per-frame paths.</summary>
[McpServerToolType]
public static class PluginLintTools
{
    [McpServerTool(Name = "hud_plugin_lint", Title = "Expensive HUD API calls in a plugin's per-frame code (offline)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(PluginLintResult))]
    [Description("""
        Static check of compiled plugins (offline: reads the DLL's IL, nothing runs, no HUD needed): every call to a
        known-expensive HUD member (Entity.DistancePlayer, Entity.Pos, Stats, Buffs, GetComponent, GameController.Entities,
        Camera.WorldToScreen...) in code reachable from Tick/Render, with its measured cost (first call per entity per
        frame) and whether it sits in a loop or a per-item lambda (cost x entity count): kind=api; allocations in per-item
        code: kind=alloc. The cheaper equivalent is in each line; numbers from knowledge pack shared/api-costs. It also
        flags plugins the JIT runs unoptimized (jitOptimized=false: the HUD compiles source plugins in Debug; an
        AssemblyInfo.cs fixes it). Reflection on those paths (Type.GetProperty/GetField/GetMethod/GetMembers,
        PropertyInfo/FieldInfo.GetValue/SetValue, MethodInfo.Invoke, Delegate.DynamicInvoke, Activator.CreateInstance,
        MakeGenericMethod, GetCustomAttribute(s)/IsDefined, Expression.Compile, dynamic call sites) is kind=reflection with
        its call chain from Tick/Render; the fix is a delegate cached via CreateDelegate or a compiled getter. Reflection
        that only fills a cache once (behind a null/flag/TryGetValue guard that stores the result: lazy init, ??=,
        if (_x == null) _x = ..., TryGetValue-then-add, GetOrAdd / Lazy<T> factories, or a method reached only through
        such guards) is hidden and counted per plugin (onceOnly); includeOnceOnly=true lists it, marked onceOnly. Use
        before profile_plugin to find likely hot spots, and on your own plugin before committing.
        """)]
    public static CallToolResult HudPluginLint(BridgeRegistry bridges,
        [Description("Plugin folder name (substring); omit for every compiled source plugin")] string? plugin = null,
        [Description("'poe1' or 'poe2'; omit for every HUD installed")] string? game = null,
        [Description("Rule kinds to report, comma-separated: api, alloc, reflection, jit (the unoptimized-JIT line); omit for all")] string? kind = null,
        [Description("Also list once-only reflection (cache fills behind a guard), marked onceOnly; default false (counted only)")] bool includeOnceOnly = false)
    {
        var kinds = ParseKinds(kind);
        var sb = new StringBuilder();
        var result = new JArray();
        foreach (var hud in HudDevTools.Installs(bridges, game))
            foreach (var src in hud.SourcePlugins().Where(p => plugin == null || p.Folder.Contains(plugin, StringComparison.OrdinalIgnoreCase)))
            {
                var dll = CompiledDll(hud, src);
                if (dll == null) { sb.AppendLine($"{hud.Game} {src.Folder}: no compiled DLL found (start the HUD once so it compiles)"); continue; }
                List<PluginLint.Finding> all;
                try { all = PluginLint.Lint(dll); }
                catch (Exception ex) { sb.AppendLine($"{hud.Game} {src.Folder}: could not read {Path.GetFileName(dll)}: {ex.Message}"); continue; }
                var (findings, onceOnly) = Filter(all, kinds, includeOnceOnly);
                var refl = findings.Where(f => f.Kind == "reflection").ToList();
                var other = findings.Where(f => f.Kind != "reflection").ToList();
                var jit = PluginLint.JitOptimized(dll);
                var parts = new List<string>();
                if (kinds == null || kinds.Contains("api") || kinds.Contains("alloc"))
                    parts.Add($"{other.Count} expensive call site(s) on Tick/Render paths, {other.Count(f => f.InLoop)} in loops/lambdas");
                if (kinds == null || kinds.Contains("reflection"))
                    parts.Add($"{refl.Count(f => !f.OnceOnly)} reflection site(s)" + (onceOnly == 0 ? ""
                        : includeOnceOnly ? $" + {onceOnly} once-only cache fill(s)"
                        : $" (+{onceOnly} once-only cache fill(s) hidden: includeOnceOnly=true lists them)"));
                if (parts.Count == 0) parts.Add(jit ? "JIT optimized" : "JIT unoptimized");
                sb.AppendLine($"{hud.Game} {src.Folder}: {string.Join("; ", parts)}");
                if (!jit && (kinds == null || kinds.Contains("jit"))) sb.AppendLine($"  JIT  {PluginLint.JitAdvice}");
                // Reflection lines carry their call chain. A chain through a request handler is a lead, not proof of
                // per-frame cost. Once-only cache fills sort last, marked ONCE.
                foreach (var f in refl.Take(20))
                    sb.AppendLine($"  REFL {(f.OnceOnly ? "ONCE " : f.InLoop ? "LOOP " : "")}{f.Call} x{f.Count} via {f.Chain}");
                if (refl.Count > 20) sb.AppendLine($"  ... +{refl.Count - 20} more reflection sites (structuredContent)");
                foreach (var advice in refl.Where(f => !f.OnceOnly).Select(f => f.Advice).Distinct())
                    sb.AppendLine($"       -> {advice}");
                foreach (var f in other.Take(12))
                    sb.AppendLine($"  {(f.InLoop ? "LOOP" : "    ")} {f.Method}: {f.Call} x{f.Count} ({(f.CostNs > 0 ? $"~{f.CostNs:N0} ns each" : "allocates")}) -> {f.Advice}");
                if (other.Count > 12) sb.AppendLine($"  ... +{other.Count - 12} more (structuredContent)");
                result.Add(new JObject
                {
                    ["game"] = hud.Game, ["plugin"] = src.Folder,
                    ["jitOptimized"] = jit, ["jitAdvice"] = jit ? null : PluginLint.JitAdvice,
                    // Once-only reflection sites of the wanted kinds (listed in findings only with includeOnceOnly=true).
                    ["onceOnly"] = onceOnly,
                    ["findings"] = new JArray(findings.Select(f => new JObject
                    {
                        ["method"] = f.Method, ["call"] = f.Call, ["count"] = f.Count, ["inLoop"] = f.InLoop, ["costNs"] = f.CostNs, ["advice"] = f.Advice,
                        // kind (api / alloc / reflection), chain (Tick/Render > ... > method) and onceOnly ride in the DTO's Extra.
                        ["kind"] = f.Kind, ["chain"] = f.Chain, ["onceOnly"] = f.OnceOnly,
                    })),
                });
            }
        if (sb.Length == 0) sb.Append("No matching source plugins.");
        return Dto.Result(Dto.From<PluginLintResult>(new JObject { ["plugins"] = result }), sb.ToString());
    }

    /// <summary>The <c>kind</c> argument as a set (null = every kind); an unknown kind is an error naming the valid ones.</summary>
    public static HashSet<string>? ParseKinds(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var set = kind.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(k => k.ToLowerInvariant()).ToHashSet();
        var valid = PluginLint.Kinds.Append("jit").ToArray();
        if (set.FirstOrDefault(k => !valid.Contains(k)) is { } bad)
            throw new McpException($"kind '{bad}' is not a rule kind: use {string.Join(", ", valid)} (comma-separated for several).");
        return set;
    }

    /// <summary>Findings of the wanted kinds; once-only reflection dropped unless asked for, and counted either way.</summary>
    public static (List<PluginLint.Finding> findings, int onceOnly) Filter(IEnumerable<PluginLint.Finding> all, HashSet<string>? kinds, bool includeOnceOnly)
    {
        var wanted = all.Where(f => kinds == null || kinds.Contains(f.Kind)).ToList();
        var onceOnly = wanted.Count(f => f.OnceOnly);
        return (includeOnceOnly ? wanted : wanted.Where(f => !f.OnceOnly).ToList(), onceOnly);
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
