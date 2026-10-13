using System.ComponentModel;
using System.Diagnostics;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// "Which field changes when X happens?" - sample an object through the bridge over time and report only the leaves that
/// changed. Each sample is one bridge object.snapshot (every leaf down to `depth`, walked inside the HUD with a node budget);
/// an older bridge without it falls back to eval: (2 levels), named in the result. Read-only.
/// </summary>
[McpServerToolType]
public static class WatchTools
{
    private const int MaxChanges = 200;

    [McpServerTool(Name = "watch_object", Title = "Watch an object for changes", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(WatchObjectResult))]
    [Description("Sample an object path (same syntax as eval_path, e.g. 'GameController.IngameState.IngameUi.InventoryPanel' or " +
                 "'GameController.Player.GetComponent<Life>()') repeatedly for durationMs and report only the values that " +
                 "changed: per leaf path (a walker sub-path such as Health.Current or Buffs[2].Timer), how often it changed, " +
                 "first/last values and when. Follows nested objects down to depth levels (default 3, up to 8) within a node " +
                 "budget per sample. Use it to discover which field reflects an in-game event (ask the user to do the thing while it " +
                 "runs, or watch passive changes like regen). Leaves that change on nearly every sample are flagged 'noisy' " +
                 "(timers, counters). Result: WatchObjectResult (budget = the last sample's walk).")]
    public static async Task<CallToolResult> WatchObject(BridgeRegistry bridges,
        [Description("Object path starting with GameController (see eval_path / describe_type)")] string expression,
        [Description("How long to watch, ms (500-60000, default 5000)")] int durationMs = 5000,
        [Description("Sampling interval, ms (50-5000, default 250)")] int intervalMs = 250,
        [Description("Levels below the path to follow (1-8, default 3)")] int depth = 3,
        [Description("Objects opened per sample at most (10-20000, default 2000)")] int maxNodes = 2000,
        [Description("Entities: also follow their components (GetComponent<T>() leaves). Default false")] bool components = false,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        durationMs = Math.Clamp(durationMs, 500, 60_000);
        intervalMs = Math.Clamp(intervalMs, 50, 5_000);
        depth = Math.Clamp(depth, 1, 8);
        maxNodes = Math.Clamp(maxNodes, 10, 20_000);
        var snapParams = new JObject
        {
            ["path"] = expression, ["depth"] = depth, ["maxNodes"] = maxNodes, ["components"] = components,
            // Each sample is one main-thread request: keep it well inside the interval.
            ["timeMs"] = Math.Clamp(intervalMs / 2, 10, 250),
        };
        var sw = Stopwatch.StartNew();
        var diff = new WatchDiff();
        int errors = 0, truncatedSamples = 0;
        string? lastError = null, type = null, fallback = null;
        bool useEval = false;
        ObjectWalkBudget? budget = null;
        ObjectWalkTruncation? lastCut = null;

        while (sw.ElapsedMilliseconds <= durationMs)
        {
            var started = sw.ElapsedMilliseconds;
            try
            {
                if (!useEval)
                {
                    var (bridge, reply) = await bridges.CallAsync(game, "object.snapshot", snapParams, ct);
                    if (game == null && bridge.Game != "auto") game = bridge.Game;
                    if (ExploreTools.IsUnknownMethodEcho(reply, "leaves"))
                    {
                        useEval = true;
                        fallback = "This HUD's bridge has no object.snapshot (an older What's an AI Bridge): sampled with eval:, which serializes 2 levels deep"
                                   + (depth > 2 ? $" (depth {depth} asked)" : "") + ". Update the bridge to follow deeper paths.";
                        continue;
                    }
                    if (reply["error"] != null)
                    {
                        // resolve_failed names the broken segment: a path that never resolves fails fast (below).
                        errors++; lastError = $"{reply["error"]}: {reply["message"]}";
                        if (diff.Samples == 0 && errors >= 3) break;
                    }
                    else
                    {
                        var snap = Dto.From<ObjectSnapshotResponse>(reply);
                        type ??= snap.Type;
                        budget = snap.Budget;
                        if (snap.Truncated != null) { truncatedSamples++; lastCut = snap.Truncated; }
                        diff.Add(snap.Leaves, started, snap.Truncated != null);
                    }
                }
                else
                {
                    var (_, result) = await bridges.QueryAsync(game, $"eval:{expression}", ct);
                    if (result["truncated"]?.Value<bool>() == true)
                        throw new McpException($"'{expression}' serializes to more than the bridge's 64 KB limit, so it can't be diffed. Watch a narrower path (a component or sub-object).");
                    if (result["error"] is { } err) { errors++; lastError = err.ToString(); if (diff.Samples == 0 && errors >= 3) break; }
                    else
                    {
                        type ??= result["type"]?.ToString();
                        var leaves = new Dictionary<string, string>();
                        Flatten(result["value"], "", leaves);
                        diff.Add(leaves, started, false);
                    }
                }
            }
            catch (McpException ex) when (!ex.Message.Contains("64 KB", StringComparison.Ordinal))
            {
                errors++; lastError = ex.Message;
            }
            var wait = intervalMs - (int)(sw.ElapsedMilliseconds - started);
            if (wait > 0) await Task.Delay(wait, ct);
        }

        if (diff.Samples == 0)
            throw new McpException($"Could not sample '{expression}': {lastError ?? "no samples"}. Check the path with eval_path first.");

        var o = diff.Result(expression, type, sw.ElapsedMilliseconds, MaxChanges);
        o.Depth = useEval ? Math.Min(depth, 2) : depth;
        o.Via = useEval ? "eval" : "snapshot";
        o.Fallback = fallback;
        o.Budget = budget;
        if (truncatedSamples > 0) { o.TruncatedSamples = truncatedSamples; o.LastTruncation = lastCut; }
        if (errors > 0) o.Errors = $"{errors} failed samples; last: {lastError}";
        if (o.ChangedLeaves == 0)
            o.Note = useEval
                ? "Nothing changed. eval: serializes 2 levels deep: watch a deeper path for nested values."
                : $"Nothing changed in {o.LeavesWatched} leaves down to depth {depth}"
                  + (budget is { AtDepthLimit: > 0 } b ? $" ({b.AtDepthLimit} objects sat at the depth limit: raise depth or watch a deeper path)" : "") + ".";
        return Dto.Result(o, System.Text.Json.JsonSerializer.Serialize(o, Dto.Options));
    }

    /// <summary>
    /// Leaf-by-leaf change counting over consecutive samples. A leaf missing from a sample counts as removed ("(absent)") only
    /// when neither sample was cut short by a budget: a truncated walk drops leaves that still exist.
    /// </summary>
    public sealed class WatchDiff
    {
        private Dictionary<string, string>? _previous;
        private bool _previousTruncated;
        private readonly Dictionary<string, (int changes, string first, string last, long firstAtMs, long lastAtMs)> _stats = new();
        public int Samples { get; private set; }

        public void Add(Dictionary<string, string> leaves, long atMs, bool truncated)
        {
            Samples++;
            if (_previous != null)
            {
                foreach (var (path, value) in leaves)
                {
                    if (_previous.TryGetValue(path, out var before))
                    {
                        if (before == value) continue;
                    }
                    else if (truncated || _previousTruncated) continue;   // not walked last time: not a change
                    Record(path, before ?? "(absent)", value, atMs);
                }
                if (!truncated && !_previousTruncated)
                    foreach (var gone in _previous.Keys.Where(k => !leaves.ContainsKey(k)).ToList())
                        Record(gone, _previous[gone], "(absent)", atMs);
            }
            // A truncated sample keeps the previous values of leaves it did not reach, so they compare against the next one.
            if (truncated && _previous != null)
                foreach (var (k, v) in _previous) leaves.TryAdd(k, v);
            _previous = leaves;
            _previousTruncated = truncated;
        }

        private void Record(string path, string before, string value, long atMs) =>
            _stats[path] = _stats.TryGetValue(path, out var s)
                ? (s.changes + 1, s.first, value, s.firstAtMs, atMs)
                : (1, before, value, atMs, atMs);

        public WatchObjectResult Result(string expression, string? type, long durationMs, int maxChanges)
        {
            var noisyAt = Samples * 0.8;
            var result = new WatchObjectResult
            {
                Expression = expression, Type = type, Samples = Samples, DurationMs = durationMs,
                LeavesWatched = _previous?.Count ?? 0, ChangedLeaves = _stats.Count,
                Changes = _stats
                    .OrderBy(kv => kv.Value.changes >= noisyAt ? 1 : 0) // meaningful first, noisy last
                    .ThenBy(kv => kv.Value.firstAtMs)
                    .Take(maxChanges)
                    .Select(kv => new WatchLeafChange
                    {
                        Path = kv.Key, Changes = kv.Value.changes, First = kv.Value.first, Last = kv.Value.last,
                        FirstChangeAtMs = kv.Value.firstAtMs, LastChangeAtMs = kv.Value.lastAtMs,
                        Noisy = kv.Value.changes >= noisyAt ? true : null,
                    }).ToList(),
            };
            if (_stats.Count > maxChanges) result.Truncated = $"{_stats.Count - maxChanges} more changed leaves; watch a narrower path.";
            return result;
        }
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
