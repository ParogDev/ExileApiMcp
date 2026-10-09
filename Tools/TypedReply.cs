using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Bridge replies as typed results (the Dto.cs pattern) for tools whose callers also read their error results. A bridge
/// error (an "error" string, or ok:false) stays the result ToolResults.Json makes: isError, with the error object as
/// structuredContent (the stats app takes the current state from a rev_mismatch, the explorer shows the message). A
/// success is parsed with Dto.From into the tool's contract. The text stays the bridge's compact JSON.
/// </summary>
internal static class TypedReply
{
    public static CallToolResult Of<T>(JToken reply) => Of<T>(reply, ToolResults.Json(reply));

    /// <summary>plain: ToolResults' result for this reply (JsonTruncatingEntities trims the reply in place first).</summary>
    public static CallToolResult Of<T>(JToken reply, CallToolResult plain) =>
        plain.IsError == true || reply is not JObject ? plain
            : Dto.Result(Dto.From<T>(reply), ((TextContentBlock)plain.Content[0]).Text);

    /// <summary>JSON this server assembled (not a bridge reply) as a contract: no bridge-error check.</summary>
    public static T Parse<T>(JToken token) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(token.ToString(Formatting.None), Dto.Options)
        ?? throw new ModelContextProtocol.McpException($"No {typeof(T).Name}.");
}
