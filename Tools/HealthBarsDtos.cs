using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// healthbars_state / healthbars_preview: the PoE2 health bar plugin "Whats A Health Bar" seen live through its
// PluginBridge methods WhatsAHealthBar.GetState (Func<string>, JSON) and WhatsAHealthBar.Preview (Func<string, string>),
// and its pub/sub kind healthbars.preview. HealthBarsStateResult mirrors the plugin's HealthBarStateDto (it serializes
// PascalCase; Dto.Options reads either casing); the plugin's DTO grows, so every class keeps unknown fields in Extra and
// the control center's Health Bars page shows them under "More".

/// <summary>healthbars_state: what the health bar plugin is doing right now.</summary>
public sealed class HealthBarsStateResult
{
    public string? Game { get; set; }
    /// <summary>The plugin's display name and its state schema version.</summary>
    public string? Plugin { get; set; }
    public int Schema { get; set; }
    public bool Enabled { get; set; }
    /// <summary>Why no bars are drawn right now ("in town", "in hideout", "a large panel is open", "not in game", "disabled"), or null while drawing.</summary>
    public string? Suppressed { get; set; }
    public string? Area { get; set; }
    public HealthBarsCounts Counts { get; set; } = new();
    public HealthBarsTimings Timings { get; set; } = new();
    public HealthBarsPreview Preview { get; set; } = new();
    public HealthBarsBridge Bridge { get; set; } = new();
    /// <summary>A pool read that couldn't be real in the last 30 s (which pool, what was read, on which entity: a moved offset), or null.</summary>
    public string? PoolProblem { get; set; }
    /// <summary>The last kills published (newest first, up to 10).</summary>
    public List<HealthBarsKill> Kills { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HealthBarsCounts
{
    /// <summary>Monsters and players known to the plugin (EntityAdded minus EntityRemoved).</summary>
    public int Tracked { get; set; }
    /// <summary>Passed classification: alive, in range, a shown kind.</summary>
    public int Wanted { get; set; }
    /// <summary>On screen and drawn last frame.</summary>
    public int Drawn { get; set; }
    /// <summary>Most bars drawn in one frame since the area started.</summary>
    public int PeakDrawn { get; set; }
    /// <summary>Drawn last frame per kind: unique, rare, magic, normal, minion, player, self.</summary>
    public Dictionary<string, int> ByKind { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The plugin's own timings, measured with Stopwatch around its Tick and Render, microseconds.</summary>
public sealed class HealthBarsTimings
{
    public double TickUs { get; set; }
    /// <summary>Max over the last WindowSec seconds.</summary>
    public double TickMaxUs { get; set; }
    public double RenderUs { get; set; }
    public double RenderMaxUs { get; set; }
    /// <summary>The last classification pass (runs at ClassifyHz; included in TickUs on those frames).</summary>
    public double ClassifyUs { get; set; }
    /// <summary>(Tick + Render) per drawn bar, nanoseconds: the cost of one more bar on screen.</summary>
    public double PerBarNs { get; set; }
    public int ClassifyHz { get; set; }
    /// <summary>Quads written into the draw list last frame (all bars, one batch).</summary>
    public int Quads { get; set; }
    /// <summary>Render split: building the quads from the bars, and writing them into the draw list, microseconds.</summary>
    public double BuildUs { get; set; }
    public double FlushUs { get; set; }
    /// <summary>"batched" (vertices written straight into the draw list), or "fallback: why" (one ImGui call per quad).</summary>
    public string? RenderPath { get; set; }
    public int WindowSec { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HealthBarsPreview
{
    public bool Active { get; set; }
    public double SecondsLeft { get; set; }
    /// <summary>The kind the preview puts first (Unique, Rare, Magic, Normal, Minion, Player, Self), or null.</summary>
    public string? Focus { get; set; }
    /// <summary>Preview bars drawn last frame: the samples plus the stress grid; the timings' PerBarNs counts them.</summary>
    public int Bars { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HealthBarsBridge
{
    /// <summary>Whether the plugin reached Whats An AI Bridge's pub/sub (AIBridge.Publish / Subscribe); Why says what is missing.</summary>
    public bool Linked { get; set; }
    public string? Why { get; set; }
    public string? Subscribes { get; set; }
    public string? Publishes { get; set; }
    /// <summary>The Publish kills setting: Off, Uniques, Rares and uniques.</summary>
    public string? PublishKills { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The data of a healthbars.kill event.</summary>
public sealed class HealthBarsKill
{
    public string Name { get; set; } = "";
    public string Rarity { get; set; } = "";
    public string? Path { get; set; }
    public int MaxLife { get; set; }
    public int MaxEs { get; set; }
    public int MaxWard { get; set; }
    /// <summary>From the first hit the plugin saw to death, milliseconds (null when it died before a hit was seen).</summary>
    public int? TimeToKillMs { get; set; }
    public string? Area { get; set; }
    public string? At { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>healthbars_preview: how the preview request reached the plugin.</summary>
public sealed class HealthBarsPreviewResult
{
    public bool Ok { get; set; }
    public double Seconds { get; set; }
    public string? Focus { get; set; }
    /// <summary>Extra animated bars asked for on a grid (a stress test), 0 for none.</summary>
    public int Stress { get; set; }
    /// <summary>pubsub: published as healthbars.preview and the plugin's subscription received it; bridge_call: the plugin
    /// was not subscribed (or the bridge has no pub/sub), so WhatsAHealthBar.Preview was called directly.</summary>
    public string Via { get; set; } = "";
    /// <summary>The event's sequence number when published.</summary>
    public long? Seq { get; set; }
    /// <summary>Whether the plugin's own subscription to healthbars.preview was registered in the bridge (null: the bridge could not say).</summary>
    public bool? PluginSubscribed { get; set; }
    /// <summary>Agents (TCP listeners) that read healthbars.preview in the last 20 s, besides the plugin.</summary>
    public List<string> AgentListeners { get; set; } = [];
    /// <summary>What happened on the way, in plain words (the fallback taken, a pub/sub refusal).</summary>
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
