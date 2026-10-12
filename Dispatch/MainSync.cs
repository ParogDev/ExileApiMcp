using ExileApiMcp.Bridge;
using ExileApiMcp.Incidents;
using ExileApiMcp.Tools;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Dispatch;

/// <summary>
/// After an F8 handler ends, the dispatcher brings its merged fix into the HUD: the HUD compiles Whats A Route from the main
/// scaffolding checkout, and a handler can't touch that checkout (Claude Code's worktree isolation refuses every command
/// aimed at it), so this runs here, outside Claude Code. When origin/main is ahead of the main checkout: `pull --ff-only`,
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
        var fetch = FeedbackDispatcher.Git(repo, "fetch -q origin main", 120_000);
        if (fetch.Code != 0) return Fail($"git fetch in the main checkout failed: {Line(fetch.Output)}", "Check the network and GitHub credentials, then pull the main checkout.");
        var behind = FeedbackDispatcher.Git(repo, "rev-list --count HEAD..origin/main");
        if (behind.Code != 0) return Fail($"git rev-list in the main checkout failed: {Line(behind.Output)}", null);
        if (behind.Output.Trim() == "0") return new Outcome(false, "the main checkout already has origin/main");
        var branch = FeedbackDispatcher.Git(repo, "rev-parse --abbrev-ref HEAD").Output.Trim();
        if (branch != "main")
            return Fail($"the main checkout is on '{branch}', not main, so it was not pulled", "Switch the main checkout back to main, then pull it.");

        var sub = Path.Combine(repo, SubmodulePath.Replace('/', Path.DirectorySeparatorChar));
        var before = FeedbackDispatcher.Git(sub, "rev-parse HEAD").Output.Trim();
        var pull = FeedbackDispatcher.Git(repo, "pull --ff-only -q origin main", 120_000);
        if (pull.Code != 0)
            return Fail($"`git pull --ff-only` in the main checkout refused: {Line(pull.Output)}", "Nothing was reset or merged: sort out the main checkout by hand, then pull it.");

        // A live deploy_plugin deploy of the plugin (another session trying a commit in the HUD) is left alone.
        var owner = DeployIncidents.Owner(Plugin);
        if (owner.Incident != null && !owner.Orphaned)
            return Fail($"{owner.Owner} has a deploy of {Plugin} in the main checkout ({owner.Why}), so the submodule was not updated",
                $"Once {owner.Owner} restores it (deploy_plugin action=restore), the main checkout's submodule update brings the fix.");

        var update = FeedbackDispatcher.Git(repo, $"submodule update -- \"{SubmodulePath}\"", 120_000);
        if (update.Code != 0)
            return Fail($"`git submodule update` of {Plugin} in the main checkout failed: {Line(update.Output)}", "Nothing was reset: look at the plugin's checkout in the main checkout.");
        var after = FeedbackDispatcher.Git(sub, "rev-parse HEAD").Output.Trim();
        if (after == before) return new Outcome(false, $"pulled the main checkout; {Plugin} unchanged at {Short(after)}");

        JObject reload;
        try { reload = await HudDevTools.ReloadAsync(bridges, Plugin, wait: true, force: false, perf: false, game, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Fail($"{Plugin} is at {Short(after)} in the main checkout, but its reload failed: {Line(ex.Message)}", "The HUD compiles it at its next start, or press the plugin's Reload in the HUD menu.");
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

    /// <summary>The card line for a failed sync: the fix is merged, but not in game yet.</summary>
    public static string FailText(long seq, Outcome o) =>
        FeedbackKit.Clip($"#{seq}'s fix is merged but not in game yet: {o.Why}." + (o.Fix != null ? $" {o.Fix}" : ""), 400);

    private static Outcome Fail(string why, string? fix) => new(true, why, why, fix);

    private static string Line(string s) => FeedbackKit.Clip(s.Trim().Split('\n')[0].Trim(), 220);

    private static string Short(string sha) => sha.Length >= 8 ? sha[..8] : sha;
}
