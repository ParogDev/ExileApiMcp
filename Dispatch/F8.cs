using System.Text.RegularExpressions;
using ExileApiMcp.Tools;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Dispatch;

/// <summary>
/// The F8 progress contract's pure half (research/f8-agent-ux.md 3.1, 7 C4/C5, 8.1), unit-tested in Tests/: stages and
/// their phases, the legacy status both ways, reading a reply's stage, stage inference for handler sessions, and the
/// feedback.reply data this server publishes. Stages travel as strings (the enum names), compared case-insensitively.
/// </summary>
public static class F8
{
    /// <summary>F8Stage, in pipeline order (the rank is the index): inference only ever moves forward through it.</summary>
    public static readonly string[] Stages =
        ["Pressed", "Queued", "Claimed", "Reading", "Investigating", "Fixing", "LiveFix", "Reviewing", "Merging", "Deploying", "Verifying", "Done", "Failed", "Dropped"];

    public static readonly string[] Phases = ["Sent", "Looking", "Fixing", "Shipping", "Checking", "Ended"];

    /// <summary>WaitingOn. Player is the bridge's to set (derived from what the session holds), never an agent's.</summary>
    public static readonly string[] WaitingOns = ["None", "Session", "Player", "Ci", "Review", "OtherAgent", "Restart"];

    public static readonly string[] Finals = ["Done", "Failed", "Dropped"];

    /// <summary>FeedbackKit.Clip held to the limit itself (C11: Clip may add "..." past it); "" for nothing.</summary>
    public static string Fit(string? s, int max) => FeedbackKit.Clip(s, max) is var c && c.Length <= max ? c : FeedbackKit.Clip(s, max - 3);

    /// <summary>The canonical stage name for any casing ("livefix" -> "LiveFix"), or null when it is not a stage.</summary>
    public static string? Stage(string? s) => s == null ? null : Array.Find(Stages, x => x.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string? WaitingOn(string? s) => s == null ? null : Array.Find(WaitingOns, x => x.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The stage's place in the pipeline; -1 for anything else.</summary>
    public static int Rank(string? stage) => Stage(stage) is { } s ? Array.IndexOf(Stages, s) : -1;

    public static bool IsFinal(string? stage) => Stage(stage) is { } s && Finals.Contains(s);

    /// <summary>F8Phase (8.1): what the strip's five dots draw.</summary>
    public static string? Phase(string? stage) => Stage(stage) switch
    {
        "Pressed" or "Queued" => "Sent",
        "Claimed" or "Reading" or "Investigating" => "Looking",
        "Fixing" or "LiveFix" or "Reviewing" => "Fixing",
        "Merging" or "Deploying" => "Shipping",
        "Verifying" => "Checking",
        "Done" or "Failed" or "Dropped" => "Ended",
        _ => null,
    };

    /// <summary>C5: an old status as a stage (queued, claimed, working, done, failed).</summary>
    public static string? StageFromStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "queued" => "Queued",
        "claimed" => "Claimed",
        "working" => "Investigating",
        "done" => "Done",
        "failed" => "Failed",
        _ => null,
    };

    /// <summary>C5 reversed: the status an old Route build understands for a stage. Dropped ends the line like done.</summary>
    public static string LegacyStatus(string? stage) => Stage(stage) switch
    {
        "Pressed" or "Queued" => "queued",
        "Claimed" => "claimed",
        "Done" or "Dropped" => "done",
        "Failed" => "failed",
        _ => "working",
    };

    /// <summary>A feedback.reply's stage: its Stage, else its legacy status mapped (C5). Either casing.</summary>
    public static string? ReplyStage(JToken? data) =>
        Stage(Field(data, "Stage")) ?? StageFromStatus(Field(data, "Status") ?? Field(data, "status"));

    /// <summary>A feedback.reply's press: Ref or ref.</summary>
    public static long? ReplyRef(JToken? data) =>
        (data?["Ref"] ?? data?["ref"]) is { Type: JTokenType.Integer or JTokenType.Float } r ? r.Value<long>() : null;

    public static string? ReplyText(JToken? data) => Field(data, "Text") ?? Field(data, "text");

    private static string? Field(JToken? data, string name) => data is JObject o && o[name] is { Type: not JTokenType.Null } v ? v.ToString() : null;

    /// <summary>The newest stage the replies to press <paramref name="seq"/> reached (bridge events, oldest first), or null.</summary>
    public static string? CurrentStage(IEnumerable<JObject> replyEvents, long seq) =>
        replyEvents.Where(e => ReplyRef(e["data"]) == seq).Select(e => ReplyStage(e["data"])).LastOrDefault(s => s != null);

    /// <summary>The stage to publish when <paramref name="wanted"/> is inferred while the press stands at <paramref name="current"/>:
    /// only forward, never out of a final stage, never into one (inference doesn't end a press). Null: publish nothing.</summary>
    public static string? Advance(string? current, string? wanted)
    {
        if (Stage(wanted) is not { } w || IsFinal(w)) return null;
        if (IsFinal(current)) return null;
        return Rank(w) > Rank(current) ? w : null;
    }

    /// <summary>The press a caller handles, from its identity (route-f8-&lt;seq&gt;, FeedbackKit.Label), or null.</summary>
    public static long? SeqOfLabel(string? label) =>
        label != null && Regex.Match(label, @"^route-f8-(\d+)$") is { Success: true } m && long.TryParse(m.Groups[1].Value, out var s) ? s : null;

    /// <summary>Tools that read the game (C4: the handler's first one moves its press to Investigating).</summary>
    private static readonly HashSet<string> GameReads = new(StringComparer.Ordinal)
    {
        "route_state", "run_csharp", "explore_object", "find_in_object", "snapshot", "eval_path", "observe", "observe_events",
        "observe_series", "observe_timeline", "game_data", "find_in_game_data", "describe_type", "deep_scan", "memory_read",
    };

    /// <summary>C4: the stage a handler's call implies, or null when it implies none.</summary>
    public static string? InferredStage(string tool) =>
        tool == "mark_reviewed" ? "Reviewing"
        : tool.StartsWith("get_", StringComparison.Ordinal) || GameReads.Contains(tool) ? "Investigating"
        : null;

    /// <summary>The card line when a stage comes without one (old Route builds show Text above the card).</summary>
    public static string DefaultText(string stage, long seq) => Stage(stage) switch
    {
        "Queued" => $"Got #{seq}: it waits for the handler before it.",
        "Claimed" => $"Claude is on it (#{seq}).",
        "Reading" => $"Claude is reading #{seq}.",
        "Investigating" => $"Claude is looking at the game for #{seq}.",
        "Fixing" => $"Claude is fixing #{seq}.",
        "LiveFix" => $"#{seq}: a fix is live: try it now.",
        "Reviewing" => $"Claude is reviewing the fix for #{seq}.",
        "Merging" => $"#{seq}: merging the fix.",
        "Deploying" => $"#{seq}: bringing the fix into the HUD.",
        "Verifying" => $"#{seq}: watching whether the step moves on.",
        "Done" => $"Done with #{seq}.",
        "Failed" => $"#{seq} failed.",
        "Dropped" => $"#{seq} was dropped.",
        _ => $"#{seq}",
    };

    /// <summary>The feedback.reply data (both casings of the legacy fields; limits per C11). status overrides the legacy
    /// status derived from the stage (a start failure that is retried: stage Queued, status failed).</summary>
    public static F8ReplyData Reply(long seq, string stage, string? text, string? detail = null, string? ask = null, F8PrRef? pr = null,
        string? waitingOn = null, string? status = null, DateTime? at = null)
    {
        var s = Stage(stage) ?? throw new ArgumentException($"'{stage}' is not a stage ({string.Join(" | ", Stages)})");
        var t = F8.Fit(text, 400) is { Length: > 0 } x ? x : DefaultText(s, seq);
        var st = status ?? LegacyStatus(s);
        return new F8ReplyData
        {
            RefLegacy = seq, StatusLegacy = st, TextLegacy = t,
            Ref = seq, Status = st, Text = t, Stage = s,
            WaitingOn = WaitingOn(waitingOn),
            Detail = F8.Fit(detail, 400) is { Length: > 0 } d ? d : null,
            Ask = F8.Fit(ask, 120) is { Length: > 0 } a ? a : null,
            Pr = pr, At = at ?? DateTime.UtcNow,
        };
    }

    // ── The press (F8PressDto, spec 2.3-2.6, 8.2) ──

    /// <summary>Flag ids (2.3) and what each means to the handler.</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, string Means)> FlagInfo = new Dictionary<string, (string, string)>
    {
        ["wrong-place"] = ("Wrong place", "the marker or path leads somewhere wrong"),
        ["not-advancing"] = ("Not advancing", "the step should be done but the card still shows it"),
        ["missing-step"] = ("Missing step", "something must be done here that the route does not have"),
        ["cant-reach"] = ("Can't reach it", "the target is right, the path or the place is not reachable"),
        ["wrong-words"] = ("Wrong words", "the card's text is wrong or unclear"),
        ["wrong-marker"] = ("Wrong marker", "the marker is on the wrong thing"),
        ["no-marker"] = ("No marker", "there should be something to click and nothing is marked"),
        ["too-early"] = ("Too early", "the step is shown before it can be done"),
        ["already-done"] = ("Already done", "the player did this already; the route did not notice"),
        ["odd-path"] = ("Odd path", "the drawn path goes a strange way"),
        ["something-else"] = ("Something else", "none of the above; read the note"),
        ["works"] = ("Works here", "positive signal: this step works"),
    };

    public static List<string> Flags(JObject d) => (d["Flags"] as JArray)?.Select(f => f.ToString()).Where(f => f.Length > 0).ToList() ?? [];

    private static string? Str(JObject d, string name) => d[name] is { Type: JTokenType.String } v && v.ToString().Trim().Length > 0 ? v.ToString().Trim() : null;

    public static string? Note(JObject d) => Str(d, "Note");
    public static string? Verdict(JObject d) => Str(d, "Verdict");
    public static long? PressRef(JObject d) => d["Ref"] is { Type: JTokenType.Integer } r ? r.Value<long>() : null;

    /// <summary>C7: a press that only says "works here" (Flags ["works"] or the old Event "works", no note, not about an
    /// earlier press): recorded, no session.</summary>
    public static bool WorksOnly(JObject d)
    {
        if (Note(d) != null || PressRef(d) != null) return false;
        var flags = Flags(d);
        return flags.Count > 0 ? flags.All(f => f == "works") : Str(d, "Event") == "works";
    }

    /// <summary>The verify row's "Works now" (Ref + Verdict works) with nothing more to say: the referenced press is
    /// confirmed, no session.</summary>
    public static bool ConfirmsRef(JObject d) =>
        PressRef(d) != null && Verdict(d) == "works" && Note(d) == null && Flags(d).All(f => f is "works" or "something-else");

    /// <summary>The verify row's "Still wrong": a new press about the referenced one.</summary>
    public static bool Reopens(JObject d) => PressRef(d) != null && Verdict(d) == "still-wrong";

    /// <summary>What the player asked for, in a few words: the flags' labels, else the note's start, else the old verdict.</summary>
    public static string What(JObject d)
    {
        var flags = Flags(d).Where(f => f != "something-else" || Note(d) == null).Select(f => FlagInfo.TryGetValue(f, out var i) ? i.Label : f).ToList();
        if (flags.Count > 0) return string.Join(", ", flags);
        return Note(d) ?? Str(d, "Event") ?? "feedback";
    }

    /// <summary>The press's own section of the handler's brief: flags, suggestions (with the rejected ones as "do not
    /// check"), scope, the player's hints, watch-for, the coming capture, and the press it reopens. Empty for an old press.</summary>
    public static string PressSection(JObject d, long seq, string? reopenedAnswer)
    {
        var sb = new System.Text.StringBuilder();
        if (Reopens(d))
        {
            sb.AppendLine($"**This reopens #{PressRef(d)}: the player says it is STILL WRONG after that fix.**");
            sb.AppendLine(reopenedAnswer != null ? $"#{PressRef(d)} answered: \"{F8.Fit(reopenedAnswer, 400)}\"" : $"(#{PressRef(d)}'s answer is not on record: feedback_sessions has it.)");
            sb.AppendLine("Start from that fix: what it changed, and why the step is still wrong.");
            sb.AppendLine();
        }
        var flags = Flags(d);
        if (flags.Count > 0)
        {
            sb.AppendLine("What the player picked:");
            foreach (var f in flags)
                sb.AppendLine(FlagInfo.TryGetValue(f, out var i) ? $"- **{i.Label}** ({f}): {i.Means}" : $"- **{f}**");
        }
        var kept = new List<string>();
        var rejected = new List<string>();
        foreach (var s in (d["Suggested"] as JArray ?? []).OfType<JObject>())
        {
            var label = Str(s, "Label") ?? Str(s, "Id") ?? "?";
            var reason = Str(s, "Reason");
            var line = $"{label} ({Str(s, "Id")}{(reason != null ? $": {reason}" : "")}{(s["Certain"]?.Value<bool>() == true ? ", measured" : "")})";
            if (s["Kept"]?.Value<bool>() == true) kept.Add(line);
            else if (s["Certain"]?.Value<bool>() == true) rejected.Add(line);   // preselected and turned off: the player says no
        }
        if (kept.Count > 0) { sb.AppendLine("The plugin noticed, and the player kept:"); foreach (var k in kept) sb.AppendLine($"- {k}"); }
        if (rejected.Count > 0) { sb.AppendLine("The plugin noticed, and the PLAYER REJECTED (do not spend time on these):"); foreach (var r in rejected) sb.AppendLine($"- {r}"); }
        var scope = Str(d, "Scope");
        if (scope != null)
            sb.AppendLine($"Scope: **{scope}**" + (scope == "panel" ? $" (the open panel: {Str(d, "ScopePanel") ?? "unnamed"}): look only at that panel" : scope switch
            {
                "step" => ": the current route step", "area" => ": this area as a whole", "marker" => ": the marker drawn on screen",
                "route" => ": the whole route", _ => "",
            }));
        var hints = (d["Hints"] as JArray)?.Select(h => h.ToString()).ToList() ?? [];
        foreach (var h in hints)
            sb.AppendLine(h switch
            {
                "path-ok" => "- Player says **pathing is fine: do not check pathing**.",
                "panel-only" => $"- Player says **only the open panel** ({Str(d, "ScopePanel") ?? "the panel open at the press"}) matters.",
                "watch-next" => "- Player will show you what they do next (a capture follows).",
                _ => $"- Hint: {h}",
            });
        if (Str(d, "WatchFor") is { } wf) sb.AppendLine($"Where to look, in the player's words: \"{F8.Fit(wf, 200)}\"");
        if (d["WatchNext"]?.Value<bool>() == true || hints.Contains("watch-next"))
            sb.AppendLine($"**A feedback.capture event with Ref={seq} follows within 90 s** (what the player does next): wait for it with " +
                          $"`pubsub_wait kinds=[\"feedback.capture\"] since={seq} timeoutSec=90` before you conclude.");
        return sb.ToString();
    }
}

/// <summary>
/// route_agent's validation (spec 4, 8.2, C11, section 11), pure: builds the route.agent command or throws ArgumentException
/// with what to change. Owner / OwnerName / Hue are not set here: the bridge stamps them from the publishing session.
/// </summary>
public static class AgentRouteKit
{
    public const int MaxSteps = 8, DefaultTtl = 600, MaxTtl = 1800, MinTtl = 30, DefaultWithin = 25;
    public static readonly string[] DoneKinds = ["Flag", "Near", "Area", "Click", "Manual", "Agent", "Dead"];
    public static readonly string[] HandBacks = ["Resume", "Hold"];

    /// <summary>Progress events after which the route no longer leads (left parks it: resumed brings it back).</summary>
    public static readonly string[] EndEvents = ["done", "expired", "ended", "replaced", "owner-gone", "cleared", "refused"];

    public static string? DoneKind(string? s) => s == null ? null : Array.Find(DoneKinds, k => k.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase));

    public static AgentRouteCommand Set(string id, string? title, string? reason, IReadOnlyList<AgentRouteStepArg>? steps, int? ttlSec, string? handBack, long? @ref)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("set needs a title: what you lead the player to, in plain words (<= 40 chars).");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("set needs a reason: why you need it, one line the player reads on hover (<= 120 chars).");
        if (steps is not { Count: >= 1 and <= MaxSteps })
            throw new ArgumentException($"bad_steps: an agent route has 1-{MaxSteps} steps ({steps?.Count ?? 0} given): a short detour, not a route.");
        var hb = handBack == null ? "Resume" : Array.Find(HandBacks, h => h.Equals(handBack.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"handBack must be Resume or Hold, not '{handBack}'.");
        var out_ = new List<AgentRouteStepArg>();
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var n = $"step {i + 1}" + (string.IsNullOrWhiteSpace(s.Target) ? "" : $" ('{s.Target}')");
            if (string.IsNullOrWhiteSpace(s.Label)) throw new ArgumentException($"{n} has no label: the card says what to do in plain words (<= 40 chars).");
            var target = s.Target?.Trim() ?? "";
            var kind = s.Done?.Kind == null ? (target.Length > 0 ? "Near" : "Manual") : DoneKind(s.Done.Kind)
                ?? throw new ArgumentException($"{n}: done.kind must be one of {string.Join(" | ", DoneKinds)}, not '{s.Done.Kind}'.");
            var value = s.Done?.Value?.Trim() is { Length: > 0 } v ? v : null;
            if (kind is "Flag" or "Area" or "Dead" && value == null)
                throw new ArgumentException($"{n}: done.kind {kind} needs done.value ({kind switch { "Flag" => "the quest flag name", "Area" => "the area pattern", _ => "the monster path part" }}).");
            if (kind is "Near" or "Click" && target.Length == 0)
                throw new ArgumentException($"{n}: done.kind {kind} needs a target (a route target: checkpoint, waypoint, transition:X, tile:X, radar:X, entity:X, click:X, item:X).");
            if (s.Highlight is { } hl && hl.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new ArgumentException($"{n}: highlight must be an object in guide.highlight's target shape ({{item | path | text | panel+child | rect, label?, action?}}).");
            out_.Add(new AgentRouteStepArg
            {
                Target = target,
                Label = F8.Fit(s.Label, 40),
                Note = F8.Fit(s.Note, 120) is { Length: > 0 } note ? note : null,
                Done = new AgentRouteDoneRule { Kind = kind, Value = value, Within = s.Done?.Within is { } w ? Math.Clamp(w, 1, 200) : null },
                Highlight = s.Highlight is { ValueKind: System.Text.Json.JsonValueKind.Object } h ? h : null,
                WithinCells = s.WithinCells is { } wc ? Math.Clamp(wc, 1, 200) : null,
            });
        }
        return new AgentRouteCommand
        {
            Op = "set", Id = id, Ref = @ref, Title = F8.Fit(title, 40), Reason = F8.Fit(reason, 120),
            TtlSec = Math.Clamp(ttlSec ?? DefaultTtl, MinTtl, MaxTtl), HandBack = hb, Steps = out_,
        };
    }

    /// <summary>A route id unique enough to wait on: &lt;caller label&gt;:&lt;6 hex&gt;.</summary>
    public static string NewId(string label) => $"{label}:{Guid.NewGuid():N}"[..(label.Length + 7)];

    /// <summary>How a run of progress events (oldest first) left the route: ended (and how), parked (left, not resumed), or leading.</summary>
    public static (string? Ended, bool Parked) Outcome(IEnumerable<string> events)
    {
        string? ended = null;
        var parked = false;
        foreach (var e in events)
        {
            if (EndEvents.Contains(e)) { ended = e; parked = false; }
            else if (e == "left") parked = true;
            else if (e is "resumed" or "started") { parked = false; if (e == "started") ended = null; }
        }
        return (ended, parked);
    }
}
