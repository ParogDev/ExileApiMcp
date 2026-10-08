using System.ComponentModel;
using System.Text;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// The game's data tables (Data/*.dat) as loaded in memory (bridge data.*): name the ids you find in memory without
/// asking the user - e.g. Data/StashTabAffinityId.dat lists stash affinities in bit order. Read-only.
/// </summary>
[McpServerToolType]
public static class GameDataTools
{
    [McpServerTool(Name = "game_data", Title = "Read the game's data tables", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List or read the game's data files (Data/*.dat) as loaded in memory. Without file: lists files whose name " +
                 "contains filter (default '.dat'). With file: rows with their index, every text field decoded (+slot:text) " +
                 "and the first int32s, optionally only rows whose text contains find. Use it to name ids and enums found in " +
                 "memory before asking the user: the row index usually IS the id (e.g. StashTabAffinityId.dat row n = affinity " +
                 "bit n). Works with the game running; no schema needed for string-keyed tables.")]
    public static async Task<CallToolResult> GameData(BridgeRegistry bridges,
        [Description("Data file to read, e.g. Data/StashTabAffinityId.dat (a bare name like StashTabAffinityId.dat works)")] string? file = null,
        [Description("Without file: substring of the file names to list (e.g. 'Stash')")] string? filter = null,
        [Description("Only rows whose text contains this")] string? find = null,
        [Description("First row (default 0)")] int offset = 0,
        [Description("Rows to return (1-500, default 50)")] int limit = 50,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        if (file == null)
        {
            var (_, list) = await bridges.CallAsync(game, "data.files", new JObject { ["filter"] = filter ?? ".dat", ["limit"] = 500 }, ct);
            Check(list);
            return ToolResults.Json(list);
        }
        var p = new JObject { ["file"] = file, ["offset"] = offset, ["limit"] = limit };
        if (find != null) p["find"] = find;
        var (_, r) = await bridges.CallAsync(game, "data.read", p, ct);
        Check(r);
        if (r["error"] != null) return ToolResults.Json(r);
        var sb = new StringBuilder($"{r["file"]}: {r["count"]} rows x {r["recordLength"]} bytes" + (find != null ? $" (rows containing '{find}')" : "") + "\n");
        foreach (var row in (r["rows"] as JArray ?? []).OfType<JObject>())
        {
            var strings = string.Join(" | ", (row["strings"] as JArray ?? []).Select(s => s.ToString()));
            var refs = string.Join(" | ", (row["refs"] as JArray ?? []).Select(s => "-> " + s));
            sb.AppendLine($"  [{row["index"]}] {(strings.Length > 0 ? strings : row["ints"])}{(refs.Length > 0 ? "  " + refs : "")}");
        }
        if (r["truncated"] != null) sb.AppendLine($"({r["truncated"]})");
        sb.Append("Row index is usually the id the game stores. structuredContent has hex and int32 columns per row.");
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString() }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(r.ToString(Newtonsoft.Json.Formatting.None)),
        };
    }

    private static void Check(JToken? r)
    {
        if (r is not JObject o || (o["error"] == null && o["rows"] == null && o["files"] == null))
            throw new McpException("This HUD's bridge plugin has no data.* methods yet: update What's an AI Bridge and restart the HUD.");
    }
}
