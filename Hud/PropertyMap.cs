using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

namespace ExileApiMcp.Hud;

/// <summary>
/// Which game memory each HUD property reads, from the IL of its getter (offline, MetadataLoadContext):
///   cached struct  - getter reads CachedValue/FrameCache&lt;XOffsets&gt;.Value, then a chain of fields: offsets add up
///                    through [FieldOffset] (Life.CurHP -> LifeComponentOffsets.Health(+X).Current(+Y))
///   read-at        - getter calls M.Read&lt;T&gt;(Address + const): the constant and T
///   field-struct   - getter reads a struct field of the object (XOffsets _o) then its members
/// Helper calls on the same type are followed (depth 3). On PoE2 this names what the obfuscated tNNNN.fNNNN fields are.
/// A property that maps to nothing carries a 'why' (fail at the broken link): what its getter does instead.
/// </summary>
public static class PropertyMap
{
    public sealed record Entry(string Type, string Property, string? Struct, string? FieldPath, int? Offset, string? ValueType,
        string Via, string? Why);

    private static readonly ConcurrentDictionary<HudTypes, List<Entry>> Cache = new();

    /// <summary>When set, Analyze appends every IL op and what it resolved to (hud_property_map trace=true).</summary>
    [ThreadStatic] internal static List<string>? Trace;

    /// <summary>Analyze one property with a trace of its getter IL (maintenance: shows where the map loses the thread).</summary>
    public static (List<Entry> entries, List<string> trace) TraceOne(HudTypes hud, string type, string property)
    {
        var t = hud.AllTypes().FirstOrDefault(x => { try { return x.Name == type || x.FullName == type; } catch { return false; } })
                ?? throw new InvalidOperationException($"type {type} not found");
        var p = t.GetProperty(property, BindingFlags.Public | BindingFlags.Instance) ?? throw new InvalidOperationException($"property {type}.{property} not found");
        var found = new List<Entry>();
        Trace = new List<string>();
        try
        {
            var why = Analyze(hud, p.GetGetMethod()!, t, p.Name, found, 0, new Dictionary<MethodBase, (Type, Type, string, int)?>(), out var rc);
            if (rc is { } c) found.Add(ChainEntry(t, p.Name, c));
            Trace.Add($"result: {found.Count} entries; why={why}");
            return (found, Trace);
        }
        finally { Trace = null; }
    }

    // One- and two-byte opcode tables from the runtime's OpCodes (operand sizes come from OperandType).
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    public static List<Entry> For(HudTypes hud) => Cache.GetOrAdd(hud, Build);

    private static List<Entry> Build(HudTypes hud)
    {
        var list = new List<Entry>();
        foreach (var t in hud.AllTypes())
        {
            string? ns;
            try { ns = t.Namespace; } catch { continue; }
            if (ns == null || !ns.Contains(".PoEMemory", StringComparison.Ordinal) || t.IsValueType || t.IsInterface) continue;
            PropertyInfo[] props;
            try { props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly); } catch { continue; }
            foreach (var p in props)
            {
                MethodInfo? get;
                try { get = p.GetGetMethod(); } catch { continue; }
                if (get == null) continue;
                var found = new List<Entry>();
                string? why;
                try { why = Analyze(hud, get, t, p.Name, found, 0, new Dictionary<MethodBase, (Type, Type, string, int)?>(), out var retChain) ?? null; if (retChain is { } rc) found.Add(ChainEntry(t, p.Name, rc)); }
                catch (Exception ex) { why = $"IL analysis failed: {ex.GetType().Name}: {ex.Message}"; }
                if (found.Count > 0) list.AddRange(found.DistinctBy(e => (e.Struct, e.FieldPath, e.Offset)));
                else list.Add(new Entry(HudTypes.Name(t), p.Name, null, null, null, Safe(() => HudTypes.Name(p.PropertyType)), "none", why ?? "getter reads no game memory directly"));
            }
        }
        return list;
    }

    private static string? Safe(Func<string> f) { try { return f(); } catch { return null; } }
    private static Type? Safe2(Func<Type?> f) { try { return f(); } catch { return null; } }
    private static bool SafeIsMethodParam(Type gp) { try { return gp.DeclaringMethod != null; } catch { return false; } }

    /// <summary>A struct with explicit layout ([FieldOffset] on its fields): a GameOffsets struct.</summary>
    private static bool IsOffsetsStruct(Type? t)
    {
        try
        {
            if (t == null || !t.IsValueType || t.IsPrimitive || t.IsEnum) return false;
            return t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(f => HudTypes.FieldOffset(f) != null);
        }
        catch { return false; }
    }

    /// <summary>Walk one method's IL; adds entries; returns a reason when nothing was found.</summary>
    /// <summary>The struct (or field of one) a method returns, as an entry: e.g. Health =&gt; _life.Value.Health.</summary>
    private static Entry ChainEntry(Type owner, string property, (Type type, Type root, string path, int offset) c) =>
        new(HudTypes.Name(owner), property, HudTypes.Name(c.root, true), c.path.Length == 0 ? "(whole struct)" : c.path.TrimStart('.'), c.offset, HudTypes.Name(c.type), "cached-struct", null);

    private static string? Analyze(HudTypes hud, MethodBase m, Type owner, string property, List<Entry> found, int depth, Dictionary<MethodBase, (Type, Type, string, int)?> seen,
        out (Type type, Type root, string path, int offset)? retChain)
    {
        retChain = null;
        // A helper already analysed for this property: reuse what it returns (its entries are already recorded).
        if (seen.TryGetValue(m, out var memo)) { retChain = memo; return null; }
        seen[m] = null;
        var body = m.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il == null || il.Length == 0) return "getter has no IL body (abstract/extern)";
        // Protected builds (ExileCore2) ship stub bodies: a few bytes, no member tokens, branches into their own operands.
        // The real IL is only handed to the JIT, so nothing here can map them - say so instead of "reads nothing".
        if (IsObfuscatedStub(il))
            return "IL is an obfuscated stub (protected build: the real body is supplied at JIT time), so it can't be mapped statically";
        var tokens = IlTokens.For(m.Module, hud);

        // Chain = (struct type we're inside, start struct, path so far, offset so far)
        (Type type, Type root, string path, int offset)? chain = null;
        var locals = new Dictionary<int, (Type, Type, string, int)>();
        long? lastConst = null;
        bool sawAddress = false;
        bool staticInAddress = false;   // an ldsfld between get_Address and Read: the offset is partly a static (not a constant)
        var notes = new List<string>();

        void Emit(string via, Type root, string path, int offset, Type? valueType)
        {
            found.Add(new Entry(HudTypes.Name(owner), property, HudTypes.Name(root, true), path, offset, valueType == null ? null : HudTypes.Name(valueType), via, null));
        }

        int pos = 0;
        while (pos < il.Length)
        {
            short code = il[pos++];
            if (code == 0xFE) code = (short)(0xFE00 | il[pos++]);
            if (!OpCodesByValue.TryGetValue(code, out var op)) return $"unknown opcode 0x{code:X}";
            int operand = 0;
            long operand8 = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: operand = il[pos]; pos += 1; break;
                case OperandType.InlineVar: operand = BitConverter.ToUInt16(il, pos); pos += 2; break;
                case OperandType.InlineI8: operand8 = BitConverter.ToInt64(il, pos); pos += 8; break;
                case OperandType.InlineR: pos += 8; break;
                case OperandType.ShortInlineR: pos += 4; break;
                case OperandType.InlineSwitch: var n = BitConverter.ToInt32(il, pos); pos += 4 + 4 * n; break;
                default: operand = BitConverter.ToInt32(il, pos); pos += 4; break;
            }

            var name = op.Name ?? "";
            var traceAt = Trace?.Count;
            Trace?.Add($"{new string(' ', depth * 2)}{m.Name} IL_{pos:X4} {name} {(op.OperandType == OperandType.InlineNone ? "" : "0x" + operand.ToString("X"))}");
            // Constants (for Read<T>(Address + c)).
            if (name.StartsWith("ldc.i4", StringComparison.Ordinal))
                lastConst = op.OperandType == OperandType.InlineNone ? name switch { "ldc.i4.m1" => -1, _ => name.Length > 7 ? name[7] - '0' : 0 } : (long)operand;
            else if (name == "ldc.i8") lastConst = operand8;

            if (op.OperandType == OperandType.InlineField)
            {
                if (name == "ldsfld" && sawAddress) staticInAddress = true;
                FieldInfo? f;
                try { f = tokens.Field(operand); } catch (Exception ex) { f = null; Trace?.Add($"    ResolveField failed: {ex.GetType().Name}: {ex.Message}"); }
                if (f != null) Trace?.Add($"    field {Safe(() => HudTypes.Name(f.DeclaringType, true))}.{f.Name} : {Safe(() => HudTypes.Name(f.FieldType, true))} offset={HudTypes.FieldOffset(f)} offsetsStruct={IsOffsetsStruct(Safe2(() => f.FieldType))} chain={(chain == null ? "-" : HudTypes.Name(chain.Value.type))}");
                if (f == null) { chain = null; continue; }
                Type ft;
                try { ft = f.FieldType; } catch { chain = null; continue; }
                var off = HudTypes.FieldOffset(f);
                if (chain is { } c && SameType(f.DeclaringType, c.type) && off != null)
                {
                    var next = (ft, c.root, c.path + "." + f.Name, c.offset + off.Value);
                    if (IsOffsetsStruct(ft)) chain = next;
                    else { Emit(c.path.Length == 0 ? "cached-struct" : "cached-struct", c.root, next.Item3.TrimStart('.'), next.Item4, ft); chain = null; }
                }
                else if (IsOffsetsStruct(ft) && name.StartsWith("ldfld", StringComparison.Ordinal) && (SameType(f.DeclaringType, owner) || IsBase(f.DeclaringType!, owner)))
                    chain = (ft, ft, "", 0);   // a struct field of the object itself
                else chain = null;
                continue;
            }

            if (op.OperandType == OperandType.InlineMethod)
            {
                MethodBase? callee; Type[] calleeTypeArgs = [], calleeMethodArgs = [];
                try { (callee, calleeTypeArgs, calleeMethodArgs) = tokens.Method(operand); } catch (Exception ex) { callee = null; Trace?.Add($"    ResolveMethod failed: {ex.GetType().Name}: {ex.Message}"); }
                if (callee != null) Trace?.Add($"    call {Safe(() => HudTypes.Name(callee.DeclaringType, true))}.{callee.Name} returns {Safe(() => HudTypes.Name((callee as MethodInfo)?.ReturnType, true))} offsetsStruct={IsOffsetsStruct(Safe2(() => (callee as MethodInfo)?.ReturnType))}");
                if (callee == null) { chain = null; continue; }
                var cname = callee.Name;
                Type? ret = null;
                try { ret = (callee as MethodInfo)?.ReturnType; } catch { }
                // Members of generic instantiations: a return of the type's generic parameter is the instantiation's argument
                // (CachedValue<LifeComponentOffsets>.get_Value returns LifeComponentOffsets).
                if (ret is { IsGenericParameter: true } gp && calleeTypeArgs.Length > gp.GenericParameterPosition && !SafeIsMethodParam(gp))
                    ret = calleeTypeArgs[gp.GenericParameterPosition];
                if (cname == "get_Address") { sawAddress = true; staticInAddress = false; continue; }
                // CachedValue<T>.Value / FrameCache<T>.Value -> start a chain in T
                if (cname == "get_Value" && IsOffsetsStruct(ret)) { chain = (ret!, ret!, "", 0); continue; }
                // M.Read<T>(Address + const)
                if (cname == "Read" && calleeMethodArgs.Length == 1 && sawAddress && staticInAddress)
                {
                    notes.Add("Read<" + HudTypes.Name(calleeMethodArgs[0]) + ">(Address + a static offset field + const): offset not constant in IL");
                    sawAddress = false; staticInAddress = false; lastConst = null; chain = null;
                    continue;
                }
                if (cname == "Read" && calleeMethodArgs.Length == 1 && sawAddress && lastConst != null)
                {
                    var tArg = calleeMethodArgs[0];
                    found.Add(new Entry(HudTypes.Name(owner), property, null, null, (int)lastConst.Value, HudTypes.Name(tArg), "read-at", null));
                    lastConst = null; sawAddress = false; chain = null;
                    continue;
                }
                // A property getter on a struct we're inside (e.g. a computed member): note it.
                // Helper on the same type (or base): follow.
                if (depth < 3 && callee.DeclaringType != null && (SameType(callee.DeclaringType, owner) || IsBase(callee.DeclaringType, owner)) && callee is MethodInfo)
                {
                    var before = found.Count;
                    var why = Analyze(hud, callee, owner, property, found, depth + 1, seen, out var calleeChain);
                    if (calleeChain != null) { chain = calleeChain; continue; }   // helper returned a struct: keep reading its fields here
                    if (found.Count == before && why != null) notes.Add($"{cname}: {why}");
                }
                else if (chain != null && !(callee.DeclaringType != null && SameType(callee.DeclaringType, chain.Value.type)))
                    notes.Add($"calls {HudTypes.Name(callee.DeclaringType)}.{cname}");
                chain = null;
                continue;
            }

            if (name == "ret") { if (chain != null) retChain = chain; continue; }

            // Locals: keep a chain across stloc/ldloc.
            if (name.StartsWith("stloc", StringComparison.Ordinal) && chain != null) { locals[LocalIndex(name, operand)] = chain.Value; chain = null; continue; }
            if (name.StartsWith("ldloc", StringComparison.Ordinal)) { chain = locals.TryGetValue(LocalIndex(name, operand), out var l) ? l : null; continue; }
        }
        seen[m] = retChain;
        return found.Count > 0 || retChain != null ? null : notes.Count > 0 ? string.Join("; ", notes.Distinct().Take(4)) : null;
    }

    /// <summary>A protector stub: a short body that references no member at all and branches backwards (ExileCore2:
    /// ldarg.0; brtrue.s -3; ldc.i4.0; br.s -3; ... - tiny infinite loops), or into an operand. Real getters never do that.</summary>
    private static bool IsObfuscatedStub(byte[] il)
    {
        if (il.Length > 24) return false;
        var starts = new HashSet<int>();
        var branches = new List<(int at, int target)>();
        bool memberToken = false;
        int pos = 0;
        while (pos < il.Length)
        {
            starts.Add(pos);
            short code = il[pos++];
            if (code == 0xFE) { if (pos >= il.Length) return false; code = (short)(0xFE00 | il[pos++]); }
            if (!OpCodesByValue.TryGetValue(code, out var op)) return true;
            int size = op.OperandType switch
            {
                OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8, OperandType.InlineSwitch => -1, _ => 4,
            };
            if (size < 0 || pos + size > il.Length) return false;
            if (op.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType) memberToken = true;
            if (op.OperandType == OperandType.ShortInlineBrTarget) branches.Add((pos - 1, pos + 1 + (sbyte)il[pos]));
            pos += size;
        }
        return branches.Any(b => !starts.Contains(b.target) && b.target != il.Length)
               || (!memberToken && branches.Any(b => b.target <= b.at));
    }

    private static int LocalIndex(string opName, int operand)
    {
        // stloc.0..3 / ldloc.0..3 carry the index in the name; .s / plain carry it as operand
        var dot = opName.LastIndexOf('.');
        return dot > 0 && int.TryParse(opName[(dot + 1)..], out var i) ? i : operand;
    }

    private static bool SameType(Type? a, Type? b)
    {
        try { return a != null && b != null && (a == b || a.FullName == b.FullName && a.FullName != null); } catch { return false; }
    }

    private static bool IsBase(Type candidate, Type t)
    {
        try { for (var b = t.BaseType; b != null; b = b.BaseType) if (SameType(b, candidate)) return true; } catch { }
        return false;
    }
}
