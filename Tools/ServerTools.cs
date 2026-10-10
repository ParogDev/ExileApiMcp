using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ExileApiMcp.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Tools;

/// <summary>
/// This server's own builds. Every session's server runs under a supervisor (Supervisor/README.md) that follows the
/// deployed build (%LOCALAPPDATA%\ExileApiMcp\current.json): a deploy swaps each server without restarting its session and
/// without cutting a running call, so it needs no coordination. mcp_deploy deploys (MCP\ExileApiMcp\deploy.ps1);
/// mcp_status shows the deployed build and which servers run it.
/// </summary>
[McpServerToolType]
public static class ServerTools
{
    [McpServerTool(Name = "mcp_status", Title = "Which MCP server build runs where", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ServerStatus))]
    [Description("The deployed ExileApiMcp build (version, commit, who deployed it and when), this server's build and mode, and " +
                 "every supervised server on this machine (one per agent session, plus the shared HTTP server) with the build " +
                 "its worker runs and old workers still finishing calls after a swap. mode unsupervised = started by an old " +
                 "launcher: that session needs a restart to follow deployments. After merging an MCP change, mcp_deploy rolls it out.")]
    public static CallToolResult McpStatus()
    {
        var s = Status();
        return Dto.Result(s, StatusText(s));
    }

    [McpServerTool(Name = "mcp_deploy", Title = "Roll out an MCP server build to every session", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ServerDeployResult))]
    [Description("Build an ExileApiMcp commit and deploy it: every supervised server (each agent session's, and the shared HTTP " +
                 "server) switches to it within seconds, without restarting its session; calls already running finish on the old " +
                 "build. Default commit: what the scaffolding repo's origin/main points MCP/ExileApiMcp at, so do it after the " +
                 "MCP PR is merged and the submodule bump is on main. Takes ~30 s (a build). Part of the flow for every MCP change.")]
    public static async Task<CallToolResult> McpDeploy(
        [Description("Why, in a few words (deploy log and status), e.g. 'tool error messages (#117)'")] string reason,
        [Description("Commit or branch of the ExileApiMcp repo to deploy instead of the released one (testing a PR build: say so in reason)")] string? @ref = null,
        [Description("Rebuild and re-point even when that commit is already deployed")] bool force = false,
        CancellationToken ct = default)
    {
        var repo = SessionIdentity.RepoRoot() ?? throw new McpException("The scaffolding repo isn't known to this server (HEXILE_REPO unset and no tools\\restart-hud.ps1 above the cwd): run MCP\\ExileApiMcp\\deploy.ps1 from a checkout.");
        var script = Path.Combine(repo, "MCP", "ExileApiMcp", "deploy.ps1");
        if (!File.Exists(script)) throw new McpException($"{script} not found: this checkout's MCP submodule predates deploy.ps1 (update it: git submodule update MCP/ExileApiMcp).");
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = repo,
            RedirectStandardInput = true,   // closed below: git and dotnet in the script must not inherit our (always open) stdin
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Who", SessionIdentity.Label, "-Reason", reason }) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(@ref)) { psi.ArgumentList.Add("-Ref"); psi.ArgumentList.Add(@ref); }
        if (force) psi.ArgumentList.Add("-Force");

        using var proc = Process.Start(psi) ?? throw new McpException("Could not start powershell.exe.");
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(8));
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new McpException("deploy.ps1 gave no result within 8 minutes.");
        }
        var output = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        if ((await stderr).Trim() is { Length: > 0 } err) output.Add("stderr: " + err);
        var code = proc.ExitCode;
        var current = code == 0 && output.Any(l => l.StartsWith("already current", StringComparison.Ordinal));
        var result = new ServerDeployResult
        {
            Ok = code == 0, ExitCode = code, Output = output,
            Status = code switch { 0 => current ? "current" : "deployed", 2 => "bad_ref", 3 => "build_failed", 4 => "busy", _ => "error" },
        };
        if (code == 0)
        {
            // Supervisors poll every 2 s and a new worker answers within a few: look again shortly.
            if (!current) await Task.Delay(TimeSpan.FromSeconds(8), ct);
            result.After = Status();
            result.Note = result.After.Behind == 0
                ? "Every supervised server runs it. This session's server is " + (result.After.This.OnDeployed ? "on it too." : $"{result.After.This.Mode}: it follows deployments only under the supervisor.")
                : $"{result.After.Behind} supervised server(s) not on it yet: check mcp_status again (lastSwap says why if one failed).";
        }
        var text = $"{result.Status} (exit {code})\n{string.Join("\n", output)}" + (result.After != null ? "\n\n" + StatusText(result.After) : "");
        var call = Dto.Result(result, text);
        if (code != 0) call.IsError = true;
        return call;
    }

    internal static ServerStatus Status()
    {
        var d = ServerBuild.Deployed();
        var deployedSha = d?["sha"]?.ToString();
        var s = new ServerStatus
        {
            Deployed = d == null ? null : new ServerDeployed
            {
                Version = d["version"]?.ToString(), Sha = ServerBuild.Short(deployedSha), Dir = d["dir"]?.ToString(), By = d["by"]?.ToString(),
                Reason = d["reason"]?.ToString() is { Length: > 0 } r ? r : null,
                DeployedAt = DateTimeOffset.TryParse(d["deployedAt"]?.ToString(), out var at) ? at : null,
            },
            This = new ServerSelf
            {
                Version = McpSetup.Version, Sha = ServerBuild.Short(ServerBuild.Sha), Mode = ServerBuild.Mode,
                WorkerPid = Environment.ProcessId, SessionPid = SessionIdentity.Pid, Label = SessionIdentity.Label,
                OnDeployed = deployedSha != null && ServerBuild.Sha == deployedSha,
            },
        };
        if (Directory.Exists(ServerBuild.SupervisorsDir))
            foreach (var f in Directory.GetFiles(ServerBuild.SupervisorsDir, "*.json"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out var pid) || !Alive(pid)) continue;   // a dead supervisor's file is stale
                try
                {
                    var sup = JsonSerializer.Deserialize<ServerSupervisor>(File.ReadAllText(f), Dto.Options);
                    if (sup == null) continue;
                    sup.Label = sup.Cwd != null ? LabelOf(sup.Cwd) : null;
                    s.Supervisors.Add(sup);
                }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
        s.Supervisors = s.Supervisors.OrderBy(x => x.Transport).ThenBy(x => x.Label).ToList();
        foreach (var sup in s.Supervisors.Where(x => x.Mode == "supervised"))
            if (deployedSha != null && sup.Workers?.FirstOrDefault(w => w.State == "active") is { } a && a.Sha != deployedSha) s.Behind++;
        s.Draining = s.Supervisors.Sum(x => x.Workers?.Count(w => w.State == "draining") ?? 0);
        return s;
    }

    private static string LabelOf(string cwd)
    {
        var b = SessionIdentity.BranchOf(cwd);
        return b is { Length: > 0 } ? (b.StartsWith("claude/", StringComparison.Ordinal) ? b["claude/".Length..] : b) : Path.GetFileName(cwd.TrimEnd('\\', '/'));
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

    private static string StatusText(ServerStatus s)
    {
        var lines = new List<string>
        {
            s.Deployed == null ? "Deployed: nothing yet (supervised servers run their checkout's build until the first mcp_deploy)."
                : $"Deployed: {s.Deployed.Version} ({s.Deployed.Sha}) by {s.Deployed.By} at {s.Deployed.DeployedAt:yyyy-MM-dd HH:mm} UTC" + (s.Deployed.Reason != null ? $" - {s.Deployed.Reason}" : ""),
            $"This server: {s.This.Version}" + (s.This.Sha != null ? $" ({s.This.Sha})" : " (local build)") + $", {s.This.Mode}" + (s.This.OnDeployed ? ", on the deployed build" : ""),
        };
        foreach (var sup in s.Supervisors)
        {
            var active = sup.Workers?.FirstOrDefault(w => w.State == "active");
            var draining = sup.Workers?.Where(w => w.State == "draining").ToList() ?? [];
            lines.Add($"- {sup.Label ?? sup.Cwd} [{sup.Transport}{(sup.Port != null ? $" :{sup.Port}" : "")}, {sup.Mode}]: " +
                      (active != null ? $"{active.Version ?? "local"}{(active.Sha != null ? $" ({ServerBuild.Short(active.Sha)})" : "")}" : "no active worker") +
                      (draining.Count > 0 ? $"; finishing {draining.Sum(w => (w.InFlight ?? 0) + (w.Connections ?? 0))} call(s) on {string.Join(", ", draining.Select(w => w.Version ?? "local"))}" : ""));
        }
        if (s.Supervisors.Count == 0) lines.Add("No supervised servers (sessions started before the supervisor run unsupervised until restarted).");
        if (s.Behind > 0) lines.Add($"{s.Behind} supervised server(s) not on the deployed build yet.");
        return string.Join("\n", lines);
    }
}
