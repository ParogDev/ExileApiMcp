using System.Text.Json;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Turns bridge JSON into MCP tool results: compact JSON text (for any client) plus the same
/// object as structuredContent (for apps and clients that use it). Bridge responses that carry
/// an error (top-level "error" string, or "ok": false) are flagged isError so agents notice.
/// </summary>
internal static class ToolResults
{
    // Tool results get wrapped and escaped by clients; keep raw JSON comfortably small.
    public const int MaxResponseChars = 50_000;

    public static CallToolResult Json(JToken result)
    {
        var text = result.ToString(Formatting.None);
        var isError = result is JObject o
                      && ((o["error"] is JValue { Type: JTokenType.String } && o["ok"] == null)
                          || o["ok"] is JValue { Type: JTokenType.Boolean } ok && !ok.Value<bool>());
        var call = new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            IsError = isError ? true : null,
        };
        if (result is JObject)
            call.StructuredContent = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(text);
        return call;
    }

    /// <summary>
    /// If the response exceeds the character budget, drop entities from the end (furthest first,
    /// since the bridge sorts by distance) until it fits, and add a _truncated note.
    /// </summary>
    public static CallToolResult JsonTruncatingEntities(JToken result, string fullListHint)
    {
        var json = result.ToString(Formatting.None);
        if (json.Length > MaxResponseChars && result is JObject obj && obj["entities"] is JArray entities && entities.Count > 0)
        {
            var originalCount = entities.Count;
            while (json.Length > MaxResponseChars && entities.Count > 0)
            {
                entities.RemoveAt(entities.Count - 1);
                json = obj.ToString(Formatting.None);
            }
            obj["_truncated"] = $"Showing {entities.Count} of {originalCount} entities (response too large). {fullListHint}";
        }
        return Json(result);
    }
}
