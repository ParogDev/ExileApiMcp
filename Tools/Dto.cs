using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// The pattern for typed tool contracts (see ObserveDtos.cs for a full family):
// - a DTO per result, mirroring the JSON the tool already returns (same names and nesting: MCP Apps read it);
// - [JsonExtensionData] Extra on every DTO, so fields the bridge adds later pass through and the schema allows them;
// - [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(TheDto))], returning Dto.Result(value, text);
// - Dto.From<T>(bridgeReply) parses a bridge reply and turns a bridge error object into a tool error naming it.

/// <summary>Shared JSON options for the typed contracts: camelCase on the wire, nulls omitted, bridge casing tolerated.</summary>
public static class Dto
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // Plain quotes and non-ASCII in payloads (the default escapes them as XXXX: bigger and harder to read).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A bridge reply (Newtonsoft) as a typed contract; a bridge error object becomes a tool error naming it.</summary>
    public static T From<T>(Newtonsoft.Json.Linq.JToken token) => token["error"] is { } err && token["ok"] == null
        ? throw new ModelContextProtocol.McpException($"bridge: {err}: {token["message"]}")
        : Parse<T>(token);

    private static T Parse<T>(Newtonsoft.Json.Linq.JToken token) =>
        JsonSerializer.Deserialize<T>(token.ToString(Newtonsoft.Json.Formatting.None), Options)
        ?? throw new ModelContextProtocol.McpException($"The bridge returned no {typeof(T).Name}.");

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>A tool result: the typed value as structuredContent (matching the output schema) and a text rendering.</summary>
    public static ModelContextProtocol.Protocol.CallToolResult Result<T>(T value, string text) => new()
    {
        Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = text.TrimEnd() }],
        StructuredContent = Element(value),
    };
}
