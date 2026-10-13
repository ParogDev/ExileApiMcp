using System.Collections.Concurrent;
using ExileApiMcp.Bridge;
using ExileApiMcp.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Incidents;

// The user's planned restart of a game and its HUD (hud_restart mode=user): an incident of kind "outage", key
// outage:<game>, whose "outage" field carries the phases announced -> holding -> down -> waitingGame -> back | cancelled.
// While it is open (announced..waitingGame) agent restarts are held (restart-hud.ps1 exit 8, hud_restart / deploy_plugin
// held_by_outage), calls answer "not now" (Bridge/HudState.cs), and the in-game card (bridge outage.set) shows who is busy.

/// <summary>The incident's "outage" field (camelCase, ISO UTC, unknown fields kept).</summary>
public sealed class OutageInfo
{
    public string Game { get; set; } = "";
    /// <summary>announced | holding | down | waitingGame | back | cancelled.</summary>
    public string Phase { get; set; } = "announced";
    public DateTime? PhaseAt { get; set; }
    public string? Reason { get; set; }
    /// <summary>The requesting session's id (SessionIdentity.ResponseId).</summary>
    public string? RequestedBy { get; set; }
    public string? RequestedByLabel { get; set; }
    public DateTime? AnnouncedAt { get; set; }
    /// <summary>The board rev of the announcement: a session that has seen rev &gt;= this made a tool call since.</summary>
    public int? AnnouncedRev { get; set; }
    /// <summary>We stop the HUD; the user restarts game and HUD by hand.</summary>
    public bool StopOnly { get; set; } = true;
    /// <summary>The in-game card's answer: null | now | notNow.</summary>
    public string? Answer { get; set; }
    /// <summary>Phase down: when the HUD stops (Restart now + <see cref="OutageLogic.StopCountdown"/>); the card counts down to
    /// it and its Cancel (outage.answer notNow) still cancels. Null outside the countdown.</summary>
    public DateTime? StopAt { get; set; }
    /// <summary>Phase down: when restart-hud.ps1 -StopOnly stopped the HUD (null while counting down to <see cref="StopAt"/>).</summary>
    public DateTime? StoppedAt { get; set; }
    public Dictionary<string, OutageParticipant> Participants { get; set; } = [];
    public DateTime? ClosedAt { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

public sealed class OutageParticipant
{
    public string Label { get; set; } = "";
    /// <summary>The label, made unique among the participants ("Claude Desktop (2)").</summary>
    public string? Display { get; set; }
    /// <summary>busy | ready | idle | gone.</summary>
    public string State { get; set; } = "ready";
    public string? Doing { get; set; }
    public DateTime? At { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

/// <summary>The bridge's outage.state reply (bridge OutageStateDto, Bridge/BridgeContract.cs mirror): the readiness lists and
/// the card's answer, all the runner reads; the card's own fields pass through <see cref="Extra"/>.</summary>
public sealed class OutageStateReply
{
    /// <summary>Sessions doing something / holding a lease; null on a bridge without outage.state.</summary>
    public List<OutageParty>? Busy { get; set; }
    /// <summary>Every other connected agent session.</summary>
    public List<OutageParty>? Ready { get; set; }
    /// <summary>The card's answer: null | now | notNow.</summary>
    public string? Answer { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

/// <summary>One session in outage.state's busy / ready lists (bridge OutageParty).</summary>
public sealed class OutageParty
{
    public string? Session { get; set; }
    public string? Label { get; set; }
    /// <summary>What it is doing (busy only).</summary>
    public string? Doing { get; set; }
    /// <summary>The lease it holds (busy only).</summary>
    public string? Lease { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }
}

/// <summary>One session as the readiness check sees it.</summary>
public sealed record OutageSession(string Session, string Label, string? Doing, string? Lease);

/// <summary>What the runner should do next (pure decision).</summary>
public enum OutageStep { None, Hold, Countdown, Stop, Cancel, WaitingGame, Back }

/// <summary>The pure half: reading and writing the field, the phase machine and readiness. Unit-tested.</summary>
public static class OutageLogic
{
    public static readonly string[] HoldingPhases = ["announced", "holding", "down", "waitingGame"];
    /// <summary>Restart now -> phase down with stopAt = now + this; the card counts down with Cancel, then the HUD stops.</summary>
    public static readonly TimeSpan StopCountdown = TimeSpan.FromSeconds(8);
    private static readonly JsonSerializer Json = JsonSerializer.Create(IncidentJson.Settings);

    public static string Key(string game) => $"outage:{game}";

    public static OutageInfo? Info(Incident i)
    {
        try { return i.Get("outage") is JObject o ? o.ToObject<OutageInfo>(Json) : null; }
        catch (JsonException) { return null; }
    }

    public static void SetInfo(Incident i, OutageInfo info) => i.Set("outage", JObject.FromObject(info, Json));

    /// <summary>The open outage of a game whose phase still holds restarts (announced..waitingGame; an unreadable field counts as open).</summary>
    public static Incident? OpenFor(IncidentFile f, string game) =>
        f.Incidents.FirstOrDefault(i => i.IsOpen && string.Equals(i.Key, Key(game), StringComparison.OrdinalIgnoreCase)
                                        && (Info(i)?.Phase is not { } p || HoldingPhases.Contains(p)));

    /// <summary>Raise the outage (or return the open one: created false).</summary>
    public static (Incident Incident, bool Created) Raise(IncidentFile f, string game, string reason, string requestedBy, string requestedByLabel, int ownerPid, DateTime now)
    {
        if (OpenFor(f, game) is { } open) return (open, false);
        var G = game == "poe1" ? "PoE1" : "PoE2";
        var info = new OutageInfo
        {
            Game = game, Phase = "announced", PhaseAt = now, Reason = reason, RequestedBy = requestedBy, RequestedByLabel = requestedByLabel,
            AnnouncedAt = now, AnnouncedRev = f.Rev + 1, StopOnly = true,
        };
        var (i, created) = IncidentLogic.Raise(f, new IncidentDraft
        {
            Kind = "outage", Scope = game, Key = Key(game),
            Title = $"{G}: the user restarts game + HUD ({reason})",
            Detail = Detail(info),
            Fix = "Agents: finish or pause what you do (a lease, a guided step, an open ask keeps you busy); calls to this HUD answer " +
                  "'not now' until the game is back in, and agent restarts are held. The incident resolves by itself (back) or on Not now (cancelled).",
            Fields = { ["game"] = game, ["ownerPid"] = ownerPid },
        }, requestedByLabel, now);
        SetInfo(i, info);
        return (i, created);
    }

    public static string Detail(OutageInfo o) => o.Phase switch
    {
        "announced" => $"Announced by {o.RequestedByLabel}: the in-game card asks the user; the HUD stops only on the user's Restart now.",
        "holding" => $"Waiting for the user's Restart now on the in-game card (busy agents: {string.Join(", ", o.Participants.Values.Where(p => p.State == "busy").Select(p => p.Display ?? p.Label).DefaultIfEmpty("none"))}).",
        "down" when o.StoppedAt == null && o.StopAt != null =>$"The user pressed Restart now: the HUD stops at {o.StopAt:HH:mm:ss}Z ({StopCountdown.TotalSeconds:0} s countdown on the card, Cancel still cancels).",
        "down" => "HUD stopped (restart-hud.ps1 -StopOnly): the user restarts the game and the HUD by hand.",
        "waitingGame" => "A new HUD is up, the game isn't in yet (login / character select / load).",
        "back" => "The HUD is back and the game is in.",
        "cancelled" => "Cancelled (Not now on the card, or the requester).",
        _ => $"phase {o.Phase}",
    };

    /// <summary>Move to a phase (no-op when already there); back / cancelled resolve the incident with that resolution.</summary>
    public static bool SetPhase(Incident i, string phase, string who, DateTime now, string? note = null)
    {
        var o = Info(i) ?? new OutageInfo { Game = i.Scope };
        if (o.Phase == phase && i.IsOpen) return false;
        o.Phase = phase; o.PhaseAt = now;
        if (phase is "back" or "cancelled") o.ClosedAt = now;
        SetInfo(i, o);
        i.Detail = Detail(o) + (note != null ? " " + note : "");
        if (phase is "back" or "cancelled") IncidentLogic.Resolve(i, who, phase == "back" ? "back" : note ?? "cancelled", now);
        return true;
    }

    /// <summary>Restart now: phase down with stopAt (the card's countdown; Cancel still cancels until then). False when not open.</summary>
    public static bool BeginCountdown(Incident i, string who, DateTime now, DateTime stopAt)
    {
        if (!i.IsOpen) return false;
        var o = Info(i) ?? new OutageInfo { Game = i.Scope };
        o.Phase = "down"; o.PhaseAt = now; o.StopAt = stopAt; o.StoppedAt = null; o.Answer = "now";
        SetInfo(i, o);
        i.Detail = Detail(o);
        return true;
    }

    /// <summary>restart-hud.ps1 -StopOnly stopped the HUD: from here the monitor waits for a newer bridge.</summary>
    public static bool MarkStopped(Incident i, DateTime now)
    {
        if (!i.IsOpen) return false;
        var o = Info(i) ?? new OutageInfo { Game = i.Scope, Phase = "down" };
        o.StoppedAt = now;
        SetInfo(i, o);
        i.Detail = Detail(o);
        return true;
    }

    /// <summary>Record the card's answer and who is busy / ready (display names unique per label). True when something changed.</summary>
    public static bool SetParticipants(Incident i, string? answer, IReadOnlyList<OutageSession> busy, IReadOnlyList<OutageSession> ready, DateTime now)
    {
        var o = Info(i) ?? new OutageInfo { Game = i.Scope };
        var all = busy.Select(s => (s, "busy")).Concat(ready.Select(s => (s, "ready"))).ToList();
        var names = IncidentLogic.DisplayNames(all.Select(x => (x.s.Label, (string?)x.s.Session)).ToList());
        var next = new Dictionary<string, OutageParticipant>();
        for (var k = 0; k < all.Count; k++)
        {
            var (s, state) = all[k];
            var prev = o.Participants.TryGetValue(s.Session, out var p) ? p : null;
            var changed = prev == null || prev.State != state || prev.Doing != s.Doing || prev.Display != names[k];
            next[s.Session] = new OutageParticipant { Label = s.Label, Display = names[k], State = state, Doing = s.Doing, At = changed ? now : prev!.At };
        }
        var before = JsonConvert.SerializeObject(o.Participants, IncidentJson.Settings) + o.Answer;
        o.Participants = next;
        if (answer != null) o.Answer = answer;
        if (before == JsonConvert.SerializeObject(o.Participants, IncidentJson.Settings) + o.Answer) return false;
        SetInfo(i, o);
        if (o.Phase is "announced" or "holding") i.Detail = Detail(o);
        return true;
    }

    /// <summary>
    /// The next step. announced/holding: Not now cancels; only the user's Restart now starts the stop (Countdown: phase down
    /// with stopAt), never "nobody busy" alone (busyCount only informs the card); otherwise hold.
    /// down before the stop: Cancel on the card (notNow) cancels; at stopAt the HUD stops (Stop).
    /// down after the stop: a newer bridge (heartbeat after the stop) at menu/loading/starting is waitingGame, in game is back.
    /// waitingGame: in game (fresh heartbeat, HUD alive) is back.
    /// </summary>
    public static OutageStep Decide(OutageInfo o, string? answer, int busyCount, HudStateFile? state, bool? hudAlive, DateTime now)
    {
        switch (o.Phase)
        {
            case "announced":
            case "holding":
                if ((answer ?? o.Answer) == "notNow") return OutageStep.Cancel;
                if ((answer ?? o.Answer) == "now") return OutageStep.Countdown;
                return o.Phase == "announced" ? OutageStep.Hold : OutageStep.None;
            case "down" when o.StoppedAt == null && o.StopAt != null:   // no stopAt: an older runner's down, already stopped
                if ((answer ?? o.Answer) == "notNow") return OutageStep.Cancel;
                return now < o.StopAt.Value ? OutageStep.None : OutageStep.Stop;
            case "down":
            case "waitingGame":
                if (state == null || hudAlive == false) return OutageStep.None;
                var stopped = o.StoppedAt ?? o.PhaseAt;
                var fresh = state.Heartbeat is { } hb && now - hb < HudAvailabilityLogic.HeartbeatStale && (stopped == null || hb > stopped.Value);
                if (!fresh) return OutageStep.None;
                if (state.IsInGame && state.Phase is "ready" or null) return OutageStep.Back;
                if (o.Phase == "down" && state.Phase is "menu" or "loading" or "starting" or "ready") return OutageStep.WaitingGame;
                return OutageStep.None;
            default:
                return OutageStep.None;
        }
    }

    /// <summary>Readiness from the bridge's outage.state reply ({busy: [{session, label, doing, lease}], ready: [...]}); null when it has none.</summary>
    public static (List<OutageSession> Busy, List<OutageSession> Ready, string? Answer)? FromOutageState(JToken reply)
    {
        if (reply is not JObject o || (o["busy"] is not JArray && o["ready"] is not JArray)) return null;
        OutageStateReply r;
        try { r = o.ToObject<OutageStateReply>(Json)!; }
        catch (JsonException) { return null; }   // not the shape outage.state writes: readiness falls back to session.list
        static List<OutageSession> List(List<OutageParty>? parties) => (parties ?? []).Where(x => x != null).Select(x => new OutageSession(
            x.Session ?? (x.Extra != null && x.Extra.TryGetValue("id", out var id) ? Str(id) : null) ?? x.Label ?? "?", x.Label ?? x.Session ?? "?", x.Doing, x.Lease)).ToList();
        return (List(r.Busy), List(r.Ready), r.Answer);
    }

    /// <summary>
    /// Readiness from session.list (bridges without outage.state): a connected agent (not infra, not stale) is busy when it is
    /// doing something, holds a lease or has an attention item queued; every other one is ready. Readiness never waits on silence.
    /// </summary>
    public static (List<OutageSession> Busy, List<OutageSession> Ready) FromSessionList(JToken reply, string? exclude)
    {
        var busy = new List<OutageSession>(); var ready = new List<OutageSession>();
        foreach (var s in (reply["sessions"] as JArray ?? []).OfType<JObject>())
        {
            if (s["connected"]?.Value<bool>() != true || s["infra"]?.Value<bool>() == true || s["stale"]?.Value<bool>() == true) continue;
            var id = s["id"]?.ToString() ?? "?";
            if (exclude != null && id == exclude) continue;
            var label = s["label"]?.ToString() ?? id;
            var doing = (s["doing"] as JArray ?? []).Select(d => d.ToString()).FirstOrDefault();
            var queued = (s["queued"] as JArray)?.Count > 0;
            var leases = s["leases"]?.Type == JTokenType.Integer && s["leases"]!.Value<int>() > 0;
            var item = new OutageSession(id, label, doing ?? (queued ? "waiting to show the player something" : leases ? "holds a lease" : null), leases ? "lease" : null);
            (doing != null || queued || leases ? busy : ready).Add(item);
        }
        return (busy, ready);
    }

    /// <summary>The worker process that drives the outage (runnerPid), or null.</summary>
    public static int? RunnerPid(Incident i) => i.Get("runnerPid")?.Type == JTokenType.Integer ? i.Get("runnerPid")!.Value<int>() : null;

    /// <summary>
    /// Who else drives this outage: null when this process may (owner = this session's pid or dead / unrecorded, runner = this
    /// process or dead / unrecorded), else the link that says no. Liveness unknown (null) counts as alive.
    /// </summary>
    public static string? DrivenElsewhere(Incident i, int sessionPid, int processId, Func<int, bool?> alive)
    {
        if (i.OwnerPid is { } owner && owner != sessionPid && alive(owner) != false) return $"owner pid {owner} (another server) is alive";
        if (RunnerPid(i) is { } runner && runner != processId && alive(runner) != false) return $"runner pid {runner} (another worker) is alive";
        return null;
    }

    /// <summary>The stop may still run: open, phase down, counting down (not stopped yet), no Cancel recorded. Else why not.</summary>
    public static string? WhyNotStop(Incident? i)
    {
        if (i == null) return "the outage is gone from the board";
        if (!i.IsOpen) return $"the outage is {i.State}";
        if (Info(i) is not { } o) return "the outage field is unreadable";
        if (o.Phase != "down") return $"phase is {o.Phase}, not down";
        if (o.StoppedAt != null) return "already stopped";
        if (o.Answer == "notNow") return "the user pressed Cancel";
        return null;
    }

    private static string? Str(JToken? t) => t == null || t.Type == JTokenType.Null ? null : t is JArray a ? string.Join("; ", a.Select(x => x.ToString())) : t.ToString();
}

/// <summary>
/// Drives an outage in this server (hud_restart mode=user starts it; any server's IncidentMonitor adopts an outage whose
/// owner process died). Every 2 s: announced/holding reads the card's answer and who is busy (bridge outage.state, pub/sub
/// outage.answer, session.list as the fallback; busy / ready only inform the card) and cancels on Not now. Only the user's
/// Restart now starts the stop: phase down with stopAt = now + 8 s (outage.set {phase: down, stopAt}: the card counts down
/// with Cancel), then at stopAt a last check for outage.answer notNow (cancelled), else restart-hud.ps1 -StopOnly -Kind user
/// -Outage id. After the stop, down/waitingGame watch hud-state.json until the game is back in. The bridge posts every outage
/// toast itself; the runner posts none.
/// </summary>
public static class OutageRunner
{
    private static readonly ConcurrentDictionary<string, Task> Running = new();
    /// <summary>An outage the user never answers goes after this (cancelled, "expired").</summary>
    public static readonly TimeSpan HoldMax = TimeSpan.FromHours(2);
    public const string AnswerKind = "outage.answer";

    public static bool IsRunning(string id) => Running.TryGetValue(id, out var t) && !t.IsCompleted;

    private static int? RunnerPid(Incident i) => OutageLogic.RunnerPid(i);

    /// <summary>Runs the outage here; refused (null returned is success, else the reason) when another live server owns it or
    /// another live worker already runs it: two runners would each defer to the other's runnerPid and nobody would drive it.</summary>
    public static string? Start(BridgeRegistry bridges, string game, string id)
    {
        if (IsRunning(id)) return null;
        // The worker process that runs it (a supervised session's pid stays the supervisor's across hot swaps, so the old
        // worker and the new one would both drive it): stamped only when nobody else live drives it (RunAsync, AdoptOrphans).
        var refused = IncidentStore.Update(f =>
        {
            if (IncidentLogic.Find(f, id) is not { IsOpen: true } i) return "the outage is not open";
            if (OutageLogic.DrivenElsewhere(i, SessionIdentity.Pid, Environment.ProcessId, p => DeployIncidents.PidAlive(p, null)) is { } why) return why;
            i.Set("runnerPid", Environment.ProcessId);
            return null;
        });
        if (refused != null) { Console.Error.WriteLine($"[Outage] {id}: not started here: {refused}"); return refused; }
        Running[id] = Task.Run(async () =>
        {
            try { await RunAsync(bridges, game, id); }
            catch (Exception ex) { Console.Error.WriteLine($"[Outage] {id} runner failed: {ex.GetType().Name}: {ex.Message}"); }
            finally { Running.TryRemove(id, out _); }
        });
        return null;
    }

    /// <summary>Busy / ready and the card's answer, best effort (empty lists when the bridge can't say).</summary>
    public static async Task<(List<OutageSession> Busy, List<OutageSession> Ready, string? Answer, string? Note)> ReadinessAsync(
        BridgeRegistry bridges, string game, string id, string? exclude, CancellationToken ct)
    {
        try
        {
            var (_, r) = await bridges.CallAsync(game, "outage.state", new JObject { ["id"] = id }, ct);
            if (OutageLogic.FromOutageState(r) is { } st) return (st.Busy, st.Ready, st.Answer, null);
        }
        catch (ModelContextProtocol.McpException) { }
        try
        {
            var (_, r) = await bridges.CallAsync(game, "session.list", new JObject(), ct);
            var (busy, ready) = OutageLogic.FromSessionList(r, exclude);
            return (busy, ready, null, "this bridge has no outage.state: readiness from session.list");
        }
        catch (ModelContextProtocol.McpException ex) { return ([], [], null, $"readiness unknown: {ex.Message}"); }
    }

    /// <summary>The card's answer published on pub/sub (outage.answer {id, answer}) after <paramref name="since"/>.</summary>
    private static async Task<(string? Answer, long Since)> AnswerAsync(BridgeRegistry bridges, string game, string id, long since, CancellationToken ct)
    {
        try
        {
            var (_, r) = await bridges.CallAsync(game, "pubsub.events", new JObject { ["since"] = since, ["kinds"] = new JArray(AnswerKind), ["limit"] = 100 }, ct);
            string? answer = null;
            foreach (var e in (r["events"] as JArray ?? []).OfType<JObject>())
            {
                since = Math.Max(since, e["seq"]?.Value<long>() ?? since);
                if (e["data"] is JObject d && d["id"]?.ToString() == id && d["answer"]?.ToString() is "now" or "notNow") answer = d["answer"]!.ToString();
            }
            if (r["seq"]?.Type == JTokenType.Integer) since = Math.Max(since, r["seq"]!.Value<long>());
            return (answer, since);
        }
        catch (ModelContextProtocol.McpException) { return (null, since); }
    }

    /// <summary>Shows / updates the card (outage.set); clear removes it. Best effort: an older bridge has no card.</summary>
    public static async Task<string?> SetCardAsync(BridgeRegistry bridges, string game, string id, string phase, string? reason, string? by, bool clear, CancellationToken ct,
        DateTime? stopAt = null)
    {
        try
        {
            var p = new JObject { ["id"] = id, ["game"] = game, ["phase"] = phase, ["reason"] = reason, ["by"] = by, ["stopOnly"] = true };
            if (stopAt is { } at) p["stopAt"] = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            if (clear) p["clear"] = true;
            var (_, r) = await bridges.CallAsync(game, "outage.set", p, ct);
            return r["error"] != null ? $"outage.set refused: {r["error"]} {r["message"]}" : null;
        }
        catch (ModelContextProtocol.McpException ex) { return $"no in-game card (outage.set failed: {ex.Message})"; }
    }

    private static async Task RunAsync(BridgeRegistry bridges, string game, string id)
    {
        using var stop = new CancellationTokenSource();
        var ct = stop.Token;
        var bridge = bridges.Bridges.FirstOrDefault(b => b.Game == game) ?? bridges.Bridges.FirstOrDefault(b => b.Game == "auto");
        if (bridge == null) { Console.Error.WriteLine($"[Outage] {id}: no bridge configured for {game}"); return; }
        long since = long.MaxValue;
        var started = DateTime.UtcNow;
        while (true)
        {
            var board = IncidentStore.Read();
            var inc = IncidentLogic.Find(board, id);
            var info = inc != null ? OutageLogic.Info(inc) : null;
            if (inc == null || !inc.IsOpen || info == null)
            {
                // Resolved by someone else (incident_resolve: the requester cancelled): take the card down if it may still show.
                if (info?.Phase is "announced" or "holding" or "cancelled" || info is { Phase: "down", StoppedAt: null }) await SetCardAsync(bridges, game, id, "cancelled", info.Reason, info.RequestedByLabel, true, ct);
                return;
            }
            // Another server drives it, or another worker of this session took over.
            if (OutageLogic.DrivenElsewhere(inc, SessionIdentity.Pid, Environment.ProcessId, p => DeployIncidents.PidAlive(p, null)) != null) return;
            var now = DateTime.UtcNow;
            switch (info.Phase)
            {
                case "announced":
                case "holding":
                {
                    if (since == long.MaxValue)
                    {
                        try { var (_, head) = await bridges.CallAsync(game, "pubsub.events", new JObject { ["since"] = long.MaxValue, ["limit"] = 1 }, ct); since = head["seq"]?.Value<long>() ?? 0; }
                        catch (ModelContextProtocol.McpException) { since = 0; }
                    }
                    var (answer, next) = await AnswerAsync(bridges, game, id, since, ct);
                    since = next;
                    var (busy, ready, stateAnswer, _) = await ReadinessAsync(bridges, game, id, info.RequestedBy, ct);
                    answer ??= stateAnswer;
                    IncidentStore.Update(f => IncidentLogic.Find(f, id) is { IsOpen: true } i && OutageLogic.SetParticipants(i, answer, busy, ready, DateTime.UtcNow));
                    var step = OutageLogic.Decide(info, answer, busy.Count, null, null, now);
                    if (step == OutageStep.None && now - (info.AnnouncedAt ?? started) > HoldMax) step = OutageStep.Cancel;
                    switch (step)
                    {
                        case OutageStep.Hold:
                            IncidentStore.Update(f => IncidentLogic.Find(f, id) is { IsOpen: true } i && OutageLogic.SetPhase(i, "holding", SessionIdentity.Label, DateTime.UtcNow));
                            await SetCardAsync(bridges, game, id, "holding", info.Reason, info.RequestedByLabel, false, ct);
                            break;
                        case OutageStep.Cancel:
                        {
                            var why = (answer ?? info.Answer) == "notNow" ? "cancelled: the user pressed Not now" : $"cancelled: expired after {HoldMax.TotalHours:0} h without the user's Restart now";
                            IncidentStore.Update(f => IncidentLogic.Find(f, id) is { IsOpen: true } i && OutageLogic.SetPhase(i, "cancelled", SessionIdentity.Label, DateTime.UtcNow, why));
                            await SetCardAsync(bridges, game, id, "cancelled", info.Reason, info.RequestedByLabel, true, ct);
                            Console.Error.WriteLine($"[Outage] {id}: {why}");
                            return;
                        }
                        case OutageStep.Countdown:
                        {
                            // The user's Restart now: phase down with stopAt; the card counts down with Cancel (outage.answer notNow).
                            var stopAt = DateTime.UtcNow + OutageLogic.StopCountdown;
                            IncidentStore.Update(f => IncidentLogic.Find(f, id) is { IsOpen: true } i && OutageLogic.BeginCountdown(i, SessionIdentity.Label, DateTime.UtcNow, stopAt));
                            await SetCardAsync(bridges, game, id, "down", info.Reason, info.RequestedByLabel, false, ct, stopAt);
                            Console.Error.WriteLine($"[Outage] {id}: Restart now: the HUD stops at {stopAt:HH:mm:ss}Z unless Cancel");
                            break;
                        }
                    }
                    break;
                }
                case "down" when info.StoppedAt == null && info.StopAt != null:
                {
                    // Counting down to stopAt: Cancel on the card (outage.answer notNow) still cancels, re-checked at stopAt itself.
                    if (info.StopAt.Value > now)
                    {
                        // Sleep to stopAt, then go round again: the board is re-read (resolved / cancelled / another driver / Cancel recorded).
                        try { await Task.Delay(info.StopAt.Value - now + TimeSpan.FromMilliseconds(100), ct); } catch (OperationCanceledException) { return; }
                        continue;
                    }
                    if (since == long.MaxValue) since = 0;
                    var (answer, next) = await AnswerAsync(bridges, game, id, since, ct);
                    since = next;
                    if (answer == null)
                    {
                        var (_, _, stateAnswer, _) = await ReadinessAsync(bridges, game, id, info.RequestedBy, ct);
                        if (stateAnswer == "notNow") answer = stateAnswer;
                    }
                    var step = OutageLogic.Decide(info, answer, 0, null, null, now);
                    if (step == OutageStep.Cancel)
                    {
                        const string why = "cancelled: the user pressed Cancel during the stop countdown";
                        IncidentStore.Update(f => IncidentLogic.Find(f, id) is { IsOpen: true } i && OutageLogic.SetPhase(i, "cancelled", SessionIdentity.Label, DateTime.UtcNow, why));
                        await SetCardAsync(bridges, game, id, "cancelled", info.Reason, info.RequestedByLabel, true, ct);
                        Console.Error.WriteLine($"[Outage] {id}: {why}");
                        return;
                    }
                    if (step != OutageStep.Stop) break;
                    // Last look at the board right before the stop: the answer calls above took time.
                    if (OutageLogic.WhyNotStop(IncidentLogic.Find(IncidentStore.Read(), id)) is { } whyNot)
                    {
                        Console.Error.WriteLine($"[Outage] {id}: no stop at stopAt: {whyNot}");
                        continue;   // the loop head takes the card down / follows the new phase
                    }
                    var (code, output) = await Tools.SessionTools.RunRestartScriptAsync(game, info.RequestedByLabel ?? SessionIdentity.Label, info.Reason ?? "user restart",
                        60, stopOnly: true, force: false, kind: "user", outageId: id, ct);   // -Outage skips the coordination by itself
                    if (code == 0)
                    {
                        IncidentStore.Update(f => IncidentLogic.Find(f, id) is { IsOpen: true } i && OutageLogic.MarkStopped(i, DateTime.UtcNow));
                        Console.Error.WriteLine($"[Outage] {id}: HUD stopped, waiting for the user to restart game + HUD");
                    }
                    else
                    {
                        var why = $"cancelled: restart-hud.ps1 -StopOnly failed (exit {code}): {string.Join(" | ", output.TakeLast(3))}";
                        IncidentStore.Update(f => IncidentLogic.Find(f, id) is { IsOpen: true } i && OutageLogic.SetPhase(i, "cancelled", SessionIdentity.Label, DateTime.UtcNow, why));
                        await SetCardAsync(bridges, game, id, "cancelled", info.Reason, info.RequestedByLabel, true, ct);
                        Console.Error.WriteLine($"[Outage] {id}: {why}");
                        return;
                    }
                    break;
                }
                case "down":
                case "waitingGame":
                {
                    var state = HudStateReader.Read(bridge.BridgeDir);
                    var hudDir = Path.GetDirectoryName(Path.GetFullPath(bridge.BridgeDir).TrimEnd('\\', '/')) ?? bridge.BridgeDir;
                    var (alive, _, _) = BridgeDiagnosis.FindHud(hudDir);
                    var step = OutageLogic.Decide(info, null, 0, state, alive, now);
                    if (step == OutageStep.WaitingGame)
                        IncidentStore.Update(f => IncidentLogic.Find(f, id) is { IsOpen: true } i
                                                  && OutageLogic.SetPhase(i, "waitingGame", SessionIdentity.Label, DateTime.UtcNow, $"(phase {state?.Phase})"));
                    else if (step == OutageStep.Back)
                    {
                        IncidentStore.Update(f => IncidentLogic.Find(f, id) is { IsOpen: true } i && OutageLogic.SetPhase(i, "back", SessionIdentity.Label, DateTime.UtcNow));
                        // The restart's own incidents (the stop's bridge-down) are over too.
                        IncidentStore.ResolveKeys(IncidentAuto.BridgeKeys(game), $"the {game} HUD is back and the game is in after the user's restart ({id})");
                        Console.Error.WriteLine($"[Outage] {id}: back ({state?.Area})");
                        return;
                    }
                    break;
                }
                default:
                    return;
            }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The monitor's half: adopt open outages whose owner process is gone (re-stamp ownerPid) and run them here.</summary>
    public static void AdoptOrphans(BridgeRegistry bridges)
    {
        var board = IncidentStore.Read();
        foreach (var i in board.Incidents.Where(i => i.IsOpen && i.Kind == "outage").ToList())
        {
            if (IsRunning(i.Id)) continue;
            var owner = i.OwnerPid;
            if (owner == SessionIdentity.Pid)
            {
                // Ours: resume unless another worker of this session (before or after a hot swap) still runs it.
                if (RunnerPid(i) is { } runner && runner != Environment.ProcessId && DeployIncidents.PidAlive(runner, null) != false) continue;
                Start(bridges, i.Scope, i.Id);
                continue;
            }
            if (owner != null && DeployIncidents.PidAlive(owner.Value, null) != false) continue;
            var adopted = IncidentStore.Update(f =>
            {
                var cur = IncidentLogic.Find(f, i.Id);
                if (cur is not { IsOpen: true } || cur.OwnerPid != owner) return false;   // raced with another server
                cur.Set("ownerPid", SessionIdentity.Pid);
                cur.Set("adoptedBy", SessionIdentity.Label);
                return true;
            });
            if (!adopted) continue;
            Console.Error.WriteLine($"[Outage] adopted {i.Id} (owner pid {owner?.ToString() ?? "none"} gone)");
            Start(bridges, i.Scope, i.Id);
        }
    }
}
