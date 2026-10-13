using System.Diagnostics;
using ExileApiMcp.Tools;
using ModelContextProtocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Bridge;

/// <summary>
/// &lt;bridge dir&gt;\hud-state.json as the bridge writes it (atomically, camelCase, ISO UTC): the HUD's phase (starting |
/// menu | loading | ready | restarting | user-restart | closed), a worker-timer heartbeat every 2 s, the main thread's last
/// tick, the area before a load, the game and HUD pids and the measured startup times. Unknown fields pass through (Extra).
/// Older bridges write no file: every reader treats "missing" as unknown and keeps the behaviour from before the file.
/// </summary>
public sealed class HudStateFile
{
    public int Schema { get; set; }
    public string? Game { get; set; }
    public string? Phase { get; set; }
    public string? PrevPhase { get; set; }
    public DateTime? Since { get; set; }
    public long Seq { get; set; }
    public DateTime? Heartbeat { get; set; }
    public DateTime? TickAt { get; set; }
    public bool InGame { get; set; }
    public bool Loading { get; set; }
    public string? LastArea { get; set; }
    public string? Area { get; set; }
    public DateTime? AreaChangedAt { get; set; }
    public long? LastLoadMs { get; set; }
    public int? GamePid { get; set; }
    public int? HudPid { get; set; }
    public bool ListenerUp { get; set; }
    public int? Port { get; set; }
    public double? TypicalStartupSec { get; set; }
    public List<double>? RecentStartupSec { get; set; }
    public string? CloseReason { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Extra { get; set; }

    /// <summary>In game for "back" decisions: inGame, or phase ready.</summary>
    [JsonIgnore] public bool IsInGame => InGame || Phase == "ready";
}

/// <summary>Reads hud-state.json. Never throws: missing or unreadable is null (unknown).</summary>
public static class HudStateReader
{
    public const string FileName = "hud-state.json";

    public static string PathIn(string bridgeDir) => Path.Combine(bridgeDir, FileName);

    public static HudStateFile? Read(string bridgeDir)
    {
        var path = PathIn(bridgeDir);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return Parse(sr.ReadToEnd());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (attempt == 0) Thread.Sleep(40);   // the bridge replaces it atomically; a sharing clash is brief
            }
        }
        return null;
    }

    /// <summary>The file's text as a state; empty or malformed text throws JsonException (Read turns that into null).</summary>
    public static HudStateFile? Parse(string json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonConvert.DeserializeObject<HudStateFile>(json, Incidents.IncidentJson.Settings);
}

/// <summary>The facts a diagnosis is made from (Gather fills them from the machine; tests pass them in).</summary>
public sealed class HudAvailabilityFacts
{
    public string Game = "";
    public DateTime Now = DateTime.UtcNow;
    /// <summary>null: no hud-state.json (an older bridge) or unreadable.</summary>
    public HudStateFile? State;
    /// <summary>hud_loading | hud_timeout (the bridge's error data.reason) | unavailable (no connection) | null.</summary>
    public string? ErrorReason;
    /// <summary>The bridge's error data.tickAgeMs (main thread silence), when it sent one.</summary>
    public long? TickAgeMs;
    /// <summary>null: unknown.</summary>
    public bool? GameAlive, HudAlive;
    public DateTime? HudStartedAt;
    /// <summary>restart-state.json says an agent/script restart is stopping the HUD (written in the last 3 min).</summary>
    public bool RestartUnderWay;
    /// <summary>An open outage incident for this game (announced..waitingGame).</summary>
    public string? OutageId, OutagePhase;
}

/// <summary>
/// "Not now" (normal: a load screen, the HUD starting or restarting, the user's planned restart, the game at the menu) vs
/// "broken" (game gone, HUD gone with nothing announced, a load stuck over a minute, the HUD frozen). Pure: facts in,
/// verdict out; null when the facts say nothing beyond today's error (no file, or the HUD is ready and answering).
/// </summary>
public static class HudAvailabilityLogic
{
    public static readonly TimeSpan StuckLoading = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HeartbeatStale = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MainThreadFrozen = TimeSpan.FromSeconds(5);
    public static readonly string[] OutageHoldingPhases = ["announced", "holding", "down", "waitingGame"];

    public static HudAvailability? Diagnose(HudAvailabilityFacts f)
    {
        var s = f.State;
        var G = f.Game == "poe1" ? "PoE1" : f.Game == "poe2" ? "PoE2" : f.Game;
        var outage = f.OutageId != null && (f.OutagePhase == null || OutageHoldingPhases.Contains(f.OutagePhase));
        if (outage && (s == null || f.HudAlive == false || !s.IsInGame || s.Phase is not "ready"))
            return NotNow(f, "user-restart", s,
                $"the user is restarting {G} (game and HUD) by hand: outage {f.OutageId} is at phase {f.OutagePhase ?? "?"}; it resolves by itself once the game is back in",
                $"incident_wait (outage {f.OutageId} resolves when the game is back)", outageId: f.OutageId);
        if (s == null) return null;   // an older bridge: no file, keep today's behaviour

        var since = s.Since is { } t ? Math.Max(0, (f.Now - t).TotalSeconds) : (double?)null;
        var typical = s.TypicalStartupSec;
        if (f.RestartUnderWay && s.Phase != "ready")
            return NotNow(f, "restarting", s, $"an agent's coordinated restart of the {G} HUD is under way (restart-state.json phase stopping)",
                "incident_wait (the hud-restart incident resolves when it is back)", expected: typical);

        if (f.HudAlive == false)
        {
            if (s.Phase == "restarting" && since < 180)
                return NotNow(f, "restarting", s, $"the {G} HUD closed for a restart {Sec(since)} ago (hud-state.json phase restarting); the new one is starting",
                    "incident_wait (the hud-restart incident resolves when it is back)", expected: typical);
            if (s.Phase == "user-restart")
                return NotNow(f, "user-restart", s, $"the {G} HUD closed for the user's planned restart {Sec(since)} ago (hud-state.json phase user-restart); the user starts it again",
                    "incident_wait (the outage incident resolves when the game is back)");
            return Broken(f, "hudGone", s,
                $"no Loader.exe of the {G} HUD runs and nothing announced a restart (hud-state.json phase {s.Phase ?? "?"}{(s.CloseReason != null ? $", closeReason {s.CloseReason}" : "")}, {Sec(since)} ago)",
                $"the user starts the {G} HUD, or hud_restart game={f.Game} (it launches a HUD that isn't running)");
        }

        // A HUD process newer than the file's last heartbeat: plugins are compiling, the bridge hasn't initialised yet.
        if (f.HudStartedAt is { } hs && (s.Heartbeat == null || hs > s.Heartbeat.Value))
        {
            var inSec = Math.Max(0, (f.Now - hs).TotalSeconds);
            return NotNow(f, "starting", s,
                $"the {G} HUD is starting, plugins compiling{(typical != null ? $", typically {typical:0} s" : "")}, {inSec:0} s in (process newer than the bridge's last heartbeat)",
                "pubsub_wait kinds=hud.phase, or retry when it is up", sinceOverride: inSec, expected: typical is { } ty ? Math.Max(1, ty - inSec) : null);
        }

        switch (s.Phase)
        {
            case "starting":
            case "closed" when f.HudAlive == true:
                return NotNow(f, "starting", s,
                    $"the {G} HUD is starting{(typical != null ? $", typically {typical:0} s" : "")}, {Sec(since)} in (hud-state.json phase {s.Phase})",
                    "pubsub_wait kinds=hud.phase, or retry when it is up", expected: typical is { } ty2 && since is { } sn ? Math.Max(1, ty2 - sn) : null);
            case "restarting":
                return NotNow(f, "restarting", s, $"the {G} HUD is closing for a restart (hud-state.json phase restarting, {Sec(since)} ago)",
                    "incident_wait (the hud-restart incident resolves when it is back)", expected: typical);
            case "user-restart":
                return NotNow(f, "user-restart", s, $"the {G} HUD is closing for the user's planned restart (hud-state.json phase user-restart)",
                    "incident_wait (the outage incident resolves when the game is back)");
        }

        if (f.GameAlive == false)
            return Broken(f, "gameGone", s, $"the {G} game process (pid {s.GamePid}) is gone while the HUD runs (hud-state.json phase {s.Phase ?? "?"})",
                "the user starts the game again; the HUD reattaches by itself");
        if (s.Heartbeat is { } hb && f.Now - hb > HeartbeatStale)
            return Broken(f, "frozen", s,
                $"hud-state.json's heartbeat is {(f.Now - hb).TotalSeconds:0} s old while the HUD process runs: the bridge's worker timer stopped (HUD hung, or the bridge plugin failed)",
                $"hud_health_report game={f.Game}; if it stays, hud_restart game={f.Game}");

        switch (s.Phase)
        {
            case "loading":
                if (since is { } ls && ls > StuckLoading.TotalSeconds && f.GameAlive != false)
                    return Broken(f, "stuckLoading", s,
                        $"the {G} game has been on a load screen for {ls:0} s (from {s.LastArea ?? "?"}; the last load took {Ms(s.LastLoadMs)}): stuck, not a normal load",
                        "ask the user what the game shows (a crash dialog, a disconnect, a frozen load); hud_health_report");
                return NotNow(f, "loading", s,
                    $"load screen for {Sec(since)} (from {s.LastArea ?? "?"}), normal; the last load took {Ms(s.LastLoadMs)}",
                    "observe_wait / pubsub_wait kinds=area.ready", from: s.LastArea,
                    expected: s.LastLoadMs is { } ll ? Math.Max(1, ll / 1000.0 - (since ?? 0)) : null);
            case "menu":
                return NotNow(f, "menu", s, $"the {G} game is at login / character select (hud-state.json phase menu, {Sec(since)}): back when the user enters a character",
                    "pubsub_wait kinds=hud.phase (or area.ready)");
        }

        // Ready, yet the call failed: only a frozen main thread is ours to name.
        var tickAge = f.TickAgeMs is { } ta ? TimeSpan.FromMilliseconds(ta) : s.TickAt is { } tk ? f.Now - tk : (TimeSpan?)null;
        if (f.ErrorReason == "hud_timeout" && tickAge > MainThreadFrozen)
            return Broken(f, "frozen", s,
                $"the {G} HUD's main thread hasn't ticked for {tickAge.Value.TotalSeconds:0} s while in game (phase ready): frozen, not loading",
                $"hud_health_report game={f.Game}; if it stays, hud_restart game={f.Game}");
        return null;
    }

    /// <summary>One line for tool text: "Not now (normal): ... Wait with ..." or "BROKEN: ... Fix: ...".</summary>
    public static string Text(HudAvailability a) =>
        (a.NotNow ? "Not now (normal): " : "BROKEN: ") + a.Detail + "." +
        (a.ExpectedBackSec is { } e && a.NotNow ? $" Expected back in ~{Math.Ceiling(e):0} s." : "") +
        (a.WaitWith != null ? (a.NotNow ? $" Wait with {a.WaitWith}." : $" Fix: {a.WaitWith}.") : "");

    private static HudAvailability NotNow(HudAvailabilityFacts f, string reason, HudStateFile? s, string detail, string waitWith,
        string? from = null, double? expected = null, string? outageId = null, double? sinceOverride = null) => new()
    {
        State = "notNow", Reason = reason, Phase = s?.Phase, SinceSec = Round(sinceOverride ?? SinceOf(f, s)), From = from,
        ExpectedBackSec = Round(expected), WaitWith = waitWith, Detail = detail, OutageId = outageId, Game = f.Game,
    };

    private static HudAvailability Broken(HudAvailabilityFacts f, string reason, HudStateFile s, string detail, string fix) => new()
    {
        State = "broken", Reason = reason, Phase = s.Phase, SinceSec = Round(SinceOf(f, s)), From = s.LastArea, WaitWith = fix, Detail = detail, Game = f.Game,
    };

    private static double? SinceOf(HudAvailabilityFacts f, HudStateFile? s) => s?.Since is { } t ? Math.Max(0, (f.Now - t).TotalSeconds) : null;
    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;
    private static string Sec(double? s) => s is { } x ? $"{x:0} s" : "? s";
    private static string Ms(long? ms) => ms is { } x ? $"{x / 1000.0:0.#} s" : "? s";
}

/// <summary>The machine half: facts for a bridge, the short wait across a load, and the exception tools turn into a typed result.</summary>
public static class HudAvailabilityProbe
{
    /// <summary>How long an ordinary call waits for a load screen to end before it answers "not now".</summary>
    public static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(15);

    /// <summary>The diagnosis for this bridge after <paramref name="error"/> (null error: unreachable). Null when nothing beyond the error is known.</summary>
    public static HudAvailability? Diagnose(BridgeClient bridge, BridgeException? error)
    {
        try
        {
            if (bridge.Game is not ("poe1" or "poe2")) return null;
            var f = new HudAvailabilityFacts
            {
                Game = bridge.Game, State = HudStateReader.Read(bridge.BridgeDir),
                ErrorReason = error?.Reason ?? (error is null or BridgeUnavailableException ? "unavailable" : error.Message.Contains("timed out", StringComparison.Ordinal) ? "hud_timeout" : null),
                TickAgeMs = error?.ErrorData?["tickAgeMs"]?.Type is JTokenType.Integer or JTokenType.Float ? error.ErrorData["tickAgeMs"]!.Value<long>() : null,
            };
            if (Incidents.OutageLogic.OpenFor(Incidents.IncidentStore.Read(), bridge.Game) is { } o)
            {
                f.OutageId = o.Id;
                f.OutagePhase = Incidents.OutageLogic.Info(o)?.Phase;
            }
            if (f.State == null && f.OutageId == null) return null;   // older bridge, nothing announced: today's error stands
            var hudDir = Path.GetDirectoryName(Path.GetFullPath(bridge.BridgeDir).TrimEnd('\\', '/')) ?? bridge.BridgeDir;
            var (running, _, started) = Incidents.BridgeDiagnosis.FindHud(hudDir);
            f.HudAlive = running ?? (f.State?.HudPid is { } hp ? PidAlive(hp) : null);
            f.HudStartedAt = started;
            f.GameAlive = f.State?.GamePid is { } gp && gp > 0 ? PidAlive(gp) : null;
            f.RestartUnderWay = Incidents.IncidentAuto.RestartStopping(bridge);
            return HudAvailabilityLogic.Diagnose(f);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Bridge:{bridge.Game}] availability diagnosis failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool? PidAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Waits (up to <paramref name="max"/>) for hud-state.json to say ready with a fresh heartbeat. False at once when there is
    /// no file (an older bridge) or the phase isn't one that ends by itself soon (loading, starting); false on timeout.
    /// </summary>
    public static async Task<bool> WaitReadyAsync(BridgeClient bridge, TimeSpan max, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + max;
        while (true)
        {
            var s = HudStateReader.Read(bridge.BridgeDir);
            if (s == null) return false;
            if (s.Phase == "ready" && (s.Heartbeat == null || DateTime.UtcNow - s.Heartbeat.Value < HudAvailabilityLogic.HeartbeatStale)) return true;
            if (s.Phase is not ("loading" or "starting")) return false;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(250, ct);
        }
    }

    /// <summary>The tool error for a HUD that can't answer: the diagnosis plus the bridge's own message.</summary>
    public static HudUnavailableException Exception(HudAvailability a, string? bridgeMessage, string? suffix = null) =>
        new(a, HudAvailabilityLogic.Text(a) + (bridgeMessage != null ? $" (bridge: {bridgeMessage})" : "") + (suffix ?? ""));
}

/// <summary>A bridge call failed because the HUD can't answer right now (notNow) or is broken; carries the typed diagnosis.
/// McpSetup's call filter turns it into an error result with structured content {error: hud_not_now | hud_broken, availability}.</summary>
public sealed class HudUnavailableException(HudAvailability availability, string message) : McpException(message)
{
    public HudAvailability Availability { get; } = availability;
    public bool NotNow => Availability.NotNow;
}

/// <summary>
/// For waiting tools (await_change, observe_wait, pubsub_wait, guided steps, queue waits): a "not now" failure pauses the
/// wait instead of failing it, and the time spent paused is added to the deadline, up to <see cref="Max"/> in all.
/// </summary>
public sealed class HudPause
{
    public static readonly TimeSpan Max = TimeSpan.FromMinutes(10);
    private TimeSpan _paused;

    public TimeSpan Paused => _paused;

    /// <summary>True when <paramref name="ex"/> is a "not now" this wait still has pause budget for.</summary>
    public bool Holds(Exception ex) => ex is HudUnavailableException { NotNow: true } && _paused < Max;

    /// <summary>Records a pause of <paramref name="step"/> and returns the deadline moved by it.</summary>
    public DateTime Extend(DateTime until, TimeSpan step)
    {
        _paused += step;
        return until + step;
    }
}
