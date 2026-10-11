using System.Collections.Concurrent;
using ExileApiMcp.Bridge;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Incidents;

/// <summary>
/// Incidents this server raises and resolves by itself:
///   - a bridge call that can't reach the HUD (BridgeRegistry.CallAsync) is diagnosed at the broken link
///     (<see cref="BridgeDiagnosis"/>) and raised as bridge-down / hud-restart / ports-reserved; the error the tool
///     returns ends with the open incidents for that scope;
///   - a restart-state.json that says "stopping" raises hud-restart with an ETA (whoever restarts: hud_restart or a shell);
///   - a bridge that answers again resolves its bridge-down / hud-restart incidents.
/// Never throws: the board is a help, not a dependency of any tool.
/// </summary>
public static class IncidentAuto
{
    /// <summary>A failure already on the board is re-stamped (lastSeenAt) at most this often, so failing calls don't churn rev.</summary>
    private static readonly TimeSpan Restamp = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, DateTime> LastOkCheck = new();

    public static string[] BridgeKeys(string game) => [$"bridge-down:{game}", $"hud-restart:{game}"];

    /// <summary>Diagnose, raise, and return " " + the one-line summary of open incidents for the game (or "").</summary>
    public static string BridgeFailed(BridgeClient bridge, string error)
    {
        try
        {
            if (bridge.Game is not ("poe1" or "poe2")) return Suffix(null);
            var facts = BridgeDiagnosis.Gather(bridge, error);
            if (BridgeDiagnosis.Diagnose(facts) is { } v) RaiseVerdict(v, bridge.Game);
            return Suffix(bridge.Game);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Incidents] diagnosing {bridge.Game} failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>No game given and no bridge looks up: diagnose the HUDs that run (a HUD that isn't running isn't news then),
    /// and the port reservations, which break any HUD that starts.</summary>
    public static string NoBridge(IEnumerable<BridgeClient> bridges)
    {
        try
        {
            foreach (var b in bridges.Where(b => b.Game is "poe1" or "poe2"))
            {
                var facts = BridgeDiagnosis.Gather(b, "no bridge token");
                if (facts.HudRunning == false)
                {
                    // Only the port check: a HUD nobody asked for that isn't running is not an incident.
                    facts.HudRunning = true; facts.TokenExists = true; facts.Probe = BridgeDiagnosis.Probe.Refused;
                    if (BridgeDiagnosis.Diagnose(facts) is { Kind: "ports-reserved" } pv) RaiseVerdict(pv, b.Game);
                    continue;
                }
                if (BridgeDiagnosis.Diagnose(facts) is { } v) RaiseVerdict(v, b.Game);
            }
            return Suffix(null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Incidents] diagnosing the bridges failed: {ex.Message}");
            return "";
        }
    }

    private static string Suffix(string? scope)
    {
        var s = IncidentStore.Summary(scope);
        return s.Length > 0 ? " " + s + " (incidents / incident_respond)" : "";
    }

    public static void RaiseVerdict(BridgeDiagnosis.Verdict v, string game, string? who = null)
    {
        var now = DateTime.UtcNow;
        IncidentStore.Update(f =>
        {
            var existing = IncidentLogic.OpenByKey(f, v.Key);
            if (existing != null && existing.Get("cause")?.ToString() == v.Cause && existing.Title == v.Title.Trim()
                && DeployIncidents.ParseIso(existing.Get("lastSeenAt")) is { } seen && now - seen < Restamp)
                return existing;   // known, recently confirmed: no write
            var d = new IncidentDraft
            {
                Kind = v.Kind, Scope = v.Scope, Title = v.Title, Detail = v.Detail, Fix = v.Fix, Eta = v.Eta, Key = v.Key,
                Fields = { ["cause"] = v.Cause, ["game"] = game, ["lastSeenAt"] = IsoUtcConverter.Iso(now), ["auto"] = true },
            };
            var (i, created) = IncidentLogic.Raise(f, d, who ?? Hosting.SessionIdentity.Label, now);
            // The bridge is down for one reason at a time: a newer diagnosis of the same game supersedes the other key.
            if (v.Kind is "bridge-down" or "hud-restart")
                foreach (var other in f.Incidents.Where(o => o.IsOpen && o != i && BridgeKeys(game).Contains(o.Key)).ToList())
                    IncidentLogic.Resolve(other, Hosting.SessionIdentity.Label, $"superseded: {v.Title}", now);
            if (created) Console.Error.WriteLine($"[Incidents] raised {i.Key}: {i.Title}");
            return i;
        });
    }

    /// <summary>A bridge call succeeded: resolve the game's bridge-down / hud-restart incidents (checked at most every 5 s).</summary>
    public static void BridgeOk(BridgeClient bridge, bool force = false)
    {
        if (bridge.Game is not ("poe1" or "poe2")) return;
        var now = DateTime.UtcNow;
        if (!force && LastOkCheck.TryGetValue(bridge.Game, out var last) && now - last < TimeSpan.FromSeconds(5)) return;
        LastOkCheck[bridge.Game] = now;
        try
        {
            var keys = BridgeKeys(bridge.Game);
            // While a restart is still stopping / relaunching the HUD (phase "stopping"), the old HUD (before it closes) and the
            // new one (before the script's alive check) both answer: that isn't "back" yet. Only a bridge-down goes then; the
            // restart's own incident is closed by restart-hud.ps1 / hud_restart, or here once the phase moved on.
            if (RestartStopping(bridge)) keys = [$"bridge-down:{bridge.Game}"];
            var open = IncidentStore.Read().Incidents.Where(i => i.IsOpen && keys.Contains(i.Key)).ToList();
            if (open.Count == 0) return;
            var note = $"the {bridge.Game} bridge answers again" + (open.FirstOrDefault(i => i.Kind == "hud-restart")?.RaisedAt is { } at
                ? $" ({(int)(now - at).TotalSeconds} s after the restart was raised)" : "");
            IncidentStore.ResolveKeys(keys, note);
            Console.Error.WriteLine($"[Incidents] resolved {string.Join(", ", open.Select(i => i.Key))}: {note}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Incidents] resolving {bridge.Game} failed: {ex.Message}"); }
    }

    /// <summary>restart-state.json says phase "stopping", written in the last 3 minutes.</summary>
    public static bool RestartStopping(BridgeClient bridge)
    {
        try
        {
            var path = Path.Combine(bridge.BridgeDir, "restart-state.json");
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(3)) return false;
            return HudRestartState.Read(bridge)?.Phase == "stopping";
        }
        catch { return false; }
    }

    /// <summary>restart-state.json says a restart is stopping the HUD: raise hud-restart (once per restart) with an ETA of about 60 s.</summary>
    public static Incident? CheckRestartState(BridgeClient bridge, DateTime? startedAfter = null)
    {
        try
        {
            if (bridge.Game is not ("poe1" or "poe2")) return null;
            var path = Path.Combine(bridge.BridgeDir, "restart-state.json");
            if (!File.Exists(path)) return null;
            var written = File.GetLastWriteTimeUtc(path);
            if (DateTime.UtcNow - written > TimeSpan.FromMinutes(3)) return null;
            var o = JObject.Parse(File.ReadAllText(path));
            if (o["phase"]?.ToString() != "stopping") return null;
            var r = HudRestartState.Read(bridge);
            if (r == null || (startedAfter != null && r.StartedAt < startedAfter.Value.AddSeconds(-1))) return null;
            var stopOnly = o["stopOnly"]?.Type == JTokenType.Boolean && o["stopOnly"]!.Value<bool>();
            var G = bridge.Game == "poe1" ? "PoE1" : "PoE2";
            var key = stopOnly ? $"bridge-down:{bridge.Game}" : $"hud-restart:{bridge.Game}";
            var board = IncidentStore.Read();
            var open = board.Incidents.FirstOrDefault(i => i.IsOpen && i.Key == key);
            if (open != null) return open;
            // Once per restart: an incident for this restart that was resolved already (the script's, or ours) stays resolved.
            if (board.Incidents.Any(i => i.Key == key && i.RaisedAt >= r.StartedAt.AddSeconds(-5))) return null;
            var v = new BridgeDiagnosis.Verdict
            {
                Kind = stopOnly ? "bridge-down" : "hud-restart", Scope = bridge.Game, Cause = stopOnly ? "hud-stopped" : "restarting",
                Title = stopOnly ? $"{G} HUD is being stopped by {r.Who}" : $"{G} HUD restart by {r.Who}",
                Detail = $"{(stopOnly ? "Stop" : "Restart")} started {r.StartedAt:HH:mm:ss}Z" + (r.Reason != null ? $" ({r.Reason})" : "") +
                         (r.GrantedBy == "user" && r.Over != null ? $"; the user pressed Restart now over: {r.Over}" : r.GrantedBy is { } g && g != "auto" ? $"; granted: {g}" : "") +
                         ". Bridge calls wait for it to come back; measurements and recordings running now are cut.",
                Fix = stopOnly ? $"Start it again when the user agrees: hud_restart game={bridge.Game}." : "Nothing: wait (about a minute).",
                Eta = stopOnly ? null : r.StartedAt.AddSeconds(60),
            };
            RaiseVerdict(v, bridge.Game, r.Who);
            return IncidentStore.Read().Incidents.FirstOrDefault(i => i.IsOpen && i.Key == key);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Incidents] restart-state check for {bridge.Game} failed: {ex.Message}");
            return null;
        }
    }
}
