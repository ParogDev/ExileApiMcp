using System.Diagnostics;
using System.Text.RegularExpressions;
using ExileApiMcp.Tools;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Dispatch;

/// <summary>
/// An F8 handler's fix that its session couldn't merge (a permission or auto-mode check refused the PR, the merge or the
/// review mark): the plugin commit sits on a branch, pushed or not, with or without a PR, in the handler's kept worktree.
/// The dispatcher asks the player on the in-game card (Merge it | Close it | Leave it) and acts on the click here, outside
/// Claude Code: the click is the player's go-ahead, and nothing here goes through the session's permission checks.
/// Merge runs the same scripts a session would (tools/merge-on-green.sh, tools/bump-pointers.sh) from the worktree.
/// </summary>
public static class UnmergedFix
{
    public const string Merge = "Merge it", Close = "Close it", Leave = "Leave it";
    public static readonly string[] Options = [Merge, Close, Leave];
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(20);

    /// <summary>The plugin fix in a kept handler worktree, or null when its plugin checkout holds nothing that isn't on
    /// origin/main (then something else kept the worktree: uncommitted changes, which a person must look at).</summary>
    public static FeedbackUnmerged? Find(long seq, string worktree)
    {
        var sub = Path.Combine(worktree, MainSync.SubmodulePath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(Path.Combine(sub, ".git")) && !File.Exists(Path.Combine(sub, ".git"))) return null;
        FeedbackDispatcher.Git(sub, "fetch -q origin", 120_000);
        if (FeedbackDispatcher.Git(sub, "log --oneline origin/main..HEAD").Output.Trim().Length == 0) return null;
        if (ContentOnMain(sub)) return null;
        var sha = FeedbackDispatcher.Git(sub, "rev-parse HEAD").Output.Trim();
        var branch = FeedbackDispatcher.Git(sub, "rev-parse --abbrev-ref HEAD").Output.Trim();
        if (branch is "" or "HEAD") branch = $"f8-{seq}-fix";
        var repo = RepoName(FeedbackDispatcher.Git(sub, "remote get-url origin").Output.Trim());
        if (repo == null) return null;
        var u = new FeedbackUnmerged
        {
            Repo = repo, Branch = branch, Sha = sha, Status = "asking",
            Pushed = FeedbackDispatcher.Git(sub, "branch -r --contains HEAD").Output.Trim().Length > 0,
            Subject = FeedbackDispatcher.Git(sub, "log -1 --format=%s HEAD").Output.Trim(),
        };
        var (code, output) = Run("gh", ["pr", "list", "--repo", repo, "--head", branch, "--state", "open", "--json", "number,title"], sub, TimeSpan.FromSeconds(60));
        if (code == 0 && TryJsonArray(output) is [JObject first, ..])
        {
            u.Pr = first["number"]?.Value<int>();
            u.Subject = first["title"]?.ToString() ?? u.Subject;
        }
        return u;
    }

    /// <summary>The commits' changes already on origin/main (a squash merge, or a later PR that folded them in).</summary>
    internal static bool ContentOnMain(string dir)
    {
        if (FeedbackDispatcher.Git(dir, "merge-base origin/main HEAD") is not { Code: 0 } mb) return false;
        var files = FeedbackDispatcher.Git(dir, $"diff --name-only {mb.Output.Trim()} HEAD").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return files.Length == 0 || FeedbackDispatcher.Git(dir, "diff --quiet origin/main HEAD -- " + string.Join(' ', files.Select(f => $"\"{f}\""))).Code == 0;
    }

    /// <summary>The question on the card (up to 120 characters) and its detail line (up to 160).</summary>
    public static (string Question, string Detail) AskText(long seq, FeedbackUnmerged u)
    {
        var what = u.Pr is { } pr ? $"PR #{pr}" : u.Pushed ? $"branch {u.Branch}" : "a commit";
        var question = FeedbackKit.Clip($"F8 #{seq}'s fix isn't merged ({what}). Merge it?", 110);
        // The subject clipped on its own: the rest of the line (about 75 characters) always fits in the card's 160.
        var detail = $"\"{FeedbackKit.Clip(u.Subject, 70)}\" Its session wasn't allowed to merge. Merge: on green, then into the HUD.";
        return (question, detail);
    }

    /// <summary>Pushes the commit when it is on no remote, opens the PR when there is none, merges it on green, bumps the
    /// scaffolding pointer and merges that on green. Returns what happened, Failed with the broken link.</summary>
    public static (bool Failed, string Text) DoMerge(long seq, string worktree, FeedbackUnmerged u)
    {
        var sub = Path.Combine(worktree, MainSync.SubmodulePath.Replace('/', Path.DirectorySeparatorChar));
        if (!u.Pushed)
        {
            var push = FeedbackDispatcher.Git(sub, $"push -q origin HEAD:refs/heads/{u.Branch}", 120_000);
            if (push.Code != 0) return (true, $"pushing {u.Branch} failed: {Line(push.Output)}");
            u.Pushed = true;
        }
        if (u.Pr == null)
        {
            var (code, output) = Run("gh", ["pr", "create", "--repo", u.Repo!, "--head", u.Branch!, "--base", "main", "--title", u.Subject ?? $"F8 #{seq} fix",
                "--body", $"The fix for F8 #{seq}. Its handler session was refused the PR; the player chose Merge on the in-game card.\n\n🤖 Generated with [Claude Code](https://claude.com/claude-code)"], sub, TimeSpan.FromSeconds(90));
            if (code != 0 || PrNumber(output) is not { } n) return (true, $"opening the PR failed: {Line(output)}");
            u.Pr = n;
        }
        var bash = GitBash();
        if (bash == null) return (true, "Git Bash was not found (needed for tools/merge-on-green.sh)");
        var merge = Run(bash, ["tools/merge-on-green.sh", u.Pr.ToString()!, "--repo", u.Repo!, "--timeout", "900"], worktree, ScriptTimeout);
        if (merge.Code != 0) return (true, $"PR #{u.Pr} was not merged ({MergeExit(merge.Code)}): {Line(merge.Output)}");
        var bump = Run(bash, ["tools/bump-pointers.sh", "--pr", MainSync.SubmodulePath], worktree, TimeSpan.FromMinutes(5));
        if (bump.Code != 0) return (true, $"PR #{u.Pr} merged, but the pointer bump failed: {Line(bump.Output)}");
        // Merged: the local topic branch (its remote one deleted by the squash merge) would count as unpushed work and
        // keep the worktree. bump-pointers left the plugin on origin/main, so the branch isn't checked out.
        FeedbackDispatcher.Git(sub, $"branch -D \"{u.Branch}\"");
        if (PrNumber(bump.Output) is not { } bumpPr) return (false, $"PR #{u.Pr} merged (the scaffolding already pointed at it)");
        var bumpMerge = Run(bash, ["tools/merge-on-green.sh", bumpPr.ToString(), "--timeout", "900"], worktree, ScriptTimeout);
        if (bumpMerge.Code != 0) return (true, $"PR #{u.Pr} merged, but the pointer bump #{bumpPr} was not ({MergeExit(bumpMerge.Code)}): {Line(bumpMerge.Output)}");
        return (false, $"PR #{u.Pr} merged, pointer bump #{bumpPr} merged");
    }

    /// <summary>Closes the PR and deletes its branch (or only deletes the pushed branch when there is no PR).</summary>
    public static (bool Failed, string Text) DoClose(long seq, string worktree, FeedbackUnmerged u)
    {
        var sub = Path.Combine(worktree, MainSync.SubmodulePath.Replace('/', Path.DirectorySeparatorChar));
        if (u.Pr is { } pr)
        {
            var (code, output) = Run("gh", ["pr", "close", pr.ToString(), "--repo", u.Repo!, "--delete-branch", "--comment", $"Closed from the in-game card (F8 #{seq})."], sub, TimeSpan.FromSeconds(90));
            return code == 0 ? (false, $"PR #{pr} closed, branch deleted") : (true, $"closing PR #{pr} failed: {Line(output)}");
        }
        if (!u.Pushed) return (false, "the commit was never pushed: dropped with the worktree");
        var del = FeedbackDispatcher.Git(sub, $"push -q origin --delete {u.Branch}", 120_000);
        return del.Code == 0 ? (false, $"branch {u.Branch} deleted") : (true, $"deleting branch {u.Branch} failed: {Line(del.Output)}");
    }

    // ── Pure helpers (unit-tested) ───────────────────────────────────

    /// <summary>owner/name from a GitHub remote URL (https or ssh, with or without .git).</summary>
    public static string? RepoName(string url)
        => Regex.Match(url, @"github\.com[:/]([^/\s]+)/([^/\s]+?)(?:\.git)?/?$") is { Success: true } m ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : null;

    /// <summary>The PR number from the last GitHub pull URL in a command's output.</summary>
    public static int? PrNumber(string output)
        => Regex.Matches(output, @"github\.com/[^/\s]+/[^/\s]+/pull/(\d+)") is { Count: > 0 } ms ? int.Parse(ms[^1].Groups[1].Value) : null;

    private static string MergeExit(int code) => code switch
    {
        1 => "a check failed", 3 => "no checks reported", 4 => "the checks took too long", 5 => "the PR is not open",
        6 => "the merge failed", 7 => "gh could not read the checks", -1 => "timed out", _ => $"exit {code}",
    };

    private static string Line(string s) => FeedbackKit.Clip(string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(2)), 160) ?? "";

    private static JArray? TryJsonArray(string s) { try { return JArray.Parse(s); } catch { return null; } }

    /// <summary>Git for Windows' bash (never the WSL bash.exe in System32: its /mnt/c paths break every submodule).</summary>
    private static string? GitBash()
    {
        var exec = FeedbackDispatcher.Git(Environment.CurrentDirectory, "--exec-path").Output.Trim();
        // <git>\mingw64\libexec\git-core -> <git>\bin\bash.exe
        if (exec.Length > 0 && Path.GetFullPath(Path.Combine(exec, "..", "..", "..", "bin", "bash.exe")) is var b && File.Exists(b)) return b;
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        return File.Exists(pf) ? pf : null;
    }

    /// <summary>Runs a command with its own closed stdin (see FeedbackDispatcher.Git) and returns its exit code and output.</summary>
    private static (int Code, string Output) Run(string exe, IEnumerable<string> args, string dir, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout)) { try { p.Kill(true); } catch { } return (-1, $"{Path.GetFileName(exe)} timed out after {timeout.TotalMinutes:0} min"); }
            return (p.ExitCode, o.Result + e.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-2, $"{exe} could not start: {ex.Message}"); }
    }
}
