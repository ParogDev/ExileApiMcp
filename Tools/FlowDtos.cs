using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contracts of guide_flow (bridge guide.flow / guide.flow_state, and the server's recipes). The bridge writes
// every state field, null when unset: those keep their explicit null on the wire ([JsonIgnore(Never)]).

/// <summary>
/// The guided flow's progress (guide_flow start / state / stop; start adds next). action=recipes returns only
/// {recipes}; action=expand returns the flow itself ({title, goal, steps:[{label, done, options}]}).
/// </summary>
public sealed class FlowStateResult
{
    public bool? Ok { get; set; }
    /// <summary>idle | running | done | stopped | timeout | failed.</summary>
    public string? Status { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Title { get; set; }
    public long? Rev { get; set; }
    /// <summary>1-based number of the current step, null when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Current { get; set; }
    public List<FlowStepState>? Steps { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? StartedAt { get; set; }
    /// <summary>What is left, first entry = what to do now.</summary>
    public List<string>? Plan { get; set; }
    /// <summary>The current step: why it is not done yet and which options are possible now (null without a current step).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public FlowDiagnostics? Diagnostics { get; set; }
    /// <summary>Problems found in the flow when it started, null when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public List<string>? Preflight { get; set; }
    /// <summary>start: what happens next.</summary>
    public string? Next { get; set; }
    /// <summary>action=recipes only.</summary>
    public List<FlowRecipeInfo>? Recipes { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class FlowStepState
{
    /// <summary>1-based.</summary>
    public int N { get; set; }
    public string Label { get; set; } = "";
    /// <summary>done | current | todo.</summary>
    public string State { get; set; } = "";
    /// <summary>The current step's option shown now, else null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Showing { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class FlowDiagnostics
{
    public string Step { get; set; } = "";
    /// <summary>The first failing link of the step's done condition.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? NotDoneBecause { get; set; }
    public List<FlowOptionState> Options { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class FlowOptionState
{
    public string Label { get; set; } = "";
    public bool Available { get; set; }
    /// <summary>Why the option is not possible now, null when it is.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Why { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>guide_flow action=recipes (a FlowStateResult with only recipes).</summary>
public sealed class FlowRecipeList
{
    public List<FlowRecipeInfo> Recipes { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class FlowRecipeInfo
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>Games the recipe works in (poe1 / poe2).</summary>
    public List<string>? Games { get; set; }
    /// <summary>Param name -> what to pass.</summary>
    public Dictionary<string, string>? Params { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
