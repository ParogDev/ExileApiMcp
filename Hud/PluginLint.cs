using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ExileApiMcp.Hud;

/// <summary>
/// Offline static check of a compiled plugin for expensive HUD API calls on its per-frame paths. It reads the plugin
/// DLL's IL with System.Reflection.Metadata (nothing is loaded or run), builds the call graph inside the plugin
/// (direct calls plus delegates via ldftn, so lambdas count), takes everything reachable from Tick/Render, and reports
/// each call to a known-expensive member with its measured cost (knowledge pack shared/api-costs) and whether it sits
/// inside a loop (between a backward branch's target and the branch: per-entity cost). Reflection on those paths
/// (Type.GetProperty, PropertyInfo.GetValue, MethodInfo.Invoke, Activator.CreateInstance, dynamic call sites...) is
/// reported too, with the call chain from Tick/Render that reaches it.
/// </summary>
public static class PluginLint
{
    /// <summary>One call site group. Kind: "api" (a measured HUD member), "alloc" (allocating BCL call in per-item code)
    /// or "reflection". Chain: the methods from Tick/Render down to <see cref="Method"/>, joined with " > ".</summary>
    public sealed record Finding(string Method, string Call, int Count, bool InLoop, int CostNs, string Advice, string Kind = "api", string? Chain = null);

    /// <summary>Fix hint for every reflection finding.</summary>
    public const string ReflectionAdvice = "reflection on a per-frame path: resolve the member once and cache a delegate via CreateDelegate / compiled getter (Expression.Lambda(...).Compile())";

    /// <summary>Reflection members flagged on Tick/Render paths: (namespace, type simple name) -> member names.</summary>
    private static readonly Dictionary<(string ns, string type), HashSet<string>> ReflectionCalls = new()
    {
        [("System", "Type")] = ["GetProperty", "GetProperties", "GetField", "GetFields", "GetMethod", "GetMethods", "GetMember", "GetMembers", "GetConstructor", "InvokeMember"],
        [("System.Reflection", "TypeInfo")] = ["GetProperty", "GetProperties", "GetField", "GetFields", "GetMethod", "GetMethods", "GetMember", "GetMembers", "GetDeclaredProperty", "GetDeclaredField", "GetDeclaredMethod"],
        [("System.Reflection", "PropertyInfo")] = ["GetValue", "SetValue"],
        [("System.Reflection", "FieldInfo")] = ["GetValue", "SetValue"],
        [("System.Reflection", "MethodBase")] = ["Invoke"],
        [("System.Reflection", "MethodInfo")] = ["Invoke", "MakeGenericMethod"],
        [("System.Reflection", "ConstructorInfo")] = ["Invoke"],
        [("System", "Delegate")] = ["DynamicInvoke"],
        [("System", "Activator")] = ["CreateInstance"],
    };

    // Measured on PoE2, first call per entity per frame (shared/api-costs). Type simple name + member name, both games.
    private static readonly Dictionary<(string type, string member), (int ns, string advice)> Costs = new()
    {
        [("Entity", "get_Stats")] = (25000, "read only for entities already classified as interesting, at 10-20 Hz"),
        [("Entity", "get_Buffs")] = (7000, "read only for interesting entities, at 10-20 Hz"),
        [("Entity", "get_DistancePlayer")] = (2900, "filter on Path/Type/IsValid first; it equals the distance between Positioned.GridPosition of entity and player (player once per frame, compare squared)"),
        [("Entity", "get_Pos")] = (2400, "hold the Render component: Entity.Pos == Render.Pos + (0,0,Render.Bounds.Z), ~7x cheaper"),
        [("Entity", "get_PosNum")] = (2400, "hold the Render component: Render.PosNum + (0,0,Bounds.Z) (verify on PoE1)"),
        [("Entity", "get_GridPos")] = (530, "hold the Positioned component and use GridPosition"),
        [("Entity", "get_GridPosNum")] = (530, "hold the Positioned component"),
        [("Entity", "GetComponent")] = (240, "look the component up once per entity and keep the reference"),
        [("Entity", "TryGetComponent")] = (240, "look the component up once per entity and keep the reference"),
        [("Entity", "get_IsAlive")] = (200, "check once per entity per frame"),
        [("GameController", "get_Entities")] = (11900, "use EntityListWrapper.ValidEntitiesByType[type]"),
        [("Element", "get_IsVisible")] = (8000, "~8 us and ~3 KB for a nested element (it walks the parents; ~2.6 us for a top-level panel): check at 10 Hz, or check the parent once per frame"),
        [("Element", "get_IsVisibleLocal")] = (4600, "~4.6 us and ~2.7 KB on a cold element: check at 10 Hz, or read the flag bit (knowledge shared/api-costs)"),
        [("Element", "GetClientRect")] = (13000, "13-32 us and ~3-4 KB cold: for UI that does not move use GetClientRectCache (a 200 ms TimeCache, 0.3 us, no allocation)"),
        [("Camera", "WorldToScreen")] = (390, "take Camera.Snapshot once per frame and project with it"),
        [("Life", "get_HPPercentage")] = (330, "use CurHP/MaxHP"),
        [("Life", "get_ESPercentage")] = (330, "use CurES/MaxES"),
        [("Life", "get_MPPercentage")] = (330, "use CurMana/MaxMana"),
    };

    private static readonly Dictionary<short, OpCode> Ops = typeof(OpCodes).GetFields()
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    /// <summary>Advice when a compiled plugin carries DisableOptimizations (knowledge pack shared/api-costs).</summary>
    public const string JitAdvice = "compiled with DisableOptimizations (the HUD builds source plugins in Debug): the JIT runs every method unoptimized, ~2-4x slower. Add AssemblyInfo.cs with [assembly: System.Diagnostics.Debuggable(System.Diagnostics.DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]";

    /// <summary>
    /// Whether the JIT optimizes this DLL: false when its assembly-level DebuggableAttribute has DisableOptimizations
    /// (Roslyn synthesizes it in Debug builds unless the assembly declares its own), true otherwise.
    /// </summary>
    public static bool JitOptimized(string dllPath)
    {
        using var pe = new PEReader(File.OpenRead(dllPath));
        var md = pe.GetMetadataReader();
        foreach (var ah in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attr = md.GetCustomAttribute(ah);
            if (AttributeTypeName(md, attr.Constructor) != "DebuggableAttribute") continue;
            var blob = md.GetBlobReader(attr.Value);
            if (blob.ReadUInt16() != 1) continue; // prolog
            // Two constructors: (DebuggingModes modes), or (bool isJITTrackingEnabled, bool isJITOptimizerDisabled).
            if (blob.RemainingBytes >= 4 + 2) return (blob.ReadInt32() & 0x100) == 0; // DebuggingModes.DisableOptimizations
            blob.ReadBoolean();
            return !blob.ReadBoolean();
        }
        return true;
    }

    private static string? AttributeTypeName(MetadataReader md, EntityHandle ctor)
    {
        var parent = ctor.Kind switch
        {
            HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)ctor).Parent,
            HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType(),
            _ => default,
        };
        return parent.Kind switch
        {
            HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
            _ => null,
        };
    }

    /// <summary>Lint one plugin DLL. Entry points: methods named Tick or Render (and their lambdas/callees).</summary>
    public static List<Finding> Lint(string dllPath)
    {
        using var pe = new PEReader(File.OpenRead(dllPath));
        var md = pe.GetMetadataReader();
        var calls = new Dictionary<MethodDefinitionHandle, List<(int token, int pos, bool inLoop, bool ldftn)>>();
        foreach (var th in md.TypeDefinitions)
            foreach (var mh in md.GetTypeDefinition(th).GetMethods())
            {
                var mdef = md.GetMethodDefinition(mh);
                if (mdef.RelativeVirtualAddress == 0) continue;
                calls[mh] = Scan(pe.GetMethodBody(mdef.RelativeVirtualAddress).GetILBytes());
            }
        // Async methods and iterators (coroutines): the body is MoveNext of a nested <Method>d__N type that the stub never
        // calls directly. Add that edge (as a non-call, like ldftn) so their bodies are on the path too.
        foreach (var th in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(th);
            foreach (var nh in type.GetNestedTypes())
            {
                var nested = md.GetTypeDefinition(nh);
                var nestedName = md.GetString(nested.Name);
                var end = nestedName.IndexOf(">d__", StringComparison.Ordinal);
                if (!nestedName.StartsWith('<') || end < 2) continue;
                var owner = nestedName[1..end];
                var moveNext = nested.GetMethods().FirstOrDefault(m => md.GetString(md.GetMethodDefinition(m).Name) == "MoveNext");
                if (moveNext.IsNil) continue;
                foreach (var mh in type.GetMethods())
                    if (md.GetString(md.GetMethodDefinition(mh).Name) == owner && calls.TryGetValue(mh, out var list))
                        list.Add((MetadataTokens.GetToken(moveNext), 0, false, true));
            }
        }

        // Reachable from Tick/Render through the plugin's own methods (MethodDef targets, incl. ldftn for lambdas).
        var roots = calls.Keys.Where(h => md.GetString(md.GetMethodDefinition(h).Name) is "Tick" or "Render").ToList();
        // BFS keeps the first (shortest) caller of each method, so a finding can name its chain from Tick/Render.
        var reach = new HashSet<MethodDefinitionHandle>(roots);
        var caller = new Dictionary<MethodDefinitionHandle, MethodDefinitionHandle>();
        var queue = new Queue<MethodDefinitionHandle>(roots);
        while (queue.Count > 0)
        {
            var from = queue.Dequeue();
            foreach (var c in calls.GetValueOrDefault(from) ?? [])
            {
                var h = MetadataTokens.EntityHandle(c.token);
                if (h.Kind == HandleKind.MethodDefinition && reach.Add((MethodDefinitionHandle)h))
                {
                    caller[(MethodDefinitionHandle)h] = from;
                    queue.Enqueue((MethodDefinitionHandle)h);
                }
            }
        }
        string Name(MethodDefinitionHandle h)
        {
            var d = md.GetMethodDefinition(h);
            return $"{md.GetString(md.GetTypeDefinition(d.GetDeclaringType()).Name)}.{md.GetString(d.Name)}";
        }
        string Chain(MethodDefinitionHandle h)
        {
            var path = new List<string> { Name(h) };
            while (caller.TryGetValue(h, out var up) && path.Count < 32) { path.Add(Name(up)); h = up; }
            path.Reverse();
            return string.Join(" > ", path);
        }

        var findings = new List<Finding>();
        foreach (var mh in reach)
        {
            var isLambda0 = md.GetString(md.GetMethodDefinition(mh).Name).StartsWith("<", StringComparison.Ordinal);
            var groups = new Dictionary<(string call, bool loop), (int n, int ns, string advice)>();
            var reflection = new Dictionary<(string call, bool loop), int>();
            foreach (var c in calls.GetValueOrDefault(mh) ?? [])
            {
                if (c.ldftn) continue;
                if (Reflection(md, c.token) is { } r)
                {
                    var rk = (r, c.inLoop || isLambda0);
                    reflection[rk] = reflection.GetValueOrDefault(rk) + 1;
                    continue;
                }
                if (Target(md, c.token) is not { } t) continue;
                if (!Costs.TryGetValue(t, out var cost))
                {
                    // Allocations in per-item code: garbage every frame, GC pauses at high fps (frame-time spikes).
                    if (!c.inLoop && !isLambda0 || Alloc(t) is not { } a) continue;
                    cost = (0, a);
                }
                var key = ($"{t.type}.{t.member.Replace("get_", "")}", c.inLoop);
                groups[key] = groups.TryGetValue(key, out var g) ? (g.n + 1, cost.ns, cost.advice) : (1, cost.ns, cost.advice);
            }
            var mdef0 = md.GetMethodDefinition(mh);
            // Compiler-generated lambdas (LINQ predicates, ForEach bodies) run once per item: count their calls as in a loop.
            var isLambda = md.GetString(mdef0.Name).StartsWith("<", StringComparison.Ordinal);
            if (isLambda)
                groups = groups.GroupBy(kv => (kv.Key.call, loop: true))
                    .ToDictionary(g => g.Key, g => (g.Sum(x => x.Value.n), g.First().Value.ns, g.First().Value.advice));
            var name = Name(mh);
            findings.AddRange(groups.Select(g => new Finding(name, g.Key.call, g.Value.n, g.Key.loop, g.Value.ns, g.Value.advice, g.Value.ns > 0 ? "api" : "alloc")));
            if (reflection.Count > 0)
            {
                var chain = Chain(mh);
                findings.AddRange(reflection.Select(r => new Finding(name, r.Key.call, r.Value, r.Key.loop, 0, ReflectionAdvice, "reflection", chain)));
            }
        }
        return findings.OrderByDescending(f => f.InLoop).ThenByDescending(f => f.Kind == "reflection").ThenByDescending(f => f.CostNs * f.Count).ToList();
    }

    /// <summary>
    /// The reflection call a token names ("MethodBase.Invoke", "dynamic (Binder.InvokeMember)"), or null. Matched by
    /// namespace too: Microsoft.CSharp.RuntimeBinder.Binder is a dynamic call site, System.Reflection.Binder is not.
    /// </summary>
    private static string? Reflection(MetadataReader md, int token)
    {
        var h = MetadataTokens.EntityHandle(token);
        if (h.Kind == HandleKind.MethodSpecification) h = md.GetMethodSpecification((MethodSpecificationHandle)h).Method;
        if (h.Kind != HandleKind.MemberReference) return null;
        var mr = md.GetMemberReference((MemberReferenceHandle)h);
        if (mr.Parent.Kind != HandleKind.TypeReference) return null;
        var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
        var (ns, type, member) = (md.GetString(tr.Namespace), md.GetString(tr.Name), md.GetString(mr.Name));
        if (ns == "Microsoft.CSharp.RuntimeBinder" && type == "Binder") return $"dynamic (Binder.{member})";
        return ReflectionCalls.TryGetValue((ns, type), out var members) && members.Contains(member) ? $"{type}.{member}" : null;
    }

    /// <summary>Allocating BCL calls worth flagging in per-item code, with the fix.</summary>
    private static string? Alloc((string type, string member) t) => t switch
    {
        ("Enumerable", "ToList" or "ToArray" or "ToDictionary" or "ToHashSet" or "Where" or "Select" or "OrderBy" or "OrderByDescending" or "GroupBy" or "Concat" or "SelectMany" or "Distinct") =>
            "LINQ allocates enumerators/lists per item per frame: use a for loop over a reused buffer",
        ("String", "Format" or "Concat" or "Join" or "Substring" or "Replace" or "Split") =>
            "builds a new string per item per frame: cache it and rebuild only when the value changes",
        ("DefaultInterpolatedStringHandler", "ToStringAndClear") => "string interpolation per item per frame: cache the text, rebuild on change",
        _ => null,
    };

    /// <summary>The (declaring type simple name, member name) of a call target outside the plugin.</summary>
    private static (string type, string member)? Target(MetadataReader md, int token)
    {
        var h = MetadataTokens.EntityHandle(token);
        if (h.Kind == HandleKind.MethodSpecification) h = md.GetMethodSpecification((MethodSpecificationHandle)h).Method;
        if (h.Kind != HandleKind.MemberReference) return null;
        var mr = md.GetMemberReference((MemberReferenceHandle)h);
        var parent = mr.Parent;
        if (parent.Kind == HandleKind.TypeSpecification) return null;   // generic instantiations: not in the table
        var typeName = parent.Kind switch
        {
            HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
            _ => null,
        };
        return typeName == null ? null : (typeName, md.GetString(mr.Name));
    }

    /// <summary>Call/callvirt/newobj/ldftn tokens with their IL offset and whether they sit inside a loop body.</summary>
    private static List<(int token, int pos, bool inLoop, bool ldftn)> Scan(byte[] il)
    {
        var raw = new List<(int token, int pos, bool ldftn)>();
        var loops = new List<(int from, int to)>();
        var pos = 0;
        while (pos < il.Length)
        {
            var at = pos;
            short code = il[pos++];
            if (code == 0xFE && pos < il.Length) code = (short)(0xFE00 | il[pos++]);
            if (!Ops.TryGetValue(code, out var op)) break;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                {
                    var target = pos + 1 + (sbyte)il[pos]; pos += 1;
                    if (target <= at) loops.Add((target, at));
                    break;
                }
                case OperandType.InlineBrTarget:
                {
                    var target = pos + 4 + BitConverter.ToInt32(il, pos); pos += 4;
                    if (target <= at) loops.Add((target, at));
                    break;
                }
                case OperandType.ShortInlineI: case OperandType.ShortInlineVar: pos += 1; break;
                case OperandType.InlineVar: pos += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: pos += 8; break;
                case OperandType.InlineSwitch: pos += 4 + 4 * BitConverter.ToInt32(il, pos); break;
                case OperandType.InlineMethod:
                    raw.Add((BitConverter.ToInt32(il, pos), at, op.Value == OpCodes.Ldftn.Value || op.Value == OpCodes.Ldvirtftn.Value));
                    pos += 4; break;
                default: pos += 4; break;   // tokens, InlineI, ShortInlineR, InlineSig, InlineString...
            }
        }
        return raw.Select(r => (r.token, r.pos, loops.Any(l => r.pos >= l.from && r.pos <= l.to), r.ldftn)).ToList();
    }
}
