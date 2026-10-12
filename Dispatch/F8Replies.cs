using System.Collections.Concurrent;
using ExileApiMcp.Bridge;
using ExileApiMcp.Tools;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Dispatch;

/// <summary>
/// Publishing F8 progress (feedback.reply with the stage fields) and reading where a press stands, for the dispatcher,
/// feedback_reply and the stage inference (C4). The bridge's event log is the record: a press's stage is the newest stage
/// its replies reached. The only memory here is a cache of stages this process published or read (Known), which saves a
/// bridge read when it already says "nothing to move"; losing it (a swapped worker) costs one read, never a wrong stage.
/// </summary>
public static class F8Replies
{
    /// <summary>The game F8 presses come from (the dispatcher's).</summary>
    public static string Game => Environment.GetEnvironmentVariable("HEXILE_DISPATCH_GAME") is { Length: > 0 } g ? g : "poe2";

    private static readonly ConcurrentDictionary<long, string> Known = new();

    private static void Remember(long seq, string stage)
    {
        if (Known.Count > 500) Known.Clear();
        Known[seq] = stage;
    }

    public static async Task<JToken> PublishAsync(BridgeRegistry bridges, string? game, F8ReplyData data, string? source, CancellationToken ct)
    {
        var p = new JObject { ["kind"] = FeedbackKit.ReplyKind, ["data"] = F8Wire.ToJObject(data) };
        if (source != null) p["source"] = source;
        var (_, r) = await bridges.CallAsync(game ?? Game, "pubsub.publish", p, ct);
        if (data.Stage != null && FeedbackKit.Refusal(r, "pubsub.publish") == null) Remember(data.Ref, data.Stage);
        return r;
    }

    /// <summary>The stage press <paramref name="seq"/> stands at now, from its replies on the bridge (null: none yet).</summary>
    public static async Task<string?> CurrentStageAsync(BridgeRegistry bridges, string? game, long seq, CancellationToken ct)
    {
        // Every page after the press: the newest reply decides, and a busy log can hold more than one page of replies.
        var events = new List<JObject>();
        for (var since = seq; ;)
        {
            var (_, r) = await bridges.CallAsync(game ?? Game, "pubsub.events", new JObject
            {
                ["since"] = since, ["kinds"] = new JArray(FeedbackKit.ReplyKind), ["limit"] = 500,
            }, ct);
            if (FeedbackKit.Refusal(r, "pubsub.events") != null) return null;
            var page = (r["events"] as JArray ?? []).OfType<JObject>().ToList();
            events.AddRange(page);
            if (page.Count < 500 || events.Count >= 4000) break;
            if (page[^1]["seq"] is not { } next) break;
            since = next.Value<long>();
        }
        var stage = F8.CurrentStage(events, seq);
        if (stage != null) Remember(seq, stage);
        return stage;
    }

    /// <summary>
    /// C4: a call by an F8 handler (caller label route-f8-&lt;seq&gt;) moves its press forward: a game read to Investigating,
    /// mark_reviewed to Reviewing. Never backwards, never out of a final stage; an explicit feedback_reply wins because it is
    /// on the bridge first. Fire and forget: the call it rides on never waits for it or fails because of it.
    /// </summary>
    public static void Infer(BridgeRegistry bridges, string label, string tool)
    {
        if (F8.SeqOfLabel(label) is not { } seq || F8.InferredStage(tool) is not { } wanted) return;
        if (Known.TryGetValue(seq, out var known) && F8.Advance(known, wanted) == null) return;   // cached: nothing to move
        Remember(seq, wanted);   // claimed here, so a burst of reads publishes once
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var current = await CurrentStageAsync(bridges, Game, seq, cts.Token);
                if (F8.Advance(current, wanted) is not { } next) { if (current != null) Remember(seq, current); return; }
                await PublishAsync(bridges, Game, F8.Reply(seq, next, null), label, cts.Token);
            }
            catch (Exception ex)
            {
                Known.TryRemove(seq, out _);
                Console.Error.WriteLine($"[F8] inferring {wanted} for #{seq} ({tool}): {ex.Message.Split('\n')[0]}");
            }
        });
    }
}
