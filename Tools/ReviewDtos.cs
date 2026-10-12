using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileApiMcp.Tools;

// Typed contract of mark_reviewed (Tools/ReviewTools.cs). Unknown fields pass through in Extra.

/// <summary>mark_reviewed: which commit is marked, and the mark file tools\pre-pr-review.ps1 reads.</summary>
public sealed class ReviewMarkResult
{
    public string Path { get; set; } = "";
    /// <summary>The commit marked as reviewed (the checkout's HEAD).</summary>
    public string Head { get; set; } = "";
    /// <summary>&lt;git dir&gt;\hexile-reviewed: the file the pre-PR hook compares with HEAD.</summary>
    public string Marker { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
