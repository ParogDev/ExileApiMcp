using System.ComponentModel;
using ExileApiMcp.Bridge;
using ExileApiMcp.Dispatch;
using ExileApiMcp.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Agent routes on Whats A Route (PoE2; research/f8-agent-ux.md section 4): an agent leads the player through a few steps
/// it needs (to a lever it must see, a panel, back to town) with the route's own card, path and markers in the agent's hue,
/// and hands back cleanly. The command goes out as pub/sub kind route.agent (data.Op set | end | step; the bridge stamps
/// Owner, OwnerName and Hue from the publishing session); the plugin answers with route.agent.progress events, read and
/// waited on here (filtered by the route's Id). Stateless: the progress lives in the bridge's event log.
/// </summary>
[McpServerToolType]
public static class AgentRouteTools
{
    public const string Kind = "route.agent", ProgressKind = "route.agent.progress";

    [McpServerTool(Name = "route_agent", Title = "Agent route: lead the player somewhere", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(AgentRouteResult))]
    [Description("Lead the player through 1-8 steps you need (Whats A Route, PoE2) on the route card, in your colour; the player's " +
                 "own route is parked, not lost, and comes back after. 'Where' is a route step (this tool), 'what' is an instruction " +
                 "(guide / await_change), 'which' is an ask (ask_user). action=set sends {title (<=40), reason (<=120, the hover), " +
                 "steps:[{target, label (<=40), note? (<=120), done?:{kind: Near|Click|Area|Flag|Dead|Manual|Agent, value?, within?}, " +
                 "highlight?, withinCells?}], ttlSec? (600, max 1800), handBack? (Resume: the normal route returns | Hold: the last " +
                 "step stays as 'Stay here' until you end it or 60 s), ref? (the F8 press)}. Another agent's route on screen: yours " +
                 "queues (a queued event). action=step marks the current step done (done.kind Agent), action=end hands back (text = " +
                 "an optional toast), action=wait blocks until progress after since (started, arrived, step-done, paused, resumed, " +
                 "left, done, expired, replaced, ended, owner-gone), action=state lists it. 'left' = the player pressed Back to route: " +
                 "ask before you set it again.")]
    public static async Task<CallToolResult> RouteAgent(BridgeRegistry bridges,
        [Description("set | end | step | wait | state")] string action,
        [Description("The route's id: optional on set (default <your label>:<6 hex>, returned); required for step / wait / state; end without it ends your active route")] string? id = null,
        [Description("set: the card's title, <= 40 chars, e.g. 'Go to the lever room'")] string? title = null,
        [Description("set: why you need it, <= 120 chars (the card's hover)")] string? reason = null,
        [Description("set: the steps, 1-8 (see the tool description)")] AgentRouteStepArg[]? steps = null,
        [Description("set: the F8 press this answers, if any (the card's byline and the agents board show it)")] long? @ref = null,
        [Description("set: the route ends by itself after this long (30-1800, default 600)")] int? ttlSec = null,
        [Description("set: Resume (default) | Hold")] string? handBack = null,
        [Description("step: which step (0-based; default the current one)")] int? stepIndex = null,
        [Description("step / end: a short line for the player")] string? text = null,
        [Description("wait / state: progress after this event sequence (0 = all of this route's)")] long since = 0,
        [Description("wait: max seconds (5-1800, default 300)")] int timeoutSec = 300,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        game ??= "poe2";
        switch (action?.Trim().ToLowerInvariant())
        {
            case "set":
                AgentRouteCommand cmd;
                id = string.IsNullOrWhiteSpace(id) ? AgentRouteKit.NewId(SessionIdentity.Label) : id.Trim();
                try { cmd = AgentRouteKit.Set(id, title, reason, steps, ttlSec, handBack, @ref); }
                catch (ArgumentException ex) { throw new McpException(ex.Message); }
                return await Publish(bridges, game, cmd, $"Agent route {id} set: {cmd.Steps!.Count} step(s), '{cmd.Title}' ({cmd.TtlSec} s, hand back {cmd.HandBack}).", ct);
            case "step":
                if (string.IsNullOrWhiteSpace(id)) throw new McpException("step needs the route's id (set returned it).");
                return await Publish(bridges, game, new AgentRouteCommand { Op = "step", Id = id, StepIndex = stepIndex is >= 0 ? stepIndex : null, Text = Line(text) },
                    $"Step {(stepIndex is >= 0 ? (stepIndex + 1).ToString() : "current")} of {id} marked done.", ct);
            case "end":
                return await Publish(bridges, game, new AgentRouteCommand { Op = "end", Id = string.IsNullOrWhiteSpace(id) ? null : id, Text = Line(text) },
                    $"Agent route {id ?? "(your active one)"} ended: the player's route is back.", ct, id ?? "");
            case "wait" or "state":
                if (string.IsNullOrWhiteSpace(id)) throw new McpException($"{action} needs the route's id.");
                var until = DateTime.UtcNow.AddSeconds(action == "wait" ? Math.Clamp(timeoutSec, 5, 1800) : 0);
                while (true)
                {
                    var r = await Progress(bridges, game, id, since, ct);
                    if (action == "state" || r.Events.Count > 0 || DateTime.UtcNow >= until)
                    {
                        r.Waiting = action == "wait" && r.Events.Count == 0;
                        return Dto.Result(r, ProgressText(r));
                    }
                    await Task.Delay(1000, ct);
                }
            default:
                throw new McpException($"action must be set, end, step, wait or state, not '{action}'.");
        }
    }

    private static string? Line(string? text) => FeedbackKit.Clip(text, 200) is { Length: > 0 } t ? t : null;

    private static async Task<CallToolResult> Publish(BridgeRegistry bridges, string game, AgentRouteCommand cmd, string text, CancellationToken ct, string? id = null)
    {
        var (_, r) = await bridges.CallAsync(game, "pubsub.publish", new JObject { ["kind"] = Kind, ["data"] = F8Wire.ToJObject(cmd) }, ct);
        if (FeedbackKit.Refusal(r, "pubsub.publish") is { } refusal) throw new McpException(refusal + ".");
        var plugins = (r["plugins"] as JArray)?.Select(p => p.ToString()).ToList() ?? [];
        var listening = r["plugins"] == null ? (bool?)null : plugins.Any(p => p.StartsWith("WhatsARoute", StringComparison.Ordinal));
        var result = new AgentRouteResult { Id = id ?? cmd.Id ?? "", Seq = r["seq"]?.Value<long>() ?? 0, RouteListening = listening };
        if (listening == false)
            text = $"Published #{result.Seq}, but Whats A Route is not subscribed to {Kind} (not loaded, or a build before agent routes): nothing shows in game.";
        return Dto.Result(result, text + (result.Id.Length > 0 ? $" Progress: route_agent action=wait id={result.Id} since={result.Seq}." : ""));
    }

    private static async Task<AgentRouteResult> Progress(BridgeRegistry bridges, string game, string id, long since, CancellationToken ct)
    {
        var (_, r) = await bridges.CallAsync(game, "pubsub.events", new JObject { ["since"] = since, ["kinds"] = new JArray(ProgressKind), ["limit"] = 500 }, ct);
        if (FeedbackKit.Refusal(r, "pubsub.events") is { } refusal) throw new McpException(refusal + ".");
        var result = new AgentRouteResult { Id = id, Seq = r["seq"]?.Value<long>() ?? since };
        foreach (var e in (r["events"] as JArray ?? []).OfType<JObject>())
        {
            if (e["data"] is not JObject d || d["Id"]?.ToString() != id) continue;
            var p = Dto.From<AgentRouteProgress>(d);
            p.Seq = e["seq"]?.Value<long>() ?? 0;
            if (p.Event.Length == 0 && d["Status"] is { } legacy) p.Event = legacy.ToString();   // a Route build before 8.1's Event
            result.Events.Add(p);
        }
        if (result.Events.Count > 0) result.Seq = result.Events[^1].Seq;
        (result.Ended, result.Parked) = AgentRouteKit.Outcome(result.Events.Select(e => e.Event));
        return result;
    }

    private static string ProgressText(AgentRouteResult r)
    {
        if (r.Events.Count == 0) return $"No progress on {r.Id} after #{r.Seq} yet{(r.Waiting ? " (timed out: wait again with since=" + r.Seq + ")" : "")}.";
        var lines = r.Events.Select(e => $"#{e.Seq} {e.Event}"
            + (e.Event is "step-done" or "arrived" ? $" {e.StepIndex + 1}/{e.StepCount}{(e.By != null ? $" by {e.By}" : "")}: {e.Label}" : "")
            + (e.Manual == true ? " (waits on the player's Did it)" : "")
            + (e.Why != null ? $" - {e.Why}" : "")
            + (e.Area != null ? $" ({e.Area})" : ""));
        return string.Join("\n", lines)
               + (r.Ended != null ? $"\nThe route ended: {r.Ended}." : r.Parked ? "\nParked: the player went Back to route (one click resumes it for 10 min): ask before you set another." : "")
               + $"\n(next since={r.Seq})";
    }
}
