using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ExileApiMcp.Hud;

/// <summary>
/// Type metadata of a HUD's assemblies, read from disk with MetadataLoadContext: nothing is loaded
/// for execution, and neither the game nor the HUD has to run. For PoE2 this is the API reference
/// (ExileCore2 ships without source). One context per HUD folder, rebuilt when its DLLs change.
/// </summary>
public sealed class HudTypes
{
    private static readonly ConcurrentDictionary<string, HudTypes> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly MetadataLoadContext _mlc;
    public IReadOnlyList<Assembly> Assemblies { get; }
    private readonly string _stamp;

    // Third-party and runtime DLLs in the HUD folder that plugin authors rarely need to inspect; they
    // still resolve as references. ImGui.NET, SharpDX and Newtonsoft stay searchable.
    private static readonly string[] SkipPrefixes =
    [
        "System.", "Microsoft.", "Google.", "Grpc.", "NAudio", "Serilog", "SixLabors", "MoreLinq", "JM.",
        "ProcessMemoryUtilities", "ClickableTransparentOverlay", "SharpGen", "Loader", "mscorlib", "netstandard",
    ];

    private HudTypes(HudInstall hud, string stamp)
    {
        _stamp = stamp;
        var hudDlls = Directory.GetFiles(hud.Root, "*.dll");
        var runtime = RuntimeDirs(hud.Game == "poe2" ? 8 : 10);
        // The HUD's own DLLs win name clashes (e.g. a newer Newtonsoft); runtime DLLs fill in the rest.
        var paths = hudDlls.Concat(runtime.SelectMany(d => Directory.GetFiles(d, "*.dll")))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
        _mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Private.CoreLib");

        var list = new List<Assembly>();
        foreach (var dll in hudDlls.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dll);
            if (SkipPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            try { list.Add(_mlc.LoadFromAssemblyPath(dll)); }
            catch (BadImageFormatException) { } // native DLL (cimgui etc.)
            catch (FileLoadException) { }
        }
        Assemblies = list;
    }

    public static HudTypes For(HudInstall hud)
    {
        var stamp = string.Join("|", Directory.GetFiles(hud.Root, "ExileCore*.dll")
            .Concat(Directory.GetFiles(hud.Root, "GameOffsets*.dll"))
            .Select(f => File.GetLastWriteTimeUtc(f).Ticks));
        return Cache.AddOrUpdate(hud.Root, _ => new HudTypes(hud, stamp),
            (_, existing) => existing._stamp == stamp ? existing : new HudTypes(hud, stamp));
    }

    /// <summary>Shared framework folders for a .NET major version (NETCore + WindowsDesktop), newest patch.</summary>
    private static IEnumerable<string> RuntimeDirs(int major)
    {
        var shared = Path.GetDirectoryName(Path.GetDirectoryName(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('\\')))!;
        foreach (var fx in new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" })
        {
            var dir = Path.Combine(shared, fx);
            if (!Directory.Exists(dir)) continue;
            var best = Directory.GetDirectories(dir)
                .Select(d => (d, v: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                .Where(x => x.v != null)
                .OrderByDescending(x => x.v!.Major == major).ThenByDescending(x => x.v)
                .FirstOrDefault();
            if (best.d != null) yield return best.d;
        }
    }

    public IEnumerable<Type> AllTypes() => Assemblies.SelectMany(SafeTypes);

    public static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); }
    }

    // ── Formatting ───────────────────────────────────────────────────

    /// <summary>C#-style name: List&lt;Entity&gt;, Dictionary&lt;String, Int32&gt;, Int32[], Vector2?</summary>
    public static string Name(Type? t, bool full = false)
    {
        if (t == null) return "?";
        try
        {
            if (t.IsByRef) return "ref " + Name(t.GetElementType(), full);
            if (t.IsArray) return Name(t.GetElementType(), full) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (t.IsPointer) return Name(t.GetElementType(), full) + "*";
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                if (def.FullName == "System.Nullable`1") return Name(t.GetGenericArguments()[0], full) + "?";
                var baseName = (full ? def.FullName ?? def.Name : def.Name);
                var tick = baseName.IndexOf('`');
                if (tick >= 0) baseName = baseName[..tick];
                return baseName + "<" + string.Join(", ", t.GetGenericArguments().Select(a => Name(a, full))) + ">";
            }
            return full ? (t.FullName ?? t.Name) : t.Name;
        }
        catch (FileNotFoundException)
        {
            return t.Name; // a reference the resolver couldn't find: the name is still useful
        }
    }

    public static string Kind(Type t)
    {
        try
        {
            if (t.IsEnum) return "enum";
            if (t.IsInterface) return "interface";
            if (t.IsValueType) return "struct";
            if (t.BaseType?.FullName == "System.MulticastDelegate") return "delegate";
            return t.IsAbstract && t.IsSealed ? "static class" : "class";
        }
        catch (FileNotFoundException) { return "type"; }
    }

    public static string Access(MethodBase? m) => m == null ? "" :
        m.IsPublic ? "public" : m.IsFamily ? "protected" : m.IsAssembly ? "internal" : m.IsFamilyOrAssembly ? "protected internal" : "private";

    public static string Access(FieldInfo f) =>
        f.IsPublic ? "public" : f.IsFamily ? "protected" : f.IsAssembly ? "internal" : f.IsFamilyOrAssembly ? "protected internal" : "private";

    public static string Signature(MethodBase m)
    {
        var sb = new StringBuilder();
        sb.Append(m.Name);
        if (m.IsGenericMethodDefinition) sb.Append('<').Append(string.Join(", ", m.GetGenericArguments().Select(a => a.Name))).Append('>');
        sb.Append('(');
        sb.Append(string.Join(", ", m.GetParameters().Select(p =>
        {
            var s = Name(p.ParameterType) + " " + p.Name;
            if (p.HasDefaultValue) s += " = " + (p.RawDefaultValue switch { null => "null", string str => $"\"{str}\"", bool b => b ? "true" : "false", var v => v.ToString() });
            return s;
        })));
        sb.Append(')');
        if (m is MethodInfo mi) sb.Append(" : ").Append(Name(mi.ReturnType));
        return sb.ToString();
    }

    /// <summary>[FieldOffset(n)] on an explicit-layout struct field (GameOffsets).</summary>
    public static int? FieldOffset(FieldInfo f)
    {
        try
        {
            var attr = f.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.InteropServices.FieldOffsetAttribute");
            return attr?.ConstructorArguments.FirstOrDefault().Value as int?;
        }
        catch (FileNotFoundException) { return null; }
    }
}
