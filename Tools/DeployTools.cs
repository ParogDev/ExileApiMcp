using System.ComponentModel;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using ExileApiMcp.Hud;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Worktree plugin deployment. The HUDs compile every plugin from &lt;HUD&gt;\Plugins\Source\&lt;folder&gt;, a junction into
/// the scaffolding's MAIN checkout, so a session working in a worktree never reaches the running HUD by editing. This
/// moves the worktree submodule's committed HEAD into the main checkout's submodule (git fetch from the worktree, then
/// a detached checkout), under a reload lease so no restart lands in the middle, then reloads the plugin (or restarts
/// the HUD for Whats An AI Bridge) and reports what the HUD runs against what this session edited.
/// Never touches uncommitted work: a dirty main-checkout submodule is refused, uncommitted worktree edits are named as
/// not deployed. The main checkout's previous branch / commit is kept in the submodule's git dir (hexile-deploy.json)
/// so action=restore puts it back; until then the scaffolding shows the plugin as modified, and every result says so.
/// </summary>
[McpServerToolType]
public static class DeployTools
{
    private const string BridgeFolder = "Whats An AI Bridge";

    [McpServerTool(Name = "deploy_plugin", Title = "Put a worktree's plugin commit into the HUD", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(DeployResult))]
    [Description("From a worktree session: make the running HUD compile your plugin's committed code. The HUD compiles plugins " +
                 "from the MAIN checkout's Plugins folders (junctions), never a worktree, so edits in a worktree don't reach it. " +
                 "action=deploy fetches your worktree submodule's HEAD commit into the main checkout's submodule (detached; " +
                 "refused when the main checkout has uncommitted changes there, or another session's deploy is active), under " +
                 "a reload lease, then reload_plugin (hud_restart for Whats An AI Bridge). Uncommitted worktree edits are not " +
                 "deployed: commit first. action=restore puts the main checkout back to the branch/commit it was on; " +
                 "action=status shows what the HUD compiles vs what you edited. Until restore (or a merged PR and a pointer " +
                 "bump), the scaffolding main checkout shows the plugin as modified: the result says so.")]
    public static async Task<CallToolResult> DeployPlugin(BridgeRegistry bridges,
        [Description("Plugin folder (or a unique part of it), e.g. 'Whats A Route'")] string plugin,
        [Description("deploy (default) | restore | status")] string action = "deploy",
        [Description("Reload after deploy / restore (default true): reload_plugin, or hud_restart for the bridge")] bool reload = true,
        [Description("deploy: replace another session's active deploy of this plugin (it is told nothing: ask first)")] bool force = false,
        [Description("poe1 | poe2: the HUD to reload; default every running HUD that compiles this folder")] string? game = null,
        CancellationToken ct = default)
    {
        var act = action.Trim().ToLowerInvariant();
        if (act is not ("deploy" or "restore" or "status")) throw new McpException("action must be deploy, restore or status.");
        var r = new DeployResult { Action = act };

        // ── Where things are: this session's checkout, the main checkout, the HUD junctions ──
        var here = SessionIdentity.RepoRoot() ?? throw new McpException("Not running in the scaffolding repo (tools\\restart-hud.ps1 not found from the cwd; HEXILE_REPO unset).");
        var common = PluginGit.Run(here, "rev-parse", "--path-format=absolute", "--git-common-dir");
        if (!common.Ok) throw new McpException($"git rev-parse --git-common-dir in {here}: {common.Err}");
        var mainRoot = Path.GetDirectoryName(common.Line.Replace('/', '\\').TrimEnd('\\'))!;
        var folder = FindFolder(Path.Combine(here, "Plugins"), plugin);
        r.Plugin = folder;
        var edited = Path.Combine(here, "Plugins", folder);
        var main = Path.Combine(mainRoot, "Plugins", folder);
        var inMain = string.Equals(Path.GetFullPath(here).TrimEnd('\\'), Path.GetFullPath(mainRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        var installs = HudDevTools.Installs(bridges, null);
        foreach (var hud in installs)
        {
            var link = Path.Combine(hud.SourcePluginsDir, folder);
            if (Directory.Exists(link) && string.Equals(PluginGit.Resolve(link).TrimEnd('\\'), Path.GetFullPath(main).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                r.Games.Add(hud.Game);
        }
        if (r.Games.Count == 0)
            r.Warnings.Add($"No HUD's Plugins\\Source\\{folder} points at {main}: the HUD doesn't compile this folder (missing junction?).");

        var mainHead = PluginGit.ReadHead(main, out var mainWhy) ?? throw new McpException($"Main checkout's {folder}: {mainWhy}");
        r.Main = Checkout(main, mainHead);
        var pointer = PluginGit.Run(mainRoot, "ls-tree", "HEAD", "--", $"Plugins/{folder}");
        r.Pointer = pointer.Ok && pointer.Line.Split(' ', '\t') is [_, "commit", var sha, ..] ? sha : null;
        var state = PluginGit.ReadState(main);
        if (state != null) r.Deployed = StateInfo(state);
        if (!inMain && PluginGit.ReadHead(edited, out _) is { } editedHead) r.Edited = Checkout(edited, editedHead);

        if (act == "status") return Finish(r, "status", true, StatusNote(r, inMain));
        if (act == "restore") return await Restore(bridges, r, main, mainHead, state, reload, game, ct);

        // ── Deploy ──
        if (inMain)
        {
            r.Note = "This session runs in the main checkout: the HUD compiles your folder directly, nothing to deploy.";
            if (reload) r.Reload = await Reload(bridges, folder, PickGames(r, game), $"reload {folder}", ct);
            return Finish(r, "unchanged", r.Reload?.Ok != false, null);
        }
        if (r.Edited == null)
            return Finish(r, "refused", false, $"{edited} is not a git checkout here: initialise the submodule in this worktree (git submodule update --init \"Plugins/{folder}\").");
        if (r.Edited.Uncommitted is { Count: > 0 } wip)
            r.Warnings.Add($"{wip.Count} uncommitted change(s) in your worktree are NOT deployed (only commits are): {string.Join(", ", wip.Take(5))}{(wip.Count > 5 ? ", ..." : "")}. Commit them and deploy again.");
        if (mainHead.Changed.Count > 0)
            return Finish(r, "refused", false, $"The main checkout's {folder} has {mainHead.Changed.Count} uncommitted change(s) ({string.Join(", ", mainHead.Changed.Take(5))}): someone is editing there. Nothing was touched.");
        if (state != null && state.By != SessionIdentity.Label && !force)
            return Finish(r, "refused", false, $"{state.By} deployed {Short(state.DeployedSha)} ({state.Subject}) from {state.From} at {state.At:u}. Ask them (or the user), then restore it or pass force=true.");
        if (state != null && mainHead.Sha != state.DeployedSha)
            r.Warnings.Add($"The main checkout moved since the last deploy ({Short(state.DeployedSha)} -> {Short(mainHead.Sha)}); restore will return to {state.BeforeBranch ?? Short(state.BeforeSha)} as first recorded.");
        if (mainHead.Sha == r.Edited.Commit)
        {
            r.Note = $"The main checkout is already at {Short(mainHead.Sha)}.";
            if (reload) r.Reload = await Reload(bridges, folder, PickGames(r, game), $"reload {folder}", ct);
            r.HudRuns = HudRunsText(r);
            return Finish(r, "unchanged", r.Reload?.Ok != false, null);
        }

        string? leaseId = null;
        var g = PickGames(r, game);
        try
        {
            leaseId = await Lease(bridges, g.FirstOrDefault(), $"deploying {folder} {Short(r.Edited.Commit)} from {SessionIdentity.Label}", ct);
            // Fetch the worktree's HEAD by path (a local fetch: no remote, no push), then check it out detached.
            var fetch = PluginGit.Run(main, "fetch", "--quiet", "--no-tags", edited, "HEAD");
            if (!fetch.Ok) return Finish(r, "failed", false, $"git fetch from {edited} into the main checkout failed: {fetch.Err}");
            var got = PluginGit.Run(main, "rev-parse", "FETCH_HEAD").Line;
            if (got != r.Edited.Commit) return Finish(r, "failed", false, $"Fetched {Short(got)}, expected {Short(r.Edited.Commit)} (the worktree moved meanwhile?). Nothing checked out.");
            var before = state ?? new PluginGit.DeployState { BeforeBranch = mainHead.Branch, BeforeSha = mainHead.Sha };
            var co = PluginGit.Run(main, "checkout", "--quiet", "--detach", got);
            if (!co.Ok) return Finish(r, "failed", false, $"git checkout {Short(got)} in the main checkout failed (it is still at {Short(mainHead.Sha)}): {co.Err}");
            before.Plugin = folder; before.DeployedSha = got; before.DeployedBranch = r.Edited.Branch; before.Subject = r.Edited.Subject;
            before.By = SessionIdentity.Label; before.From = here; before.At = DateTimeOffset.UtcNow;
            PluginGit.WriteState(main, before);
            r.Deployed = StateInfo(before);
            r.Before = new DeployCheckout { Path = main, Commit = before.BeforeSha, Branch = before.BeforeBranch };
            r.Main = Checkout(main, PluginGit.ReadHead(main, out _) ?? mainHead);
        }
        finally
        {
            // Released before reloading: the reload is itself a blocker while it compiles, and our own lease would hold a
            // bridge restart back.
            if (leaseId != null) await Release(bridges, g.FirstOrDefault(), leaseId, ct);
        }
        if (reload) r.Reload = await Reload(bridges, folder, g, $"deploy {folder} {Short(r.Main.Commit)}", ct);
        r.HudRuns = HudRunsText(r);
        return Finish(r, "deployed", r.Reload?.Ok != false, StatusNote(r, false));
    }

    private static async Task<CallToolResult> Restore(BridgeRegistry bridges, DeployResult r, string main, PluginGit.Head mainHead,
        PluginGit.DeployState? state, bool reload, string? game, CancellationToken ct)
    {
        if (state == null) return Finish(r, "unchanged", true, $"Nothing deployed into the main checkout's {r.Plugin}: it is at {Short(mainHead.Sha)}{(mainHead.Branch != null ? $" on {mainHead.Branch}" : " (detached)")}.");
        if (mainHead.Changed.Count > 0)
            return Finish(r, "refused", false, $"The main checkout's {r.Plugin} has {mainHead.Changed.Count} uncommitted change(s) ({string.Join(", ", mainHead.Changed.Take(5))}): not touched. Commit or stash them there first.");
        if (mainHead.Sha != state.DeployedSha)
            return Finish(r, "refused", false, $"The main checkout's {r.Plugin} is at {Short(mainHead.Sha)}, not the deployed {Short(state.DeployedSha)}: someone moved it since. Left as is (the deploy record stays: check it, then delete {PluginGit.StatePath(main)} by hand).");
        var g = PickGames(r, game);
        string? leaseId = null;
        try
        {
            leaseId = await Lease(bridges, g.FirstOrDefault(), $"restoring {r.Plugin} to {state.BeforeBranch ?? Short(state.BeforeSha)}", ct);
            var co = state.BeforeBranch != null && PluginGit.Run(main, "rev-parse", "--verify", "--quiet", $"refs/heads/{state.BeforeBranch}").Ok
                ? PluginGit.Run(main, "checkout", "--quiet", state.BeforeBranch)
                : PluginGit.Run(main, "checkout", "--quiet", "--detach", state.BeforeSha);
            if (!co.Ok) return Finish(r, "failed", false, $"git checkout in the main checkout failed (still at {Short(mainHead.Sha)}): {co.Err}");
            PluginGit.WriteState(main, null);
            r.Before = r.Main;
            r.Main = Checkout(main, PluginGit.ReadHead(main, out _) ?? mainHead);
            r.Deployed = null;
        }
        finally
        {
            if (leaseId != null) await Release(bridges, g.FirstOrDefault(), leaseId, ct);
        }
        if (reload) r.Reload = await Reload(bridges, r.Plugin, g, $"restore {r.Plugin}", ct);
        r.HudRuns = HudRunsText(r);
        return Finish(r, "restored", r.Reload?.Ok != false, StatusNote(r, false));
    }

    // ── Helpers ──────────────────────────────────────────────────────

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
        if (inMain) parts.Add("This session runs in the main checkout: the HUD compiles your folder directly.");
        else if (r.Edited != null && r.Main != null)
            parts.Add(r.Edited.Commit == r.Main.Commit
                ? $"The main checkout is at your commit {Short(r.Main.Commit)}."
                : $"The main checkout is at {Short(r.Main.Commit)}, you are at {Short(r.Edited.Commit)}: the HUD doesn't have your code (deploy_plugin).");
        if (r.Deployed != null)
            parts.Add($"Deployed by {r.Deployed.By} ({Short(r.Deployed.Commit)}, {r.Deployed.At:u}); restore returns it to {r.Deployed.BeforeBranch ?? Short(r.Deployed.BeforeCommit)}.");
        if (r.Pointer != null && r.Main != null && r.Pointer != r.Main.Commit)
            parts.Add($"The scaffolding main checkout records {Short(r.Pointer)} for Plugins/{r.Plugin}, so it shows the plugin as modified until restore or a merged PR plus pointer bump. Don't commit that pointer from the main checkout by accident.");
        return string.Join(" ", parts);
    }

    private static CallToolResult Finish(DeployResult r, string status, bool ok, string? note)
    {
        r.Status = status; r.Ok = ok;
        if (note != null) r.Note = r.Note != null ? r.Note + " " + note : note;
        var lines = new List<string> { $"{r.Plugin}: {status}{(r.Games.Count > 0 ? $" (HUD: {string.Join(", ", r.Games)})" : "")}" };
        if (r.Edited != null) lines.Add($"  you edited:    {Short(r.Edited.Commit)} {r.Edited.Branch ?? "(detached)"} {r.Edited.Subject}");
        if (r.Main != null) lines.Add($"  main checkout: {Short(r.Main.Commit)} {r.Main.Branch ?? "(detached)"} {r.Main.Subject}");
        if (r.HudRuns != null) lines.Add("  " + r.HudRuns);
        foreach (var w in r.Warnings) lines.Add("  ! " + w);
        if (r.Note != null) lines.Add("  " + r.Note);
        if (r.Reload?.Result is { } res) lines.Add("  reload: " + res.Replace("\n", "\n    "));
        var call = Dto.Result(r, string.Join("\n", lines));
        if (!ok) call.IsError = true;
        return call;
    }
}
