using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExileApiMcp.Tools;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Dispatch;

/// <summary>
/// Every F8 press as a session (feedback_sessions, exile://feedback/sessions): read from what the dispatcher leaves on
/// disk (handlers\dispatcher.json; handlers\&lt;seq&gt;\state.json, output.json, brief.md, stderr.log), so it works with no
/// HUD and with no dispatcher running, and merged with the bridge's press and reply events when a HUD answers (the
/// note, area and time straight from the press; what was said on the card; presses still queued, which have no folder
/// yet). The pure parts (the brief's header, the CLI's result JSON, the transcript path, the dispatcher's liveness)
/// are unit-tested in Tests/. Fields a newer dispatcher adds to state.json (a display name, a Remote Control URL, a
/// mode) are picked up by name and passed through in Extra.
/// </summary>
public static class FeedbackSessions
{
    /// <summary>The dispatcher rewrites dispatcher.json every second; older than this and no dispatcher runs.</summary>
    public static readonly TimeSpan AliveWithin = TimeSpan.FromSeconds(15);
    /// <summary>A press older than this that nobody handled is not "queued" any more (the dispatcher's own limit).</summary>
    public static readonly TimeSpan QueuedMaxAge = TimeSpan.FromHours(2);

    // ── The dispatcher ───────────────────────────────────────────────

    public static FeedbackDispatcherSummary Dispatcher(string statusFile, DateTimeOffset now)
    {
        if (!File.Exists(statusFile))
            return new FeedbackDispatcherSummary { Status = "never-ran", Alive = false, Why = $"no dispatcher has run on this machine yet ({statusFile} is missing)", Fix = NotRunningFix };
        FeedbackDispatcherState? s;
        try { s = JsonSerializer.Deserialize<FeedbackDispatcherState>(File.ReadAllText(statusFile), Dto.Options); }
        catch (Exception ex) { return new FeedbackDispatcherSummary { Status = "not-running", Alive = false, Why = $"{statusFile} is unreadable: {ex.Message}", Fix = NotRunningFix }; }
        return s == null ? new FeedbackDispatcherSummary { Status = "not-running", Alive = false, Why = $"{statusFile} is empty", Fix = NotRunningFix } : Summarize(s, now);
    }

    public const string NotRunningFix = "The shared MCP HTTP server hosts the dispatcher: any tools\\mcp-call.ps1 call (or opening the control center) starts it.";

    /// <summary>The dispatcher's own state as the view shows it: alive when written within 15 s, else not-running.</summary>
    public static FeedbackDispatcherSummary Summarize(FeedbackDispatcherState s, DateTimeOffset now)
    {
        var age = now - s.UpdatedAt;
        var alive = age <= AliveWithin && s.Status != "stopped";
        var d = new FeedbackDispatcherSummary
        {
            Status = alive ? s.Status : "not-running", Alive = alive,
            Why = alive ? s.Why : $"the dispatcher's last update was {Ago(age)} (it said \"{s.Status}\"{(s.Why != null ? $": {s.Why}" : "")}): the shared server is not running",
            Fix = alive ? (s.LoggedIn == false && s.LoginCommand != null ? $"Sign in once, in PowerShell: {s.LoginCommand}" : null) : NotRunningFix,
            UpdatedAgoSec = Math.Round(Math.Max(0, age.TotalSeconds), 1), UpdatedAt = s.UpdatedAt,
            Version = s.Version, Pid = s.Pid, Host = s.Host, ClaudeExe = s.ClaudeExe, LoggedIn = s.LoggedIn, LoginCommand = s.LoginCommand, Repo = s.Repo,
            Waiting = s.Waiting.OrderBy(x => x).ToList(),
            Running = s.Handlers.FirstOrDefault(h => h.Status is "running" or "starting")?.Seq,
            Extra = s.Extra,
        };
        return d;
    }

    private static string Ago(TimeSpan t) => t.TotalSeconds < 90 ? $"{t.TotalSeconds:0} s ago" : t.TotalMinutes < 90 ? $"{t.TotalMinutes:0} min ago" : $"{t.TotalHours:0.#} h ago";

    // ── The handler folders ──────────────────────────────────────────

    /// <summary>Every handler folder under handlersDir as a session, newest first. withFiles: brief.md and stderr.log too.</summary>
    public static List<FeedbackSession> FromFolders(string handlersDir, string userProfile, DateTimeOffset now, bool withFiles, long? only = null)
    {
        var list = new List<FeedbackSession>();
        if (!Directory.Exists(handlersDir)) return list;
        foreach (var dir in Directory.EnumerateDirectories(handlersDir))
        {
            if (!long.TryParse(Path.GetFileName(dir), out var seq) || (only != null && seq != only)) continue;
            list.Add(FromFolder(dir, seq, userProfile, now, withFiles));
        }
        return list.OrderByDescending(s => s.Seq).ToList();
    }

    public static FeedbackSession FromFolder(string dir, long seq, string userProfile, DateTimeOffset now, bool withFiles)
    {
        var s = new FeedbackSession { Seq = seq, Dir = dir, Status = "starting" };
        var stateFile = Path.Combine(dir, "state.json");
        FeedbackHandlerState? st = null;
        if (File.Exists(stateFile))
        {
            try { st = JsonSerializer.Deserialize<FeedbackHandlerState>(File.ReadAllText(stateFile), Dto.Options); }
            catch (Exception ex) { s.Error = $"state.json is unreadable: {ex.Message}"; s.Status = "failed"; }
        }
        else s.Error = "no state.json yet (the dispatcher is writing the folder, or the folder is stale)";
        if (st != null) ApplyState(s, st);
        ApplyBrief(s, Read(Path.Combine(dir, "brief.md")), withFiles);
        ApplyOutput(s, Read(Path.Combine(dir, "output.json")));
        if (withFiles) { var err = Read(Path.Combine(dir, "stderr.log")); s.Stderr = string.IsNullOrWhiteSpace(err) ? null : err; }
        if (s.SessionId != null && s.Worktree != null)
        {
            s.TranscriptPath = TranscriptPath(userProfile, s.Worktree, s.SessionId);
            s.TranscriptExists = File.Exists(s.TranscriptPath);
        }
        if (s.Worktree != null) s.WorktreeExists = Directory.Exists(s.Worktree);
        s.DurationSec = Duration(s.StartedAt, s.EndedAt, s.Status, now);
        return s;
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }   // being written: the next read has it
    }

    /// <summary>state.json onto the session; the typed fields, then the ones a newer dispatcher adds (name, url, mode) by
    /// their likely names, the rest passed through in Extra.</summary>
    public static void ApplyState(FeedbackSession s, FeedbackHandlerState st)
    {
        s.Status = st.Status is "starting" or "running" or "done" or "failed" ? st.Status : st.Status.Length > 0 ? st.Status : "starting";
        s.Pid = st.Pid; s.StartedAt = st.StartedAt; s.EndedAt = st.EndedAt; s.Worktree = st.Worktree; s.Dir ??= st.Dir;
        s.Result = st.Result; s.Error = st.Error; s.SessionId = st.SessionId; s.Replied = st.Replied;
        if (st.Extra is { Count: > 0 })
        {
            s.Name = PickString(st.Extra, "name", "displayName", "title", "sessionName");
            s.Url = PickString(st.Extra, "url", "sessionUrl", "remoteUrl", "link");
            s.Mode = PickString(st.Extra, "mode", "sessionMode");
            s.Extra = st.Extra.ToDictionary(kv => kv.Key, kv => kv.Value);
        }
    }

    public static string? PickString(Dictionary<string, JsonElement> extra, params string[] keys)
    {
        foreach (var k in keys)
            foreach (var kv in extra)
                if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase) && kv.Value.ValueKind == JsonValueKind.String && kv.Value.GetString() is { Length: > 0 } v)
                    return v;
        return null;
    }

    // ── brief.md ─────────────────────────────────────────────────────

    private static readonly Regex HeadRx = new(@"^# F8 press #(\d+) \((\d\d):(\d\d):(\d\d) UTC\)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex VerdictRx = new(@"^- Verdict: \*\*([^*]+)\*\*(?: - the player wrote: ""(.*)""| \(no note\))?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex AreaRx = new(@"^- Area: (.*?) \(([^()\s]+)\), step index (-?\d+): (.*?) \(target (.*)\)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>The press from the brief's header (FeedbackKit.Brief writes it): verdict, note, area, step label and
    /// target, and the press time (a time of day: dated from the handler's start). Fields the bridge merge fills later
    /// win over these. The brief text itself only when asked.</summary>
    public static void ApplyBrief(FeedbackSession s, string? brief, bool keepText)
    {
        if (brief == null) return;
        if (keepText) s.Brief = brief;
        if (VerdictRx.Match(brief) is { Success: true } v)
        {
            s.Verdict ??= v.Groups[1].Value.Trim();
            if (v.Groups[2].Success) s.Note ??= v.Groups[2].Value;
        }
        if (AreaRx.Match(brief) is { Success: true } a)
        {
            s.AreaName ??= a.Groups[1].Value;
            s.Area ??= a.Groups[2].Value;
            s.Label ??= a.Groups[4].Value.Length > 0 ? a.Groups[4].Value : null;
            s.Target ??= a.Groups[5].Value.Length > 0 ? a.Groups[5].Value : null;
        }
        if (s.PressedAt == null && HeadRx.Match(brief) is { Success: true } h && s.StartedAt is { } started)
        {
            var t = new TimeSpan(int.Parse(h.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(h.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(h.Groups[4].Value, CultureInfo.InvariantCulture));
            var day = started.ToUniversalTime().Date;
            var at = new DateTimeOffset(day + t, TimeSpan.Zero);
            if (at > started.ToUniversalTime().AddMinutes(1)) at = at.AddDays(-1);   // pressed before midnight, started after
            s.PressedAt = at;
        }
    }

    // ── output.json ──────────────────────────────────────────────────

    /// <summary>What `claude -p --output-format json` wrote: cost, usage, turns, model, the API time, and the result and
    /// session id when state.json has none yet (the handler just exited).</summary>
    public static void ApplyOutput(FeedbackSession s, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        JsonElement r;
        try
        {
            var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{')) ?? text.Trim();
            using var doc = JsonDocument.Parse(line);
            r = doc.RootElement.Clone();
        }
        catch (JsonException) { return; }
        if (r.ValueKind != JsonValueKind.Object) return;
        if (r.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) s.CostUsd = Math.Round(c.GetDouble(), 4);
        if (r.TryGetProperty("num_turns", out var n) && n.ValueKind == JsonValueKind.Number) s.Turns = n.GetInt32();
        if (r.TryGetProperty("duration_api_ms", out var a) && a.ValueKind == JsonValueKind.Number) s.ApiMs = a.GetDouble();
        if (r.TryGetProperty("session_id", out var id) && id.ValueKind == JsonValueKind.String) s.SessionId ??= id.GetString();
        if (r.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String) s.Result ??= res.GetString();
        if (r.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
            s.Model = string.Join(", ", mu.EnumerateObject().Select(p => p.Name));
        if (r.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            long? L(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
            s.Usage = new FeedbackUsage
            {
                InputTokens = L("input_tokens"), OutputTokens = L("output_tokens"),
                CacheReadTokens = L("cache_read_input_tokens"), CacheCreationTokens = L("cache_creation_input_tokens"),
                ThinkingTokens = u.TryGetProperty("output_tokens_details", out var od) && od.ValueKind == JsonValueKind.Object && od.TryGetProperty("thinking_tokens", out var th) && th.ValueKind == JsonValueKind.Number ? th.GetInt64() : null,
            };
        }
    }

    // ── Paths and times ──────────────────────────────────────────────

    /// <summary>Claude Code keeps a project's transcripts under ~\.claude\projects\&lt;cwd with every non-alphanumeric
    /// character as '-'&gt;\&lt;sessionId&gt;.jsonl.</summary>
    public static string TranscriptPath(string userProfile, string worktree, string sessionId) =>
        Path.Combine(userProfile, ".claude", "projects", ProjectSlug(worktree), sessionId + ".jsonl");

    public static string ProjectSlug(string cwd) => Regex.Replace(cwd.TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-");

    public static double? Duration(DateTimeOffset? started, DateTimeOffset? ended, string status, DateTimeOffset now)
    {
        if (started == null) return null;
        var end = ended ?? (status is "running" or "starting" ? now : (DateTimeOffset?)null);
        return end == null ? null : Math.Round(Math.Max(0, (end.Value - started.Value).TotalSeconds), 1);
    }

    // ── The bridge's events ──────────────────────────────────────────

    /// <summary>Merge the bridge's feedback (press) and feedback.reply events: the press's own note, area, time and
    /// verdict (over the brief's), the replies said on the card, and presses with no folder (queued, "works", or not yet
    /// seen by the dispatcher) as sessions of their own. Returns how many presses and replies it saw.</summary>
    public static (int Presses, int Replies) Merge(List<FeedbackSession> sessions, IEnumerable<JObject> events, IReadOnlyCollection<long> waiting, DateTimeOffset now)
    {
        var bySeq = sessions.ToDictionary(s => s.Seq);
        int presses = 0, replies = 0;
        var replyEvents = new List<JObject>();
        foreach (var e in events)
        {
            var kind = e["kind"]?.ToString();
            if (kind == FeedbackKit.ReplyKind) { replyEvents.Add(e); continue; }
            if (kind != FeedbackKit.PressKind || e["seq"]?.Type is not (JTokenType.Integer or JTokenType.Float)) continue;
            presses++;
            var seq = e["seq"]!.Value<long>();
            var d = e["data"] as JObject ?? new JObject();
            var at = FeedbackKit.At(e["at"]);
            if (!bySeq.TryGetValue(seq, out var s))
            {
                var verdict = d["Event"]?.ToString();
                var answered = e["claims"]?["reply"] != null;
                var handled = e["claims"]?["handle"] is JObject h && h["lapsed"]?.Value<bool>() != true;
                var age = at is { } t ? now - t : TimeSpan.Zero;
                string status;
                if (verdict == "works") status = "works";
                else if (waiting.Contains(seq)) status = "queued";
                else if (!answered && !handled && age <= QueuedMaxAge) status = "queued";
                else continue;   // answered by someone without a folder here, or too old to be taken: nothing to show
                s = new FeedbackSession { Seq = seq, Status = status };
                sessions.Add(s);
                bySeq[seq] = s;
            }
            if (d["Event"] is { Type: JTokenType.String } ev) s.Verdict = ev.ToString();
            if (d["Note"] is { Type: JTokenType.String } note) s.Note = note.ToString();
            if (d["AreaName"] is { Type: JTokenType.String } an) s.AreaName = an.ToString();
            if (d["Area"] is { Type: JTokenType.String } ar) s.Area = ar.ToString();
            if (d["Label"] is { Type: JTokenType.String } lb && lb.ToString().Length > 0) s.Label = lb.ToString();
            if (d["Target"] is { Type: JTokenType.String } tg && tg.ToString().Length > 0) s.Target = tg.ToString();
            if (at != null) s.PressedAt = at;
        }
        foreach (var e in replyEvents)
        {
            var d = e["data"] as JObject;
            if (d?["ref"]?.Type is not (JTokenType.Integer or JTokenType.Float) || !bySeq.TryGetValue(d["ref"]!.Value<long>(), out var s)) continue;
            replies++;
            s.Replies.Add(new FeedbackReply
            {
                Seq = e["seq"]?.Value<long>() ?? 0, At = FeedbackKit.At(e["at"]), Status = d["status"]?.ToString(), Text = d["text"]?.ToString(), Source = e["source"]?.ToString(),
            });
        }
        foreach (var s in sessions)
        {
            s.Replies.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            // A queued press the dispatcher already failed to start says so on the card: show it, it is the fix.
            if (s.Status == "queued" && s.Error == null && s.Replies.LastOrDefault(r => r.Status == "failed") is { } f) s.Error = f.Text;
        }
        sessions.Sort((a, b) => b.Seq.CompareTo(a.Seq));
        return (presses, replies);
    }

    /// <summary>The dispatcher's waiting list as queued sessions when the bridge is not there to describe them.</summary>
    public static void AddWaiting(List<FeedbackSession> sessions, IEnumerable<long> waiting)
    {
        var have = sessions.Select(s => s.Seq).ToHashSet();
        foreach (var seq in waiting) if (have.Add(seq)) sessions.Add(new FeedbackSession { Seq = seq, Status = "queued" });
        sessions.Sort((a, b) => b.Seq.CompareTo(a.Seq));
    }

    // ── Text ─────────────────────────────────────────────────────────

    public static string Summary(FeedbackSessionsResult r)
    {
        var d = r.Dispatcher;
        var sb = new System.Text.StringBuilder();
        sb.Append($"Dispatcher {d.Status}{(d.Alive ? "" : " (NOT RUNNING)")}{(d.Why != null ? $": {d.Why}" : "")}");
        if (d.Fix != null) sb.Append($"\n  Fix: {d.Fix}");
        if (d.ClaudeExe != null) sb.Append($"\n  CLI: {d.ClaudeExe}{(d.LoggedIn == false ? " (NOT logged in)" : d.LoggedIn == true ? " (logged in)" : "")}");
        if (d.Waiting.Count > 0) sb.Append($"\n  Waiting: {string.Join(", ", d.Waiting.Select(w => "#" + w))}");
        if (r.Bridge != null) sb.Append($"\n  Bridge: {r.Bridge}");
        sb.Append($"\n{r.Sessions.Count} of {r.Total} press(es):");
        foreach (var s in r.Sessions)
        {
            sb.Append($"\n#{s.Seq} {s.Status}");
            if (s.Name != null) sb.Append($" \"{s.Name}\"");
            if (s.AreaName != null) sb.Append($" in {s.AreaName}");
            if (s.PressedAt is { } p) sb.Append($" at {p:HH:mm:ss}");
            if (s.DurationSec is { } dur) sb.Append($", {dur:0} s");
            if (s.CostUsd is { } cost) sb.Append($", ${cost:0.00}");
            if (s.Note != null) sb.Append($"\n   note: {FeedbackKit.Clip(s.Note, 160)}");
            if (s.Result != null) sb.Append($"\n   answer: {FeedbackKit.Clip(s.Result, 200)}");
            if (s.Error != null) sb.Append($"\n   error: {FeedbackKit.Clip(s.Error, 200)}");
            if (s.Url != null) sb.Append($"\n   {s.Url}");
        }
        return sb.ToString();
    }
}
