using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// A plugin's PluginBridge method called through the bridge's hud.bridge_call, decoded into the JSON object it returned
/// or the first broken link, named: the plugin not loaded (its method missing from PluginBridge), the method throwing, a
/// bridge too old for hud.bridge_call, a result that is not JSON. Shared by the plugin-state tools (route_state,
/// healthbars_state); each keeps its own wording through <paramref name="plugin"/> and the code prefix.
/// </summary>
public static class PluginCall
{
    public sealed record Reply(JObject? Json, string? Code, string? Message)
    {
        public bool Ok => Json != null;
    }

    /// <param name="reply">The bridge's hud.bridge_call answer.</param>
    /// <param name="method">The PluginBridge method asked for, e.g. WhatsARoute.GetState.</param>
    /// <param name="plugin">The plugin's display name, for the messages.</param>
    /// <param name="folder">The plugin's folder under Plugins\Source, for the hud_plugins hint.</param>
    /// <param name="prefix">The error code prefix for a result that does not fit: "{prefix}_not_json".</param>
    public static Reply Decode(JToken reply, string method, string plugin, string folder, string prefix)
    {
        if (reply is not JObject r) return new Reply(null, "bridge_reply", $"hud.bridge_call returned {reply.Type}, not an object.");
        if (r["error"] is JValue { Type: JTokenType.String } err && r["ok"] == null)
        {
            var message = r["message"]?.ToString() ?? "";
            return err.Value<string>() switch
            {
                "method_missing" => new Reply(null, "plugin_not_loaded", $"{plugin} is not loaded in this HUD, or an older build without {method}: {message} hud_plugins plugin={folder} says whether it compiled."),
                "invoke_failed" => new Reply(null, "plugin_threw", $"{method} threw: {message}"),
                "unavailable" => new Reply(null, "bridge_unavailable", message),
                var code => new Reply(null, code ?? "bridge_error", message),
            };
        }
        if (r["ok"] == null && r["resultType"] == null)
        {
            var text = r.ToString(Newtonsoft.Json.Formatting.None);
            return new Reply(null, "bridge_too_old", $"This bridge build has no hud.bridge_call (it answered {Clip(text)}): update Whats An AI Bridge and restart the HUD.");
        }
        var kind = r["resultType"]?.ToString();
        if (kind != "json")
        {
            var text = r["result"]?.ToString() ?? "";
            return new Reply(null, $"{prefix}_not_json", $"{method} returned {kind ?? "nothing"}, not JSON: {Clip(text)}");
        }
        if (r["result"] is not JObject json) return new Reply(null, $"{prefix}_not_json", $"{method} returned a JSON {r["result"]?.Type}, not an object.");
        return new Reply(json, null, null);
    }

    /// <summary>The hud.bridge_call parameters for a method with scalar arguments (none: no args).</summary>
    public static JObject Params(string method, params object?[] args)
    {
        var p = new JObject { ["name"] = method };
        if (args.Length > 0) p["args"] = new JArray(args.Select(a => a == null ? JValue.CreateNull() : JToken.FromObject(a)));
        return p;
    }

    private static string Clip(string text) => text.Length > 200 ? text[..200] + "..." : text;
}
