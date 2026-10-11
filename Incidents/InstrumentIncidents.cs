using ExileApiMcp.Bridge;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Incidents;

/// <summary>
/// The bridge's Harmony patches (profile_plugin, pipeline_trace, hud_health_report) live only as long as their
/// measurement (bridge Shared/Instrumentation.cs). Its instrumentation block (instrument.state, and in session.list /
/// hud.plugins) says what is still patched with no measurement running (leftover) and its cleanup events. Here:
///   - leftover > 0: incident instrument:&lt;game&gt; stays open (a plugin may be running a broken patched copy) until
///     the bridge reports nothing left;
///   - an orphan the bridge found and restored by itself (orphaned / residue / expired / restore_failed event): raised
///     and resolved at once, so every session hears that a measurement leaked patches, once per bridge event.
/// Also the cancel helper the measurement tools call when their caller gives up: the bridge then ends the patch set now
/// instead of at the end of its duration. Never throws: the board is a help, not a dependency.
/// </summary>
public static class InstrumentIncidents
{
    public static string Key(string game) => $"instrument:{game}";
    private static readonly string[] Recorded = ["orphaned", "residue", "expired", "restore_failed", "sweep_failed"];

    public static void Check(string? game, JToken? instrumentation)
    {
        if (game is not ("poe1" or "poe2") || instrumentation is not JObject s) return;
        try
        {
            var key = Key(game);
            var leftover = s["leftover"]?["count"]?.Value<int>() ?? 0;
            var now = DateTime.UtcNow;
            if (leftover > 0)
            {
                var methods = string.Join(", ", (s["leftover"]?["methods"] as JArray ?? []).Take(8).Select(m => m.ToString()));
                IncidentStore.Raise(new IncidentDraft
                {
                    Kind = "custom", Scope = game, Key = key,
                    Title = $"{leftover} HUD methods still patched with no measurement running ({game})",
                    Detail = $"The bridge's instrumentation left Harmony patches behind: {methods}. A plugin running a patched copy can throw " +
                             "(EntryPointNotFoundException) or run slower. The bridge's sweep retries every 5 s." +
                             (s["broken"] is JObject b ? $" Broken link: {b}" : ""),
                    Fix = "reload_plugin the plugin(s) named (a reload loads unpatched code); if they are HUD core methods, hud_restart.",
                    Fields = { ["auto"] = true, ["game"] = game, ["lastSeenAt"] = IsoUtcConverter.Iso(now) },
                });
                return;
            }
            if (s["lastProblem"] is JObject last && Recorded.Contains(last["kind"]?.ToString())
                && DateTime.TryParse(last["at"]?.ToString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at) && now - at < TimeSpan.FromMinutes(30))
            {
                var stamp = last["at"]!.ToString();
                IncidentStore.Update(f =>
                {
                    if (f.Incidents.Any(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase) && i.Get("problemAt")?.ToString() == stamp)) return 0;
                    var (i, _) = IncidentLogic.Raise(f, new IncidentDraft
                    {
                        Kind = "custom", Scope = game, Key = key,
                        Title = $"Measurement patches were orphaned on the {game} HUD (cleaned up)",
                        Detail = $"{last["kind"]}: {last["text"]}" + (last["who"] is { Type: JTokenType.String } w ? $" (measurement by {w})" : ""),
                        Fix = null,
                        Fields = { ["auto"] = true, ["game"] = game, ["problemAt"] = stamp, ["problemSeq"] = last["seq"] },
                    }, Hosting.SessionIdentity.Label, now);
                    IncidentLogic.Resolve(i, Hosting.SessionIdentity.Label, "the bridge restored the original code itself; nothing is left patched", now);
                    return 1;
                });
                return;
            }
            IncidentStore.ResolveKeys([key], $"the {game} bridge reports nothing left patched");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Incidents] instrumentation check ({game}) failed: {ex.Message}"); }
    }

    /// <summary>Fetch instrument.state and check it (after a measurement, or for hud_plugins). Null when the bridge can't say.</summary>
    public static async Task<JObject?> Fetch(BridgeRegistry bridges, string? game, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var (bridge, r) = await bridges.CallAsync(game, "instrument.state", new JObject(), cts.Token);
            if (r is not JObject o || o["leftover"] == null) return null;   // an older bridge: no instrument.state
            Check(bridge.Game == "auto" ? game : bridge.Game, o);
            return o;
        }
        catch { return null; }
    }

    /// <summary>The caller gave up on a measurement (cancelled tool call): end its patch set now. Best effort.</summary>
    public static async Task CancelAsync(BridgeRegistry bridges, string? game, string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await bridges.CallAsync(game, "instrument.cancel", new JObject { ["id"] = id }, cts.Token);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Instrumentation] cancelling {id} failed: {ex.Message}"); }
    }

    /// <summary>One line for a measurement result: what its end restored, loud when anything stayed patched.</summary>
    public static string? EndLine(JToken? unpatch)
    {
        if (unpatch is not JObject u) return null;
        var still = u["stillPatchedCount"]?.Value<int>() ?? 0;
        var endedBy = u["endedBy"]?.ToString();
        if (still > 0)
            return $"WARNING: {still} methods are still patched after the measurement ({string.Join(", ", (u["stillPatched"] as JArray ?? []).Take(5))}): reload_plugin them. Raised on the incident board.";
        return endedBy is null or "done" ? null : $"Ended early: {endedBy}. Its patches were removed ({u["restored"]} methods restored).";
    }
}
