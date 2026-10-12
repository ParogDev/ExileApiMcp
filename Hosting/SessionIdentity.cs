using System.Diagnostics;

namespace ExileApiMcp.Hosting;

/// <summary>
/// Who this server is, for the HUD: one MCP server runs per agent session (Claude Code launches it from the session's
/// working directory, a worktree when the user works in several), so the identity comes from where it runs. The label
/// is the git branch of the working directory (worktree-aware: .git may be a file pointing at the real gitdir), the
/// folder name when there is no branch, or HEXILE_AGENT when set. The bridge tags our connection with it on connect
/// (session.hello, Bridge/BridgeClient.cs) so every guide line, lease and restart request says who asked.
/// A server started outside any repo (the desktop app starts user-scope servers in C:\Windows\System32) learns its
/// session's project from the client's MCP roots on the first tool call (AdoptRoots): the label then comes from that
/// folder's branch, Revision bumps and the bridge connections say hello again. Otherwise fixed for the process.
/// </summary>
public static class SessionIdentity
{
    private static readonly bool FromEnv = Environment.GetEnvironmentVariable("HEXILE_AGENT") is { Length: > 0 };
    // The process's own identity. On the shared HTTP server a request that names its caller (Hosting/CallerIdentity.cs)
    // answers with the caller's instead, so whatever reads Label / Id / Branch / Cwd while handling it names that agent.
    private static string _cwd = Environment.CurrentDirectory;
    private static string? _branch = ReadBranch(_cwd);
    private static string _label = MakeLabel(_branch, _cwd);
    public static string Cwd => CallerIdentity.Current is { } c ? c.Cwd ?? _cwd : _cwd;
    public static string? Branch => CallerIdentity.Current is { } c ? c.Branch : _branch;
    public static string Label => CallerIdentity.Current?.Label ?? _label;
    /// <summary>The session's process: the supervisor (Supervisor/README.md) when there is one, so a swap to a newly deployed
    /// build stays the same session to the HUD; else this process.</summary>
    public static readonly int Pid = int.TryParse(Environment.GetEnvironmentVariable("HEXILE_SESSION_PID"), out var sp) && sp > 0 ? sp : Environment.ProcessId;
    private static readonly string _id = $"{_label}#{Pid}";
    /// <summary>Stable for the life of the session; a reconnect after a HUD restart or a server swap re-identifies as the same session.
    /// A caller of the shared HTTP server is its own session: its label at this server's pid.</summary>
    public static string Id => CallerIdentity.Current is { } c ? IdOf(c) : _id;
    /// <summary>A caller's session id: its label and checkout, not this server's pid, so the same agent stays the same session
    /// to the HUD when the shared server stops after being idle and the next call starts a new one (its leases and guide card
    /// carry over like after a hot swap), and two checkouts on the same branch stay two sessions.</summary>
    public static string IdOf(CallerIdentity c) => $"{c.Label}#http-{Hash8(c.Cwd?.Replace('/', '\\').TrimEnd('\\') ?? c.Label)}";

    private static string Hash8(string s)
    {
        var h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
        return Convert.ToHexString(h, 0, 4).ToLowerInvariant();
    }
    public static string Kind { get; set; } = "mcp";
    /// <summary>Bumped when the identity changes (AdoptRoots); bridge connections re-send session.hello.</summary>
    public static int Revision { get; private set; }
    /// <summary>The project folder learned from the client's roots, when the cwd had none.</summary>
    public static string? ProjectRoot { get; private set; }
    /// <summary>True until the client's roots were asked once (not with HEXILE_AGENT, nor for a shared server's caller). Asked
    /// even when the cwd has a branch: Claude Code starts a project's stdio server in the project folder (the main checkout)
    /// while the session works in a worktree, and every such server would otherwise read "main".</summary>
    public static bool NeedsRoots => !FromEnv && CallerIdentity.Current == null && ProjectRoot == null && !_rootsAsked;
    private static bool _rootsAsked;

    private static string MakeLabel(string? branch, string cwd) => Clip(Environment.GetEnvironmentVariable("HEXILE_AGENT") is { Length: > 0 } env ? env
        : branch is { Length: > 0 } b ? (b.StartsWith("claude/", StringComparison.Ordinal) ? b["claude/".Length..] : b)
        : FolderLabel(cwd), 40);

    /// <summary>
    /// The client's roots (file:// URIs): the first that is in a git checkout becomes this session's project, its branch
    /// the label. Called once; false when nothing changed.
    /// </summary>
    /// <remarks>A server whose own cwd is in a checkout already (Claude Code starts it in the project folder, the main
    /// checkout, while the session works in a worktree) takes only the name from a root in another checkout: label, branch
    /// and cwd, not ProjectRoot, so RepoRoot keeps naming the checkout it runs from (HEXILE_REPO) for machine-wide scripts.
    /// A root in its own checkout changes nothing.</remarks>
    public static bool AdoptRoots(IEnumerable<string> uris)
    {
        _rootsAsked = true;
        var hadBranch = _branch != null;
        var own = hadBranch ? FindCheckout(_cwd)?.Checkout : null;
        foreach (var u in uris)
        {
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
            var path = uri.LocalPath;
            if (!Directory.Exists(path) || ReadBranch(path) is not { } branch) continue;
            if (own != null && string.Equals(FindCheckout(path)?.Checkout, own, StringComparison.OrdinalIgnoreCase)) continue;   // its own checkout: nothing new
            if (!hadBranch) ProjectRoot = path;
            else SessionRoot = path;
            _cwd = path;
            _branch = branch;
            _label = MakeLabel(branch, path);
            Revision++;
            Console.Error.WriteLine($"[Session] identified by the client's root {path}: {_label}");
            return true;
        }
        return false;
    }

    /// <summary>The checkout a stdio server's session works in when the client's roots named another one than the folder
    /// the server was started in (AdoptRoots): deploy_plugin's "your checkout" (WorkingRepoRoot).</summary>
    public static string? SessionRoot { get; private set; }

    /// <summary>Roots could not be asked (no roots capability, stateless HTTP): stop trying.</summary>
    public static void RootsUnavailable() => _rootsAsked = true;

    /// <summary>The scaffolding repo root (tools\restart-hud.ps1 lives there): the session's project from its roots, then
    /// HEXILE_REPO (run.cmd sets it to its own checkout), else walk up from the cwd.</summary>
    public static string? RepoRoot()
    {
        for (var dir = ProjectRoot == null ? null : new DirectoryInfo(ProjectRoot); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "restart-hud.ps1"))) return dir.FullName;
        if (Environment.GetEnvironmentVariable("HEXILE_REPO") is { Length: > 0 } env && File.Exists(Path.Combine(env, "tools", "restart-hud.ps1")))
            return Path.GetFullPath(env);
        for (var dir = new DirectoryInfo(Cwd); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "restart-hud.ps1"))) return dir.FullName;
        return null;
    }

    /// <summary>
    /// The checkout the session edits, for deploy_plugin: on the shared HTTP server the caller's own (its cwd header); for a
    /// stdio server started in the main checkout, the worktree its client's roots named (<see cref="SessionRoot"/>); else
    /// <see cref="RepoRoot"/>. Machine-wide scripts (restart-hud.ps1, deploy.ps1) keep coming from RepoRoot, the main checkout
    /// HEXILE_REPO names, never from a caller's worktree.
    /// </summary>
    public static string? WorkingRepoRoot()
    {
        if ((CallerIdentity.Current != null ? CallerIdentity.Current.Cwd : SessionRoot) is { } callerCwd && Directory.Exists(callerCwd))
            for (var dir = new DirectoryInfo(callerCwd); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "tools", "restart-hud.ps1"))) return dir.FullName;
        return RepoRoot();
    }

    /// <summary>session.hello for a bridge connection: the process's identity, or <paramref name="caller"/>'s for the
    /// connections the shared HTTP server keeps per caller (Bridge/BridgeRegistry.cs). role: the shared server's own identity
    /// (the control center) and infra callers (the F8 route-dispatcher) are "infra", which the HUD doesn't count as agents.</summary>
    public static Newtonsoft.Json.Linq.JObject HelloParams(CallerIdentity? caller = null) => caller == null
        ? new()
        {
            ["id"] = _id, ["label"] = _label, ["branch"] = _branch, ["cwd"] = _cwd, ["pid"] = Pid, ["kind"] = Kind,
            ["role"] = Kind == "mcp-http" ? "infra" : "agent",
            ["mcp"] = ServerBuild.HelloInfo(),
        }
        : new()
        {
            ["id"] = IdOf(caller), ["label"] = caller.Label, ["branch"] = caller.Branch, ["cwd"] = caller.Cwd ?? _cwd, ["pid"] = Pid, ["kind"] = Kind,
            ["role"] = caller.Infra ? "infra" : "agent",
            ["mcp"] = ServerBuild.HelloInfo(),
        };

    /// <summary>The git branch of a folder (worktree-aware), for naming another session's checkout.</summary>
    public static string? BranchOf(string dir) => ReadBranch(dir);

    private static string? ReadBranch(string start)
    {
        try
        {
            if (FindCheckout(start) is not { } c) return null;
            var head = Path.Combine(c.GitDir, "HEAD");
            if (!File.Exists(head)) return null;
            var content = File.ReadAllText(head).Trim();
            if (content.StartsWith("ref: refs/heads/", StringComparison.Ordinal)) return content["ref: refs/heads/".Length..];
            return content.Length >= 7 ? "detached@" + content[..7] : null;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Session] branch unknown: {ex.Message}"); }
        return null;
    }

    /// <summary>The nearest checkout at or above <paramref name="start"/>: the folder holding .git and its git dir (a worktree
    /// or submodule's .git file says "gitdir: &lt;path&gt;", relative to that folder or absolute). Null outside any.</summary>
    private static (string Checkout, string GitDir)? FindCheckout(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir != null; dir = dir.Parent)
        {
            var dotGit = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dotGit)) return (dir.FullName.TrimEnd('\\', '/'), dotGit);
            if (!File.Exists(dotGit)) continue;
            var line = File.ReadAllText(dotGit).Trim();
            if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                return (dir.FullName.TrimEnd('\\', '/'), Path.GetFullPath(Path.Combine(dir.FullName, line["gitdir:".Length..].Trim())));
        }
        return null;
    }

    /// <summary>
    /// The label of a server outside any git repo: its folder name, unless that folder says nothing about the session.
    /// Claude Desktop starts its servers in C:\Windows\System32 (a label "System32" meant nothing to the user); a drive
    /// root, the user folder or Program Files are no better.
    /// </summary>
    private static string FolderLabel(string cwd)
    {
        try
        {
            var full = Path.GetFullPath(cwd).TrimEnd('\\', '/');
            bool Under(Environment.SpecialFolder f)
            {
                var d = Environment.GetFolderPath(f).TrimEnd('\\');
                return d.Length > 0 && (full.Equals(d, StringComparison.OrdinalIgnoreCase) || full.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase));
            }
            if (Under(Environment.SpecialFolder.Windows)) return "Claude Desktop";
            if (full.Length <= 3 || Under(Environment.SpecialFolder.ProgramFiles) || Under(Environment.SpecialFolder.ProgramFilesX86)
                || full.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return "Claude";
            return Path.GetFileName(full) is { Length: > 0 } dir ? dir : "Claude";
        }
        catch { return "Claude"; }
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
