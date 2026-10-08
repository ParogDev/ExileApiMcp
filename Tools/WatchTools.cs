using System.ComponentModel;
using System.Diagnostics;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// "Which field changes when X happens?" - sample an object through the bridge's reflection walker
/// (eval:) over time and report only the leaves that changed. Read-only; built on eval_path.
/// </summary>
[McpServerToolType]
public static class WatchTools
{
    private const int MaxChanges = 200;

    [McpServerTool(Name = "watch_object", Title = "Watch an object for changes", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Sample an object path (same syntax as eval_path, e.g. 'GameController.IngameState.IngameUi.InventoryPanel' or " +
                 "'GameController.Player.GetComponent<Life>()') repeatedly for durationMs and report only the values that " +
                 "changed: per leaf path, how often it changed, first/last values and when. Use it to discover which field " +
                 "reflects an in-game event (ask the user to do the thing while it runs, or watch passive changes like " +
                 "regen). Leaves that change on nearly every sample are flagged 'noisy' (timers, counters).")]
    public static async Task<CallToolResult> WatchObject(BridgeRegistry bridges,
        [Description("Object path starting with GameController (see eval_path / describe_type)")] string expression,
        [Description("How long to watch, ms (500-60000, default 5000)")] int durationMs = 5000,
        [Description("Sampling interval, ms (50-5000, default 250)")] int intervalMs = 250,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        durationMs = Math.Clamp(durationMs, 500, 60_000);
        intervalMs = Math.Clamp(intervalMs, 50, 5_000);
        var sw = Stopwatch.StartNew();
        Dictionary<string, string>? previous = null;
        var stats = new Dictionary<string, (int changes, string first, string last, long firstAtMs, long lastAtMs)>();
        int samples = 0, errors = 0;
        string? lastError = null, type = null;

        while (sw.ElapsedMilliseconds <= durationMs)
        {
            var started = sw.ElapsedMilliseconds;
            JToken result;
            try { (_, result) = await bridges.QueryAsync(game, $"eval:{expression}", ct); }
            catch (McpException ex) { errors++; lastError = ex.Message; await Task.Delay(intervalMs, ct); continue; }
            if (result["truncated"]?.Value<bool>() == true)
                throw new McpException($"'{expression}' serializes to more than the bridge's 64 KB limit, so it can't be diffed. Watch a narrower path (a component or sub-object).");
            if (result["error"] is { } err) { errors++; lastError = err.ToString(); if (samples == 0 && errors >= 3) break; }
            else
            {
                samples++;
                type ??= result["type"]?.ToString();
                var leaves = new Dictionary<string, string>();
                Flatten(result["value"], "", leaves);
                if (previous != null)
                {
                    foreach (var (path, value) in leaves)
                    {
                        if (previous.TryGetValue(path, out var before) && before == value) continue;
                        previous.TryGetValue(path, out before);
                        stats[path] = stats.TryGetValue(path, out var s)
                            ? (s.changes + 1, s.first, value, s.firstAtMs, started)
                            : (1, before ?? "(absent)", value, started, started);
                    }
                    foreach (var gone in previous.Keys.Where(k => !leaves.ContainsKey(k)))
                        stats[gone] = stats.TryGetValue(gone, out var s)
                            ? (s.changes + 1, s.first, "(absent)", s.firstAtMs, started)
                            : (1, previous[gone], "(absent)", started, started);
                }
                previous = leaves;
            }
            var wait = intervalMs - (int)(sw.ElapsedMilliseconds - started);
            if (wait > 0) await Task.Delay(wait, ct);
        }

        if (samples == 0)
            throw new McpException($"Could not evaluate '{expression}': {lastError ?? "no samples"}. Check the path with eval_path first.");

        var changes = new JArray(stats
            .OrderBy(kv => kv.Value.changes >= samples * 0.8 ? 1 : 0) // meaningful first, noisy last
            .ThenBy(kv => kv.Value.firstAtMs)
            .Take(MaxChanges)
            .Select(kv =>
            {
                var row = new JObject
                {
                    ["path"] = kv.Key,
                    ["changes"] = kv.Value.changes,
                    ["first"] = kv.Value.first,
                    ["last"] = kv.Value.last,
                    ["firstChangeAtMs"] = kv.Value.firstAtMs,
                    ["lastChangeAtMs"] = kv.Value.lastAtMs,
                };
                if (kv.Value.changes >= samples * 0.8) row["noisy"] = true;
                return row;
            }));
        var o = new JObject
        {
            ["expression"] = expression, ["type"] = type, ["samples"] = samples, ["durationMs"] = sw.ElapsedMilliseconds,
            ["leavesWatched"] = previous?.Count ?? 0, ["changedLeaves"] = stats.Count, ["changes"] = changes,
        };
        if (stats.Count > MaxChanges) o["truncated"] = $"{stats.Count - MaxChanges} more changed leaves; watch a narrower path.";
        if (errors > 0) o["errors"] = $"{errors} failed samples; last: {lastError}";
        if (stats.Count == 0) o["note"] = "Nothing changed. The walker serializes 2 levels deep: watch a deeper path for nested values.";
        return ToolResults.Json(o);
    }

    internal static void Flatten(JToken? token, string path, Dictionary<string, string> into)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var p in obj.Properties()) Flatten(p.Value, path.Length == 0 ? p.Name : $"{path}.{p.Name}", into);
                break;
            case JArray arr:
                for (int i = 0; i < arr.Count; i++) Flatten(arr[i], $"{path}[{i}]", into);
                into[$"{path}.Count"] = arr.Count.ToString();
                break;
            case null:
                break;
            default:
                into[path.Length == 0 ? "value" : path] = token.Type == JTokenType.Null ? "null" : token.ToString();
                break;
        }
    }
}
