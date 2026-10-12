using ExileApiMcp.Bridge;
using ExileApiMcp.Hud;
using ExileApiMcp.Incidents;
using ExileApiMcp.Tools;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Dispatch;

/// <summary>
/// After an F8 handler ends, the dispatcher brings its merged fix into the HUD: the HUD compiles Whats A Route from the HUD
/// source (its junction's target: hud-live after tools\hud-live.ps1 cutover, the main scaffolding checkout before), and a
/// handler can't touch it (Claude Code's worktree isolation refuses every command aimed outside its worktree), so this
/// runs here, outside Claude Code, under the HUD source lock. When origin/main is ahead of it: `pull --ff-only`,
/// `submodule update -- "Plugins/Whats A Route"`, the same reload as MCP reload_plugin (its diagnostics must be empty), then
/// Settings.Route.IncludeOptional back to true when the saved setting says false. Never resets or merges: a refused pull,
/// a live deploy_plugin deploy of the plugin, or a failed reload is said on the card (feedback.reply status failed).
/// </summary>
public static class MainSync
{
    public const string Plugin = "Whats A Route", SubmodulePath = "Plugins/Whats A Route";

    /// <summary>What the sync did. Failed: the change is not in the HUD (Why says the broken link, Fix what to do).</summary>
    public sealed record Outcome(bool Failed, string Summary, string? Why = null, string? Fix = null);

    public static async Task<Outcome> RunAsync(BridgeRegistry bridges, string game, string repo, CancellationToken ct)
    {
        // repo is the HUD source (the junction's target): hud-live after tools\hud-live.ps1 cutover, the main checkout
        // before. Its git steps run under the HUD source lock (hud-live.ps1 sync and deploy_plugin take it too); the reload
        // after them only reads the tree.
        var src = HudSource.Describe(repo);
        string? after, before;
        bool pulled;
        IDisposable srcLock;
        try { srcLock = await HudSource.LockAsync(repo, $"F8 sync of {Plugin}", TimeSpan.FromSeconds(120), ct); }
        catch (IOException ex) { return Fail(Line(ex.Message), "Nothing changed: the next fix's sync, or tools\\hud-live.ps1 sync, brings it."); }
        using (srcLock)
        {
            var fetch = FeedbackDispatcher.Git(repo, "fetch -q origin main", 120_000);
            if (fetch.Code != 0) return Fail($"git fetch in {src} failed: {Line(fetch.Output)}", "Check the network and GitHub credentials, then pull it.");
            var behind = FeedbackDispatcher.Git(repo, "rev-list --count HEAD..origin/main");
            if (behind.Code != 0) return Fail($"git rev-list in {src} failed: {Line(behind.Output)}", null);
            pulled = behind.Output.Trim() != "0";
            if (pulled)
            {
                var branch = FeedbackDispatcher.Git(repo, "rev-parse --abbrev-ref HEAD").Output.Trim();
                if (branch != "main")
                    return Fail($"{src} is on '{branch}', not main, so it was not pulled", "Switch it back to main, then pull it.");
                var pull = FeedbackDispatcher.Git(repo, "pull --ff-only -q origin main", 120_000);
                if (pull.Code != 0)
                    return Fail($"`git pull --ff-only` in {src} refused: {Line(pull.Output)}", "Nothing was reset or merged: sort it out by hand, then pull it.");
            }

            // The plugin's checkout against the commit the HUD source records for it: behind also when someone else pulled
            // it without updating its submodules.
            var sub = Path.Combine(repo, SubmodulePath.Replace('/', Path.DirectorySeparatorChar));
            var pointer = PointerSha(FeedbackDispatcher.Git(repo, $"ls-tree HEAD -- \"{SubmodulePath}\"").Output);
            before = FeedbackDispatcher.Git(sub, "rev-parse HEAD").Output.Trim();
            if (pointer == null) return Fail($"{src} records no commit for {SubmodulePath}", null);
            if (before == pointer)
                return new Outcome(false, pulled ? $"pulled {src}; {Plugin} unchanged at {Short(before)}" : $"{src} and {Plugin} ({Short(before)}) are up to date");

            // A live deploy_plugin deploy of the plugin (another session trying a commit in the HUD) is left alone: live by
            // its lease, or recorded in the submodule with no lease at all (its board write failed: deploy_plugin treats
            // that as live too). Only an orphaned lease (its session gone) is replaced.
            var owner = DeployIncidents.Owner(Plugin, sub);
            var recorded = PluginGit.ReadState(sub);
            if ((owner.Incident != null && !owner.Orphaned) || (owner.Incident == null && recorded != null))
            {
                var who = owner.Incident != null ? owner.Owner : recorded!.By;
                var why = owner.Incident != null ? owner.Why : "recorded in the submodule, no lease on the incident board";
                return pulled
                    ? Fail($"{who} has a deploy of {Plugin} in {src} ({why}), so the submodule was not updated",
                        $"Once {who}'s deploy is restored (deploy_plugin action=restore), tools\\hud-live.ps1 sync (or the next fix's sync) brings the fix.")
                    : new Outcome(false, $"{src} is up to date; {Plugin} is held by {who}'s deploy");
            }

            var update = FeedbackDispatcher.Git(repo, $"submodule update -- \"{SubmodulePath}\"", 120_000);
            if (update.Code != 0)
                return Fail($"`git submodule update` of {Plugin} in {src} failed: {Line(update.Output)}", "Nothing was reset: look at the plugin's checkout there.");
            after = FeedbackDispatcher.Git(sub, "rev-parse HEAD").Output.Trim();
            if (after != pointer) return Fail($"{Plugin} is at {Short(after)} after `git submodule update`, not at {Short(pointer)}", $"Look at the plugin's checkout in {src}.");
            // An orphaned deploy it just replaced: its record and its lease go, so restore can't later undo the fix.
            if (PluginGit.ReadState(sub) is { } orphan)
            {
                try { PluginGit.WriteState(sub, null); DeployIncidents.Restored(Plugin, $"{orphan.By}'s orphaned deploy replaced by the F8 sync ({Short(after)})", sub); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"[Dispatch] sync: clearing {orphan.By}'s orphaned deploy record: {ex.Message}"); }
            }
        }

        JObject reload;
        try { reload = await HudDevTools.ReloadAsync(bridges, Plugin, wait: true, force: false, perf: false, game, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Fail($"{Plugin} is at {Short(after)} in {src}, but its reload failed: {Line(ex.Message)}", "The HUD compiles it at its next start, or press the plugin's Reload in the HUD menu.");
        }
        if (ReloadProblem(reload) is { } problem)
            return Fail($"{Plugin} {Short(after)} did not reload cleanly: {problem}", "The plugin stays off until it compiles: the next fix must repair it.");

        var optional = await RestoreIncludeOptionalAsync(bridges, game, ct);
        return new Outcome(false, $"{Plugin} {Short(before)} -> {Short(after)}, reloaded clean" + (optional != null ? $"; {optional}" : ""));
    }

    /// <summary>Settings.Route.IncludeOptional back to true when the loaded setting says false (a reload re-reads the saved
    /// settings). Returns what it did, or null when it was already on.</summary>
    private static async Task<string?> RestoreIncludeOptionalAsync(BridgeRegistry bridges, string game, CancellationToken ct)
    {
        try
        {
            var (_, d) = await bridges.CallAsync(game, "settings.describe", new JObject { ["plugin"] = Plugin }, ct);
            switch (IncludeOptional(d))
            {
                case true: return null;
                case null: return "Route.IncludeOptional not found in its settings (check it by hand)";
            }
            var (_, s) = await bridges.CallAsync(game, "settings.set", new JObject { ["plugin"] = Plugin, ["path"] = "Route.IncludeOptional", ["value"] = true }, ct);
            return s["error"] != null ? $"setting Route.IncludeOptional back to true failed: {s["error"]} {s["message"]}" : "Route.IncludeOptional set back to true";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"reading Route.IncludeOptional failed: {Line(ex.Message)}";
        }
    }

    /// <summary>Route.IncludeOptional in a settings.describe reply (null when the plugin or the setting isn't there).</summary>
    public static bool? IncludeOptional(JToken describe) =>
        (describe["plugins"] as JArray ?? []).OfType<JObject>()
            .Where(p => string.Equals(p["plugin"]?.ToString(), Plugin, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p["settings"] as JArray ?? []).OfType<JObject>()
            .FirstOrDefault(s => s["path"]?.ToString() == "Route.IncludeOptional")?["value"] is { Type: JTokenType.Boolean } v ? v.Value<bool>() : null;

    /// <summary>Why a reload_plugin result is not clean (not ok, an error, compiler diagnostics), or null when it is.</summary>
    public static string? ReloadProblem(JObject r)
    {
        if (r["diagnostics"] is JArray { Count: > 0 } diags)
            return $"{diags.Count} compiler error(s), first {diags[0]?["file"]}({diags[0]?["line"]}): {diags[0]?["code"]} {diags[0]?["message"]}";
        if (r["ok"]?.Type == JTokenType.Boolean && r["ok"]!.Value<bool>() && r["error"] == null) return null;
        return Line($"{r["error"] ?? "not ok"}{(r["message"] != null ? $": {r["message"]}" : "")}");
    }

    /// <summary>The commit in a `git ls-tree HEAD -- &lt;submodule&gt;` line ("160000 commit &lt;sha&gt;\t&lt;path&gt;"), or null.</summary>
    public static string? PointerSha(string lsTree) =>
        System.Text.RegularExpressions.Regex.Match(lsTree, @"^160000 commit ([0-9a-f]{40})\t", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The card line for a failed sync: the fix is merged, but not in game yet.</summary>
    public static string FailText(long seq, Outcome o) =>
        FeedbackKit.Clip($"#{seq}'s fix is merged but not in game yet: {o.Why}." + (o.Fix != null ? $" {o.Fix}" : ""), 400);

    private static Outcome Fail(string why, string? fix) => new(true, why, why, fix);

    private static string Line(string s) => FeedbackKit.Clip(s.Trim().Split('\n')[0].Trim(), 220);

    private static string Short(string sha) => sha.Length >= 8 ? sha[..8] : sha;
}
