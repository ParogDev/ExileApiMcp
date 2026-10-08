using System.ComponentModel;
using ExileApiMcp.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Gameplay recording and stateless playback. A recording is identified by its file name
/// (e.g. "rec_20261008_031200.jsonl" from recording_list); every playback call names it,
/// so nothing depends on a previously "loaded" recording.
/// </summary>
[McpServerToolType]
public static class RecordingTools
{
    private const string G = BridgeRegistry.GameParamDescription;
    private const string FileDesc = "Recording file name from recording_list, e.g. 'rec_20261008_031200.jsonl'";

    [McpServerTool(Name = "record_start", Title = "Start recording", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Start recording gameplay snapshots (player, area, entities) to a .jsonl file at a fixed interval.")]
    public static async Task<CallToolResult> RecordStart(BridgeRegistry bridges,
        [Description("Capture interval in milliseconds (50-2000, default 200)")] int intervalMs = 200,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, $"record:start:{intervalMs}", ct)).Result);

    [McpServerTool(Name = "record_stop", Title = "Stop recording", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stop the current recording; returns frames, duration, file name and size.")]
    public static async Task<CallToolResult> RecordStop(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, "record:stop", ct)).Result);

    [McpServerTool(Name = "record_status", Title = "Recording status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Whether a recording is in progress, its frame count, elapsed time and file.")]
    public static async Task<CallToolResult> RecordStatus(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, "record:status", ct)).Result);

    [McpServerTool(Name = "snapshot", Title = "Capture snapshot", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Capture one full snapshot (including deep entity scans) to a file and return its metadata. " +
                 "For interactive state use get_all instead.")]
    public static async Task<CallToolResult> Snapshot(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, "snapshot", ct)).Result);

    [McpServerTool(Name = "recording_list", Title = "List recordings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List saved recording/snapshot files with size and modification time.")]
    public static async Task<CallToolResult> RecordingList(BridgeRegistry bridges, [Description(G)] string? game = null,
        CancellationToken ct = default) =>
        ToolResults.Json((await bridges.QueryAsync(game, "recording:list", ct)).Result);

    [McpServerTool(Name = "recording_info", Title = "Recording info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Frame count of a recording.")]
    public static Task<CallToolResult> RecordingInfo(BridgeRegistry bridges, [Description(FileDesc)] string file,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        Call(bridges, game, "recording.info", new JObject { ["file"] = file }, ct);

    [McpServerTool(Name = "recording_frame", Title = "Recording frame", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read one frame (0-based) of a recording.")]
    public static Task<CallToolResult> RecordingFrame(BridgeRegistry bridges, [Description(FileDesc)] string file,
        [Description("Frame number, 0-based")] int frame,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        Call(bridges, game, "recording.frame", new JObject { ["file"] = file, ["frame"] = frame }, ct);

    [McpServerTool(Name = "recording_range", Title = "Recording frame range", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read frames from..to (inclusive, at most 51 frames) of a recording.")]
    public static Task<CallToolResult> RecordingRange(BridgeRegistry bridges, [Description(FileDesc)] string file,
        [Description("First frame, 0-based")] int from, [Description("Last frame, inclusive")] int to,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        Call(bridges, game, "recording.range", new JObject { ["file"] = file, ["from"] = from, ["to"] = to }, ct);

    [McpServerTool(Name = "recording_search", Title = "Search recording", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Case-insensitive substring search across a recording's frames; returns matching frame numbers.")]
    public static Task<CallToolResult> RecordingSearch(BridgeRegistry bridges, [Description(FileDesc)] string file,
        [Description("Text to find, e.g. a buff name or entity path")] string term,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        Call(bridges, game, "recording.search", new JObject { ["file"] = file, ["term"] = term }, ct);

    [McpServerTool(Name = "recording_summary", Title = "Summarize recording", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Summary of a recording: unique entity paths, unique buff names, frame count, time range.")]
    public static Task<CallToolResult> RecordingSummary(BridgeRegistry bridges, [Description(FileDesc)] string file,
        [Description(G)] string? game = null, CancellationToken ct = default) =>
        Call(bridges, game, "recording.summary", new JObject { ["file"] = file }, ct);

    private static async Task<CallToolResult> Call(BridgeRegistry bridges, string? game, string method, JObject p,
        CancellationToken ct) =>
        ToolResults.Json((await bridges.CallAsync(game, method, p, ct)).Result);
}
