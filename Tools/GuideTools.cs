using System.ComponentModel;
using ExileApiMcp.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// The in-game agent guide (bridge guide.*): a sticky "do this now" card and a short log of what the agent is doing,
/// drawn by the HUD plugin. Use it whenever the user must act in game, so they never have to watch the chat.
/// Guide updates are best-effort: a HUD without the guide (older bridge) is ignored silently.
/// </summary>
[McpServerToolType]
public static class GuideTools
{
    [McpServerTool(Name = "guide", Title = "Show the user what to do in game", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Show an instruction in the in-game agent guide panel (sticky 'do this now' card) and/or add a line to its " +
                 "log. Use it before asking the user to do anything in game - they may not be looking at the chat. " +
                 "await_change sets the instruction and status by itself when you pass instruction. Statuses: waiting (user " +
                 "must act) | detected | settling | captured | failed | info | done. clear=true removes the card.")]
    public static async Task<CallToolResult> Guide(BridgeRegistry bridges,
        [Description("What the user should do, short and concrete, e.g. 'Ctrl+scroll DOWN once over the stash'")] string? instruction = null,
        [Description("waiting | detected | settling | captured | failed | info | done")] string? status = null,
        [Description("Experiment or task title, e.g. 'Stash: switch tabs'")] string? title = null,
        [Description("Step number")] int? step = null,
        [Description("Total steps")] int? steps = null,
        [Description("One extra line under the instruction")] string? detail = null,
        [Description("Add this line to the guide's log")] string? log = null,
        [Description("Remove the instruction card")] bool clear = false,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var set = new JObject();
        if (clear) set["clear"] = true;
        if (instruction != null) set["instruction"] = instruction;
        if (status != null) set["status"] = status;
        if (title != null) set["title"] = title;
        if (step != null) set["step"] = step;
        if (steps != null) set["steps"] = steps;
        if (detail != null) set["detail"] = detail;
        JToken? state = null;
        if (set.Count > 0) state = (await bridges.CallAsync(game, "guide.set", set, ct)).Result;
        if (log != null) await bridges.CallAsync(game, "guide.log", new JObject { ["text"] = log, ["kind"] = "agent" }, ct);
        state ??= (await bridges.CallAsync(game, "guide.state", null, ct)).Result;
        return ToolResults.Json(state);
    }

    [McpServerTool(Name = "guide_state", Title = "What the in-game guide shows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read the in-game agent guide without changing it: the current instruction card (title, instruction, step, " +
                 "status, detail) and the last log lines. Cheap; MCP Apps poll it to mirror the card.")]
    public static async Task<CallToolResult> GuideState(BridgeRegistry bridges,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.CallAsync(game, "guide.state", null, ct)).Result);

    /// <summary>Best-effort guide update from other tools (never throws).</summary>
    internal static async Task SetAsync(BridgeRegistry bridges, string? game, JObject set, CancellationToken ct)
    {
        try { await bridges.CallAsync(game, "guide.set", set, ct); } catch { }
    }

    internal static async Task LogAsync(BridgeRegistry bridges, string? game, string text, string kind, CancellationToken ct)
    {
        try { await bridges.CallAsync(game, "guide.log", new JObject { ["text"] = text, ["kind"] = kind }, ct); } catch { }
    }
}
