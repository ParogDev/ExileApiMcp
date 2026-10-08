using System.ComponentModel;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hud;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// The plugin dev loop, read from the HUD folders on disk: did my plugin compile, and what did the
/// HUD log? No bridge, game or running HUD needed. The HUD compiles source plugins at startup (or
/// from the menu's Reload button; its file watcher only reloads compiled DLLs), so: edit -> restart
/// the HUD -> hud_plugins (compiled?) -> hud_log (runtime errors?) -> live tools.
/// </summary>
[McpServerToolType]
public static partial class HudDevTools
{
    private const string GameOpt = "'poe1' or 'poe2'; omit for every HUD installed";
    private const int MaxText = 3000;

    [McpServerTool(Name = "hud_plugins", Title = "Source plugin compile status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Did the HUD compile and load each source plugin in its latest run? Per plugin: status " +
                 "(loaded | cached | failed | not-seen), when, the compiler error for failures, counts of runtime " +
                 "errors/warnings it logged, and its Errors.txt - flagged stale when older than the last successful " +
                 "compile (the HUD never deletes it). Reads HUD folders on disk: works with the game and HUD closed. " +
                 "Use after a HUD restart (or the plugin's Reload button) to confirm an edit compiled: the HUD " +
                 "compiles source plugins at startup, not on save.")]
    public static CallToolResult HudPlugins(BridgeRegistry bridges,
        [Description("Plugin folder or project name (substring, case-insensitive); omit for all")] string? plugin = null,
        [Description(GameOpt)] string? game = null)
    {
        var result = new JArray();
        foreach (var hud in Installs(bridges, game))
        {
            var run = hud.LatestRun();
            var plugins = new JArray();
            foreach (var p in hud.SourcePlugins())
            {
                if (plugin != null && !Matches(p, plugin)) continue;
                plugins.Add(PluginStatus(hud, run, p));
            }
            result.Add(new JObject
            {
                ["game"] = hud.Game,
                ["hudRoot"] = hud.Root,
                ["run"] = RunInfo(run),
                ["plugins"] = plugins,
            });
        }
        return ToolResults.Json(new JObject { ["huds"] = result });
    }

    [McpServerTool(Name = "hud_log", Title = "HUD log (latest run)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("The HUD's log for its latest run (since its last start), newest last. Repeated messages are " +
                 "collapsed with a count, so noisy warnings don't drown real errors. Filter by minimum level, plugin " +
                 "and text. Paths outside the HUD folder are shortened. Works with the game and HUD closed.")]
    public static CallToolResult HudLog(BridgeRegistry bridges,
        [Description("Minimum level: 'error', 'warning' (default), 'info' or 'verbose'")] string level = "warning",
        [Description("Only entries mentioning this plugin (folder or project name, case-insensitive)")] string? plugin = null,
        [Description("Only entries containing this text (case-insensitive)")] string? contains = null,
        [Description("Most recent distinct messages to return (1-200, default 40)")] int max = 40,
        [Description(GameOpt)] string? game = null)
    {
        var minRank = level.ToLowerInvariant() switch
        {
            "error" or "err" => 4, "warning" or "warn" or "wrn" => 3, "info" or "inf" => 2, "verbose" or "debug" => 0,
            _ => throw new McpException($"Unknown level '{level}'. Use error, warning, info or verbose."),
        };
        max = Math.Clamp(max, 1, 200);

        var result = new JArray();
        foreach (var hud in Installs(bridges, game))
        {
            var run = hud.LatestRun();
            var names = plugin == null ? null
                : hud.SourcePlugins().Where(p => Matches(p, plugin)).SelectMany(Names).Append(plugin).Distinct().ToList();
            var selected = (run?.Entries ?? [])
                .Where(e => HudInstall.LevelRank(e.Level) >= minRank)
                .Where(e => contains == null || e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase))
                .Where(e => names == null || names.Any(n => e.Message.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var groups = selected
                .GroupBy(e => (e.Level, FirstLine(e.Message)))
                .Select(g => new { g.Key.Level, First = g.First(), Last = g.Last(), Count = g.Count() })
                .OrderBy(g => g.Last.Time)
                .ToList();
            var shown = groups.Skip(Math.Max(0, groups.Count - max)).Select(g =>
            {
                var o = new JObject
                {
                    ["level"] = g.Level,
                    ["at"] = g.Last.Time.ToString("O"),
                    ["message"] = Clip(hud.ForAgent(g.Last.Message)),
                };
                if (g.Count > 1)
                {
                    o["count"] = g.Count;
                    o["firstAt"] = g.First.Time.ToString("O");
                }
                return o;
            });

            result.Add(new JObject
            {
                ["game"] = hud.Game,
                ["run"] = RunInfo(run),
                ["counts"] = new JObject
                {
                    ["error"] = run?.Entries.Count(e => HudInstall.LevelRank(e.Level) >= 4) ?? 0,
                    ["warning"] = run?.Entries.Count(e => e.Level == "WRN") ?? 0,
                },
                ["matched"] = selected.Count,
                ["distinct"] = groups.Count,
                ["entries"] = new JArray(shown),
            });
        }
        return ToolResults.Json(new JObject { ["huds"] = result });
    }

    [McpServerTool(Name = "reload_plugin", Title = "Recompile a HUD plugin in place", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Recompile and reload one source plugin in the running HUD, like its Reload button in the HUD menu - " +
                 "no HUD restart. Waits for the result (the HUD pauses while compiling, usually 1-10 s) and returns ok/error " +
                 "plus any errors the plugin logged right after loading. Use after editing a plugin. The bridge plugin " +
                 "itself can't be reloaded this way (restart the HUD), and a brand-new plugin folder needs a restart too.")]
    public static async Task<CallToolResult> ReloadPlugin(BridgeRegistry bridges,
        [Description("Plugin folder or display name, e.g. 'Whats A Mirage'")] string plugin,
        [Description("Wait for the compile to finish (default true); false returns as soon as it is queued")] bool wait = true,
        [Description("Reload even when the HUD setting 'Avoid locking plugin dlls' is off (only safe when the code is unchanged)")] bool force = false,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var (bridge, queued) = await bridges.CallAsync(game, "hud.reload_plugin", new JObject { ["name"] = plugin, ["force"] = force }, ct);
        if (queued["queued"]?.Value<bool>() != true || !wait) return ToolResults.Json(queued);
        var folder = queued["plugin"]?.Value<string>() ?? plugin;

        JToken? last = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400, ct);
            JToken status;
            try { (_, status) = await bridges.CallAsync(bridge.Game == "auto" ? game : bridge.Game, "hud.reload_status", null, ct); }
            catch (McpException) { continue; } // the bridge can be briefly unreachable while the HUD compiles
            var l = status["last"];
            if (l?["plugin"]?.Value<string>() == folder && l["finishedAt"] != null
                && l["startedAt"]?.Value<DateTime>().ToUniversalTime() >= started.AddSeconds(-2))
            {
                last = l;
                break;
            }
        }
        if (last == null)
            return ToolResults.Json(new JObject { ["plugin"] = folder, ["error"] = "timeout", ["message"] = "No result after 120 s; check hud_log and hud.reload_status." });

        var result = new JObject { ["plugin"] = folder, ["ok"] = last["ok"], ["durationMs"] = last["durationMs"] };
        var hud = Installs(bridges, bridge.Game == "auto" ? game : bridge.Game).FirstOrDefault();
        if (last["error"] is { } err && hud != null) result["error"] = Clip(hud.ForAgent(err.ToString()));
        else if (last["error"] != null) result["error"] = last["error"];

        // Give the plugin a moment to initialise, then report what it logged since the reload started.
        if (hud != null)
        {
            await Task.Delay(750, ct);
            var src = hud.SourcePlugins().FirstOrDefault(p => string.Equals(p.Folder, folder, StringComparison.OrdinalIgnoreCase));
            var names = src != null ? Names(src).ToList() : [folder];
            var since = new DateTimeOffset(last["startedAt"]!.Value<DateTime>().ToUniversalTime());
            var logged = (hud.LatestRun()?.Entries ?? [])
                .Where(e => e.Time >= since && HudInstall.LevelRank(e.Level) >= 3 && IsFrom(e.Message, names))
                .Select(e => new JObject { ["level"] = e.Level, ["message"] = Clip(hud.ForAgent(e.Message)) })
                .Take(10).ToList();
            result["loggedSinceReload"] = new JArray(logged);
        }
        if (result["ok"]?.Value<bool>() == false)
        {
            result["error"] ??= "reload failed";
            // The HUD unloads the old assembly before compiling, so a failed reload leaves the plugin off.
            result["note"] = "The plugin stays unloaded until it compiles: fix the diagnostics and call reload_plugin again.";
            var diags = new JArray();
            var text = string.Join("\n", (result["loggedSinceReload"] as JArray ?? []).Select(x => x["message"]?.ToString()));
            foreach (System.Text.RegularExpressions.Match m in CompilerDiagnostic().Matches(text))
            {
                if (diags.Count >= 30) break;
                var file = m.Groups["path"].Value;
                var src = hud?.SourcePlugins().FirstOrDefault(p => file.StartsWith(p.Path + "\\", StringComparison.OrdinalIgnoreCase));
                diags.Add(new JObject
                {
                    ["file"] = src != null ? file[(src.Path.Length + 1)..] : file,
                    ["line"] = int.Parse(m.Groups["line"].Value),
                    ["col"] = int.Parse(m.Groups["col"].Value),
                    ["code"] = m.Groups["code"].Value,
                    ["message"] = m.Groups["msg"].Value.Trim(),
                });
            }
            if (diags.Count > 0) result["diagnostics"] = diags;
        }
        return ToolResults.Json(result);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    internal static List<HudInstall> Installs(BridgeRegistry bridges, string? game)
    {
        var g = game?.Trim().ToLowerInvariant();
        if (g is not (null or "" or "poe1" or "poe2"))
            throw new McpException($"Unknown game '{game}'. Use 'poe1' or 'poe2'.");
        var installs = bridges.Bridges
            .Select(b => HudInstall.FromBridgeDir(b.Game, b.BridgeDir))
            .OfType<HudInstall>()
            .Where(h => string.IsNullOrEmpty(g) || h.Game == g)
            .ToList();
        if (installs.Count == 0)
            throw new McpException(string.IsNullOrEmpty(g)
                ? "No HUD install found next to the configured bridge folders (POE1_BRIDGE_DIR / POE2_BRIDGE_DIR)."
                : $"No {g} HUD install found next to its bridge folder.");
        return installs;
    }

    private static JObject RunInfo(HudInstall.Run? run) => run == null
        ? new JObject { ["found"] = false }
        : new JObject
        {
            ["startedAt"] = run.StartedAt.ToString("O"),
            ["closedAt"] = run.ClosedAt?.ToString("O"),
            // No Close marker: still running, or it crashed (then the log just stops).
            ["running"] = run.ClosedAt == null,
            ["lastLogAt"] = run.Entries.Count > 0 ? run.Entries[^1].Time.ToString("O") : null,
        };

    private static IEnumerable<string> Names(HudInstall.SourcePlugin p) =>
        p.ProjectName == null ? [p.Folder] : [p.Folder, p.ProjectName];

    private static bool Matches(HudInstall.SourcePlugin p, string query) =>
        Names(p).Any(n => n.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || n.Replace(" ", "").Contains(query.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));

    private static JObject PluginStatus(HudInstall hud, HudInstall.Run? run, HudInstall.SourcePlugin p)
    {
        string status = "not-seen";
        DateTimeOffset? at = null, lastGood = null;
        string? error = null;
        int errors = 0, warnings = 0;
        var names = Names(p).ToList();
        var dirMarker = "\\" + p.Folder + "\\";

        foreach (var e in run?.Entries ?? [])
        {
            var m = e.Message;
            if (m.StartsWith($"Plugins from directory {p.Folder} compiled and loaded", StringComparison.OrdinalIgnoreCase))
            {
                (status, at, lastGood, error) = ("loaded", e.Time, e.Time, null);
            }
            else if (m.StartsWith("Skipping compilation of", StringComparison.OrdinalIgnoreCase) && m.Contains(dirMarker, StringComparison.OrdinalIgnoreCase))
            {
                (status, at, lastGood, error) = ("cached", e.Time, e.Time, null);
            }
            else if (m.StartsWith($"Compilation of {p.Folder} failed", StringComparison.OrdinalIgnoreCase)
                     || names.Any(n => m.StartsWith($"{n} -> CompilePlugin failed", StringComparison.OrdinalIgnoreCase)))
            {
                (status, at) = ("failed", e.Time);
                // The compiler output is in this entry's continuation lines or the "<proj> -> System.Exception" entry.
                if (m.Contains('\n')) error = m;
            }
            else if (status == "failed" && error == null && names.Any(n => m.StartsWith($"{n} -> ", StringComparison.OrdinalIgnoreCase)))
            {
                error = m;
            }
            else if (e.Level is "ERR" or "FTL" or "WRN" && IsFrom(m, names))
            {
                if (e.Level == "WRN") warnings++; else errors++;
            }
        }

        var o = new JObject { ["folder"] = p.Folder, ["project"] = p.ProjectName, ["status"] = status };
        if (at != null) o["at"] = at.Value.ToString("O");
        if (p.ProjectName == null) o["note"] = "No .csproj in the folder: the HUD won't compile it.";
        if (error != null) o["error"] = Clip(hud.ForAgent(error));
        if (errors > 0) o["runtimeErrors"] = errors;
        if (warnings > 0) o["warnings"] = warnings;

        var errorsTxt = Path.Combine(p.Path, "Errors.txt");
        if (File.Exists(errorsTxt))
        {
            var modified = new DateTimeOffset(File.GetLastWriteTime(errorsTxt));
            var stale = (lastGood != null && modified < lastGood) || (run != null && modified < run.StartedAt && status != "failed");
            var info = new JObject { ["modified"] = modified.ToString("O"), ["stale"] = stale };
            if (stale) info["note"] = "Left over from an older failed build; the HUD never deletes Errors.txt. Safe to delete.";
            else info["text"] = Clip(hud.ForAgent(HudInstall.ReadShared(errorsTxt)));
            o["errorsTxt"] = info;
        }
        return o;
    }

    /// <summary>Plugin log lines start with "[Folder]", "Project ->" or "Project, Method ->".</summary>
    private static bool IsFrom(string message, List<string> names) =>
        names.Any(n => message.StartsWith($"[{n}]", StringComparison.OrdinalIgnoreCase)
                       || message.StartsWith($"{n} ->", StringComparison.OrdinalIgnoreCase)
                       || message.StartsWith($"{n},", StringComparison.OrdinalIgnoreCase));

    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return i < 0 ? s : s[..i];
    }

    private static string Clip(string s) => s.Length <= MaxText ? s : s[..MaxText] + $" ...[+{s.Length - MaxText} chars]";
}

public static partial class HudDevTools
{
    // "[2026-10-08 5:02:15 AM, C:\...\File.cs(2, 47)] CS1525: Invalid expression term '}'"
    [System.Text.RegularExpressions.GeneratedRegex(@"\[[^,\]\n]+, (?<path>[A-Za-z]:\\[^\]\n]+?)\((?<line>\d+), (?<col>\d+)\)\] (?<code>(?:CS|MSB|NU)\d+): (?<msg>[^\n]*)")]
    private static partial System.Text.RegularExpressions.Regex CompilerDiagnostic();
}
