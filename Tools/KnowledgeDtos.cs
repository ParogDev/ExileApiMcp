using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contract of the knowledge tool. One result type for its three modes: no arguments lists packs, search finds
// lines, topic reads one pack (its markdown is the text content; structuredContent names the pack).

public sealed class KnowledgeResult
{
    /// <summary>The list (no topic or search).</summary>
    public List<KnowledgePackInfo>? Packs { get; set; }
    /// <summary>search: the text searched for, and the matching lines (at most 40).</summary>
    public string? Search { get; set; }
    public List<KnowledgeHit>? Hits { get; set; }
    /// <summary>topic: the pack read (its text is the result's text content).</summary>
    public KnowledgePackInfo? Pack { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class KnowledgePackInfo
{
    /// <summary>game/topic, e.g. poe2/api-differences.</summary>
    public string Topic { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>The same pack as a resource: exile://knowledge/{game}/{topic}.</summary>
    public string Uri { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class KnowledgeHit
{
    public string Pack { get; set; } = "";
    /// <summary>1-based line number in the pack.</summary>
    public int Line { get; set; }
    public string Text { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
