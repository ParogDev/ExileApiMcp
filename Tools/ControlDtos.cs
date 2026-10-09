using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the control center (hud_catalog, hud_settings, hud_settings_set): what the server offers and the
// settings of every loaded HUD plugin, both found by reflection (the tool registry; each plugin's ISettings nodes).
// The control-center app (ui-src/src/control) is built on these; unknown fields pass through in Extra.

/// <summary>hud_catalog: everything this server offers, for browsing and for generating forms and result views.</summary>
public sealed class CatalogResult
{
    public CatalogServer Server { get; set; } = new();
    public List<CatalogTool> Tools { get; set; } = [];
    public List<CatalogResource> Resources { get; set; } = [];
    public List<CatalogPrompt> Prompts { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CatalogServer
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string Version { get; set; } = "";
    public List<CatalogIcon>? Icons { get; set; }
    /// <summary>Which games have a reachable HUD bridge right now (poe1 / poe2).</summary>
    public List<string> GamesUp { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CatalogIcon
{
    public string Src { get; set; } = "";
    public string? MimeType { get; set; }
    /// <summary>light | dark | null (any).</summary>
    public string? Theme { get; set; }
}

public sealed class CatalogTool
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    /// <summary>The family it belongs to (the class declaring it, e.g. Observe, Memory, Stats).</summary>
    public string Family { get; set; } = "";
    public bool ReadOnly { get; set; }
    public bool Destructive { get; set; }
    public bool Idempotent { get; set; }
    public bool OpenWorld { get; set; }
    public List<CatalogIcon>? Icons { get; set; }
    public JsonElement? InputSchema { get; set; }
    /// <summary>Present for typed tools: the structured result's schema.</summary>
    public JsonElement? OutputSchema { get; set; }
    /// <summary>The MCP App it opens (ui://...), if any.</summary>
    public string? AppUri { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CatalogResource
{
    /// <summary>A fixed URI, or null when UriTemplate is set.</summary>
    public string? Uri { get; set; }
    public string? UriTemplate { get; set; }
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? MimeType { get; set; }
    /// <summary>True when subscriptions/listen pushes updates for it.</summary>
    public bool Subscribable { get; set; }
    public List<CatalogIcon>? Icons { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CatalogPrompt
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public List<CatalogPromptArgument>? Arguments { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CatalogPromptArgument
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Required { get; set; }
}

/// <summary>hud_settings: the settings of loaded HUD plugins.</summary>
public sealed class SettingsResult
{
    public string Game { get; set; } = "";
    public List<PluginSettings> Plugins { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PluginSettings
{
    /// <summary>The plugin as the HUD lists it (e.g. "Whats An AI Bridge").</summary>
    public string Plugin { get; set; } = "";
    public bool Enabled { get; set; }
    public List<SettingNode> Settings { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One setting: a HUD settings node found by reflection, with labels from the bridge's metadata when it has them.</summary>
public sealed class SettingNode
{
    /// <summary>Property path on the plugin's settings object (e.g. AllowHudInstrumentation, Section.Child).</summary>
    public string Path { get; set; } = "";
    /// <summary>A readable name (metadata, else the property name split into words).</summary>
    public string Label { get; set; } = "";
    public string? Group { get; set; }
    public string? Description { get; set; }
    /// <summary>toggle | range | text | list | color | hotkey | button | unknown.</summary>
    public string Kind { get; set; } = "unknown";
    /// <summary>The current value: bool, number, string, color "#RRGGBBAA", hotkey text; null when unreadable.</summary>
    public JsonElement? Value { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>list: the choices.</summary>
    public List<string>? Options { get; set; }
    /// <summary>Grants agents power (C# scripts, HUD instrumentation, plugin reload): never changeable through MCP tools.</summary>
    public bool Permission { get; set; }
    /// <summary>Shown but not editable from outside the game (e.g. hotkeys).</summary>
    public bool ReadOnly { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_settings_set: the setting after the change.</summary>
public sealed class SettingChangeResult
{
    public string Plugin { get; set; } = "";
    public SettingNode Setting { get; set; } = new();
    public JsonElement? Previous { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
