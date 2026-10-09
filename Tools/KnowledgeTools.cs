using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Knowledge packs: short, verified per-game facts for HUD plugin development (Knowledge/{shared,poe1,poe2}/*.md,
/// embedded in the server). Exposed as a tool (what agents actually call) and as resources
/// exile://knowledge/{game}/{topic} (for clients that browse resources).
/// </summary>
[McpServerToolType]
[McpServerResourceType]
public static class KnowledgeTools
{
    private sealed record Pack(string Game, string Topic, string Title, string Summary, string Text)
    {
        public string Uri => $"exile://knowledge/{Game}/{Topic}";
    }

    private static readonly Lazy<List<Pack>> Packs = new(Load);

    private static List<Pack> Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var list = new List<Pack>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("knowledge/") && n.EndsWith(".md")))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Replace("\r\n", "\n");
            var parts = name["knowledge/".Length..^".md".Length].Replace('\\', '/').Split('/');
            if (parts.Length != 2) continue;
            var lines = text.Split('\n');
            var title = lines.FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim() ?? parts[1];
            var summary = string.Join(" ", lines.SkipWhile(l => !l.StartsWith("# ")).Skip(1)
                .SkipWhile(string.IsNullOrWhiteSpace).TakeWhile(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))).Trim();
            list.Add(new Pack(parts[0], parts[1], title, summary, text));
        }
        return list.OrderBy(p => p.Game == "shared" ? 0 : 1).ThenBy(p => p.Game).ThenBy(p => p.Topic).ToList();
    }

    [McpServerTool(Name = "knowledge", Title = "HUD dev knowledge packs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(KnowledgeResult), IconSource = ExileApiMcp.Hosting.IconSet.KnowledgeLight)]
    [Description("Verified facts for ExileApi (PoE1) / ExileCore2 (PoE2) plugin development: how the HUD compiles and reloads " +
                 "plugins, PoE2 API differences, obfuscated PoE2 offsets, player-stat identity and resistance layers, and " +
                 "how to ask the user for in-game help (shared/working-with-users). " +
                 "No arguments lists the packs; topic reads one (e.g. 'poe2/api-differences' or 'dev-loop'); search finds " +
                 "lines across packs. Read the relevant pack before guessing how the HUD behaves.")]
    public static CallToolResult Knowledge(
        [Description("Pack to read: 'game/topic' or just 'topic'")] string? topic = null,
        [Description("Case-insensitive text to find across all packs")] string? search = null,
        [Description("'poe1', 'poe2' or 'shared' to narrow the list or search")] string? game = null)
    {
        var packs = Packs.Value.Where(p => game == null || p.Game.Equals(game, StringComparison.OrdinalIgnoreCase)
                                           || (p.Game == "shared" && game is "poe1" or "poe2")).ToList();
        if (topic != null)
        {
            var t = topic.Trim().TrimStart('/');
            var match = packs.Where(p => $"{p.Game}/{p.Topic}".Equals(t, StringComparison.OrdinalIgnoreCase)
                                         || p.Topic.Equals(t, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 1)
            {
                var pack = new KnowledgePackInfo { Topic = $"{match[0].Game}/{match[0].Topic}", Title = match[0].Title, Summary = match[0].Summary, Uri = match[0].Uri };
                return new CallToolResult { Content = [new TextContentBlock { Text = match[0].Text }], StructuredContent = Dto.Element(new KnowledgeResult { Pack = pack }) };
            }
            throw new McpException(match.Count == 0
                ? $"No knowledge pack '{topic}'. Available: {string.Join(", ", packs.Select(p => $"{p.Game}/{p.Topic}"))}"
                : $"'{topic}' is ambiguous: {string.Join(", ", match.Select(p => $"{p.Game}/{p.Topic}"))}");
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var hits = new JArray();
            foreach (var p in packs)
            {
                var lines = p.Text.Split('\n');
                for (var i = 0; i < lines.Length && hits.Count < 40; i++)
                    if (lines[i].Contains(search, StringComparison.OrdinalIgnoreCase))
                        hits.Add(new JObject { ["pack"] = $"{p.Game}/{p.Topic}", ["line"] = i + 1, ["text"] = lines[i].Trim() });
            }
            return Typed(new JObject { ["search"] = search, ["hits"] = hits });
        }
        return Typed(new JObject
        {
            ["packs"] = new JArray(packs.Select(p => new JObject
            {
                ["topic"] = $"{p.Game}/{p.Topic}", ["title"] = p.Title, ["summary"] = p.Summary, ["uri"] = p.Uri,
            })),
        });
    }

    private static CallToolResult Typed(JObject o) => Dto.Result(TypedReply.Parse<KnowledgeResult>(o), o.ToString(Newtonsoft.Json.Formatting.None));

    [McpServerResource(UriTemplate = "exile://knowledge/{game}/{topic}", Name = "knowledge-pack", MimeType = "text/markdown", IconSource = ExileApiMcp.Hosting.IconSet.KnowledgeLight)]
    [Description("A knowledge pack: game is 'shared', 'poe1' or 'poe2'. The knowledge tool lists them.")]
    public static string KnowledgeResource(string game, string topic) =>
        Packs.Value.FirstOrDefault(p => p.Game.Equals(game, StringComparison.OrdinalIgnoreCase) && p.Topic.Equals(topic, StringComparison.OrdinalIgnoreCase))?.Text
        ?? throw new McpException($"No knowledge pack {game}/{topic}.");
}
