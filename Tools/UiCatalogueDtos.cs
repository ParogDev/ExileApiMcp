using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contract of ui_catalogue (bridge observe.ui_catalogue, Whats An AI Bridge Shared/UiCatalogue.cs). Unknown fields
// pass through in Extra, so a field the bridge adds later arrives without a server release.

public sealed class UiCatalogueResult
{
    public bool Ok { get; set; }
    /// <summary>Whether passive observation is on (the catalogue fills only while it is).</summary>
    public bool Observing { get; set; }
    /// <summary>The catalogue file on the HUD machine.</summary>
    public string? File { get; set; }
    /// <summary>Interfaces in the catalogue (before the query).</summary>
    public int Total { get; set; }
    public List<UiCatalogueEntry> Entries { get; set; } = [];
    /// <summary>What the captures cost: captures, lastUs, maxUs, lastBytes, maxBytes, lastVisited.</summary>
    public JsonElement? Cost { get; set; }
    /// <summary>Panels open now that are being captured.</summary>
    public int? OpenNow { get; set; }
    /// <summary>Why nothing matched (observe off, no such key, nothing catalogued yet).</summary>
    public string? Why { get; set; }
    public string? LastError { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One distinct interface: an NPC's dialogue, a travel menu, a choice or reward panel.</summary>
public sealed class UiCatalogueEntry
{
    /// <summary>Fingerprint: "NpcDialog|&lt;npc&gt;" for NPC dialogues, else "&lt;mapped property or panel&gt;|&lt;title&gt;".</summary>
    public string Key { get; set; } = "";
    /// <summary>IngameUi property that returns the panel, when the HUD maps it.</summary>
    public string? Mapped { get; set; }
    /// <summary>The panel's first text: highlight's panel / within match on it.</summary>
    public string? Title { get; set; }
    public string? Npc { get; set; }
    /// <summary>Its first texts: how to recognise it.</summary>
    public List<string> Recognise { get; set; } = [];
    public List<string> Areas { get; set; } = [];
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int Opened { get; set; }
    /// <summary>Distinct text sets seen (pages of a dialogue).</summary>
    public int Views { get; set; }
    public float[]? Rect { get; set; }
    /// <summary>Visible texts with their highlight targets; null with options=false (then OptionCount).</summary>
    public List<UiCatalogueOption>? Options { get; set; }
    public int? OptionCount { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class UiCatalogueOption
{
    public string Text { get; set; } = "";
    /// <summary>Child indexes from the panel to the element.</summary>
    public int[] Child { get; set; } = [];
    public float[]? Rect { get; set; }
    /// <summary>The element or a parent within two levels reacts to hover: likely clickable.</summary>
    public bool Clickable { get; set; }
    /// <summary>The text is unique in its view, so the text target hits only this element.</summary>
    public bool Unique { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int Seen { get; set; }
    /// <summary>Pass as a highlight target (add label, tier, action as needed).</summary>
    public JsonElement? Highlight { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
