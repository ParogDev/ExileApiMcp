using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

// The bridge's hud.reload_plugin / hud.reload_status replies as reload_plugin reads them (Tools/HudDevTools.cs ReloadAsync).
// Read with Newtonsoft (the bridge reply is a JToken); mirrors of bridge ReloadQueuedResponse, ReloadStatusResponse and
// ReloadResultDto in Bridge/BridgeContract.cs, so a renamed or retyped field fails the contract test, named at the field.
// Fields a newer bridge adds pass through Extra.

/// <summary>hud.reload_plugin: queued, or refused with error + message (e.g. dll_locked).</summary>
public sealed class ReloadQueuedReply
{
    public bool Queued { get; set; }
    /// <summary>The plugin folder the bridge resolved the name to.</summary>
    public string? Plugin { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

/// <summary>hud.reload_status: the last reload's result and the plugins still queued.</summary>
public sealed class ReloadStatusReply
{
    public ReloadResultReply? Last { get; set; }
    public List<string>? Pending { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

/// <summary>One reload's result (bridge ReloadResultDto). Null fields: not finished yet, or a bridge build before them.</summary>
public sealed class ReloadResultReply
{
    public string Plugin { get; set; } = "";
    public DateTime QueuedAt { get; set; }
    public DateTime StartedAt { get; set; }
    /// <summary>Null while the compile runs.</summary>
    public DateTime? FinishedAt { get; set; }
    public long? DurationMs { get; set; }
    public bool? Ok { get; set; }
    /// <summary>Compiler output, or not_loaded / not_initialised / init_failed (newer bridges) with <see cref="Message"/>.</summary>
    public string? Error { get; set; }
    public string? Message { get; set; }
    /// <summary>How the reload ran (the HUD's own reload, or the bridge's fallback).</summary>
    public string? Via { get; set; }
    /// <summary>Plugin instances found after the reload (newer bridges).</summary>
    public long? Instances { get; set; }
    /// <summary>The new instance initialised (newer bridges; null on older ones, which don't check).</summary>
    public bool? Initialized { get; set; }
    /// <summary>PluginBridge methods still answering from the unloaded instance.</summary>
    public List<string>? StaleBridgeMethods { get; set; }
    public List<string> Warnings { get; set; } = [];
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}
