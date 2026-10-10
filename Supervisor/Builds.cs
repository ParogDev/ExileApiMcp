using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExileApiMcp.Supervisor;

/// <summary>A server build a worker can run: its folder (holding ExileApiMcp.dll) and what it is.</summary>
public sealed record BuildRef(string Dir, string? Version, string? Sha, string Source)
{
    public string Dll => Path.Combine(Dir, "ExileApiMcp.dll");
    public string Describe => Source == "deployed" ? $"{Version} ({Sha?[..Math.Min(7, Sha.Length)]})" : $"local build {Dir}";
    public bool SameAs(BuildRef? o) => o != null && string.Equals(Path.GetFullPath(Dir), Path.GetFullPath(o.Dir), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Where builds come from. deploy.ps1 builds a commit into %LOCALAPPDATA%\ExileApiMcp\builds\&lt;version&gt;-&lt;sha&gt; and points
/// current.json at it: that is the deployed build every supervised server follows. A server pinned to its checkout
/// (HEXILE_MCP_LOCAL=1, for MCP development) or started before the first deploy runs the local build run.cmd made.
/// </summary>
public static class Builds
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExileApiMcp");
    public static readonly string CurrentFile = Path.Combine(Root, "current.json");
    public static readonly string SupervisorsDir = Path.Combine(Root, "supervisors");

    /// <summary>The deployed build, or null when nothing is deployed or its folder is gone (the reason goes to stderr once per change).</summary>
    public static BuildRef? Deployed(Action<string>? why = null)
    {
        try
        {
            if (!File.Exists(CurrentFile)) return null;
            var o = JsonNode.Parse(File.ReadAllText(CurrentFile)) as JsonObject;
            var dir = o?["dir"]?.GetValue<string>();
            if (string.IsNullOrEmpty(dir)) { why?.Invoke($"{CurrentFile} has no dir"); return null; }
            var b = new BuildRef(dir, o!["version"]?.GetValue<string>(), o["sha"]?.GetValue<string>(), "deployed");
            if (!File.Exists(b.Dll)) { why?.Invoke($"{CurrentFile} points at {dir}, which has no ExileApiMcp.dll"); return null; }
            return b;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Mid-write by deploy.ps1 (it replaces the file atomically, but readers on other volumes may still race): next poll.
            why?.Invoke($"{CurrentFile} unreadable: {ex.Message}");
            return null;
        }
    }

    public static BuildRef Local(string dir)
    {
        string? version = null, sha = null;
        try
        {
            var info = Path.Combine(dir, "build.json");
            if (File.Exists(info) && JsonNode.Parse(File.ReadAllText(info)) is JsonObject o) { version = o["version"]?.GetValue<string>(); sha = o["sha"]?.GetValue<string>(); }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new BuildRef(dir, version, sha, "local");
    }
}
