using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Hosting;

/// <summary>
/// Which build of this server is running, and which one is deployed. deploy.ps1 builds a commit into
/// %LOCALAPPDATA%\ExileApiMcp\builds\&lt;version&gt;-&lt;sha&gt; (with a build.json naming it) and points current.json at it; a
/// supervised server (Supervisor/README.md) follows current.json. mode says how this server is run:
///   supervised   - under the supervisor, follows deployments;
///   local        - under the supervisor, pinned to its checkout's build (HEXILE_MCP_LOCAL=1, for MCP development);
///   unsupervised - started without the supervisor (an old launcher): it needs a session restart to follow deployments.
/// </summary>
public static class ServerBuild
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExileApiMcp");
    public static readonly string CurrentFile = Path.Combine(Root, "current.json");
    public static readonly string SupervisorsDir = Path.Combine(Root, "supervisors");

    public static string Mode { get; } = Environment.GetEnvironmentVariable("HEXILE_SUPERVISED") == "1"
        ? Environment.GetEnvironmentVariable("HEXILE_MCP_MODE") ?? "supervised" : "unsupervised";

    /// <summary>This build's commit (build.json next to the DLL; deploy.ps1 writes it), or null for a local build.</summary>
    public static string? Sha { get; } = ReadOwnSha();

    /// <summary>What deploy.ps1 last deployed: {version, sha, dir, deployedAt, by, reason, previous}; null when nothing is.</summary>
    public static JObject? Deployed()
    {
        try { return File.Exists(CurrentFile) ? JObject.Parse(File.ReadAllText(CurrentFile)) : null; }
        catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The session.hello "mcp" object: the bridge counts which sessions run the deployed build.</summary>
    public static JObject HelloInfo()
    {
        var d = Deployed();
        return new JObject
        {
            ["version"] = McpSetup.Version, ["sha"] = Short(Sha), ["mode"] = Mode,
            ["deployedVersion"] = d?["version"]?.ToString(), ["deployedSha"] = Short(d?["sha"]?.ToString()),
            ["workerPid"] = Environment.ProcessId,
        };
    }

    public static string? Short(string? sha) => sha is { Length: > 7 } ? sha[..7] : sha;

    private static string? ReadOwnSha()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "build.json");
            return File.Exists(p) ? JObject.Parse(File.ReadAllText(p))["sha"]?.ToString() : null;
        }
        catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException) { return null; }
    }
}
