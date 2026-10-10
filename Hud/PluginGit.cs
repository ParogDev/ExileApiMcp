using System.Diagnostics;
using Newtonsoft.Json;

namespace ExileApiMcp.Hud;

/// <summary>
/// Git facts about a plugin folder, for "what does the HUD run vs what did I edit": the HUD compiles each plugin from
/// &lt;HUD&gt;\Plugins\Source\&lt;folder&gt;, a junction into the scaffolding's MAIN checkout, never a worktree. Used by
/// hud_plugins (the commit each source folder is at) and deploy_plugin (moving a worktree's commit into the main checkout).
/// Read-only except <see cref="Run"/>, which the deploy tool uses for fetch / checkout.
/// </summary>
public static class PluginGit
{
    public sealed record GitResult(int Code, string Out, string Err)
    {
        public bool Ok => Code == 0;
        public string Line => Out.Trim();
    }

    /// <summary>One git command in <paramref name="dir"/>; never throws (code -1 when git can't start or times out).</summary>
    public static GitResult Run(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                // Its own stdin, closed at once: git status waits for as long as an inherited stdin pipe stays open, and
                // ours always is (the client's protocol pipe in stdio, the supervisor's control pipe in http).
                RedirectStandardInput = true,
                WorkingDirectory = dir,
            };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.quotepath=off");
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } return new(-1, "", $"git {string.Join(' ', args)} timed out"); }
            return new(p.ExitCode, stdout.Result, stderr.Result.Trim());
        }
        catch (Exception ex) { return new(-1, "", $"git could not run: {ex.Message}"); }
    }

    /// <summary>The real folder behind a junction or symlink (a HUD's Plugins\Source entry), else the path itself.</summary>
    public static string Resolve(string path)
    {
        try
        {
            var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            if (target != null) return Path.GetFullPath(target.FullName.Replace(@"\??\", ""));
        }
        catch { }
        return Path.GetFullPath(path);
    }

    /// <summary>A folder that is the top of its own git repository (a plugin submodule): it has a .git file or folder.</summary>
    public static bool IsRepo(string dir) => File.Exists(Path.Combine(dir, ".git")) || Directory.Exists(Path.Combine(dir, ".git"));

    public sealed class Head
    {
        public string Sha = "";
        public string? Branch;               // null: detached
        public List<string> Changed = new(); // tracked files with uncommitted changes (porcelain lines)
        public DateTimeOffset? CommittedAt;
        public string? Subject;
    }

    /// <summary>HEAD, branch, uncommitted tracked changes and the commit's time and subject; null with the reason when it isn't a repo.</summary>
    public static Head? ReadHead(string dir, out string? why)
    {
        why = null;
        if (!IsRepo(dir)) { why = $"{dir} is not a git checkout (no .git: submodule not initialised here?)"; return null; }
        var st = Run(dir, "status", "--porcelain=v2", "--branch", "--untracked-files=no");
        if (!st.Ok) { why = $"git status in {dir}: {st.Err}"; return null; }
        var h = new Head();
        foreach (var line in st.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# branch.oid ", StringComparison.Ordinal)) h.Sha = line[13..].Trim();
            else if (line.StartsWith("# branch.head ", StringComparison.Ordinal)) { var b = line[14..].Trim(); h.Branch = b == "(detached)" ? null : b; }
            else if (!line.StartsWith('#')) h.Changed.Add(PathOf(line));
        }
        var log = Run(dir, "log", "-1", "--format=%cI%x1f%s");
        if (log.Ok && log.Line.Split('\x1f') is [var at, var subject, ..])
        {
            if (DateTimeOffset.TryParse(at, out var t)) h.CommittedAt = t;
            h.Subject = subject.Length > 100 ? subject[..100] : subject;
        }
        return h;
    }

    /// <summary>The path of a porcelain v2 entry: "1 XY sub mH mI mW hH hI path", "2 ... X<score> path\torig", "u ... h1 h2 h3 path".</summary>
    private static string PathOf(string line)
    {
        var fields = line[0] switch { '1' => 9, '2' => 10, 'u' => 11, _ => 0 };
        if (fields == 0) return line.Trim();
        var parts = line.Split(' ', fields);
        return parts.Length == fields ? parts[^1].Split('\t')[0] : line.Trim();
    }

    /// <summary>The newest *.cs write time under a plugin folder (bin, obj and .git skipped): edits the HUD hasn't compiled yet.</summary>
    public static DateTimeOffset? NewestSource(string dir)
    {
        DateTime newest = DateTime.MinValue;
        var stack = new Stack<string>();
        stack.Push(dir);
        var seen = 0;
        while (stack.Count > 0 && seen < 5000)
        {
            var d = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*.cs")) { seen++; var t = File.GetLastWriteTimeUtc(f); if (t > newest) newest = t; }
                foreach (var s in Directory.EnumerateDirectories(d))
                {
                    var n = Path.GetFileName(s);
                    if (n is "bin" or "obj" or ".git" or "node_modules" or ".vs") continue;
                    stack.Push(s);
                }
            }
            catch { }
        }
        return newest == DateTime.MinValue ? null : new DateTimeOffset(newest, TimeSpan.Zero);
    }

    // ── Deploy state (deploy_plugin) ─────────────────────────────────

    /// <summary>What deploy_plugin put into a main-checkout submodule, kept in its git dir so restore can undo it.</summary>
    public sealed class DeployState
    {
        [JsonProperty("plugin")] public string Plugin = "";
        [JsonProperty("beforeBranch")] public string? BeforeBranch;
        [JsonProperty("beforeSha")] public string BeforeSha = "";
        [JsonProperty("deployedSha")] public string DeployedSha = "";
        [JsonProperty("deployedBranch")] public string? DeployedBranch;
        [JsonProperty("subject")] public string? Subject;
        [JsonProperty("by")] public string By = "";
        [JsonProperty("from")] public string From = "";
        [JsonProperty("at")] public DateTimeOffset At;
    }

    public static string? StatePath(string repoDir)
    {
        var g = Run(repoDir, "rev-parse", "--absolute-git-dir");
        return g.Ok ? Path.Combine(g.Line, "hexile-deploy.json") : null;
    }

    public static DeployState? ReadState(string repoDir)
    {
        try
        {
            var p = StatePath(repoDir);
            return p != null && File.Exists(p) ? JsonConvert.DeserializeObject<DeployState>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    public static void WriteState(string repoDir, DeployState? s)
    {
        var p = StatePath(repoDir) ?? throw new IOException($"{repoDir}: no git dir for the deploy state");
        if (s == null) { if (File.Exists(p)) File.Delete(p); return; }
        File.WriteAllText(p, JsonConvert.SerializeObject(s, Formatting.Indented));
    }
}
