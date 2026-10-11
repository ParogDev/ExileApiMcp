using System.Diagnostics;
using ExileApiMcp.Hosting;
using ExileApiMcp.Hud;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Incidents;

/// <summary>
/// deploy_plugin deploys as leases on the incident board (kind deploy, key deploy:&lt;plugin folder&gt;): the owning server
/// (ownerPid = the session's process, the supervisor when there is one) beats every 30 s in a sidecar file, so a deploy
/// whose owner is gone, or silent for 10 min, is "orphaned": any session may restore it or deploy over it without force.
/// A main-checkout submodule left detached at a commit that isn't on origin/main, with no deploy on the board, is raised
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

    /// <summary>Is this deploy incident's owner gone? Pure given the facts (pid liveness is passed in for tests).</summary>
    public static (bool Orphaned, string Why) Judge(Incident i, DateTime? heartbeat, Func<int, DateTime?, bool?> pidAlive, DateTime now)
    {
        var pid = i.OwnerPid;
        if (pid == null) return (true, "no owner process recorded (found by the scan, or deployed before the board existed)");
        var alive = pidAlive(pid.Value, i.RaisedAt);
        if (alive == false) return (true, $"owner process {pid} ({i.Get("owner") ?? i.RaisedBy}) is gone");
        var beat = heartbeat ?? ParseIso(i.Get("heartbeatAt")) ?? i.RaisedAt;
        if (beat is { } b && now - b > HeartbeatStale)
            return (true, $"no heartbeat from {i.Get("owner") ?? i.RaisedBy} (pid {pid}) for {(int)(now - b).TotalMinutes} min");
        return (false, $"owner {i.Get("owner") ?? i.RaisedBy} (pid {pid}) is alive, last heartbeat {IncidentLogic.Ago(beat, now)}");
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

    public static OwnerState Owner(string folder) => Owner(IncidentStore.Read(), folder, IncidentStore.ReadHeartbeats());

    public static OwnerState Owner(IncidentFile f, string folder, Dictionary<string, DateTime> beats)
    {
        var i = IncidentLogic.OpenByKey(f, Key(folder));
        if (i == null) return new OwnerState(null, true, "no deploy on the incident board");
        var (orphaned, why) = Judge(i, beats.TryGetValue(i.Id, out var b) ? b : null, PidAlive, DateTime.UtcNow);
        return new OwnerState(i, orphaned, why);
    }

    /// <summary>A deploy happened: this session owns deploy:&lt;folder&gt; now (an earlier owner's incident is resolved as taken over).</summary>
    public static Incident Deployed(string folder, string commit, string? subject, string? beforeBranch, string beforeSha, List<string> games,
        string mainPath, string? takeoverNote)
    {
        var now = DateTime.UtcNow;
        var me = SessionIdentity.Label;
        var scope = games.Count == 1 ? games[0] : "machine";
        var inc = IncidentStore.Update(f =>
        {
            if (IncidentLogic.OpenByKey(f, Key(folder)) is { } old && (old.OwnerPid != SessionIdentity.Pid || old.RaisedBy != me))
                IncidentLogic.Resolve(old, me, takeoverNote ?? $"taken over by {me}", now);
            var (i, _) = IncidentLogic.Raise(f, new IncidentDraft
            {
                Kind = "deploy", Scope = scope, Key = Key(folder),
                Title = $"{folder}: {me}'s commit {Short(commit)} is deployed into the main checkout",
                Detail = $"The HUD compiles Plugins/{folder} from the main checkout, which is detached at {Short(commit)} ({subject}) instead of " +
                         $"{beforeBranch ?? Short(beforeSha)}. The scaffolding main checkout shows the plugin as modified until it is restored.",
                Fix = $"{me} runs deploy_plugin plugin=\"{folder}\" action=restore when done. If {me}'s session is gone (orphaned), any session may restore it or deploy over it.",
                Fields =
                {
                    ["owner"] = me, ["ownerPid"] = SessionIdentity.Pid, ["plugin"] = folder, ["commit"] = commit,
                    ["mainPath"] = mainPath, ["beforeBranch"] = beforeBranch, ["beforeCommit"] = beforeSha, ["heartbeatAt"] = IsoUtcConverter.Iso(now),
                },
            }, me, now);
            return i;
        });
        try { IncidentStore.Beat([inc.Id], IncidentStore.Read().Incidents.Where(x => x.IsOpen).Select(x => x.Id)); } catch { }
        return inc;
    }

    public static void Restored(string folder, string note) => IncidentStore.ResolveKeys([Key(folder)], note);

    /// <summary>The ids of the open deploy incidents this process owns (the monitor beats for them). On the shared HTTP server
    /// every caller's deploy carries this pid and the caller's label, while the monitor runs as no caller: it beats them all
    /// for as long as the server runs (once it stops after being idle, they are orphaned like any dead session's).</summary>
    public static List<string> Mine(IncidentFile f) =>
        f.Incidents.Where(i => i.IsOpen && i.Kind == "deploy" && i.OwnerPid == SessionIdentity.Pid
                               && (SessionIdentity.Kind == "mcp-http" || i.RaisedBy == SessionIdentity.Label)).Select(i => i.Id).ToList();

    // ── The scan: main-checkout submodules detached off origin/main with no deploy on the board ──

    private static string? _mainRoot;
    private static DateTime _lastScan;
    private static readonly object ScanLock = new();

    /// <summary>The scaffolding's main checkout (the folder the HUD junctions point into), from this session's repo.</summary>
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
    /// Walk the main checkout's plugin submodules: raise an orphaned deploy for one detached at a commit that isn't on
    /// origin/main with no deploy on the board; resolve a deploy whose folder is back on a branch. At most once a minute
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
            var root = MainRoot();
            var plugins = root == null ? null : Path.Combine(root, "Plugins");
            if (plugins == null || !Directory.Exists(plugins)) return;
            var board = IncidentStore.Read();
            foreach (var dir in Directory.GetDirectories(plugins))
            {
                var folder = Path.GetFileName(dir);
                if (!PluginGit.IsRepo(dir)) continue;
                var (branch, sha) = ReadHeadFile(dir);
                var open = IncidentLogic.OpenByKey(board, Key(folder));
                if (branch != null)
                {
                    if (open != null) Restored(folder, $"the main checkout's {folder} is back on {branch}");
                    continue;
                }
                if (sha == null || open != null) continue;
                var onMain = PluginGit.Run(dir, "merge-base", "--is-ancestor", sha, "origin/main");
                if (onMain.Code == 0) continue;   // e.g. a plain submodule update: detached at the recorded commit
                if (onMain.Code != 1) { Console.Error.WriteLine($"[Incidents] scan {folder}: {onMain.Err}"); continue; }
                var state = PluginGit.ReadState(dir);
                var subject = PluginGit.Run(dir, "log", "-1", "--format=%s", sha).Line;
                IncidentStore.Update(f =>
                {
                    if (IncidentLogic.OpenByKey(f, Key(folder)) != null) return 0;   // raced with another server's scan
                    IncidentLogic.Raise(f, new IncidentDraft
                    {
                        Kind = "deploy", Scope = "machine", Key = Key(folder),
                        Title = $"{folder}: orphaned deploy, main checkout left detached at {Short(sha)} (not on origin/main)",
                        Detail = $"Plugins/{folder} in the main checkout is detached at {Short(sha)} ({subject}), a commit origin/main (as fetched there) " +
                                 $"doesn't contain, and no session holds a deploy of it" +
                                 (state != null ? $": deploy_plugin by {state.By} at {state.At:u}, from {state.From}, never restored." : ". Nothing recorded who put it there."),
                        Fix = state != null
                            ? $"Any session: deploy_plugin plugin=\"{folder}\" action=restore (returns it to {state.BeforeBranch ?? Short(state.BeforeSha)}), or deploy over it."
                            : $"Check what it is (git -C \"Plugins/{folder}\" log -1 in the main checkout), then switch it back to its branch, or deploy over it with deploy_plugin.",
                        Fields = { ["owner"] = state?.By, ["plugin"] = folder, ["commit"] = sha, ["mainPath"] = dir, ["orphanedAt"] = IsoUtcConverter.Iso(DateTime.UtcNow), ["auto"] = true },
                    }, SessionIdentity.Label, DateTime.UtcNow);
                    return 0;
                });
                Console.Error.WriteLine($"[Incidents] orphaned deploy: {folder} detached at {Short(sha)}");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Incidents] deploy scan failed: {ex.Message}"); }
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
        var (orphaned, why) = Judge(i, beat, PidAlive, DateTime.UtcNow);
        view["orphaned"] = orphaned;
        view["ownerState"] = why;
    }
}
