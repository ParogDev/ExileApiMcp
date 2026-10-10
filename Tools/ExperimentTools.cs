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

    [McpServerTool(Name = "experiment_presets", Title = "Guided experiment presets", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ExperimentPresetsResult))]
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
        return Dto.Result(Dto.From<ExperimentPresetsResult>(new JObject { ["presets"] = presets, ["records"] = records }), sb.ToString());
    }

    [McpServerTool(Name = "await_change", Title = "Wait for the user's action and diff it", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(AwaitChangeResult), IconSource = ExileApiMcp.Hosting.IconSet.GuideLight)]
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
        [Description("Highlight while waiting (removed after): JSON targets as for the highlight tool, e.g. [{\"item\":\"Chaos Orb\"}]")] string? highlight = null,
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
                async s => { state["status"] = s; await WriteInFlight(experiment, state); }, ct, highlight);
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
        if (o["changed"]?.Value<bool>() != true) return Typed<AwaitChangeResult>(o);
        return Dto.Result(Dto.From<AwaitChangeResult>(o), StepOutline(o));
    }

    /// <summary>
    /// One guided step: baseline, wait for a lasting change, settle, diff, record. Drives the in-game guide card and
    /// reports progress through <paramref name="onStatus"/> (waiting / detected / settling) for the non-blocking API.
    /// </summary>
    internal static async Task<JObject> RunStepAsync(BridgeRegistry bridges, string[] watch, string label, string experiment,
        int timeoutMs, int settleMs, string? instruction, int? step, int? steps, string? game, Func<string, Task>? onStatus, CancellationToken ct, string? highlight = null)
    {
        // A piloted step must not be cut by another agent's HUD restart (Sessions.cs in the bridge): hold a pilot lease for
        // the whole step, baseline to capture. The card's waiting status blocks too, but not between the baseline and the card.
        string? lease = null;
        try
        {
            var (_, l) = await bridges.CallAsync(game, "lease.acquire", new JObject
                { ["kind"] = "pilot", ["label"] = $"guided step '{label}' of {experiment}", ["ttlSec"] = Math.Clamp(timeoutMs / 1000 + 60, 60, 3600) }, ct);
            if (l["error"]?.ToString() == "restart_pending")
                throw new McpException($"The HUD is about to restart: {l["message"]} Wait for bridge_status to show it back, then run the step.");
            lease = l["id"]?.ToString();
        }
        catch (McpException ex) when (!ex.Message.Contains("about to restart", StringComparison.Ordinal)) { /* older bridge: no leases */ }
        try { return await RunStepCoreAsync(bridges, watch, label, experiment, timeoutMs, settleMs, instruction, step, steps, game, onStatus, ct, highlight); }
        finally
        {
            if (lease != null)
                try { await bridges.CallAsync(game, "lease.release", new JObject { ["id"] = lease }, CancellationToken.None); } catch (McpException) { }
        }
    }

    private static async Task<JObject> RunStepCoreAsync(BridgeRegistry bridges, string[] watch, string label, string experiment,
        int timeoutMs, int settleMs, string? instruction, int? step, int? steps, string? game, Func<string, Task>? onStatus, CancellationToken ct, string? highlight)
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
        await GuideTools.LogAsync(bridges, game, $"{ExileApiMcp.Hosting.SessionIdentity.Label}: waiting for '{label}'", "step", ct);
        if (highlight != null) await GuideTools.HighlightAsync(bridges, game, highlight, ct);
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
        if (highlight != null) await GuideTools.HighlightAsync(bridges, game, null, CancellationToken.None);
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

    [McpServerTool(Name = "experiment_step_start", Title = "Start a guided step (non-blocking)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ExperimentStepStartedResult))]
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
        [Description("Highlight while waiting (removed after): JSON targets as for the highlight tool, e.g. [{\"item\":\"Chaos Orb\"}]")] string? highlight = null,
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
                    async s => { state["status"] = s; await WriteInFlight(experiment, state); }, cts.Token, highlight);
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
        return Typed<ExperimentStepStartedResult>(new JObject
        {
            ["started"] = true, ["experiment"] = experiment, ["label"] = label, ["startedAt"] = state["startedAt"], ["timeoutMs"] = timeoutMs,
            ["next"] = "Poll experiment_status (every 1-3 s) until status is captured, failed, cancelled or error.",
        });
    }

    [McpServerTool(Name = "experiment_status", Title = "Progress of a guided experiment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ExperimentStatusResult))]
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
        return Typed<ExperimentStatusResult>(o);
    }

    [McpServerTool(Name = "experiment_step_cancel", Title = "Cancel a running guided step", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ExperimentStepCancelResult))]
    [Description("Stop the step started with experiment_step_start for this experiment (nothing is recorded).")]
    public static CallToolResult ExperimentStepCancel([Description("Experiment record name")] string experiment)
    {
        var running = InFlight.TryGetValue(experiment, out var cts);
        if (running) cts!.Cancel();
        return Typed<ExperimentStepCancelResult>(new JObject { ["experiment"] = experiment, ["cancelled"] = running,
            ["note"] = running ? "Cancelling; experiment_status shows 'cancelled' shortly." : "No step of this experiment is running in this server process." });
    }

    // ── Queued steps (the user starts them in game) ──────────────────
    // The bridge keeps the queue and records the step itself once the user presses Start on the in-game card, so
    // nothing waits on an agent being connected. experiment_queue_status collects finished steps into the same
    // experiment records as await_change (diffed here with the HUD's field names), so experiment_summary covers both.

    [McpServerTool(Name = "experiment_queue", Title = "Queue a step for the user to start in game", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, IconSource = ExileApiMcp.Hosting.IconSet.GuideLight)]
    [Description("Leave a guided step for the USER to start from the in-game guide card when they are ready (they may be " +
                 "away): nothing is recorded until they press Start, then the HUD records it itself (baseline, wait for a lasting " +
                 "change, settle) for each repeat - no agent needs to be connected. The queue survives HUD restarts, so " +
                 "developers see later what you asked and why (note). Collect results with experiment_queue_status. Prefer this " +
                 "over await_change when the user isn't actively waiting on you.")]
    public static async Task<CallToolResult> ExperimentQueue(BridgeRegistry bridges,
        [Description("Watch specs: value:<path> | memory:<path>[:size] | collection:<path>[:Label1,Label2]")] string[] watch,
        [Description("Step label, e.g. 'public-on'")] string label,
        [Description("Experiment record name the results go to")] string experiment,
        [Description("What the user should do, in game words, e.g. 'Tick Public on Dump leveling, then confirm'")] string instruction,
        [Description("How many times in a row (1-10, default 2: repeats are the evidence). Each repeat starts from the state the last one left")] int repeats = 2,
        [Description("Why you need it / what you expect, shown on the card for the developer")] string? note = null,
        [Description("Card title, e.g. 'Stash: which checkbox is Flags bit 3?'")] string? title = null,
        [Description("Max wait per repeat after Start, ms (5000-600000, default 120000)")] int timeoutMs = 120_000,
        [Description("Settle time, ms (100-5000, default 500)")] int settleMs = 500,
        [Description("Start this step by itself as soon as the previous step of the same experiment is captured, so one Start press runs a whole series (queue the first step without chain)")] bool chain = false,
        [Description("Highlight while the step records (shown after Start, removed after): JSON targets as for the highlight tool")] string? highlight = null,
        [Description("Guide the step as a flow (see guide_flow): a recipe id from Knowledge/flows.json, run from Start until the step ends")] string? recipe = null,
        [Description("Recipe params, e.g. tab=DUMP;affinity=Ritual;set=false (or JSON)")] string? recipeArgs = null,
        [Description("Or a full flow JSON (see guide_flow)")] string? flow = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var p = new JObject
        {
            ["experiment"] = experiment, ["label"] = label, ["instruction"] = instruction, ["watch"] = new JArray(watch),
            ["repeats"] = repeats, ["timeoutMs"] = timeoutMs, ["settleMs"] = settleMs, ["by"] = "Claude", ["chain"] = chain,
        };
        if (!string.IsNullOrWhiteSpace(highlight)) p["highlight"] = JArray.Parse(highlight);
        if (!string.IsNullOrWhiteSpace(flow)) p["flow"] = JObject.Parse(flow);
        else if (recipe != null)
            p["flow"] = await FlowTools.ExpandAsync(bridges, game, recipe, ParseRecipeArgs(recipeArgs), ct);
        if (note != null) p["note"] = note;
        if (title != null) p["title"] = title;
        foreach (var w in watch) Spec.Parse(w);
        var (_, r) = await bridges.CallAsync(game, "experiment.queue", p, ct);
        NeedQueue(r);
        if (r["error"] == null) r["next"] = "The user presses Start on the in-game guide card. To continue on your own when they finish, run experiment_queue_wait experiment=<name> in the background (it returns when nothing of it is queued or running and collects the results); experiment_queue_status checks any time.";
        return ToolResults.Json(r);
    }

    [McpServerTool(Name = "experiment_queue_status", Title = "Queued steps and their recordings", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("The in-game step queue: what waits for the user, what runs, what finished. Finished recordings not collected " +
                 "yet are diffed (bytes, bits, values, with HUD field names), appended to their experiment record and marked " +
                 "collected - then experiment_summary shows what changed in every repeat. all=true also lists collected steps.")]
    public static async Task<CallToolResult> ExperimentQueueStatus(BridgeRegistry bridges,
        [Description("Also list steps already collected")] bool all = false,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (bridge, list) = await bridges.CallAsync(game, "experiment.queued", new JObject { ["all"] = all }, ct);
        NeedQueue(list);
        var gameName = bridge.Game;
        var sb = new StringBuilder();
        var imported = new JArray();
        foreach (var s in (list["steps"] as JArray ?? []).OfType<JObject>())
        {
            var status = s["status"]?.ToString();
            sb.AppendLine($"[{status}] {s["id"]} {s["experiment"]}/{s["label"]} x{s["repeats"]} ({s["captured"]} recorded): {s["instruction"]}");
            if (status is "queued" or "running" || s["collected"]?.Value<bool>() == true) continue;
            if ((s["captured"]?.Value<int>() ?? 0) == 0)
            {
                // Failed / skipped with nothing recorded: reported once, then out of the default listing.
                await bridges.CallAsync(gameName, "experiment.collected", new JObject { ["id"] = s["id"] }, ct);
                continue;
            }
            var (_, full) = await bridges.CallAsync(gameName, "experiment.result", new JObject { ["id"] = s["id"] }, ct);
            var specs = (full["watch"] as JArray ?? []).Select(w => Spec.Parse(w.ToString())).ToList();
            foreach (var sp in specs) await sp.PrepareAsync(bridges, gameName, ct);
            var experiment = full["experiment"]!.ToString();
            var label = full["label"]!.ToString();
            JObject? record = null;
            foreach (var c in (full["captures"] as JArray ?? []).OfType<JObject>())
            {
                var before = new Dictionary<string, string>();
                var after = new Dictionary<string, string>();
                foreach (var sp in specs)
                {
                    if (c["Before"]?[sp.Raw] is { } b) AddToMap(sp, b, before);
                    if (c["After"]?[sp.Raw] is { } a) AddToMap(sp, a, after);
                }
                var changes = Diff(specs, before, after);
                record = await AppendStep(experiment, new JObject
                {
                    ["label"] = label, ["instruction"] = full["instruction"], ["at"] = c["At"], ["game"] = gameName,
                    ["changedAfterMs"] = c["ChangedAfterMs"], ["watch"] = full["watch"], ["changes"] = changes,
                    ["queuedStep"] = s["id"], ["repeat"] = c["Repeat"], ["startedFrom"] = full["startedFrom"],
                }, ct);
                sb.AppendLine($"    repeat {c["Repeat"]}: " + (changes.Count == 0 ? "no differences" : string.Join("; ", changes.OfType<JObject>().Take(6).Select(x =>
                    x["kind"]?.ToString() == "bytes" ? $"{x["key"]}: {x["from"]} -> {x["to"]}{(x["bitsFlipped"] is JArray bits ? $" (bits {string.Join(",", bits)})" : "")}" : $"{x["key"]}: {x["from"]} -> {x["to"]}"))));
            }
            await bridges.CallAsync(gameName, "experiment.collected", new JObject { ["id"] = s["id"] }, ct);
            var entry = new JObject { ["id"] = s["id"], ["experiment"] = experiment, ["label"] = label };
            if (record != null) entry["consistent"] = Consistent(record, label);
            imported.Add(entry);
        }
        if (sb.Length == 0) sb.AppendLine(all ? "The queue is empty." : "Nothing queued or waiting to be collected (all=true lists collected steps).");
        if (imported.Count > 0) sb.AppendLine($"Collected {imported.Count} step(s) into their experiment records (experiment_summary).");
        list["imported"] = imported;
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(list.ToString(Formatting.None)),
        };
    }

    [McpServerTool(Name = "experiment_queue_wait", Title = "Wait until the user has run the queued steps", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Block until no step of the experiment is queued or running any more (the user pressed Start and finished, " +
                 "or skipped), then collect the results like experiment_queue_status. Use it so you continue on your own when " +
                 "the user is done, instead of waiting for them to tell you: run it in the background where your client can " +
                 "(Claude Code: tools\\mcp-call.ps1 experiment_queue_wait experiment=<name> -TimeoutSec 3700 as a background " +
                 "task; you are woken when it returns). Returns waiting:true on timeout - call it again.")]
    public static async Task<CallToolResult> ExperimentQueueWait(BridgeRegistry bridges,
        [Description("Experiment whose queued steps to wait for (omit: any queued step)")] string? experiment = null,
        [Description("Max wait, seconds (5-3600, default 1800)")] int timeoutSec = 1800,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSec, 5, 3600));
        while (true)
        {
            JToken list;
            try { (_, list) = await bridges.CallAsync(game, "experiment.queued", new JObject(), ct); }
            catch (McpException) when (DateTime.UtcNow < until) { await Task.Delay(5000, ct); continue; }   // HUD restarting
            NeedQueue(list);
            var open = (list["steps"] as JArray ?? []).OfType<JObject>().Count(s =>
                s["status"]?.ToString() is "queued" or "running" && (experiment == null || s["experiment"]?.ToString() == experiment));
            if (open == 0) break;
            if (DateTime.UtcNow >= until)
                return ToolResults.Json(new JObject { ["waiting"] = true, ["open"] = open, ["experiment"] = experiment,
                    ["note"] = "Still queued or running: the user hasn't finished. Call experiment_queue_wait again." });
            await Task.Delay(2000, ct);
        }
        return await ExperimentQueueStatus(bridges, false, game, ct);
    }

    [McpServerTool(Name = "experiment_queue_cancel", Title = "Remove a queued step", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Cancel a queued (or running) in-game step by id, e.g. when the question is already answered.")]
    public static async Task<CallToolResult> ExperimentQueueCancel(BridgeRegistry bridges,
        [Description("Step id from experiment_queue / experiment_queue_status")] string id,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (_, r) = await bridges.CallAsync(game, "experiment.cancel", new JObject { ["id"] = id }, ct);
        NeedQueue(r);
        return ToolResults.Json(r);
    }

    /// <summary>The JSON text as before, with the result as typed structuredContent.</summary>
    private static CallToolResult Typed<T>(JObject o) => Dto.Result(Dto.From<T>(o), o.ToString(Formatting.None));

    private static Dictionary<string, string> ParseRecipeArgs(string? s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(s)) return d;
        if (s.TrimStart().StartsWith('{'))
        {
            foreach (var p in JObject.Parse(s).Properties()) d[p.Name] = p.Value.ToString();
            return d;
        }
        foreach (var kv in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = kv.IndexOf('=');
            if (i > 0) d[kv[..i].Trim()] = kv[(i + 1)..].Trim();
        }
        return d;
    }

    private static void NeedQueue(JToken? r)
    {
        if (r is not JObject o || (o["ok"] == null && o["error"] == null))
            throw new McpException("This HUD's bridge plugin has no experiment queue yet: update What's an AI Bridge and restart the HUD.");
    }

    [McpServerTool(Name = "experiment_summary", Title = "Summarise a guided experiment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ExperimentSummaryResult))]
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
        return Dto.Result(Dto.From<ExperimentSummaryResult>(new JObject { ["experiment"] = experiment, ["labels"] = labels, ["record"] = record }), sb.ToString());
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
            JToken r = s.Kind switch
            {
                "value" => (await bridges.QueryAsync(game, $"eval:{s.Path}", ct)).Item2,
                "memory" => (await bridges.CallAsync(game, "memory.read", new JObject { ["path"] = s.Path, ["size"] = s.Size, ["classify"] = false }, ct)).Item2,
                _ => (await bridges.CallAsync(game, "memory.collect", new JObject { ["path"] = s.Path, ["size"] = s.Size, ["labels"] = new JArray(s.Labels), ["limit"] = 500 }, ct)).Item2,
            };
            AddToMap(s, r, d);
        }
        return d;
    }

    /// <summary>One spec's bridge response (eval / memory.read / memory.collect) as flat "spec|key" -> value entries.</summary>
    private static void AddToMap(Spec s, JToken r, Dictionary<string, string> d)
    {
        {
            switch (s.Kind)
            {
                case "value":
                {
                    var leaves = new Dictionary<string, string>();
                    if (r["error"] != null) leaves["error"] = r["error"]!.ToString();
                    else WatchTools.Flatten(r["value"], "", leaves);
                    foreach (var (k, v) in leaves) d[$"{s.Raw}|{k}"] = v;
                    break;
                }
                case "memory":
                {
                    d[$"{s.Raw}|@"] = r["data"]?.ToString() ?? $"error:{r["error"]}";
                    d[$"{s.Raw}|address"] = r["address"]?.ToString() ?? "";
                    break;
                }
                case "collection":
                {
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
