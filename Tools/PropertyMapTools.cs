using System.ComponentModel;
using System.Text;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hud;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// hud_property_map: which game memory each HUD property reads, from its getter's IL (offline). Forward ("what does
/// Life.CurHP read?") and reverse ("which HUD properties read ServerStashTabOffsets +61?"). On PoE2 it names the
/// obfuscated offset fields by the API property that reads them.
/// </summary>
[McpServerToolType]
public static class PropertyMapTools
{
    [McpServerTool(Name = "hud_property_map", Title = "Which memory each HUD property reads", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Map HUD properties to the game memory they read, by reading their getters' IL from the HUD DLLs (offline, " +
                 "no game needed): the offsets struct, the field path and the byte offset (summing [FieldOffset] through " +
                 "nested structs), or a direct Read<T>(Address + n). Forward: type (+ property). Reverse: struct (+ offset) - " +
                 "'which HUD properties read this?'. On PoE2 it explains obfuscated tNNNN.fNNNN fields by the property using " +
                 "them - except protected builds whose getters are IL stubs (ExileCore2: the real IL is only given to the JIT), reported as such. Properties that read nothing directly carry 'why' (what the getter does instead). trace=true prints a getter's IL ops.")]
    public static CallToolResult HudPropertyMap(BridgeRegistry bridges,
        [Description("HUD type, simple or full name, e.g. Life, ServerStashTab")] string? type = null,
        [Description("Property name (with type)")] string? property = null,
        [Description("Reverse: offsets struct (simple or full name, e.g. ServerStashTabOffsets, t44615)")] string? @struct = null,
        [Description("Reverse: byte offset within the struct (with struct): properties reading it")] int? offset = null,
        [Description("Include properties that read nothing directly (with their 'why')")] bool includeUnmapped = false,
        [Description("With type + property: print the getter's IL ops and what each resolved to (diagnose why a property doesn't map)")] bool trace = false,
        [Description("Max rows (1-500, default 100)")] int max = 100,
        [Description("'poe1' or 'poe2'; omit for every HUD installed")] string? game = null)
    {
        if (type == null && @struct == null) throw new McpException("Pass type (forward: what a HUD type's properties read) or struct (reverse: which properties read it).");
        max = Math.Clamp(max, 1, 500);
        if (trace)
        {
            if (type == null || property == null) throw new McpException("trace needs type and property.");
            var lines = new List<string>();
            foreach (var hud in HudDevTools.Installs(bridges, game))
            {
                try { var (_, tr) = PropertyMap.TraceOne(HudTypes.For(hud), type, property); lines.Add($"== {hud.Game}"); lines.AddRange(tr); }
                catch (Exception ex) { lines.Add($"== {hud.Game}: {ex.Message}"); }
            }
            return new CallToolResult { Content = [new TextContentBlock { Text = string.Join(Environment.NewLine, lines) }] };
        }
        var rows = new JArray();
        var sb = new StringBuilder();
        int total = 0, unmapped = 0;
        foreach (var hud in HudDevTools.Installs(bridges, game))
        {
            var map = PropertyMap.For(HudTypes.For(hud));
            IEnumerable<PropertyMap.Entry> hits = map;
            if (type != null) hits = hits.Where(e => NameMatches(e.Type, type));
            if (property != null) hits = hits.Where(e => e.Property.Equals(property, StringComparison.OrdinalIgnoreCase));
            if (@struct != null) hits = hits.Where(e => e.Struct != null && NameMatches(e.Struct, @struct));
            if (offset != null) hits = hits.Where(e => e.Offset == offset);
            var list = hits.ToList();
            unmapped += list.Count(e => e.Via == "none");
            if (!includeUnmapped) list = list.Where(e => e.Via != "none").ToList();
            total += list.Count;
            if (list.Count > 0) sb.AppendLine($"{hud.Game}:");
            foreach (var e in list.Take(max - rows.Count))
            {
                rows.Add(new JObject
                {
                    ["game"] = hud.Game, ["type"] = e.Type, ["property"] = e.Property, ["struct"] = e.Struct, ["field"] = e.FieldPath,
                    ["offset"] = e.Offset, ["hex"] = e.Offset is { } o ? $"0x{o:X}" : null, ["valueType"] = e.ValueType, ["via"] = e.Via, ["why"] = e.Why,
                });
                sb.AppendLine(e.Via == "none"
                    ? $"  {e.Type}.{e.Property}: - ({e.Why})"
                    : e.Via == "read-at"
                        ? $"  {e.Type}.{e.Property} -> Read<{e.ValueType}>(Address + 0x{e.Offset:X})"
                        : $"  {e.Type}.{e.Property} -> {Short(e.Struct)}.{e.FieldPath} @ +{e.Offset} (0x{e.Offset:X}) {e.ValueType}");
            }
        }
        if (total == 0) sb.AppendLine(type != null
            ? $"No mapped properties for '{type}{(property != null ? "." + property : "")}'" + (unmapped > 0 ? $" ({unmapped} read nothing directly: includeUnmapped=true shows why)" : " (type not found in the HUD's PoEMemory namespaces?)")
            : $"No HUD property reads {@struct}{(offset != null ? $" +{offset}" : "")}.");
        if (total > rows.Count) sb.AppendLine($"(showing {rows.Count} of {total}; raise max or narrow)");
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(new JObject { ["total"] = total, ["rows"] = rows }.ToString(Newtonsoft.Json.Formatting.None)),
        };
    }

    private static bool NameMatches(string full, string q) =>
        full.Equals(q, StringComparison.OrdinalIgnoreCase) || full.EndsWith("." + q, StringComparison.OrdinalIgnoreCase) || Short(full).Equals(q, StringComparison.OrdinalIgnoreCase);

    private static string Short(string? full) => full == null ? "?" : full[(full.LastIndexOf('.') + 1)..];
}
