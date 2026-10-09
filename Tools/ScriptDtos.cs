using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contract of run_csharp (bridge script.run / script.result). value is whatever the script returned.

/// <summary>A script's outcome: status done (ok, value, log, timings) or timeout; failures are error results of the same shape.</summary>
public sealed class ScriptResult
{
    public string? Id { get; set; }
    /// <summary>done | timeout | unknown | rejected.</summary>
    public string Status { get; set; } = "";
    /// <summary>main | worker.</summary>
    public string? Thread { get; set; }
    public bool? Ok { get; set; }
    /// <summary>compile_error | runtime_error | scripts_disabled | failed ...</summary>
    public string? Error { get; set; }
    public string? Message { get; set; }
    /// <summary>Compile errors, with line numbers in the script.</summary>
    public List<ScriptDiagnostic>? Diagnostics { get; set; }
    public string? ReturnType { get; set; }
    /// <summary>The last expression's value: any JSON (serialized with depth and size limits).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Value { get; set; }
    /// <summary>Lines printed with Log(...).</summary>
    public List<string>? Log { get; set; }
    public string? StackTrace { get; set; }
    public long? CompileMs { get; set; }
    public long? RunMs { get; set; }
    /// <summary>The compiled script came from the cache.</summary>
    public bool? Cached { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ScriptDiagnostic
{
    public int Line { get; set; }
    public int Col { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
