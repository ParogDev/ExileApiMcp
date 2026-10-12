using System.ComponentModel;
using ExileApiMcp.Bridge;
using ExileApiMcp.Dispatch;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// agent_status: the calling session's own row on the in-game agents board (research/f8-agent-ux.md 5.5): the bridge's
/// work.set for the caller (Key defaults to its session id; the bridge knows who calls from the connection's hello).
/// F8 presses are not set here: their items follow feedback.reply (feedback_reply).
/// </summary>
[McpServerToolType]
public static class AgentStatusTools
{
    [McpServerTool(Name = "agent_status", Title = "Say what you are working on (agents board)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(AgentStatusResult))]
    [Description("Set your own row on the in-game agents board (the sessions chip's hover and the pinned board): title = your task " +
                 "(<= 60 chars), stage = where you are (Investigating, Fixing, Reviewing, Merging, Deploying, Done, Failed: any " +
                 "subset), text = a short current line (<= 400), detail = the longer why, pr = the PR you wait on. Only the fields " +
                 "you pass change. 'Needs you' is derived by the bridge from your open ask / guided step / Manual route step, never " +
                 "set here. F8 handlers use feedback_reply for their press instead.")]
    public static async Task<CallToolResult> AgentStatus(BridgeRegistry bridges,
        [Description("Your task, <= 60 chars, e.g. 'Stash tab sort for Whats A Route'")] string? title = null,
        [Description("Investigating | Fixing | Reviewing | Merging | Deploying | Verifying | Done | Failed")] string? stage = null,
        [Description("A short current line, <= 400 chars")] string? text = null,
        [Description("The longer why, <= 400 chars (hover)")] string? detail = null,
        [Description("The PR you wait on: {repo, url, number, checks: pending | green | red}")] F8PrRef? pr = null,
        [Description("Task (default) | F8")] string? kind = null,
        [Description("The item's key (default: your session id; one item per session)")] string? key = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        string? s = null;
        if (stage != null)
        {
            s = F8.Stage(stage) ?? throw new McpException($"stage must be one of {string.Join(" | ", F8.Stages)}, not '{stage}'.");
            if (s is "Pressed" or "Queued" or "Claimed") throw new McpException($"{s} is the F8 pipeline's (the bridge and the dispatcher set it): a session starts at Investigating.");
        }
        var k = kind == null ? null : kind.Trim().Equals("f8", StringComparison.OrdinalIgnoreCase) ? "F8" : kind.Trim().Equals("task", StringComparison.OrdinalIgnoreCase) ? "Task"
            : throw new McpException($"kind must be Task or F8, not '{kind}'.");
        if (title == null && s == null && text == null && detail == null && pr == null)
            throw new McpException("Pass at least one of title, stage, text, detail, pr.");
        var set = new AgentWorkSet
        {
            Key = string.IsNullOrWhiteSpace(key) ? null : key.Trim(), Kind = k,
            Title = Clip(title, 60), Stage = s, Text = Clip(text, 400), Detail = Clip(detail, 400), Pr = pr,
        };
        var (_, r) = await bridges.CallAsync(game, "work.set", F8Wire.ToJObject(set), ct);
        var item = r["item"] as JObject ?? (r is JObject o && (o["Key"] ?? o["key"]) != null ? o : null);
        if (item == null)
        {
            if (r["error"] != null) throw new McpException($"work.set refused: {r["error"]}: {r["message"]}");
            throw new McpException($"This bridge build has no work.set (it answered {FeedbackKit.Clip(r.ToString(Newtonsoft.Json.Formatting.None), 120)}): " +
                                   "update Whats An AI Bridge and restart the HUD (hud_restart).");
        }
        var result = new AgentStatusResult { Ok = true, Item = Dto.From<AgentWork>(item) };
        return Dto.Result(result, $"Your board row: {WorkLine(result.Item)}");
    }

    private static string? Clip(string? s, int max) => FeedbackKit.Clip(s, max) is { Length: > 0 } c ? c : null;

    /// <summary>One work item as a short line: "F8 #12 Wrong place - Fixing (waits on Ci): text".</summary>
    internal static string WorkLine(AgentWork? w)
    {
        if (w == null) return "(none)";
        var what = w.Kind == "F8" && w.Seq is { } seq ? $"F8 #{seq}{(w.Title != null ? " " + w.Title : "")}" : w.Title ?? w.Key ?? "?";
        var line = $"{what} - {w.Stage ?? "?"}";
        if (w.WaitingOn is { } wo && wo != "None") line += $" (waits on {wo}{(wo == "Player" && w.Ask != null ? $": {w.Ask}" : "")})";
        if (w.Pr is { } pr) line += $" [PR #{pr.Number}{(pr.Checks != null ? " " + pr.Checks : "")}]";
        if (w.Text != null) line += $": {FeedbackKit.Clip(w.Text, 120)}";
        return line;
    }
}
