using System.ComponentModel;
using ExileApiMcp.Bridge;
using ExileApiMcp.Dispatch;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// The PoE2 health bar plugin "Whats A Health Bar" seen live: its PluginBridge method WhatsAHealthBar.GetState (counts,
/// its own timings, why bars are suppressed, the preview, its bridge link, recent kills) as a typed contract, and its
/// in-game preview (sample bars with fake hits and heals) started through the bridge's pub/sub (kind healthbars.preview,
/// which the plugin subscribes to) or, when it is not subscribed, through WhatsAHealthBar.Preview directly. The control
/// center's Health Bars page polls the state and previews every setting change in game. Every link that can break is
/// named in the error: the plugin not loaded, the method throwing, a bridge without hud.bridge_call or pub/sub, a result
/// that no longer fits the contract.
/// </summary>
[McpServerToolType]
public static class HealthBarsTools
{
    public const string Plugin = "Whats A Health Bar", Folder = "Whats A Health Bar";
    public const string StateMethod = "WhatsAHealthBar.GetState", PreviewMethod = "WhatsAHealthBar.Preview";
    public const string PreviewKind = "healthbars.preview", KillKind = "healthbars.kill";
    /// <summary>The plugin's subscriber names start with this (one per plugin load).</summary>
    public const string SubscriberPrefix = "WhatsAHealthBar#";
    public static readonly string[] Kinds = ["Unique", "Rare", "Magic", "Normal", "Minion", "Player", "Self"];

    [McpServerTool(Name = "healthbars_state", Title = "Health bar plugin: live state", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(HealthBarsStateResult))]
    [Description("What the PoE2 health bar plugin 'Whats A Health Bar' is doing right now: whether it draws and why not " +
                 "(in town, in hideout, a large panel is open), the area, counts (tracked, wanted, drawn, the peak, per kind), its " +
                 "own timings in microseconds (tick and render average and 5 s max, the classify pass and its rate, nanoseconds " +
                 "per drawn bar), the in-game preview (active, seconds left, focused kind), its link to the AI Bridge's pub/sub " +
                 "(subscribes healthbars.preview, publishes healthbars.kill) and the last kills (name, rarity, time to kill). Read " +
                 "through its PluginBridge method " + StateMethod + "; fields the plugin adds later pass through. Errors name the " +
                 "broken link: plugin not loaded, method threw, result not JSON. Compare its cost with the stock HealthBars plugin " +
                 "through hud_plugin_perf.")]
    public static async Task<CallToolResult> HealthBarsState(BridgeRegistry bridges, [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (bridge, reply) = await bridges.CallAsync(game, "hud.bridge_call", PluginCall.Params(StateMethod), ct);
        var decoded = PluginCall.Decode(reply, StateMethod, Plugin, Folder, "healthbars");
        if (!decoded.Ok) return Fail(decoded.Code!, decoded.Message!);

        HealthBarsStateResult typed;
        try { typed = TypedReply.Parse<HealthBarsStateResult>(decoded.Json!); }
        catch (System.Text.Json.JsonException ex) { return Fail("healthbars_shape", $"{StateMethod} returned JSON that no longer fits HealthBarsStateResult (Tools/HealthBarsDtos.cs): {ex.Message}"); }
        typed.Game ??= bridge.Game;
        return Dto.Result(typed, Summary(typed));
    }

    [McpServerTool(Name = "healthbars_preview", Title = "Health bar plugin: preview in game", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(HealthBarsPreviewResult))]
    [Description("Show the health bar plugin's sample bars in game: one bar per kind mid-screen with fake hits and heals, for " +
                 "`seconds` (1-60, default 8), the `focus` kind first (Unique, Rare, Magic, Normal, Minion, Player, Self), plus `stress` " +
                 "extra animated bars on a grid (0-2000) to measure the cost of many bars (healthbars_state then reports preview.bars and " +
                 "timings.perBarNs over them). It " +
                 "publishes healthbars.preview through the AI Bridge's pub/sub, which the plugin subscribes to; when the plugin " +
                 "is not subscribed (the bridge's pubsub.status does not list it) it calls " + PreviewMethod + " directly. The " +
                 "result says which way it went (via), the event's seq, and whether the plugin was subscribed. Use it after " +
                 "changing a look or animation setting (hud_settings_set, plugin '" + Plugin + "') to see the result without a " +
                 "monster nearby. Errors name the broken link: plugin not loaded, bridge without pub/sub, method threw.")]
    public static async Task<CallToolResult> HealthBarsPreview(BridgeRegistry bridges,
        [Description("How long the sample bars stay, seconds (1-60, default 8)")] double seconds = 8,
        [Description("The kind to put first: Unique, Rare, Magic, Normal, Minion, Player or Self (You). Omit for the natural order")] string? focus = null,
        [Description("Also draw this many animated bars on a grid in game, a stress test (0-2000, default 0)")] int stress = 0,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        if (!double.IsFinite(seconds) || seconds < 1 || seconds > 60) throw new ModelContextProtocol.McpException($"seconds must be 1-60, not {seconds}.");
        if (stress is < 0 or > 2000) throw new ModelContextProtocol.McpException($"stress must be 0-2000, not {stress}.");
        var kind = NormalizeFocus(focus);
        var result = new HealthBarsPreviewResult { Seconds = seconds, Focus = kind, Stress = stress };

        // 1. Is the plugin's subscription registered? pubsub.status lists in-process subscribers by name and kinds.
        string? note = null;
        try
        {
            var (_, status) = await bridges.CallAsync(game, "pubsub.status", null, ct);
            if (status is JObject so && so["plugins"] is JArray plugins)
            {
                result.PluginSubscribed = plugins.Any(p => (p["subscriber"]?.ToString() ?? "").StartsWith(SubscriberPrefix, StringComparison.Ordinal)
                                                           && p["kinds"] is JArray ks && ks.Any(k => k.ToString() == PreviewKind));
                if (result.PluginSubscribed == false)
                    note = plugins.Count == 0
                        ? $"no plugin is subscribed to anything in this bridge: {Plugin} is not loaded, or its link to the bridge is down (healthbars_state bridge.why says)"
                        : $"{Plugin} is not subscribed to {PreviewKind} (subscribed: {string.Join(", ", plugins.Select(p => $"{p["subscriber"]} -> {string.Join("|", (p["kinds"] as JArray ?? []).Select(k => k.ToString()))}"))})";
            }
            else if (FeedbackKit.Refusal(status, "pubsub.status") is { } refusal) note = refusal;
        }
        catch (ModelContextProtocol.McpException ex) when (ex.Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            note = $"this bridge build has no pubsub.status ({ex.Message}): a build before pub/sub";
        }

        // 2. Subscribed (or the bridge could not say): publish; the plugin's handler queues it for its next Tick.
        if (result.PluginSubscribed != false)
        {
            var data = new JObject { ["seconds"] = seconds };
            if (kind != null) data["focus"] = kind;
            if (stress > 0) data["stress"] = stress;
            var (_, pub) = await bridges.CallAsync(game, "pubsub.publish", new JObject { ["kind"] = PreviewKind, ["data"] = data }, ct);
            if (FeedbackKit.Refusal(pub, "pubsub.publish") is { } refusal) note = refusal;
            else
            {
                result.Ok = true;
                result.Via = "pubsub";
                result.Seq = pub["seq"]?.Value<long>();
                result.AgentListeners = (pub["subscribers"] as JArray ?? []).Select(s => s["who"]?.ToString() ?? s["subscriber"]?.ToString() ?? "?").ToList();
                result.Note = result.PluginSubscribed == null ? "the bridge could not list its plugin subscribers; the event was published anyway: healthbars_state preview.active says whether it arrived" : null;
                return Dto.Result(result, Summary(result));
            }
        }

        // 3. Not subscribed, or pub/sub refused: the plugin's own method, which also tells us whether it is loaded at all.
        var arg = new JObject { ["seconds"] = seconds };
        if (kind != null) arg["focus"] = kind;
        if (stress > 0) arg["stress"] = stress;
        var (_, reply) = await bridges.CallAsync(game, "hud.bridge_call", PluginCall.Params(PreviewMethod, arg.ToString(Newtonsoft.Json.Formatting.None)), ct);
        var decoded = PluginCall.Decode(reply, PreviewMethod, Plugin, Folder, "healthbars");
        if (!decoded.Ok) return Fail(decoded.Code!, $"{decoded.Message} (pub/sub first: {note ?? "the plugin was not subscribed"})");
        if (decoded.Json!["ok"]?.Value<bool>() != true)
            return Fail("preview_refused", $"{PreviewMethod} refused: {decoded.Json["error"]}: {decoded.Json["message"]}");
        result.Ok = true;
        result.Via = "bridge_call";
        result.Note = $"called {PreviewMethod} directly because {note ?? "the plugin was not subscribed to " + PreviewKind}";
        return Dto.Result(result, Summary(result));
    }

    /// <summary>A kind name as the plugin spells it, or null for the natural order; "You" is Self.</summary>
    public static string? NormalizeFocus(string? focus)
    {
        if (string.IsNullOrWhiteSpace(focus)) return null;
        var f = focus.Trim();
        if (f.Equals("You", StringComparison.OrdinalIgnoreCase)) return "Self";
        return Kinds.FirstOrDefault(k => k.Equals(f, StringComparison.OrdinalIgnoreCase))
               ?? throw new ModelContextProtocol.McpException($"focus must be one of {string.Join(", ", Kinds)} (or You), not '{focus}'.");
    }

    /// <summary>An error result the Health Bars page can read by code (isError, structuredContent {error, message}).</summary>
    private static CallToolResult Fail(string code, string message) => ToolResults.Json(new JObject { ["error"] = code, ["message"] = message });

    private static string Summary(HealthBarsStateResult s)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(s.Plugin ?? Plugin).Append(s.Enabled ? "" : " (disabled)");
        if (s.Area != null) sb.Append($" · {s.Area}");
        sb.Append(s.Suppressed != null ? $"\nNot drawing: {s.Suppressed}" : "\nDrawing");
        var c = s.Counts;
        var byKind = c.ByKind.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value}");
        sb.Append($"\nBars: {c.Drawn} drawn of {c.Wanted} wanted, {c.Tracked} tracked, peak {c.PeakDrawn}{(byKind.Any() ? $" ({string.Join(", ", byKind)})" : "")}");
        var t = s.Timings;
        sb.Append($"\nOwn cost per frame: tick {t.TickUs:0.#} us (max {t.TickMaxUs:0.#} over {t.WindowSec} s), render {t.RenderUs:0.#} us (max {t.RenderMaxUs:0.#}); " +
                  $"classify {t.ClassifyUs:0.#} us at {t.ClassifyHz} Hz; {t.PerBarNs:0} ns per drawn bar");
        if (s.Preview.Active) sb.Append($"\nPreview in game: {s.Preview.SecondsLeft:0.#} s left{(s.Preview.Focus != null ? $", {s.Preview.Focus} first" : "")}");
        sb.Append($"\nBridge: {(s.Bridge.Linked ? "linked" : "not linked")}{(s.Bridge.Why != null ? $" ({s.Bridge.Why})" : "")}; publish kills: {s.Bridge.PublishKills ?? "?"}");
        if (s.PoolProblem != null) sb.Append($"\nPOOL PROBLEM (a read that couldn't be real; a moved offset?): {s.PoolProblem}");
        if (s.Kills.Count > 0)
        {
            sb.Append($"\nKills ({s.Kills.Count}):");
            foreach (var k in s.Kills.Take(5)) sb.Append($"\n- {k.Name} ({k.Rarity}){(k.TimeToKillMs is { } ms ? $" in {ms / 1000.0:0.#} s" : "")}{(k.Area != null ? $" · {k.Area}" : "")}");
        }
        return sb.ToString();
    }

    private static string Summary(HealthBarsPreviewResult r)
    {
        var what = $"Preview for {r.Seconds:0.#} s{(r.Focus != null ? $", {r.Focus} first" : "")}{(r.Stress > 0 ? $", {r.Stress} stress bars" : "")}";
        return r.Via == "pubsub"
            ? $"{what}: published {PreviewKind} #{r.Seq}; the plugin is {(r.PluginSubscribed == true ? "subscribed" : "probably subscribed (the bridge could not list it)")}" +
              $"{(r.AgentListeners.Count > 0 ? $"; agent listeners: {string.Join(", ", r.AgentListeners)}" : "")}{(r.Note != null ? $"\n{r.Note}" : "")}"
            : $"{what} through {PreviewMethod}.\n{r.Note}";
    }
}
