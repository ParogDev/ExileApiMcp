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
    public static readonly TimeSpan CallerIdleClose = TimeSpan.FromMinutes(30);
    private long _nextSweepTicks;
    private readonly int _port;
    private readonly List<(string Game, string Dir)> _layout = new();

    public BridgeRegistry()
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("BRIDGE_PORT"), out var p) ? p : 50900;
        _port = port;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (Environment.GetEnvironmentVariable("BRIDGE_DIR") is { Length: > 0 } legacyDir)
            _layout.Add(("auto", legacyDir));
        else
        {
            _layout.Add(("poe1", Environment.GetEnvironmentVariable("POE1_BRIDGE_DIR") ?? Path.Combine(docs, "PoeHelper", "claude-bridge")));
            _layout.Add(("poe2", Environment.GetEnvironmentVariable("POE2_BRIDGE_DIR") ?? Path.Combine(docs, "halp2", "claude-bridge")));
        }
        foreach (var (game, dir) in _layout) _bridges.Add(new BridgeClient(game, dir, port));
    }

    /// <summary>The bridges for whoever is calling: the caller's own connections on the shared HTTP server when the request
    /// named one (Hosting/CallerIdentity.cs), else the process's.</summary>
    public IReadOnlyList<BridgeClient> Bridges => Hosting.CallerIdentity.Current is { } c ? ForCaller(c) : _bridges;

    private List<BridgeClient> ForCaller(Hosting.CallerIdentity c)
    {
        SweepIdleCallers();
        var set = _callers.GetOrAdd(c.Label, _ => new CallerBridges(_layout.Select(x => new BridgeClient(x.Game, x.Dir, _port, c)).ToList()));
        Interlocked.Exchange(ref set.LastUsedTicks, DateTime.UtcNow.Ticks);
        return set.Bridges;
    }

    /// <summary>Closes the connections of callers that made no call for <see cref="CallerIdleClose"/> (at most once a minute):
    /// the HUD then shows them as gone instead of connected forever. Their next call connects again.</summary>
    private void SweepIdleCallers()
    {
        var now = DateTime.UtcNow.Ticks;
        var next = Interlocked.Read(ref _nextSweepTicks);
        if (now < next || Interlocked.CompareExchange(ref _nextSweepTicks, now + TimeSpan.TicksPerMinute, next) != next) return;
        foreach (var (_, set) in _callers)
        {
            if (now - Interlocked.Read(ref set.LastUsedTicks) < CallerIdleClose.Ticks) continue;
            foreach (var b in set.Bridges.Where(b => b.IsConnected && !b.Busy))
            {
                Console.Error.WriteLine($"[Bridge:{b.Game}] closing {b.Caller?.Label}'s connection: no call for {CallerIdleClose.TotalMinutes:0} min");
                b.Close();
            }
        }
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
                    catch (BridgeException again) { throw new McpException(again.Message); }
                }
            }
            // Name the broken link and raise it on the incident board (every session hears of it), then list what's open.
            throw new McpException(ex.Message + RestartHint(bridge) + await Task.Run(() => Incidents.IncidentAuto.BridgeFailed(bridge, ex.Message), CancellationToken.None));
        }
        catch (BridgeException ex)
        {
            throw new McpException(ex.Message);
        }
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
        foreach (var b in _bridges) b.Dispose();
        foreach (var (_, set) in _callers) foreach (var b in set.Bridges) b.Dispose();
    }
}
