using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contract of watch_object: the leaves of a walker path that changed while it was sampled.

public sealed class WatchLeafChange
{
    /// <summary>Leaf path under the expression, e.g. "CurHP" or "Buffs[2].Timer".</summary>
    public string Path { get; set; } = "";
    public int Changes { get; set; }
    public string First { get; set; } = "";
    public string Last { get; set; } = "";
    public long FirstChangeAtMs { get; set; }
    public long LastChangeAtMs { get; set; }
    /// <summary>Changed on nearly every sample (timers, counters).</summary>
    public bool? Noisy { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>watch_object: which leaves changed, meaningful first and noisy last.</summary>
public sealed class WatchObjectResult
{
    public string Expression { get; set; } = "";
    /// <summary>The walker's type name of the object (null when it never said).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Type { get; set; }
    public int Samples { get; set; }
    public long DurationMs { get; set; }
    public int LeavesWatched { get; set; }
    public int ChangedLeaves { get; set; }
    public List<WatchLeafChange> Changes { get; set; } = [];
    public string? Truncated { get; set; }
    /// <summary>"N failed samples; last: ..." when some samples failed.</summary>
    public string? Errors { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
