using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// "Not now" vs "broken" (Bridge/HudState.cs): what a failed, timed-out or -32010 bridge call means, from the bridge's
// hud-state.json plus the game and HUD processes. Carried in the error result of any tool whose bridge call failed that way
// ({error: hud_not_now | hud_broken, message, availability}), and in hud_restart's mode=user result.

/// <summary>Why the HUD can't answer right now, and whether that is normal (notNow) or a fault (broken).</summary>
public sealed class HudAvailability
{
    /// <summary>notNow (normal, comes back by itself) | broken (needs someone).</summary>
    public string State { get; set; } = "notNow";
    /// <summary>loading | starting | restarting | user-restart | menu | stuckLoading | stuckStarting | gameGone | hudGone | frozen.</summary>
    public string Reason { get; set; } = "";
    /// <summary>The bridge's phase from hud-state.json (starting | menu | loading | ready | restarting | user-restart | closed).</summary>
    public string? Phase { get; set; }
    /// <summary>Seconds in the current phase.</summary>
    public double? SinceSec { get; set; }
    /// <summary>loading: the area the load started from.</summary>
    public string? From { get; set; }
    /// <summary>Roughly when it should be back, seconds from now (last load / typical startup); null when unknown.</summary>
    public double? ExpectedBackSec { get; set; }
    /// <summary>What to wait with instead of retrying blind, e.g. "pubsub_wait kinds=area.ready".</summary>
    public string? WaitWith { get; set; }
    /// <summary>The broken link or the normal reason, in one sentence.</summary>
    public string Detail { get; set; } = "";
    /// <summary>user-restart: the outage incident on the board.</summary>
    public string? OutageId { get; set; }
    public string? Game { get; set; }
    /// <summary>For the agent: who tells the user (the bridge posts outage and long-load toasts itself; loading / starting / menu need no toast).</summary>
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool NotNow => State == "notNow";
}

/// <summary>The error result of a tool whose bridge call hit a HUD that can't answer (McpSetup's call filter writes it).</summary>
public sealed class HudUnavailableResult
{
    /// <summary>hud_not_now | hud_broken.</summary>
    public string Error { get; set; } = "";
    public string Message { get; set; } = "";
    public HudAvailability? Availability { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
