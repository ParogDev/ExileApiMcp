using System.ComponentModel;
using System.Text;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>hud_runtime_layout: the CLR's real layout of the HUD's offsets structs, and which property reads which offset.</summary>
[McpServerToolType]
public static class RuntimeLayoutTools
{
    [McpServerTool(Name = "hud_runtime_layout", Title = "Real offsets of a HUD struct (runtime reflection)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Runtime reflection inside the running HUD: the layout the CLR actually uses for an offsets struct (each field set
        alone to a sentinel in a boxed instance; nested game structs flattened as a.b), next to the metadata [FieldOffset]
        ("decoy" where they differ). With path= a memory object (e.g. GameController.Player.GetComponent<Life>()), it also
        maps each public property to the struct field holding its value and checks that field against fresh game memory:
        the PoE2 property -> offset map that hud_property_map cannot read offline (protected getters, obfuscated names).
        Use it to find or confirm an offset before reading memory directly. Read-only.
        """)]
    public static async Task<CallToolResult> HudRuntimeLayout(BridgeRegistry bridges,
        [Description("Walker path to a memory object (its cached offsets struct is used), e.g. GameController.IngameState.Camera")] string? path = null,
        [Description("Or an offsets struct type name (GameOffsets*/ExileCore*), e.g. PositionedComponentOffsets")] string? type = null,
        [Description("List every field (default: only properties and a field count)")] bool fields = false,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(type)) throw new McpException("Pass path or type.");
        var (_, r) = await bridges.CallAsync(game, "layout.runtime", new JObject { ["path"] = path, ["type"] = type }, ct);
        if (r["error"] != null) return ToolResults.Json(r);
        var sb = new StringBuilder();
        var fs = r["fields"] as JArray ?? [];
        sb.AppendLine($"{r["struct"]} size {r["size"]}: {fs.Count} fields measured" +
                      (r["decoyOffsets"] is { } d ? $", {d} with a decoy metadata offset" : ", metadata offsets are real") +
                      (r["unmeasured"] is JArray u ? $", {u.Count} unmeasured" : ""));
        if (r["object"] != null)
        {
            sb.AppendLine($"{r["object"]} at {r["path"]}: struct from {r["structVia"]}, matches memory at Address: {r["structMatchesMemoryAtAddress"] ?? "n/a"}");
            foreach (var p in r["properties"] as JArray ?? [])
                sb.AppendLine(p["ambiguous"] is JArray amb
                    ? $"  {p["property"]} ({p["type"]}): ambiguous {string.Join(", ", amb)}"
                    : $"  {p["property"]} ({p["type"]}) = {p["field"]} @ {p["offset"]}  [{p["memoryNow"]}]");
            if ((r["properties"] as JArray)?.Any(p => p["ambiguous"] != null) == true)
                sb.AppendLine("  ambiguous = several fields hold that value right now (e.g. Current == Max at full life): retry when they differ");
            if ((r["properties"] as JArray)?.Count is null or 0) sb.AppendLine("  no property matched a field (computed properties, or all zero right now)");
        }
        if (fields)
            foreach (var f in fs)
                sb.AppendLine($"  {f["offset"],-7} {f["name"]} : {f["type"]} ({f["size"]} B){(f["decoy"]?.Type == JTokenType.Boolean ? $"  metadata says {f["metadataOffset"]}" : "")}{(f["value"] is { Type: JTokenType.String } v ? $" = {v}" : "")}");
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString().TrimEnd() }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(r.ToString(Newtonsoft.Json.Formatting.None)),
        };
    }
}
