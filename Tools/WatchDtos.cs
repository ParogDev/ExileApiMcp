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
    /// <summary>Levels below the expression that were sampled.</summary>
    public int? Depth { get; set; }
    /// <summary>snapshot (bridge object.snapshot, any depth) | eval (fallback: eval:, 2 levels).</summary>
    public string? Via { get; set; }
    /// <summary>Set when the fallback ran: why.</summary>
    public string? Fallback { get; set; }
    /// <summary>The last sample's walk budget (snapshot only): nodes, members, what was skipped, ms per sample.</summary>
    public ObjectWalkBudget? Budget { get; set; }
    /// <summary>Samples cut short by a budget (their missing leaves are not counted as removed), and the last cut.</summary>
    public int? TruncatedSamples { get; set; }
    public ObjectWalkTruncation? LastTruncation { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Bridge ObjectSnapshotResponse (object.snapshot): every leaf under a path, keyed by walker sub-path.</summary>
public sealed class ObjectSnapshotResponse
{
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public int Depth { get; set; }
    public Dictionary<string, string> Leaves { get; set; } = [];
    public ObjectWalkBudget Budget { get; set; } = new();
    public ObjectWalkTruncation? Truncated { get; set; }
    public List<ObjectWalkError>? Errors { get; set; }
    public List<ObjectWalkAlias>? Aliases { get; set; }
    public string Game { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
