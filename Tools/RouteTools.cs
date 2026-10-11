using System.ComponentModel;
using ExileApiMcp.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// The campaign navigation plugin "Whats A Route" (PoE2) seen live: its PluginBridge method WhatsARoute.GetState
/// (Func&lt;string&gt;, JSON) called through the bridge's hud.bridge_call and returned as a typed contract. The control
/// center's Route page polls it. Every link that can break is named in the error: the plugin not loaded (its method
/// missing from PluginBridge), the method throwing, a non-JSON result, or JSON that no longer fits the contract.
/// </summary>
[McpServerToolType]
public static class RouteTools
{
    public const string Method = "WhatsARoute.GetState";

    [McpServerTool(Name = "route_state", Title = "Route plugin: live state", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(RouteStateResult))]
    [Description("What the PoE2 campaign navigation plugin 'Whats A Route' is doing right now: area, mode, the current step with " +
                 "its target and label, why there is no path when there is none, every step with done / optional, active quests, " +
                 "the quest objective and travel target, the player's grid cell, the area size, the route as grid cells (mapPath, " +
                 "every few cells), the travel map's stops while it is open, and the plugin's own timings with the load they were " +
                 "measured under (entities, field cells reached). Read through its PluginBridge method " + Method + "; fields " +
                 "the plugin adds later pass through. Errors name the broken link: plugin not loaded, method threw, result not JSON.")]
    public static async Task<CallToolResult> RouteState(BridgeRegistry bridges, [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var (_, reply) = await bridges.CallAsync(game, "hud.bridge_call", new JObject { ["name"] = Method }, ct);
        if (reply is not JObject r) return Fail("bridge_reply", $"hud.bridge_call returned {reply.Type}, not an object.");
        if (r["error"] is JValue { Type: JTokenType.String } err && r["ok"] == null)
        {
            var message = r["message"]?.ToString() ?? "";
            return err.Value<string>() switch
            {
                "method_missing" => Fail("plugin_not_loaded", $"Whats A Route is not loaded in this HUD, or an older build without {Method}: {message} hud_plugins plugin=Route says whether it compiled."),
                "invoke_failed" => Fail("plugin_threw", $"{Method} threw: {message}"),
                "unavailable" => Fail("bridge_unavailable", message),
                var code => Fail(code ?? "bridge_error", message),
            };
        }
        if (r["ok"] == null && r["resultType"] == null)
        {
            var text = r.ToString(Newtonsoft.Json.Formatting.None);
            return Fail("bridge_too_old", $"This bridge build has no hud.bridge_call (it answered {(text.Length > 200 ? text[..200] + "..." : text)}): update Whats An AI Bridge and restart the HUD.");
        }
        var kind = r["resultType"]?.ToString();
        if (kind != "json")
        {
            var text = r["result"]?.ToString() ?? "";
            return Fail("route_not_json", $"{Method} returned {kind ?? "nothing"}, not JSON: {(text.Length > 200 ? text[..200] + "..." : text)}");
        }
        if (r["result"] is not JObject state) return Fail("route_not_json", $"{Method} returned a JSON {r["result"]?.Type}, not an object.");

        RouteStateResult typed;
        try { typed = TypedReply.Parse<RouteStateResult>(state); }
        catch (System.Text.Json.JsonException ex) { return Fail("route_shape", $"{Method} returned JSON that no longer fits RouteStateResult (Tools/RouteDtos.cs): {ex.Message}"); }
        return Dto.Result(typed, Summary(typed));
    }

    /// <summary>An error result the Route page can read by code (isError, structuredContent {error, message}).</summary>
    private static CallToolResult Fail(string code, string message) => ToolResults.Json(new JObject { ["error"] = code, ["message"] = message });

    private static string Summary(RouteStateResult s)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(s.AreaName ?? s.Area ?? "no area");
        if (s.AreaName != null && s.Area != null) sb.Append($" ({s.Area})");
        if (!string.IsNullOrEmpty(s.Mode)) sb.Append($" · mode {s.Mode}");
        var step = s.StepIndex >= 0 && s.StepIndex < s.Steps.Count ? s.Steps[s.StepIndex] : null;
        if (step != null) sb.Append($"\nStep {s.StepIndex + 1}/{s.Steps.Count}: {step.Label ?? step.Target}{(step.Optional ? " (optional)" : "")}");
        else if (s.Steps.Count > 0) sb.Append($"\n{s.Steps.Count(x => x.Done)}/{s.Steps.Count} steps done, no current step");
        else sb.Append("\nNo steps for this area");
        if (s.Target != null) sb.Append($"\nTarget: {s.Target}{(s.Label != null ? $" -> {s.Label}" : "")} ({s.Goals} goal(s))");
        if (s.Why != null) sb.Append($"\nNo path: {s.Why}");
        else if (s.PathCells > 0) sb.Append($"\nPath: {s.PathCells} cells, {s.PathWorldLength:0} world units, {s.MapPath.Count / 2} map points");
        if (s.PlayerGrid is { Length: 2 }) sb.Append($"\nPlayer at [{s.PlayerGrid[0]}, {s.PlayerGrid[1]}]{(s.AreaSize is { Length: 2 } ? $" of {s.AreaSize[0]}x{s.AreaSize[1]}" : "")}{(s.PlayerTile != null ? $" on {s.PlayerTile}" : "")}");
        if (s.QuestObjective != null) sb.Append($"\nQuest: {s.QuestObjective}");
        if (s.TravelTo != null) sb.Append($"\nTravel to: {s.TravelTo}{(s.TravelMapOpen ? $" (map open, {s.TravelMapStops.Count} stops)" : "")}");
        if (s.ActiveQuests.Count > 0) sb.Append($"\nActive quests: {string.Join(", ", s.ActiveQuests)}");
        var t = s.Timings;
        sb.Append($"\nTimings under {t.Entities} entities, {t.Places} places, {t.Goals} goal(s): tick {t.TickUs:0} us, render {t.RenderUs:0} us per frame; " +
                  $"replan {t.LastReplanUs:0} us ({s.PathCells} path cells); sightings scan {t.SightingsScanUs:0} us, resolve {t.ResolveUs:0} us (2 Hz); " +
                  $"grid {t.GridBuildMs:0.#} ms, field {t.FieldBuildMs:0.#} ms ({t.FieldCellsReached} cells reached), edges {t.EdgeBuildMs:0.#} ms ({t.EdgeContours} contours, {s.EdgePieces} pieces)");
        foreach (var n in s.Notes) sb.Append($"\n- {n}");
        return sb.ToString();
    }
}
