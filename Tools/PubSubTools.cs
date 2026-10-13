using System.ComponentModel;
using System.Text.Json;
using ExileApiMcp.Bridge;
using ExileApiMcp.Dispatch;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Plugin pub/sub through the AI Bridge (bridge Shared/PubSub.cs): events plugins publish (AIBridge.Publish), read,
/// waited on and answered by agents. First user: Whats A Route's F8 presses (kind feedback), its trace (kind route) and
/// the replies shown above its card (kind feedback.reply), dispatched to fresh Claude Code sessions by
/// Dispatch/FeedbackDispatcher.cs.
/// </summary>
[McpServerToolType]
public static class PubSubTools
{
    [McpServerTool(Name = "pubsub_events", Title = "Events plugins published", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(PubSubEventsResult))]
    [Description("Events HUD plugins published through the AI Bridge (AIBridge.Publish) after sequence number since, each with its " +
                 "claims (who took it, once per stage). Kinds so far: feedback (Whats A Route's F8 press: verdict, note, route state), " +
                 "route (its trace: area, quest state with text, objective, door, boss-fight, boss-dead, quest-item-taken, " +
                 "route-reloaded), feedback.reply (answers shown above the route card, with the press's Stage), feedback.capture " +
                 "(what the player did after a 'watch what I do next' press, Ref = the press), route.agent / route.agent.progress " +
                 "(agent routes), work (agents' work items), pubsub.claim / pubsub.release. Kept across " +
                 "HUD restarts (<BridgeDirectory>\\pubsub\\events.jsonl).")]
    public static async Task<CallToolResult> PubSubEvents(BridgeRegistry bridges,
        [Description("Only events after this sequence number (0 = all the bridge keeps, up to 2000)")] long since = 0,
        [Description("Only these kinds, e.g. [\"feedback\", \"route\"]")] string[]? kinds = null,
        [Description("Max events (1-500, default 100)")] int limit = 100,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var r = await Events(bridges, game, since, kinds, limit, ct);
        return Dto.Result(r, Summary(r));
    }

    [McpServerTool(Name = "pubsub_wait", Title = "Wake when a plugin publishes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(PubSubEventsResult))]
    [Description("Block until events of the given kinds are published after since, then return them (no events and " +
                 "extra.waiting = true on timeout). Polls the bridge once a second. Run it in the background (tools\\mcp-call.ps1 " +
                 "pubsub_wait kinds=feedback since=<seq> -TimeoutSec 3700). F8 presses already have a dispatcher (feedback_dispatcher): " +
                 "wait on them only to watch, never to handle them a second time.")]
    public static async Task<CallToolResult> PubSubWait(BridgeRegistry bridges,
        [Description("Kinds to wake for, e.g. [\"feedback\"]")] string[] kinds,
        [Description("Sequence number already handled (from the last pubsub_* result); 0 = only what comes next")] long since = 0,
        [Description("Max wait, seconds (5-3600, default 1800)")] int timeoutSec = 1800,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSec, 5, 3600));
        var pause = new HudPause();   // a "not now" (load screen, restart) pauses the wait instead of failing it
        if (since <= 0) since = (await Events(bridges, game, long.MaxValue, kinds, 1, ct)).Seq;
        while (true)
        {
            PubSubEventsResult r;
            try { r = await Events(bridges, game, since, kinds, 500, ct); }
            catch (McpException ex) when (ex is not PubSubBrokenException && (pause.Holds(ex) || DateTime.UtcNow < until)) { if (pause.Holds(ex)) until = pause.Extend(until, TimeSpan.FromSeconds(5)); await Task.Delay(5000, ct); continue; }   // HUD restarting; a "not now" pauses the wait
            if (r.Events.Count > 0) return Dto.Result(r, Summary(r));
            if (DateTime.UtcNow >= until)
            {
                r.Extra = new() { ["waiting"] = JsonSerializer.SerializeToElement(true) };
                return Dto.Result(r, $"Nothing of {string.Join(", ", kinds)} after #{since} yet. Call pubsub_wait again with since={since}.");
            }
            await Task.Delay(1000, ct);
        }
    }

    [McpServerTool(Name = "pubsub_publish", Title = "Publish an event to the HUD plugins", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(PubSubPublishResult))]
    [Description("Publish an event through the AI Bridge: it is logged with the plugins' events and delivered to every plugin " +
                 "subscribed to its kind (AIBridge.Subscribe). For Whats A Route's card use feedback_reply instead. pubsub.* kinds are " +
                 "the bridge's own.")]
    public static async Task<CallToolResult> PubSubPublish(BridgeRegistry bridges,
        [Description("Event kind, e.g. feedback.reply")] string kind,
        [Description("The event's data, a JSON object")] JsonElement data,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (_, r) = await bridges.CallAsync(game, "pubsub.publish", new JObject { ["kind"] = kind, ["data"] = JToken.Parse(data.GetRawText()) }, ct);
        return Published(r);
    }

    [McpServerTool(Name = "feedback_reply", Title = "Tell the player (route card)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(PubSubPublishResult))]
    [Description("Move F8 press #seq along and tell the player (kind feedback.reply: the bridge's work strip and the line above Whats " +
                 "A Route's card). stage = where the press is: Investigating (looking at the game; set for you on your first game " +
                 "read), Fixing (changing route data or code: say it, the server can't see your edits), LiveFix (a fix is in " +
                 "route.live.json: the player can try it now), Reviewing (set for you by mark_reviewed), Merging (PR and CI; pass " +
                 "pr), Deploying, Verifying, Done, Failed. Stages only move forward; yours always win over the inferred ones. text = " +
                 "one or two plain sentences that say what to do now; detail = the longer why (hover); ask = the one line you need " +
                 "from the player (the bridge shows 'needs you' only while you hold an ask, a guided step or a Manual route step). " +
                 "Done tells the dispatcher the player already has the answer (it won't repeat your final message, and skips its " +
                 "verification watch). status working | done | failed is the old form (no stage: working keeps the current stage).")]
    public static async Task<CallToolResult> FeedbackReply(BridgeRegistry bridges,
        [Description("The press's bridge sequence number (the brief's #)")] long seq,
        [Description("One or two short plain sentences for the player (<= 400 chars; default a line for the stage)")] string? text = null,
        [Description("Investigating | Fixing | LiveFix | Reviewing | Merging | Deploying | Verifying | Done | Failed (Queued, Claimed, Reading, Dropped are the dispatcher's)")] string? stage = null,
        [Description("Legacy: working | done | failed. Without stage: done -> Done, failed -> Failed, working keeps the current stage")] string? status = null,
        [Description("The longer why, <= 400 chars (shown on hover)")] string? detail = null,
        [Description("What you need from the player, <= 120 chars (shown while you hold an ask / step / Manual route step)")] string? ask = null,
        [Description("The pull request, once there is one: {repo, url, number, checks: pending | green | red}")] F8PrRef? pr = null,
        [Description("What it waits on besides the player: None | Session | Ci | Review | OtherAgent | Restart (Player is set by the bridge)")] string? waitingOn = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        if (status != null && status is not ("working" or "done" or "failed")) throw new McpException($"status must be working, done or failed, not '{status}'.");
        string? s = null;
        if (stage != null)
        {
            s = F8.Stage(stage) ?? throw new McpException($"stage must be one of {string.Join(" | ", F8.Stages)}, not '{stage}'.");
            if (s == "Pressed") throw new McpException("Pressed is the bridge's own stage (it sets it when the press arrives).");
        }
        if (waitingOn != null && F8.WaitingOn(waitingOn) == null)
            throw new McpException($"waitingOn must be one of {string.Join(" | ", F8.WaitingOns.Where(x => x != "Player"))}, not '{waitingOn}'.");
        if (F8.WaitingOn(waitingOn) == "Player")
            throw new McpException("waitingOn Player is the bridge's to set: it derives it from your open ask (ask_user), guided step or Manual route step.");
        if (s == null && status is "done" or "failed") s = F8.StageFromStatus(status);
        // The old form "working": keep the stage the press is at (C5 would read it as Investigating, a step back from Fixing).
        s ??= await F8Replies.CurrentStageAsync(bridges, game, seq, ct) is { } cur && F8.Rank(cur) >= F8.Rank("Investigating") && !F8.IsFinal(cur) ? cur : "Investigating";
        if (string.IsNullOrWhiteSpace(text) && stage == null) throw new McpException("text is required without a stage: what the player should read.");
        var r = await F8Replies.PublishAsync(bridges, game, F8.Reply(seq, s, text, detail, ask, pr, waitingOn, status), null, ct);
        return Published(r);
    }

    [McpServerTool(Name = "feedback_dispatcher", Title = "F8 dispatcher: state and handlers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(FeedbackDispatcherState))]
    [Description("The F8 feedback dispatcher (on the shared MCP HTTP server): listening / idle / broken with the broken link, the " +
                 "Claude Code CLI it found and whether it is logged in, the presses waiting, and the last handlers (a fresh session per " +
                 "press, in worktree .claude\\worktrees\\route-f8-<seq>: a background session with Remote Control the user sees in Claude, " +
                 "named 'F8 #<seq>: <note>', with its session id and claude.ai/code URL, or headless as the fallback; brief, output and " +
                 "stderr in its dir). Read from " +
                 "handlers\\dispatcher.json, which the dispatcher rewrites every second: updatedAt older than a few seconds means no " +
                 "dispatcher runs (the shared server is down: any tools\\mcp-call.ps1 call starts it).")]
    public static CallToolResult FeedbackDispatcherStatus()
    {
        if (!File.Exists(FeedbackDispatcher.StatusFile))
            throw new McpException($"No dispatcher has run on this machine yet ({FeedbackDispatcher.StatusFile} is missing): the shared MCP HTTP server hosts it; any tools\\mcp-call.ps1 call starts that server.");
        var s = JsonSerializer.Deserialize<FeedbackDispatcherState>(File.ReadAllText(FeedbackDispatcher.StatusFile), Dto.Options)
                ?? throw new McpException($"{FeedbackDispatcher.StatusFile} is empty.");
        var age = DateTimeOffset.UtcNow - s.UpdatedAt;
        var text = (age.TotalSeconds > 15 ? $"NOT RUNNING: last update {age.TotalMinutes:0.#} min ago (the shared server is down; tools\\mcp-call.ps1 starts it).\n" : "")
                   + $"Dispatcher {s.Status}{(s.Why != null ? $": {s.Why}" : "")} (pid {s.Pid}, {s.Version}); cursor #{s.Cursor}\n"
                   + $"CLI: {s.ClaudeExe ?? "not found yet"}{(s.LoggedIn == false ? " (NOT logged in)" : "")}\n"
                   + (s.LoggedIn == false && s.LoginCommand != null && s.Why?.Contains(s.LoginCommand) != true ? $"Sign in once, in PowerShell: {s.LoginCommand}\n" : "")
                   + $"Waiting: {(s.Waiting.Count == 0 ? "none" : string.Join(", ", s.Waiting.Select(w => "#" + w)))}\n"
                   + string.Join("\n", s.Handlers.Select(h => $"#{h.Seq} {h.Status}{(h.Error != null ? $": {h.Error}" : h.Result != null ? $": {FeedbackKit.Clip(h.Result, 160)}" : "")} ({h.Dir})"
                       + (h.Mode == "remote-control"
                           ? $"\n    visible session \"{h.Name}\" (background {h.BackgroundId}, session {h.SessionId ?? "?"}){(h.Url != null ? $" {h.Url}" : " (no Remote Control URL yet)")}"
                             + (h.Stopped ? ", stopped" : h.StopAt != null ? ", stop pending (retried every minute)" : "")
                           : h.Mode == "headless" ? $"\n    headless{(h.FallbackReason != null ? $" ({h.FallbackReason})" : "")}{(h.SessionId != null ? $", session {h.SessionId}" : "")}" : "")));
        return Dto.Result(s, text);
    }

    private static async Task<PubSubEventsResult> Events(BridgeRegistry bridges, string? game, long since, string[]? kinds, int limit, CancellationToken ct)
    {
        var p = new JObject { ["since"] = since, ["limit"] = Math.Clamp(limit, 1, 500) };
        if (kinds is { Length: > 0 }) p["kinds"] = new JArray(kinds);
        var (_, r) = await bridges.CallAsync(game, "pubsub.events", p, ct);
        if (FeedbackKit.Refusal(r, "pubsub.events") is { } refusal) throw new PubSubBrokenException(refusal + ".");

        return Dto.From<PubSubEventsResult>(r);
    }

    /// <summary>The bridge answered, and refused: a broken link, never retried as "the HUD is restarting".</summary>
    private sealed class PubSubBrokenException(string message) : McpException(message);

    private static CallToolResult Published(JToken r)
    {
        if (FeedbackKit.Refusal(r, "pubsub.publish") is { } refusal) throw new McpException(refusal + ".");

        var typed = Dto.From<PubSubPublishResult>(r);
        return Dto.Result(typed, $"Published #{typed.Seq} ({typed.Kind}); {typed.Subscribers.Count} agent listener(s) on that kind.");
    }

    private static string Summary(PubSubEventsResult r)
    {
        if (r.Events.Count == 0) return $"No events (next since={r.Next ?? r.Seq}).";
        var lines = r.Events.Select(e =>
        {
            var d = e.Data is { ValueKind: JsonValueKind.Object } o ? o : default;
            string? S(string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;
            var what = e.Kind switch
            {
                "feedback" or "route" => $"{S("Event")} {S("AreaName")} {S("Label")} {S("Note")}".Trim(),
                "feedback.reply" => $"#{S("Ref") ?? S("ref")} {S("Stage") ?? S("status")}: {S("Text") ?? S("text")}",
                _ => FeedbackKit.Clip(e.Data?.GetRawText(), 160),
            };
            var claims = e.Claims is { Count: > 0 } c ? $" [claimed: {string.Join(", ", c.Select(kv => $"{kv.Key} by {kv.Value.By}"))}]" : "";
            return $"#{e.Seq} {e.At:HH:mm:ss} {e.Kind}: {what}{claims}";
        });
        return string.Join("\n", lines) + $"\n(next since={r.Next ?? r.Seq})";
    }
}
