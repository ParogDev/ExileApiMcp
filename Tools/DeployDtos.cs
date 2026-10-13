using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of deploy_plugin: what a worktree session edited, what the main checkout (the folder the HUD compiles)
// is at, and what the HUD runs after the reload. Every type keeps unknown fields in Extra.

/// <summary>deploy_plugin: one action on one plugin.</summary>
public sealed class DeployResult
{
    public string Plugin { get; set; } = "";
    /// <summary>deploy | restore | status.</summary>
    public string Action { get; set; } = "";
    /// <summary>deployed | restored | unchanged | status | refused | failed | held_by_outage.</summary>
    public string Status { get; set; } = "";
    /// <summary>held_by_outage: the deploy needs a HUD restart (Whats An AI Bridge) and the user's planned restart is open.</summary>
    public string? Error { get; set; }
    /// <summary>The outage that holds it (Error held_by_outage).</summary>
    public OutageHold? HeldBy { get; set; }
    public bool Ok { get; set; }
    /// <summary>The HUD game(s) whose Plugins\Source junction points at the HUD source's folder.</summary>
    public List<string> Games { get; set; } = [];
    /// <summary>The HUD source tree: the checkout the HUD's junction for this folder points into (Hud/HudSource.cs).</summary>
    public string? Source { get; set; }
    /// <summary>hud-live | main-checkout | worktree | other.</summary>
    public string? SourceTree { get; set; }
    /// <summary>This session's copy (the worktree's submodule).</summary>
    public DeployCheckout? Edited { get; set; }
    /// <summary>The HUD source's copy, which the HUD compiles (named "main" from before hud-live).</summary>
    public DeployCheckout? Main { get; set; }
    /// <summary>The HUD source's copy before this deploy (what restore returns to).</summary>
    public DeployCheckout? Before { get; set; }
    /// <summary>The submodule commit the HUD source records: when Main differs, it shows the plugin as modified.</summary>
    public string? Pointer { get; set; }
    /// <summary>Who deployed what (the state kept for restore), null when nothing is deployed.</summary>
    public DeployStateInfo? Deployed { get; set; }
    /// <summary>The reload (or HUD restart, for the bridge) that made the HUD compile it.</summary>
    public DeployReload? Reload { get; set; }
    /// <summary>What the HUD runs now, in words: the commit, and whether that is what this session edited.</summary>
    public string? HudRuns { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string? Note { get; set; }
    /// <summary>The deploy's lease on the incident board (key deploy:&lt;folder&gt;): owner, heartbeat, orphaned.</summary>
    public IncidentView? Incident { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DeployCheckout
{
    public string Path { get; set; } = "";
    public string Commit { get; set; } = "";
    /// <summary>null: detached HEAD.</summary>
    public string? Branch { get; set; }
    public string? Subject { get; set; }
    public DateTimeOffset? CommittedAt { get; set; }
    /// <summary>Tracked files with uncommitted changes (never deployed, never overwritten).</summary>
    public List<string>? Uncommitted { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DeployStateInfo
{
    public string Commit { get; set; } = "";
    public string? Branch { get; set; }
    public string? Subject { get; set; }
    public string By { get; set; } = "";
    public string From { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? BeforeBranch { get; set; }
    public string BeforeCommit { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DeployReload
{
    /// <summary>reload_plugin | hud_restart | skipped.</summary>
    public string Kind { get; set; } = "";
    public string? Game { get; set; }
    public bool Ok { get; set; }
    /// <summary>The reload or restart tool's own result text.</summary>
    public string? Result { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
