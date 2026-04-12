using System.ComponentModel;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

[McpServerToolType]
public sealed class RecordingTools
{
    private readonly BridgeClient _client;

    public RecordingTools(BridgeClient client)
    {
        _client = client;
    }

    [McpServerTool(Name = "record_start"), Description("Start recording gameplay snapshots at a configurable interval. Captures player, area, and entity state each frame.")]
    public async Task<string> RecordStart(
        [Description("Capture interval in milliseconds (default 200, range 50-2000)")] int intervalMs = 200)
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param($"record:start:{intervalMs}"));
        return result.ToString();
    }

    [McpServerTool(Name = "record_stop"), Description("Stop the current recording and get stats (frames, duration, file size)")]
    public async Task<string> RecordStop()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("record:stop"));
        return result.ToString();
    }

    [McpServerTool(Name = "record_status"), Description("Get current recording status: is recording, frame count, elapsed time")]
    public async Task<string> RecordStatus()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("record:status"));
        return result.ToString();
    }

    [McpServerTool(Name = "snapshot"), Description("Capture a full gameplay snapshot to file (includes deep entity scans). Returns file metadata. Use get_all for interactive game state queries.")]
    public async Task<string> Snapshot()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("snapshot"));
        return result.ToString();
    }

    [McpServerTool(Name = "recording_list"), Description("List all saved recording files with size and modification date")]
    public async Task<string> RecordingList()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("recording:list"));
        return result.ToString();
    }

    [McpServerTool(Name = "recording_load"), Description("Load a recording file for playback and analysis")]
    public async Task<string> RecordingLoad(
        [Description("Recording filename (e.g. 'rec_20260410_135500.jsonl')")] string filename)
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param($"recording:load:{filename}"));
        return result.ToString();
    }

    [McpServerTool(Name = "recording_frame"), Description("Read a specific frame from a loaded recording")]
    public async Task<string> RecordingFrame(
        [Description("Frame number (0-indexed)")] int frame)
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param($"recording:frame:{frame}"));
        return result.ToString();
    }

    [McpServerTool(Name = "recording_search"), Description("Search loaded recording frames for a substring (case-insensitive). Returns matching frame numbers.")]
    public async Task<string> RecordingSearch(
        [Description("Search term to find in frame data")] string term)
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param($"recording:search:{term}"));
        return result.ToString();
    }

    [McpServerTool(Name = "recording_summary"), Description("Analyze a loaded recording: unique entity paths, unique buff names, frame count, time range")]
    public async Task<string> RecordingSummary()
    {
        await _client.EnsureConnectedAsync();
        var result = await _client.SendRequestAsync("query", Param("recording:summary"));
        return result.ToString();
    }

    private static JObject Param(string queryType) => new() { ["type"] = queryType };
}
