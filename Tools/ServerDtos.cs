using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the server tools (mcp_status, mcp_deploy): the deployed build, this server, and every supervised
// server on this machine with the build each runs (Hosting/ServerBuild.cs, Supervisor/README.md). Unknown fields stay in Extra.

/// <summary>mcp_status: which build is deployed and who runs what.</summary>
public sealed class ServerStatus
{
    public ServerDeployed? Deployed { get; set; }
    public ServerSelf This { get; set; } = new();
    public List<ServerSupervisor> Supervisors { get; set; } = new();
    /// <summary>Supervised servers whose active worker isn't on the deployed build yet (a swap under way, or one that failed: see lastSwap).</summary>
    public int Behind { get; set; }
    /// <summary>Old workers still finishing their calls after a swap.</summary>
    public int Draining { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ServerDeployed
{
    public string? Version { get; set; }
    public string? Sha { get; set; }
    public string? Dir { get; set; }
    public DateTimeOffset? DeployedAt { get; set; }
    public string? By { get; set; }
    public string? Reason { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ServerSelf
{
    public string Version { get; set; } = "";
    public string? Sha { get; set; }
    /// <summary>supervised | local | unsupervised (an old launcher: a session restart puts it under the supervisor).</summary>
    public string Mode { get; set; } = "";
    public int WorkerPid { get; set; }
    public int SessionPid { get; set; }
    public string Label { get; set; } = "";
    public bool OnDeployed { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One supervisor's status file (supervisors\&lt;pid&gt;.json, written by the supervisor).</summary>
public sealed class ServerSupervisor
{
    public int Pid { get; set; }
    /// <summary>stdio (one client session) | http (the shared server, tools\mcp-call.ps1 and the control center).</summary>
    public string? Transport { get; set; }
    public int? Port { get; set; }
    public string? Mode { get; set; }
    public string? Cwd { get; set; }
    /// <summary>The session's label to the HUD (its checkout's branch).</summary>
    public string? Label { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public List<ServerWorker>? Workers { get; set; }
    public JsonElement? LastSwap { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ServerWorker
{
    public int Seq { get; set; }
    public int? Pid { get; set; }
    /// <summary>starting | active | draining | exited</summary>
    public string? State { get; set; }
    public string? Version { get; set; }
    public string? Sha { get; set; }
    public string? Source { get; set; }
    public int? InFlight { get; set; }
    public int? Connections { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>mcp_deploy: what deploy.ps1 did, then the status a few seconds later (who switched).</summary>
public sealed class ServerDeployResult
{
    public bool Ok { get; set; }
    /// <summary>deployed | current | bad_ref | build_failed | busy | error</summary>
    public string Status { get; set; } = "";
    public int ExitCode { get; set; }
    public List<string> Output { get; set; } = new();
    public ServerStatus? After { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
