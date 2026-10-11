using System.ComponentModel;
using System.Text.Json;
using ExileApiMcp.Bridge;
using ExileApiMcp.Dispatch;
using ExileApiMcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// feedback_sessions: every F8 press of Whats A Route with the Claude session the dispatcher started for it, newest
/// first (Dispatch/FeedbackSessions.cs reads the files; the bridge's press and reply events are merged in when a HUD
/// answers). The control center's "F8 sessions" page shows it and, standalone, follows exile://feedback/sessions
/// (Bridge/FeedbackHub.cs pushes when the dispatcher's files change).
/// </summary>
[McpServerToolType]
public static class FeedbackSessionsTools
{
    [McpServerTool(Name = "feedback_sessions", Title = "F8 presses and their Claude sessions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(FeedbackSessionsResult))]
    [Description("Every F8 press of Whats A Route (PoE2) with the Claude Code session the dispatcher started for it, newest first: " +
                 "the player's note, area and step, when it was pressed, status (queued | starting | running | done | failed | works), " +
                 "the answer shown on the card, duration, cost and usage from the CLI's result, the session id and transcript path, " +
                 "the worktree, and the session's name and URL when the dispatcher recorded them (a Remote Control session). Plus the " +
                 "dispatcher's state: listening / idle / broken with the fix, not-running when its file is stale, the CLI and its " +
                 "sign-in, what is queued. Read from %LOCALAPPDATA%\\Hexile\\WhatsARoute\\handlers\\ (works with no HUD); with a HUD " +
                 "up, the bridge's press and reply events are merged in (the note straight from the press, what was said on the " +
                 "card, presses still queued). seq= returns that one press with its brief.md and stderr.log. Subscribable as " +
                 "exile://feedback/sessions.")]
    public static async Task<CallToolResult> FeedbackSessionsList(BridgeRegistry bridges,
        [Description("One press by its bridge sequence number: its brief and stderr come too")] long? seq = null,
        [Description("Max presses (1-200, default 50)")] int limit = 50,
        [Description("Merge the HUD bridge's press and reply events (default true; false = files only, no bridge call)")] bool merge = true,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var r = await BuildAsync(bridges, seq, limit, merge, game, ct);
        if (seq != null && r.Sessions.Count == 0)
            return ToolResults.Json(new JObject { ["error"] = "press_unknown", ["message"] = $"No press #{seq}: no folder {Path.Combine(FeedbackDispatcher.HandlersDir, seq.ToString()!)} and the bridge has no such press." });
        return Dto.Result(r, FeedbackSessions.Summary(r));
    }

    /// <summary>The result the tool and the resource share.</summary>
    public static async Task<FeedbackSessionsResult> BuildAsync(BridgeRegistry bridges, long? seq, int limit, bool merge, string? game, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dispatcher = FeedbackSessions.Dispatcher(FeedbackDispatcher.StatusFile, now);
        limit = Math.Clamp(limit, 1, 200);
        var (sessions, folders) = FeedbackSessions.FromFolders(FeedbackDispatcher.HandlersDir, userProfile, now, withFiles: seq != null, only: seq, limit: limit);
        // A dead dispatcher's last waiting list says nothing about now.
        var waiting = dispatcher.Alive ? (seq == null ? dispatcher.Waiting : dispatcher.Waiting.Where(w => w == seq).ToList()) : [];
        string? bridgeNote = null;
        if (merge)
        {
            game ??= Environment.GetEnvironmentVariable("HEXILE_DISPATCH_GAME") is { Length: > 0 } g ? g : "poe2";
            // No token file = no HUD: say so without a bridge call (a failed call would diagnose and raise bridge-down every read).
            if (bridges.Bridges.FirstOrDefault(b => b.Game == game) is not { LooksAvailable: true })
                bridgeNote = $"not merged: no {game} HUD bridge is running";
            else
            {
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    budget.CancelAfter(TimeSpan.FromSeconds(6));
                    var events = await PressEventsAsync(bridges, game, budget.Token);
                    if (seq != null) events = events.Where(e => e["seq"]?.Value<long>() == seq || e["data"]?["ref"]?.Value<long>() == seq).ToList();
                    var (presses, replies) = FeedbackSessions.Merge(sessions, events, waiting, now);
                    bridgeNote = $"{game}: {presses} press(es), {replies} replies merged";
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { bridgeNote = $"not merged: the {game} bridge did not answer within 6 s"; }
                catch (ModelContextProtocol.McpException ex) { bridgeNote = $"not merged: {ex.Message.Split('\n')[0]}"; }
            }
        }
        FeedbackSessions.ApplyWaiting(sessions, waiting);
        var total = Math.Max(folders, sessions.Count);
        return new FeedbackSessionsResult
        {
            Dispatcher = dispatcher, Sessions = sessions.Take(limit).ToList(), Total = total,
            HandlersDir = FeedbackDispatcher.HandlersDir, Bridge = bridgeNote ?? "not merged (merge=false)", At = now,
        };
    }

    /// <summary>Every feedback and feedback.reply event the bridge still has: pubsub.events pages oldest first (500 a
    /// page, `next` to continue), and its ring holds 2000, so at most 5 pages.</summary>
    private static async Task<List<JObject>> PressEventsAsync(BridgeRegistry bridges, string game, CancellationToken ct)
    {
        var all = new List<JObject>();
        long since = 0;
        for (var page = 0; page < 5; page++)
        {
            var (_, r) = await bridges.CallAsync(game, "pubsub.events", new JObject
            {
                ["since"] = since, ["limit"] = 500, ["kinds"] = new JArray(FeedbackKit.PressKind, FeedbackKit.ReplyKind),
            }, ct);
            if (FeedbackKit.Refusal(r, "pubsub.events") is { } refusal) throw new ModelContextProtocol.McpException(refusal);
            var events = (r["events"] as JArray ?? []).OfType<JObject>().ToList();
            all.AddRange(events);
            var next = r["next"]?.Type is JTokenType.Integer ? r["next"]!.Value<long>() : r["seq"]?.Value<long>() ?? 0;
            if (events.Count < 500 || next <= since) break;
            since = next;
        }
        return all;
    }
}

[McpServerResourceType]
public static class FeedbackResources
{
    [McpServerResource(UriTemplate = FeedbackHub.Uri, Name = "feedback-sessions", Title = "F8 presses and their sessions", MimeType = "application/json", IconSource = IconSet.GuideLight)]
    [Description("feedback_sessions as a resource (FeedbackSessionsResult: the dispatcher and the newest 50 presses, briefs left out). Subscribable: updated when the dispatcher's files change (a press queued, a handler started, its output written).")]
    public static async Task<string> Sessions(BridgeRegistry bridges, CancellationToken ct)
    {
        var r = await FeedbackSessionsTools.BuildAsync(bridges, null, 50, merge: true, game: null, ct);
        return JsonSerializer.Serialize(r, Dto.Options);
    }
}
