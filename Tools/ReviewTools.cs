using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExileApiMcp.Tools;

/// <summary>
/// mark_reviewed: the review mark the scaffolding's pre-PR hook (tools\pre-pr-review.ps1) looks for, written by this server
/// for sessions that can't write it themselves. Claude Code's worktree isolation for background sessions (the F8 handlers)
/// refuses writes into the shared .git folder, where a worktree's (and its submodules') git dir lives, and refuses
/// `powershell -File tools/mark-reviewed.ps1` too. Same file as that script: &lt;git dir&gt;\hexile-reviewed, holding HEAD.
/// </summary>
[McpServerToolType]
public static class ReviewTools
{
    public const string MarkFile = "hexile-reviewed";

    [McpServerTool(Name = "mark_reviewed", Title = "Mark a checkout's HEAD as reviewed", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ReviewMarkResult))]
    [Description("After /code-review ran on a checkout's changes and its findings are fixed and committed: marks that exact HEAD as " +
                 "reviewed, so the pre-PR hook lets `gh pr create` through (a new commit needs a new review). The same mark as " +
                 "tools\\mark-reviewed.ps1, for sessions that can't run it or write into .git (F8 handlers). The path is absolute: " +
                 "the server doesn't know your working directory.")]
    public static CallToolResult MarkReviewed(
        [Description("Absolute path of the reviewed checkout, e.g. <your worktree>\\Plugins\\Whats A Route (a submodule) or the worktree itself")] string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new McpException($"path '{path}' is relative: pass the absolute path of the checkout (this server's working directory is not yours).");
        if (!Directory.Exists(path)) throw new McpException($"path '{path}' does not exist.");
        var gitDir = Git(path, "rev-parse --absolute-git-dir");
        var head = Git(path, "rev-parse HEAD");
        if (gitDir == null || head == null) throw new McpException($"'{path}' is not a git checkout (git rev-parse failed there).");
        var marker = Path.Combine(gitDir.Replace('/', Path.DirectorySeparatorChar), MarkFile);
        File.WriteAllText(marker, head + "\r\n", System.Text.Encoding.ASCII);
        var result = new ReviewMarkResult { Path = path, Head = head, Marker = marker };
        return Dto.Result(result, $"Marked {head[..8]} as reviewed ({path}).");
    }

    private static string? Git(string dir, string args) =>
        Dispatch.FeedbackDispatcher.Git(dir, args, 15000) is { Code: 0, Output: var o } && o.Trim().Split('\n')[0].Trim() is { Length: > 0 } s ? s : null;   // stdout's line (stderr follows it)
}
