using System.ComponentModel;
using System.Text.Json;
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
    [McpServerTool(Name = "guide", Title = "Show the user what to do in game", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(GuideState), IconSource = ExileApiMcp.Hosting.IconSet.GuideLight)]
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
        [Description("Add this line to the guide's log (shown as a toast that fades in 3 s)")] string? log = null,
        [Description("The log line's kind, which sets its toast's colour and icon: agent (default) | step | result | warn | error")] string? logKind = null,
        [Description("A 2-3 word toast title, e.g. 'Low life' (shown in caps); default: from the kind")] string? logTitle = null,
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
        if (log != null)
        {
            var line = new JObject { ["text"] = log, ["kind"] = logKind is "step" or "result" or "warn" or "error" ? logKind : "agent" };
            if (logTitle != null) line["title"] = logTitle;
            await bridges.CallAsync(game, "guide.log", line, ct);
        }
        state ??= (await bridges.CallAsync(game, "guide.state", null, ct)).Result;
        // After a change the bridge only acknowledges it ({ok, rev}); otherwise this is the full state.
        return set.Count > 0 ? TypedReply.Of<GuideAck>(state) : TypedReply.Of<GuideState>(state);
    }

    [McpServerTool(Name = "guide_state", Title = "What the in-game guide shows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(GuideState))]
    [Description("Read the in-game agent guide without changing it: the current instruction card (title, instruction, step, " +
                 "status, detail) and the last log lines. Cheap; MCP Apps poll it to mirror the card.")]
    public static async Task<CallToolResult> GuideState(BridgeRegistry bridges,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default) =>
        TypedReply.Of<GuideState>((await bridges.CallAsync(game, "guide.state", null, ct)).Result);

    [McpServerTool(Name = "highlight", Title = "Point at things in game", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(HighlightState), IconSource = ExileApiMcp.Hosting.IconSet.GuideLight)]
    [Description("Draw attention to things on the user's screen (never clicks): items in the inventory or the visible stash tab " +
                 "(by name, all matches), any UI element (walker path) or a screen area. Each target has a tier - primary " +
                 "(click/look here, animated), secondary (related) or context (an area to orient the eye) - and optionally an " +
                 "order for a sequence (numbered; the current step is emphasised, advance=true moves on). Targets follow the UI. " +
                 "Use it with the guide card whenever you ask the user to click something. clear=true removes it. " +
                 "Every agent draws in its own layer: a call replaces or clears only your own targets, never another " +
                 "agent's (their questions stay on screen), and the overlay shows all layers together; the result's layers " +
                 "lists whose targets are on screen. " +
                 "Unsure what a UI element is? Give the target ask='Is this the Keth stop?' (+ key for correlation): the user " +
                 "gets Yes / No / Not sure next to it in game, and await_verdicts collects the answers - no chat round trip.")]
    public static async Task<CallToolResult> Highlight(BridgeRegistry bridges,
        [Description("Targets: [{item:'Chaos Orb' | panel:'Stash Tab Settings', child:[0,1,7,1,11,1] | path:'GameController.IngameState.IngameUi.StashElement' | rect:[x,y,w,h], label?, tier?: primary|secondary|context, order?, ask?, key?}]. For unmapped panels prefer panel (text inside it) + child (indexes inside it): top-level IngameUi.Children indexes shift. text:'DUMP' (+ within:'panel text') targets elements by their exact label (stash tabs, buttons). action: click|rightclick draws a mouse cue. until: checked|unchecked|gone ends a step; sequences advance by themselves as the user acts (a later step appearing, a met until, the current target leaving). ask: a yes/no question about your guess, shown with Yes / No / Not sure controls (the user validates your mapping on the spot); key: your id for the answer, e.g. 'worldmap.stop.10=G2_4_1'")] System.Text.Json.JsonElement? targets = null,
        [Description("Shortcut: item names to highlight as primary targets")] string[]? items = null,
        [Description("Optional heading, e.g. 'Move these to the stash'")] string? title = null,
        [Description("Sequence step to show as current (default: the lowest order)")] int? current = null,
        [Description("Remove after this many seconds (default: until cleared or replaced)")] double? durationSec = null,
        [Description("Move a sequence to its next step")] bool advance = false,
        [Description("Remove your highlight (other agents' layers stay)")] bool clear = false,
        [Description("With clear=true: clear every agent's layer, unanswered questions included. Only when the user asked for a clean screen")] bool force = false,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        if (advance) return TypedReply.Of<HighlightState>((await bridges.CallAsync(game, "guide.highlight_advance", new JObject(), ct)).Result);
        var p = new JObject();
        if (clear) { p["clear"] = true; if (force) p["force"] = true; }
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
        return r["cleared"] != null ? TypedReply.Of<HighlightCleared>(r) : TypedReply.Of<HighlightState>(r);
    }

    [McpServerTool(Name = "verdicts", Title = "The user's answers to asked highlights", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(VerdictsResult), IconSource = ExileApiMcp.Hosting.IconSet.GuideLight)]
    [Description("Read the verdicts the user gave in game (Yes / No / Not sure on highlight targets that had ask), newest seq, " +
                 "and what your highlight is still asking (asked, pending; your own layer plus a flow or queued step you " +
                 "started). Each verdict says whose question it was (who, layer); layers is every agent's highlight with its " +
                 "pending count. since = the seq already handled. " +
                 "Verdicts persist in the HUD's verdicts.jsonl. Non-blocking; await_verdicts waits for them.")]
    public static async Task<CallToolResult> Verdicts(BridgeRegistry bridges,
        [Description("Return verdicts with seq greater than this (default 0: the last 100 kept)")] long since = 0,
        [Description("At most this many (1-300, default 100)")] int limit = 100,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (_, r) = await bridges.CallAsync(game, "guide.verdicts", new JObject { ["since"] = since, ["limit"] = limit }, ct);
        NeedVerdicts(r);
        var v = Dto.From<VerdictsResult>(r);
        return Dto.Result(v, VerdictsText(v));
    }

    [McpServerTool(Name = "await_verdicts", Title = "Wait for the user's Yes / No in game", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(VerdictsResult), IconSource = ExileApiMcp.Hosting.IconSet.GuideLight)]
    [Description("Block until the user has answered the asked highlight targets (highlight targets with ask), then return the " +
                 "verdicts. Without keys: until every asked target of your highlight is answered (other agents' questions don't " +
                 "count; returns at once, with a " +
                 "note, when nothing is asked, e.g. the highlight was cleared or replaced). With keys: until each key (or id) has a " +
                 "verdict after since (pass the highlight result's verdictSeq as since so earlier sessions' answers to the same " +
                 "key don't count). Timeout: returns what there is with extra.waiting = true. Never sends input.")]
    public static async Task<CallToolResult> AwaitVerdicts(BridgeRegistry bridges,
        [Description("Keys (or ids) that must all be answered; default: every asked target of the current highlight")] string[]? keys = null,
        [Description("Only verdicts with seq greater than this count (default: those given under the current highlight)")] long? since = null,
        [Description("Max wait, seconds (5-3600, default 600)")] int timeoutSec = 600,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSec, 5, 3600));
        var want = keys is { Length: > 0 } ? keys.ToHashSet(StringComparer.Ordinal) : null;
        while (true)
        {
            JToken r;
            try { (_, r) = await bridges.CallAsync(game, "guide.verdicts", new JObject { ["since"] = since ?? 0, ["limit"] = 300 }, ct); }
            catch (McpException) when (DateTime.UtcNow < until) { await Task.Delay(3000, ct); continue; }   // HUD restarting
            NeedVerdicts(r);
            var v = Dto.From<VerdictsResult>(r);
            // Without since: only answers given under your current highlight(s) count (a reused key answered last week does
            // not, nor another agent's answer). Each asked target carries the rev of its layer.
            if (since == null)
            {
                var revs = v.Asked.Select(a => a.HighlightRev ?? v.HighlightRev).Append(v.HighlightRev).ToHashSet();
                v.Verdicts = v.Verdicts.Where(x => revs.Contains(x.HighlightRev)).ToList();
            }
            bool done;
            string? note = null;
            if (want != null)
            {
                var have = v.Verdicts.Select(x => x.Key).Concat(v.Verdicts.Select(x => x.Id)).Where(x => x != null).ToHashSet(StringComparer.Ordinal)!;
                done = want.All(k => have.Contains(k));
                v.Verdicts = v.Verdicts.Where(x => want.Contains(x.Id) || (x.Key != null && want.Contains(x.Key))).ToList();
            }
            else if (v.Asked.Count == 0) { done = true; note = "Nothing is asked right now: the highlight has no ask targets (cleared, replaced or never set)."; }
            else done = v.Pending == 0;
            if (done) return Dto.Result(v, note != null ? note + "\n" + VerdictsText(v) : VerdictsText(v));
            if (DateTime.UtcNow >= until)
            {
                v.Extra = new() { ["waiting"] = JsonSerializer.SerializeToElement(true) };
                return Dto.Result(v, $"Still waiting: {v.Pending} asked target(s) unanswered.\n" + VerdictsText(v));
            }
            await Task.Delay(500, ct);
        }
    }

    private static void NeedVerdicts(JToken? r)
    {
        if (r is not JObject o || (o["ok"] == null && o["error"] == null))
            throw new McpException("This HUD's bridge plugin has no verdicts yet: update What's an AI Bridge and restart the HUD.");
    }

    private static string VerdictsText(VerdictsResult v)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{v.Verdicts.Count} verdict(s), seq {v.Seq}; your highlight rev {v.HighlightRev}: {v.Asked.Count} asked, {v.Pending} pending.");
        var others = (v.Layers ?? []).Where(l => l.Mine != true).ToList();
        if (others.Count > 0)
            sb.Append($"\n  Other agents' layers: {string.Join(", ", others.Select(l => $"{l.Who ?? l.Layer} ({l.Targets} target(s){(l.Pending > 0 ? $", {l.Pending} pending" : "")})"))}");
        foreach (var x in v.Verdicts)
            sb.Append($"\n  #{x.Seq} {x.Answer.ToUpperInvariant()}  {x.Ask}{(x.Key != null ? $"  [{x.Key}]" : "")}{(x.Label != null ? $"  ({x.Label})" : "")}{(x.Who != null && others.Any(l => l.Layer == x.Layer) ? $"  - {x.Who}'s" : "")}");
        foreach (var a in v.Asked.Where(a => a.Answer == null))
            sb.Append($"\n  ? {a.Ask}{(a.Key != null ? $"  [{a.Key}]" : "")}{(a.OnScreen ? "" : "  (not on screen now)")}");
        return sb.ToString();
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
