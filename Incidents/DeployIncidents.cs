using System.Diagnostics;
using ExileApiMcp.Hosting;
using ExileApiMcp.Hud;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Incidents;

/// <summary>
/// deploy_plugin deploys as leases on the incident board (kind deploy, key deploy:&lt;plugin folder&gt;): the owning server
/// (ownerPid = the session's process, the supervisor when there is one) beats every 30 s in a sidecar file, so a deploy
/// whose owner is gone, or silent for 10 min, is "orphaned": any session may restore it or deploy over it without force.
/// A HUD-source submodule (the HUD junctions' target: hud-live or the main checkout) left detached at a commit that isn't on origin/main, with no deploy on the board, is raised
/// as an orphaned deploy too (a session that died before this board existed, or a hand-made checkout).
/// </summary>
public static class DeployIncidents
{
    public static readonly TimeSpan HeartbeatStale = TimeSpan.FromMinutes(10);

    public static string Key(string folder) => $"deploy:{folder}";

    public sealed record OwnerState(Incident? Incident, bool Orphaned, string Why)
    {
        public string? Owner => Incident?.Get("owner")?.ToString() ?? Incident?.RaisedBy;
    }

    /// <summary>
    /// Is this deploy incident's owner gone? Pure given the facts (pid liveness is passed in for tests). A deploy whose owner
    /// process died is not orphaned while another server of the same owner beats (<paramref name="sameOwnerServer"/>: the
    /// live server pid other than the owner's working for the deploy's client session, or for its label when the deploy
    /// records no session; null if none): that is the same agent after a server restart or a resumed session, and its
    /// monitor adopts the deploy (<see cref="Adopt"/>).
    /// </summary>
    public static (bool Orphaned, string Why) Judge(Incident i, DateTime? heartbeat, Func<int, DateTime?, bool?> pidAlive, DateTime now,
        Func<Incident, int, int?>? sameOwnerServer = null)
    {
        var pid = i.OwnerPid;
        if (pid == null) return (true, "no owner process recorded (found by the scan, or deployed before the board existed)");
        var owner = OwnerLabel(i);
        var alive = pidAlive(pid.Value, i.RaisedAt);
        var beat = heartbeat ?? ParseIso(i.Get("heartbeatAt")) ?? i.RaisedAt;
        var stale = beat is { } b && now - b > HeartbeatStale;
        // Only a dead owner: Adopt takes dead pids only, so a live-but-silent owner stays orphaned by its stale heartbeat below.
        if (alive == false && sameOwnerServer?.Invoke(i, pid.Value) is { } other)
            return (false, $"owner process {pid} ({owner}) is gone, but a server of the same {(OwnerSession(i) != null ? "session" : "label")} (pid {other}) beats: it adopts the deploy");
        if (alive == false) return (true, $"owner process {pid} ({owner}) is gone");
        if (stale)
            return (true, $"no heartbeat from {owner} (pid {pid}) for {(int)(now - beat!.Value).TotalMinutes} min");
        return (false, $"owner {owner} (pid {pid}) is alive, last heartbeat {IncidentLogic.Ago(beat, now)}");
    }

    /// <summary>The deploy's owner label: its "owner" field, else who raised it.</summary>
    public static string OwnerLabel(Incident i) => i.Get("owner")?.ToString() is { Length: > 0 } o ? o : i.RaisedBy;

    /// <summary>The client session that deployed (SessionIdentity.ClientSession at deploy time), or null (older deploys, or a
    /// server that knew no client session id).</summary>
    public static string? OwnerSession(Incident i) => i.Get("ownerSession")?.ToString() is { Length: > 0 } s ? s : null;

    /// <summary>Is <paramref name="i"/> this server's to adopt: the same client session when the deploy records one, else
    /// (older deploys) the same label.</summary>
    public static bool SameOwner(Incident i, string label, string? session) =>
        OwnerSession(i) is { } s ? string.Equals(s, session, StringComparison.Ordinal) : string.Equals(OwnerLabel(i), label, StringComparison.OrdinalIgnoreCase);

    /// <summary>Servers that beat within this long count as alive for the same-owner rule.</summary>
    public static readonly TimeSpan ServerBeatFresh = TimeSpan.FromMinutes(2);

    /// <summary>The live server (fresh beat, process alive) of this deploy's owner other than <paramref name="ownerPid"/>, from the per-pid sidecar.</summary>
    public static int? SameOwnerServer(Incident i, int ownerPid) =>
        SameLabelServer(IncidentStore.ReadServerBeats(), OwnerLabel(i), ownerPid, PidAlive, DateTime.UtcNow, OwnerSession(i));

    /// <summary>The live server with this label (or, when <paramref name="session"/> is given, working for that client
    /// session: labels like main are shared by many sessions) other than <paramref name="ownerPid"/>.</summary>
    public static int? SameLabelServer(Dictionary<int, ServerBeat> beats, string label, int ownerPid, Func<int, DateTime?, bool?> pidAlive, DateTime now,
        string? session = null)
    {
        foreach (var (pid, b) in beats)
            if (pid != ownerPid && (session != null ? string.Equals(b.Session, session, StringComparison.Ordinal) : string.Equals(b.Label, label, StringComparison.OrdinalIgnoreCase))
                && b.At is { } at && now - at < ServerBeatFresh && b.Kind != "mcp-http" && pidAlive(pid, null) != false)
                return pid;
        return null;
    }

    /// <summary>
    /// This server adopts the open deploys of its own client session (or, for deploys recording none, its label) whose owner
    /// process is gone (a server restart, a resumed session): ownerPid becomes this server's, so it beats for them (stdio
    /// servers only: a shared server's deploys carry its callers' identities). Returns the adopted ids.
    /// </summary>
    public static List<string> Adopt()
    {
        if (SessionIdentity.Kind == "mcp-http") return [];
        var me = SessionIdentity.Label;
        var session = SessionIdentity.ClientSession;
        var board = IncidentStore.Read();
        var candidates = board.Incidents.Where(i => i.IsOpen && i.Kind == "deploy" && i.OwnerPid is { } p && p != SessionIdentity.Pid
                                                    && SameOwner(i, me, session)
                                                    && PidAlive(p, i.RaisedAt) == false).ToList();
        if (candidates.Count == 0) return [];
        return IncidentStore.Update(f =>
        {
            var done = new List<string>();
            foreach (var c in candidates)
            {
                var cur = IncidentLogic.Find(f, c.Id);
                if (cur is not { IsOpen: true } || cur.OwnerPid != c.OwnerPid) continue;   // raced with another server
                cur.Set("adoptedFrom", cur.OwnerPid);
                cur.Set("ownerPid", SessionIdentity.Pid);
                done.Add(cur.Id);
                Console.Error.WriteLine($"[Incidents] adopted deploy {cur.Key} from dead pid {c.OwnerPid} (same owner: {me}{(session != null ? " session " + session : "")})");
            }
            return done;
        });
    }

    /// <summary>The process exists and started before the deploy (a reused pid started later); null when unknown.</summary>
    public static bool? PidAlive(int pid, DateTime? since)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            try { if (since != null && p.StartTime.ToUniversalTime() > since.Value.AddSeconds(5)) return false; } catch { }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch { return null; }
    }

    /// <param name="dir">The copy of the folder asked about (the HUD source's plugin folder); a deploy recorded for another
    /// copy (the other tree, after or during a cutover to hud-live) doesn't hold this one. Null: any copy.</param>
    public static OwnerState Owner(string folder, string? dir = null) => Owner(IncidentStore.Read(), folder, IncidentStore.ReadHeartbeats(), dir);

    public static OwnerState Owner(IncidentFile f, string folder, Dictionary<string, DateTime> beats, string? dir = null)
    {
        var i = IncidentLogic.OpenByKey(f, Key(folder));
        if (i == null) return new OwnerState(null, true, "no deploy on the incident board");
        if (!IsFor(i, dir)) return new OwnerState(null, true, $"the deploy on the board is in another tree ({i.Get("mainPath")})");
        var (orphaned, why) = Judge(i, beats.TryGetValue(i.Id, out var b) ? b : null, PidAlive, DateTime.UtcNow, SameOwnerServer);
        return new OwnerState(i, orphaned, why);
    }

    /// <summary>The deploy incident is about this copy of the folder: its mainPath names <paramref name="dir"/>, or it names
    /// none (raised before the board recorded it), or no copy was asked about.</summary>
    public static bool IsFor(Incident i, string? dir) =>
        dir == null || i.Get("mainPath")?.ToString() is not { Length: > 0 } at || SamePath(at, dir);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>A deploy happened: this session owns deploy:&lt;folder&gt; now (an earlier owner's incident is resolved as taken over).</summary>
    public static Incident Deployed(string folder, string commit, string? subject, string? beforeBranch, string beforeSha, List<string> games,
        string mainPath, string? takeoverNote)
    {
        var now = DateTime.UtcNow;
        var me = SessionIdentity.Label;
        var scope = games.Count == 1 ? games[0] : "machine";
        var src = HudSource.CheckoutOf(mainPath) is { } root ? HudSource.Describe(root) : "the HUD source";
        var inc = IncidentStore.Update(f =>
        {
            if (IncidentLogic.OpenByKey(f, Key(folder)) is { } old && (old.OwnerPid != SessionIdentity.Pid || old.RaisedBy != me))
                IncidentLogic.Resolve(old, me, takeoverNote ?? $"taken over by {me}", now);
            var (i, _) = IncidentLogic.Raise(f, new IncidentDraft
            {
                Kind = "deploy", Scope = scope, Key = Key(folder),
                Title = $"{folder}: {me}'s commit {Short(commit)} is deployed into the HUD source",
                Detail = $"The HUD compiles Plugins/{folder} from {src}, which is detached at {Short(commit)} ({subject}) instead of " +
                         $"{beforeBranch ?? Short(beforeSha)}. Syncs of the HUD source leave the plugin alone until it is restored.",
                Fix = $"{me} runs deploy_plugin plugin=\"{folder}\" action=restore when done. If {me}'s session is gone (orphaned), any session may restore it or deploy over it.",
                Fields =
                {
                    ["owner"] = me, ["ownerPid"] = SessionIdentity.Pid, ["ownerSession"] = SessionIdentity.ClientSession, ["plugin"] = folder, ["commit"] = commit,
                    ["mainPath"] = mainPath, ["beforeBranch"] = beforeBranch, ["beforeCommit"] = beforeSha, ["heartbeatAt"] = IsoUtcConverter.Iso(now),
                },
            }, me, now);
            return i;
        });
        try { IncidentStore.Beat([inc.Id], IncidentStore.Read().Incidents.Where(x => x.IsOpen).Select(x => x.Id)); } catch { }
        return inc;
    }

    /// <summary>Resolve deploy:&lt;folder&gt;, unless it is recorded for another copy of the folder than <paramref name="dir"/>.</summary>
    public static void Restored(string folder, string note, string? dir = null)
    {
        if (dir != null && IncidentLogic.OpenByKey(IncidentStore.Read(), Key(folder)) is { } i && !IsFor(i, dir)) return;
        IncidentStore.ResolveKeys([Key(folder)], note);
    }

    /// <summary>The ids of the open deploy incidents this process owns (the monitor beats for them). On the shared HTTP server
    /// every caller's deploy carries this pid and the caller's label, while the monitor runs as no caller: it beats them all
    /// for as long as the server runs (once it stops after being idle, they are orphaned like any dead session's).</summary>
    public static List<string> Mine(IncidentFile f) =>
        f.Incidents.Where(i => i.IsOpen && i.Kind == "deploy" && i.OwnerPid == SessionIdentity.Pid
                               && (SessionIdentity.Kind == "mcp-http" || SameOwner(i, SessionIdentity.Label, SessionIdentity.ClientSession))).Select(i => i.Id).ToList();

    // ── The scan: HUD-source submodules detached off origin/main with no deploy on the board ──

    private static string? _mainRoot;
    private static DateTime _lastScan;
    private static readonly object ScanLock = new();

    /// <summary>The scaffolding's main checkout, from this session's repo.</summary>
    public static string? MainRoot()
    {
        if (_mainRoot != null) return _mainRoot;
        var here = SessionIdentity.RepoRoot();
        if (here == null) return null;
        var common = PluginGit.Run(here, "rev-parse", "--path-format=absolute", "--git-common-dir");
        if (!common.Ok) return null;
        return _mainRoot = Path.GetDirectoryName(common.Line.Replace('/', '\\').TrimEnd('\\'));
    }
    /// <summary>HEAD of a checkout from its files (no git process): (branch, null) or (null, sha).</summary>
    public static (string? Branch, string? Sha) ReadHeadFile(string dir)
    {
        try
        {
            var dotGit = Path.Combine(dir, ".git");
            string? gitDir = Directory.Exists(dotGit) ? dotGit : null;
            if (gitDir == null && File.Exists(dotGit))
            {
                var line = File.ReadAllText(dotGit).Trim();
                if (line.StartsWith("gitdir:", StringComparison.Ordinal)) gitDir = Path.GetFullPath(Path.Combine(dir, line["gitdir:".Length..].Trim()));
            }
            if (gitDir == null) return (null, null);
            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            return head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? (head[16..], null) : (null, head.Length >= 40 ? head[..40] : null);
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// Walk the plugin folders the HUDs compile (Hud/HudSource.cs): raise an orphaned deploy for one detached at a commit that isn't on
    /// origin/main with no deploy on the board; resolve a deploy whose folder is back on a branch, or back on origin/main with no deploy recorded. At most once a minute
    /// unless <paramref name="force"/>.
    /// </summary>
    public static void Scan(bool force = false)
    {
        lock (ScanLock)
        {
            if (!force && DateTime.UtcNow - _lastScan < TimeSpan.FromMinutes(1)) return;
            _lastScan = DateTime.UtcNow;
        }
        try
        {
            // The plugin folders the HUDs compile (junction targets); with no junction at all, the main checkout's plugins.
            var dirs = HudSource.Links().Where(l => l.Root != null).Select(l => l.Target)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var fromJunctions = dirs.Count > 0;
            if (!fromJunctions && MainRoot() is { } main && Directory.Exists(Path.Combine(main, "Plugins")))
                dirs = Directory.GetDirectories(Path.Combine(main, "Plugins")).ToList();
            if (dirs.Count == 0) return;
            var board = IncidentStore.Read();
            // The commit each tree records per plugin (one ls-tree per tree): a plugin at it needs no merge-base process.
            var pointers = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                var folder = Path.GetFileName(dir);
                var root = HudSource.CheckoutOf(dir);
                var src = root != null ? HudSource.Describe(root) : "the HUD source";
                if (!PluginGit.IsRepo(dir)) continue;
                var (branch, sha) = ReadHeadFile(dir);
                var open = IncidentLogic.OpenByKey(board, Key(folder));
                // A deploy recorded for another copy of this folder (a half-done cutover: the other tree) is not this one's.
                if (open != null && !IsFor(open, dir)) continue;
                if (branch != null)
                {
                    if (open != null) Restored(folder, $"{folder} in {src} is back on {branch}", dir);
                    continue;
                }
                if (sha == null) continue;
                var state = PluginGit.ReadState(dir);
                if (root != null && !pointers.ContainsKey(root)) pointers[root] = Pointers(root);
                var atPointer = root != null && pointers[root].TryGetValue(folder, out var ptr) && ptr == sha;
                var onMain = atPointer ? new PluginGit.GitResult(0, "", "") : PluginGit.Run(dir, "merge-base", "--is-ancestor", sha, "origin/main");
                if (open != null)
                {
                    // hud-live's plugins sit detached at origin/main commits: one back there with no deploy recorded is restored.
                    if (onMain.Code == 0 && state == null) Restored(folder, $"{folder} in {src} is back at {Short(sha)} on origin/main, no deploy recorded", dir);
                    continue;
                }
                if (onMain.Code == 0) continue;   // e.g. a plain submodule update: detached at the recorded commit
                if (onMain.Code != 1) { Console.Error.WriteLine($"[Incidents] scan {folder}: {onMain.Err}"); continue; }
                var subject = PluginGit.Run(dir, "log", "-1", "--format=%s", sha).Line;
                IncidentStore.Update(f =>
                {
                    if (IncidentLogic.OpenByKey(f, Key(folder)) != null) return 0;   // raced with another server's scan
                    IncidentLogic.Raise(f, new IncidentDraft
                    {
                        Kind = "deploy", Scope = "machine", Key = Key(folder),
                        Title = $"{folder}: orphaned deploy, HUD source left detached at {Short(sha)} (not on origin/main)",
                        Detail = $"Plugins/{folder} in {src} is detached at {Short(sha)} ({subject}), a commit origin/main (as fetched there) " +
                                 $"doesn't contain, and no session holds a deploy of it" +
                                 (state != null ? $": deploy_plugin by {state.By} at {state.At:u}, from {state.From}, never restored." : ". Nothing recorded who put it there."),
                        Fix = state != null
                            ? $"Any session: deploy_plugin plugin=\"{folder}\" action=restore (returns it to {state.BeforeBranch ?? Short(state.BeforeSha)}), or deploy over it."
                            : $"Check what it is (git -C \"{dir}\" log -1), then put it back (tools\\hud-live.ps1 sync for hud-live, its branch in the main checkout), or deploy over it with deploy_plugin.",
                        Fields = { ["owner"] = state?.By, ["plugin"] = folder, ["commit"] = sha, ["mainPath"] = dir, ["orphanedAt"] = IsoUtcConverter.Iso(DateTime.UtcNow), ["auto"] = true },
                    }, SessionIdentity.Label, DateTime.UtcNow);
                    return 0;
                });
                Console.Error.WriteLine($"[Incidents] orphaned deploy: {folder} detached at {Short(sha)}");
            }
            // Deploys into a tree no HUD compiles any more (its junctions were cut over to hud-live): they hold nothing.
            if (fromJunctions)
                foreach (var i in board.Incidents.Where(i => i.IsOpen && i.Kind == "deploy").ToList())
                    if (i.Get("mainPath")?.ToString() is { Length: > 0 } at && !dirs.Any(d => SamePath(d, at)) && i.Get("plugin")?.ToString() is { Length: > 0 } plugin)
                        Restored(plugin, $"no HUD compiles {at} any more (its junction points elsewhere, e.g. after tools\\hud-live.ps1 cutover); the folder there was left as it is", at);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Incidents] deploy scan failed: {ex.Message}"); }
    }

    /// <summary>The commit a checkout records for each plugin submodule (git ls-tree HEAD -- Plugins/), by folder.</summary>
    private static Dictionary<string, string> Pointers(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ls = PluginGit.Run(root, "ls-tree", "HEAD", "--", "Plugins/");
        if (!ls.Ok) return map;
        foreach (var line in ls.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (line.Split('\t', 2) is [var meta, var path] && meta.Split(' ') is [_, "commit", var sha])
                map[Path.GetFileName(path.Trim())] = sha;
        return map;
    }

    public static DateTime? ParseIso(JToken? t) =>
        t != null && DateTime.TryParse(t.ToString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    private static string Short(string? sha) => sha is { Length: >= 7 } ? sha[..7] : sha ?? "?";

    /// <summary>The view fields a deploy incident gets in tool results: heartbeatAt (sidecar), orphaned and why.</summary>
    public static void Annotate(JObject view, Incident i, Dictionary<string, DateTime> beats)
    {
        if (i.Kind != "deploy" || !i.IsOpen) return;
        var beat = beats.TryGetValue(i.Id, out var b) ? b : (DateTime?)null;
        if (beat != null) view["heartbeatAt"] = IsoUtcConverter.Iso(beat.Value);
        var (orphaned, why) = Judge(i, beat, PidAlive, DateTime.UtcNow, SameOwnerServer);
        view["orphaned"] = orphaned;
        view["ownerState"] = why;
    }
}
