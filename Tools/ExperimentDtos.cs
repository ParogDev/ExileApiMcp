using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of the guided-experiment tools, mirroring ui-src/src/memory/types.ts (the memory view's experiment
// runner parses them). Experiment records on disk are passed through as written (older records differ in detail), so
// experiment_summary's record stays open JSON. A field that is sometimes absent and sometimes null is a JsonElement
// skipped only when absent (WhenWritingDefault), so both stay as they were on the wire.

/// <summary>One ready-made experiment from Knowledge/experiments.json.</summary>
public sealed class ExperimentPreset
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public List<string>? Games { get; set; }
    public string? Question { get; set; }
    public string? Setup { get; set; }
    public List<ExperimentPresetStep>? Steps { get; set; }
    /// <summary>Watch specs: value:&lt;path&gt; | memory:&lt;path&gt;[:size] | collection:&lt;path&gt;[:Label1,Label2].</summary>
    public List<string>? Watch { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ExperimentPresetStep
{
    public string Label { get; set; } = "";
    /// <summary>What the user does in game.</summary>
    public string? Instruction { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>An experiment record on disk.</summary>
public sealed class ExperimentRecordInfo
{
    public string Name { get; set; } = "";
    public string Updated { get; set; } = "";
    public int Steps { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>experiment_presets (and experiment_summary without a name).</summary>
public sealed class ExperimentPresetsResult
{
    public List<ExperimentPreset> Presets { get; set; } = [];
    /// <summary>Records on disk, newest first.</summary>
    public List<ExperimentRecordInfo> Records { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One change between the baseline and the settled state.</summary>
public sealed class ExperimentChange
{
    /// <summary>The watch spec it belongs to.</summary>
    public string Watch { get; set; } = "";
    /// <summary>value | bytes | label | moved.</summary>
    public string Kind { get; set; } = "";
    /// <summary>"spec key" for values, "spec item X +off Field" for bytes.</summary>
    public string Key { get; set; } = "";
    /// <summary>Collection item ("Name#0") for label / bytes changes of a collection, else null; absent for moved.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Item { get; set; }
    public int? Off { get; set; }
    public int? Size { get; set; }
    /// <summary>HUD field name covering the bytes, "(unmapped)" when none.</summary>
    public string? Field { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    /// <summary>Flipped bits within the byte range (absent when more than 16).</summary>
    public List<int>? BitsFlipped { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Change keys seen in every repeat of a label (evidence) vs only some ("key (1/2)").</summary>
public sealed class ExperimentConsistency
{
    public int Repeats { get; set; }
    public List<string> Always { get; set; } = [];
    public List<string> Sometimes { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One watch spec at the end of a step: whether anything under it changed, its value now, or why it didn't read.</summary>
public sealed class ExperimentWatched
{
    public string Watch { get; set; } = "";
    public bool Changed { get; set; }
    /// <summary>value specs: up to 4 leaves as they read now ("k=v, ..."); collection specs: the item count.</summary>
    public string? Now { get; set; }
    /// <summary>The spec did not read at all (a broken path: the walker names the link), so it could never change.</summary>
    public string? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// await_change: changed true with the diff (step, repeatsOfThisLabel, changedAfterMs, changes, consistent), or changed
/// false after the timeout (transientChanges, note), after the user's Done with nothing changed (userMarkedDone, note
/// naming the watch specs), or after the user closed the card (cancelledByUser). watched: each spec's state at the end.
/// </summary>
public sealed class AwaitChangeResult
{
    public string Experiment { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Changed { get; set; }
    /// <summary>The user pressed Done on the card. With changed false: nothing watched changed, the watch spec is probably wrong.</summary>
    public bool? UserMarkedDone { get; set; }
    /// <summary>The user closed the card (its x cancels a waiting step): nothing was recorded.</summary>
    public bool? CancelledByUser { get; set; }
    /// <summary>Each watch spec at the end of the step: changed or not, its value now, or its read error.</summary>
    public List<ExperimentWatched>? Watched { get; set; }
    /// <summary>Steps in the record now.</summary>
    public int? Step { get; set; }
    public int? RepeatsOfThisLabel { get; set; }
    public long? ChangedAfterMs { get; set; }
    public List<ExperimentChange>? Changes { get; set; }
    public int? TransientChangesIgnored { get; set; }
    public ExperimentConsistency? Consistent { get; set; }
    /// <summary>changed false: brief changes that reverted.</summary>
    public int? TransientChanges { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>experiment_step_start: the step runs in the server; poll experiment_status.</summary>
public sealed class ExperimentStepStartedResult
{
    public bool Started { get; set; }
    public string Experiment { get; set; } = "";
    public string Label { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public int TimeoutMs { get; set; }
    public string? Next { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The current or last step of an experiment (the server's .inflight.json).</summary>
public sealed class ExperimentStepState
{
    public string Experiment { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>What the user was told (null when none).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Instruction { get; set; }
    /// <summary>Step number and total for the guide panel (null when none).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Step { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Steps { get; set; }
    public string? StartedAt { get; set; }
    public long? TimeoutMs { get; set; }
    /// <summary>starting | waiting | checking (the user pressed Done) | detected | captured | failed | interrupted | cancelled | error | stale.</summary>
    public string Status { get; set; } = "";
    public List<string>? Watch { get; set; }
    /// <summary>A blocking await_change (not experiment_step_start).</summary>
    public bool? Blocking { get; set; }
    public string? UpdatedAt { get; set; }
    /// <summary>While running: the server's own measure.</summary>
    public long? ElapsedMs { get; set; }
    public string? FinishedAt { get; set; }
    /// <summary>The await_change result once captured or failed.</summary>
    public AwaitChangeResult? Result { get; set; }
    public string? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>experiment_status.</summary>
public sealed class ExperimentStatusResult
{
    public string Experiment { get; set; } = "";
    public ExperimentStepState? Step { get; set; }
    public bool Running { get; set; }
    public int RecordedSteps { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>experiment_step_cancel.</summary>
public sealed class ExperimentStepCancelResult
{
    public string Experiment { get; set; } = "";
    public bool Cancelled { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ExperimentLabelSummary
{
    public string Label { get; set; } = "";
    public int Repeats { get; set; }
    public ExperimentConsistency Consistent { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// experiment_summary. With a name: experiment, labels and the record ({experiment, steps}, as on disk). Without one:
/// the presets and records listing of experiment_presets.
/// </summary>
public sealed class ExperimentSummaryResult
{
    public string? Experiment { get; set; }
    public List<ExperimentLabelSummary>? Labels { get; set; }
    /// <summary>The record as written on disk: {experiment, steps: [{label, instruction, at, game, changedAfterMs, watch, changes}]}.</summary>
    public JsonElement? Record { get; set; }
    public List<ExperimentPreset>? Presets { get; set; }
    public List<ExperimentRecordInfo>? Records { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
