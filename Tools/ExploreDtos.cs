using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of explore_object / show_data_explorer (bridge object.explore) and find_in_object.
// ui-src/src/explorer/types.ts mirrors ExploreNode for the data explorer app: change both together.

/// <summary>
/// One addressable value in the HUD object model: the root of an explore_object result, one of its children (object
/// members, list items "[i]", dictionary entries "[Key]") or an entity component. Optional fields are omitted when empty.
/// </summary>
public sealed class ExploreNode
{
    /// <summary>Children and components: the member name, "[i]" or "[Key]".</summary>
    public string? Name { get; set; }
    /// <summary>Walker path, e.g. GameController.Player.GetComponent&lt;Life&gt;(). Absent where the walker cannot address it.</summary>
    public string? Path { get; set; }
    /// <summary>Null-safe C# for a plugin, e.g. GameController?.Player?.GetComponent&lt;Life&gt;().</summary>
    public string? Csharp { get; set; }
    /// <summary>Friendly runtime type, e.g. Dictionary&lt;GameStat, Int32&gt;.</summary>
    public string? Type { get; set; }
    /// <summary>Declared member type when it differs from the runtime type.</summary>
    public string? DeclaredType { get; set; }
    /// <summary>For the using a plugin needs (the root only).</summary>
    public string? Namespace { get; set; }
    /// <summary>null | bool | number | string | enum | struct | object | list | dictionary | sequence | blocked | component.</summary>
    public string? Kind { get; set; }
    /// <summary>One line: numbers, "text", X=1 Y=2 for structs, Entity RenderName="..." for objects, Count = N.</summary>
    public string? Preview { get; set; }
    /// <summary>Lists and dictionaries.</summary>
    public int? Count { get; set; }
    public bool? Expandable { get; set; }
    public List<ExploreNode>? Children { get; set; }
    /// <summary>Entities: GetComponent&lt;T&gt;() rows. No path = no HUD wrapper type for that component.</summary>
    public List<ExploreNode>? Components { get; set; }
    /// <summary>Collections: the window of items in children.</summary>
    public ExplorePage? Page { get; set; }
    /// <summary>Members not read because the bridge's time budget ran out.</summary>
    public ExploreSkipped? Skipped { get; set; }
    public string? Note { get; set; }
    /// <summary>Children: the getter took this long (>= 5 ms).</summary>
    public double? SlowMs { get; set; }
    /// <summary>Children: the getter threw.</summary>
    public string? Error { get; set; }
    /// <summary>depth > 1: why a nested expansion was skipped (back-reference, plumbing) or failed.</summary>
    public string? NotExpanded { get; set; }
    public string? ExpandError { get; set; }
    /// <summary>Root, depth > 1: set when the lookup budget ran out.</summary>
    public string? Truncated { get; set; }
    public double? ElapsedMs { get; set; }
    /// <summary>Root: which game's HUD answered.</summary>
    public string? Game { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ExplorePage
{
    public int Offset { get; set; }
    public int Limit { get; set; }
    public int? Total { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ExploreSkipped
{
    public string? Reason { get; set; }
    public List<string> Members { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>find_in_object: members under root whose name and/or value matched. name and value echo the search (null when not used).</summary>
public sealed class FindInObjectResult
{
    public string Root { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Name { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Value { get; set; }
    public int ObjectsOpened { get; set; }
    public List<FindMatch> Matches { get; set; } = [];
    /// <summary>Set when the search stopped early (maxNodes): how many objects were left.</summary>
    public string? Incomplete { get; set; }
    /// <summary>Set when nothing matched.</summary>
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class FindMatch
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Path { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Csharp { get; set; }
    /// <summary>The member's type, or "component".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Type { get; set; }
    /// <summary>The one-line preview; null for components.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Value { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
