using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the running-HUD reflection tools (bridge hud.plugin_perf / hud.plugin_settings / hud.bridge_methods).

/// <summary>hud_plugin_perf: every loaded plugin's Tick and Render cost, most expensive first.</summary>
public sealed class PluginPerfResult
{
    public string? Note { get; set; }
    public List<PluginPerfRow> Plugins { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PluginPerfRow
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Logic cost; null when the HUD has no counter for it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public PluginCostCounter? Tick { get; set; }
    /// <summary>Drawing cost; null when the HUD has no counter for it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public PluginCostCounter? Render { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The HUD's own per-plugin DebugInformation counters, in ms (recent window).</summary>
public sealed class PluginCostCounter
{
    public double? AvgMs { get; set; }
    public double? MaxMs { get; set; }
    public double? LastMs { get; set; }
    public double? TotalAvgMs { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_plugin_settings: a plugin's live settings tree. Nodes are {node, value, min?, max?, values?}, groups nested objects.</summary>
public sealed class PluginSettingsResult
{
    public string Plugin { get; set; } = "";
    /// <summary>The settings class's full name.</summary>
    public string? Type { get; set; }
    /// <summary>Setting name -> node or nested group; its shape is the plugin's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Settings { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>plugin_bridge_methods: everything registered on the HUD's PluginBridge.</summary>
public sealed class BridgeMethodsResult
{
    public string? Note { get; set; }
    public List<BridgeMethodInfo> Methods { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>plugin_bridge_call: one PluginBridge delegate invoked by name (the bridge's hud.bridge_call).</summary>
public sealed class BridgeCallResult
{
    public bool Ok { get; set; }
    public string? Name { get; set; }
    /// <summary>e.g. "String ()".</summary>
    public string? Signature { get; set; }
    /// <summary>json (a string result that parsed as JSON), text, value (a scalar or serialized object) or null.</summary>
    public string? ResultType { get; set; }
    /// <summary>What the method returned, shaped by resultType.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public JsonElement? Result { get; set; }
    /// <summary>How long the call took inside the HUD.</summary>
    public double? Ms { get; set; }
    public string? Note { get; set; }
    /// <summary>Set when the method belongs to a plugin instance that is no longer loaded: the result is that old instance's state.</summary>
    public string? Stale { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BridgeMethodInfo
{
    public string? Name { get; set; }
    /// <summary>e.g. "Double (Entity e)".</summary>
    public string? Signature { get; set; }
    /// <summary>e.g. "Func&lt;Entity, Double&gt;": what to pass to PluginBridge.GetMethod.</summary>
    public string? DelegateType { get; set; }
    public string? DeclaredIn { get; set; }
    /// <summary>Set instead of the above when the registered value is not a delegate.</summary>
    public string? ValueType { get; set; }
    /// <summary>Set when the delegate belongs to a plugin instance that is no longer loaded (reloaded or failed to compile): it answers from that instance's stale state.</summary>
    public string? Stale { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
