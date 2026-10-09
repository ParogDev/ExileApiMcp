using System.ComponentModel;
using System.Text;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hud;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>hud_api_diff: what changed in the HUD's API between builds, and which of our files use it.</summary>
[McpServerToolType]
public static class ApiDiffTools
{
    [McpServerTool(Name = "hud_api_diff", Title = "What a HUD update changed, and what it breaks", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ApiDiffResult))]
    [Description("Snapshot the installed HUD build's API (public types and members, offsets struct fields with [FieldOffset], " +
                 "the IL property map) and diff it with the previous snapshot: types/members removed, renamed or with a new " +
                 "signature, offsets that moved or changed type, properties that now read different memory - plus the impact: " +
                 "where our plugins (the HUD's Plugins\\Source) and the MCP's knowledge (findings, flows, experiments, packs) use " +
                 "the changed names. Run it after every HUD update. from/to: build ids (list=true shows them) or 'poe1'/'poe2' " +
                 "for that game's latest (cross-game diffs work too).")]
    public static CallToolResult HudApiDiff(BridgeRegistry bridges,
        [Description("Older build id, or poe1/poe2 (latest of that game); default: the snapshot before the current one")] string? from = null,
        [Description("Newer build id, or poe1/poe2; default: the installed build (snapshotted now if new)")] string? to = null,
        [Description("Only list the snapshots")] bool list = false,
        [Description("Also list additions (non-breaking)")] bool additions = false,
        [Description("'poe1' or 'poe2'; omit for every HUD installed")] string? game = null)
    {
        var sb = new StringBuilder();
        var result = new JObject();
        foreach (var hud in HudDevTools.Installs(bridges, game))
        {
            var (build, path, created) = ApiSnapshot.Ensure(hud);
            var snaps = ApiSnapshot.List(hud.Game);
            sb.AppendLine($"{hud.Game}: installed build {build}{(created ? " (new snapshot)" : "")}; {snaps.Count} snapshot(s)");
            if (list) { foreach (var s in snaps) sb.AppendLine($"  {s.build}  {s.at:yyyy-MM-dd HH:mm}Z"); continue; }

            var toPath = Resolve(to, hud.Game) ?? path;
            var fromPath = Resolve(from, hud.Game) ?? snaps.Where(s => s.path != toPath).Select(s => s.path).LastOrDefault();
            if (fromPath == null) { sb.AppendLine("  only one snapshot so far: nothing to diff yet (the next HUD update will have one)."); continue; }
            var a = JObject.Parse(File.ReadAllText(fromPath));
            var b = JObject.Parse(File.ReadAllText(toPath));
            var changes = ApiSnapshot.Diff(a, b);
            var breaking = changes.Where(c => c.Breaking).ToList();
            sb.AppendLine($"  {a["game"]}:{a["build"]} -> {b["game"]}:{b["build"]}: {breaking.Count} breaking change(s), {changes.Count - breaking.Count} addition(s)");
            foreach (var g in breaking.GroupBy(c => c.Kind))
            {
                sb.AppendLine($"  {g.Key} ({g.Count()}):");
                foreach (var c in g.Take(25)) sb.AppendLine($"    {Short(c.Type)}: {c.Detail}");
                if (g.Count() > 25) sb.AppendLine($"    ... +{g.Count() - 25} more (structuredContent)");
            }
            if (additions)
                foreach (var c in changes.Where(c => !c.Breaking).Take(40)) sb.AppendLine($"  + {Short(c.Type)}: {c.Detail}");
            var impact = ApiSnapshot.Impact(hud, breaking.Select(c => (Short(c.Type), c.Kind == "type removed" ? Short(c.Type) : c.Name ?? "")));
            if (impact.Count > 0)
            {
                sb.AppendLine($"  Impact - our code using changed names ({impact.Count} hit(s)):");
                foreach (var g in impact.GroupBy(h => h.name).Take(30))
                    sb.AppendLine($"    {g.Key}: {string.Join(", ", g.Take(6).Select(h => h.where))}{(g.Count() > 6 ? $" +{g.Count() - 6}" : "")}");
            }
            else if (breaking.Count > 0) sb.AppendLine("  Impact: no use of the changed names found in our plugins or knowledge.");
            result[hud.Game] = new JObject
            {
                ["from"] = $"{a["game"]}:{a["build"]}", ["to"] = $"{b["game"]}:{b["build"]}",
                ["breaking"] = new JArray(breaking.Select(c => new JObject { ["kind"] = c.Kind, ["type"] = c.Type, ["detail"] = c.Detail })),
                ["additions"] = changes.Count - breaking.Count,
                ["impact"] = new JArray(impact.Select(h => new JObject { ["name"] = h.name, ["where"] = h.where })),
            };
        }
        return Dto.Result(Dto.From<ApiDiffResult>(result), sb.ToString());
    }

    /// <summary>A build id of this game, a full snapshot path, or 'poe1'/'poe2' = that game's latest snapshot.</summary>
    private static string? Resolve(string? id, string game)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id is "poe1" or "poe2") return ApiSnapshot.List(id).LastOrDefault().path ?? throw new McpException($"No snapshot for {id} yet.");
        foreach (var g in new[] { game, "poe1", "poe2" })
        {
            var p = Path.Combine(ApiSnapshot.Dir, g, id + ".json");
            if (File.Exists(p)) return p;
        }
        throw new McpException($"No snapshot '{id}' (list=true shows them).");
    }

    private static string Short(string full) => full[(full.LastIndexOfAny(['.', '+']) + 1)..];
}
