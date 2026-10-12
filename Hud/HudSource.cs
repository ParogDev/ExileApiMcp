using System.Text;
using Newtonsoft.Json;

namespace ExileApiMcp.Hud;

/// <summary>
/// The tree the HUDs compile plugins from: the scaffolding checkout their &lt;HUD&gt;\Plugins\Source junctions point into.
/// That used to be the dev main checkout; tools\hud-live.ps1 cutover moves it to a dedicated clone (hud-live), which only
/// hud-live.ps1 sync, deploy_plugin and the F8 dispatcher's sync change. Everything here is resolved from the junction
/// targets on disk, so it is right before and after a cutover, and per folder when only one HUD was cut over.
/// Changes to the tree are serialized by <see cref="Lock"/> (the same file hud-live.ps1 takes).
/// </summary>
public static class HudSource
{
    /// <summary>Marker hud-live.ps1 init writes into the clone's git dir.</summary>
    public const string HudLiveMarker = "hexile-hud-live.json";
    public const string LockFile = "hexile-hud-source.lock";

    /// <summary>One Plugins\Source junction: the folder it shows the HUD, the folder it points at, and the checkout holding it.</summary>
    public sealed record Link(string Game, string Folder, string LinkPath, string Target, string? Root);

    /// <summary>The HUD folders, from BridgeRegistry's layout: each HUD is the parent of its bridge folder.</summary>
    public static List<(string Game, string Root)> HudRoots()
    {
        var list = new List<(string, string)>();
        foreach (var (game, dir) in Bridge.BridgeRegistry.Layout())
            if (Path.GetDirectoryName(Path.GetFullPath(dir.TrimEnd('\\', '/'))) is { } root && Directory.Exists(root)) list.Add((game, root));
        return list;
    }

    /// <summary>Every junction (or symlink) under each HUD's Plugins\Source.</summary>
    public static List<Link> Links(IEnumerable<(string Game, string Root)>? huds = null)
    {
        var links = new List<Link>();
        foreach (var (game, root) in huds ?? HudRoots())
        {
            var src = Path.Combine(root, "Plugins", "Source");
            if (!Directory.Exists(src)) continue;
            DirectoryInfo[] dirs;
            try { dirs = new DirectoryInfo(src).GetDirectories(); } catch { continue; }
            foreach (var d in dirs)
            {
                if (d.LinkTarget == null) continue;
                var target = PluginGit.Resolve(d.FullName).TrimEnd('\\');
                links.Add(new Link(game, d.Name, d.FullName, target, CheckoutOf(target)));
            }
        }
        return links;
    }

    /// <summary>The checkout holding a plugin folder &lt;root&gt;\Plugins\&lt;folder&gt; (it has a .git), else null.</summary>
    public static string? CheckoutOf(string pluginDir)
    {
        var parent = Path.GetDirectoryName(pluginDir.TrimEnd('\\'));
        if (parent == null || !string.Equals(Path.GetFileName(parent), "Plugins", StringComparison.OrdinalIgnoreCase)) return null;
        var root = Path.GetDirectoryName(parent);
        return root != null && (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"))) ? root : null;
    }

    /// <summary>The checkout(s) the HUDs compile from, most-used first (one, unless a cutover is half done).</summary>
    public static List<string> Roots(IEnumerable<(string Game, string Root)>? huds = null) =>
        Links(huds).Where(l => l.Root != null).GroupBy(l => l.Root!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();

    /// <summary>The checkout a HUD compiles <paramref name="folder"/> from (the first HUD whose junction names it), or null
    /// when no HUD has a junction for it.</summary>
    public static string? RootFor(string folder, IEnumerable<(string Game, string Root)>? huds = null) =>
        Links(huds).FirstOrDefault(l => l.Root != null && string.Equals(l.Folder, folder, StringComparison.OrdinalIgnoreCase))?.Root;

    /// <summary>hud-live (marked by hud-live.ps1 init), the scaffolding's main checkout (a .git folder), a worktree, or other.</summary>
    public static string Kind(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (File.Exists(Path.Combine(dotGit, HudLiveMarker))) return "hud-live";
        if (root.Contains(@"\.claude\worktrees\", StringComparison.OrdinalIgnoreCase)) return "worktree";
        return Directory.Exists(dotGit) ? "main-checkout" : "other";
    }

    /// <summary>The main scaffolding checkout a hud-live clone was made from (hud-live.ps1 init records it in its marker),
    /// else null.</summary>
    public static string? MainCheckoutOf(string root)
    {
        try
        {
            var marker = Path.Combine(root, ".git", HudLiveMarker);
            if (!File.Exists(marker)) return null;
            var main = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(marker))["mainCheckout"]?.ToString();
            return main is { Length: > 0 } && Directory.Exists(Path.Combine(main, ".git")) ? main : null;
        }
        catch { return null; }
    }

    /// <summary>"hud-live (path)" / "the main checkout (path)": how results name the tree.</summary>
    public static string Describe(string root) => Kind(root) switch
    {
        "hud-live" => $"hud-live ({root})",
        "main-checkout" => $"the main checkout ({root})",
        var k => $"{k} ({root})",
    };

    /// <summary>
    /// Exclusive lock on changes to a HUD source tree: &lt;root&gt;\.git\hexile-hud-source.lock, opened without write
    /// sharing and holding who has it. Waits up to <paramref name="wait"/>, then throws an IOException naming the holder
    /// (or naming the access problem at once). The OS releases it when the holder dies.
    /// </summary>
    public static async Task<IDisposable> LockAsync(string root, string what, TimeSpan wait, CancellationToken ct = default)
    {
        var gitDir = PluginGit.Run(root, "rev-parse", "--absolute-git-dir");
        if (!gitDir.Ok) throw new IOException($"{root}: no git dir for the HUD source lock ({gitDir.Err})");
        var path = Path.GetFullPath(Path.Combine(gitDir.Line, LockFile));
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                // Read sharing lets a refused taker name the holder. .NET on Unix (CI) only locks exclusively with None.
                var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None);
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new LockHolder
                {
                    Who = Hosting.SessionIdentity.Label, What = what, Pid = Environment.ProcessId, At = DateTimeOffset.UtcNow,
                }));
                fs.SetLength(0); fs.Write(bytes); fs.Flush();
                return fs;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException($"the HUD source lock {path} can't be opened ({ex.Message}): made read-only, or by an elevated process? Delete it (nothing holds it then) and try again.", ex);
            }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(250, ct); }
            catch (IOException)
            {
                throw new IOException($"{Describe(root)} is locked by another change for over {(int)wait.TotalSeconds} s: {Holder(path) ?? "holder unknown"}");
            }
        }
    }

    private static string? Holder(string path)
    {
        try
        {
            using var r = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new StreamReader(r).ReadToEnd();
        }
        catch { return null; }
    }

    private sealed class LockHolder
    {
        [JsonProperty("who")] public string Who = "";
        [JsonProperty("what")] public string What = "";
        [JsonProperty("pid")] public int Pid;
        [JsonProperty("at")] public DateTimeOffset At;
    }
}
