using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Dispatch;

/// <summary>
/// The pure half of the F8 dispatcher (no IO beyond what the arguments name), unit-tested in Tests/: finding the Claude
/// Code CLI among the desktop app's versions, the handler's brief, its command line and environment, and the card text
/// made from its final message.
/// </summary>
public static class FeedbackKit
{
    public const string PressKind = "feedback", ReplyKind = "feedback.reply", TraceKind = "route";

    /// <summary>Where the desktop app keeps Claude Code: %APPDATA%\Claude\claude-code\&lt;version&gt;\&lt;hash&gt;\claude.exe (as an
    /// agent inside the MSIX package sees it), and the same folder under the package's LocalCache\Roaming from outside it.</summary>
    public static IEnumerable<string> CliRoots(string appData, string localAppData)
    {
        yield return Path.Combine(appData, "Claude", "claude-code");
        var packages = Path.Combine(localAppData, "Packages");
        if (!Directory.Exists(packages)) yield break;
        foreach (var pkg in Directory.EnumerateDirectories(packages, "Claude_*"))
            yield return Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude-code");
    }

    /// <summary>The newest claude.exe under the roots (by version folder name), or null with the folders it looked in.</summary>
    public static (string? Exe, string Looked) FindCli(IEnumerable<string> roots)
    {
        var looked = new List<string>();
        (Version v, string exe)? best = null;
        foreach (var root in roots)
        {
            looked.Add(root);
            if (!Directory.Exists(root)) continue;
            foreach (var verDir in Directory.EnumerateDirectories(root))
            {
                if (!Version.TryParse(Path.GetFileName(verDir), out var v)) continue;
                var exe = Directory.EnumerateFiles(verDir, "claude.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe != null && (best == null || v > best.Value.v)) best = (v, exe);
            }
        }
        return (best?.exe, string.Join("; ", looked));
    }

    /// <summary>The real %LOCALAPPDATA% (the user profile's AppData\Local): inside the MSIX package the variable and the known
    /// folder can be redirected, the profile directory is not.</summary>
    public static string RealLocalAppData(string userProfile) => Path.Combine(userProfile, "AppData", "Local");

    /// <summary>The same claude.exe as a user's own shell sees it. %APPDATA%\Claude\claude-code\&lt;ver&gt;\&lt;hash&gt;\claude.exe
    /// exists only for processes inside the Claude desktop app's MSIX package; outside it the file is at
    /// &lt;LocalAppData&gt;\Packages\Claude_&lt;publisher&gt;\LocalCache\Roaming\Claude\claude-code\&lt;ver&gt;\&lt;hash&gt;\claude.exe. Returns the
    /// package copy that holds the same version and hash, the exe itself when it already is outside (a package path or
    /// HEXILE_CLAUDE_EXE elsewhere), or null when no package folder holds it.</summary>
    public static string? OutsidePath(string exe, string realLocalAppData)
    {
        var parts = exe.Split('\\', '/');
        var cc = Array.FindLastIndex(parts, p => p.Equals("claude-code", StringComparison.OrdinalIgnoreCase));
        if (cc < 1 || !parts[cc - 1].Equals("Claude", StringComparison.OrdinalIgnoreCase)) return exe;   // not the desktop app's layout
        var packages = Path.Combine(realLocalAppData, "Packages");
        if (exe.StartsWith(packages + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return exe;
        var rel = Path.Combine(parts[(cc + 1)..]);
        try
        {
            if (!Directory.Exists(packages)) return null;
            // Several Claude_* folders (a stale one left by an old install): the most recently written copy of that exe.
            return Directory.EnumerateDirectories(packages, "Claude_*")
                .Select(pkg => Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude-code", rel))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The sign-in command for the user's own PowerShell: the outside path when found, else the path as the
    /// dispatcher sees it (the fallback). Written with $env:LOCALAPPDATA when it lies under the real LocalAppData and the
    /// variable names that same folder (envLocalAppData: the variable's value; a redirected one gets the literal path).
    /// PowerShell needs &amp; before a quoted path; $ and ` in the literal part are escaped for its double quotes.</summary>
    public static string LoginCommand(string exe, string userProfile, string? envLocalAppData)
    {
        var local = RealLocalAppData(userProfile);
        var path = OutsidePath(exe, local) ?? exe;
        var prefix = "";
        if (path.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && string.Equals(envLocalAppData?.TrimEnd('\\'), local, StringComparison.OrdinalIgnoreCase))
            (prefix, path) = ("$env:LOCALAPPDATA", path[local.Length..]);
        return $"& \"{prefix}{path.Replace("`", "``").Replace("$", "`$").Replace("\"", "`\"")}\" auth login";
    }

    /// <summary>Variables a handler must not inherit: the starting session's Claude Code identity and auth plumbing, and
    /// the MCP server's own settings (the handler reaches the shared server over HTTP with its own label).</summary>
    public static bool Scrub(string name) =>
        name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("HEXILE_MCP_", StringComparison.OrdinalIgnoreCase)
        || name is "HEXILE_AGENT" or "HEXILE_SESSION_PID" or "HEXILE_PUBLIC_PORT" or "HEXILE_SUPERVISED" or "MCP_HTTP_PORT" or "MCP_HTTP_TOKEN";

    public static string Label(long seq) => $"route-f8-{seq}";

    /// <summary>cmd.exe arguments that run the CLI with the prompt on stdin and its JSON result and stderr in files, so the
    /// handler keeps running (and its output lands) when the server that started it is swapped for a new build.</summary>
    public static string CmdArguments(string exe, long seq, string dir, string mcpConfig, IEnumerable<string> addDirs, IEnumerable<string>? slim = null)
    {
        var sb = new StringBuilder();
        sb.Append(Q(exe)).Append(" -p --output-format json --permission-mode auto");
        sb.Append(" --worktree ").Append(Label(seq));
        sb.Append(" --name ").Append(Q($"Route F8 #{seq}"));
        sb.Append(" --mcp-config ").Append(Q(mcpConfig)).Append(" --strict-mcp-config --allowedTools mcp__exileapi");
        foreach (var d in addDirs) sb.Append(" --add-dir ").Append(Q(d));
        foreach (var a in slim ?? []) sb.Append(' ').Append(a.StartsWith("--") ? a : Q(a));
        sb.Append(" < ").Append(Q(Path.Combine(dir, "prompt.txt")));
        sb.Append(" > ").Append(Q(Path.Combine(dir, "output.json")));
        sb.Append(" 2> ").Append(Q(Path.Combine(dir, "stderr.log")));
        return $"/d /s /c \"{sb}\"";
    }

    private static string Q(string s) => "\"" + s.Replace("\"", "") + "\"";

    // ── Visible handlers: a background session with Remote Control ────

    /// <summary>The handler's display name, as the user sees it in claude.ai/code, the Claude app and `claude agents`:
    /// "F8 #&lt;seq&gt;: &lt;first words of the note&gt; (&lt;area&gt;)", the verdict when there is no note.</summary>
    public static string DisplayName(JObject press)
    {
        var d = press["data"] as JObject ?? new JObject();
        var seq = press["seq"]?.Value<long>() ?? 0;
        var what = d["Note"] is { Type: JTokenType.String } n && n.ToString().Trim().Length > 0 ? n.ToString() : d["Event"]?.ToString() ?? "feedback";
        var all = Regex.Replace(what.Trim(), @"\s+", " ");
        var head = string.Join(' ', all.Split(' ').Take(7));
        if (head.Length > 48) head = head[..48].TrimEnd();
        if (head.Length < all.Length) head += "...";
        var area = d["AreaName"] is { Type: JTokenType.String } a && a.ToString().Length > 0 ? $" ({a})" : "";
        return Regex.Replace($"F8 #{seq}: {head}{area}", "[\"\r\n]", "'");
    }

    /// <summary>The CLI arguments for a visible handler: a background session (`--bg`, managed by the CLI's background
    /// service, outside this server's process tree) with Remote Control on, named, in its own worktree, the prompt as the
    /// first message. Same tools and MCP config as the headless run. The prompt follows a single-value option: after a
    /// variadic one (--add-dir, --mcp-config, --allowedTools) the parser would take it as one more value.</summary>
    public static IReadOnlyList<string> BackgroundArguments(long seq, string name, string mcpConfig, IEnumerable<string> addDirs, string prompt,
        IEnumerable<string>? slim = null)
    {
        var a = new List<string> { "--bg", "--remote-control", name, "--worktree", Label(seq),
            "--mcp-config", mcpConfig, "--strict-mcp-config", "--allowedTools", "mcp__exileapi" };
        foreach (var d in addDirs) { a.Add("--add-dir"); a.Add(d); }
        a.AddRange(slim ?? []);
        a.AddRange(["--permission-mode", "auto", "--name", name, prompt]);
        return a;
    }

    /// <summary>The tools a handler gets: what a route fix and its PR flow need (files, shell for git/gh/scripts, the
    /// code-review skill), plus ToolSearch, which keeps the ~125 exileapi MCP tools deferred (names only). Without it the
    /// CLI loads every MCP schema up front (measured: 78k tokens instead of 16k).</summary>
    public const string HandlerTools = "Read,Edit,Write,Bash,Grep,Glob,ToolSearch,Skill";

    /// <summary>The settings a handler starts with (the CLI's settings.json keys): no CLAUDE.md (the workspace one is ~10k
    /// tokens; the standing brief replaces it) and no auto-memory.</summary>
    public sealed class HandlerSettings
    {
        [Newtonsoft.Json.JsonProperty("claudeMdExcludes")] public string[] ClaudeMdExcludes { get; set; } = ["**/CLAUDE.md"];
        [Newtonsoft.Json.JsonProperty("autoMemoryEnabled")] public bool AutoMemoryEnabled { get; set; }
    }

    public static string SlimSettingsJson() => Newtonsoft.Json.JsonConvert.SerializeObject(new HandlerSettings());

    /// <summary>
    /// The arguments that start a handler slim (measured on CLI 2.1.295, first turn: 48k tokens by default, 18k with
    /// these): only the project's settings (its hooks and permissions: the main-checkout guard and the review gate stay;
    /// no user plugins), the settings above, the tools above, and the standing brief (Whats A Route's
    /// docs/feedback-agent.md) appended to the system prompt: as text (briefText; the background session, whose argument
    /// list is passed as is) or as a file (the headless run, whose cmd.exe line can't carry the text). Variadic --tools
    /// comes first, so nothing after it is taken as a tool name. No brief when its file is missing (the prompt names it).
    /// </summary>
    public static IReadOnlyList<string> SlimArguments(string settingsFile, string standingBrief, string? briefText, bool asFile)
    {
        var a = new List<string> { "--tools", HandlerTools, "--setting-sources", "project", "--settings", settingsFile };
        if (briefText != null) a.AddRange(asFile ? ["--append-system-prompt-file", standingBrief] : ["--append-system-prompt", briefText]);
        return a;
    }

    /// <summary>The pid in a refused `git worktree remove` of a worktree the CLI locked ("cannot remove a locked working
    /// tree, lock reason: claude session route-f8-138 (pid 25444)"), or null when it is another lock.</summary>
    public static int? StaleLockPid(string gitOutput) =>
        Regex.Match(gitOutput, @"locked working tree, lock reason: claude session [^\r\n]*\(pid (\d+)\)") is { Success: true } m
        && int.TryParse(m.Groups[1].Value, out var pid) ? pid : null;

    /// <summary>The short id `claude --bg` prints ("backgrounded · 98b400ca · name"), or null.</summary>
    public static string? ParseBackgrounded(string stdout) =>
        Regex.Match(Regex.Replace(stdout, @"\x1b\[[0-9;?]*[A-Za-z]", ""), @"backgrounded\s*\S\s*([0-9a-f]{6,})") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>One background session from `claude agents --json --all`: its session id, its state (working | blocked |
    /// done ...), and whether its process is alive (a stopped or exited one has no pid). Null when it is not listed.</summary>
    /// Throws FormatException when the output is not that list (the CLI failed: not the same as "not listed").</summary>
    public static (string? SessionId, string? State, bool Alive)? ParseAgent(string json, string id)
    {
        int from = json.IndexOf('['), to = json.LastIndexOf(']');
        if (from < 0 || to < from) throw new FormatException($"`claude agents --json` printed no list: {Clip(json, 160)}");
        try
        {
            using var doc = JsonDocument.Parse(json[from..(to + 1)]);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("id", out var i) || i.GetString() != id) continue;
                string? Str(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                return (Str("sessionId"), Str("state"), e.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new FormatException($"`claude agents --json` is not the expected list: {ex.Message}");
        }
        return null;
    }

    /// <summary>What a session's transcript (~/.claude/projects/&lt;dir&gt;/&lt;session-id&gt;.jsonl) says: the claude.ai/code
    /// URL Remote Control gave it (a system bridge_status line) and its last answer (the text of the last assistant
    /// message that has text), and whether its turn has ended (a system turn_duration line after the last user or assistant
    /// line: the final answer is written by then). Feeds on lines as they come: pass the previous result to continue.</summary>
    public static TranscriptState ParseTranscript(IEnumerable<string> lines, TranscriptState? previous = null)
    {
        var s = previous ?? new TranscriptState();
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var type = r.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var subtype = r.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                if (type == "system" && subtype == "bridge_status" && r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) s.Url = u.GetString();
                else if (type == "system" && subtype == "turn_duration") s.TurnEnded = true;
                else if (type == "user") s.TurnEnded = false;
                else if (type == "assistant")
                {
                    s.TurnEnded = false;
                    if (r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                    {
                        var text = string.Join("\n", c.EnumerateArray()
                            .Where(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                                        && b.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                            .Select(b => b.GetProperty("text").GetString()));
                        if (text.Trim().Length > 0) s.LastText = text.Trim();
                    }
                }
            }
            catch (JsonException) { }
        }
        return s;
    }

    public sealed class TranscriptState
    {
        public string? Url { get; set; }
        public string? LastText { get; set; }
        public bool TurnEnded { get; set; }
        /// <summary>Bytes of the file read so far (the caller's: where to continue).</summary>
        public long Offset { get; set; }
        public string? Path { get; set; }
    }

    /// <summary>The card line saying where the handler runs: the session's name in Claude, or that it runs headless and why.</summary>
    public static string WhereText(long seq, string mode, string? name, string? fallbackWhy) => mode == "remote-control"
        ? $"Claude is on #{seq}: watch it as \"{name}\" in Claude (Remote Control: claude.ai/code or the Claude app)."
        : $"Claude is on #{seq} (headless run: {Clip(fallbackWhy, 160)}).";

    public static string Prompt(long seq, string briefPath) =>
        $"You handle one F8 press (#{seq}) from the player, who is playing Path of Exile 2 with the Whats A Route plugin. " +
        $"Read {briefPath} now: it has the press, the route state and the recent trace. Follow the standing brief (in your " +
        "system prompt). Keep your context small and end when the press is handled: nobody follows up in this session. Your " +
        "final message is shown to the player above the route " +
        "card: one or two short plain sentences that say what changed and what to do now.";

    /// <summary>The handler's brief: the press, what the route was doing, the trace since the previous press, the last
    /// answers, and where the standing brief and the reply path are.</summary>
    public static string Brief(JObject press, IReadOnlyList<JObject> trace, IReadOnlyList<JObject> lastReplies, string standingBrief,
        string liveDir, long? previousPress)
    {
        var d = press["data"] as JObject ?? new JObject();
        var seq = press["seq"]?.Value<long>() ?? 0;
        var sb = new StringBuilder();
        sb.AppendLine($"# F8 press #{seq} ({Time(press["at"])} UTC)");
        sb.AppendLine();
        sb.AppendLine($"- Verdict: **{d["Event"]}**" + (d["Note"] is { Type: JTokenType.String } n ? $" - the player wrote: \"{n}\"" : " (no note)"));
        sb.AppendLine($"- Area: {d["AreaName"]} ({d["Area"]}), step index {d["StepIndex"]}: {d["Label"]} (target {d["Target"]})");
        if (d["PlayerGrid"] is JArray g) sb.AppendLine($"- Player grid cell: [{string.Join(", ", g)}]");
        sb.AppendLine($"- Bridge event #{seq}; feedback.jsonl line #{d["Seq"]}.");
        sb.AppendLine();
        sb.AppendLine("## First");
        sb.AppendLine($"1. The standing brief (the user's rules, the fix flow, how to reply) is in your system prompt; read `{standingBrief}` only if it is not.");
        sb.AppendLine($"2. Show the player you are on it when it takes more than a minute: MCP `feedback_reply seq={seq} status=working text=\"...\"`.");
        sb.AppendLine($"3. Live route fixes: `{Path.Combine(liveDir, "route.live.json")}` (reloaded within 0.5 s). Another handler or agent may edit it too: read it right before you write, change only your entries.");
        sb.AppendLine("4. End with one or two plain sentences for the player (shown above the route card); they say what to do now.");
        sb.AppendLine();
        if (lastReplies.Count > 0)
        {
            sb.AppendLine("## What the last handlers answered");
            foreach (var r in lastReplies)
                sb.AppendLine($"- #{r["data"]?["ref"]} ({Time(r["at"])}): {Clip(r["data"]?["text"]?.ToString(), 300)}");
            sb.AppendLine();
        }
        sb.AppendLine(previousPress is { } p ? $"## Trace since the previous press (#{p})" : "## Recent trace");
        if (trace.Count == 0) sb.AppendLine("(nothing)");
        foreach (var t in trace) sb.AppendLine("- " + TraceLine(t));
        sb.AppendLine();
        sb.AppendLine("## The route state at the press (WhatsARoute.GetState; MCP `route_state` reads it live)");
        sb.AppendLine("```json");
        var state = d["State"]?.ToString(Newtonsoft.Json.Formatting.Indented) ?? "null";
        sb.AppendLine(state.Length > 12000 ? state[..12000] + "\n... (clipped; route_state has it all)" : state);
        sb.AppendLine("```");
        return sb.ToString();
    }

    public static string TraceLine(JObject e)
    {
        var d = e["data"] as JObject ?? new JObject();
        var sb = new StringBuilder($"{Time(e["at"])} {d["Event"]}");
        if (d["AreaName"] is { Type: JTokenType.String } a) sb.Append($" in {a} ({d["Area"]})");
        if (d["Label"] is { Type: JTokenType.String } l && l.ToString().Length > 0) sb.Append($" step '{l}'");
        if (d["Target"] is { Type: JTokenType.String } t && t.ToString().Length > 0) sb.Append($" target {t}");
        if (d["Note"] is { Type: JTokenType.String } n) sb.Append($" note: {Clip(n.ToString(), 200)}");
        if (d["Quests"] is JArray q && q.Count > 0) sb.Append($" quests: {string.Join(" | ", q)}");
        return sb.ToString();
    }

    private static string Time(JToken? at) => At(at) is { } t ? t.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "?";

    /// <summary>An event's "at" in UTC. Bridge replies are parsed with Newtonsoft's default date handling, so it is usually a
    /// Date token already (whose ToString() is culture-formatted without an offset): never re-parse that text.</summary>
    public static DateTimeOffset? At(JToken? at)
    {
        if (at?.Type == JTokenType.Date)
        {
            var d = at.Value<DateTime>();
            return new DateTimeOffset(d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime());
        }
        return at?.Type == JTokenType.String && DateTimeOffset.TryParse(at.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
    }

    public static string Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = Regex.Replace(s.Trim(), @"\s+", " ");
        if (s.Length <= max) return s;
        var cut = s[..max];
        var end = cut.LastIndexOfAny(['.', '!', '?']);
        return end > max / 2 ? cut[..(end + 1)] : cut.TrimEnd() + "...";
    }

    /// <summary>What `claude -p --output-format json` wrote: the final message, whether it failed, the session id.</summary>
    public static (string? Result, bool IsError, string? SessionId, string? Why) ParseOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, true, null, "the handler wrote no result (output.json is empty)");
        try
        {
            // The result object is the last JSON line (earlier lines can be warnings).
            var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{')) ?? text.Trim();
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var isError = r.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            return (Str("result"), isError, Str("session_id"), isError ? $"the session ended with an error ({Str("terminal_reason") ?? Str("subtype")})" : null);
        }
        catch (JsonException ex) { return (null, true, null, $"output.json is not the CLI's JSON result: {ex.Message}"); }
    }

    /// <summary>
    /// Why a bridge reply to a pubsub.* method is not an answer, or null when it is (an object with ok: true). A bridge build
    /// before pub/sub answers unknown methods with a bare {game, query, timestamp}: no ok, no error.
    /// </summary>
    public static string? Refusal(JToken? reply, string method) => reply switch
    {
        JObject o when o["ok"]?.Type == JTokenType.Boolean && o["ok"]!.Value<bool>() => null,
        JObject o when o["error"] != null && o["ok"] == null => $"{method} refused: {o["error"]}: {o["message"]}",
        JObject o when o["ok"] != null => null,   // ok: false is an answer (claimed, not_yours...): the caller reads it
        _ => $"this bridge build has no {method} (it answered {Clip(reply?.ToString(Newtonsoft.Json.Formatting.None), 120)}): a build before pub/sub. Update Whats An AI Bridge and restart the HUD",
    };
    /// <summary>The card line for a failure: what broke and what fixes it.</summary>
    public static string FailText(long seq, string why, string? fix) => Clip($"Claude could not take #{seq}: {why}." + (fix != null ? $" {fix}" : ""), 400);
}
