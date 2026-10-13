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
    /// or "reflection". Chain: the methods from Tick/Render down to <see cref="Method"/>, joined with " > ".
    /// OnceOnly (reflection only): the call fills a cache once, not per frame: it sits behind a null/flag/TryGetValue
    /// guard whose branch stores the same field/property/dictionary (lazy init, <c>??=</c>, <c>if (_x == null) _x = ...</c>,
    /// TryGetValue-then-add), in a GetOrAdd / Lazy&lt;T&gt; factory, or in a method reached only through such guards.</summary>
    public sealed record Finding(string Method, string Call, int Count, bool InLoop, int CostNs, string Advice, string Kind = "api", string? Chain = null, bool OnceOnly = false);

    /// <summary>The rule kinds a finding can carry (hud_plugin_lint's <c>kind</c> filter).</summary>
    public static readonly string[] Kinds = ["api", "alloc", "reflection"];

    /// <summary>Fix hint for every reflection finding.</summary>
    public const string ReflectionAdvice = "reflection on a per-frame path: resolve the member once and cache a delegate via CreateDelegate / compiled getter (Expression.Lambda(...).Compile())";

    /// <summary>Fix hint for Expression.Compile on a per-frame path.</summary>
    public const string CompileAdvice = "Expression.Compile builds and JITs a new delegate (allocates, ~100 us+) every call: compile once into a field and reuse it";

    /// <summary>Fix hint for custom attribute lookups on a per-frame path.</summary>
    public const string AttributeAdvice = "attribute lookup walks metadata and allocates attribute instances every call: read it once per type/member and cache the result";

    /// <summary>Reflection members flagged on Tick/Render paths: (namespace, type simple name) -> member names.</summary>
    private static readonly Dictionary<(string ns, string type), HashSet<string>> ReflectionCalls = new()
    {
        [("System.Reflection", "CustomAttributeExtensions")] = ["GetCustomAttribute", "GetCustomAttributes", "IsDefined"],
        [("System.Reflection", "MemberInfo")] = ["GetCustomAttribute", "GetCustomAttributes", "IsDefined"],
        [("System", "Attribute")] = ["GetCustomAttribute", "GetCustomAttributes", "IsDefined"],
        [("System.Linq.Expressions", "LambdaExpression")] = ["Compile"],
        [("System.Linq.Expressions", "Expression`1")] = ["Compile"],
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
        var bodies = new Dictionary<MethodDefinitionHandle, List<Insn>>();
        foreach (var th in md.TypeDefinitions)
            foreach (var mh in md.GetTypeDefinition(th).GetMethods())
            {
                var mdef = md.GetMethodDefinition(mh);
                if (mdef.RelativeVirtualAddress == 0) continue;
                bodies[mh] = Scan(pe.GetMethodBody(mdef.RelativeVirtualAddress).GetILBytes() ?? []);
            }
        // The fields each method stores, so `if (_x == null) Bind();` counts as a guard when Bind() stores _x.
        var stores = bodies.ToDictionary(kv => kv.Key, kv => kv.Value.Where(i => i.Op is OpStfld or OpStsfld).Select(i => i.Operand).ToHashSet());
        var calls = bodies.ToDictionary(kv => kv.Key, kv => Calls(md, kv.Value, stores));
        // Async methods, async lambdas/local functions and iterators (coroutines): the body is MoveNext of a compiler-made
        // state machine type that the stub never calls directly. The stub names it in its [AsyncStateMachine] /
        // [IteratorStateMachine] / [AsyncIteratorStateMachine] attribute; add that edge (as a non-call, like ldftn) so the
        // bodies are on the path too. Matched per stub, so overloads don't cross-link.
        var typesByName = md.TypeDefinitions.GroupBy(FullName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var mh in calls.Keys)
            foreach (var ah in md.GetMethodDefinition(mh).GetCustomAttributes())
            {
                var attr = md.GetCustomAttribute(ah);
                if (AttributeTypeName(md, attr.Constructor) is not ("AsyncStateMachineAttribute" or "IteratorStateMachineAttribute" or "AsyncIteratorStateMachineAttribute")) continue;
                var blob = md.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() != 1 || blob.ReadSerializedString() is not { } smName) continue;
                // A serialized type name may carry ", Assembly..." after the type; nested types are joined with '+'.
                var comma = smName.IndexOf(", ", StringComparison.Ordinal);
                if (comma > 0) smName = smName[..comma];
                if (!typesByName.TryGetValue(smName, out var sm)) continue;
                var moveNext = md.GetTypeDefinition(sm).GetMethods().FirstOrDefault(m => md.GetString(md.GetMethodDefinition(m).Name) == "MoveNext");
                if (!moveNext.IsNil) calls[mh].Add(new Call(MetadataTokens.GetToken(moveNext), 0, false, true, false));
            }
        string FullName(TypeDefinitionHandle th)
        {
            var t = md.GetTypeDefinition(th);
            var outer = t.GetDeclaringType();
            if (!outer.IsNil) return FullName(outer) + "+" + md.GetString(t.Name);
            var ns = md.GetString(t.Namespace);
            return ns.Length == 0 ? md.GetString(t.Name) : ns + "." + md.GetString(t.Name);
        }

        // Reachable from Tick/Render through the plugin's own methods (MethodDef targets, incl. ldftn for lambdas).
        var roots = calls.Keys.Where(h => md.GetString(md.GetMethodDefinition(h).Name) is "Tick" or "Render").ToList();
        // BFS keeps the first (shortest) caller of each method, so a finding can name its chain from Tick/Render. Two
        // passes: every edge (reach), and only edges outside once-only guards (hot): a method reached only through
        // guards (a Bind() behind `if (_x == null)`) fills a cache once, its reflection is not per frame.
        var (reach, caller) = Bfs(roots, guardedToo: true);
        var (hot, hotCaller) = Bfs(roots, guardedToo: false);
        (HashSet<MethodDefinitionHandle>, Dictionary<MethodDefinitionHandle, MethodDefinitionHandle>) Bfs(List<MethodDefinitionHandle> from0, bool guardedToo)
        {
            var seen = new HashSet<MethodDefinitionHandle>(from0);
            var up = new Dictionary<MethodDefinitionHandle, MethodDefinitionHandle>();
            var queue = new Queue<MethodDefinitionHandle>(from0);
            while (queue.Count > 0)
            {
                var from = queue.Dequeue();
                foreach (var c in calls.GetValueOrDefault(from) ?? [])
                {
                    if (c.Guarded && !guardedToo) continue;
                    var h = MetadataTokens.EntityHandle(c.Token);
                    if (h.Kind == HandleKind.MethodDefinition && seen.Add((MethodDefinitionHandle)h))
                    {
                        up[(MethodDefinitionHandle)h] = from;
                        queue.Enqueue((MethodDefinitionHandle)h);
                    }
                }
            }
            return (seen, up);
        }
        string Name(MethodDefinitionHandle h)
        {
            var d = md.GetMethodDefinition(h);
            return $"{md.GetString(md.GetTypeDefinition(d.GetDeclaringType()).Name)}.{md.GetString(d.Name)}";
        }
        string Chain(MethodDefinitionHandle h, Dictionary<MethodDefinitionHandle, MethodDefinitionHandle> caller)
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
            var reflection = new Dictionary<(string call, bool loop, bool once), int>();
            var methodHot = hot.Contains(mh);
            foreach (var c in calls.GetValueOrDefault(mh) ?? [])
            {
                if (c.Ldftn) continue;
                if (Reflection(md, c.Token) is { } r)
                {
                    var rk = (r, c.InLoop || isLambda0, c.Guarded || !methodHot);
                    reflection[rk] = reflection.GetValueOrDefault(rk) + 1;
                    continue;
                }
                if (Target(md, c.Token) is not { } t) continue;
                if (!Costs.TryGetValue(t, out var cost))
                {
                    // Allocations in per-item code: garbage every frame, GC pauses at high fps (frame-time spikes).
                    if (!c.InLoop && !isLambda0 || Alloc(t) is not { } a) continue;
                    cost = (0, a);
                }
                var key = ($"{t.type}.{t.member.Replace("get_", "")}", c.InLoop);
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
                var chainAny = Chain(mh, caller);
                var chainHot = methodHot ? Chain(mh, hotCaller) : chainAny;
                findings.AddRange(reflection.Select(r => new Finding(name, r.Key.call, r.Value, r.Key.loop, 0, ReflectionAdviceFor(r.Key.call), "reflection",
                    r.Key.once ? chainAny : chainHot, r.Key.once)));
            }
        }
        return findings.OrderBy(f => f.OnceOnly).ThenByDescending(f => f.InLoop).ThenByDescending(f => f.Kind == "reflection").ThenByDescending(f => f.CostNs * f.Count).ToList();
    }

    private static string ReflectionAdviceFor(string call) =>
        call.EndsWith(".Compile", StringComparison.Ordinal) ? CompileAdvice
        : call.Contains("CustomAttribute", StringComparison.Ordinal) || call.EndsWith(".IsDefined", StringComparison.Ordinal) ? AttributeAdvice
        : ReflectionAdvice;

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
        // Generic parents (Expression<TDelegate>.Compile) are a TypeSpecification: read its generic type definition.
        if (ParentTypeRef(md, mr.Parent) is not { } tr) return null;
        var (ns, type, member) = (md.GetString(tr.Namespace), md.GetString(tr.Name), md.GetString(mr.Name));
        if (ns == "Microsoft.CSharp.RuntimeBinder" && type == "Binder") return $"dynamic (Binder.{member})";
        var tick = type.IndexOf('`');
        return ReflectionCalls.TryGetValue((ns, type), out var members) && members.Contains(member) ? $"{(tick > 0 ? type[..tick] : type)}.{member}" : null;
    }

    /// <summary>The TypeReference a member's parent names: itself, or the generic definition of a GENERICINST TypeSpecification.</summary>
    private static TypeReference? ParentTypeRef(MetadataReader md, EntityHandle parent)
    {
        if (parent.Kind == HandleKind.TypeReference) return md.GetTypeReference((TypeReferenceHandle)parent);
        if (parent.Kind != HandleKind.TypeSpecification) return null;
        var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
        if (blob.ReadByte() != 0x15) return null;                          // ELEMENT_TYPE_GENERICINST
        if (blob.ReadByte() is not (0x12 or 0x11)) return null;            // CLASS / VALUETYPE
        var def = blob.ReadTypeHandle();
        return def.Kind == HandleKind.TypeReference ? md.GetTypeReference((TypeReferenceHandle)def) : null;
    }

    /// <summary>The member name a method token names (MethodDef, MemberRef or MethodSpec), or null.</summary>
    private static string? MemberName(MetadataReader md, int token)
    {
        var h = MetadataTokens.EntityHandle(token);
        if (h.Kind == HandleKind.MethodSpecification) h = md.GetMethodSpecification((MethodSpecificationHandle)h).Method;
        return h.Kind switch
        {
            HandleKind.MemberReference => md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name),
            HandleKind.MethodDefinition => md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)h).Name),
            _ => null,
        };
    }

    /// <summary>The simple name of the type a method token's member is declared on ("Lazy`1"), or null.</summary>
    private static string? DeclaringTypeName(MetadataReader md, int token)
    {
        var h = MetadataTokens.EntityHandle(token);
        if (h.Kind == HandleKind.MethodSpecification) h = md.GetMethodSpecification((MethodSpecificationHandle)h).Method;
        if (h.Kind == HandleKind.MethodDefinition) return md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)h).GetDeclaringType()).Name);
        if (h.Kind != HandleKind.MemberReference) return null;
        return ParentTypeRef(md, md.GetMemberReference((MemberReferenceHandle)h).Parent) is { } tr ? md.GetString(tr.Name) : null;
    }

    /// <summary>The name of the field a ldfld/stfld token names (FieldDef or MemberRef), or "".</summary>
    private static string FieldName(MetadataReader md, int token)
    {
        var h = MetadataTokens.EntityHandle(token);
        return h.Kind switch
        {
            HandleKind.FieldDefinition => md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)h).Name),
            HandleKind.MemberReference => md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name),
            _ => "",
        };
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

    /// <summary>One IL instruction: offset, opcode value, operand (a token, or the absolute target of a branch; 0 otherwise).</summary>
    private readonly record struct Insn(int Pos, short Op, int Operand, OperandType Type)
    {
        public bool IsBranch => Type is OperandType.ShortInlineBrTarget or OperandType.InlineBrTarget;
    }

    /// <summary>A call/callvirt/newobj/ldftn site. Guarded: it runs once per cache fill, not per frame (see <see cref="Finding"/>).</summary>
    private readonly record struct Call(int Token, int Pos, bool InLoop, bool Ldftn, bool Guarded);

    private const short OpLdfld = 0x7B, OpLdsfld = 0x7E, OpStfld = 0x7D, OpStsfld = 0x80, OpCall = 0x28, OpCallvirt = 0x6F, OpNewobj = 0x73,
        OpDup = 0x25, OpRet = 0x2A, OpThrow = 0x7A;

    /// <summary>Decode a method body into instructions (stops at an unknown opcode).</summary>
    private static List<Insn> Scan(byte[] il)
    {
        var list = new List<Insn>();
        var pos = 0;
        while (pos < il.Length)
        {
            var at = pos;
            short code = il[pos++];
            if (code == 0xFE && pos < il.Length) code = (short)(0xFE00 | il[pos++]);
            if (!Ops.TryGetValue(code, out var op)) break;
            var operand = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: operand = pos + 1 + (sbyte)il[pos]; pos += 1; break;
                case OperandType.InlineBrTarget: operand = pos + 4 + BitConverter.ToInt32(il, pos); pos += 4; break;
                case OperandType.ShortInlineI: case OperandType.ShortInlineVar: pos += 1; break;
                case OperandType.InlineVar: pos += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: pos += 8; break;
                case OperandType.InlineSwitch: pos += 4 + 4 * BitConverter.ToInt32(il, pos); break;
                default: operand = BitConverter.ToInt32(il, pos); pos += 4; break;   // tokens, InlineI, ShortInlineR, InlineSig, InlineString...
            }
            list.Add(new Insn(at, op.Value, operand, op.OperandType));
        }
        return list;
    }

    /// <summary>
    /// The call sites of one method: in a loop body (between a backward branch's target and the branch: per-entity
    /// cost), and guarded (once-only). A direct call is guarded inside a once-only region (<see cref="GuardedRegions"/>).
    /// A lambda (ldftn) is guarded when its delegate goes to GetOrAdd / AddOrUpdate / LazyInitializer / new Lazy&lt;T&gt;
    /// (a factory that runs once per key/instance), or when it is created inside a once-only region and handed straight
    /// to a call (<c>_loading ??= Task.Run(() => ...)</c>). A delegate stored into a field (<c>_get ??= x => ...</c>) is not
    /// guarded: creating it is once-only, but every later call of it runs the body.
    /// </summary>
    private static List<Call> Calls(MetadataReader md, List<Insn> ins, Dictionary<MethodDefinitionHandle, HashSet<int>> stores)
    {
        var loops = ins.Where(i => i.IsBranch && i.Operand <= i.Pos).Select(i => (from: i.Operand, to: i.Pos)).ToList();
        var regions = GuardedRegions(md, ins, stores);
        var result = new List<Call>();
        for (var k = 0; k < ins.Count; k++)
        {
            var i = ins[k];
            if (i.Type != OperandType.InlineMethod) continue;
            var ldftn = i.Op == OpCodes.Ldftn.Value || i.Op == OpCodes.Ldvirtftn.Value;
            var guarded = regions.Any(r => i.Pos >= r.from && i.Pos < r.to);
            if (ldftn) guarded = FactoryLambda(md, ins, k) || guarded && !DelegateStored(md, ins, k);
            result.Add(new Call(i.Operand, i.Pos, loops.Any(l => i.Pos >= l.from && i.Pos <= l.to), ldftn, guarded));
        }
        return result;
    }

    /// <summary>
    /// IL ranges [from, to) that run only while a cache is empty. A forward conditional branch whose condition reads a
    /// field (<c>_x == null</c>, <c>!_bound</c>, <c>type == _lastType</c>), a property (<c>Prop == null</c>) or a dictionary
    /// lookup (TryGetValue / ContainsKey) guards:
    /// (a) the skipped block, when it stores that field (directly or through a plugin method that stores it), sets that
    ///     property, or writes a dictionary (indexer set, Add, TryAdd, GetOrAdd, AddOrUpdate): <c>if (_x == null) _x = ...</c>,
    ///     <c>_x ??= ...</c>, <c>if (!d.TryGetValue(k, out v)) d[k] = v = ...</c>;
    /// (b) the rest of the method, when the skipped block returns or throws and the rest fills the cache:
    ///     <c>if (_x != null) return _x; ... _x = ...</c>, <c>if (d.TryGetValue(k, out v)) return v; ... d[k] = ...</c>.
    /// Compiler lambda caches (<c>&lt;&gt;9__</c> fields) are not guards. Works on Debug and Release IL (the Debug
    /// condition goes through a stloc/ldloc pair).
    /// </summary>
    private static List<(int from, int to)> GuardedRegions(MetadataReader md, List<Insn> ins, Dictionary<MethodDefinitionHandle, HashSet<int>> stores)
    {
        var regions = new List<(int from, int to)>();
        Dictionary<int, int>? index = null;
        for (var k = 0; k < ins.Count; k++)
        {
            var b = ins[k];
            if (!IsConditional(b.Op) || b.Operand <= b.Pos) continue;
            if (Condition(md, ins, k) is not { } cond) continue;
            index ??= ins.Select((x, n) => (x.Pos, n)).ToDictionary(p => p.Pos, p => p.n);
            if (!index.TryGetValue(b.Operand, out var t)) continue;
            if (Fills(md, ins, k + 1, t, cond, stores)) regions.Add((ins[k + 1].Pos, b.Operand));
            else if (t > k + 1 && Exits(ins, index, ins[t - 1]) && Fills(md, ins, t, ins.Count, cond, stores)) regions.Add((b.Operand, int.MaxValue));
        }
        return regions;
    }

    /// <summary>brfalse/brtrue/beq/bne.un, short and long forms.</summary>
    private static bool IsConditional(short op) => op is 0x2C or 0x2D or 0x2E or 0x33 or 0x39 or 0x3A or 0x3B or 0x40;

    /// <summary>What a guard tests: fields (tokens), properties (names, matched against set_&lt;name&gt;) and whether it
    /// looks a key up in a dictionary.</summary>
    private sealed record Guard(HashSet<int> Fields, HashSet<string> Props, bool Dict);

    /// <summary>
    /// What the branch at <paramref name="k"/> tests, walking back over the condition's own instructions (up to 24):
    /// field loads, property getters, TryGetValue / ContainsKey, comparisons, locals, constants and the other branches of
    /// a compound condition (<c>a &amp;&amp; (b || c)</c>, which Debug IL merges through ldc/stloc/ldloc). It stops at
    /// the previous statement (a store, a return, any other call); null when nothing guard-like was read.
    /// </summary>
    private static Guard? Condition(MetadataReader md, List<Insn> ins, int k)
    {
        var g = new Guard([], [], false);
        var dict = false;
        for (var j = k - 1; j >= 0 && j >= k - 24; j--)
        {
            var i = ins[j];
            if (i.IsBranch) continue;   // the other parts of a compound condition
            switch (i.Op)
            {
                case OpLdfld or OpLdsfld:
                    if (!FieldName(md, i.Operand).StartsWith("<>", StringComparison.Ordinal)) g.Fields.Add(i.Operand);   // not a compiler lambda cache
                    continue;
                case OpCall or OpCallvirt:
                    var name = MemberName(md, i.Operand);
                    if (name is "TryGetValue" or "ContainsKey") { dict = true; continue; }
                    if (name is "Equals" or "ReferenceEquals" || name?.StartsWith("op_", StringComparison.Ordinal) == true) continue;   // ==, <, >=, implicit...

                    if (name != null && name.StartsWith("get_", StringComparison.Ordinal) && name != "get_Item") { g.Props.Add(name[4..]); continue; }
                    j = -1; break;
                // nop, ldarg.0-3, ldloc.0-3, stloc.0-3, ldarg.s, ldloc.s, ldloca.s, stloc.s, ldnull, ldc.i4.*, dup, isinst,
                // ceq/cgt/cgt.un/clt/clt.un, ldarg, ldloc, stloc (long forms).
                case 0x00 or >= 0x02 and <= 0x0E or 0x11 or 0x12 or 0x13 or 0x14 or >= 0x15 and <= 0x20 or OpDup or 0x75
                    or unchecked((short)0xFE01) or unchecked((short)0xFE02) or unchecked((short)0xFE03) or unchecked((short)0xFE04) or unchecked((short)0xFE05)
                    or unchecked((short)0xFE09) or unchecked((short)0xFE0C) or unchecked((short)0xFE0E):
                    continue;
                default:
                    j = -1; break;
            }
        }
        g = g with { Dict = dict };
        return g.Fields.Count > 0 || g.Props.Count > 0 || g.Dict ? g : null;
    }

    /// <summary>Whether ins[from..to) fills the cache <paramref name="g"/> tests: stores one of its fields (directly or
    /// through a plugin method that stores it), sets one of its properties, or writes a dictionary it looked up.</summary>
    private static bool Fills(MetadataReader md, List<Insn> ins, int from, int to, Guard g, Dictionary<MethodDefinitionHandle, HashSet<int>> stores)
    {
        for (var j = from; j < to && j < ins.Count; j++)
        {
            var i = ins[j];
            if (i.Op is OpStfld or OpStsfld && g.Fields.Contains(i.Operand)) return true;
            if (i.Op is not (OpCall or OpCallvirt)) continue;
            if (g.Fields.Count > 0 && MetadataTokens.EntityHandle(i.Operand) is { Kind: HandleKind.MethodDefinition } h
                && stores.TryGetValue((MethodDefinitionHandle)h, out var s) && s.Overlaps(g.Fields)) return true;
            if (MemberName(md, i.Operand) is not { } name) continue;
            if (g.Props.Count > 0 && name.StartsWith("set_", StringComparison.Ordinal) && g.Props.Contains(name[4..])) return true;
            if (g.Dict && name is "set_Item" or "Add" or "TryAdd" or "GetOrAdd" or "AddOrUpdate") return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="last"/> (the instruction before a branch target) leaves the method: ret, throw, or
    /// br/leave to a return (Debug IL returns through a shared <c>ldloc; ret</c>).</summary>
    private static bool Exits(List<Insn> ins, Dictionary<int, int> index, Insn last)
    {
        if (last.Op is OpRet or OpThrow || last.Op == unchecked((short)0xFE1A)) return true;   // rethrow
        if (last.Op is not (0x2B or 0x38 or 0xDD or 0xDE) || !index.TryGetValue(last.Operand, out var t)) return false;   // br.s, br, leave, leave.s
        if (ins[t].Op == OpRet) return true;
        return t + 1 < ins.Count && ins[t].Op is >= 0x06 and <= 0x09 or 0x11 or unchecked((short)0xFE0C) && ins[t + 1].Op == OpRet;
    }

    /// <summary>Whether the delegate made from the ldftn at <paramref name="k"/> goes to a once-per-key factory.</summary>
    private static bool FactoryLambda(MetadataReader md, List<Insn> ins, int k)
    {
        if (k + 1 >= ins.Count || ins[k + 1].Op != OpNewobj) return false;
        for (var j = k + 2; j < ins.Count && j <= k + 5; j++)
        {
            var i = ins[j];
            if (i.Op is OpDup or OpStsfld) continue;   // the compiler's static lambda cache: dup; stsfld <>9__
            if (i.Op is not (OpCall or OpCallvirt or OpNewobj)) return false;
            return MemberName(md, i.Operand) is "GetOrAdd" or "AddOrUpdate" or "EnsureInitialized"
                || i.Op == OpNewobj && DeclaringTypeName(md, i.Operand) == "Lazy`1";
        }
        return false;
    }

    /// <summary>Whether the delegate made from the ldftn at <paramref name="k"/> is kept (stored to a field or local), not
    /// handed straight to a call. The compiler's own lambda cache field doesn't count.</summary>
    private static bool DelegateStored(MetadataReader md, List<Insn> ins, int k)
    {
        if (k + 1 >= ins.Count || ins[k + 1].Op != OpNewobj) return true;
        var j = k + 2;
        if (j + 1 < ins.Count && ins[j].Op == OpDup && ins[j + 1].Op == OpStsfld && FieldName(md, ins[j + 1].Operand).StartsWith("<>", StringComparison.Ordinal)) j += 2;
        if (j >= ins.Count) return true;
        return ins[j].Op is OpDup or OpStfld or OpStsfld or >= 0x0A and <= 0x0D or 0x13 or unchecked((short)0xFE0E);
    }
}
