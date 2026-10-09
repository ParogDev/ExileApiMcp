using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the game-state reads (bridge "query" replies), eval_path / describe_type and bridge_status.
// They mirror the bridge's BridgeDtos: same names and nesting; fields a game's HUD cannot provide are omitted (and
// named in unsupported), never faked. Unknown fields pass through in Extra.

/// <summary>What every bridge query reply carries: which HUD answered, the query and when.</summary>
public abstract class BridgeQueryResult
{
    /// <summary>poe1 | poe2: which HUD answered.</summary>
    public string? Game { get; set; }
    public string Query { get; set; } = "";
    public string Timestamp { get; set; } = "";
    /// <summary>Features touched by this query that this game's HUD cannot provide (their fields are omitted).</summary>
    public List<string>? Unsupported { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PlayerResult : BridgeQueryResult
{
    public PlayerInfo? Player { get; set; }
}

public sealed class AreaResult : BridgeQueryResult
{
    public AreaInfo? Area { get; set; }
}

/// <summary>get_entities / deep_scan: entities nearest first; deep_scan adds filter, matchCount and component dumps.</summary>
public sealed class EntitiesResult : BridgeQueryResult
{
    public List<EntityInfo>? Entities { get; set; }
    /// <summary>deep_scan: the metadata path substring.</summary>
    public string? Filter { get; set; }
    public int? MatchCount { get; set; }
    /// <summary>Set when entities were dropped (furthest first) to keep the result small.</summary>
    [JsonPropertyName("_truncated")] public string? Truncated { get; set; }
}

public sealed class NpcDialogResult : BridgeQueryResult
{
    public NpcDialogInfo? NpcDialog { get; set; }
}

public sealed class MapDataResult : BridgeQueryResult
{
    public MapDataInfo? MapData { get; set; }
}

public sealed class UiPanelsResult : BridgeQueryResult
{
    public UiPanelsInfo? Ui { get; set; }
}

public sealed class StashResult : BridgeQueryResult
{
    public List<StashTabInfo>? StashTabs { get; set; }
}

public sealed class PlayerStatsRawResult : BridgeQueryResult
{
    /// <summary>GameStat enum name -> value.</summary>
    public Dictionary<string, int>? Stats { get; set; }
}

/// <summary>get_all: player, area, nearby entities, NPC dialog and map data in one reply.</summary>
public sealed class GameOverviewResult : BridgeQueryResult
{
    public PlayerInfo? Player { get; set; }
    public AreaInfo? Area { get; set; }
    public List<EntityInfo>? Entities { get; set; }
    public NpcDialogInfo? NpcDialog { get; set; }
    public MapDataInfo? MapData { get; set; }
    [JsonPropertyName("_truncated")] public string? Truncated { get; set; }
}

public sealed class PlayerInfo
{
    public string Path { get; set; } = "";
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Es { get; set; }
    public int MaxEs { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }
    /// <summary>Grid position.</summary>
    public List<double> Pos { get; set; } = [];
    public double? Rotation { get; set; }
    public List<BuffInfo>? Buffs { get; set; }
    public List<SkillInfo>? Skills { get; set; }
    public ActorInfo? Actor { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BuffInfo
{
    public string Name { get; set; } = "";
    public int Charges { get; set; }
    public double Timer { get; set; }
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public int Stacks { get; set; }
    public double MaxTime { get; set; }
    public long SourceEntityId { get; set; }
    public int SourceSkillId { get; set; }
    public string? SourceName { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SkillInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? InternalName { get; set; }
    public string? DisplayName { get; set; }
    public bool CanBeUsed { get; set; }
    public bool IsOnSkillBar { get; set; }
    public double Cooldown { get; set; }
    public bool IsUsing { get; set; }
    public bool? IsUserSkill { get; set; }
    public bool? IsMine { get; set; }
    public int? TotalUses { get; set; }
    public int? Cost { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ActorInfo
{
    public int ActionId { get; set; }
    public string Action { get; set; } = "";
    public int AnimationId { get; set; }
    public string Animation { get; set; } = "";
    public bool IsMoving { get; set; }
    public bool IsAttacking { get; set; }
    public CurrentActionInfo? CurrentAction { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CurrentActionInfo
{
    public string? Skill { get; set; }
    public List<int> Destination { get; set; } = [];
    public long? TargetId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AreaInfo
{
    public string? Name { get; set; }
    public int AreaLevel { get; set; }
    public int Act { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class NpcDialogInfo
{
    public bool IsVisible { get; set; }
    public int DialogDepth { get; set; }
    public string? NpcName { get; set; }
    public bool? IsLoreTalk { get; set; }
    public List<string>? Lines { get; set; }
    public string? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MapDataInfo
{
    public int DialogDepth { get; set; }
    /// <summary>Map stat name -> value.</summary>
    public Dictionary<string, int>? MapStats { get; set; }
    public QuestFlagsInfo? QuestFlags { get; set; }
    public string? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Selected quest flags: flag name -> bool (in Extra), plus how many flags the game has.</summary>
public sealed class QuestFlagsInfo
{
    [JsonPropertyName("_total")] public int Total { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class UiPanelsInfo
{
    public int DialogDepth { get; set; }
    public bool NpcDialog { get; set; }
    public bool PurchaseWindow { get; set; }
    public bool SellWindow { get; set; }
    public bool? MapDeviceWindow { get; set; }
    public bool TradeWindow { get; set; }
    public bool PopUpWindow { get; set; }
    public bool RitualWindow { get; set; }
    public bool? VillageRewardWindow { get; set; }
    public bool? MercenaryEncounterWindow { get; set; }
    public bool? ZanaMissionChoice { get; set; }
    public LeagueMechanicButtonsInfo? LeagueMechanicButtons { get; set; }
    /// <summary>Visible top-level IngameUi children: index, child count, text, child texts.</summary>
    public List<UiChildInfo> VisibleChildren { get; set; } = [];
    public string? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LeagueMechanicButtonsInfo
{
    public bool Vis { get; set; }
    public int Cc { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class UiChildInfo
{
    public int I { get; set; }
    public int Cc { get; set; }
    public string? T { get; set; }
    public List<string>? Ct { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class StashTabInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int VisibleIndex { get; set; }
    public RgbInfo Color { get; set; } = new();
    public bool IsPremium { get; set; }
    public bool IsPublic { get; set; }
    public bool IsRemoveOnly { get; set; }
    public bool IsHidden { get; set; }
    public bool IsMapSeries { get; set; }
    public int RawFlags { get; set; }
    public long? Affinity { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RgbInfo
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>An entity; deep_scan (deep = true) adds the component dumps.</summary>
public sealed class EntityInfo
{
    public long Id { get; set; }
    public bool? Deep { get; set; }
    public string Type { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Name { get; set; }
    public bool Alive { get; set; }
    public bool Hostile { get; set; }
    public string Rarity { get; set; } = "";
    public double Dist { get; set; }
    public List<double> Pos { get; set; } = [];
    public int Hp { get; set; }
    public int MaxHp { get; set; }

    // Deep fields
    public bool? IsValid { get; set; }
    public List<string>? AllComponents { get; set; }
    public RenderInfo? Render { get; set; }
    public PositionedInfo? Positioned { get; set; }
    public AnimatedInfo? Animated { get; set; }
    public StateMachineInfo? StateMachine { get; set; }
    public NpcInfo? Npc { get; set; }
    public LifeInfo? Life { get; set; }
    public TargetableInfo? Targetable { get; set; }
    public ChestInfo? Chest { get; set; }
    public OmpInfo? Omp { get; set; }
    public MinimapIconInfo? MinimapIcon { get; set; }
    public List<BuffInfo>? Buffs { get; set; }
    /// <summary>Stat name -> value (and _truncated: the total when cut).</summary>
    public Dictionary<string, JsonElement>? Stats { get; set; }
    public ActorInfo? Actor { get; set; }
    public BeamInfo? Beam { get; set; }
    public GroundEffectInfo? GroundEffect { get; set; }
    public bool? HasEffectPack { get; set; }
    public AnimControllerInfo? AnimController { get; set; }
    [JsonPropertyName("_end")] public bool? End { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RenderInfo
{
    public string? Name { get; set; }
    public List<double> Pos { get; set; } = [];
    public List<double> Bounds { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PositionedInfo
{
    public List<int>? Grid { get; set; }
    public int? Reaction { get; set; }
    public int? Size { get; set; }
    public double? Scale { get; set; }
    public double? Rotation { get; set; }
    public double? TravelProgress { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AnimatedInfo
{
    public string? BaseEntityPath { get; set; }
    public long BaseEntityId { get; set; }
    public bool? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class StateMachineInfo
{
    public bool? CanBeTarget { get; set; }
    public bool? InTarget { get; set; }
    public Dictionary<string, int> States { get; set; } = [];
    public bool? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class NpcInfo
{
    public bool HasIconOverhead { get; set; }
    public bool IsIgnoreHidden { get; set; }
    public bool IsMinMapLabelVisible { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LifeInfo
{
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Es { get; set; }
    public int MaxEs { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class TargetableInfo
{
    public bool IsTargetable { get; set; }
    public bool IsTargeted { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ChestInfo
{
    public bool IsOpened { get; set; }
    public bool IsLocked { get; set; }
    public bool IsStrongbox { get; set; }
    public bool DestroyAfterOpen { get; set; }
    public bool IsLarge { get; set; }
    public bool Stompable { get; set; }
    public bool OpenOnDamage { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>ObjectMagicProperties: rarity and mods.</summary>
public sealed class OmpInfo
{
    public string Rarity { get; set; } = "";
    public List<string>? Mods { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MinimapIconInfo
{
    public string Name { get; set; } = "";
    public bool IsVisible { get; set; }
    public bool IsHide { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BeamInfo
{
    public List<double> Start { get; set; } = [];
    public List<double> End { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GroundEffectInfo
{
    public double Duration { get; set; }
    public double MaxDuration { get; set; }
    public double Scale { get; set; }
    public double SizeIncrease { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AnimControllerInfo
{
    public int AnimId { get; set; }
    public int Stage { get; set; }
    public double Progress { get; set; }
    public double Speed { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// ── eval_path / describe_type ────────────────────────────────────

/// <summary>eval_path: the value at a path. value is whatever the path holds (number, string, object dump, list, null).</summary>
public sealed class EvalResult
{
    public string Expression { get; set; } = "";
    /// <summary>Runtime type name of the value ("null" for null).</summary>
    public string? Type { get; set; }
    /// <summary>Any JSON value (explicit null when the path is null).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Value { get; set; }
    /// <summary>True when the dump was cut by depth or size limits.</summary>
    public bool? Truncated { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>describe_type: public properties and (up to 30) methods of the object at a path.</summary>
public sealed class DescribeTypeResult
{
    public string Expression { get; set; } = "";
    /// <summary>Full type name.</summary>
    public string? Type { get; set; }
    public List<TypePropertyInfo> Properties { get; set; } = [];
    public List<TypeMethodInfo> Methods { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class TypePropertyInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool CanRead { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class TypeMethodInfo
{
    public string Name { get; set; } = "";
    public string ReturnType { get; set; } = "";
    public List<TypeParameterInfo>? Parameters { get; set; }
    public bool? IsGeneric { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class TypeParameterInfo
{
    public string? Name { get; set; }
    public string Type { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// ── bridge_status ────────────────────────────────────────────────

public sealed class BridgeStatusResult
{
    public List<BridgeStatusEntry> Bridges { get; set; } = [];
    /// <summary>Whether the user's screen can show the HUD right now.</summary>
    public DesktopInfo? Desktop { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BridgeStatusEntry
{
    public string Game { get; set; } = "";
    public string BridgeDir { get; set; } = "";
    /// <summary>not running | connected | unreachable.</summary>
    public string Status { get; set; } = "";
    public int? Port { get; set; }
    public BridgeHello? Hello { get; set; }
    /// <summary>Findings verified on another game but not this one.</summary>
    public string? FindingsToCheck { get; set; }
    /// <summary>The HUD build changed since the previous API snapshot: run hud_api_diff.</summary>
    public string? HudApiChanged { get; set; }
    /// <summary>Why the bridge is unreachable.</summary>
    public string? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The bridge's handshake: the game and HUD behind it and what that HUD cannot provide.</summary>
public sealed class BridgeHello
{
    public string? Game { get; set; }
    public int? ProtocolVersion { get; set; }
    public string? Hud { get; set; }
    public string? HudBuild { get; set; }
    public string? BridgeBuild { get; set; }
    public bool? InGame { get; set; }
    public List<string>? Unsupported { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DesktopInfo
{
    public long? IdleSeconds { get; set; }
    public long? DisplayTimeoutSeconds { get; set; }
    public bool? DisplayLikelyOff { get; set; }
    public bool? GameForeground { get; set; }
    public string? Warning { get; set; }
    public string? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
