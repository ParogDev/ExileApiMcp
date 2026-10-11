// Unit checks for the incident board (Incidents/): parsing, raise / respond / resolve / prune, the bridge diagnosis from
// canned facts and netsh output, deploy orphan judgement, and the store's mutex + atomic writes against a temp folder.
// No test framework: each Check prints pass/FAIL; the exit code is the number of failures.
using ExileApiMcp.Incidents;
using Newtonsoft.Json.Linq;

var failures = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "pass" : "FAIL")}  {name}{(ok || detail == null ? "" : "  -- " + detail)}");
    if (!ok) failures++;
}
void Throws<T>(string name, Action a) where T : Exception
{
    try { a(); Check(name, false, "no exception"); }
    catch (T) { Check(name, true); }
    catch (Exception ex) { Check(name, false, ex.GetType().Name + ": " + ex.Message); }
}

var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

// ── Parsing: the shared contract, unknown fields kept ──
{
    const string json = """
    { "rev": 7, "writer": "incident.ps1", "incidents": [ {
        "id": "i-20261011115500-ab12", "kind": "bridge-down", "scope": "poe2", "title": "PoE2 bridge down",
        "detail": "no token", "fix": null, "raisedBy": "route-act2", "raisedAt": "2026-10-11T11:55:00Z",
        "eta": null, "state": "open", "resolvedAt": null, "resolvedBy": null, "key": "bridge-down:poe2",
        "hookSeen": ["a", "b"],
        "responses": [ { "who": "route-act2", "status": "blocked", "note": "waiting", "until": "2026-10-11T12:10:00.000Z", "at": "2026-10-11T11:56:00+00:00", "via": "hook" } ] } ] }
    """;
    var f = IncidentLogic.Parse(json);
    Check("parse: rev and incident", f.Rev == 7 && f.Incidents.Count == 1 && f.Incidents[0].Key == "bridge-down:poe2");
    Check("parse: times are UTC", f.Incidents[0].RaisedAt == new DateTime(2026, 10, 11, 11, 55, 0, DateTimeKind.Utc)
                                  && f.Incidents[0].Responses[0].Until == new DateTime(2026, 10, 11, 12, 10, 0, DateTimeKind.Utc)
                                  && f.Incidents[0].Responses[0].At == new DateTime(2026, 10, 11, 11, 56, 0, DateTimeKind.Utc));
    // Read back as raw text values (JObject.Parse would turn ISO strings into DateTime tokens).
    var back = Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>(IncidentJson.Serialize(f), IncidentJson.Settings)!;
    Check("round trip: top-level unknown field kept", back["writer"]?.ToString() == "incident.ps1");
    Check("round trip: incident unknown field kept", back["incidents"]![0]!["hookSeen"] is JArray { Count: 2 });
    Check("round trip: response unknown field kept", back["incidents"]![0]!["responses"]![0]!["via"]?.ToString() == "hook");
    Check("round trip: camelCase and nulls written", back["incidents"]![0]!["raisedBy"] != null && back["incidents"]![0]!.Value<JObject>()!.ContainsKey("fix"));
    Check("round trip: ISO UTC times", back["incidents"]![0]!["raisedAt"]!.ToString() == "2026-10-11T11:55:00Z", back["incidents"]![0]!["raisedAt"]!.ToString());
    Check("parse: empty text is an empty board", IncidentLogic.Parse("").Incidents.Count == 0 && IncidentLogic.Parse("  ").Rev == 0);
    Throws<Newtonsoft.Json.JsonException>("parse: partial file throws (reader retries)", () => IncidentLogic.Parse("{ \"rev\": 3, \"incidents\": [ { \"id\": "));
    var bad = IncidentLogic.Parse("""{ "rev": 1, "incidents": [ { "id": "x", "kind": "custom", "scope": "machine", "title": "t", "raisedAt": "not a time", "state": "open", "key": "k" } ] }""");
    Check("parse: an unreadable time doesn't fail the file", bad.Incidents[0].RaisedAt == null);
}

// ── Raise / respond / resolve / prune ──
{
    var f = new IncidentFile();
    var (a, created) = IncidentLogic.Raise(f, new IncidentDraft { Kind = "bridge-down", Scope = "poe2", Title = "PoE2 bridge down", Detail = "x" }, "s1", now);
    Check("raise: new", created && a.Key == "bridge-down:poe2" && a.Id.StartsWith("i-20261011120000-") && a.Id.Length == "i-20261011120000-abcd".Length);
    var (b, created2) = IncidentLogic.Raise(f, new IncidentDraft { Kind = "bridge-down", Scope = "poe2", Title = "PoE2 bridge down", Detail = "y" }, "s2", now.AddMinutes(1));
    Check("raise: same key updates, no duplicate", !created2 && b.Id == a.Id && f.Incidents.Count == 1 && b.Detail == "y" && b.RaisedBy == "s1");
    Check("raise: ports-reserved key is machine-wide", new IncidentDraft { Kind = "ports-reserved", Scope = "machine" }.EffectiveKey == "ports-reserved:machine");
    Check("raise: custom key from the title", new IncidentDraft { Kind = "custom", Scope = "machine", Title = "Docker is down!" }.EffectiveKey == "custom:docker is down!");
    Throws<ArgumentException>("raise: unknown kind refused", () => IncidentLogic.Raise(f, new IncidentDraft { Kind = "oops", Scope = "poe2", Title = "t" }, "s", now));
    Throws<ArgumentException>("raise: unknown scope refused", () => IncidentLogic.Raise(f, new IncidentDraft { Kind = "custom", Scope = "poe3", Title = "t" }, "s", now));

    IncidentLogic.Respond(a, "s2", "blocked", "need the HUD", now.AddMinutes(10), now);
    IncidentLogic.Respond(a, "S2", "waiting", "paused", null, now);
    Check("respond: one response per session (replaced)", a.Responses.Count == 1 && a.Responses[0].Status == "waiting");
    Throws<ArgumentException>("respond: bad status refused", () => IncidentLogic.Respond(a, "s3", "maybe", null, null, now));

    Check("find: by key and by id", IncidentLogic.Find(f, "bridge-down:poe2") == a && IncidentLogic.Find(f, a.Id) == a);
    Check("resolve: first time", IncidentLogic.Resolve(a, "s1", "back", now.AddMinutes(2)) && a.State == "resolved" && a.Get("resolution")?.ToString() == "back");
    Check("resolve: second time is a no-op", !IncidentLogic.Resolve(a, "s9", null, now.AddMinutes(3)) && a.ResolvedBy == "s1");
    var (c, created3) = IncidentLogic.Raise(f, new IncidentDraft { Kind = "bridge-down", Scope = "poe2", Title = "again" }, "s1", now.AddMinutes(5));
    Check("raise: after resolve, the key opens a new incident", created3 && c.Id != a.Id && f.Incidents.Count == 2);
    Check("prune: resolved kept for 7 days", IncidentLogic.Prune(f, now.AddDays(6)) == 0 && f.Incidents.Count == 2);
    Check("prune: resolved dropped after 7 days, open kept", IncidentLogic.Prune(f, now.AddDays(8)) == 1 && f.Incidents.Single() == c);

    IncidentLogic.Raise(f, new IncidentDraft { Kind = "ports-reserved", Scope = "machine", Title = "ports", Fix = "run netsh" }, "s1", now);
    IncidentLogic.Raise(f, new IncidentDraft { Kind = "hud-restart", Scope = "poe1", Title = "poe1 restart", Eta = now.AddMinutes(1) }, "s1", now);
    var s = IncidentLogic.Summary(f, "poe2", now);
    Check("summary: own scope + machine, not the other game", s.Contains("again") && s.Contains("ports") && !s.Contains("poe1 restart") && s.Contains("fix: run netsh"), s);
    Check("summary: nothing open is empty", IncidentLogic.Summary(new IncidentFile(), "poe2", now) == "");
}

// ── netsh and the diagnosis ──
const string netsh = """

Protocol tcp Port Exclusion Ranges

Start Port    End Port
----------    --------
      1086        1185
      5357        5357
     50000       50059     *
     50882       51081

* - Administered port exclusions.

""";
{
    var ranges = BridgeDiagnosis.ParseExcludedRanges(netsh);
    Check("netsh: four ranges", ranges.Count == 4 && ranges[3] == new BridgeDiagnosis.PortRange(50882, 51081), string.Join(",", ranges));
    Check("netsh: all 10 bridge ports blocked", BridgeDiagnosis.BlockedBridgePorts(ranges).Count == 10);
    Check("netsh: nothing blocked without the WinNAT range", BridgeDiagnosis.BlockedBridgePorts(ranges.Take(3)).Count == 0);
    Check("netsh: '*' marks administered ranges", ranges[2].Administered && !ranges[3].Administered);
    const string fixedMachine = "Start Port    End Port\r\n----------    --------\r\n     50000       50059     *\r\n     50900       50910     *\r\n\r\n* - Administered port exclusions.\r\n";
    Check("netsh: the applied fix (administered 50900-50910) blocks nothing", BridgeDiagnosis.BlockedBridgePorts(BridgeDiagnosis.ParseExcludedRanges(fixedMachine)).Count == 0);
    Check("netsh: German output parses too", BridgeDiagnosis.ParseExcludedRanges("Protokoll tcp Portausschlussbereiche\r\n\r\nStartport    Endport\r\n----------    --------\r\n     50900       50902\r\n").Count == 1);

    BridgeDiagnosis.Facts Base() => new()
    {
        Game = "poe2", BridgeDir = @"C:\hud\claude-bridge", HudDir = @"C:\hud", HudRunning = true, HudPid = 42,
        HudStartedAt = now.AddMinutes(-5), TokenExists = true, TokenWrittenAt = now.AddMinutes(-4), Port = 50900, Excluded = [], Now = now,
        Probe = BridgeDiagnosis.Probe.Accepted,
    };

    var f1 = Base(); f1.Excluded = ranges; f1.TokenExists = false; f1.Probe = BridgeDiagnosis.Probe.NotTried;
    var v1 = BridgeDiagnosis.Diagnose(f1);
    Check("diagnose: ports reserved (the WinNAT case)", v1 is { Kind: "ports-reserved", Scope: "machine", Key: "ports-reserved:machine" } && v1.Fix!.Contains("net stop winnat")
        && v1.Fix.Contains("startport=50900 numberofports=11") && v1.Detail.Contains("50882-51081"), v1?.Title + " / " + v1?.Detail);

    var f2 = Base(); f2.HudRunning = false;
    var v2 = BridgeDiagnosis.Diagnose(f2);
    Check("diagnose: HUD not running", v2 is { Kind: "bridge-down", Cause: "hud-not-running", Key: "bridge-down:poe2" } && v2.Detail.Contains("Loader.exe"), v2?.Title);

    var f3 = Base(); f3.HudRunning = false; f3.RestartPhase = "stopping"; f3.RestartWho = "route-act2"; f3.RestartReason = "bridge change";
    f3.RestartStartedAt = now.AddSeconds(-10); f3.RestartWrittenAt = now.AddSeconds(-10);
    var v3 = BridgeDiagnosis.Diagnose(f3);
    Check("diagnose: restart in progress wins, with an ETA", v3 is { Kind: "hud-restart", Key: "hud-restart:poe2" } && v3.Eta == now.AddSeconds(50) && v3.Title.Contains("route-act2"), v3?.Title);

    var f4 = Base(); f4.HudRunning = false; f4.RestartPhase = "done"; f4.RestartStopOnly = true; f4.RestartWho = "me"; f4.RestartWrittenAt = now.AddMinutes(-20);
    Check("diagnose: stopped on purpose", BridgeDiagnosis.Diagnose(f4) is { Cause: "hud-stopped" });

    var f5 = Base(); f5.TokenExists = false;
    Check("diagnose: HUD up, no token", BridgeDiagnosis.Diagnose(f5) is { Cause: "token-missing" } v5 && v5.Detail.Contains("no bridge-token.txt"));

    var f6 = Base(); f6.TokenWrittenAt = now.AddMinutes(-60);
    Check("diagnose: token older than the HUD process = bridge not started this run", BridgeDiagnosis.Diagnose(f6) is { Cause: "token-missing" } v6 && v6.Detail.Contains("older than the HUD"));

    var f7 = Base(); f7.Probe = BridgeDiagnosis.Probe.Refused;
    Check("diagnose: HUD up, nothing listening", BridgeDiagnosis.Diagnose(f7) is { Cause: "not-listening" } v7 && v7.Title.Contains("50900"));

    var f8 = Base(); f8.Error = "request timed out";
    Check("diagnose: accepts but doesn't answer", BridgeDiagnosis.Diagnose(f8) is { Cause: "not-answering" });

    Check("diagnose: a live bridge is no incident", BridgeDiagnosis.Diagnose(Base()) == null);

    var f9 = Base(); f9.Excluded = [new(50900, 50900)]; f9.Port = 50901; f9.PortFromFile = true; f9.Probe = BridgeDiagnosis.Probe.Accepted; f9.Error = null;
    Check("diagnose: one reserved port the bridge moved past is no incident", BridgeDiagnosis.Diagnose(f9) == null);

    var f10 = Base(); f10.Excluded = [new(50900, 50900)]; f10.Probe = BridgeDiagnosis.Probe.Refused;
    Check("diagnose: the bridge's own port reserved", BridgeDiagnosis.Diagnose(f10) is { Kind: "ports-reserved" } v10 && v10.Title.Contains("1 of 10"), BridgeDiagnosis.Diagnose(f10)?.Title);
}

// ── Deploy orphan judgement ──
{
    Incident Deploy(int? pid, DateTime raised)
    {
        var i = new Incident { Id = "i-1", Kind = "deploy", Key = "deploy:X", RaisedBy = "s1", RaisedAt = raised, State = "open" };
        if (pid != null) i.Set("ownerPid", pid.Value);
        return i;
    }
    Check("orphan: no owner pid", DeployIncidents.Judge(Deploy(null, now), null, (_, _) => true, now).Orphaned);
    Check("orphan: owner process gone", DeployIncidents.Judge(Deploy(5, now), now, (_, _) => false, now).Orphaned);
    Check("orphan: heartbeat older than 10 min", DeployIncidents.Judge(Deploy(5, now.AddHours(-1)), now.AddMinutes(-11), (_, _) => true, now).Orphaned);
    Check("live: recent heartbeat, process alive", !DeployIncidents.Judge(Deploy(5, now.AddHours(-1)), now.AddSeconds(-30), (_, _) => true, now).Orphaned);
    Check("live: no sidecar beat yet, raised just now", !DeployIncidents.Judge(Deploy(5, now.AddMinutes(-1)), null, (_, _) => null, now).Orphaned);
    Check("orphan: this test process is alive", DeployIncidents.PidAlive(Environment.ProcessId, DateTime.UtcNow) == true);
}

// ── The store: mutex, atomic replace, rev, no-op writes, concurrency ──
{
    var dir = Path.Combine(Path.GetTempPath(), "hexile-incidents-test-" + Guid.NewGuid().ToString("N")[..8]);
    Environment.SetEnvironmentVariable("HEXILE_INCIDENT_DIR", dir);
    Environment.SetEnvironmentVariable("HEXILE_AGENT", "unit-test");
    try
    {
        Check("store: missing file reads as empty", IncidentStore.Read().Incidents.Count == 0);
        var (i, created) = IncidentStore.Raise(new IncidentDraft { Kind = "custom", Scope = "machine", Title = "test one" }, "unit-test");
        var rev1 = IncidentStore.Read().Rev;
        Check("store: raise writes rev 1", created && rev1 == 1 && File.Exists(IncidentStore.FilePath) && !File.Exists(IncidentStore.FilePath + ".tmp"));
        Check("store: incident stamped with its rev", IncidentStore.Read().Incidents[0].ChangedRev == 1);
        IncidentStore.Raise(new IncidentDraft { Kind = "custom", Scope = "machine", Title = "test one" }, "unit-test");
        Check("store: identical raise writes nothing", IncidentStore.Read().Rev == rev1);
        IncidentStore.Respond(i.Id, "blocked", "hm", null, "other");
        Check("store: respond bumps rev", IncidentStore.Read().Rev == rev1 + 1);

        var tasks = Enumerable.Range(0, 16).Select(n => Task.Run(() =>
            IncidentStore.Raise(new IncidentDraft { Kind = "custom", Scope = "machine", Title = $"parallel {n}" }, $"w{n}"))).ToArray();
        Task.WaitAll(tasks);
        var board = IncidentStore.Read();
        Check("store: 16 parallel writers, nothing lost", board.Incidents.Count == 17 && board.Rev == rev1 + 17, $"{board.Incidents.Count} incidents, rev {board.Rev}");

        IncidentStore.Resolve(i.Id, "done", "unit-test");
        Check("store: resolve", IncidentLogic.Find(IncidentStore.Read(), i.Id)!.State == "resolved");
        IncidentStore.ResolveKeys(["custom:no-such"], "x");
        Check("store: resolving a key that isn't open writes nothing", IncidentStore.Read().Rev == rev1 + 18);

        File.WriteAllText(IncidentStore.FilePath, "{ \"rev\": 3, \"incid");
        Check("store: partial file reads as the last good board", IncidentStore.Read().Incidents.Count == 17);

        IncidentStore.Beat(["i-a"], ["i-a", "i-b"]);
        IncidentStore.Beat(["i-b"], ["i-b"]);
        var beats = IncidentStore.ReadHeartbeats();
        Check("heartbeats: sidecar kept apart, closed ids dropped", beats.ContainsKey("i-b") && !beats.ContainsKey("i-a") && IncidentStore.Read().Incidents.Count == 17);
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) FAILED.");
return failures;
