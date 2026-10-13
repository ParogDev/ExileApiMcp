using System.Diagnostics;

namespace ExileApiMcp.Hosting;

/// <summary>
/// Who this server is, for the HUD: one MCP server runs per agent session (Claude Code launches it from the session's
/// working directory, a worktree when the user works in several), so the identity comes from where it runs. The label
/// is the git branch of the working directory (worktree-aware: .git may be a file pointing at the real gitdir), the
/// folder name when there is no branch, or HEXILE_AGENT when set. The bridge tags our connection with it on connect
/// (session.hello, Bridge/BridgeClient.cs) so every guide line, lease and restart request says who asked.
/// On the first tool call the client's MCP roots are asked once (AdoptRoots): when they clearly name one other checkout
/// than the folder the server was started in, its branch becomes the label, Revision bumps and the bridge connections say
/// hello again. A server started outside any repo (the Claude desktop app starts user-scope servers in
/// C:\Windows\System32 and shares one among its conversations) never takes its label from roots. Identity says why.
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
    /// <summary>The name an incident answer carries: <see cref="Label"/>, made distinct by a tool call's _meta conversation id
    /// on a shared server ("Claude Desktop:&lt;8&gt;"). Only answers: deploys, leases and the bridge keep Label, which the
    /// background monitor sees too.</summary>
    public static string ResponseLabel => CallerIdentity.Current?.Label ?? (RequestSession.Value is { } m ? Distinct(_label, m.Id) : _label);
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

    /// <summary>
    /// The id this session's incident responses carry (IncidentResponse.SessionId), so its new answer replaces its own and
    /// not another session's with the same label. The client's own session id when it gives one: a shared-server caller's
    /// X-Hexile-Session header, or, for a stdio server, CLAUDE_CODE_SESSION_ID / CLAUDE_SESSION_ID in the environment Claude
    /// Code started it with (the id the incident hook uses). Else <see cref="Id"/>. Gap: Claude Desktop shares one stdio
    /// server among its conversations and sends no per-conversation id, so they answer as one session.
    /// </summary>
    public static string ResponseId => ClientSession ?? (CallerIdentity.Current is { } c ? IdOf(c) : _id);

    /// <summary>The client's own session id (the Claude Code session id the hooks see), or null when the client gives none:
    /// a shared-server caller's X-Hexile-Session header, else the tool call's _meta conversation id, else a stdio server's
    /// CLAUDE_CODE_SESSION_ID / CLAUDE_SESSION_ID. Incident answers and labels use it.</summary>
    // The env id wins over _meta: a Claude Code stdio server's env id is the one its hooks use, so one session never
    // answers under two ids.
    public static string? ClientSession => CallerIdentity.Current is { } c ? c.ClientSessionId : (Kind == "mcp" ? EnvSession().Id : null) ?? RequestSession.Value?.Id;

    /// <summary>The session id that is the same inside a tool call and in the background monitor: the HTTP caller's header,
    /// else a stdio server's env id, never a per-request _meta id. Deploy leases record it (ownerSession) and server beats
    /// carry it, so the monitor's Mine / Adopt see the same id a deploy was stamped with.</summary>
    public static string? ServerSession => CallerIdentity.Current is { } c ? c.ClientSessionId : Kind == "mcp" ? EnvSession().Id : null;

    /// <summary>Where a stdio server's client session id came from (env var name, or the request's _meta key), or null:
    /// mcp_status shows it, so whether the client passes its session id is visible, not assumed.</summary>
    public static string? ClientSessionSource => CallerIdentity.Current is { } c ? (c.ClientSessionId != null ? "header " + CallerIdentity.SessionHeader : null)
        : (Kind == "mcp" ? EnvSession().Name : null) ?? (RequestSession.Value is { } m ? "_meta " + m.Key : null);

    // A per-conversation id the client put in the tool call's _meta, for the request being handled (UseRequestMeta).
    private static readonly AsyncLocal<(string Key, string Id)?> RequestSession = new();

    /// <summary>The _meta keys the client sent on its first tool call (diagnostics in mcp_status: whether a shared server's
    /// client, such as the Claude desktop app, names the conversation per request).</summary>
    public static string[]? FirstMetaKeys { get; private set; }

    /// <summary>
    /// A shared stdio server (the Claude desktop app runs one for all its conversations, Code tab included) gets no
    /// per-conversation id from MCP itself. When the client puts one in a tool call's _meta (any string value whose key names
    /// a session or conversation id), the call runs as that session: incident answers carry it and the label becomes
    /// "Claude Desktop:&lt;short id&gt;". Deploy leases don't (ServerSession: the monitor can't see a per-request id).
    /// Without one, every conversation stays this server's one identity.
    /// </summary>
    public static void UseRequestMeta(IReadOnlyCollection<KeyValuePair<string, string?>>? meta)
    {
        if (CallerIdentity.Current != null) return;   // an HTTP caller names itself in headers
        // The first call that carries _meta at all (a call without any says nothing about what the client can send).
        if (FirstMetaKeys == null && meta is { Count: > 0 }) FirstMetaKeys = meta.Select(kv => kv.Key).ToArray();
        RequestSession.Value = meta is { Count: > 0 } ? PickMetaSession(meta) : null;
    }

    /// <summary>The _meta entry naming the conversation (pure: tests call it): a key whose last segment (after '/' or '.')
    /// is exactly sessionId / session_id / conversationId / conversation_id (any case; parentSessionId is not one), with a
    /// value of 8-100 chars holding at least 8 letters or digits.</summary>
    public static (string Key, string Id)? PickMetaSession(IEnumerable<KeyValuePair<string, string?>> meta)
    {
        foreach (var (k, v) in meta)
        {
            var last = k[(Math.Max(k.LastIndexOf('/'), k.LastIndexOf('.')) + 1)..].Replace("_", "").Replace("-", "");
            if ((last.Equals("sessionid", StringComparison.OrdinalIgnoreCase) || last.Equals("conversationid", StringComparison.OrdinalIgnoreCase))
                && IsSessionLike(v))
                return (k, v!.Trim());
        }
        return null;
    }

    /// <summary>Long enough to tell sessions apart: 8-100 chars after trimming, at least 8 of them letters or digits.</summary>
    public static bool IsSessionLike(string? v) => v?.Trim() is { Length: >= 8 and <= 100 } t && t.Count(char.IsAsciiLetterOrDigit) >= 8;

    // A method, not a static field: MakeLabel needs it while the static fields above are still being initialized.
    private static (string? Name, string? Id) EnvSession()
    {
        foreach (var n in new[] { "CLAUDE_CODE_SESSION_ID", "CLAUDE_SESSION_ID" })
            if (Environment.GetEnvironmentVariable(n) is { } v && !string.IsNullOrWhiteSpace(v)) return (n, v.Trim());
        return (null, null);
    }

    /// <summary>
    /// The label rule for sessions that would otherwise share one (the same rule as tools\incident-lib.ps1
    /// Get-HexileSessionLabel and tools\mcp-http-lib.ps1 Get-McpCaller): every session in the main checkout is on branch
    /// main, so with a known client session id its label becomes main:&lt;first 8 chars of the id&gt;. Other labels (worktree
    /// branches, HEXILE_AGENT) are kept: they already name one session.
    /// </summary>
    public static string Distinct(string label, string? sessionId)
    {
        // No static fields here: MakeLabel calls this while the statics are still being initialized.
        if (sessionId is not { Length: > 0 } || !(label.Equals("main", StringComparison.OrdinalIgnoreCase) || label.Equals("master", StringComparison.OrdinalIgnoreCase)
                                                  || label.Equals("Claude Desktop", StringComparison.Ordinal) || label.Equals("Claude", StringComparison.Ordinal)))
            return label;
        var shortId = new string(sessionId.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
        return shortId.Length == 0 ? label : $"{label}:{shortId}";
    }

    private static string Hash8(string s)
    {
        var h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
        return Convert.ToHexString(h, 0, 4).ToLowerInvariant();
    }
    public static string Kind { get; set; } = "mcp";
    /// <summary>Bumped when the identity changes (AdoptRoots); bridge connections re-send session.hello.</summary>
    public static int Revision { get; private set; }
    /// <summary>True until the roots question was settled once (not with HEXILE_AGENT, nor for a shared server's caller).
    /// <see cref="BeginRoots"/> claims it, so concurrent first calls ask once.</summary>
    public static bool NeedsRoots => !FromEnv && CallerIdentity.Current == null && Volatile.Read(ref _rootsState) == 0;
    private static int _rootsState;   // 0 not asked, 1 claimed (BeginRoots)

    /// <summary>Claims the one roots question of this process: true for the first caller only.</summary>
    public static bool BeginRoots() => Interlocked.CompareExchange(ref _rootsState, 1, 0) == 0;

    private static string MakeLabel(string? branch, string cwd) => Clip(Environment.GetEnvironmentVariable("HEXILE_AGENT") is { Length: > 0 } env ? env
        : branch is { Length: > 0 } b ? Distinct(b.StartsWith("claude/", StringComparison.Ordinal) ? b["claude/".Length..] : b, EnvSession().Id)
        : FolderLabel(cwd), 40);

    /// <summary>How this server's label was decided, for hud_sessions (identity) and mcp_status (this.identity): source env |
    /// cwd | roots | folder, why, the roots the client gave (only when they were asked) and who the client is.</summary>
    public sealed record IdentityDiagnostic(string Source, string Note, string[]? Roots = null, string? Client = null);

    private static readonly string StartNote = FromEnv ? "HEXILE_AGENT names this server"
        : _branch != null ? $"the branch of the folder it was started in ({_cwd})"
        : $"started outside any git checkout ({_cwd})";

    /// <summary>This server's identity decision (process-wide; a shared server's callers have their own headers instead).</summary>
    public static IdentityDiagnostic Identity { get; private set; } = new(FromEnv ? "env" : _branch != null ? "cwd" : "folder", StartNote + "; roots not asked yet");

    /// <summary>The MCP client whose roots may name the session: Claude Code starts one stdio server per session. Any other
    /// client (the Claude desktop app shares one server among its conversations) is never asked.</summary>
    public const string PerSessionClient = "claude-code";

    /// <summary>One root of the client's that is in a git checkout (Checkout: the top-level repo or worktree, never a submodule).</summary>
    public readonly record struct RootCheckout(string Path, string Checkout, string Branch);

    /// <summary>
    /// Whether this server may ask its client's roots at all: only a server started in a checkout (Claude Code starts a
    /// project's server in the project folder) by Claude Code, which runs one server per session. A server started outside
    /// any checkout is the Claude desktop app's (it starts user-scope servers in System32 and shares each among its
    /// conversations: 2026-10-12 one read another session's worktree), and any other client is unknown: neither is asked,
    /// so another conversation's roots are never even recorded. Returns null when it may ask, else why not.
    /// </summary>
    public static string? WhyNotAskRoots(bool inCheckout, string? clientName) =>
        !inCheckout ? "started outside any git checkout: the Claude desktop app shares such a server among its conversations, so roots can't name one session; not asked"
        : !string.Equals(clientName, PerSessionClient, StringComparison.OrdinalIgnoreCase) ? $"the client is {clientName ?? "unnamed"}, not Claude Code (one server per session), so its roots may not be this session's; not asked"
        : null;

    /// <summary>
    /// What the client's roots say about this session (pure: tests call it). Never a guess: a wrong label names another
    /// session's worktree on the card, and deploy_plugin would deploy from it.
    ///   - roots in two or more checkouts are ambiguous, the server's own among them (a session in the main checkout with a
    ///     worktree as an extra directory reads the same as one in the worktree with main as an extra): kept;
    ///   - roots in its own checkout only, or none in a checkout: nothing new;
    ///   - exactly one other checkout: adopted.
    /// Returns the root to adopt (null: keep the label) and why.
    /// </summary>
    public static (RootCheckout? Adopt, string Note) DecideRoots(string ownCheckout, IReadOnlyList<RootCheckout> roots)
    {
        static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        var checkouts = new List<RootCheckout>();
        foreach (var r in roots) if (!checkouts.Any(c => Same(c.Checkout, r.Checkout))) checkouts.Add(r);
        if (checkouts.Count == 0) return (null, "no root is in a git checkout: kept the branch of the folder it was started in");
        if (checkouts.Count > 1) return (null, $"roots name {checkouts.Count} checkouts ({string.Join(", ", checkouts.Select(c => c.Checkout))}): ambiguous, kept the branch of the folder it was started in");
        if (Same(checkouts[0].Checkout, ownCheckout)) return (null, "the roots are the checkout it was started in");
        return (checkouts[0], $"the client's roots name one checkout, {checkouts[0].Checkout}");
    }

    /// <summary>The checkout this server was started in, when it is in one (for WhyNotAskRoots / DecideRoots).</summary>
    public static string? StartCheckout()
    {
        try { return _branch != null && SessionRoot == null ? FindRepoTop(_cwd)?.Checkout : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// The client's roots (file:// URIs), asked once on the first tool call when WhyNotAskRoots allows: when they clearly
    /// name one other checkout than the folder this server was started in (Claude Code starts a project's server in the main
    /// checkout while the session works in a worktree), that checkout's branch becomes the label and the checkout
    /// deploy_plugin's (SessionRoot); cwd and branch follow. Otherwise nothing changes and Identity says why (DecideRoots).
    /// RepoRoot is never changed: machine-wide scripts keep coming from the checkout the server runs from. Revision bumps
    /// when the label changed, so the bridge connections say hello again. False when the label didn't change.
    /// </summary>
    public static bool AdoptRoots(IEnumerable<string> uris, string? client = null)
    {
        Interlocked.Exchange(ref _rootsState, 1);
        var given = uris.ToList();
        var roots = new List<RootCheckout>();
        foreach (var u in given)
        {
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
            var path = uri.LocalPath;
            try
            {
                if (Directory.Exists(path) && FindRepoTop(path) is { } c && BranchOfGitDir(c.GitDir) is { } branch) roots.Add(new RootCheckout(path, c.Checkout, branch));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        if (StartCheckout() is not { } own)
        {
            RootsUnavailable(WhyNotAskRoots(false, client));
            return false;
        }
        var (adopt, note) = DecideRoots(own, roots);
        Console.Error.WriteLine($"[Session] roots from {client ?? "the client"}: [{string.Join(", ", given)}] -> {(adopt is { } a ? "adopted " + a.Path : "kept '" + _label + "'")}: {note}");
        if (adopt is not { } r)
        {
            Identity = new("cwd", $"{StartNote}; {note}", given.ToArray(), client);
            return false;
        }
        SessionRoot = r.Path;
        _cwd = r.Path;
        _branch = r.Branch;
        _label = MakeLabel(r.Branch, r.Path);
        Revision++;   // the bridge connections say hello again with the new label
        Identity = new("roots", note, given.ToArray(), client);
        return true;
    }

    /// <summary>The checkout a stdio server's session works in when the client's roots clearly named another one than the
    /// folder the server was started in (AdoptRoots): deploy_plugin's "your checkout" (WorkingRepoRoot). Never set from an
    /// ambiguous or shared server's roots.</summary>
    public static string? SessionRoot { get; private set; }

    /// <summary>The roots question is settled without adopting (not asked, no roots capability, stateless HTTP, failed):
    /// stop trying, and say why in Identity when there is something to say.</summary>
    public static void RootsUnavailable(string? why = null)
    {
        Interlocked.Exchange(ref _rootsState, 1);
        if (why == null) return;
        Identity = Identity with { Note = $"{StartNote}; {why}" };
    }


    /// <summary>The scaffolding repo root (tools\restart-hud.ps1 lives there): HEXILE_REPO (run.cmd sets it to its own
    /// checkout), else walk up from the cwd. Never from the client's roots (another session's worktree on a shared server).</summary>
    public static string? RepoRoot()
    {
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
            return FindCheckout(start, skipSubmodules: false) is { } c ? BranchOfGitDir(c.GitDir) : null;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Session] branch unknown: {ex.Message}"); }
        return null;
    }

    /// <summary>The branch a git dir's HEAD names ("detached@abc1234" when detached), or null.</summary>
    private static string? BranchOfGitDir(string gitDir)
    {
        var head = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(head)) return null;
        var content = File.ReadAllText(head).Trim();
        if (content.StartsWith("ref: refs/heads/", StringComparison.Ordinal)) return content["ref: refs/heads/".Length..];
        return content.Length >= 7 ? "detached@" + content[..7] : null;
    }

    /// <summary>The top-level checkout (a repo or a worktree, never a submodule) at or above a path, for telling sessions'
    /// checkouts apart: a root inside Plugins\X belongs to the checkout holding it, not to the plugin's own repo.</summary>
    private static (string Checkout, string GitDir)? FindRepoTop(string start) => FindCheckout(start, skipSubmodules: true);

    /// <summary>The nearest checkout at or above <paramref name="start"/>: the folder holding .git and its git dir (a worktree
    /// or submodule's .git file says "gitdir: &lt;path&gt;", relative to that folder or absolute). With skipSubmodules, a .git
    /// file pointing into a repo's modules\ folder is passed over for the superproject above it. Null outside any.</summary>
    private static (string Checkout, string GitDir)? FindCheckout(string start, bool skipSubmodules)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir != null; dir = dir.Parent)
        {
            var dotGit = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dotGit)) return (dir.FullName.TrimEnd('\\', '/'), dotGit);
            if (!File.Exists(dotGit)) continue;
            var line = File.ReadAllText(dotGit).Trim();
            if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) continue;
            var gitDir = Path.GetFullPath(Path.Combine(dir.FullName, line["gitdir:".Length..].Trim()));
            if (skipSubmodules && gitDir.Replace('/', '\\').Contains(@"\modules\", StringComparison.OrdinalIgnoreCase)) continue;
            return (dir.FullName.TrimEnd('\\', '/'), gitDir);
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
