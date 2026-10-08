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

    public BridgeRegistry()
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("BRIDGE_PORT"), out var p) ? p : 50900;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (Environment.GetEnvironmentVariable("BRIDGE_DIR") is { Length: > 0 } legacyDir)
        {
            _bridges.Add(new BridgeClient("auto", legacyDir, port));
            return;
        }

        _bridges.Add(new BridgeClient("poe1",
            Environment.GetEnvironmentVariable("POE1_BRIDGE_DIR") ?? Path.Combine(docs, "PoeHelper", "claude-bridge"), port));
        _bridges.Add(new BridgeClient("poe2",
            Environment.GetEnvironmentVariable("POE2_BRIDGE_DIR") ?? Path.Combine(docs, "halp2", "claude-bridge"), port));
    }

    public IReadOnlyList<BridgeClient> Bridges => _bridges;

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
        if (!string.IsNullOrWhiteSpace(game))
        {
            var g = game.Trim().ToLowerInvariant();
            if (g is not ("poe1" or "poe2"))
                throw new McpException($"Unknown game '{game}'. Use 'poe1' or 'poe2'.");
            return _bridges.FirstOrDefault(b => b.Game == g)
                   ?? _bridges.FirstOrDefault(b => b.Game == "auto")
                   ?? throw new McpException($"No bridge configured for {g}.");
        }

        if (_bridges.Count == 1) return _bridges[0];

        var available = _bridges.Where(b => b.LooksAvailable).ToList();
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
        var bridge = await ResolveWaitingAsync(game, ct);
        try
        {
            await bridge.EnsureConnectedAsync(ct);
            try
            {
                return (bridge, await bridge.SendRequestAsync(method, parameters, ct));
            }
            catch (BridgeNotSentException)
            {
                // Stale connection (e.g. the HUD restarted): the request never left, so reconnect and resend once.
                await bridge.EnsureConnectedAsync(ct);
                return (bridge, await bridge.SendRequestAsync(method, parameters, ct));
            }
        }
        catch (BridgeException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>Classic bridge query: method "query" with params.type.</summary>
    public Task<(BridgeClient Bridge, JToken Result)> QueryAsync(string? game, string type, CancellationToken ct) =>
        CallAsync(game, "query", new JObject { ["type"] = type }, ct);

    public void Dispose()
    {
        foreach (var b in _bridges) b.Dispose();
    }
}
