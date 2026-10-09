using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the stats tools (bridge stats.*). ui-src/src/types.ts mirrors these for the player stats app:
// change both together. Optional fields are omitted by the bridge when empty, never faked.

/// <summary>The shared stats view state owned by the HUD plugin. Every change bumps rev.</summary>
public sealed class StatsViewState
{
    public long Rev { get; set; }
    public List<string> PinnedStatKeys { get; set; } = [];
    public string Filter { get; set; } = "";
    /// <summary>all | vitals | resistances | defense | offense | charges | movement | other.</summary>
    public string Category { get; set; } = "all";
    public string? SelectedStatKey { get; set; }
    /// <summary>category | key | value.</summary>
    public string SortBy { get; set; } = "category";
    public bool SortDesc { get; set; }
    public bool PanelOpen { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class StatsVitals
{
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Es { get; set; }
    public int MaxEs { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }
    /// <summary>PoE2 only: active weapon set (0/1).</summary>
    public int? WeaponSet { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One stat, identified by its Stats.dat key (stable across patches and games).</summary>
public sealed class StatItem
{
    /// <summary>GameStat value for this game build: not stable across patches.</summary>
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public int Value { get; set; }
    /// <summary>Translated in-game text; absent for stats without a description.</summary>
    public string? Text { get; set; }
    public string Category { get; set; } = "";
    public bool? IsLocal { get; set; }
    public bool? Pinned { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>stats_ui_state: the full state, or only {rev, unchanged:true, vitals} when sinceRev is current.</summary>
public sealed class StatsUiStateResult
{
    public string? Game { get; set; }
    public long Rev { get; set; }
    public bool? Unchanged { get; set; }
    public StatsViewState? State { get; set; }
    public StatsVitals? Vitals { get; set; }
    public bool InGame { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>stats_page: a page of stats, per-category counts over all stats, and all pinned stats.</summary>
public sealed class StatsPageResult
{
    public string? Game { get; set; }
    public long Rev { get; set; }
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<StatItem> Items { get; set; } = [];
    /// <summary>Category -> count over the player's full stat set (ignoring filter and paging).</summary>
    public Dictionary<string, int> Categories { get; set; } = [];
    public List<StatItem> Pinned { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>get_stat: one stat (present:false when the player lacks it), its Stats.dat record type and flags.</summary>
public sealed class StatDetailResult
{
    public string? Game { get; set; }
    public StatItem? Stat { get; set; }
    public string? RecordType { get; set; }
    public bool? IsWeaponLocal { get; set; }
    public bool Present { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The stats view mutators: the new state (an error result with ok:false, error and the current state when nothing was applied).</summary>
public sealed class StatsMutationResult
{
    public bool Ok { get; set; }
    public long Rev { get; set; }
    public StatsViewState State { get; set; } = new();
    /// <summary>rev_mismatch | unknown_stat | invalid_argument | too_many_pins.</summary>
    public string? Error { get; set; }
    public string? Message { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>show_player_stats: what the app gets first, before any polling. Every field is always present (null when the bridge had none).</summary>
public sealed class ShowPlayerStatsResult
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Game { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long? Rev { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool? InGame { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public StatsVitals? Vitals { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public StatsViewState? State { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public List<StatItem>? Pinned { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public List<StatItem>? Resistances { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public Dictionary<string, int>? Categories { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
