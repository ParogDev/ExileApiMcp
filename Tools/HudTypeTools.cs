using System.ComponentModel;
using System.Reflection;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hud;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Offline API reference for HUD plugin authors: types and members of the HUD's assemblies
/// (ExileCore / ExileCore2, GameOffsets / GameOffsets2, ImGui.NET, ...), read from the DLL metadata
/// on disk. Complements describe_type / eval_path, which need a running HUD and start at
/// GameController, and only see public members.
/// </summary>
[McpServerToolType]
public static class HudTypeTools
{
    private const string GameOpt = "'poe1' or 'poe2'; omit to search every HUD installed";
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [McpServerTool(Name = "hud_find_types", Title = "Find HUD API types", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(HudFindTypesResult))]
    [Description("Search the HUD's assemblies for types by name, or for types that have a member with a given name " +
                 "(e.g. member='ActiveWeaponSetIndex'). Reads DLL metadata from disk: works with the game and HUD closed. " +
                 "Follow up with hud_type for members.")]
    public static CallToolResult HudFindTypes(BridgeRegistry bridges,
        [Description("Substring of the type name or full name (case-insensitive), e.g. 'Stats', 'ExileCore2.PoEMemory.Components'")] string? query = null,
        [Description("Instead: find types declaring a member (field/property/method) whose name contains this")] string? member = null,
        [Description("Include non-public types and members (default false)")] bool nonPublic = false,
        [Description("Max results (1-200, default 40)")] int max = 40,
        [Description(GameOpt)] string? game = null)
    {
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(member))
            throw new McpException("Pass query (type name) or member (member name).");
        max = Math.Clamp(max, 1, 200);
        var results = new JArray();
        var total = 0;

        foreach (var hud in HudDevTools.Installs(bridges, game))
        {
            var types = HudTypes.For(hud).AllTypes().Where(t => nonPublic || IsVisible(t));
            if (!string.IsNullOrWhiteSpace(query))
            {
                var q = query.Trim();
                var hits = types
                    .Where(t => (t.FullName ?? t.Name).Contains(q, StringComparison.OrdinalIgnoreCase))
                    .Where(t => !t.Name.StartsWith('<')) // compiler-generated
                    .OrderBy(t => t.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0 : t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                    .ThenBy(t => t.FullName, StringComparer.Ordinal)
                    .ToList();
                total += hits.Count;
                foreach (var t in hits.Take(max - results.Count)) results.Add(TypeSummary(hud, t));
            }
            else
            {
                var m = member!.Trim();
                var flags = nonPublic ? All | BindingFlags.DeclaredOnly : BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var t in types)
                {
                    MemberInfo[] members;
                    try { members = t.GetMembers(flags); } catch (Exception) { continue; }
                    foreach (var mi in members.Where(x => x.Name.Contains(m, StringComparison.OrdinalIgnoreCase) && !x.Name.StartsWith('<') && !IsAccessor(x)))
                    {
                        total++;
                        if (results.Count >= max) continue;
                        results.Add(new JObject
                        {
                            ["game"] = hud.Game,
                            ["type"] = t.FullName,
                            ["member"] = mi.Name,
                            ["memberKind"] = mi.MemberType.ToString().ToLowerInvariant(),
                            ["declaration"] = Describe(mi),
                        });
                    }
                }
            }
        }
        var o = new JObject { ["total"] = total, ["results"] = results };
        if (total > results.Count) o["truncated"] = $"Showing {results.Count} of {total}; narrow the query or raise max.";
        return Dto.Result(Dto.From<HudFindTypesResult>(o), o.ToString(Newtonsoft.Json.Formatting.None));
    }

    [McpServerTool(Name = "hud_type", Title = "HUD API type members", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(HudTypeResult))]
    [Description("Members of one HUD type from DLL metadata: fields (with [FieldOffset] for GameOffsets structs), " +
                 "properties, methods with signatures, enum values, base types, interfaces, nested types. Can include " +
                 "non-public members and inherited ones. Works with the game and HUD closed. Note: GameOffsets2 (PoE2) " +
                 "offsets are obfuscated decoys - verify layouts live or in Ghidra before relying on them.")]
    public static CallToolResult HudType(BridgeRegistry bridges,
        [Description("Full name (e.g. 'ExileCore2.PoEMemory.Components.Stats') or a unique simple name ('Stats' asks you to pick when ambiguous)")] string name,
        [Description("Include non-public members (default false)")] bool nonPublic = false,
        [Description("Include members inherited from base types (default true; declaringType shows where they come from)")] bool inherited = true,
        [Description("Only members whose name contains this (case-insensitive)")] string? filter = null,
        [Description("Max members (1-500, default 200)")] int max = 200,
        [Description(GameOpt)] string? game = null)
    {
        max = Math.Clamp(max, 1, 500);
        var found = new List<(HudInstall Hud, Type Type)>();
        var candidates = new List<string>();
        foreach (var hud in HudDevTools.Installs(bridges, game))
        {
            var types = HudTypes.For(hud).AllTypes().ToList();
            var exact = types.Where(t => string.Equals(t.FullName, name, StringComparison.Ordinal)).ToList();
            if (exact.Count == 0) exact = types.Where(t => string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 0) exact = types.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) found.Add((hud, exact[0]));
            else candidates.AddRange(exact.Select(t => $"{hud.Game}: {t.FullName}"));
        }
        if (found.Count == 0)
        {
            throw new McpException(candidates.Count > 0
                ? $"'{name}' is ambiguous; pass a full name: {string.Join(", ", candidates.Take(20))}"
                : $"No type '{name}'. Search with hud_find_types query='{name}'.");
        }
        var result = new JObject { ["types"] = new JArray(found.Select(f => TypeDetail(f.Hud, f.Type, nonPublic, inherited, filter, max))) };
        return Dto.Result(Dto.From<HudTypeResult>(result), result.ToString(Newtonsoft.Json.Formatting.None));
    }

    // ── Shapes ───────────────────────────────────────────────────────

    private static JObject TypeSummary(HudInstall hud, Type t)
    {
        var o = new JObject
        {
            ["game"] = hud.Game,
            ["type"] = t.FullName,
            ["kind"] = HudTypes.Kind(t),
            ["assembly"] = t.Assembly.GetName().Name,
        };
        var b = SafeBase(t);
        if (b != null && b.FullName is not ("System.Object" or "System.ValueType" or "System.Enum")) o["base"] = HudTypes.Name(b, true);
        if (!IsVisible(t)) o["nonPublic"] = true;
        return o;
    }

    private static JObject TypeDetail(HudInstall hud, Type t, bool nonPublic, bool inherited, string? filter, int max)
    {
        var o = TypeSummary(hud, t);
        var chain = new List<string>();
        for (var b = SafeBase(t); b != null && b.FullName != "System.Object"; b = SafeBase(b)) chain.Add(HudTypes.Name(b, true));
        if (chain.Count > 0) o["baseChain"] = new JArray(chain);
        try
        {
            var ifaces = t.GetInterfaces().Select(i => HudTypes.Name(i, true)).ToList();
            if (ifaces.Count > 0) o["interfaces"] = new JArray(ifaces);
        }
        catch (FileNotFoundException) { }

        bool Keep(MemberInfo m) => (filter == null || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) && !m.Name.StartsWith('<');
        var flags = (nonPublic ? All : BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) | (inherited ? 0 : BindingFlags.DeclaredOnly);
        if (inherited && nonPublic) flags |= BindingFlags.FlattenHierarchy;
        var shown = 0;
        var skipped = 0;
        JObject Member(MemberInfo m, JObject body)
        {
            if (m.DeclaringType != t && m.DeclaringType != null) body["declaringType"] = m.DeclaringType.Name;
            return body;
        }

        if (t.IsEnum)
        {
            var values = new JObject();
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(Keep))
            {
                if (shown++ >= max) { skipped++; continue; }
                values[f.Name] = JToken.FromObject(f.GetRawConstantValue() ?? 0);
            }
            o["values"] = values;
        }
        else
        {
            var fields = new JArray();
            foreach (var f in SafeMembers(() => t.GetFields(flags)).Where(Keep).OrderBy(f => HudTypes.FieldOffset(f) ?? int.MaxValue))
            {
                if (shown++ >= max) { skipped++; continue; }
                var fo = new JObject { ["name"] = f.Name, ["type"] = HudTypes.Name(f.FieldType) };
                if (HudTypes.FieldOffset(f) is int off)
                {
                    fo["offset"] = $"0x{off:X}";
                    // No game struct is megabytes long: GameOffsets2 publishes decoy offsets like these.
                    if (off is < 0 or > 0x100000) fo["suspect"] = "implausible offset (obfuscated decoy?) - verify live or in Ghidra";
                }
                if (f.IsStatic) fo["static"] = true;
                if (f.IsLiteral) try { fo["const"] = JToken.FromObject(f.GetRawConstantValue() ?? "null"); } catch (Exception) { }
                if (!f.IsPublic) fo["access"] = HudTypes.Access(f);
                fields.Add(Member(f, fo));
            }
            if (fields.Count > 0) o["fields"] = fields;

            var props = new JArray();
            foreach (var p in SafeMembers(() => t.GetProperties(flags)).Where(Keep).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (shown++ >= max) { skipped++; continue; }
                var getter = p.GetMethod;
                var setter = p.SetMethod;
                var po = new JObject { ["name"] = p.Name, ["type"] = HudTypes.Name(p.PropertyType) };
                var acc = (getter != null ? "get" : "") + (setter != null ? (getter != null ? ";" : "") + (setter.IsPublic ? "set" : HudTypes.Access(setter) + " set") : "");
                po["accessors"] = acc;
                if ((getter ?? setter)?.IsStatic == true) po["static"] = true;
                if (getter != null && !getter.IsPublic) po["access"] = HudTypes.Access(getter);
                var idx = p.GetIndexParameters();
                if (idx.Length > 0) po["indexer"] = string.Join(", ", idx.Select(i => HudTypes.Name(i.ParameterType) + " " + i.Name));
                props.Add(Member(p, po));
            }
            if (props.Count > 0) o["properties"] = props;

            var methods = new JArray();
            var ctorFlags = flags & ~BindingFlags.Static & ~BindingFlags.FlattenHierarchy | BindingFlags.DeclaredOnly;
            foreach (var c in SafeMembers(() => t.GetConstructors(ctorFlags)).Where(c => filter == null))
            {
                if (shown++ >= max) { skipped++; continue; }
                methods.Add(new JObject { ["signature"] = "new " + HudTypes.Signature(c).Replace(".ctor", t.Name), ["access"] = HudTypes.Access(c) });
            }
            foreach (var m in SafeMembers(() => t.GetMethods(flags)).Where(m => Keep(m) && !m.IsSpecialName && m.DeclaringType?.FullName != "System.Object")
                         .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (shown++ >= max) { skipped++; continue; }
                var mo = new JObject { ["signature"] = SafeSignature(m) };
                if (m.IsStatic) mo["static"] = true;
                if (m.IsAbstract) mo["abstract"] = true;
                else if (m.IsVirtual && !m.IsFinal) mo["virtual"] = true;
                if (!m.IsPublic) mo["access"] = HudTypes.Access(m);
                methods.Add(Member(m, mo));
            }
            if (methods.Count > 0) o["methods"] = methods;

            var nested = SafeMembers(() => t.GetNestedTypes(nonPublic ? BindingFlags.Public | BindingFlags.NonPublic : BindingFlags.Public))
                .Where(n => !n.Name.StartsWith('<')).Select(n => n.Name).ToList();
            if (nested.Count > 0) o["nestedTypes"] = new JArray(nested);
        }
        if (skipped > 0) o["truncated"] = $"{skipped} more members; use filter or raise max.";
        return o;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static bool IsVisible(Type t)
    {
        try { return t.IsVisible; } catch (Exception) { return t.IsPublic; }
    }

    private static Type? SafeBase(Type t)
    {
        try { return t.BaseType; } catch (FileNotFoundException) { return null; }
    }

    private static T[] SafeMembers<T>(Func<T[]> get)
    {
        try { return get(); } catch (Exception e) when (e is FileNotFoundException or TypeLoadException or NotSupportedException) { return []; }
    }

    private static string SafeSignature(MethodBase m)
    {
        try { return HudTypes.Signature(m); } catch (Exception) { return m.Name + "(?)"; }
    }

    private static bool IsAccessor(MemberInfo m) => m is MethodInfo { IsSpecialName: true };

    private static string Describe(MemberInfo m)
    {
        try
        {
            return m switch
            {
                FieldInfo f => $"{HudTypes.Name(f.FieldType)} {f.Name}" + (HudTypes.FieldOffset(f) is int off ? $" @0x{off:X}" + (off is < 0 or > 0x100000 ? " (implausible: decoy?)" : "") : ""),
                PropertyInfo p => $"{HudTypes.Name(p.PropertyType)} {p.Name} {{ {(p.GetMethod != null ? "get; " : "")}{(p.SetMethod != null ? "set; " : "")}}}",
                MethodBase mb => HudTypes.Signature(mb),
                Type nt => $"nested {HudTypes.Kind(nt)} {nt.Name}",
                _ => m.Name,
            };
        }
        catch (Exception) { return m.Name; }
    }
}
