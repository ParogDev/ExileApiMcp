using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the performance tools (hud_health_report, show_hud_performance, perf_watch and the resource
// exile://perf/{game}/report). They mirror the JSON these tools already returned (ui-src/src/perf/types.ts reads it);
// every type keeps unknown fields in Extra, so trace fields the bridge adds later pass through.

/// <summary>Summary statistics of one per-frame measurement (ms).</summary>
public sealed class TraceStats
{
    public int N { get; set; }
    public double? Avg { get; set; }
    public double? P5 { get; set; }
    public double? P50 { get; set; }
    public double? P95 { get; set; }
    public double? Max { get; set; }
    public double? Sd { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Per-frame arrays, aligned by index (pipeline_trace series=true).</summary>
public sealed class TraceSeries
{
    [JsonPropertyName("tMs")] public List<double>? TMs { get; set; }
    /// <summary>Interval to the next frame; null for the last frame.</summary>
    public List<double?>? IntervalMs { get; set; }
    public List<double?>? WorkMs { get; set; }
    public List<double>? PluginsMs { get; set; }
    /// <summary>GC pause that landed between this frame's start and the next.</summary>
    public List<double>? GcPauseMs { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SharedArrayPoolInfo
{
    public int? Partitions { get; set; }
    public int? MaxArraysPerPartition { get; set; }
    public int? ArraysPerSize { get; set; }
    public string? EnvMaxArraysPerPartition { get; set; }
    /// <summary>The runtime internal that moved, when the limits can't be read.</summary>
    public string? Broken { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class TraceGc
{
    public int Gen0 { get; set; }
    public int Gen1 { get; set; }
    public int Gen2 { get; set; }
    public double PauseMsTotal { get; set; }
    public double AllocMBPerSecond { get; set; }
    public double FetchedMBPerSecond { get; set; }
    public double? FetchedPagesPerFrame { get; set; }
    public SharedArrayPoolInfo? SharedArrayPool { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Was the game the foreground window during a measurement? In the background the overlay is hidden and the game may cap its fps.</summary>
public sealed class TraceForeground
{
    public double? Share { get; set; }
    public bool? AtStart { get; set; }
    public bool? AtEnd { get; set; }
    public int? Samples { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A pipeline trace (bridge pipeline.trace_result). Only the fields tools reason about are typed.</summary>
public sealed class PipelineTraceResult
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public int? DurationMs { get; set; }
    public int? Frames { get; set; }
    public double? HudFps { get; set; }
    public TraceStats? FrameIntervalMs { get; set; }
    public TraceStats? UpdateMs { get; set; }
    public TraceStats? PluginsMs { get; set; }
    public TraceStats? CoreMs { get; set; }
    public TraceStats? DrawMs { get; set; }
    public Dictionary<string, TraceStats>? PluginTickMs { get; set; }
    public Dictionary<string, TraceStats>? PluginRenderMs { get; set; }
    public TraceGc? Gc { get; set; }
    public TraceSeries? Series { get; set; }
    /// <summary>How much of the measurement had the game in front (the bridge samples it every 100 ms).</summary>
    public TraceForeground? Foreground { get; set; }
    /// <summary>Set when the trace couldn't run (instrumentation_disabled, busy, harmony_unavailable).</summary>
    public string? Error { get; set; }
    public string? Message { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PerfPlugin
{
    public string Name { get; set; } = "";
    public double TickMs { get; set; }
    public double RenderMs { get; set; }
    public double AllocKBPerFrame { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PerfLint
{
    public string Plugin { get; set; } = "";
    public string Method { get; set; } = "";
    public string Call { get; set; } = "";
    public int Count { get; set; }
    public int CostNs { get; set; }
    public string Advice { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A suggested next step: call tool with args.</summary>
public sealed class PerfAction
{
    public string Label { get; set; } = "";
    public string Tool { get; set; } = "";
    public JsonElement Args { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>hud_health_report / show_hud_performance: the HUD's health in one object.</summary>
public sealed class HealthReport
{
    public DesktopInfo? Desktop { get; set; }
    public PipelineTraceResult? Trace { get; set; }
    public List<string>? Findings { get; set; }
    public List<PerfPlugin>? Plugins { get; set; }
    public List<PerfLint>? Lint { get; set; }
    public List<PerfAction>? Actions { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>perf_watch and exile://perf/{game}/report: the latest report and its place in the sequence.</summary>
public sealed class PerfSnapshot
{
    public string Game { get; set; } = "";
    /// <summary>Increases with each new report; pass it as since to wait for the next one.</summary>
    public long Seq { get; set; }
    public DateTimeOffset? At { get; set; }
    /// <summary>True when this is a report newer than since; false when the wait timed out (the latest is returned).</summary>
    public bool Fresh { get; set; }
    /// <summary>Seconds between reports while someone watches.</summary>
    public double IntervalSec { get; set; }
    public HealthReport? Report { get; set; }
    public string? Text { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
