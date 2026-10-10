using System.Diagnostics;

namespace ExileApiMcp.Hosting;

/// <summary>
/// Who this server is, for the HUD: one MCP server runs per agent session (Claude Code launches it from the session's
/// working directory, a worktree when the user works in several), so the identity comes from where it runs. The label
/// is the git branch of the working directory (worktree-aware: .git may be a file pointing at the real gitdir), the
/// folder name when there is no branch, or HEXILE_AGENT when set. The bridge tags our connection with it on connect
/// (session.hello, Bridge/BridgeClient.cs) so every guide line, lease and restart request says who asked.
/// Process-wide and immutable: the server is stateless per request, but it is one process for one session.
/// </summary>
public static class SessionIdentity
{
    public static readonly string Cwd = Environment.CurrentDirectory;
    public static readonly string? Branch = ReadBranch(Cwd);
    public static readonly string Label = Clip(Environment.GetEnvironmentVariable("HEXILE_AGENT") is { Length: > 0 } env ? env
        : Branch is { Length: > 0 } b ? (b.StartsWith("claude/", StringComparison.Ordinal) ? b["claude/".Length..] : b)
        : Path.GetFileName(Cwd.TrimEnd('\\', '/')) is { Length: > 0 } dir ? dir : "agent", 40);
    public static readonly int Pid = Environment.ProcessId;
    /// <summary>Stable for the life of this process; a reconnect after a HUD restart re-identifies as the same session.</summary>
    public static readonly string Id = $"{Label}#{Pid}";
    public static string Kind { get; set; } = "mcp";

    /// <summary>The scaffolding repo root (tools\restart-hud.ps1 lives there): run.cmd sets HEXILE_REPO; else walk up from the cwd.</summary>
    public static string? RepoRoot()
    {
        if (Environment.GetEnvironmentVariable("HEXILE_REPO") is { Length: > 0 } env && File.Exists(Path.Combine(env, "tools", "restart-hud.ps1")))
            return Path.GetFullPath(env);
        for (var dir = new DirectoryInfo(Cwd); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "restart-hud.ps1"))) return dir.FullName;
        return null;
    }

    public static Newtonsoft.Json.Linq.JObject HelloParams() => new()
    {
        ["id"] = Id, ["label"] = Label, ["branch"] = Branch, ["cwd"] = Cwd, ["pid"] = Pid, ["kind"] = Kind,
    };

    private static string? ReadBranch(string start)
    {
        try
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");
                string? gitDir = null;
                if (Directory.Exists(dotGit)) gitDir = dotGit;
                else if (File.Exists(dotGit))
                {
                    // A worktree or submodule: "gitdir: <path>" (relative to the folder holding .git, or absolute).
                    var line = File.ReadAllText(dotGit).Trim();
                    if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                        gitDir = Path.GetFullPath(Path.Combine(dir.FullName, line["gitdir:".Length..].Trim()));
                }
                if (gitDir == null) continue;
                var head = Path.Combine(gitDir, "HEAD");
                if (!File.Exists(head)) return null;
                var content = File.ReadAllText(head).Trim();
                if (content.StartsWith("ref: refs/heads/", StringComparison.Ordinal)) return content["ref: refs/heads/".Length..];
                return content.Length >= 7 ? "detached@" + content[..7] : null;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Session] branch unknown: {ex.Message}"); }
        return null;
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
