using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of gameplay recording and playback (bridge record:* / snapshot / recording:list queries and
// recording.* methods). They mirror the bridge's recording DTOs; frames reuse the game-state types (GameStateDtos.cs).

/// <summary>
/// record_start / record_stop / record_status: status is recording_started | recording_stopped | recording | idle
/// (or similar); the other fields are present when they apply.
/// </summary>
public sealed class RecordingStatusResult
{
    public string Status { get; set; } = "";
    public bool? IsRecording { get; set; }
    public int? Frames { get; set; }
    public double? DurationMs { get; set; }
    /// <summary>The .jsonl file (relative to the HUD folder for start/stop).</summary>
    public string? File { get; set; }
    public int? IntervalMs { get; set; }
    public long? SizeBytes { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>snapshot: the file one full snapshot was written to.</summary>
public sealed class SnapshotResult
{
    public string File { get; set; } = "";
    public long SizeBytes { get; set; }
    public int Frame { get; set; }
    public string Timestamp { get; set; } = "";
    public int EntityCount { get; set; }
    public string Message { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RecordingListResult
{
    public List<RecordingFileInfo> Recordings { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RecordingFileInfo
{
    /// <summary>The name every playback tool takes as file.</summary>
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Modified { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>recording_info: the recording's frame count.</summary>
public sealed class RecordingInfoResult
{
    public string Status { get; set; } = "";
    public string File { get; set; } = "";
    public int Frames { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>recording_frame: one recorded frame (snapshot files hold deep entity scans).</summary>
public sealed class RecordingFrameResult
{
    public int Frame { get; set; }
    public string Timestamp { get; set; } = "";
    /// <summary>ms since the recording started.</summary>
    public double ElapsedMs { get; set; }
    public AreaInfo? Area { get; set; }
    public PlayerInfo? Player { get; set; }
    public List<EntityInfo>? Entities { get; set; }
    [JsonPropertyName("_end")] public bool? End { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>recording_range: the frames as recorded, each one JSON line (a RecordingFrameResult) as a string.</summary>
public sealed class RecordingRangeResult
{
    public List<string> Frames { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RecordingSearchResult
{
    public string Term { get; set; } = "";
    public int MatchCount { get; set; }
    /// <summary>Matching frame numbers (0-based).</summary>
    public List<int> Frames { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RecordingSummaryResult
{
    public string File { get; set; } = "";
    public int TotalFrames { get; set; }
    public string? FirstTimestamp { get; set; }
    public string? LastTimestamp { get; set; }
    public List<string> UniqueEntityPaths { get; set; } = [];
    public List<string> UniqueBuffNames { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
