using System.ComponentModel;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
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

    [McpServerTool(Name = "highlight", Title = "Point at things in game", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Draw attention to things on the user's screen (never clicks): items in the inventory or the visible stash tab " +
                 "(by name, all matches), any UI element (walker path) or a screen area. Each target has a tier - primary " +
                 "(click/look here, animated), secondary (related) or context (an area to orient the eye) - and optionally an " +
                 "order for a sequence (numbered; the current step is emphasised, advance=true moves on). Targets follow the UI. " +
                 "Use it with the guide card whenever you ask the user to click something. clear=true removes it.")]
    public static async Task<CallToolResult> Highlight(BridgeRegistry bridges,
        [Description("Targets: [{item:'Chaos Orb' | path:'GameController.IngameState.IngameUi.StashElement' | rect:[x,y,w,h], label?, tier?: primary|secondary|context, order?}]")] System.Text.Json.JsonElement? targets = null,
        [Description("Shortcut: item names to highlight as primary targets")] string[]? items = null,
        [Description("Optional heading, e.g. 'Move these to the stash'")] string? title = null,
        [Description("Sequence step to show as current (default: the lowest order)")] int? current = null,
        [Description("Remove after this many seconds (default: until cleared or replaced)")] double? durationSec = null,
        [Description("Move a sequence to its next step")] bool advance = false,
        [Description("Remove all highlights")] bool clear = false,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        if (advance) return ToolResults.Json((await bridges.CallAsync(game, "guide.highlight_advance", new JObject(), ct)).Result);
        var p = new JObject();
        if (clear) p["clear"] = true;
        else
        {
            var list = targets switch
            {
                { ValueKind: System.Text.Json.JsonValueKind.Array } t => JArray.Parse(t.GetRawText()),
                { ValueKind: System.Text.Json.JsonValueKind.String } s => JArray.Parse(s.GetString()!),   // key=@file, or JSON passed as text
                _ => new JArray(),
            };
            foreach (var i in items ?? []) list.Add(new JObject { ["item"] = i, ["tier"] = "primary" });
            if (list.Count == 0) throw new McpException("Pass targets (or items), or clear=true.");
            p["targets"] = list;
            if (title != null) p["title"] = title;
            if (current != null) p["current"] = current;
            if (durationSec != null) p["durationSec"] = durationSec;
        }
        var (_, r) = await bridges.CallAsync(game, "guide.highlight", p, ct);
        if (r is not JObject o || (o["ok"] == null && o["error"] == null))
            throw new McpException("This HUD's bridge plugin has no highlights yet: update What's an AI Bridge and restart the HUD.");
        return ToolResults.Json(r);
    }

    /// <summary>Best-effort highlight from other tools (never throws); null/empty clears.</summary>
    internal static async Task HighlightAsync(BridgeRegistry bridges, string? game, string? targetsJson, CancellationToken ct)
    {
        try
        {
            var p = string.IsNullOrWhiteSpace(targetsJson) ? new JObject { ["clear"] = true } : new JObject { ["targets"] = JArray.Parse(targetsJson) };
            await bridges.CallAsync(game, "guide.highlight", p, ct);
        }
        catch { }
    }

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
