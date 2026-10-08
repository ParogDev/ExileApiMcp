using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ExileApiMcp.Apps;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Mapping the live object model for plugin developers (bridge object.explore). The model gets a compact
/// indented outline - name: type = preview - which costs a fraction of the JSON; structuredContent carries
/// the full tree (paths, null-safe C#, kinds, counts) for the data explorer app and for clients that use it.
/// </summary>
[McpServerToolType]
public static class ExploreTools
{
    private const int MaxBridgeCalls = 40;
    private const int DeepChildLimit = 20;

    // No UI link: agents call this in loops, and a linked tool opens a panel per call in Desktop.
    // The explorer app calls it like any model-visible tool.
    [McpServerTool(Name = "explore_object", Title = "Explore the object model", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Map out live HUD data: the object at a path and its children, one line each - name: type = preview " +
                 "(structs as X=.. Y=.., objects with their Name and visible/hidden, collections with counts) - plus an " +
                 "entity's components. depth 2-3 expands nested objects. Child paths are parent.Name, parent[i] or " +
                 "parent[\"key\"]; the C# to read one in a plugin is the path with ?. (structuredContent has both per node, " +
                 "with enum dictionary keys typed). Start at 'GameController', then follow what you need; then use " +
                 "eval_path for full values, watch_object to see what changes, or hud_type for members' declarations.")]
    public static async Task<CallToolResult> ExploreObject(BridgeRegistry bridges,
        [Description("Walker path, e.g. GameController.Player, GameController.Player.GetComponent<Life>(), GameController.IngameState.IngameUi.Children[3]")]
        string path = "GameController",
        [Description("Levels to expand (1-3). Deeper levels show at most 20 items per collection.")] int depth = 1,
        [Description("First collection item to show (paging)")] int offset = 0,
        [Description("Collection items per page (1-200, default 50)")] int limit = 50,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        depth = Math.Clamp(depth, 1, 3);
        var calls = 0;
        var (bridge, root) = await ExploreAsync(bridges, game, path, null, offset, Math.Clamp(limit, 1, 200), ct);
        calls++;
        if (root["error"] != null) return ToolResults.Json(root);
        game = bridge.Game == "auto" ? game : bridge.Game;
        if (game != null) root["game"] = game;

        // Breadth-first expansion of nested objects/structs/collections, within a call budget. Back-references
        // (Owner, an ancestor's address) and memory plumbing are listed but not expanded: they repeat what is
        // already shown or say nothing about game data.
        var frontier = new List<(JObject node, int level, HashSet<string> ancestors)> { (root, 1, AddressesOf(root, [])) };
        var truncated = false;
        while (frontier.Count > 0)
        {
            var next = new List<(JObject, int, HashSet<string>)>();
            foreach (var (node, level, ancestors) in frontier)
            {
                if (level >= depth || node["children"] is not JArray kids) continue;
                foreach (var child in kids.OfType<JObject>())
                {
                    if (child["expandable"]?.Value<bool>() != true || child["path"]?.Value<string>() is not { } childPath) continue;
                    if (NotWorthExpanding(child, ancestors) is { } why) { child["notExpanded"] = why; continue; }
                    if (calls >= MaxBridgeCalls) { truncated = true; break; }
                    var (_, sub) = await ExploreAsync(bridges, game, childPath, child["csharp"]?.Value<string>(), 0, DeepChildLimit, ct);
                    calls++;
                    if (sub["error"] != null) { child["expandError"] = sub["message"] ?? sub["error"]; continue; }
                    if (sub["children"] is JArray subKids && Address(subKids) is { } addr && ancestors.Contains(addr))
                    { child["notExpanded"] = "back-reference"; continue; }
                    foreach (var key in new[] { "children", "components", "page", "skipped" })
                        if (sub[key] != null) child[key] = sub[key];
                    next.Add((child, level + 1, AddressesOf(child, ancestors)));
                }
            }
            frontier = next;
        }
        if (truncated) root["truncated"] = $"Stopped expanding after {MaxBridgeCalls} lookups; explore a narrower path or use depth 1.";

        var outline = Outline(root);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = outline }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(root.ToString(Formatting.None)),
        };
    }

    [McpServerTool(Name = "show_data_explorer", Title = "Open the data explorer", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpAppUi(ResourceUri = DataExplorerApp.ResourceUri)]
    [McpMeta("ui/resourceUri", DataExplorerApp.ResourceUri)]
    [Description("Open an interactive explorer of the live HUD object model at a path (clients that support MCP Apps): " +
                 "the user expands objects, lists and components, sees values update, and copies paths or plugin C#. " +
                 "Other clients get the same outline as explore_object.")]
    public static Task<CallToolResult> ShowDataExplorer(BridgeRegistry bridges,
        [Description("Path to open at (default GameController)")] string path = "GameController",
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
        => ExploreObject(bridges, path, 1, 0, 50, game, ct);

    [McpServerTool(Name = "find_in_object", Title = "Find where a value or member lives", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Search the live object graph under a path for members whose name matches, or whose value contains, " +
                 "what you're looking for - e.g. where the 356 shown in game lives (value=356), or every '*Resist*' member " +
                 "(name=resist). Breadth-first over explore_object results, skipping back-references and memory plumbing; " +
                 "collections are read up to 200 items. Returns each match's path, C#, type and current value.")]
    public static async Task<CallToolResult> FindInObject(BridgeRegistry bridges,
        [Description("Root path to search under (default GameController.Player)")] string path = "GameController.Player",
        [Description("Case-insensitive substring of member names to match")] string? name = null,
        // JsonElement: clients send 356 as a number as often as "356".
        [Description("Text or number the value must contain (numbers match whole: 356 won't match 3560)")] JsonElement? value = null,
        [Description("Levels below the root to search (1-5, default 3)")] int depth = 3,
        [Description("Maximum objects to open (10-300, default 120); each costs one bridge round trip")] int maxNodes = 120,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var text = value is { } v && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(text))
            throw new McpException("Pass name (a member name substring) and/or value (text the value contains).");
        depth = Math.Clamp(depth, 1, 5);
        maxNodes = Math.Clamp(maxNodes, 10, 300);
        var numeric = text != null && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
        var valueRx = string.IsNullOrWhiteSpace(text) ? null
            : new System.Text.RegularExpressions.Regex(numeric ? $@"(?<![\d.]){System.Text.RegularExpressions.Regex.Escape(text)}(?![\d.])" : System.Text.RegularExpressions.Regex.Escape(text),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var matches = new JArray();
        var queue = new Queue<(string path, string? csharp, int level, HashSet<string> ancestors)>();
        queue.Enqueue((path, null, 0, []));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int opened = 0;
        string? resolvedGame = game;
        while (queue.Count > 0 && opened < maxNodes && matches.Count < 100)
        {
            var (p, cs, level, ancestors) = queue.Dequeue();
            JObject node;
            try
            {
                // Whole collections up to 200 (dictionaries such as Stats are what name searches are for).
                var (bridge, result) = await ExploreAsync(bridges, resolvedGame, p, cs, 0, 200, ct);
                if (opened == 0 && bridge.Game != "auto") resolvedGame = bridge.Game;
                node = result;
            }
            catch (McpException) when (opened > 0) { continue; }
            opened++;
            if (node["error"] != null)
            {
                if (opened == 1) return ToolResults.Json(node);
                continue;
            }
            var kids = node["children"] as JArray ?? [];
            var here = AddressesOf(node, ancestors);
            if (opened > 1 && Address(kids) is { } addr && ancestors.Contains(addr)) continue; // cycle

            IEnumerable<JObject> candidates = kids.OfType<JObject>();
            if (node["components"] is JArray comps)
                candidates = candidates.Concat(comps.OfType<JObject>().Where(c => c["path"] != null));
            foreach (var c in candidates)
            {
                var childName = c["name"]?.ToString() ?? "";
                var preview = c["preview"]?.ToString() ?? "";
                var kind = c["kind"]?.ToString();
                bool nameHit = !string.IsNullOrWhiteSpace(name) && childName.Contains(name, StringComparison.OrdinalIgnoreCase);
                bool valueHit = valueRx != null && kind is not ("object" or "list" or "dictionary" or "component") && valueRx.IsMatch(preview);
                if ((string.IsNullOrWhiteSpace(name) || nameHit) && (valueRx == null || valueHit))
                    matches.Add(new JObject
                    {
                        ["path"] = c["path"], ["csharp"] = c["csharp"], ["type"] = c["type"] ?? "component",
                        ["value"] = kind == "component" ? null : preview,
                    });
                if (level + 1 < depth && c["expandable"]?.Value<bool>() == true && c["path"]?.Value<string>() is { } cp
                    && NotWorthExpanding(c, here) == null && seen.Add(cp))
                    queue.Enqueue((cp, c["csharp"]?.Value<string>(), level + 1, here));
            }
        }

        var o = new JObject
        {
            ["root"] = path, ["name"] = name, ["value"] = text, ["objectsOpened"] = opened, ["matches"] = matches,
        };
        if (queue.Count > 0) o["incomplete"] = $"Stopped after opening {opened} objects ({queue.Count} left): raise maxNodes, lower depth or search a narrower root.";
        if (matches.Count == 0) o["note"] = "No match. Values are compared against one-line previews (strings, numbers, struct fields).";
        return ToolResults.Json(o);
    }

    private static async Task<(BridgeClient bridge, JObject result)> ExploreAsync(BridgeRegistry bridges, string? game,
        string path, string? csharp, int offset, int limit, CancellationToken ct)
    {
        var p = new JObject { ["path"] = path, ["offset"] = offset, ["limit"] = limit };
        if (csharp != null) p["csharp"] = csharp;
        var (bridge, result) = await bridges.CallAsync(game, "object.explore", p, ct);
        // A bridge without object.explore treats the method as an unknown query and echoes {game, query, timestamp}.
        if (result is not JObject o || (o["path"] == null && o["error"] == null))
            throw new McpException("This HUD's bridge plugin has no object.explore yet: update What's an AI Bridge and restart the HUD.");
        return (bridge, o);
    }

    private static readonly HashSet<string> PlumbingTypes = new(StringComparer.Ordinal) { "Memory", "IMemory", "GameController", "TheGame" };

    private static string? NotWorthExpanding(JObject child, HashSet<string> ancestorAddresses)
    {
        if (PlumbingTypes.Contains(child["type"]?.ToString() ?? "")) return "plumbing";
        return child["name"]?.ToString() == "Owner" ? "back-reference" : null;
    }

    /// <summary>The node's own Address child plus its ancestors' (RemoteMemoryObjects expose Address).</summary>
    private static HashSet<string> AddressesOf(JObject node, HashSet<string> ancestors)
    {
        var set = new HashSet<string>(ancestors, StringComparer.Ordinal);
        if (node["children"] is JArray kids && Address(kids) is { } a && a != "0") set.Add(a);
        return set;
    }

    private static string? Address(JArray kids) =>
        kids.OfType<JObject>().FirstOrDefault(k => k["name"]?.ToString() == "Address")?["preview"]?.ToString();

    // ── Outline ──────────────────────────────────────────────────────

    internal static string Outline(JObject root)
    {
        var sb = new StringBuilder();
        sb.Append(root["path"]).Append(": ").Append(root["type"]);
        AppendPreview(sb, root);
        sb.AppendLine();
        sb.Append("  C#: ").Append(root["csharp"]);
        if (root["namespace"] != null) sb.Append("   (namespace ").Append(root["namespace"]).Append(')');
        sb.AppendLine();
        AppendBody(sb, root, 1);
        if (root["truncated"] != null) sb.AppendLine().Append("Note: ").Append(root["truncated"]);
        return sb.ToString().TrimEnd();
    }

    private static void AppendBody(StringBuilder sb, JObject node, int indent)
    {
        var pad = new string(' ', indent * 2);
        if (node["components"] is JArray comps && comps.Count > 0)
        {
            var known = comps.Where(c => c["path"] != null).Select(c => c["name"]!.ToString()).ToList();
            var unknown = comps.Where(c => c["path"] == null).Select(c => c["name"]!.ToString()).ToList();
            sb.Append(pad).Append("components (GetComponent<T>()): ").Append(string.Join(", ", known));
            if (unknown.Count > 0) sb.Append("; no HUD type: ").Append(string.Join(", ", unknown));
            sb.AppendLine();
        }
        if (node["children"] is JArray kids)
            foreach (var c in kids.OfType<JObject>())
            {
                sb.Append(pad).Append(DisplayName(c)).Append(": ").Append(c["type"]);
                AppendPreview(sb, c);
                if (c["slowMs"] != null) sb.Append("  (slow ").Append(c["slowMs"]).Append(" ms)");
                if (c["error"] != null) sb.Append("  ! ").Append(c["error"]);
                if (c["expandError"] != null) sb.Append("  ! ").Append(c["expandError"]);
                if (c["kind"]?.ToString() == "blocked") sb.Append("  (not readable by the walker)");
                if (c["notExpanded"] != null) sb.Append("  (").Append(c["notExpanded"]).Append(", not expanded)");
                sb.AppendLine();
                AppendBody(sb, c, indent + 1);
            }
        if (node["page"] is JObject page && page["total"] is { } total
            && page["offset"]!.Value<int>() + (node["children"] as JArray)?.Count < total.Value<int>())
            sb.Append(pad).Append("... ").Append(total.Value<int>() - page["offset"]!.Value<int>() - ((node["children"] as JArray)?.Count ?? 0))
              .Append(" more (offset/limit)").AppendLine();
        if (node["skipped"]?["members"] is JArray skipped && skipped.Count > 0)
            sb.Append(pad).Append("not read (time budget): ").Append(string.Join(", ", skipped)).AppendLine();
    }

    private static void AppendPreview(StringBuilder sb, JObject n)
    {
        var kind = n["kind"]?.ToString();
        var preview = n["preview"]?.ToString();
        if (kind is "list" or "dictionary") { sb.Append(" [").Append(n["count"]).Append(']'); return; }
        if (string.IsNullOrEmpty(preview) || preview == n["type"]?.ToString()) return;
        // Object previews repeat the type name first ("Entity RenderName=..."): keep only the extra part.
        var type = n["type"]?.ToString() ?? "";
        if (kind == "object" && preview.StartsWith(type + " ", StringComparison.Ordinal)) sb.Append("  ").Append(preview[(type.Length + 1)..]);
        else sb.Append(" = ").Append(preview);
    }

    /// <summary>Dictionary entries show their C# key ([GameStat.MaximumLife]), not the walker's string key.</summary>
    private static string DisplayName(JObject c)
    {
        var name = c["name"]?.ToString() ?? "?";
        if (!name.StartsWith('[') || c["csharp"]?.ToString() is not { } cs) return name;
        var i = cs.LastIndexOf("?[", StringComparison.Ordinal);
        return i >= 0 ? cs[(i + 1)..] : name;
    }
}
