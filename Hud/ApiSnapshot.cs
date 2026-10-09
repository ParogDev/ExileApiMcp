using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Hud;

/// <summary>
/// Snapshots of a HUD build's API (public types and members of ExileCore*/GameOffsets*, every offsets struct field with
/// its [FieldOffset], and the property map), and diffs between builds with the impact on our own code - so a HUD update
/// that renames a member or moves an offset is reported the day it lands, naming what it breaks, instead of surfacing as
/// a silent failure later. Stored under %LOCALAPPDATA%\ExileApiMcp\api-snapshots\&lt;game&gt;\&lt;build&gt;.json.
/// </summary>
public static class ApiSnapshot
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExileApiMcp", "api-snapshots");

    /// <summary>Build id: hash of the HUD's API DLLs (ExileCore*, GameOffsets*).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string stamp, string id)> BuildIds = new(StringComparer.OrdinalIgnoreCase);

    public static string BuildId(HudInstall hud)
    {
        // Hashing the DLLs costs a few ms; only redo it when their timestamps change (bridge_status calls this).
        var stamp = string.Join("|", Directory.GetFiles(hud.Root, "ExileCore*.dll").Concat(Directory.GetFiles(hud.Root, "GameOffsets*.dll"))
            .Select(f => File.GetLastWriteTimeUtc(f).Ticks + ":" + new FileInfo(f).Length));
        if (BuildIds.TryGetValue(hud.Root, out var c) && c.stamp == stamp) return c.id;
        var id = HashBuild(hud);
        BuildIds[hud.Root] = (stamp, id);
        return id;
    }

    private static string HashBuild(HudInstall hud)
    {
        using var sha = SHA256.Create();
        foreach (var f in Directory.GetFiles(hud.Root, "ExileCore*.dll").Concat(Directory.GetFiles(hud.Root, "GameOffsets*.dll")).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(f);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!)[..12].ToLowerInvariant();
    }

    /// <summary>Take (or reuse) the snapshot of the installed build. Returns (build id, path, created now).</summary>
    public static (string build, string path, bool created) Ensure(HudInstall hud)
    {
        var build = BuildId(hud);
        var dir = Path.Combine(Dir, hud.Game);
        var path = Path.Combine(dir, build + ".json");
        if (File.Exists(path)) return (build, path, false);
        Directory.CreateDirectory(dir);
        var snap = Build(hud, build);
        File.WriteAllText(path, snap.ToString(Formatting.None));
        return (build, path, true);
    }

    public static List<(string build, DateTime at, string path)> List(string game)
    {
        var dir = Path.Combine(Dir, game);
        if (!Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir).GetFiles("*.json")
            .Select(f => (Path.GetFileNameWithoutExtension(f.Name), f.CreationTimeUtc, f.FullName))
            .OrderBy(x => x.CreationTimeUtc).ToList();
    }

    private static JObject Build(HudInstall hud, string build)
    {
        var types = HudTypes.For(hud);
        var typesObj = new JObject();
        var structs = new JObject();
        foreach (var t in types.AllTypes())
        {
            string? full;
            try { full = t.FullName; if (full == null || !t.IsPublic && !t.IsNestedPublic || t.Name.StartsWith('<')) continue; } catch { continue; }
            var asm = t.Assembly.GetName().Name ?? "";
            if (!asm.StartsWith("ExileCore", StringComparison.Ordinal) && !asm.StartsWith("GameOffsets", StringComparison.Ordinal)) continue;
            var members = new JArray();
            try
            {
                foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.Name.StartsWith('<') || m is MethodInfo { IsSpecialName: true }) continue;
                    members.Add(Describe(m));
                }
            }
            catch { }
            typesObj[full] = new JObject { ["kind"] = HudTypes.Kind(t), ["members"] = new JArray(members.Select(x => x.ToString()).Distinct().OrderBy(x => x)) };
            // Offsets structs: every field (public or not) with its offset and type.
            try
            {
                var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (t.IsValueType && fields.Any(f => HudTypes.FieldOffset(f) != null))
                {
                    var fo = new JObject();
                    foreach (var f in fields)
                        if (HudTypes.FieldOffset(f) is { } off) fo[f.Name] = new JObject { ["off"] = off, ["type"] = SafeName(() => HudTypes.Name(f.FieldType)) };
                    structs[full] = fo;
                }
            }
            catch { }
        }
        var props = new JArray(PropertyMap.For(types).Where(e => e.Via != "none")
            .Select(e => $"{e.Type}.{e.Property} -> {e.Struct}.{e.FieldPath} @{e.Offset} {e.ValueType}").Distinct().OrderBy(x => x));
        return new JObject
        {
            ["game"] = hud.Game, ["build"] = build, ["takenAt"] = DateTime.UtcNow.ToString("O"),
            ["types"] = typesObj, ["structs"] = structs, ["properties"] = props,
        };
    }

    private static string SafeName(Func<string> f) { try { return f(); } catch { return "?"; } }

    private static string Describe(MemberInfo m)
    {
        try
        {
            return m switch
            {
                PropertyInfo p => $"P {p.Name}: {HudTypes.Name(p.PropertyType)}",
                FieldInfo f => $"F {f.Name}: {HudTypes.Name(f.FieldType)}",
                MethodBase mb => "M " + HudTypes.Signature(mb),
                EventInfo e => $"E {e.Name}",
                Type nt => $"T {nt.Name}",
                _ => m.Name,
            };
        }
        catch { return m.Name; }
    }

    // ── Diff ─────────────────────────────────────────────────────────

    public sealed record Change(string Kind, string Type, string Detail, string? Name, bool Breaking);

    public static List<Change> Diff(JObject a, JObject b)
    {
        // Across games, compare like with like: ExileCore2.X vs ExileCore.X (a PoE1 -> PoE2 diff then shows real API differences).
        if (a["game"]?.ToString() != b["game"]?.ToString()) { a = Normalize(a); b = Normalize(b); }
        var changes = new List<Change>();
        var ta = (JObject)a["types"]!; var tb = (JObject)b["types"]!;
        foreach (var p in ta.Properties())
        {
            if (tb[p.Name] == null) { changes.Add(new("type removed", p.Name, p.Name, Short(p.Name), true)); continue; }
            var ma = p.Value["members"]!.Select(x => x.ToString()).ToHashSet();
            var mb = tb[p.Name]!["members"]!.Select(x => x.ToString()).ToHashSet();
            var removed = ma.Except(mb).ToList();
            var added = mb.Except(ma).ToList();
            foreach (var r in removed)
            {
                // Renamed: an added member of the same kind with the same signature apart from the name.
                // Only an unambiguous match is a rename: enum values all share one shape, so they never pair.
                var shape = Shape(r);
                var candidates = added.Where(x => Shape(x) == shape && x[0] == r[0]).ToList();
                var renamed = candidates.Count == 1 && removed.Count(x => Shape(x) == shape && x[0] == r[0]) == 1 ? candidates[0] : null;
                if (renamed != null) { changes.Add(new("member renamed", p.Name, $"{r}  ->  {renamed}", MemberName(r), true)); added.Remove(renamed); }
                else
                {
                    var sameName = added.FirstOrDefault(x => MemberName(x) == MemberName(r) && x[0] == r[0]);
                    if (sameName != null) { changes.Add(new("signature changed", p.Name, $"{r}  ->  {sameName}", MemberName(r), true)); added.Remove(sameName); }
                    else changes.Add(new("member removed", p.Name, r, MemberName(r), true));
                }
            }
            foreach (var x in added) changes.Add(new("member added", p.Name, x, MemberName(x), false));
        }
        foreach (var p in tb.Properties().Where(p => ta[p.Name] == null)) changes.Add(new("type added", p.Name, p.Name, Short(p.Name), false));

        var sa = (JObject)a["structs"]!; var sb = (JObject)b["structs"]!;
        foreach (var s in sa.Properties())
        {
            if (sb[s.Name] is not JObject fb) continue;
            foreach (var f in ((JObject)s.Value).Properties())
            {
                if (fb[f.Name] is not JObject nf) { changes.Add(new("offset field removed", s.Name, $"{f.Name} @+{f.Value["off"]}", f.Name, true)); continue; }
                if (nf["off"]!.Value<int>() != f.Value["off"]!.Value<int>())
                    changes.Add(new("offset moved", s.Name, $"{f.Name}: +{f.Value["off"]} -> +{nf["off"]}", f.Name, true));
                if (nf["type"]?.ToString() != f.Value["type"]?.ToString())
                    changes.Add(new("offset type changed", s.Name, $"{f.Name}: {f.Value["type"]} -> {nf["type"]}", f.Name, true));
            }
        }
        var pa = a["properties"]!.Select(x => x.ToString()).ToHashSet();
        var pb = b["properties"]!.Select(x => x.ToString()).ToHashSet();
        var byPropA = pa.GroupBy(x => x[..x.IndexOf(" -> ", StringComparison.Ordinal)]).ToDictionary(g => g.Key, g => g.ToHashSet());
        var byPropB = pb.GroupBy(x => x[..x.IndexOf(" -> ", StringComparison.Ordinal)]).ToDictionary(g => g.Key, g => g.ToHashSet());
        foreach (var (prop, readsA) in byPropA)
            if (byPropB.TryGetValue(prop, out var readsB) && !readsA.SetEquals(readsB))
                changes.Add(new("property reads changed", prop[..prop.LastIndexOf('.')], $"{string.Join("; ", readsA.Except(readsB))}  ->  {string.Join("; ", readsB.Except(readsA))}", prop[(prop.LastIndexOf('.') + 1)..], true));
        return changes;
    }

    private static JObject Normalize(JObject s) =>
        JObject.Parse(s.ToString(Formatting.None).Replace("ExileCore2.", "ExileCore.").Replace("GameOffsets2.", "GameOffsets."));

    private static string Short(string full) => full[(full.LastIndexOfAny(['.', '+']) + 1)..];
    private static string MemberName(string desc) { var m = Regex.Match(desc, @"^[PFMET] ([^:(<\s]+)"); return m.Success ? m.Groups[1].Value : desc; }
    private static string Shape(string desc) { var n = MemberName(desc); var i = desc.IndexOf(n, StringComparison.Ordinal); return i < 0 ? desc : desc[..i] + "#" + desc[(i + n.Length)..]; }

    // ── Impact on our code ───────────────────────────────────────────

    /// <summary>Where our own code uses a changed name: plugin sources (the HUD's Plugins\Source, which links our repos),
    /// and the MCP's knowledge (findings, flows, experiments, packs, embedded in this server).</summary>
    public static List<(string name, string where)> Impact(HudInstall hud, IEnumerable<(string type, string name)> changed)
    {
        // A member name alone ("Flags") matches everywhere: count it only in files that also mention its type.
        var byType = changed.Where(c => c.name.Length >= 3).GroupBy(c => c.type)
            .ToDictionary(g => g.Key, g => g.Select(c => c.name).Distinct().ToList());
        var hits = new List<(string, string)>();
        if (byType.Count == 0) return hits;
        var typeRx = byType.Keys.ToDictionary(t => t, t => new Regex(@"\b" + Regex.Escape(t) + @"\b", RegexOptions.Compiled));
        var memberRx = byType.ToDictionary(kv => kv.Key, kv => new Regex(@"\b(" + string.Join("|", kv.Value.Select(Regex.Escape)) + @")\b", RegexOptions.Compiled));
        void Scan(string label, string text)
        {
            string[]? lines = null;
            foreach (var (type, trx) in typeRx)
            {
                if (!trx.IsMatch(text)) continue;
                lines ??= text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    foreach (Match m in memberRx[type].Matches(lines[i]))
                        if (hits.Count < 400) hits.Add(($"{type}.{m.Value}", $"{label}:{i + 1}"));
            }
        }
        if (Directory.Exists(hud.SourcePluginsDir))
            foreach (var f in Directory.EnumerateFiles(hud.SourcePluginsDir, "*.cs", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                try { Scan(Path.GetRelativePath(hud.SourcePluginsDir, f), File.ReadAllText(f)); } catch { }
            }
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(r => r.StartsWith("knowledge/", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(res);
            if (s != null) Scan("MCP " + res, new StreamReader(s).ReadToEnd());
        }
        return hits;
    }
}
