using System.ComponentModel;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using ExileApiMcp.Hud;
using ExileApiMcp.Incidents;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Worktree plugin deployment. The HUDs compile every plugin from &lt;HUD&gt;\Plugins\Source\&lt;folder&gt;, a junction into
/// the HUD source tree (Hud/HudSource.cs: hud-live once tools\hud-live.ps1 cut it over, the scaffolding's main checkout
/// before), never a worktree, so a session working in a worktree never reaches the running HUD by editing. This moves
/// the worktree submodule's committed HEAD into the HUD source's submodule (git fetch from the worktree, then a detached
/// checkout), under the HUD source lock and a reload lease so no sync or restart lands in the middle, then reloads the
/// plugin (or restarts the HUD for Whats An AI Bridge) and reports what the HUD runs against what this session edited.
/// Never touches uncommitted work: a dirty HUD-source submodule is refused, uncommitted worktree edits are named as not
/// deployed. The previous branch / commit is kept in the submodule's git dir (hexile-deploy.json) so action=restore puts
/// it back; until then the HUD source shows the plugin as modified (hud-live.ps1 sync leaves it alone), and every result
/// says so. The JSON keeps the name "main" for the HUD source's copy.
/// </summary>
[McpServerToolType]
public static class DeployTools
{
    private const string BridgeFolder = "Whats An AI Bridge";
    /// <summary>How long a deploy waits for the HUD source lock (a sync or another deploy holding it).</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(60);

    [McpServerTool(Name = "deploy_plugin", Title = "Put a worktree's plugin commit into the HUD", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(DeployResult))]
    [Description("From a worktree session: make the running HUD compile your plugin's committed code. The HUD compiles plugins " +
                 "from its HUD source tree (the target of its Plugins\\Source junctions: hud-live, or the main checkout before " +
                 "tools\\hud-live.ps1 cutover), never a worktree, so edits in a worktree don't reach it. " +
                 "action=deploy fetches your worktree submodule's HEAD commit into the HUD source's submodule (detached; " +
                 "refused when it has uncommitted changes there, or another live session's deploy is active), under the HUD " +
                 "source lock and a reload lease, then reload_plugin (hud_restart for Whats An AI Bridge). Uncommitted worktree " +
                 "edits are not deployed: commit first. action=restore puts the HUD source back to the branch/commit it was on; " +
                 "action=status shows what the HUD compiles vs what you edited. Each deploy is a lease on the incident board " +
                 "(deploy:<folder>, kept alive by your server's heartbeat): every session sees it, and once your session is gone " +
                 "(or silent 10 min) it is orphaned and any session may restore it or deploy over it without force. Until " +
                 "restore, hud-live.ps1 sync (and the F8 sync) leave the plugin alone.")]
    public static async Task<CallToolResult> DeployPlugin(BridgeRegistry bridges,
        [Description("Plugin folder (or a unique part of it), e.g. 'Whats A Route'")] string plugin,
        [Description("deploy (default) | restore | status")] string action = "deploy",
        [Description("Reload after deploy / restore (default true): reload_plugin, or hud_restart for the bridge")] bool reload = true,
        [Description("deploy / restore: replace another session's LIVE deploy of this plugin (needs message too). An orphaned deploy (its session is gone or silent for 10 min) needs no force")] bool force = false,
        [Description("With force over a live deploy: what you tell its owner (recorded on its incident, which the owner hears about)")] string? message = null,
        [Description("poe1 | poe2: the HUD to reload; default every running HUD that compiles this folder")] string? game = null,
        [Description("Absolute path of your scaffolding worktree (or any folder in it). Default: this MCP server's (or, on the shared " +
                     "server, the caller's) working directory. Pass it when they differ, e.g. a session that started in the main " +
                     "checkout and then entered a worktree: its server still runs from the main checkout")] string? worktree = null,
        CancellationToken ct = default)
    {
        var act = action.Trim().ToLowerInvariant();
        if (act is not ("deploy" or "restore" or "status")) throw new McpException("action must be deploy, restore or status.");
        var r = new DeployResult { Action = act };

        // ── Where things are: this session's checkout, the HUD source (the junctions' target), the HUD junctions ──
        var here = string.IsNullOrWhiteSpace(worktree)
            ? SessionIdentity.WorkingRepoRoot() ?? throw new McpException("Not running in the scaffolding repo (tools\\restart-hud.ps1 not found from the cwd; HEXILE_REPO unset): pass worktree=<absolute path of your worktree>.")
            : ScaffoldingRoot(worktree);
        var folder = FindFolder(Path.Combine(here, "Plugins"), plugin);
        r.Plugin = folder;
        var edited = Path.Combine(here, "Plugins", folder);
        var installs = HudDevTools.Installs(bridges, null);
        // The tree the HUD compiles this folder from, read off its junction (hud-live after the cutover, the main checkout
        // before): the game asked for first, so a half-done cutover deploys where that HUD looks. No junction for it: the
        // main checkout, as before hud-live, and the warning below says so.
        var wanted = game?.Trim().ToLowerInvariant();
        var srcRoot = HudSource.RootFor(folder, installs.Where(h => string.IsNullOrEmpty(wanted) || h.Game == wanted).Select(h => (h.Game, h.Root)))
                      ?? HudSource.RootFor(folder, installs.Select(h => (h.Game, h.Root)));
        if (srcRoot == null)
        {
            var common = PluginGit.Run(here, "rev-parse", "--path-format=absolute", "--git-common-dir");
            if (!common.Ok) throw new McpException($"No HUD has a Plugins\\Source\\{folder} junction, and git rev-parse --git-common-dir in {here} failed: {common.Err}");
            srcRoot = Path.GetDirectoryName(common.Line.Replace('/', '\\').TrimEnd('\\'))!;
        }
        var src = HudSource.Describe(srcRoot);
        r.Source = srcRoot; r.SourceTree = HudSource.Kind(srcRoot);
        var main = Path.Combine(srcRoot, "Plugins", folder);
        var inMain = string.Equals(Path.GetFullPath(here).TrimEnd('\\'), Path.GetFullPath(srcRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        foreach (var hud in installs)
        {
            var link = Path.Combine(hud.SourcePluginsDir, folder);
            if (Directory.Exists(link) && string.Equals(PluginGit.Resolve(link).TrimEnd('\\'), Path.GetFullPath(main).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                r.Games.Add(hud.Game);
        }
        if (r.Games.Count == 0)
            r.Warnings.Add($"No HUD's Plugins\\Source\\{folder} points at {main}: the HUD doesn't compile this folder (missing junction?).");
        // A half-done cutover: one HUD on hud-live, the other still on the main checkout. Only the first is deployed to.
        var elsewhere = HudSource.Links(installs.Select(h => (h.Game, h.Root)))
            .Where(l => string.Equals(l.Folder, folder, StringComparison.OrdinalIgnoreCase) && l.Root != null && !string.Equals(l.Root, srcRoot, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var l in elsewhere)
            r.Warnings.Add($"The {l.Game} HUD compiles {folder} from {HudSource.Describe(l.Root!)}, not {src}: this deploy doesn't reach it (cut that HUD over: tools\\hud-live.ps1 cutover -Game {l.Game}).");

        var mainHead = PluginGit.ReadHead(main, out var mainWhy) ?? throw new McpException($"The HUD source's {folder} ({src}): {mainWhy}");
        r.Main = Checkout(main, mainHead);
        var pointer = PluginGit.Run(srcRoot, "ls-tree", "HEAD", "--", $"Plugins/{folder}");
        r.Pointer = pointer.Ok && pointer.Line.Split(' ', '\t') is [_, "commit", var sha, ..] ? sha : null;
        var state = PluginGit.ReadState(main);
        if (state != null) r.Deployed = StateInfo(state);
        if (!inMain && PluginGit.ReadHead(edited, out _) is { } editedHead) r.Edited = Checkout(edited, editedHead);

        // The deploy lease on the incident board: who owns it, and whether its session is gone (orphaned).
        var owner = DeployIncidents.Owner(folder, main);
        if (owner.Incident == null && state != null)
        {
            // No lease on the board: the scan raises a deploy left detached off origin/main as orphaned. Anything else
            // without a lease (the deployer's board write failed, or it runs a build without the board) is treated as live.
            DeployIncidents.Scan(force: true);
            owner = DeployIncidents.Owner(folder, main);
        }
        if (owner.Incident != null) r.Incident = IncidentTools.View(owner.Incident, Incidents.IncidentStore.ReadHeartbeats());
        string? takeover = null;
        if (act != "status" && state != null && state.By != SessionIdentity.Label)
        {
            // On the shared HTTP server every caller has its pid (Hosting/CallerIdentity.cs): there, a deploy another label
            // raised is not this caller's, pid or not. A stdio session is its pid (its label may change, AdoptRoots).
            var mine = owner.Incident?.OwnerPid == SessionIdentity.Pid && (SessionIdentity.Kind != "mcp-http" || owner.Incident.RaisedBy == SessionIdentity.Label);
            if (owner.Incident == null)
            {
                if (!force)
                    return Finish(r, "refused", false,
                        $"{state.By} deployed {Short(state.DeployedSha)} ({state.Subject}) from {state.From} at {state.At:u}, with no lease on the incident board " +
                        "(its commit is on origin/main, or the deployer couldn't record one): ask them (or the user), then pass force=true.");
                takeover = $"{act} by {SessionIdentity.Label} over {state.By}'s unleased deploy (force)" + (string.IsNullOrWhiteSpace(message) ? "" : $": {message}");
            }
            else if (!owner.Orphaned && !mine)
            {
                if (!force || string.IsNullOrWhiteSpace(message))
                    return Finish(r, "refused", false,
                        $"{state.By} deployed {Short(state.DeployedSha)} ({state.Subject}) from {state.From} at {state.At:u}, and that session is live ({owner.Why}). " +
                        "Ask them (or the user); to go ahead anyway pass force=true with message=\"...\" (recorded on their incident).");
                takeover = $"{act} by {SessionIdentity.Label} over the live deploy (force): {message}";
            }
            else if (!mine)
            {
                takeover = $"orphaned deploy by {state.By} ({owner.Why}): {act} by {SessionIdentity.Label}" + (string.IsNullOrWhiteSpace(message) ? "" : $": {message}");
                r.Warnings.Add($"Taking over {state.By}'s orphaned deploy ({owner.Why}).");
            }
        }

        if (act == "status") return Finish(r, "status", true, StatusNote(r, inMain));
        if (act == "restore") return await Restore(bridges, r, main, mainHead, state, reload, game, takeover, ct);

        // ── Deploy ──
        if (inMain)
        {
            r.Note = $"This session runs in the HUD source, {src}: the HUD compiles your folder directly, nothing to deploy. " +
                     "If you work in a worktree (entered after the session started, so this server still runs from the main checkout), pass worktree=<its absolute path>.";
            if (reload) r.Reload = await Reload(bridges, folder, PickGames(r, game), $"reload {folder}", ct);
            return Finish(r, "unchanged", r.Reload?.Ok != false, null);
        }
        if (r.Edited == null)
            return Finish(r, "refused", false, $"{edited} is not a git checkout here: initialise the submodule in this worktree (git submodule update --init \"Plugins/{folder}\").");
        if (r.Edited.Uncommitted is { Count: > 0 } wip)
            r.Warnings.Add($"{wip.Count} uncommitted change(s) in your worktree are NOT deployed (only commits are): {string.Join(", ", wip.Take(5))}{(wip.Count > 5 ? ", ..." : "")}. Commit them and deploy again.");
        if (mainHead.Changed.Count > 0)
            return Finish(r, "refused", false, $"{folder} in {src} has {mainHead.Changed.Count} uncommitted change(s) ({string.Join(", ", mainHead.Changed.Take(5))}): someone is editing there. Nothing was touched.");
        if (state != null && mainHead.Sha != state.DeployedSha)
            r.Warnings.Add($"{folder} in {src} moved since the last deploy ({Short(state.DeployedSha)} -> {Short(mainHead.Sha)}); restore will return to {state.BeforeBranch ?? Short(state.BeforeSha)} as first recorded.");
        if (mainHead.Sha == r.Edited.Commit)
        {
            r.Note = $"{folder} in {src} is already at {Short(mainHead.Sha)}.";
            // Our deploy without its lease (the board write failed, or it predates the board): record it now.
            if (state != null && state.DeployedSha == mainHead.Sha && (owner.Incident == null || owner.Incident.OwnerPid != SessionIdentity.Pid))
                try
                {
                    if (state.By != SessionIdentity.Label) { state.By = SessionIdentity.Label; state.From = here; state.At = DateTimeOffset.UtcNow; PluginGit.WriteState(main, state); }
                    var inc = DeployIncidents.Deployed(folder, mainHead.Sha, mainHead.Subject, state.BeforeBranch, state.BeforeSha, r.Games, main, takeover);
                    r.Incident = IncidentTools.View(inc, Incidents.IncidentStore.ReadHeartbeats());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { r.Warnings.Add($"The deploy's lease isn't on the incident board ({ex.Message})."); }
            if (reload) r.Reload = await Reload(bridges, folder, PickGames(r, game), $"reload {folder}", ct);
            r.HudRuns = HudRunsText(r);
            return Finish(r, "unchanged", r.Reload?.Ok != false, null);
        }

        string? leaseId = null;
        IDisposable? srcLock = null;
        var g = PickGames(r, game);
        try
        {
            // One change to the HUD source at a time (hud-live.ps1 sync, the F8 sync, other deploys). Taken before the
            // reload lease, so waiting for it never holds another agent's restart back.
            try { srcLock = await HudSource.LockAsync(srcRoot, $"deploy_plugin {folder} {Short(r.Edited.Commit)}", LockWait, ct); }
            catch (IOException ex) { return Finish(r, "refused", false, $"{ex.Message}. Nothing was touched: try again in a minute."); }
            leaseId = await Lease(bridges, g.FirstOrDefault(), $"deploying {folder} {Short(r.Edited.Commit)} from {SessionIdentity.Label}", ct);
            if (PluginGit.ReadHead(main, out _) is { } now && now.Sha != mainHead.Sha)
                return Finish(r, "refused", false, $"{folder} in {src} moved to {Short(now.Sha)} while this deploy waited (a sync or another deploy). Nothing was touched: run deploy_plugin again.");
            // Fetch the worktree's HEAD by path (a local fetch: no remote, no push), then check it out detached.
            var fetch = PluginGit.Run(main, "fetch", "--quiet", "--no-tags", edited, "HEAD");
            if (!fetch.Ok) return Finish(r, "failed", false, $"git fetch from {edited} into {src} failed: {fetch.Err}");
            var got = PluginGit.Run(main, "rev-parse", "FETCH_HEAD").Line;
            if (got != r.Edited.Commit) return Finish(r, "failed", false, $"Fetched {Short(got)}, expected {Short(r.Edited.Commit)} (the worktree moved meanwhile?). Nothing checked out.");
            var before = state ?? new PluginGit.DeployState { BeforeBranch = mainHead.Branch, BeforeSha = mainHead.Sha };
            var co = PluginGit.Run(main, "checkout", "--quiet", "--detach", got);
            if (!co.Ok) return Finish(r, "failed", false, $"git checkout {Short(got)} in {src} failed (it is still at {Short(mainHead.Sha)}): {co.Err}");
            before.Plugin = folder; before.DeployedSha = got; before.DeployedBranch = r.Edited.Branch; before.Subject = r.Edited.Subject;
            before.By = SessionIdentity.Label; before.From = here; before.At = DateTimeOffset.UtcNow;
            PluginGit.WriteState(main, before);
            r.Deployed = StateInfo(before);
            // The deploy as a lease on the incident board: every session sees it, and it is orphaned if this session goes away.
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var inc = DeployIncidents.Deployed(folder, got, r.Edited.Subject, before.BeforeBranch, before.BeforeSha, r.Games, main, takeover);
                    r.Incident = IncidentTools.View(inc, Incidents.IncidentStore.ReadHeartbeats());
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Without the lease, the orphan scan would offer this live deploy to anyone: say so loudly.
                    if (attempt == 3)
                        r.Warnings.Add($"Deployed, but NOT recorded on the incident board ({ex.Message}): other sessions may take it for an orphaned deploy. Run deploy_plugin action=deploy again to record it.");
                    else await Task.Delay(500, ct);
                }
            }
            r.Before = new DeployCheckout { Path = main, Commit = before.BeforeSha, Branch = before.BeforeBranch };
            r.Main = Checkout(main, PluginGit.ReadHead(main, out _) ?? mainHead);
        }
        finally
        {
            // Released before reloading: the reload is itself a blocker while it compiles, and our own lease would hold a
            // bridge restart back. The reload only reads the tree, so the source lock goes too.
            srcLock?.Dispose();
            if (leaseId != null) await Release(bridges, g.FirstOrDefault(), leaseId, ct);
        }
        if (reload) r.Reload = await Reload(bridges, folder, g, $"deploy {folder} {Short(r.Main.Commit)}", ct);
        r.HudRuns = HudRunsText(r);
        return Finish(r, "deployed", r.Reload?.Ok != false, StatusNote(r, false));
    }

    private static async Task<CallToolResult> Restore(BridgeRegistry bridges, DeployResult r, string main, PluginGit.Head mainHead,
        PluginGit.DeployState? state, bool reload, string? game, string? takeover, CancellationToken ct)
    {
        var src = r.Source != null ? HudSource.Describe(r.Source) : "the HUD source";
        if (state == null)
        {
            // Nothing recorded and not detached off origin/main: no deploy to hold (a submodule update leaves it detached
            // at the recorded commit, which is not a deploy).
            if (mainHead.Branch != null || PluginGit.Run(main, "merge-base", "--is-ancestor", mainHead.Sha, "origin/main").Code == 0)
                try { DeployIncidents.Restored(r.Plugin, $"{r.Plugin} in {src} is at {Short(mainHead.Sha)}{(mainHead.Branch != null ? $" on {mainHead.Branch}" : " (on origin/main)")}", main); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { r.Warnings.Add($"The incident board wasn't updated ({ex.Message})."); }
            return Finish(r, "unchanged", true, $"Nothing deployed into {r.Plugin} in {src}: it is at {Short(mainHead.Sha)}{(mainHead.Branch != null ? $" on {mainHead.Branch}" : " (detached; deploy_plugin recorded nothing to return to: switch it back by hand, or deploy over it)")}.");
        }
        if (mainHead.Changed.Count > 0)
            return Finish(r, "refused", false, $"{r.Plugin} in {src} has {mainHead.Changed.Count} uncommitted change(s) ({string.Join(", ", mainHead.Changed.Take(5))}): not touched. Commit or stash them there first.");
        if (mainHead.Sha != state.DeployedSha)
            return Finish(r, "refused", false, $"{r.Plugin} in {src} is at {Short(mainHead.Sha)}, not the deployed {Short(state.DeployedSha)}: someone moved it since. Left as is (the deploy record stays: check it, then delete {PluginGit.StatePath(main)} by hand).");
        var g = PickGames(r, game);
        string? leaseId = null;
        IDisposable? srcLock = null;
        // hud-live follows origin/main: its syncs left this plugin alone while it was deployed, so restore goes to the
        // commit hud-live records now, not the one it had before the deploy.
        var toPointer = r.SourceTree == "hud-live" && state.BeforeBranch == null && r.Pointer != null;
        var target = toPointer ? r.Pointer! : state.BeforeSha;
        try
        {
            if (r.Source != null)
                try { srcLock = await HudSource.LockAsync(r.Source, $"deploy_plugin restore {r.Plugin}", LockWait, ct); }
                catch (IOException ex) { return Finish(r, "refused", false, $"{ex.Message}. Nothing was touched: try again in a minute."); }
            leaseId = await Lease(bridges, g.FirstOrDefault(), $"restoring {r.Plugin} to {state.BeforeBranch ?? Short(target)}", ct);
            if (toPointer && !PluginGit.Run(main, "cat-file", "-e", target + "^{commit}").Ok)
            {
                var fetched = PluginGit.Run(main, "fetch", "--quiet", "origin");
                if (!fetched.Ok) return Finish(r, "failed", false, $"hud-live records {Short(target)} for {r.Plugin}, which its checkout doesn't have, and git fetch failed: {fetched.Err}");
            }
            var co = state.BeforeBranch != null && PluginGit.Run(main, "rev-parse", "--verify", "--quiet", $"refs/heads/{state.BeforeBranch}").Ok
                ? PluginGit.Run(main, "checkout", "--quiet", state.BeforeBranch)
                : PluginGit.Run(main, "checkout", "--quiet", "--detach", target);
            if (!co.Ok) return Finish(r, "failed", false, $"git checkout in {src} failed (still at {Short(mainHead.Sha)}): {co.Err}");
            PluginGit.WriteState(main, null);
            var back = state.BeforeBranch ?? Short(target);
            if (toPointer && target != state.BeforeSha)
                r.Warnings.Add($"Restored to {Short(target)}, the commit hud-live records now (it was at {Short(state.BeforeSha)} before the deploy; syncs moved on meanwhile).");
            try { DeployIncidents.Restored(r.Plugin, $"restored to {back} by {SessionIdentity.Label}" + (takeover != null ? $" ({takeover})" : ""), main); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { r.Warnings.Add($"Restored, but the incident board wasn't updated ({ex.Message})."); }
            r.Incident = null;
            r.Before = r.Main;
            r.Main = Checkout(main, PluginGit.ReadHead(main, out _) ?? mainHead);
            r.Deployed = null;
        }
        finally
        {
            srcLock?.Dispose();
            if (leaseId != null) await Release(bridges, g.FirstOrDefault(), leaseId, ct);
        }
        if (reload) r.Reload = await Reload(bridges, r.Plugin, g, $"restore {r.Plugin}", ct);
        r.HudRuns = HudRunsText(r);
        return Finish(r, "restored", r.Reload?.Ok != false, StatusNote(r, false));
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>The scaffolding checkout holding <paramref name="path"/> (the folder with tools\restart-hud.ps1), for an explicit worktree=.</summary>
    private static string ScaffoldingRoot(string path)
    {
        path = path.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path)) throw new McpException($"worktree '{path}' is relative: pass the absolute path of your worktree (this server's working directory is not yours).");
        if (!Directory.Exists(path)) throw new McpException($"worktree '{path}' does not exist.");
        for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "restart-hud.ps1"))) return dir.FullName;
        throw new McpException($"worktree '{path}' is not in a scaffolding checkout (no tools\\restart-hud.ps1 in it or above it).");
    }

    private static string FindFolder(string pluginsDir, string query)
    {
        if (!Directory.Exists(pluginsDir)) throw new McpException($"No Plugins folder at {pluginsDir}.");
        var dirs = Directory.GetDirectories(pluginsDir).Select(Path.GetFileName).OfType<string>().ToList();
        var exact = dirs.FirstOrDefault(d => string.Equals(d, query, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var q = query.Replace(" ", "");
        var hits = dirs.Where(d => d.Replace(" ", "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Count switch
        {
            1 => hits[0],
            0 => throw new McpException($"No plugin folder matches '{query}' in {pluginsDir}."),
            _ => throw new McpException($"'{query}' matches {hits.Count} folders ({string.Join(", ", hits)}): be more specific."),
        };
    }

    private static DeployCheckout Checkout(string path, PluginGit.Head h) => new()
    {
        Path = path, Commit = h.Sha, Branch = h.Branch, Subject = h.Subject, CommittedAt = h.CommittedAt,
        Uncommitted = h.Changed.Count > 0 ? h.Changed : null,
    };

    private static DeployStateInfo StateInfo(PluginGit.DeployState s) => new()
    {
        Commit = s.DeployedSha, Branch = s.DeployedBranch, Subject = s.Subject, By = s.By, From = s.From, At = s.At,
        BeforeBranch = s.BeforeBranch, BeforeCommit = s.BeforeSha,
    };

    private static string Short(string? sha) => sha is { Length: >= 7 } ? sha[..7] : sha ?? "?";

    /// <summary>The games to reload: the one asked for, else every HUD compiling this folder whose bridge answers.</summary>
    private static List<string> PickGames(DeployResult r, string? game)
    {
        if (!string.IsNullOrWhiteSpace(game)) return [game.Trim().ToLowerInvariant()];
        return r.Games;
    }

    private static async Task<string?> Lease(BridgeRegistry bridges, string? game, string label, CancellationToken ct)
    {
        if (game == null) return null;
        try
        {
            var (_, l) = await bridges.CallAsync(game, "lease.acquire", new JObject { ["kind"] = "reload", ["label"] = label, ["ttlSec"] = 120 }, ct);
            if (l["error"]?.ToString() == "restart_pending") throw new McpException($"Not deployed: {l["message"]}");
            return l["id"]?.ToString();
        }
        catch (McpException ex) when (!ex.Message.StartsWith("Not deployed", StringComparison.Ordinal)) { return null; }   // HUD down: nothing to protect
    }

    private static async Task Release(BridgeRegistry bridges, string? game, string id, CancellationToken ct)
    {
        try { await bridges.CallAsync(game, "lease.release", new JObject { ["id"] = id }, ct); } catch { }
    }

    /// <summary>reload_plugin per running HUD; Whats An AI Bridge needs a coordinated restart instead.</summary>
    private static async Task<DeployReload> Reload(BridgeRegistry bridges, string folder, List<string> games, string reason, CancellationToken ct)
    {
        var running = games.Where(g => bridges.Bridges.Any(b => b.Game == g && b.LooksAvailable)).ToList();
        if (running.Count == 0)
            return new DeployReload { Kind = "skipped", Ok = true, Result = games.Count == 0 ? "No HUD compiles this folder." : $"No running HUD ({string.Join(", ", games)}): it compiles the new code at its next start." };
        var outs = new List<string>();
        var ok = true;
        var kind = folder == BridgeFolder ? "hud_restart" : "reload_plugin";
        foreach (var g in running)
        {
            CallToolResult call;
            try
            {
                call = kind == "hud_restart"
                    ? await SessionTools.HudRestart(bridges, reason, g, 600, false, false, ct)
                    : await HudDevTools.ReloadPlugin(bridges, folder, true, false, false, g, ct);
            }
            catch (McpException ex) { outs.Add($"{g}: {ex.Message}"); ok = false; continue; }
            var text = string.Join("\n", call.Content.OfType<TextContentBlock>().Select(t => t.Text));
            outs.Add($"{g}: {(text.Length > 1500 ? text[..1500] + "..." : text)}");
            if (call.IsError == true) ok = false;
        }
        return new DeployReload { Kind = kind, Game = string.Join(",", running), Ok = ok, Result = string.Join("\n", outs) };
    }

    private static string HudRunsText(DeployResult r)
    {
        var main = Short(r.Main?.Commit);
        var mine = r.Edited != null && r.Main?.Commit == r.Edited.Commit;
        var compiled = r.Reload switch
        {
            { Kind: "skipped" } x => $"not reloaded ({x.Result})",
            { Ok: true } => "compiled and loaded",
            { Ok: false } => "the reload FAILED (see reload.result): the HUD runs nothing or the previous build of it",
            null => "not reloaded (reload=false): the HUD still runs whatever it compiled last",
        };
        return $"The HUD compiles {r.Plugin} at {main}{(mine ? " (your commit)" : r.Edited != null ? $" (you edited {Short(r.Edited.Commit)})" : "")}: {compiled}.";
    }

    private static string StatusNote(DeployResult r, bool inMain)
    {
        var parts = new List<string>();
        var src = r.Source != null ? HudSource.Describe(r.Source) : "the HUD source";
        if (inMain) parts.Add($"This session runs in the HUD source, {src}: the HUD compiles your folder directly.");
        else if (r.Edited != null && r.Main != null)
            parts.Add(r.Edited.Commit == r.Main.Commit
                ? $"The HUD source, {src}, is at your commit {Short(r.Main.Commit)}."
                : $"The HUD source, {src}, is at {Short(r.Main.Commit)}, you are at {Short(r.Edited.Commit)}: the HUD doesn't have your code (deploy_plugin).");
        if (r.Deployed != null)
            parts.Add($"Deployed by {r.Deployed.By} ({Short(r.Deployed.Commit)}, {r.Deployed.At:u}); restore returns it to {r.Deployed.BeforeBranch ?? Short(r.Deployed.BeforeCommit)}.");
        if (r.Pointer != null && r.Main != null && r.Pointer != r.Main.Commit)
            parts.Add(r.SourceTree == "hud-live"
                ? r.Deployed != null
                    ? $"hud-live records {Short(r.Pointer)} for Plugins/{r.Plugin}; until restore, hud-live.ps1 sync and the F8 sync leave this plugin alone."
                    : $"hud-live records {Short(r.Pointer)} for Plugins/{r.Plugin} (it moved meanwhile): tools\\hud-live.ps1 sync brings the plugin there."
                : $"The scaffolding main checkout records {Short(r.Pointer)} for Plugins/{r.Plugin}, so it shows the plugin as modified until restore or a merged PR plus pointer bump. Don't commit that pointer from the main checkout by accident.");
        return string.Join(" ", parts);
    }

    private static CallToolResult Finish(DeployResult r, string status, bool ok, string? note)
    {
        r.Status = status; r.Ok = ok;
        if (note != null) r.Note = r.Note != null ? r.Note + " " + note : note;
        var lines = new List<string> { $"{r.Plugin}: {status}{(r.Games.Count > 0 ? $" (HUD: {string.Join(", ", r.Games)})" : "")}" };
        if (r.Edited != null) lines.Add($"  you edited:    {Short(r.Edited.Commit)} {r.Edited.Branch ?? "(detached)"} {r.Edited.Subject}");
        if (r.Main != null) lines.Add($"  HUD source:    {Short(r.Main.Commit)} {r.Main.Branch ?? "(detached)"} {r.Main.Subject}{(r.SourceTree != null ? $" [{r.SourceTree}]" : "")}");
        if (r.HudRuns != null) lines.Add("  " + r.HudRuns);
        foreach (var w in r.Warnings) lines.Add("  ! " + w);
        if (r.Note != null) lines.Add("  " + r.Note);
        if (r.Incident is { State: "open" } inc)
            lines.Add($"  lease: {inc.Id} by {inc.RaisedBy}{(inc.Orphaned == true ? $", ORPHANED ({inc.OwnerState})" : inc.OwnerState != null ? $", {inc.OwnerState}" : "")}");
        if (r.Reload?.Result is { } res) lines.Add("  reload: " + res.Replace("\n", "\n    "));
        var call = Dto.Result(r, string.Join("\n", lines));
        if (!ok) call.IsError = true;
        return call;
    }
}
