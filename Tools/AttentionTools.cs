using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// The attention queue from the agent's side (bridge attention.*, Shared\AttentionQueue.cs in What's an AI Bridge). Several
/// agent sessions share one player: one holds the floor (the in-game card) at a time, the rest wait in line, restarts
/// first, nothing new in combat. ask_user puts a question with 2-4 choices on the card and waits for the answer; the
/// guided tools (await_change, experiment_step_start, guide_flow) take the floor through <see cref="AttentionTurn"/>
/// before they show anything. All queue state lives in the bridge: this server only asks and waits.
/// </summary>
[McpServerToolType, McpServerResourceType]
public static class AttentionTools
{
    [McpServerTool(Name = "ask_user", Title = "Ask the player a question in game", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(AskUserResult), IconSource = IconSet.GuideLight)]
    [Description("Ask the player a question ON THE IN-GAME CARD, with 2-4 choices (the card adds Later), and wait for the click. " +
                 "Use it instead of chat when the user is playing: they answer without alt-tabbing. Several agents share the " +
                 "card: the question waits its turn (restarts first, round-robin by session, never shown in combat; urgency=calm " +
                 "waits for town, hideout or a pause), with progress notifications as its place in line changes. Later puts it " +
                 "back for a calmer moment (3 min, then 10 min; the third Later dismisses it). Returns status answered (answer = " +
                 "the chosen option) | dismissed | expired | cancelled, or pending with the id when timeoutSec runs out first: " +
                 "keep working and pick it up with ask_user_result. Batch questions into one ask; ask when blocked, not early.")]
    public static async Task<CallToolResult> AskUser(BridgeRegistry bridges, IProgress<ProgressNotificationValue> progress,
        [Description("The question, short, in game words (up to 120 characters), e.g. 'Which exit leads to Keth?'")] string question,
        [Description("2-4 choices as short labels (up to 24 characters each), e.g. [\"North gate\", \"South road\"]")] string[] options,
        [Description("A 2-4 word title for the card, e.g. 'Route to Keth' (up to 40 characters)")] string? title = null,
        [Description("now (default: as soon as it is your turn and the player isn't fighting) | calm (only in town, hideout or a pause in play)")] string urgency = "now",
        [Description("Seconds to wait for the answer in this call (5-3600, default 300); then status=pending with the id")] int timeoutSec = 300,
        [Description("One extra line under the question (up to 160 characters)")] string? detail = null,
        [Description("Optional hint per option, same order as options (shown when the player hovers the choice)")] string[]? hints = null,
        [Description("How long the question may wait in line before it expires, seconds (10-3600, default 1800)")] int ttlSec = 1800,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        if (options.Length is < 2 or > 4) throw new McpException($"Pass 2-4 options (got {options.Length}); the card adds Later itself.");
        var opts = new JArray(options.Select((o, i) =>
        {
            var j = new JObject { ["label"] = o };
            if (hints != null && i < hints.Length && !string.IsNullOrWhiteSpace(hints[i])) j["hint"] = hints[i];
            return j;
        }));
        var p = new JObject { ["question"] = question, ["options"] = opts, ["urgency"] = urgency, ["ttlSec"] = Math.Clamp(ttlSec, 10, 3600) };
        if (title != null) p["title"] = title;
        if (detail != null) p["detail"] = detail;
        var (bridge, r) = await bridges.CallAsync(game, "attention.ask", p, ct);
        var item = Need<AttentionItemInfo>(r, "attention.ask");
        await LogAsk(bridges, game, $"{SessionIdentity.Label} asks: {question}");
        return await WaitAnswerAsync(bridges, bridge.Game, item, TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 5, 3600)), progress, ct);
    }

    [McpServerTool(Name = "ask_user_result", Title = "The player's answer to an earlier ask_user", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(AskUserResult))]
    [Description("Pick up an ask_user that returned pending: its status and answer now, or wait up to waitSec for it to finish. " +
                 "Queued asks survive a HUD restart, so this works across one.")]
    public static async Task<CallToolResult> AskUserResult(BridgeRegistry bridges, IProgress<ProgressNotificationValue> progress,
        [Description("The ask's id from ask_user")] string id,
        [Description("Wait up to this many seconds for an answer (0-3600, default 0 = just read it)")] int waitSec = 0,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (bridge, r) = await bridges.CallAsync(game, "attention.result", new JObject { ["id"] = id }, ct);
        var item = Need<AttentionItemInfo>(r, "attention.result");
        return await WaitAnswerAsync(bridges, bridge.Game, item, TimeSpan.FromSeconds(Math.Clamp(waitSec, 0, 3600)), progress, ct);
    }

    [McpServerResource(UriTemplate = "exile://attention/{game}/queue", Name = "attention-queue", Title = "Who has the in-game card, who waits", MimeType = "application/json", IconSource = IconSet.GuideLight)]
    [Description("The attention queue (AttentionStateView): who holds the floor (the in-game card), the queue with positions, a " +
                 "waiting HUD restart and the player's state (combat, calm). Subscribable: updated when anything in it moves " +
                 "(the server polls attention.state at 1 Hz only while subscribed).")]
    public static async Task<string> Queue(BridgeRegistry bridges, string game, CancellationToken ct)
        => JsonSerializer.Serialize(await StateAsync(bridges, game, ct), Dto.Options);

    /// <summary>attention.state as the typed view (the resource and AttentionHub share it).</summary>
    internal static async Task<AttentionStateView> StateAsync(BridgeRegistry bridges, string? game, CancellationToken ct)
    {
        var (bridge, r) = await bridges.CallAsync(game, "attention.state", new JObject(), ct);
        var v = Need<AttentionStateView>(r, "attention.state");
        v.Game = bridge.Game;
        return v;
    }

    // ── Waiting ──────────────────────────────────────────────────────

    private static readonly HashSet<string> Final = new(StringComparer.Ordinal) { "answered", "dismissed", "expired", "cancelled", "done" };

    /// <summary>Poll the item once a second until it ends or the wait runs out; a progress notification on each move in line.</summary>
    private static async Task<CallToolResult> WaitAnswerAsync(BridgeRegistry bridges, string game, AttentionItemInfo item, TimeSpan wait,
        IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var said = "";
        var lastBeat = TimeSpan.Zero;
        while (!Final.Contains(item.Status) && sw.Elapsed < wait)
        {
            var line = Line(item);
            if (line != said || sw.Elapsed - lastBeat > TimeSpan.FromSeconds(20))
            {
                said = line; lastBeat = sw.Elapsed;
                progress?.Report(new ProgressNotificationValue { Progress = (float)sw.Elapsed.TotalSeconds, Total = (float)wait.TotalSeconds, Message = line });
            }
            await Task.Delay(1000, ct);
            try { item = Need<AttentionItemInfo>((await bridges.CallAsync(game, "attention.result", new JObject { ["id"] = item.Id }, ct)).Result, "attention.result"); }
            catch (McpException) when (!ct.IsCancellationRequested)
            {
                // The HUD restarting (restarts go first) or the bridge briefly down: the ask is persisted, keep waiting.
                await Task.Delay(2000, ct);
            }
        }
        var final = Final.Contains(item.Status);
        var result = new AskUserResult
        {
            Status = final ? item.Status : "pending", Id = item.Id, Game = game, Answer = item.Answer, WaitedSec = item.WaitedSec ?? (int)sw.Elapsed.TotalSeconds,
            Position = final ? null : item.Position, Ahead = final ? null : item.Ahead, WaitingFor = item.WaitingFor, LaterCount = item.LaterCount,
            Reason = item.Reason, Item = item,
            Note = item.Status switch
            {
                "answered" => item.Answer?.SharedWith is { Count: > 0 } sw2 ? $"Answered on one card shared with {string.Join(", ", sw2)} (they asked the same)." : null,
                "dismissed" => item.Reason == "later_x3" ? "The player said Later three times: they don't want to answer this now. Decide yourself or ask in chat." : "The player dismissed the question.",
                "expired" => item.Reason == "unanswered" ? "It was on the card unanswered for its showTtlSec: go on without the answer, or ask again later." : "It never got a turn before its ttlSec.",
                "cancelled" => item.Why,
                _ => final ? null : $"Still waiting ({Line(item)}). Keep working; ask_user_result id={item.Id} picks it up (waitSec to block).",
            },
        };
        var text = result.Status switch
        {
            "answered" => $"Answered: {item.Answer?.Label} ({item.Answer?.OptionId}) after {result.WaitedSec} s.",
            "pending" => $"Pending ({item.Id}): {Line(item)}.",
            _ => $"{result.Status}{(item.Reason != null ? $" ({item.Reason})" : "")}: {result.Note}",
        };
        return Dto.Result(result, text);
    }

    /// <summary>"on the card now" / "2nd in line, after whats-a-route" / "... (waiting for a calm moment)".</summary>
    internal static string Line(AttentionItemInfo i)
    {
        if (i.Status == "showing") return i.VisibleSince != null ? "on the card now" : "next on the card";
        if (i.Status != "queued") return i.Status;
        var pos = i.Position ?? 0;
        var text = $"{Ordinal(pos)} in line" + (i.Ahead is { Count: > 0 } a ? $", after {string.Join(", ", a)}" : "");
        if (i.LaterCount is > 0) text += $", Later x{i.LaterCount}";
        if (i.WaitingFor != null) text += $" (waiting for {i.WaitingFor})";
        return text;
    }

    private static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>
    /// A reply from a bridge without the attention queue: its unknown-method fallback answers with a bare error or another
    /// query's data, never an item (status), a state (seq) or one of the queue's own errors (which carry a message).
    /// </summary>
    internal static bool IsLegacy(JToken r) => r is not JObject o || (o["status"] == null && o["seq"] == null && o["message"] == null);

    /// <summary>A bridge reply as T; a bridge without the attention queue, or a bridge error, says what broke.</summary>
    internal static T Need<T>(JToken r, string method)
    {
        if (IsLegacy(r))
            throw new McpException($"bridge: {method} is unknown to this HUD's bridge: update What's an AI Bridge and restart the HUD.");
        return Dto.From<T>(r);
    }

    private static async Task LogAsk(BridgeRegistry bridges, string? game, string text) => await GuideTools.LogAsync(bridges, game, text, "agent", CancellationToken.None);
}

/// <summary>
/// The floor for a guided tool (await_change, experiment_step_start, guide_flow): attention.request, then wait for the
/// turn inside the tool's own time limit, reporting the place in line; release at the end. A bridge without the
/// attention queue gives a turn that holds nothing (the tool runs as before).
/// </summary>
internal sealed class AttentionTurn
{
    public string? Id { get; private init; }
    public bool Granted { get; private init; }
    public AttentionItemInfo? Item { get; private init; }
    public TimeSpan Waited { get; private init; }
    /// <summary>Not granted: what it waited for, in words, for the tool's result.</summary>
    public string? Note { get; private init; }

    public static async Task<AttentionTurn> TakeAsync(BridgeRegistry bridges, string? game, string kind, string title, TimeSpan limit,
        Func<string, Task>? onWait, IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (_, r) = await bridges.CallAsync(game, "attention.request", new JObject
            { ["kind"] = kind, ["title"] = title, ["ttlSec"] = Math.Clamp((int)limit.TotalSeconds + 30, 10, 3600) }, ct);
        // An older bridge has no attention.request (it answers with a bare error or some other query's reply, never an
        // item with a status): no queue there, so the tool runs as before.
        if (AttentionTools.IsLegacy(r)) return new AttentionTurn { Granted = true };
        if (r["error"] != null) throw new McpException($"bridge: attention.request: {r["error"]}: {r["message"]}");
        var item = Dto.From<AttentionItemInfo>(r);
        var said = "";
        try
        {
            while (item.Status == "queued" && sw.Elapsed < limit)
            {
                var line = AttentionTools.Line(item);
                if (line != said)
                {
                    said = line;
                    progress?.Report(new ProgressNotificationValue { Progress = (float)sw.Elapsed.TotalSeconds, Total = (float)limit.TotalSeconds, Message = "Waiting for the card: " + line });
                    if (onWait != null) await onWait("queued");
                }
                await Task.Delay(700, ct);
                item = Dto.From<AttentionItemInfo>((await bridges.CallAsync(game, "attention.result", new JObject { ["id"] = item.Id }, ct)).Result);
            }
        }
        catch
        {
            await ReleaseAsync(bridges, game, item.Id, cancel: true);
            throw;
        }
        if (item.Status == "showing") return new AttentionTurn { Id = item.Id, Granted = true, Item = item, Waited = sw.Elapsed };
        // Never got the turn within the limit (or it ended in line): leave the line, say where it stood.
        if (item.Status == "queued") await ReleaseAsync(bridges, game, item.Id, cancel: true);
        return new AttentionTurn
        {
            Id = item.Id, Granted = false, Item = item, Waited = sw.Elapsed,
            Note = item.Status == "queued"
                ? $"Not your turn within {(int)limit.TotalSeconds} s: {AttentionTools.Line(item)}. Nothing was shown to the player; try again later, or use experiment_queue / ask_user (they wait in line without blocking you)."
                : $"The turn ended in line ({item.Status}{(item.Reason != null ? $", {item.Reason}" : "")}{(item.Why != null ? $": {item.Why}" : "")}).",
        };
    }

    /// <summary>End the turn (release = done; cancel = leave the line). Best effort: never throws.</summary>
    public static async Task ReleaseAsync(BridgeRegistry bridges, string? game, string? id, bool cancel = false)
    {
        if (id == null) return;
        try { await bridges.CallAsync(game, cancel ? "attention.cancel" : "attention.release", new JObject { ["id"] = id }, CancellationToken.None); }
        catch (McpException) { }
    }

    public Task ReleaseAsync(BridgeRegistry bridges, string? game) => ReleaseAsync(bridges, game, Granted ? Id : null);
}
