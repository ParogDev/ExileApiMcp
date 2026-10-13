using ExileApiMcp.Hosting;
using ModelContextProtocol;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Bridge;

/// <summary>
/// Knows the HUD bridges (one per game) and picks the right one for a tool call.
///
/// Configuration (environment):
///   POE1_BRIDGE_DIR  default %USERPROFILE%\Documents\PoeHelper\claude-bridge
///   POE2_BRIDGE_DIR  default %USERPROFILE%\Documents\halp2\claude-bridge
///   BRIDGE_DIR       legacy single-HUD setting (game detected via the bridge's hello); when set,
///                    it replaces both defaults. BRIDGE_PORT is the fallback port (default 50900).
///
/// Tools take an optional "game" argument ("poe1" | "poe2"). Without it, the single HUD whose
/// bridge is up is used; if both are up the caller must choose.
/// </summary>
public sealed class BridgeRegistry : IDisposable
{
    public const string GameParamDescription =
        "Which game's HUD to query: 'poe1' (ExileApi) or 'poe2' (ExileCore2). Optional when only one HUD is running.";

    private readonly List<BridgeClient> _bridges = new();

    /// <summary>
    /// The shared HTTP server's callers (Hosting/CallerIdentity.cs), each with its own bridge connections: every connection
    /// says session.hello for one session, so this is how the HUD (hud_sessions, the guide card, leases, toasts) tells the
    /// agents behind one server apart. A caller's connections close after <see cref="CallerIdleClose"/> without a call.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CallerBridges> _callers = new(StringComparer.Ordinal);
    private sealed class CallerBridges(List<BridgeClient> bridges)
    {
        public readonly List<BridgeClient> Bridges = bridges;
        public long LastUsedTicks = DateTime.UtcNow.Ticks;
    }
    /// <summary>A caller idle this long has its connections closed. The HUD already stops counting it after 10 min (the
    /// bridge's McpHttpIdle, unless it holds a lease); the connection stays longer so its leases, card and queued asks
    /// (released when the connection goes) survive a pause between calls.</summary>
    public static readonly TimeSpan CallerIdleClose = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CallerTickEvery = TimeSpan.FromMinutes(1);
    private long _nextSweepTicks;
    private Timer? _callerTimer;
    private volatile bool _draining;
    private readonly int _port;
    private readonly List<(string Game, string Dir)> _layout = new();

    public BridgeRegistry()
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("BRIDGE_PORT"), out var p) ? p : 50900;
        _port = port;
        _layout.AddRange(Layout());
        foreach (var (game, dir) in _layout) _bridges.Add(new BridgeClient(game, dir, port));
    }

    /// <summary>The bridge folders by game, from the environment (see the class summary). Static, so code without a registry
    /// (the orphaned-deploy scan, Hud/HudSource.cs) finds the same HUDs.</summary>
    public static List<(string Game, string Dir)> Layout()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Environment.GetEnvironmentVariable("BRIDGE_DIR") is { Length: > 0 } legacyDir) return [("auto", legacyDir)];
        return
        [
            ("poe1", Environment.GetEnvironmentVariable("POE1_BRIDGE_DIR") ?? Path.Combine(docs, "PoeHelper", "claude-bridge")),
            ("poe2", Environment.GetEnvironmentVariable("POE2_BRIDGE_DIR") ?? Path.Combine(docs, "halp2", "claude-bridge")),
        ];
    }

    /// <summary>The bridges for whoever is calling: the caller's own connections on the shared HTTP server when the request
    /// named one (Hosting/CallerIdentity.cs), else the process's.</summary>
    public IReadOnlyList<BridgeClient> Bridges => Hosting.CallerIdentity.Current is { } c ? ForCaller(c) : _bridges;

    private List<BridgeClient> ForCaller(Hosting.CallerIdentity c)
    {
        // The minute tick runs from a timer (a worker that only finishes old calls after a swap gets no new ones to sweep
        // from); started by the first caller, so a stdio server never runs it.
        if (_callerTimer == null)
        {
            var t = new Timer(_ => _ = CallerTickAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (Interlocked.CompareExchange(ref _callerTimer, t, null) == null) t.Change(CallerTickEvery, CallerTickEvery);
            else t.Dispose();   // another call started it first
        }
        var set = _callers.GetOrAdd(SessionIdentity.IdOf(c), _ => new CallerBridges(_layout.Select(x => new BridgeClient(x.Game, x.Dir, _port, c)).ToList()));
        Interlocked.Exchange(ref set.LastUsedTicks, DateTime.UtcNow.Ticks);
        return set.Bridges;
    }

    /// <summary>
    /// Once a minute, per caller: one with an HTTP call still running (Hosting/CallerIdentity.cs OpenCalls: incident_wait, a
    /// long observe_wait) sends session.touch on its open connections, so the HUD keeps it present; one that made no call for
    /// <see cref="CallerIdleClose"/> (or any idle one, once the supervisor retired this worker) has its connections closed, so
    /// the HUD stops counting it. Its next call connects again under the same session id.
    /// </summary>
    private async Task CallerTickAsync()
    {
        try { await CallerTickCoreAsync(); }
        catch (Exception ex) { Console.Error.WriteLine($"[Bridge] caller tick failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task CallerTickCoreAsync()
    {
        var now = DateTime.UtcNow.Ticks;
        var next = Interlocked.Read(ref _nextSweepTicks);
        if (now < next || Interlocked.CompareExchange(ref _nextSweepTicks, now + CallerTickEvery.Ticks - TimeSpan.TicksPerSecond, next) != next) return;
        foreach (var (id, set) in _callers)
        {
            if (Hosting.CallerIdentity.OpenCalls(id) > 0)
            {
                Interlocked.Exchange(ref set.LastUsedTicks, now);
                foreach (var b in set.Bridges.Where(b => b.IsConnected))
                {
                    try
                    {
                        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await b.SendRequestAsync("session.touch", null, budget.Token);
                    }
                    catch (Exception ex) when (ex is BridgeException or OperationCanceledException or IOException) { }   // an older bridge, or it went away
                }
                continue;
            }
            if (!_draining && now - Interlocked.Read(ref set.LastUsedTicks) < CallerIdleClose.Ticks) continue;
            CloseIdle(set, _draining ? "this worker was retired by a newer build" : $"no call for {CallerIdleClose.TotalMinutes:0} min");
        }
    }

    private static void CloseIdle(CallerBridges set, string why)
    {
        foreach (var b in set.Bridges.Where(b => b.IsConnected && !b.Busy))
        {
            Console.Error.WriteLine($"[Bridge:{b.Game}] closing {b.Caller?.Label}'s connection: {why}");
            b.Close();
        }
    }

    /// <summary>
    /// The supervisor retired this worker (a newer build took over): close the connections of callers with no call running
    /// now, and of the others as their calls end (the minute tick), so the HUD doesn't keep them on the old build ("swapping")
    /// until this worker exits. Their next call goes to the new worker.
    /// </summary>
    public void Retire()
    {
        _draining = true;
        foreach (var (id, set) in _callers)
            if (Hosting.CallerIdentity.OpenCalls(id) == 0) CloseIdle(set, "this worker was retired by a newer build");
    }

    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(4);

    /// <summary>
    /// <see cref="Resolve"/>, but when no bridge is up yet, wait briefly for one: a HUD that is just
    /// (re)starting writes its bridge token within a few seconds, and failing instantly in that window
    /// sends agents off debugging a non-problem.
    /// </summary>
    private async Task<BridgeClient> ResolveWaitingAsync(string? game, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StartupGrace;
        while (true)
        {
            try { return Resolve(game); }
            catch (McpException) when (string.IsNullOrWhiteSpace(game) && _bridges.Count > 1
                                       && !_bridges.Any(b => b.LooksAvailable) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, ct);
            }
        }
    }

    /// <summary>Resolve the bridge for a call, or throw an <see cref="McpException"/> the model can act on.</summary>
    public BridgeClient Resolve(string? game)
    {
        var bridges = Bridges;   // the caller's own connections on the shared HTTP server
        if (!string.IsNullOrWhiteSpace(game))
        {
            var g = game.Trim().ToLowerInvariant();
            if (g is not ("poe1" or "poe2"))
                throw new McpException($"Unknown game '{game}'. Use 'poe1' or 'poe2'.");
            return bridges.FirstOrDefault(b => b.Game == g)
                   ?? bridges.FirstOrDefault(b => b.Game == "auto")
                   ?? throw new McpException($"No bridge configured for {g}.");
        }

        if (bridges.Count == 1) return bridges[0];

        var available = bridges.Where(b => b.LooksAvailable).ToList();
        return available.Count switch
        {
            1 => available[0],
            0 => throw new McpException(
                "No HUD bridge is running. Start the PoE1 (ExileApi) or PoE2 (ExileCore2) HUD with " +
                "'Whats An AI Bridge' enabled, then retry."),
            _ => throw new McpException(
                "Both the PoE1 and PoE2 HUD bridges look active - pass game: 'poe1' or 'poe2'."),
        };
    }

    /// <summary>Connects if needed and sends one request; bridge failures become actionable McpExceptions.</summary>
    public async Task<(BridgeClient Bridge, JToken Result)> CallAsync(string? game, string method, JObject? parameters,
        CancellationToken ct)
    {
        BridgeClient bridge;
        try { bridge = await ResolveWaitingAsync(game, ct); }
        catch (McpException ex) when (string.IsNullOrWhiteSpace(game) && !_bridges.Any(b => b.LooksAvailable))
        {
            // No HUD bridge at all: diagnose the HUDs that run (Incidents/IncidentAuto.cs) and name the open incidents.
            throw new McpException(ex.Message + await Task.Run(() => Incidents.IncidentAuto.NoBridge(_bridges), CancellationToken.None));
        }
        try
        {
            await bridge.EnsureConnectedAsync(ct);
            JToken result;
            try
            {
                result = await bridge.SendRequestAsync(method, parameters, ct);
            }
            catch (BridgeNotSentException)
            {
                // Stale connection (e.g. the HUD restarted): the request never left, so reconnect and resend once.
                await bridge.EnsureConnectedAsync(ct);
                result = await bridge.SendRequestAsync(method, parameters, ct);
            }
            catch (BridgeException loading) when (loading.IsHudLoading)
            {
                // A load screen (-32010): the bridge dropped the request unprocessed. Wait briefly for the area to come up
                // (hud-state.json phase ready) and send it once more; still loading: "not now" with the diagnosis.
                Console.Error.WriteLine($"[Bridge:{bridge.Game}] {method}: HUD on a load screen, waiting up to {HudAvailabilityProbe.LoadWait.TotalSeconds:0} s");
                if (!await HudAvailabilityProbe.WaitReadyAsync(bridge, HudAvailabilityProbe.LoadWait, ct)) throw NotNowOr(bridge, loading);
                try
                {
                    await bridge.EnsureConnectedAsync(ct);
                    result = await bridge.SendRequestAsync(method, parameters, ct);
                }
                catch (BridgeException again) when (again is not BridgeUnavailableException) { throw NotNowOr(bridge, again); }
            }
            Incidents.IncidentAuto.BridgeOk(bridge);   // resolves this game's bridge-down / hud-restart incidents (checked every 5 s at most)
            return (bridge, result);
        }
        catch (BridgeUnavailableException ex)
        {
            // A coordinated HUD restart is under way (restart-state.json): wait for the HUD to come back and send the request
            // then, instead of failing every agent's call with "retry". The request never left, so sending it later is safe.
            if (RestartComesBackBy(bridge) is DateTime until)
            {
                Console.Error.WriteLine($"[Bridge:{bridge.Game}] HUD restart under way: holding {method} until it is back (up to {(int)(until - DateTime.UtcNow).TotalSeconds} s)");
                while (DateTime.UtcNow < until)
                {
                    await Task.Delay(1000, ct);
                    try
                    {
                        await bridge.EnsureConnectedAsync(ct);
                        var back = await bridge.SendRequestAsync(method, parameters, ct);
                        Incidents.IncidentAuto.BridgeOk(bridge, force: true);
                        return (bridge, back);
                    }
                    catch (BridgeUnavailableException) { }   // not back yet (BridgeNotSentException included)
                    catch (BridgeException again) { throw NotNowOr(bridge, again); }
                }
            }
            // Name the broken link and raise it on the incident board (every session hears of it), then list what's open.
            var suffix = RestartHint(bridge) + await Task.Run(() => Incidents.IncidentAuto.BridgeFailed(bridge, ex.Message), CancellationToken.None);
            // hud-state.json tells "not now" (starting, restarting, the user's planned restart) from broken (HUD gone).
            if (await Task.Run(() => HudAvailabilityProbe.Diagnose(bridge, ex), CancellationToken.None) is { } a)
                throw HudAvailabilityProbe.Exception(a, ex.Message, suffix);
            throw new McpException(ex.Message + suffix);
        }
        catch (BridgeException ex)
        {
            throw NotNowOr(bridge, ex);
        }
    }

    /// <summary>
    /// A bridge error as the tool error: a load screen, a timeout or the main thread silent get the hud-state.json diagnosis
    /// (<see cref="HudUnavailableException"/>: not now or broken); anything else, or an older bridge without the file, stays
    /// the bridge's message as before.
    /// </summary>
    private static McpException NotNowOr(BridgeClient bridge, BridgeException ex)
    {
        if (ex.Reason is "hud_loading" or "hud_timeout" || ex.Message.Contains("timed out after", StringComparison.Ordinal))
            if (HudAvailabilityProbe.Diagnose(bridge, ex) is { } a) return HudAvailabilityProbe.Exception(a, ex.Message);
        return new McpException(ex.Message);
    }

    /// <summary>
    /// Until when a call should wait for the HUD to come back: a restart that is stopping or relaunching it (phase stopping,
    /// written in the last 3 minutes) gets 150 s from its start; one that just finished (phase done) gets 45 s for the bridge
    /// to come up. Null when no restart is under way (a HUD that is simply not running fails at once, as before).
    /// </summary>
    private static DateTime? RestartComesBackBy(BridgeClient bridge)
    {
        try
        {
            var path = Path.Combine(bridge.BridgeDir, "restart-state.json");
            if (!File.Exists(path)) return null;
            var written = File.GetLastWriteTimeUtc(path);
            var o = JObject.Parse(File.ReadAllText(path));
            var phase = o["phase"]?.ToString();
            if (o["stopOnly"]?.Value<bool>() == true) return null;   // stopped on purpose: it isn't coming back
            var started = o["startedAt"]?.Value<DateTime>().ToUniversalTime() ?? written;
            DateTime? until = phase switch
            {
                "stopping" when DateTime.UtcNow - written < TimeSpan.FromMinutes(3) => started.AddSeconds(150),
                "done" when DateTime.UtcNow - written < TimeSpan.FromSeconds(45) => written.AddSeconds(45),
                _ => null,
            };
            return until > DateTime.UtcNow ? until : null;
        }
        catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException or FormatException or InvalidCastException) { return null; }
    }

    /// <summary>
    /// A bridge that doesn't answer may be a HUD mid-restart: tools\restart-hud.ps1 writes restart-state.json in the bridge
    /// folder before stopping the HUD. If it is recent, say who is restarting and why, so the agent waits instead of debugging.
    /// </summary>
    private static string RestartHint(BridgeClient bridge)
    {
        try
        {
            var path = Path.Combine(bridge.BridgeDir, "restart-state.json");
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(3)) return "";
            var o = JObject.Parse(File.ReadAllText(path));
            var age = (int)(DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds;
            if (o["phase"]?.ToString() == "done")
                return $" The HUD was just restarted by {o["who"]} ({age} s ago{(o["reason"] != null ? $", {o["reason"]}" : "")}): its bridge comes up a few seconds after launch, retry.";
            return $" The HUD is being restarted by {o["who"]} ({(o["reason"] != null ? o["reason"] + ", " : "")}started {age} s ago): wait ~20 s and retry; don't restart it again.";
        }
        catch { return ""; }
    }

    /// <summary>Classic bridge query: method "query" with params.type.</summary>
    public Task<(BridgeClient Bridge, JToken Result)> QueryAsync(string? game, string type, CancellationToken ct) =>
        CallAsync(game, "query", new JObject { ["type"] = type }, ct);

    public void Dispose()
    {
        _callerTimer?.Dispose();
        foreach (var b in _bridges) b.Dispose();
        foreach (var (_, set) in _callers) foreach (var b in set.Bridges) b.Dispose();
    }
}
