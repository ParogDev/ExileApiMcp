using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the observer tools and resources (their output schemas). Code parses these, not an LLM.
// Every type keeps unknown fields in Extra ([JsonExtensionData]): a field the bridge adds later still arrives, and the
// schema allows additional properties, so new data never needs a server release to pass through.

/// <summary>One observer event. Fields per kind are typed; anything else is in Extra.</summary>
public sealed class ObserveEvent
{
    public long Seq { get; set; }
    /// <summary>UTC time of the event: the axis across HUD runs.</summary>
    public DateTimeOffset At { get; set; }
    /// <summary>ms on the observer clock of this HUD run (lines layers up precisely within a run).</summary>
    public double? T { get; set; }
    /// <summary>HUD (ImGui) frame number.</summary>
    public long? Frame { get; set; }
    /// <summary>layer | layer.noisy | ui | area | level | entity (older journals: server | server.noisy).</summary>
    public string Kind { get; set; } = "";

    // layer
    public string? Layer { get; set; }
    public string? Mode { get; set; }
    /// <summary>What changed within the layer: struct offset (0x..), property name, dictionary key or item id.</summary>
    public string? Unit { get; set; }
    /// <summary>The HUD's name for the unit (struct mode), null when unmapped.</summary>
    public string? Name { get; set; }
    public string? Old { get; set; }
    public string? New { get; set; }
    public string? Off { get; set; }
    public int? Len { get; set; }
    public string? I32 { get; set; }
    public string? I64 { get; set; }
    public double? Delta { get; set; }
    /// <summary>added | removed (dict / list), null for a value change.</summary>
    public string? Change { get; set; }
    public string? Group { get; set; }
    public string? Note { get; set; }

    // ui
    public int? Index { get; set; }
    public bool? Visible { get; set; }
    public string? Mapped { get; set; }
    public bool? FirstSeen { get; set; }
    public List<string>? Texts { get; set; }

    // area / level / entity
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Area { get; set; }
    public string? Type { get; set; }
    public string? EntityType { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Older journals wrote the server layer as kind server/server.noisy with off: read them as layer events.</summary>
    public ObserveEvent Normalized()
    {
        if (Kind is "server" or "server.noisy")
        {
            Kind = Kind == "server" ? "layer" : "layer.noisy";
            Layer ??= "server"; Mode ??= "struct"; Unit ??= Off;
        }
        return this;
    }

    /// <summary>What happened, without values: the key companions are counted by in observe_timeline.</summary>
    public string Key() => Kind switch
    {
        "layer" => $"{Layer} {Unit}" + (Name != null ? $" {Name}" : ""),
        "layer.noisy" => $"{Layer} {Group ?? Unit} noisy",
        "ui" => $"ui [{Index}] {(Visible == true ? "opened" : "closed")} {Mapped ?? "unmapped"}",
        "area" => "area change",
        "level" => "level up",
        "entity" => $"entity {Type}",
        _ => Kind,
    };

    /// <summary>One line for text output.</summary>
    public string Line() => Kind switch
    {
        "layer" => $"{Layer} {Unit}{(Name != null ? $" {Name}" : Mode == "struct" ? " (unmapped)" : "")} {Old ?? "-"} -> {New ?? "-"}"
                   + (I32 != null ? $"  i32 {I32}" : I64 != null ? $"  i64 {I64}" : Delta is { } d ? $"  {d:+0.###;-0.###}" : ""),
        "layer.noisy" => $"{Layer} {Group ?? Unit} is noisy: counted in its layer map, not logged",
        "ui" => $"ui [{Index}] {(Visible == true ? "opened" : "closed")} {Mapped ?? "UNMAPPED"}{(FirstSeen == true ? " (first time)" : "")}"
                + (Texts is { Count: > 0 } t ? $" texts: {string.Join(" | ", t.Take(4))}" : ""),
        "area" => $"area {From} -> {To}",
        "level" => $"level {From} -> {To}{(Area != null ? $" in {Area}" : "")}",
        "entity" => $"entity {Type}{(EntityType != null ? $" ({EntityType})" : "")}",
        _ => Kind,
    };
}

public sealed class ObserveEventsResult
{
    public bool Enabled { get; set; }
    /// <summary>The last sequence number: pass it as since next time.</summary>
    public long Seq { get; set; }
    public List<ObserveEvent> Events { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A layer spec: what to watch, how and how often (observe_layers action=set).</summary>
public sealed class LayerSpec
{
    public string Id { get; set; } = "";
    /// <summary>A walker path starting at GameController (as in eval_path / explore_object).</summary>
    public string Path { get; set; } = "";
    /// <summary>struct | props | dict | list.</summary>
    public string Mode { get; set; } = "props";
    public double Hz { get; set; } = 4;
    public bool Enabled { get; set; } = true;
    /// <summary>list mode: the item property identifying an item (default Address).</summary>
    public string? Key { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LayerStatus
{
    public LayerSpec Spec { get; set; } = new();
    public long Events { get; set; }
    public long Ticks { get; set; }
    public double CostMs { get; set; }
    public long UnitsChanged { get; set; }
    public long NoisyUnits { get; set; }
    public int? Bytes { get; set; }
    public int? NamedRanges { get; set; }
    public List<string>? SlowProps { get; set; }
    /// <summary>A link that no longer exists (the layer stopped): fix the spec.</summary>
    public string? Broken { get; set; }
    /// <summary>Resolves to nothing right now (loading screen, no player): normal.</summary>
    public string? NotNow { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LayersResult
{
    public List<string> Modes { get; set; } = [];
    public List<LayerStatus> Layers { get; set; } = [];
    /// <summary>For action=set: the stored spec and its preflight.</summary>
    public LayerSpec? Layer { get; set; }
    public string? Preflight { get; set; }
    public string? Removed { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LayerUnit
{
    public string Unit { get; set; } = "";
    public string? Name { get; set; }
    public long Changes { get; set; }
    public double PerMinute { get; set; }
    public double FirstT { get; set; }
    public double LastT { get; set; }
    public string Last { get; set; } = "";
    /// <summary>False when its noise group went noisy: counted here, not in the journal.</summary>
    public bool Logged { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LayerMapResult
{
    public double T { get; set; }
    public LayerStatus Layer { get; set; } = new();
    public List<LayerUnit> Units { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class TimelineCompanion
{
    public string Event { get; set; } = "";
    public int Count { get; set; }
    public double AvgDtMs { get; set; }
}

public sealed class TimelineResult
{
    public int JournalEvents { get; set; }
    public int WindowMs { get; set; }
    /// <summary>around mode: the centre event and every event in the window.</summary>
    public long? Centre { get; set; }
    public List<ObserveEvent>? Events { get; set; }
    /// <summary>unit mode: the layer and unit, how often it changed, and what happened around those changes.</summary>
    public string? Layer { get; set; }
    public string? Unit { get; set; }
    public int? Changes { get; set; }
    public List<TimelineCompanion>? Companions { get; set; }
    public List<ObserveEvent>? RecentChanges { get; set; }
}
