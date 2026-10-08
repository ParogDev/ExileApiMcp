using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Guided experiments: the user performs one in-game action per step (the tools never send input); await_change
/// captures a baseline, waits until the watched state changes, lets it settle, and diffs it. Steps are appended to an
/// experiment record on disk (%LOCALAPPDATA%\ExileApiMcp\experiments\&lt;name&gt;.json) so repeats can be summarised:
/// what changes every time an action is done is the evidence. Presets: Knowledge/experiments.json.
/// Watch specs: "value:&lt;walker path&gt;" (leaf values via the walker), "memory:&lt;walker path&gt;[:size]" (raw bytes of the
/// object at its Address, labelled with the HUD's field names), "collection:&lt;walker path&gt;[:Label1,Label2]" (the HUD
/// struct of every item, matched by the first label).
/// </summary>
[McpServerToolType]
public static class ExperimentTools
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExileApiMcp", "experiments");

    private static readonly Lazy<JObject> Presets = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("knowledge/experiments.json");
        return s == null ? new JObject { ["experiments"] = new JArray() } : JObject.Parse(new StreamReader(s).ReadToEnd());
    });

    [McpServerTool(Name = "experiment_presets", Title = "Guided experiment presets", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Ready-made guided experiments (e.g. stash: Ctrl+scroll to the next tab, Ctrl+click an item to the inventory): " +
                 "each has steps with an instruction for the USER to perform in game and the state to watch. Run a step with " +
                 "await_change. Also lists experiment records already on disk.")]
    public static CallToolResult ExperimentPresets([Description(BridgeRegistry.GameParamDescription)] string? game = null)
    {
        var presets = new JArray((Presets.Value["experiments"] as JArray ?? []).OfType<JObject>()
            .Where(p => game == null || p["games"] is not JArray g || g.Any(x => x.ToString() == game)));
        Directory.CreateDirectory(Dir);
        var records = new JArray(new DirectoryInfo(Dir).GetFiles("*.json").Where(f => !f.Name.EndsWith(".inflight.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTime).Take(30)
            .Select(f =>
            {
                int count = 0;
                try { count = JObject.Parse(File.ReadAllText(f.FullName))["steps"]?.Count() ?? 0; } catch { }
                return new JObject { ["name"] = Path.GetFileNameWithoutExtension(f.Name), ["updated"] = f.LastWriteTime.ToString("O"), ["steps"] = count };
            }));
        var sb = new StringBuilder();
        foreach (var p in presets.OfType<JObject>())
        {
            sb.AppendLine($"{p["id"]}: {p["title"]}");
            foreach (var s in (p["steps"] as JArray ?? []).OfType<JObject>()) sb.AppendLine($"  - [{s["label"]}] {s["instruction"]}");
            sb.AppendLine($"  watch: {string.Join(" | ", (p["watch"] as JArray ?? []).Select(w => w.ToString()))}");
        }
        sb.Append($"{records.Count} experiment record(s) on disk. Run a step: await_change experiment=<name> label=<step label> watch=<preset watch>.");
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString() }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(new JObject { ["presets"] = presets, ["records"] = records }.ToString(Formatting.None)),
        };
    }

    [McpServerTool(Name = "await_change", Title = "Wait for the user's action and diff it", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("One step of a guided experiment. Captures the watched state, waits (up to timeoutMs) until the USER does " +
                 "the instructed thing in game and the state changes, waits until it settles, and returns what changed: leaf " +
                 "values, bytes and bits (with HUD field names), collection items. The step is appended to the experiment record " +
                 "so experiment_summary can show what changes every time. Never sends input: tell the user what to do first. " +
                 "Writes only to %LOCALAPPDATA%\\ExileApiMcp\\experiments.")]
    public static async Task<CallToolResult> AwaitChange(BridgeRegistry bridges,
        [Description("Watch specs: value:<path> | memory:<path>[:size] | collection:<path>[:Label1,Label2]")] string[] watch,
        [Description("Step label, e.g. 'next-tab' (repeats of the same label are compared)")] string label,
        [Description("Experiment record name, e.g. 'stash-tab-switch' (letters, digits, - _ .)")] string experiment,
        [Description("Max wait for the change, ms (1000-120000, default 60000)")] int timeoutMs = 60000,
        [Description("The state must stay unchanged this long before the 'after' capture, ms (100-5000, default 500)")] int settleMs = 500,
        [Description("What the user should do, shown in the in-game guide panel while waiting (recommended)")] string? instruction = null,
        [Description("Step number and total, for the guide panel")] int? step = null,
        [Description("Total steps, for the guide panel")] int? steps = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        // Same progress file as experiment_step_start, so apps and other clients can follow a blocking step too.
        var limit = Math.Clamp(timeoutMs, 1000, 120_000);
        var state = new JObject
        {
            ["experiment"] = experiment, ["label"] = label, ["instruction"] = instruction, ["step"] = step, ["steps"] = steps,
            ["startedAt"] = DateTimeOffset.Now.ToString("O"), ["timeoutMs"] = limit, ["status"] = "starting", ["watch"] = new JArray(watch),
            ["blocking"] = true,
        };
        if (Regex.IsMatch(experiment, @"^[\w.-]{1,64}$")) await WriteInFlight(experiment, state);
        JObject o;
        try
        {
            o = await RunStepAsync(bridges, watch, label, experiment, limit, settleMs, instruction, step, steps, game,
                async s => { state["status"] = s; await WriteInFlight(experiment, state); }, ct);
            state["status"] = o["changed"]?.Value<bool>() == true ? "captured" : "failed";
            state["result"] = o;
        }
        catch (OperationCanceledException) { state["status"] = "cancelled"; throw; }
        catch (Exception ex) { state["status"] = "error"; state["error"] = ex.Message; throw; }
        finally
        {
            state["finishedAt"] = DateTimeOffset.Now.ToString("O");
            try { await WriteInFlight(experiment, state); } catch { }
        }
        if (o["changed"]?.Value<bool>() != true) return ToolResults.Json(o);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = StepOutline(o) }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(o.ToString(Formatting.None)),
        };
    }

    /// <summary>
    /// One guided step: baseline, wait for a lasting change, settle, diff, record. Drives the in-game guide card and
    /// reports progress through <paramref name="onStatus"/> (waiting / detected / settling) for the non-blocking API.
    /// </summary>
    internal static async Task<JObject> RunStepAsync(BridgeRegistry bridges, string[] watch, string label, string experiment,
        int timeoutMs, int settleMs, string? instruction, int? step, int? steps, string? game, Func<string, Task>? onStatus, CancellationToken ct)
    {
        onStatus ??= _ => Task.CompletedTask;
        if (watch.Length == 0) throw new McpException("Pass at least one watch spec (see experiment_presets).");
        if (!Regex.IsMatch(experiment, @"^[\w.-]{1,64}$")) throw new McpException("experiment: letters, digits, '-', '_' or '.', up to 64 characters.");
        timeoutMs = Math.Clamp(timeoutMs, 1000, 600_000);
        settleMs = Math.Clamp(settleMs, 100, 5000);
        var specs = watch.Select(Spec.Parse).ToList();
        var (bridge, _) = await bridges.QueryAsync(game, "hello", ct);
        game = bridge.Game;
        foreach (var s in specs) await s.PrepareAsync(bridges, game, ct);

        var sw = Stopwatch.StartNew();
        var before = await CaptureAll(specs, bridges, game, ct);
        // The in-game guide shows the user what to do now (they may not be looking at the chat).
        var guideTitle = $"Experiment: {experiment}";
        await GuideTools.SetAsync(bridges, game, new JObject
        {
            ["title"] = guideTitle, ["instruction"] = instruction ?? $"Do the '{label}' action now", ["status"] = "waiting",
            ["step"] = step, ["steps"] = steps, ["detail"] = $"Watching {watch.Length} value(s) for up to {timeoutMs / 1000} s",
        }, ct);
        await GuideTools.LogAsync(bridges, game, $"Claude: waiting for '{label}'", "step", ct);
        await onStatus("waiting");
        Dictionary<string, string>? current = before, last = before;
        long changedAt = -1, stableSince = -1;
        int transients = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(120, ct);
            current = await CaptureAll(specs, bridges, game, ct);
            if (changedAt < 0)
            {
                if (!Same(current, before))
                {
                    changedAt = sw.ElapsedMilliseconds; stableSince = changedAt; last = current;
                    await GuideTools.SetAsync(bridges, game, new JObject { ["status"] = "detected", ["detail"] = "Change seen - hold still" }, ct);
                    await onStatus("detected");
                }
                continue;
            }
            if (!Same(current, last)) { stableSince = sw.ElapsedMilliseconds; last = current; continue; }
            if (sw.ElapsedMilliseconds - stableSince < settleMs) continue;
            // Settled. A change that went back to the baseline (hover/animation flicker) isn't the action: keep waiting.
            if (Same(last, before))
            {
                transients++; changedAt = -1;
                await GuideTools.SetAsync(bridges, game, new JObject { ["status"] = "waiting", ["detail"] = "That changed back - still waiting for the action" }, ct);
                await onStatus("waiting");
                continue;
            }
            break;
        }
        if (changedAt >= 0 && Same(last!, before)) changedAt = -1;
        if (changedAt < 0)
        {
            await GuideTools.SetAsync(bridges, game, new JObject { ["status"] = "failed", ["detail"] = "Nothing lasting changed - Claude will ask again" }, ct);
            await GuideTools.LogAsync(bridges, game, $"No lasting change for '{label}'", "warn", ct);
        }
        if (changedAt < 0)
            return new JObject { ["experiment"] = experiment, ["label"] = label, ["changed"] = false,
                ["transientChanges"] = transients,
                ["note"] = $"No lasting change within {timeoutMs} ms" + (transients > 0 ? $" ({transients} brief change(s) that reverted were ignored)" : "") +
                           ". Did the action happen in game (window focused, panel open), and do the watched values follow it? Run the step again." };

        var after = last!;
        var changes = Diff(specs, before, after);
        var stepRecord = new JObject
        {
            ["label"] = label, ["instruction"] = instruction, ["at"] = DateTimeOffset.Now.ToString("O"), ["game"] = game,
            ["changedAfterMs"] = changedAt, ["watch"] = new JArray(watch), ["changes"] = changes,
        };
        var record = await AppendStep(experiment, stepRecord, ct);
        var repeats = record["steps"]!.Count(s => s["label"]?.ToString() == label);
        var o = new JObject
        {
            ["experiment"] = experiment, ["label"] = label, ["changed"] = true, ["step"] = record["steps"]!.Count(),
            ["repeatsOfThisLabel"] = repeats, ["changedAfterMs"] = changedAt, ["changes"] = changes,
        };
        if (transients > 0) o["transientChangesIgnored"] = transients;
        var headline = changes.OfType<JObject>().FirstOrDefault(c => c["kind"]?.ToString() == "value") ?? changes.OfType<JObject>().FirstOrDefault();
        var summary = headline == null ? "captured" : $"{ShortKey(headline["key"]!.ToString())}: {headline["from"]} -> {headline["to"]}" +
                                                       (changes.Count > 1 ? $" (+{changes.Count - 1} more)" : "");
        await GuideTools.SetAsync(bridges, game, new JObject { ["status"] = "captured", ["detail"] = summary }, ct);
        await GuideTools.LogAsync(bridges, game, $"Captured '{label}': {summary}", "result", ct);
        if (repeats >= 2) o["consistent"] = Consistent(record, label);
        return o;
    }

    // ── Non-blocking steps ───────────────────────────────────────────
    // A step can wait minutes for the user; hosts time out tool calls far sooner. experiment_step_start runs the step in
    // the server process and writes its progress to <experiment>.inflight.json, so the app, an agent or another client
    // can follow it with experiment_status. Only the cancellation handle lives in memory (per process).

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> InFlight = new(StringComparer.OrdinalIgnoreCase);

    private static string InFlightFile(string experiment) => Path.Combine(Dir, experiment + ".inflight.json");

    private static async Task WriteInFlight(string experiment, JObject state)
    {
        Directory.CreateDirectory(Dir);
        state["updatedAt"] = DateTimeOffset.Now.ToString("O");
        var tmp = InFlightFile(experiment) + ".tmp";
        await File.WriteAllTextAsync(tmp, state.ToString(Formatting.None));
        File.Move(tmp, InFlightFile(experiment), overwrite: true);
    }

    [McpServerTool(Name = "experiment_step_start", Title = "Start a guided step (non-blocking)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Like await_change, but returns at once: the step runs in the server (up to 10 minutes) and experiment_status " +
                 "reports its progress (waiting -> detected -> captured | failed | cancelled) and result. Use it when the wait may " +
                 "outlast a tool call (MCP Apps, long pauses), or to keep working while the user acts. One step per experiment " +
                 "at a time; experiment_step_cancel stops it. The in-game guide card follows the step as with await_change.")]
    public static async Task<CallToolResult> ExperimentStepStart(BridgeRegistry bridges,
        [Description("Watch specs: value:<path> | memory:<path>[:size] | collection:<path>[:Label1,Label2]")] string[] watch,
        [Description("Step label, e.g. 'next-tab'")] string label,
        [Description("Experiment record name")] string experiment,
        [Description("What the user should do, shown in the in-game guide panel")] string? instruction = null,
        [Description("Max wait for the change, ms (1000-600000, default 120000)")] int timeoutMs = 120_000,
        [Description("Settle time, ms (100-5000, default 500)")] int settleMs = 500,
        [Description("Step number, for the guide panel")] int? step = null,
        [Description("Total steps, for the guide panel")] int? steps = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null)
    {
        if (!Regex.IsMatch(experiment, @"^[\w.-]{1,64}$")) throw new McpException("experiment: letters, digits, '-', '_' or '.', up to 64 characters.");
        timeoutMs = Math.Clamp(timeoutMs, 1000, 600_000);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs + 30_000));
        if (!InFlight.TryAdd(experiment, cts))
            throw new McpException($"A step of '{experiment}' is already running: experiment_status to follow it, experiment_step_cancel to stop it.");
        var started = DateTimeOffset.Now;
        var state = new JObject
        {
            ["experiment"] = experiment, ["label"] = label, ["instruction"] = instruction, ["step"] = step, ["steps"] = steps,
            ["startedAt"] = started.ToString("O"), ["timeoutMs"] = timeoutMs, ["status"] = "starting", ["watch"] = new JArray(watch),
        };
        await WriteInFlight(experiment, state);
        _ = Task.Run(async () =>
        {
            try
            {
                // RunStepAsync clamps to 120 s for blocking callers; the non-blocking path passes its own cap.
                var o = await RunStepAsync(bridges, watch, label, experiment, timeoutMs, settleMs, instruction, step, steps, game,
                    async s => { state["status"] = s; await WriteInFlight(experiment, state); }, cts.Token);
                state["status"] = o["changed"]?.Value<bool>() == true ? "captured" : "failed";
                state["result"] = o;
            }
            catch (OperationCanceledException) { state["status"] = "cancelled"; }
            catch (Exception ex) { state["status"] = "error"; state["error"] = ex.Message; }
            finally
            {
                state["finishedAt"] = DateTimeOffset.Now.ToString("O");
                try { await WriteInFlight(experiment, state); } catch { }
                if (state["status"]?.ToString() == "cancelled")
                    await GuideTools.SetAsync(bridges, game, new JObject { ["status"] = "info", ["detail"] = "Step cancelled" }, CancellationToken.None);
                InFlight.TryRemove(experiment, out _);
                cts.Dispose();
            }
        });
        return ToolResults.Json(new JObject
        {
            ["started"] = true, ["experiment"] = experiment, ["label"] = label, ["startedAt"] = state["startedAt"], ["timeoutMs"] = timeoutMs,
            ["next"] = "Poll experiment_status (every 1-3 s) until status is captured, failed, cancelled or error.",
        });
    }

    [McpServerTool(Name = "experiment_status", Title = "Progress of a guided experiment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("The current or last step started with experiment_step_start (status waiting | detected | captured | failed | " +
                 "cancelled | error, elapsed time, and the result once finished) plus how many steps the record holds. Cheap: " +
                 "reads two small files.")]
    public static CallToolResult ExperimentStatus([Description("Experiment record name")] string experiment)
    {
        Directory.CreateDirectory(Dir);
        var o = new JObject { ["experiment"] = experiment };
        var f = InFlightFile(experiment);
        if (File.Exists(f))
        {
            var s = JObject.Parse(File.ReadAllText(f));
            var finished = s["finishedAt"] != null;
            if (!finished && DateTimeOffset.TryParse(s["startedAt"]?.ToString(), out var st))
            {
                s["elapsedMs"] = (long)(DateTimeOffset.Now - st).TotalMilliseconds;
                // A step whose server process died never finishes: call it stale after its timeout plus a margin.
                if (!InFlight.ContainsKey(experiment) && s["elapsedMs"]!.Value<long>() > (s["timeoutMs"]?.Value<long>() ?? 120_000) + 60_000)
                    s["status"] = "stale";
            }
            o["step"] = s;
            o["running"] = !finished && s["status"]?.ToString() != "stale";
        }
        else o["running"] = false;
        var rec = Path.Combine(Dir, experiment + ".json");
        o["recordedSteps"] = File.Exists(rec) ? JObject.Parse(File.ReadAllText(rec))["steps"]?.Count() ?? 0 : 0;
        return ToolResults.Json(o);
    }

    [McpServerTool(Name = "experiment_step_cancel", Title = "Cancel a running guided step", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stop the step started with experiment_step_start for this experiment (nothing is recorded).")]
    public static CallToolResult ExperimentStepCancel([Description("Experiment record name")] string experiment)
    {
        var running = InFlight.TryGetValue(experiment, out var cts);
        if (running) cts!.Cancel();
        return ToolResults.Json(new JObject { ["experiment"] = experiment, ["cancelled"] = running,
            ["note"] = running ? "Cancelling; experiment_status shows 'cancelled' shortly." : "No step of this experiment is running in this server process." });
    }

    [McpServerTool(Name = "experiment_summary", Title = "Summarise a guided experiment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("For an experiment record: per step label, how many repeats, which changes happened in EVERY repeat (the " +
                 "evidence) and which only sometimes (noise or side effects). Pass no name to list records.")]
    public static CallToolResult ExperimentSummary([Description("Experiment record name")] string? experiment = null)
    {
        Directory.CreateDirectory(Dir);
        if (string.IsNullOrWhiteSpace(experiment)) return ExperimentPresets();
        var file = Path.Combine(Dir, experiment + ".json");
        if (!File.Exists(file)) throw new McpException($"No experiment record '{experiment}'.");
        var record = JObject.Parse(File.ReadAllText(file));
        var labels = new JArray(record["steps"]!.Select(s => s["label"]!.ToString()).Distinct().Select(l => new JObject
        {
            ["label"] = l, ["repeats"] = record["steps"]!.Count(s => s["label"]?.ToString() == l), ["consistent"] = Consistent(record, l),
        }));
        var sb = new StringBuilder($"Experiment {experiment}: {record["steps"]!.Count()} step(s)\n");
        foreach (var l in labels.OfType<JObject>())
        {
            sb.AppendLine($"[{l["label"]}] x{l["repeats"]}");
            foreach (var c in ((JArray)l["consistent"]!["always"]!).Take(30)) sb.AppendLine($"  always: {c}");
            foreach (var c in ((JArray)l["consistent"]!["sometimes"]!).Take(15)) sb.AppendLine($"  sometimes: {c}");
        }
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(new JObject { ["experiment"] = experiment, ["labels"] = labels, ["record"] = record }.ToString(Formatting.None)),
        };
    }

    // ── Watch specs ──────────────────────────────────────────────────

    private sealed class Spec
    {
        public string Kind = "", Path = "", Raw = "";
        public int Size;
        public string[] Labels = [];
        public List<(int off, int size, string name)> Fields = [];

        public static Spec Parse(string raw)
        {
            var s = new Spec { Raw = raw };
            var i = raw.IndexOf(':');
            if (i < 1) throw new McpException($"Watch spec '{raw}': expected value:|memory:|collection: prefix.");
            s.Kind = raw[..i].Trim().ToLowerInvariant();
            var rest = raw[(i + 1)..].Trim();
            // Optional trailing ":size" / ":labels" after the path (the path itself may contain ':' only inside <...>, which it doesn't).
            var j = rest.LastIndexOf(':');
            if (j > 0 && s.Kind is "memory" or "collection")
            {
                var tail = rest[(j + 1)..];
                if (s.Kind == "memory" && int.TryParse(tail, out var size)) { s.Size = size; rest = rest[..j]; }
                else if (s.Kind == "collection") { s.Labels = tail.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); rest = rest[..j]; }
            }
            if (s.Kind is not ("value" or "memory" or "collection")) throw new McpException($"Watch spec '{raw}': kind must be value, memory or collection.");
            s.Path = rest;
            return s;
        }

        public async Task PrepareAsync(BridgeRegistry bridges, string game, CancellationToken ct)
        {
            if (Kind is not ("memory" or "collection")) return;
            var path = Kind == "collection" ? Path + "[0]" : Path;
            var (_, l) = await bridges.CallAsync(game, "memory.layout", new JObject { ["path"] = path }, ct);
            if (l is JObject lo && lo["error"] == null)
            {
                Fields = (lo["fields"] as JArray ?? []).OfType<JObject>().Select(f => (f["off"]!.Value<int>(), f["size"]!.Value<int>(), f["name"]!.ToString())).ToList();
                if (Size <= 0) Size = lo["structSize"]?.Value<int>() ?? 0;
            }
            if (Size <= 0) Size = 256;
            Size = Math.Min(Size, 1024);
        }

        public string FieldAt(int off)
        {
            foreach (var (o, s, n) in Fields) if (off >= o && off < o + s) return n;
            return "(unmapped)";
        }
    }

    /// <summary>Flat map of "spec|key" -> value for every spec (values as leaves, memory as one hex string per 8 bytes).</summary>
    private static async Task<Dictionary<string, string>> CaptureAll(List<Spec> specs, BridgeRegistry bridges, string game, CancellationToken ct)
    {
        var d = new Dictionary<string, string>();
        foreach (var s in specs)
        {
            switch (s.Kind)
            {
                case "value":
                {
                    var (_, r) = await bridges.QueryAsync(game, $"eval:{s.Path}", ct);
                    var leaves = new Dictionary<string, string>();
                    if (r["error"] != null) leaves["error"] = r["error"]!.ToString();
                    else WatchTools.Flatten(r["value"], "", leaves);
                    foreach (var (k, v) in leaves) d[$"{s.Raw}|{k}"] = v;
                    break;
                }
                case "memory":
                {
                    var (_, r) = await bridges.CallAsync(game, "memory.read", new JObject { ["path"] = s.Path, ["size"] = s.Size, ["classify"] = false }, ct);
                    d[$"{s.Raw}|@"] = r["data"]?.ToString() ?? $"error:{r["error"]}";
                    d[$"{s.Raw}|address"] = r["address"]?.ToString() ?? "";
                    break;
                }
                case "collection":
                {
                    var (_, r) = await bridges.CallAsync(game, "memory.collect", new JObject { ["path"] = s.Path, ["size"] = s.Size, ["labels"] = new JArray(s.Labels), ["limit"] = 500 }, ct);
                    var seen = new Dictionary<string, int>();
                    foreach (var item in (r["items"] as JArray ?? []).OfType<JObject>())
                    {
                        var key = s.Labels.Length > 0 ? item["labels"]?[s.Labels[0]]?.ToString() ?? item["index"]!.ToString() : item["index"]!.ToString();
                        seen[key] = seen.TryGetValue(key, out var n) ? n + 1 : 0;
                        var k = $"{key}#{seen[key]}";
                        d[$"{s.Raw}|{k}|@"] = item["data"]?.ToString() ?? "";
                        foreach (var p in (item["labels"] as JObject)?.Properties() ?? []) d[$"{s.Raw}|{k}|{p.Name}"] = p.Value.ToString();
                    }
                    d[$"{s.Raw}|count"] = ((r["items"] as JArray)?.Count ?? 0).ToString();
                    break;
                }
            }
        }
        return d;
    }

    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Human-readable change list: value leaves "a -> b", byte ranges with field names and flipped bits.</summary>
    private static JArray Diff(List<Spec> specs, Dictionary<string, string> a, Dictionary<string, string> b)
    {
        var list = new JArray();
        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            a.TryGetValue(key, out var va); b.TryGetValue(key, out var vb);
            if (va == vb) continue;
            var parts = key.Split('|');
            var spec = specs.First(s => s.Raw == parts[0]);
            // Bytes of a re-created object aren't comparable with the old one: report the move only.
            var moved = parts[^1] == "@" && a.TryGetValue($"{parts[0]}|address", out var aa) && b.TryGetValue($"{parts[0]}|address", out var ab) && aa != ab;
            if (moved) continue;
            if (parts[^1] == "@" && va != null && vb != null && !va.StartsWith("error") && !vb.StartsWith("error"))
            {
                var ba = Convert.FromBase64String(va); var bb = Convert.FromBase64String(vb);
                var where = parts.Length == 3 ? $" item {parts[1]}" : "";
                for (int i = 0; i < Math.Min(ba.Length, bb.Length); i++)
                {
                    if (ba[i] == bb[i]) continue;
                    int j = i; while (j + 1 < Math.Min(ba.Length, bb.Length) && ba[j + 1] != bb[j + 1]) j++;
                    var x = new byte[j - i + 1];
                    for (int k = i; k <= j; k++) x[k - i] = (byte)(ba[k] ^ bb[k]);
                    var bits = Enumerable.Range(0, x.Length * 8).Where(n => (x[n / 8] >> (n % 8) & 1) == 1).ToList();
                    list.Add(new JObject
                    {
                        ["watch"] = parts[0], ["kind"] = "bytes", ["item"] = parts.Length == 3 ? parts[1] : null,
                        ["key"] = $"{parts[0]}{where} +{i} {spec.FieldAt(i)}",
                        ["off"] = i, ["size"] = j - i + 1, ["field"] = spec.FieldAt(i),
                        ["from"] = BitConverter.ToString(ba, i, j - i + 1).Replace("-", " "), ["to"] = BitConverter.ToString(bb, i, j - i + 1).Replace("-", " "),
                        ["bitsFlipped"] = bits.Count <= 16 ? new JArray(bits) : null,
                    });
                    i = j;
                }
            }
            else if (parts[^1] == "address")
                list.Add(new JObject
                {
                    ["watch"] = parts[0], ["kind"] = "moved", ["key"] = $"{parts[0]} object moved (re-created)",
                    ["from"] = va ?? "(absent)", ["to"] = vb ?? "(absent)",
                });
            else
                list.Add(new JObject
                {
                    ["watch"] = parts[0], ["kind"] = parts.Length == 3 ? "label" : "value", ["item"] = parts.Length == 3 ? parts[1] : null,
                    ["key"] = string.Join(" ", parts.Skip(0)), ["from"] = va ?? "(absent)", ["to"] = vb ?? "(absent)",
                });
        }
        foreach (var c in list.OfType<JObject>()) foreach (var p in c.Properties().Where(p => p.Value.Type == JTokenType.Null).ToList()) p.Remove();
        return list;
    }

    private static async Task<JObject> AppendStep(string experiment, JObject step, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var file = Path.Combine(Dir, experiment + ".json");
        var record = File.Exists(file) ? JObject.Parse(await File.ReadAllTextAsync(file, ct)) : new JObject { ["experiment"] = experiment, ["steps"] = new JArray() };
        ((JArray)record["steps"]!).Add(step);
        await File.WriteAllTextAsync(file, record.ToString(Formatting.None), ct);
        return record;
    }

    /// <summary>Change keys present in every repeat of a label (evidence) vs only some (noise / side effects).</summary>
    private static JObject Consistent(JObject record, string label)
    {
        var steps = record["steps"]!.Where(s => s["label"]?.ToString() == label).ToList();
        var sets = steps.Select(s => new HashSet<string>((s["changes"] as JArray ?? []).Select(c => c["key"]!.ToString()))).ToList();
        var all = sets.SelectMany(x => x).Distinct().ToList();
        return new JObject
        {
            ["repeats"] = steps.Count,
            ["always"] = new JArray(all.Where(k => sets.All(s => s.Contains(k)))),
            ["sometimes"] = new JArray(all.Where(k => !sets.All(s => s.Contains(k))).Select(k => $"{k} ({sets.Count(s => s.Contains(k))}/{steps.Count})")),
        };
    }

    /// <summary>"value:GameController.IngameState.IngameUi.StashElement.IndexVisibleStash value" -> "StashElement.IndexVisibleStash".</summary>
    private static string ShortKey(string key)
    {
        var k = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;
        k = k.Split(' ')[0];
        var parts = k.Split('.');
        return parts.Length <= 2 ? k : string.Join(".", parts[^2..]);
    }

    private static string StepOutline(JObject o)
    {
        var sb = new StringBuilder($"{o["experiment"]} step {o["step"]} [{o["label"]}] (repeat {o["repeatsOfThisLabel"]}): changed after {o["changedAfterMs"]} ms\n");
        foreach (var c in ((JArray)o["changes"]!).OfType<JObject>().Take(40))
            sb.AppendLine(c["kind"]?.ToString() == "bytes"
                ? $"  {c["key"]}: {c["from"]} -> {c["to"]}{(c["bitsFlipped"] is JArray b ? $" (bits {string.Join(",", b)})" : "")}"
                : $"  {c["key"]}: {c["from"]} -> {c["to"]}");
        if (o["consistent"] is JObject k)
        {
            sb.AppendLine($"Across {k["repeats"]} repeats - always:");
            foreach (var x in ((JArray)k["always"]!).Take(30)) sb.AppendLine($"  {x}");
        }
        return sb.ToString().TrimEnd();
    }
}
