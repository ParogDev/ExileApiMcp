using System.ComponentModel;
using System.Text;
using System.Text.Json;
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

    [McpServerTool(Name = "find_in_game_data", Title = "Which data table column holds these ids?", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Scan every loaded data table (Data/*.dat) for a set of values found in memory and rank the columns that hold " +
                 "them: the answer to 'what are these ids?'. Pass several values from the same field across a population (e.g. " +
                 "the +0 of every Map stash page): a column holding nearly all of them is the source, and its rows name them. " +
                 "Unpack tagged or flag-packed values first (e.g. q >> 5). Takes a few seconds (runs off the HUD's main thread).")]
    public static async Task<CallToolResult> FindInGameData(BridgeRegistry bridges,
        [Description("Values to look for: numbers or \"0x...\" strings (negative int32s match their 32-bit pattern)")] JsonElement values,
        [Description("Bytes per value: 1, 2, 4 or 8 (default: smallest that holds the largest value)")] int size = 0,
        [Description("Only tables whose name contains this, e.g. 'Map'")] string? filter = null,
        [Description("Columns must hold at least this many distinct values (default min(3, count))")] int? minHits = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var list = new JArray();
        if (values.ValueKind == JsonValueKind.Array) foreach (var v in values.EnumerateArray()) list.Add(v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString());
        else list.Add(values.ValueKind == JsonValueKind.Number ? values.GetRawText() : values.GetString());
        var p = new JObject { ["values"] = list, ["size"] = size };
        if (filter != null) p["filter"] = filter;
        if (minHits != null) p["minHits"] = minHits;
        var (_, start) = await bridges.CallAsync(game, "data.find_value", p, ct);
        if (start is not JObject s || s["id"] == null)
        {
            if (start is JObject e && e["error"] != null) return ToolResults.Json(e);
            throw new McpException("This HUD's bridge plugin has no data.find_value yet: update What's an AI Bridge and reload it.");
        }
        JToken r = s;
        var until = DateTime.UtcNow.AddSeconds(90);
        while ((string?)r["status"] == "running" && DateTime.UtcNow < until)
        {
            await Task.Delay(400, ct);
            r = (await bridges.CallAsync(game, "data.find_result", new JObject { ["id"] = s["id"] }, ct)).Result ?? r;
        }
        if ((string?)r["status"] != "done") return ToolResults.Json(r);
        var sb = new StringBuilder($"{r["values"]} values ({r["size"]} bytes) across {r["tables"]} tables in {r["ms"]} ms:\n");
        var cols = r["columns"] as JArray ?? [];
        if (cols.Count == 0) sb.AppendLine($"  no column holds {r["minHits"]}+ of them. Try another size, unpack the values, or lower minHits.");
        foreach (var c in cols.OfType<JObject>())
        {
            sb.AppendLine($"  {c["file"]} +{c["offset"]}: {c["found"]}/{r["values"]} values, {c["rowsMatched"]} of {c["rowCount"]} rows");
            foreach (var mt in (c["matches"] as JArray ?? []).Take(cols.IndexOf(c) == 0 ? 30 : 3))
                sb.AppendLine($"      {mt["value"]} = row {mt["row"]}{(string.IsNullOrEmpty((string?)mt["label"]) ? "" : $" \"{mt["label"]}\"")}");
        }
        if ((r["missing"] as JArray)?.Count > 0 && cols.Count > 0) sb.AppendLine($"  not in the top column: {string.Join(", ", r["missing"]!)}");
        sb.Append(r["note"]);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString() }],
            StructuredContent = JsonSerializer.Deserialize<JsonElement>(r.ToString(Newtonsoft.Json.Formatting.None)),
        };
    }

    private static void Check(JToken? r)
    {
        if (r is not JObject o || (o["error"] == null && o["rows"] == null && o["files"] == null))
            throw new McpException("This HUD's bridge plugin has no data.* methods yet: update What's an AI Bridge and restart the HUD.");
    }
}
