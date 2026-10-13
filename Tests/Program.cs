// Unit checks for the incident board (Incidents/): parsing, raise / respond / resolve / prune, the bridge diagnosis from
// canned facts and netsh output, deploy orphan judgement, and the store's mutex + atomic writes against a temp folder.
// No test framework: each Check prints pass/FAIL; the exit code is the number of failures.
using ExileApiMcp.Bridge;
using ExileApiMcp.Dispatch;
using ExileApiMcp.Hosting;
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

        var revBefore = IncidentStore.Read().Rev;
        IncidentStore.ResolveKeys(["custom:parallel 1"], "after corruption");
        var repaired = IncidentLogic.Parse(File.ReadAllText(IncidentStore.FilePath));
        Check("store: a corrupt board is replaced on the next write, rev continues",
            repaired.Rev > revBefore && Directory.GetFiles(dir, "incidents.json.corrupt-*").Length == 1, $"rev {repaired.Rev} vs {revBefore}");

        File.WriteAllText(IncidentStore.FilePath, """{ "rev": 50, "incidents": [ { "id": "", "kind": "custom", "scope": "machine", "title": "a", "state": "open", "key": "k1" }, { "id": "dup", "kind": "custom", "scope": "machine", "title": "b", "state": "open", "key": "k2" }, { "id": "dup", "kind": "custom", "scope": "machine", "title": "c", "state": "open", "key": "k3" } ] }""");
        IncidentStore.Respond("k2", "ok", null, null, "unit-test");
        var fixedBoard = IncidentStore.Read();
        Check("store: empty ids repaired, duplicate ids tolerated", fixedBoard.Rev == 51 && fixedBoard.Incidents.All(x => x.Id.Length > 0) && fixedBoard.Incidents.Count == 3);

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-GB");
        IncidentStore.Beat(["i-gb"], ["i-gb"]);
        var gb = IncidentStore.ReadHeartbeats();
        System.Globalization.CultureInfo.CurrentCulture = culture;
        Check("heartbeats: read back right under another culture", gb.TryGetValue("i-gb", out var gbt) && Math.Abs((DateTime.UtcNow - gbt).TotalMinutes) < 1);

        IncidentStore.Beat(["i-a"], ["i-a", "i-b"]);
        IncidentStore.Beat(["i-b"], ["i-b"]);
        var beats = IncidentStore.ReadHeartbeats();
        Check("heartbeats: sidecar kept apart, closed ids dropped", beats.ContainsKey("i-b") && !beats.ContainsKey("i-a") && IncidentStore.Read().Rev == 51);
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}

// ── Measurement patches (bridge instrumentation): leftovers stay open, orphan cleanups are recorded once ──
{
    var dir = Path.Combine(Path.GetTempPath(), "hexile-incidents-test-" + Guid.NewGuid().ToString("N")[..8]);
    Environment.SetEnvironmentVariable("HEXILE_INCIDENT_DIR", dir);
    Environment.SetEnvironmentVariable("HEXILE_AGENT", "unit-test");
    try
    {
        JObject State(int leftover, JObject? problem = null) => new()
        {
            ["leftover"] = new JObject { ["count"] = leftover, ["methods"] = new JArray(Enumerable.Range(0, leftover).Select(n => $"Route.M{n} (whatsanaibridge.profile.x)")) },
            ["lastProblem"] = problem,
        };
        InstrumentIncidents.Check("poe2", State(2));
        var open = IncidentLogic.OpenByKey(IncidentStore.Read(), "instrument:poe2");
        Check("instrument: leftover patches raise instrument:poe2", open != null && open.Title.StartsWith("2 HUD methods") && open.Detail!.Contains("Route.M0"));
        InstrumentIncidents.Check("poe2", State(0));
        Check("instrument: nothing left resolves it", IncidentLogic.OpenByKey(IncidentStore.Read(), "instrument:poe2") == null);
        var at = DateTime.UtcNow.ToString("O");
        var orphan = new JObject { ["seq"] = 4, ["at"] = at, ["kind"] = "orphaned", ["text"] = "3 methods were still patched", ["who"] = "route-act3" };
        InstrumentIncidents.Check("poe2", State(0, orphan));
        InstrumentIncidents.Check("poe2", State(0, orphan));
        var recorded = IncidentStore.Read().Incidents.Where(i => i.Key == "instrument:poe2" && i.Get("problemAt")?.ToString() == at).ToList();
        Check("instrument: an orphan cleanup is recorded once, already resolved", recorded.Count == 1 && !recorded[0].IsOpen && recorded[0].Detail!.Contains("route-act3"));
        InstrumentIncidents.Check("poe2", State(0, new JObject { ["seq"] = 5, ["at"] = at, ["kind"] = "cut", ["text"] = "reloaded" }));
        Check("instrument: a cut (plugin reloaded) is not an incident", IncidentStore.Read().Incidents.Count(i => i.Key == "instrument:poe2") == 2);
        InstrumentIncidents.Check("poe2", State(1, orphan));   // leftovers again after the recorded orphan cleanup
        InstrumentIncidents.Check("poe2", State(0, orphan));   // gone, the last problem already on the board
        Check("instrument: nothing left resolves a leftover raised after a recorded cleanup", IncidentLogic.OpenByKey(IncidentStore.Read(), "instrument:poe2") == null);
        Check("instrument: end line warns when methods stay patched",
            InstrumentIncidents.EndLine(new JObject { ["stillPatchedCount"] = 1, ["stillPatched"] = new JArray("A.B"), ["endedBy"] = "done" })!.StartsWith("WARNING")
            && InstrumentIncidents.EndLine(new JObject { ["stillPatchedCount"] = 0, ["endedBy"] = "done" }) == null);
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}

// ── Per-call identity on the shared HTTP server (Hosting/CallerIdentity.cs) ──
{
    Check("caller: no header is no caller", CallerIdentity.FromHeaders(null, null, null) == null && CallerIdentity.FromHeaders("  ", "x", "y") == null);
    var c = CallerIdentity.FromHeaders(Uri.EscapeDataString("route-act2 ü"), "claude%2Froute-act2", Uri.EscapeDataString(@"C:\w t\x") + "%0A");
    Check("caller: headers are unescaped and control characters dropped", c is { Label: "route-act2 ü", Branch: "claude/route-act2", Cwd: @"C:\w t\x" }, c?.ToString());
    Check("caller: label clipped to 40", CallerIdentity.FromHeaders(new string('a', 60), null, null)!.Label.Length == 40);
    var own = SessionIdentity.Label;
    string? inside = null, idInside = null; JObject? hello = null;
    await CallerIdentity.RunAsAsync(c, async () =>
    {
        await Task.Yield();   // flows across awaits, as through the MCP pipeline
        inside = SessionIdentity.Label; idInside = SessionIdentity.Id;
        hello = SessionIdentity.HelloParams();   // no caller passed: the process's own hello, even inside a request
    });
    Check("caller: SessionIdentity answers with the caller inside its request", inside == "route-act2 ü" && idInside == SessionIdentity.IdOf(c!) && idInside!.StartsWith("route-act2 ü#http-"), inside);
    Check("caller: and with its own identity outside", SessionIdentity.Label == own && CallerIdentity.Current == null);
    Check("caller: base hello stays the process's", hello?["label"]?.ToString() == own);
    var ch = SessionIdentity.HelloParams(c!);
    Check("caller: a caller's hello names it", ch["label"]?.ToString() == "route-act2 ü" && ch["id"]?.ToString() == SessionIdentity.IdOf(c!) && ch["branch"]?.ToString() == "claude/route-act2");
    var reg = new ExileApiMcp.Bridge.BridgeRegistry();
    IReadOnlyList<ExileApiMcp.Bridge.BridgeClient>? a1 = null, a2 = null, b1 = null;
    await CallerIdentity.RunAsAsync(c, () => { a1 = reg.Bridges; a2 = reg.Bridges; return Task.CompletedTask; });
    await CallerIdentity.RunAsAsync(CallerIdentity.FromHeaders("other", null, null), () => { b1 = reg.Bridges; return Task.CompletedTask; });
    Check("caller: each caller gets its own bridge connections, reused across calls",
        a1 != null && ReferenceEquals(a1, a2) && !ReferenceEquals(a1, b1) && !ReferenceEquals(a1, reg.Bridges) && a1.All(b => b.Caller == c) && reg.Bridges.All(b => b.Caller == null));
    IReadOnlyList<ExileApiMcp.Bridge.BridgeClient>? m1 = null, m2 = null;
    await CallerIdentity.RunAsAsync(CallerIdentity.FromHeaders("main", "main", @"C:\repo"), () => { m1 = reg.Bridges; return Task.CompletedTask; });
    await CallerIdentity.RunAsAsync(CallerIdentity.FromHeaders("main", "main", @"C:\elsewhere"), () => { m2 = reg.Bridges; return Task.CompletedTask; });
    Check("caller: same label, other checkout: its own session", !ReferenceEquals(m1, m2)
        && SessionIdentity.IdOf(CallerIdentity.FromHeaders("main", null, @"C:\repo")!) != SessionIdentity.IdOf(CallerIdentity.FromHeaders("main", null, @"C:\elsewhere")!));
    Check("caller: the id doesn't depend on the server's pid (stable across an idle restart)", System.Text.RegularExpressions.Regex.IsMatch(SessionIdentity.IdOf(c!), "^route-act2 ü#http-[0-9a-f]{8}$") && SessionIdentity.IdOf(c!) == SessionIdentity.IdOf(CallerIdentity.FromHeaders(c!.Label, null, c.Cwd + "\\")!));
    reg.Dispose();
    // The HUD counts agents by role and by whether a call is open (bridge Shared/Sessions.cs PresentLocked).
    Check("caller: an agent caller's hello says role agent", ch["role"]?.ToString() == "agent");
    Check("caller: an infra caller's hello says role infra", SessionIdentity.HelloParams(new CallerIdentity("route-dispatcher", null, null) { Infra = true })["role"]?.ToString() == "infra");
    var cid = SessionIdentity.IdOf(c!);
    var e1 = CallerIdentity.Enter(cid); var e2 = CallerIdentity.Enter(cid);
    var two = CallerIdentity.OpenCalls(cid);
    e1.Dispose(); e1.Dispose();   // twice: counted once
    var one = CallerIdentity.OpenCalls(cid);
    e2.Dispose();
    // Roots never guess a label (SessionIdentity.DecideRoots): 2026-10-12 a desktop-app server read another session's worktree.
    var main = @"C:\repo"; var wtA = @"C:\repo\.claude\worktrees\a"; var wtB = @"C:\repo\.claude\worktrees\b";
    SessionIdentity.RootCheckout R(string p, string b) => new(p, p, b);
    Check("roots: a server outside any checkout never asks", SessionIdentity.WhyNotAskRoots(false, "claude-code") != null);
    Check("roots: a client other than Claude Code is never asked", SessionIdentity.WhyNotAskRoots(true, "claude-ai") != null && SessionIdentity.WhyNotAskRoots(true, null) != null);
    Check("roots: Claude Code in a checkout is asked", SessionIdentity.WhyNotAskRoots(true, "claude-code") == null);
    Check("roots: one other checkout is adopted", SessionIdentity.DecideRoots(main, [R(wtA, "claude/a")]).Adopt?.Path == wtA);
    Check("roots: two roots in one other checkout are one", SessionIdentity.DecideRoots(main, [R(wtA, "claude/a"), new(wtA + @"\Plugins\X", wtA + "\\", "claude/a")]).Adopt?.Path == wtA);
    Check("roots: two checkouts are ambiguous", SessionIdentity.DecideRoots(main, [R(wtA, "claude/a"), R(wtB, "claude/b")]).Adopt == null);
    Check("roots: its own checkout plus a worktree is ambiguous", SessionIdentity.DecideRoots(main, [R(main, "main"), R(wtA, "claude/a")]).Adopt == null);
    Check("roots: only its own checkout changes nothing", SessionIdentity.DecideRoots(main, [R(main + "\\", "main")]).Adopt == null);
    Check("roots: no checkout changes nothing", SessionIdentity.DecideRoots(main, []).Adopt == null);
    Check("caller: open calls counted per caller, each leave once", two == 2 && one == 1 && CallerIdentity.OpenCalls(cid) == 0 && CallerIdentity.OpenCalls("nobody") == 0, $"{two} {one}");
}

// ── F8 dispatcher: CLI discovery, handler command line, environment, brief, result parsing (Dispatch/FeedbackKit.cs) ──
{
    var root = Path.Combine(Path.GetTempPath(), "hexile-kit-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        var appData = Path.Combine(root, "Roaming");
        var cc = Path.Combine(appData, "Claude", "claude-code");
        foreach (var (ver, hash) in new[] { ("2.1.293", "aa"), ("2.1.295", "bb"), ("2.1.40", "cc") })
        {
            Directory.CreateDirectory(Path.Combine(cc, ver, hash));
            File.WriteAllText(Path.Combine(cc, ver, hash, "claude.exe"), "");
        }
        Directory.CreateDirectory(Path.Combine(cc, "not-a-version"));
        var (exe, looked) = ExileApiMcp.Dispatch.FeedbackKit.FindCli(ExileApiMcp.Dispatch.FeedbackKit.CliRoots(appData, Path.Combine(root, "Local")));
        Check("cli: newest version wins (by version, not by name)", exe == Path.Combine(cc, "2.1.295", "bb", "claude.exe"), exe);
        var (none, lookedNone) = ExileApiMcp.Dispatch.FeedbackKit.FindCli([Path.Combine(root, "nowhere")]);
        Check("cli: none found names where it looked", none == null && lookedNone.Contains("nowhere"));

        // The sign-in command: the inside path (%APPDATA%, MSIX-virtualized) becomes the package's LocalCache path, as the
        // user's own PowerShell sees it, written with $env:LOCALAPPDATA and the & a quoted path needs.
        var profile = Path.Combine(root, "Profile");
        var local = ExileApiMcp.Dispatch.FeedbackKit.RealLocalAppData(profile);
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Claude_aaaa", "LocalCache", "Roaming", "Claude", "claude-code", "2.1.293", "aa"));
        var pkgCc = Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude", "claude-code");
        Directory.CreateDirectory(Path.Combine(pkgCc, "2.1.295", "bb"));
        File.WriteAllText(Path.Combine(pkgCc, "2.1.295", "bb", "claude.exe"), "");
        var login = ExileApiMcp.Dispatch.FeedbackKit.LoginCommand(exe!, profile, local);
        Check("login: the package path outside the MSIX, same version and hash, in PowerShell form",
            login == "& \"$env:LOCALAPPDATA" + Path.DirectorySeparatorChar   // CI runs on Linux: the separator is the platform's
                + Path.Combine("Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude", "claude-code", "2.1.295", "bb", "claude.exe") + "\" auth login", login);
        Check("login: a path already in the package stays (as $env:LOCALAPPDATA)",
            ExileApiMcp.Dispatch.FeedbackKit.LoginCommand(Path.Combine(pkgCc, "2.1.295", "bb", "claude.exe"), profile, local + "\\") == login);
        var redirected = ExileApiMcp.Dispatch.FeedbackKit.LoginCommand(exe!, profile, @"D:\Elsewhere\Local");
        Check("login: %LOCALAPPDATA% redirected elsewhere: the literal path, not $env:LOCALAPPDATA",
            redirected == $"& \"{Path.Combine(pkgCc, "2.1.295", "bb", "claude.exe").Replace("$", "`$")}\" auth login", redirected);
        var noPkg = ExileApiMcp.Dispatch.FeedbackKit.LoginCommand(Path.Combine(cc, "2.1.293", "aa", "claude.exe"), profile, local);
        Check("login: no package folder holds that version: the inside path as the fallback",
            noPkg == $"& \"{Path.Combine(cc, "2.1.293", "aa", "claude.exe")}\" auth login", noPkg);
        Check("login: a HEXILE_CLAUDE_EXE elsewhere is used as is",
            ExileApiMcp.Dispatch.FeedbackKit.LoginCommand(@"D:\tools\claude.exe", profile, local) == "& \"D:\\tools\\claude.exe\" auth login");
        Check("login: $ and ` in a literal path are escaped for PowerShell's double quotes",
            ExileApiMcp.Dispatch.FeedbackKit.LoginCommand(@"D:\t$dev\a`b\claude.exe", profile, local) == "& \"D:\\t`$dev\\a``b\\claude.exe\" auth login");
    }
    finally { try { Directory.Delete(root, true); } catch { } }

    var cmdArgs = ExileApiMcp.Dispatch.FeedbackKit.CmdArguments(@"C:\x\claude.exe", 42, @"C:\h\42", @"C:\h\42\mcp.json", [@"C:\live", @"C:\Path of Hexile"]);
    Check("cmd: headless, own worktree, prompt on stdin, output to files",
        cmdArgs.StartsWith("/d /s /c \"") && cmdArgs.Contains("-p --output-format json") && cmdArgs.Contains("--worktree route-f8-42") && cmdArgs.Contains("--strict-mcp-config")
        && cmdArgs.Contains($"< \"{Path.Combine(@"C:\h\42", "prompt.txt")}\"") && cmdArgs.Contains($"> \"{Path.Combine(@"C:\h\42", "output.json")}\"") && cmdArgs.Contains($"2> \"{Path.Combine(@"C:\h\42", "stderr.log")}\"")
        && cmdArgs.Contains("--add-dir \"C:\\Path of Hexile\"") && cmdArgs.EndsWith("\""), cmdArgs);
    Check("env: the starting session's Claude variables are scrubbed",
        ExileApiMcp.Dispatch.FeedbackKit.Scrub("CLAUDE_CODE_SESSION_ID") && ExileApiMcp.Dispatch.FeedbackKit.Scrub("CLAUDECODE") && ExileApiMcp.Dispatch.FeedbackKit.Scrub("ANTHROPIC_API_KEY")
        && ExileApiMcp.Dispatch.FeedbackKit.Scrub("HEXILE_MCP_LOCAL") && ExileApiMcp.Dispatch.FeedbackKit.Scrub("HEXILE_AGENT")
        && !ExileApiMcp.Dispatch.FeedbackKit.Scrub("PATH") && !ExileApiMcp.Dispatch.FeedbackKit.Scrub("exilecore2Package") && !ExileApiMcp.Dispatch.FeedbackKit.Scrub("HEXILE_REPO"));

    var press = JObject.Parse("""{ "seq": 120, "kind": "feedback", "at": "2026-10-11T10:05:00Z", "data": { "Event": "note", "Note": "lever not shown", "Area": "G3_2_2", "AreaName": "The Matlan Waterways", "Label": "Pull the Waterways Lever", "Target": "radar:Waterways Lever", "StepIndex": 0, "PlayerGrid": [10, 20], "Seq": 400, "State": { "Area": "G3_2_2" } } }""");
    var trace = new List<JObject> { JObject.Parse("""{ "seq": 118, "kind": "route", "at": "2026-10-11T10:04:00Z", "data": { "Event": "quest", "Area": "G3_2_2", "AreaName": "The Matlan Waterways", "Quests": ["SearchForTheVaalRepository:34 text"] } }""") };
    var brief = ExileApiMcp.Dispatch.FeedbackKit.Brief(press, trace, [], @"C:\m\docs\feedback-agent.md", @"C:\live", 110);
    Check("brief: press, note, standing brief, reply path, trace and state",
        brief.Contains("# F8 press #120") && brief.Contains("lever not shown") && brief.Contains(@"C:\m\docs\feedback-agent.md") && brief.Contains("feedback_reply seq=120")
        && brief.Contains("since the previous press (#110)") && brief.Contains("SearchForTheVaalRepository:34") && brief.Contains("\"Area\": \"G3_2_2\""), brief);

    var ok = ExileApiMcp.Dispatch.FeedbackKit.ParseOutput("""{"type":"result","subtype":"success","is_error":false,"result":"Fixed the lever step. Pull the lever now.","session_id":"abc"}""");
    Check("output: final message and session", ok.Result == "Fixed the lever step. Pull the lever now." && !ok.IsError && ok.SessionId == "abc");
    var bad = ExileApiMcp.Dispatch.FeedbackKit.ParseOutput("""{"type":"result","is_error":true,"result":"Not logged in","terminal_reason":"api_error"}""");
    Check("output: an error result is a failure with its reason", bad.IsError && bad.Why!.Contains("api_error") && bad.Result == "Not logged in");
    Check("output: empty is a failure", ExileApiMcp.Dispatch.FeedbackKit.ParseOutput("").IsError);

    // Visible handlers (background session with Remote Control): name, arguments, the CLI's answers, the transcript.
    var named = ExileApiMcp.Dispatch.FeedbackKit.DisplayName(JObject.Parse("""{ "seq": 12, "data": { "Event": "note", "Note": "the lever   step never \"ends\" after I pull it twice", "AreaName": "The Molten Vault" } }"""));
    Check("name: seq, first words of the note, area, no quotes", named == "F8 #12: the lever step never 'ends' after I... (The Molten Vault)", named);
    var verdict = ExileApiMcp.Dispatch.FeedbackKit.DisplayName(JObject.Parse("""{ "seq": 3, "data": { "Event": "Wrong place" } }"""));
    Check("name: the verdict without a note", verdict == "F8 #3: Wrong place", verdict);
    var bgArgs = ExileApiMcp.Dispatch.FeedbackKit.BackgroundArguments(42, "F8 #42: x", @"C:\h\42\mcp.json", [@"C:\live"], "Handle it.");
    Check("bg: background, Remote Control named, own worktree, prompt last",
        bgArgs[0] == "--bg" && bgArgs[1] == "--remote-control" && bgArgs[2] == "F8 #42: x" && string.Join(" ", bgArgs).Contains("--worktree route-f8-42")
        && bgArgs.Contains("--strict-mcp-config") && !bgArgs.Contains("-p") && bgArgs[^1] == "Handle it.", string.Join(" | ", bgArgs));
    var slimArgs = ExileApiMcp.Dispatch.FeedbackKit.SlimArguments(@"C:\h\42\settings.json", @"C:\repo\Plugins\Whats A Route\docs\feedback-agent.md", "# Brief\n\"quoted\"", asFile: true);
    var slimText = ExileApiMcp.Dispatch.FeedbackKit.SlimArguments(@"C:\h\42\settings.json", @"C:\b.md", "# Brief\n\"quoted\"", asFile: false);
    var slimBg = ExileApiMcp.Dispatch.FeedbackKit.BackgroundArguments(42, "F8 #42: x", @"C:\h\42\mcp.json", [@"C:\live"], "Handle it.", slimText);
    Check("slim: project settings only, no CLAUDE.md or memory, route-fix tools with ToolSearch (MCP stays deferred), brief as system prompt",
        slimArgs.SequenceEqual(["--tools", "Read,Edit,Write,Bash,Grep,Glob,ToolSearch,Skill", "--setting-sources", "project", "--settings", @"C:\h\42\settings.json",
            "--append-system-prompt-file", @"C:\repo\Plugins\Whats A Route\docs\feedback-agent.md"])
        && slimText[^2] == "--append-system-prompt" && slimText[^1] == "# Brief\n\"quoted\""
        && ExileApiMcp.Dispatch.FeedbackKit.SlimSettingsJson() == """{"claudeMdExcludes":["**/CLAUDE.md"],"autoMemoryEnabled":false}"""
        && !ExileApiMcp.Dispatch.FeedbackKit.SlimArguments("s", "b", null, true).Any(x => x.StartsWith("--append")), string.Join(" | ", slimArgs));
    Check("slim bg: the prompt still follows a single-value option",
        slimBg[^1] == "Handle it." && slimBg[^2] == "F8 #42: x" && slimBg[^3] == "--name" && slimBg.Contains("--tools"), string.Join(" | ", slimBg));
    var slimCmd = ExileApiMcp.Dispatch.FeedbackKit.CmdArguments(@"C:\x\claude.exe", 42, @"C:\h\42", @"C:\h\42\mcp.json", [@"C:\live"], slimArgs);
    Check("slim cmd: options bare, values quoted", slimCmd.Contains("--tools \"Read,Edit,Write,Bash,Grep,Glob,ToolSearch,Skill\" --setting-sources \"project\"")
        && slimCmd.Contains("--append-system-prompt-file \"C:\\repo\\Plugins\\Whats A Route\\docs\\feedback-agent.md\" <"), slimCmd);
    Check("bg: short id from the CLI's answer",
        ExileApiMcp.Dispatch.FeedbackKit.ParseBackgrounded("Starting background service…\nbackgrounded · 98b400ca · F8 test\n  claude agents") == "98b400ca"
        && ExileApiMcp.Dispatch.FeedbackKit.ParseBackgrounded("Workspace not trusted.") == null);
    Check("bg: a CLI answer on the card is one line, no usage help",
        ExileApiMcp.Dispatch.FeedbackKit.CliLine("\n  Error: not logged in\n  claude agents  list sessions\n") == "Error: not logged in"
        && ExileApiMcp.Dispatch.FeedbackKit.CliLine("") == "(no output)");
    Check("bg: short id through code-page mojibake (press #219) and from the attach hint",
        ExileApiMcp.Dispatch.FeedbackKit.ParseBackgrounded("backgrounded â”¬â•– 1cfc0796 â”¬â•– F8 #219: Should be (Kingsmarch) claude agents list sessions") == "1cfc0796"
        && ExileApiMcp.Dispatch.FeedbackKit.ParseBackgrounded("started\n  claude attach 2448b670   open in this terminal") == "2448b670"
        && ExileApiMcp.Dispatch.FeedbackKit.ParseBackgrounded("backgrounded · F8 #12: abcdef fix") == null);
    const string namedJson = """
        [ { "id": "200d925a", "name": "F8 #219: x", "startedAt": 1000 },
          { "id": "297d8314", "name": "F8 #219: x", "startedAt": 2000 },
          { "id": "aaaaaaaa", "name": "F8 #220: y", "startedAt": 3000 } ]
        """;
    Check("agents: newest session by name since the start",
        ExileApiMcp.Dispatch.FeedbackKit.FindAgentByName(namedJson, "F8 #219: x", 0) == "297d8314"
        && ExileApiMcp.Dispatch.FeedbackKit.FindAgentByName(namedJson, "F8 #219: x", 2500) == null
        && ExileApiMcp.Dispatch.FeedbackKit.FindAgentByName(namedJson, "F8 #221: z", 0) == null);
    const string agentsJson = """
        [ { "pid": 1, "kind": "interactive", "sessionId": "s0", "status": "busy" },
          { "pid": 26424, "id": "98b400ca", "kind": "background", "sessionId": "98b400ca-5279", "status": "idle", "state": "done" },
          { "id": "c37e5f7f", "kind": "background", "sessionId": "c37e5f7f-3394", "state": "stopped" },
          { "id": "832c4db8", "kind": "background", "startedAt": 1791779738464, "state": "working" },
          { "id": "bc5a8b97", "kind": "background", "state": "done" } ]
        exit 0
        """;
    var live = ExileApiMcp.Dispatch.FeedbackKit.ParseAgent(agentsJson, "98b400ca");
    var stopped = ExileApiMcp.Dispatch.FeedbackKit.ParseAgent(agentsJson, "c37e5f7f");
    Check("agents: state, session id, alive by pid", live is { SessionId: "98b400ca-5279", State: "done", Alive: true } && stopped is { Alive: false });
    // CLI 2.1.295 lists a running background job without a pid: reading that as "exited" stopped F8 #346 at its first poll.
    Check("agents: no pid, a running state is alive",
        ExileApiMcp.Dispatch.FeedbackKit.ParseAgent(agentsJson, "832c4db8") is { State: "working", Alive: true }
        && ExileApiMcp.Dispatch.FeedbackKit.ParseAgent(agentsJson, "bc5a8b97") is { State: "done", Alive: true });
    Check("worktree: the CLI's lock pid, not another lock's",
        ExileApiMcp.Dispatch.FeedbackKit.StaleLockPid("fatal: cannot remove a locked working tree, lock reason: claude session route-f8-138 (pid 25444)\nuse 'remove -f -f'") == 25444
        && ExileApiMcp.Dispatch.FeedbackKit.StaleLockPid("fatal: cannot remove a locked working tree, lock reason: on a USB stick") == null);
    Check("agents: unlisted is null", ExileApiMcp.Dispatch.FeedbackKit.ParseAgent(agentsJson, "ffffffff") == null);
    Throws<FormatException>("agents: no list is a CLI failure, not 'unlisted'", () => ExileApiMcp.Dispatch.FeedbackKit.ParseAgent("error: service down", "98b400ca"));
    var named2 = ExileApiMcp.Dispatch.FeedbackKit.DisplayName(JObject.Parse("""{ "seq": 4, "data": { "Note": "fix  it" } }"""));
    Check("name: no ellipsis when nothing was cut", named2 == "F8 #4: fix it", named2);
    var rc = ExileApiMcp.Dispatch.FeedbackKit.ParseTranscript([
        """{"type":"user","message":{"role":"user","content":"go"}}""",
        """{"type":"system","subtype":"bridge_status","content":"/remote-control is active","url":"https://claude.ai/code/session_01Rx"}""",
        """{"type":"assistant","message":{"content":[{"type":"text","text":"First look."},{"type":"tool_use","name":"Read"}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash"}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"text","text":"Fixed the lever step. Pull it now."}]}}""",
        "not json {",
    ]);
    Check("transcript: Remote Control URL and the last answer, turn not ended yet",
        rc.Url == "https://claude.ai/code/session_01Rx" && rc.LastText == "Fixed the lever step. Pull it now." && !rc.TurnEnded, $"{rc.Url} | {rc.LastText} | {rc.TurnEnded}");
    ExileApiMcp.Dispatch.FeedbackKit.ParseTranscript(["""{"type":"system","subtype":"turn_duration","durationMs":6481}"""], rc);
    Check("transcript: turn_duration ends the turn (fed on)", rc.TurnEnded && rc.LastText == "Fixed the lever step. Pull it now.");
    ExileApiMcp.Dispatch.FeedbackKit.ParseTranscript(["""{"type":"user","message":{"role":"user","content":"and the next step?"}}"""], rc);
    Check("transcript: a follow-up opens a new turn", !rc.TurnEnded);
    Check("refusal: an older bridge's bare answer is a broken link",
        ExileApiMcp.Dispatch.FeedbackKit.Refusal(JObject.Parse("""{ "game": "poe2", "query": "pubsub.events", "timestamp": "x" }"""), "pubsub.events")?.Contains("no pubsub.events") == true);
    Check("refusal: ok true and ok false (claimed) are answers, an error object is not",
        ExileApiMcp.Dispatch.FeedbackKit.Refusal(JObject.Parse("""{ "ok": true }"""), "m") == null
        && ExileApiMcp.Dispatch.FeedbackKit.Refusal(JObject.Parse("""{ "ok": false, "error": "claimed" }"""), "m") == null
        && ExileApiMcp.Dispatch.FeedbackKit.Refusal(JObject.Parse("""{ "error": "bad_kind", "message": "x" }"""), "m")?.Contains("bad_kind") == true
        && ExileApiMcp.Dispatch.FeedbackKit.Refusal(new JArray(), "m") != null);
    var prev = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        var dated = JObject.Parse("""{ "at": "2026-10-11T10:05:00.1234567Z" }""");   // Newtonsoft makes it a Date token
        var at = ExileApiMcp.Dispatch.FeedbackKit.At(dated["at"]);
        Check("at: a Date token is UTC whatever the culture", at == new DateTimeOffset(2026, 10, 11, 10, 5, 0, TimeSpan.Zero).AddTicks(1234567), at?.ToString("O"));
        var text = ExileApiMcp.Dispatch.FeedbackKit.At(new JValue("2026-10-11T10:05:00Z"));
        Check("at: an ISO string is UTC", text == new DateTimeOffset(2026, 10, 11, 10, 5, 0, TimeSpan.Zero), text?.ToString("O"));
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
    var clipped = ExileApiMcp.Dispatch.FeedbackKit.Clip("First sentence is here. Second sentence goes on and on and on.", 40);
    Check("clip: cuts at a sentence end", clipped == "First sentence is here.", clipped);
}

// ── F8 sessions: the brief's header, the CLI's result, the transcript path, the dispatcher's liveness, the bridge merge (Dispatch/FeedbackSessions.cs) ──
{
    var fsNow = new DateTimeOffset(2026, 10, 11, 11, 0, 0, TimeSpan.Zero);
    var s = new ExileApiMcp.Tools.FeedbackSession { Seq = 21, Status = "done", StartedAt = new DateTimeOffset(2026, 10, 11, 10, 55, 36, TimeSpan.Zero) };
    const string brief = "# F8 press #21 (10:50:32 UTC)\n\n- Verdict: **note** - the player wrote: \"The forgemaster is \"not\" at the right place\"\n- Area: The Molten Vault (G3_9), step index 0: Mektul, the Forgemaster (target radar:Mektul, The Forgemaster (Reforging Bench))\n- Player grid cell: [1241, 1948]\n";
    ExileApiMcp.Dispatch.FeedbackSessions.ApplyBrief(s, brief, keepText: false);
    Check("brief: verdict and note (quotes inside kept)", s.Verdict == "note" && s.Note == "The forgemaster is \"not\" at the right place", s.Note);
    Check("brief: area, id, label and target (parentheses inside kept)", s.AreaName == "The Molten Vault" && s.Area == "G3_9" && s.Label == "Mektul, the Forgemaster" && s.Target == "radar:Mektul, The Forgemaster (Reforging Bench)", $"{s.AreaName}|{s.Area}|{s.Label}|{s.Target}");
    Check("brief: the press time is dated from the handler's start", s.PressedAt == new DateTimeOffset(2026, 10, 11, 10, 50, 32, TimeSpan.Zero) && s.Brief == null, s.PressedAt?.ToString("O"));
    var midnight = new ExileApiMcp.Tools.FeedbackSession { Seq = 5, Status = "done", StartedAt = new DateTimeOffset(2026, 10, 12, 0, 1, 0, TimeSpan.Zero) };
    ExileApiMcp.Dispatch.FeedbackSessions.ApplyBrief(midnight, "# F8 press #5 (23:58:10 UTC)\n\n- Verdict: **works** (no note)\n- Area: X (G1_1), step index -1:  (target )\n", keepText: true);
    Check("brief: a press before midnight taken after it keeps its day; empty label and target are null; text kept when asked", midnight.PressedAt == new DateTimeOffset(2026, 10, 11, 23, 58, 10, TimeSpan.Zero) && midnight.Verdict == "works" && midnight.Note == null && midnight.Label == null && midnight.Target == null && midnight.Brief != null, midnight.PressedAt?.ToString("O"));

    var o = new ExileApiMcp.Tools.FeedbackSession { Seq = 21, Status = "done" };
    ExileApiMcp.Dispatch.FeedbackSessions.ApplyOutput(o, "warning line\n" + """{"duration_api_ms":95536,"session_id":"abc","total_cost_usd":0.9567534,"usage":{"input_tokens":40,"cache_creation_input_tokens":65938,"cache_read_input_tokens":1256547,"output_tokens":8889,"output_tokens_details":{"thinking_tokens":3956}},"modelUsage":{"claude-opus-5-5":{}},"num_turns":26,"result":"Done.","type":"result"}""");
    Check("output: cost, turns, model, api time, usage", o.CostUsd == 0.9568 && o.Turns == 26 && o.Model == "claude-opus-5-5" && o.ApiMs == 95536 && o.Usage?.CacheReadTokens == 1256547 && o.Usage?.ThinkingTokens == 3956 && o.SessionId == "abc" && o.Result == "Done.");
    var kept = new ExileApiMcp.Tools.FeedbackSession { Seq = 1, Status = "done", Result = "from state", SessionId = "state-id" };
    ExileApiMcp.Dispatch.FeedbackSessions.ApplyOutput(kept, """{"result":"from output","session_id":"out-id"}""");
    Check("output: state.json's result and session id win", kept.Result == "from state" && kept.SessionId == "state-id");
    ExileApiMcp.Dispatch.FeedbackSessions.ApplyOutput(kept, "not json");
    Check("output: garbage changes nothing", kept.Result == "from state" && kept.CostUsd == null);

    var slug = ExileApiMcp.Dispatch.FeedbackSessions.ProjectSlug(@"C:\Users\Parog\Documents\Projects\Path of Hexile\.claude\worktrees\route-f8-21");
    Check("transcript: the project slug replaces every non-alphanumeric character", slug == "C--Users-Parog-Documents-Projects-Path-of-Hexile--claude-worktrees-route-f8-21", slug);
    var tp = ExileApiMcp.Dispatch.FeedbackSessions.TranscriptPath(@"C:\Users\Parog", @"C:\Users\Parog\Documents\Projects\Path of Hexile\.claude\worktrees\route-f8-21\", "0e49");
    // CI runs on Linux too: compare with the platform's separator.
    Check("transcript: under ~\\.claude\\projects\\<slug>\\<sessionId>.jsonl", tp == Path.Combine(@"C:\Users\Parog", ".claude", "projects", "C--Users-Parog-Documents-Projects-Path-of-Hexile--claude-worktrees-route-f8-21", "0e49.jsonl"), tp);

    var st = new ExileApiMcp.Tools.FeedbackHandlerState { Seq = 7, Status = "running", Pid = 1, StartedAt = fsNow.AddMinutes(-2), Extra = new() { ["displayName"] = System.Text.Json.JsonSerializer.SerializeToElement("F8 #7: the lever"), ["url"] = System.Text.Json.JsonSerializer.SerializeToElement("https://claude.ai/code/x"), ["mode"] = System.Text.Json.JsonSerializer.SerializeToElement("remote-control"), ["processStart"] = System.Text.Json.JsonSerializer.SerializeToElement("2026-10-11T10:58:00Z") } };
    var ns = new ExileApiMcp.Tools.FeedbackSession { Seq = 7, Status = "" };
    ExileApiMcp.Dispatch.FeedbackSessions.ApplyState(ns, st);
    Check("state: a newer dispatcher's name, url and mode are picked up, and everything passes through", ns.Name == "F8 #7: the lever" && ns.Url == "https://claude.ai/code/x" && ns.Mode == "remote-control" && ns.Extra!.ContainsKey("processStart") && ns.Extra.ContainsKey("displayName"));
    var typed = new ExileApiMcp.Tools.FeedbackSession { Seq = 8, Status = "" };
    ExileApiMcp.Dispatch.FeedbackSessions.ApplyState(typed, new ExileApiMcp.Tools.FeedbackHandlerState { Seq = 8, Status = "done", Name = "F8 #8: x", Url = "https://claude.ai/code/y", Mode = "remote-control" });
    Check("state: the dispatcher's typed name, url and mode", typed.Name == "F8 #8: x" && typed.Url == "https://claude.ai/code/y" && typed.Mode == "remote-control");
    Check("duration: a running handler counts to now, a finished one to its end, an unstarted one has none",
        ExileApiMcp.Dispatch.FeedbackSessions.Duration(fsNow.AddSeconds(-90), null, "running", fsNow) == 90
        && ExileApiMcp.Dispatch.FeedbackSessions.Duration(fsNow.AddSeconds(-90), fsNow.AddSeconds(-30), "done", fsNow) == 60
        && ExileApiMcp.Dispatch.FeedbackSessions.Duration(null, null, "queued", fsNow) == null
        && ExileApiMcp.Dispatch.FeedbackSessions.Duration(fsNow.AddSeconds(-90), null, "failed", fsNow) == null);

    var live = ExileApiMcp.Dispatch.FeedbackSessions.Summarize(new ExileApiMcp.Tools.FeedbackDispatcherState { Status = "listening", UpdatedAt = fsNow.AddSeconds(-2), Waiting = [9, 3], Handlers = [new() { Seq = 8, Status = "running" }] }, fsNow);
    Check("dispatcher: fresh file = alive, waiting sorted, the running handler named", live.Alive && live.Status == "listening" && live.Waiting.SequenceEqual([3L, 9L]) && live.Running == 8 && live.Fix == null);
    var stale = ExileApiMcp.Dispatch.FeedbackSessions.Summarize(new ExileApiMcp.Tools.FeedbackDispatcherState { Status = "listening", UpdatedAt = fsNow.AddMinutes(-20) }, fsNow);
    Check("dispatcher: a stale file = not running, with the fix", !stale.Alive && stale.Status == "not-running" && stale.Why!.Contains("20 min ago") && stale.Fix == ExileApiMcp.Dispatch.FeedbackSessions.NotRunningFix, stale.Why);
    var signin = ExileApiMcp.Dispatch.FeedbackSessions.Summarize(new ExileApiMcp.Tools.FeedbackDispatcherState { Status = "broken", Why = "not logged in", UpdatedAt = fsNow, LoggedIn = false, LoginCommand = "& \"x\" auth login" }, fsNow);
    Check("dispatcher: not signed in = broken with the sign-in command as the fix", signin.Alive && signin.Status == "broken" && signin.Fix!.Contains("auth login"));
    Check("dispatcher: no file = never ran", ExileApiMcp.Dispatch.FeedbackSessions.Dispatcher(Path.Combine(Path.GetTempPath(), "no-such-dispatcher.json"), fsNow).Status == "never-ran");

    var sessions = new List<ExileApiMcp.Tools.FeedbackSession> { new() { Seq = 21, Status = "done", Note = "from the brief", AreaName = "Brief Area" } };
    var events = new[]
    {
        JObject.Parse("""{ "seq": 21, "kind": "feedback", "at": "2026-10-11T10:50:32Z", "data": { "Event": "note", "Note": "from the press", "AreaName": "The Molten Vault", "Area": "G3_9", "Label": "Mektul", "Target": "radar:Mektul" }, "claims": { "handle": { "by": "d" }, "reply": { "by": "d" } } }"""),
        JObject.Parse("""{ "seq": 30, "kind": "feedback.reply", "at": "2026-10-11T10:54:06Z", "source": "route-dispatcher", "data": { "ref": 21, "status": "queued", "text": "Got #21" } }"""),
        JObject.Parse("""{ "seq": 37, "kind": "feedback.reply", "at": "2026-10-11T10:55:56Z", "source": "route-f8-21", "data": { "ref": 21, "status": "working", "text": "Looking" } }"""),
        JObject.Parse("""{ "seq": 40, "kind": "feedback", "at": "2026-10-11T10:58:00Z", "data": { "Event": "note", "Note": "queued one", "AreaName": "A" }, "claims": {} }"""),
        JObject.Parse("""{ "seq": 41, "kind": "feedback", "at": "2026-10-11T10:59:00Z", "data": { "Event": "works", "AreaName": "A" }, "claims": {} }"""),
        JObject.Parse("""{ "seq": 2, "kind": "feedback", "at": "2026-10-11T07:00:00Z", "data": { "Event": "note", "Note": "too old" }, "claims": {} }"""),
        JObject.Parse("""{ "seq": 42, "kind": "feedback", "at": "2026-10-11T10:59:30Z", "data": { "Event": "note", "Note": "someone else answered" }, "claims": { "reply": { "by": "agent" } } }"""),
        JObject.Parse("""{ "seq": 43, "kind": "feedback.reply", "at": "2026-10-11T10:59:40Z", "source": "route-dispatcher", "data": { "ref": 40, "status": "failed", "text": "Claude could not take #40: not logged in" } }"""),
    };
    var (presses, replies) = ExileApiMcp.Dispatch.FeedbackSessions.Merge(sessions, events, [40], fsNow);
    var s21 = sessions.First(x => x.Seq == 21);
    Check("merge: the press's own note, area and time win over the brief's", s21.Note == "from the press" && s21.AreaName == "The Molten Vault" && s21.Area == "G3_9" && s21.Label == "Mektul" && s21.PressedAt == new DateTimeOffset(2026, 10, 11, 10, 50, 32, TimeSpan.Zero));
    Check("merge: replies attached in order with their source", s21.Replies.Count == 2 && s21.Replies[0].Status == "queued" && s21.Replies[1].Source == "route-f8-21");
    Check("merge: a waiting press and an 'it works' press become sessions; too old and answered elsewhere do not; newest first",
        sessions.Select(x => x.Seq).SequenceEqual([41L, 40L, 21L]) && sessions[1].Status == "queued" && sessions[0].Status == "works", string.Join(",", sessions.Select(x => $"{x.Seq}:{x.Status}")));
    Check("merge: a queued press that the dispatcher failed to start shows that failure", sessions[1].Error!.Contains("not logged in") && presses == 5 && replies == 3, sessions[1].Error);
    sessions.Add(new() { Seq = 50, Status = "failed", Error = "could not start" });
    ExileApiMcp.Dispatch.FeedbackSessions.ApplyWaiting(sessions, [99, 21, 50]);
    Check("waiting: unknown seqs become queued sessions, a failed-to-start one the dispatcher keeps is queued with its reason, done ones are left alone",
        sessions.Count == 5 && sessions[0].Seq == 99 && sessions[0].Status == "queued" && sessions.Count(x => x.Seq == 21) == 1 && sessions.First(x => x.Seq == 21).Status == "done"
        && sessions.First(x => x.Seq == 50).Status == "queued" && sessions.First(x => x.Seq == 50).Error == "could not start", string.Join(",", sessions.Select(x => $"{x.Seq}:{x.Status}")));
    ExileApiMcp.Dispatch.FeedbackSessions.ApplyOutput(sessions[0], """{"num_turns":26.0}""");
    Check("output: a non-integral turn count is rounded, not thrown", sessions[0].Turns == 26);

    var tmp = Path.Combine(Path.GetTempPath(), "hexile-f8-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(tmp, "handlers", "21"));
        Directory.CreateDirectory(Path.Combine(tmp, "handlers", "notes"));   // not a press
        File.WriteAllText(Path.Combine(tmp, "handlers", "21", "state.json"), """{"seq":21,"status":"done","pid":9004,"startedAt":"2026-10-11T10:55:36+00:00","endedAt":"2026-10-11T10:57:19+00:00","worktree":"C:\\wt\\route-f8-21","dir":"x","result":"Changed it.","sessionId":"0e49","replied":true,"processStart":"2026-10-11T10:55:36Z","name":"F8 #21: Mektul"}""");
        File.WriteAllText(Path.Combine(tmp, "handlers", "21", "brief.md"), brief);
        File.WriteAllText(Path.Combine(tmp, "handlers", "21", "output.json"), """{"total_cost_usd":0.5,"num_turns":3,"session_id":"0e49","result":"Changed it."}""");
        File.WriteAllText(Path.Combine(tmp, "handlers", "21", "stderr.log"), "");
        var dispFile = Path.Combine(tmp, "handlers", "dispatcher.json");
        File.WriteAllText(dispFile, """{"status":"listening","updatedAt":"2026-10-11T10:59:59+00:00","waiting":[],"handlers":[]}""");
        Directory.CreateDirectory(Path.Combine(tmp, "handlers", "3"));
        File.WriteAllText(Path.Combine(tmp, "handlers", "3", "state.json"), """{"seq":3,"status":"done"}""");
        var (folders, count) = ExileApiMcp.Dispatch.FeedbackSessions.FromFolders(Path.Combine(tmp, "handlers"), tmp, fsNow, withFiles: true, limit: 1);
        Check("folders: only the newest `limit` folders are read, the count says how many there are", folders.Count == 1 && count == 2);
        Check("folders: one session per numbered folder, from state + brief + output, files when asked", folders.Count == 1 && folders[0].Seq == 21 && folders[0].Name == "F8 #21: Mektul" && folders[0].Note!.StartsWith("The forgemaster") && folders[0].CostUsd == 0.5 && folders[0].Brief != null && folders[0].Stderr == null && folders[0].DurationSec == 103 && folders[0].WorktreeExists == false && folders[0].TranscriptExists == false && folders[0].TranscriptPath!.EndsWith(Path.Combine("C--wt-route-f8-21", "0e49.jsonl")), folders.Count > 0 ? $"{folders[0].Name}|{folders[0].DurationSec}|{folders[0].TranscriptPath}" : "none");
        var fp1 = ExileApiMcp.Bridge.FeedbackHub.Fingerprint(Path.Combine(tmp, "handlers"), dispFile);
        File.WriteAllText(dispFile, """{"status":"listening","updatedAt":"2026-10-11T11:00:01+00:00","cursor":5,"waiting":[],"handlers":[]}""");
        var fp2 = ExileApiMcp.Bridge.FeedbackHub.Fingerprint(Path.Combine(tmp, "handlers"), dispFile);
        File.WriteAllText(dispFile, """{"status":"listening","updatedAt":"2026-10-11T11:00:02+00:00","cursor":6,"waiting":[30],"handlers":[]}""");
        var fp3 = ExileApiMcp.Bridge.FeedbackHub.Fingerprint(Path.Combine(tmp, "handlers"), dispFile);
        File.AppendAllText(Path.Combine(tmp, "handlers", "21", "stderr.log"), "x");
        var fp4 = ExileApiMcp.Bridge.FeedbackHub.Fingerprint(Path.Combine(tmp, "handlers"), dispFile);
        Check("fingerprint: the dispatcher's once-a-second fields don't change it; a waiting press and a handler's output do", fp1 == fp2 && fp2 != fp3 && fp3 != fp4);
    }
    finally { try { Directory.Delete(tmp, true); } catch { } }
}

// ── F8 dispatcher's main-checkout sync: a clean reload, Route.IncludeOptional, the card line (Dispatch/MainSync.cs) ──
{
    Check("sync: a clean reload has no problem", ExileApiMcp.Dispatch.MainSync.ReloadProblem(JObject.Parse("{\"plugin\":\"Whats A Route\",\"ok\":true,\"durationMs\":900,\"loggedSinceReload\":[]}")) == null);
    var diag = ExileApiMcp.Dispatch.MainSync.ReloadProblem(JObject.Parse("{\"ok\":false,\"error\":\"reload failed\",\"diagnostics\":[{\"file\":\"WhatsARoute.cs\",\"line\":12,\"col\":3,\"code\":\"CS1002\",\"message\":\"; expected\"}]}"));
    Check("sync: diagnostics are the problem", diag != null && diag.Contains("CS1002") && diag.Contains("WhatsARoute.cs(12)"), diag);
    Check("sync: not ok without diagnostics", ExileApiMcp.Dispatch.MainSync.ReloadProblem(JObject.Parse("{\"ok\":false,\"error\":\"not_initialised\",\"message\":\"Initialise returned false\"}")) is { } ni && ni.Contains("not_initialised") && ni.Contains("Initialise returned false"));
    Check("sync: a timeout is a problem", ExileApiMcp.Dispatch.MainSync.ReloadProblem(JObject.Parse("{\"error\":\"timeout\",\"message\":\"No result after 120 s\"}")) != null);
    Check("sync: an unqueued reload is a problem", ExileApiMcp.Dispatch.MainSync.ReloadProblem(JObject.Parse("{\"queued\":false,\"error\":\"dll_locked\"}")) is { } dl && dl.Contains("dll_locked"));
    JObject Describe(string value) => JObject.Parse("{\"plugins\":[{\"plugin\":\"Whats A Route\",\"settings\":[{\"path\":\"Route.Mode\",\"value\":\"Route\"},{\"path\":\"Route.IncludeOptional\",\"value\":" + value + "}]}]}");
    Check("sync: IncludeOptional read", ExileApiMcp.Dispatch.MainSync.IncludeOptional(Describe("false")) == false && ExileApiMcp.Dispatch.MainSync.IncludeOptional(Describe("true")) == true);
    Check("sync: IncludeOptional missing is null", ExileApiMcp.Dispatch.MainSync.IncludeOptional(JObject.Parse("{\"plugins\":[]}")) == null
        && ExileApiMcp.Dispatch.MainSync.IncludeOptional(Describe("\"x\"")) == null);
    Check("sync: submodule pointer from ls-tree", ExileApiMcp.Dispatch.MainSync.PointerSha("160000 commit c3c854af62cc98b478f8137d3b926652b92e1dca\tPlugins/Whats A Route\n") == "c3c854af62cc98b478f8137d3b926652b92e1dca"
        && ExileApiMcp.Dispatch.MainSync.PointerSha("") == null && ExileApiMcp.Dispatch.MainSync.PointerSha("100644 blob c3c854af62cc98b478f8137d3b926652b92e1dca\tREADME.md") == null);
    var text = ExileApiMcp.Dispatch.MainSync.FailText(290, new ExileApiMcp.Dispatch.MainSync.Outcome(true, "s", "`git pull --ff-only` in the main checkout refused: fatal: Not possible to fast-forward", "Nothing was reset or merged."));
    Check("sync: the card line says merged, not in game, and why", text.StartsWith("#290's fix is merged but not in game yet: `git pull --ff-only`") && text.EndsWith("Nothing was reset or merged.") && text.Length <= 400, text);
}

// ── healthbars_state / healthbars_preview: the plugin's PascalCase state fits the contract; focus and reply decoding ──
{
    // A real WhatsAHealthBar.GetState answer (PoE2, in town), plus a field the plugin may add later.
    const string live = """
    {"Plugin":"Whats A Health Bar","Schema":1,"Enabled":true,"Suppressed":"in town","Area":"Kingsmarch (53)",
     "Counts":{"Tracked":5,"Wanted":0,"Drawn":0,"PeakDrawn":0,"ByKind":{"unique":0,"rare":0,"magic":0,"normal":0,"minion":0,"player":0,"self":0}},
     "Timings":{"TickUs":4.3,"TickMaxUs":1647.8,"RenderUs":0.1,"RenderMaxUs":0.4,"ClassifyUs":0,"PerBarNs":0,"ClassifyHz":10,"WindowSec":5},
     "Preview":{"Active":false,"SecondsLeft":0,"Focus":null},
     "Bridge":{"Linked":true,"Why":null,"Subscribes":"healthbars.preview","Publishes":"healthbars.kill","PublishKills":"Uniques"},
     "Kills":[{"Name":"Silverfist","Rarity":"Unique","MaxLife":5200,"MaxEs":0,"MaxWard":300,"TimeToKillMs":4120,"Area":"The Grelwood","At":"2026-10-12T01:00:00Z","Streak":2}],
     "Later":{"x":1}}
    """;
    var s = System.Text.Json.JsonSerializer.Deserialize<ExileApiMcp.Tools.HealthBarsStateResult>(live, ExileApiMcp.Tools.Dto.Options)!;
    Check("healthbars: PascalCase state parses", s.Plugin == "Whats A Health Bar" && s.Suppressed == "in town" && s.Counts.Tracked == 5 && s.Counts.ByKind["self"] == 0 && s.Timings.TickMaxUs == 1647.8 && s.Bridge.Linked && s.Bridge.PublishKills == "Uniques");
    Check("healthbars: kills with pools and time to kill", s.Kills.Count == 1 && s.Kills[0].MaxWard == 300 && s.Kills[0].TimeToKillMs == 4120 && s.Kills[0].Extra?.ContainsKey("Streak") == true);
    Check("healthbars: unknown top-level field kept", s.Extra?.ContainsKey("Later") == true);
    var wire = JObject.Parse(ExileApiMcp.Tools.Dto.Element(s).GetRawText());
    Check("healthbars: camelCase on the wire", wire["counts"]?["peakDrawn"] != null && wire["timings"]?["perBarNs"] != null && wire["Counts"] == null);

    Check("healthbars: focus normalised", ExileApiMcp.Tools.HealthBarsTools.NormalizeFocus("rare") == "Rare" && ExileApiMcp.Tools.HealthBarsTools.NormalizeFocus("you") == "Self" && ExileApiMcp.Tools.HealthBarsTools.NormalizeFocus(" ") == null);
    Throws<ModelContextProtocol.McpException>("healthbars: a wrong focus is refused", () => ExileApiMcp.Tools.HealthBarsTools.NormalizeFocus("Boss"));

    // PluginCall.Decode: the bridge's hud.bridge_call answers, each broken link by code.
    var missing = ExileApiMcp.Tools.PluginCall.Decode(JObject.Parse("""{"error":"method_missing","message":"PluginBridge has no 'WhatsAHealthBar.GetState'"}"""), "WhatsAHealthBar.GetState", "Whats A Health Bar", "Whats A Health Bar", "healthbars");
    Check("plugin call: method missing is plugin_not_loaded, names the plugin and the hud_plugins hint", missing.Code == "plugin_not_loaded" && missing.Message!.Contains("Whats A Health Bar is not loaded") && missing.Message.Contains("hud_plugins plugin=Whats A Health Bar"));
    Check("plugin call: the method threw", ExileApiMcp.Tools.PluginCall.Decode(JObject.Parse("""{"error":"invoke_failed","message":"threw"}"""), "M", "P", "F", "x").Code == "plugin_threw");
    Check("plugin call: a bridge without hud.bridge_call", ExileApiMcp.Tools.PluginCall.Decode(JObject.Parse("""{"foo":1}"""), "M", "P", "F", "x").Code == "bridge_too_old");
    Check("plugin call: text instead of JSON carries the prefix", ExileApiMcp.Tools.PluginCall.Decode(JObject.Parse("""{"ok":true,"resultType":"text","result":"hello"}"""), "M", "P", "F", "route").Code == "route_not_json");
    var okReply = ExileApiMcp.Tools.PluginCall.Decode(JObject.Parse("""{"ok":true,"resultType":"json","result":{"a":1}}"""), "M", "P", "F", "x");
    Check("plugin call: a JSON result", okReply.Ok && okReply.Json!["a"]?.Value<int>() == 1);
    var ps = ExileApiMcp.Tools.PluginCall.Params("WhatsAHealthBar.Preview", "{\"seconds\":8}");
    Check("plugin call: params carry name and scalar args", ps["name"]?.ToString() == "WhatsAHealthBar.Preview" && ps["args"] is JArray { Count: 1 } && ExileApiMcp.Tools.PluginCall.Params("M")["args"] == null);
}

// ── F8 unmerged fixes: what is asked on the card, what the scripts' output says ──
{
    Check("unmerged: repo from https remote", ExileApiMcp.Dispatch.UnmergedFix.RepoName("https://github.com/ParogDev/WhatsARoute.git") == "ParogDev/WhatsARoute");
    Check("unmerged: repo from ssh remote", ExileApiMcp.Dispatch.UnmergedFix.RepoName("git@github.com:ParogDev/WhatsARoute") == "ParogDev/WhatsARoute");
    Check("unmerged: no repo from a local path", ExileApiMcp.Dispatch.UnmergedFix.RepoName("C:/repos/x") == null);
    Check("unmerged: last PR number in output", ExileApiMcp.Dispatch.UnmergedFix.PrNumber("pushed\nhttps://github.com/ParogDev/WhatsARoute/pull/49\nhttps://github.com/ParogDev/ExileApiScaffolding/pull/186\n") == 186);
    Check("unmerged: no PR number", ExileApiMcp.Dispatch.UnmergedFix.PrNumber("nothing to bump") == null);
    var withPr = ExileApiMcp.Dispatch.UnmergedFix.AskText(647, new ExileApiMcp.Tools.FeedbackUnmerged { Pr = 51, Pushed = true, Branch = "f8-647-travel-map", Subject = "Read 'travel to a new island' as Sail" });
    var noPr = ExileApiMcp.Dispatch.UnmergedFix.AskText(389, new ExileApiMcp.Tools.FeedbackUnmerged { Pushed = true, Branch = "route-g4-7-arena", Subject = new string('x', 300) });
    Check("unmerged: question names the PR", withPr.Question.Contains("PR #51") && withPr.Question.Contains("#647") && withPr.Question.Length <= 120, withPr.Question);
    Check("unmerged: question names the branch without a PR", noPr.Question.Contains("route-g4-7-arena") && noPr.Detail.Length <= 160, noPr.Question);
    Check("unmerged: three choices", ExileApiMcp.Dispatch.UnmergedFix.Options.Length == 3);
}

// ── hud_plugin_lint: DebuggableAttribute read from metadata agrees with reflection (Debug and Release builds) ──
foreach (var asm in new[] { typeof(ExileApiMcp.Hud.PluginLint).Assembly, typeof(JObject).Assembly })
{
    var flags = (asm.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false).FirstOrDefault() as System.Diagnostics.DebuggableAttribute)?.DebuggingFlags ?? 0;
    var expected = (flags & System.Diagnostics.DebuggableAttribute.DebuggingModes.DisableOptimizations) == 0;
    Check($"lint: JitOptimized({asm.GetName().Name}) == {expected} ({flags})", ExileApiMcp.Hud.PluginLint.JitOptimized(asm.Location) == expected);
}

// ── HUD source: which tree a junction points into, and the lock that serializes changes to it (Hud/HudSource.cs) ──
{
    var tmp = Path.Combine(Path.GetTempPath(), "hexile-hudsource-test-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        var live = Path.Combine(tmp, "hud-live");
        var main = Path.Combine(tmp, "main");
        Directory.CreateDirectory(Path.Combine(live, ".git"));
        Directory.CreateDirectory(Path.Combine(live, "Plugins", "Whats A Route"));
        File.WriteAllText(Path.Combine(live, ".git", ExileApiMcp.Hud.HudSource.HudLiveMarker), "{}");
        Directory.CreateDirectory(Path.Combine(main, ".git"));
        Directory.CreateDirectory(Path.Combine(main, "Plugins", "Whats A Route"));
        Check("hud source: a plugin folder's checkout", ExileApiMcp.Hud.HudSource.CheckoutOf(Path.Combine(live, "Plugins", "Whats A Route")) == live
            && ExileApiMcp.Hud.HudSource.CheckoutOf(Path.Combine(tmp, "elsewhere", "X")) == null);
        Check("hud source: hud-live by its marker, a .git folder is the main checkout",
            ExileApiMcp.Hud.HudSource.Kind(live) == "hud-live" && ExileApiMcp.Hud.HudSource.Kind(main) == "main-checkout"
            && ExileApiMcp.Hud.HudSource.Kind(@"C:\x\.claude\worktrees\agent-1") == "worktree");

        // Junctions in a fake HUD: one into hud-live, one into the main checkout (a half-done cutover).
        var hud = Path.Combine(tmp, "hud");
        Directory.CreateDirectory(Path.Combine(hud, "Plugins", "Source"));
        Directory.CreateDirectory(Path.Combine(main, "Plugins", "Whats An AI Bridge"));
        // Junctions, as the HUD installs have (symlinks need Developer Mode or elevation on Windows; junctions don't). CI
        // runs on Linux: a symlink there.
        bool Junction(string link, string target)
        {
            if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return Directory.Exists(link); }
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(10_000);
            return Directory.Exists(link);
        }
        var linked = Junction(Path.Combine(hud, "Plugins", "Source", "Whats A Route"), Path.Combine(live, "Plugins", "Whats A Route"))
                     && Junction(Path.Combine(hud, "Plugins", "Source", "Whats An AI Bridge"), Path.Combine(main, "Plugins", "Whats An AI Bridge"));
        if (linked)
        {
            var huds = new[] { ("poe2", hud) };
            Check("hud source: a folder's tree from its junction", ExileApiMcp.Hud.HudSource.RootFor("whats a route", huds) == live
                && ExileApiMcp.Hud.HudSource.RootFor("Whats An AI Bridge", huds) == main && ExileApiMcp.Hud.HudSource.RootFor("Nope", huds) == null);
            Check("hud source: both trees of a half-done cutover", ExileApiMcp.Hud.HudSource.Roots(huds).Count == 2);
        }
        else Check("hud source: mklink /J made the test junctions", false);

        // The lock: a second taker waits, then names the holder.
        var repo = Path.Combine(tmp, "repo");
        Directory.CreateDirectory(repo);
        ExileApiMcp.Hud.PluginGit.Run(repo, "init", "-q");
        using (ExileApiMcp.Hud.HudSource.LockAsync(repo, "first change", TimeSpan.FromSeconds(1)).GetAwaiter().GetResult())
        {
            string? why = null;
            try { using var second = ExileApiMcp.Hud.HudSource.LockAsync(repo, "second change", TimeSpan.FromMilliseconds(600)).GetAwaiter().GetResult(); }
            catch (IOException ex) { why = ex.Message; }
            Check("hud source lock: a second change is refused, naming the first (Windows)", why != null && (why.Contains("first change") || !OperatingSystem.IsWindows()), why);
        }
        using (ExileApiMcp.Hud.HudSource.LockAsync(repo, "after release", TimeSpan.FromSeconds(1)).GetAwaiter().GetResult()) Check("hud source lock: free again once released", true);
    }
    finally { try { Directory.Delete(tmp, true); } catch { } }
}

// ── F8 progress contract (Dispatch/F8.cs): stages, phases, legacy status, inference, replies, presses ──
{
    var phases = F8.Stages.Select(F8.Phase).ToList();
    Check("f8: every stage has a phase, in pipeline order",
        string.Join(",", phases) == "Sent,Sent,Looking,Looking,Looking,Fixing,Fixing,Fixing,Shipping,Shipping,Checking,Ended,Ended,Ended", string.Join(",", phases));
    Check("f8: stage names in any casing", F8.Stage("livefix") == "LiveFix" && F8.Stage(" DONE ") == "Done" && F8.Stage("nope") == null && F8.Phase("nope") == null);
    Check("f8: C5 legacy status -> stage",
        F8.StageFromStatus("queued") == "Queued" && F8.StageFromStatus("claimed") == "Claimed" && F8.StageFromStatus("working") == "Investigating"
        && F8.StageFromStatus("done") == "Done" && F8.StageFromStatus("failed") == "Failed" && F8.StageFromStatus("odd") == null);
    Check("f8: stage -> legacy status round-trips C5",
        new[] { "queued", "claimed", "working", "done", "failed" }.All(s => F8.LegacyStatus(F8.StageFromStatus(s)) == s));
    Check("f8: legacy status of the new stages",
        F8.LegacyStatus("Reading") == "working" && F8.LegacyStatus("LiveFix") == "working" && F8.LegacyStatus("Verifying") == "working"
        && F8.LegacyStatus("Dropped") == "done" && F8.LegacyStatus("Pressed") == "queued");
    Check("f8: inference moves forward only", F8.Advance(null, "Investigating") == "Investigating" && F8.Advance("Reading", "Investigating") == "Investigating"
        && F8.Advance("Fixing", "Investigating") == null && F8.Advance("Investigating", "Reviewing") == "Reviewing" && F8.Advance("Reviewing", "Reviewing") == null);
    Check("f8: inference never leaves or enters a final stage", F8.Advance("Done", "Reviewing") == null && F8.Advance("Failed", "Investigating") == null
        && F8.Advance("Fixing", "Done") == null);
    Check("f8: handler label -> press", F8.SeqOfLabel("route-f8-123") == 123 && F8.SeqOfLabel("route-f8-x") == null && F8.SeqOfLabel("main") == null && F8.SeqOfLabel(null) == null);
    Check("f8: inferred stages", F8.InferredStage("route_state") == "Investigating" && F8.InferredStage("get_player") == "Investigating"
        && F8.InferredStage("run_csharp") == "Investigating" && F8.InferredStage("mark_reviewed") == "Reviewing" && F8.InferredStage("feedback_reply") == null
        && F8.InferredStage("pubsub_wait") == null);

    var legacyReply = JObject.Parse("""{ "seq": 50, "kind": "feedback.reply", "data": { "ref": 41, "status": "working", "text": "Looking" } }""");
    var newReply = JObject.Parse("""{ "seq": 51, "kind": "feedback.reply", "data": { "ref": 41, "Ref": 41, "status": "working", "Status": "working", "Stage": "Fixing", "text": "x", "Text": "x" } }""");
    var other = JObject.Parse("""{ "seq": 52, "kind": "feedback.reply", "data": { "ref": 42, "status": "done", "text": "y" } }""");
    Check("f8: a reply's stage: Stage first, else its legacy status", F8.ReplyStage(legacyReply["data"]) == "Investigating" && F8.ReplyStage(newReply["data"]) == "Fixing");
    Check("f8: a press's current stage is its newest reply's", F8.CurrentStage([legacyReply, newReply, other], 41) == "Fixing" && F8.CurrentStage([other], 41) == null
        && F8.CurrentStage([legacyReply, newReply, other], 42) == "Done");

    var reply = F8.Reply(41, "livefix", "Try the lever now.", detail: new string('d', 500), ask: "Pull it", waitingOn: "ci",
        pr: new ExileApiMcp.Tools.F8PrRef { Repo = "ParogDev/WhatsARoute", Number = 57, Checks = "pending" });
    var wire = ExileApiMcp.Tools.F8Wire.ToJObject(reply);
    Check("f8 reply: legacy lowercase and PascalCase both on the wire",
        wire["ref"]?.Value<long>() == 41 && wire["Ref"]?.Value<long>() == 41 && wire["status"]?.ToString() == "working" && wire["Status"]?.ToString() == "working"
        && wire["text"]?.ToString() == "Try the lever now." && wire["Text"]?.ToString() == "Try the lever now.", wire.ToString());
    Check("f8 reply: new fields PascalCase, canonical, clipped",
        wire["Stage"]?.ToString() == "LiveFix" && wire["WaitingOn"]?.ToString() == "Ci" && wire["Ask"]?.ToString() == "Pull it" && wire["Detail"]!.ToString().Length <= 400
        && wire["Pr"]?["Number"]?.Value<int>() == 57 && wire["Pr"]?["Repo"]?.ToString() == "ParogDev/WhatsARoute" && wire["At"]?.Type == JTokenType.String, wire.ToString());
    var plain = ExileApiMcp.Tools.F8Wire.ToJObject(F8.Reply(7, "Queued", null, status: "failed"));
    Check("f8 reply: nulls omitted, default text, legacy status override",
        !plain.ContainsKey("Pr") && !plain.ContainsKey("Ask") && !plain.ContainsKey("WaitingOn") && plain["status"]?.ToString() == "failed" && plain["Stage"]?.ToString() == "Queued"
        && plain["text"]?.ToString().Contains("#7") == true, plain.ToString());
    Throws<ArgumentException>("f8 reply: an unknown stage is refused", () => F8.Reply(7, "Shipping", "x"));
    Check("f8 reply: the published data reads back (ReplyRef / ReplyStage / ReplyText)",
        F8.ReplyRef(wire) == 41 && F8.ReplyStage(wire) == "LiveFix" && F8.ReplyText(wire) == "Try the lever now.");

    var works = JObject.Parse("""{ "Event": "f8", "Flags": ["works"] }""");
    var worksNote = JObject.Parse("""{ "Event": "f8", "Flags": ["works"], "Note": "but the marker is late" }""");
    var oldWorks = JObject.Parse("""{ "Event": "works" }""");
    var confirm = JObject.Parse("""{ "Event": "f8", "Flags": ["something-else"], "Ref": 11, "Verdict": "works" }""");
    var reopen = JObject.Parse("""{ "Event": "f8", "Flags": ["not-advancing"], "Ref": 11, "Verdict": "still-wrong" }""");
    Check("f8 press: C7 works-only (new and old), not with a note or a ref",
        F8.WorksOnly(works) && F8.WorksOnly(oldWorks) && !F8.WorksOnly(worksNote) && !F8.WorksOnly(confirm) && !F8.WorksOnly(JObject.Parse("""{ "Flags": ["works", "odd-path"] }""")));
    Check("f8 press: Works now confirms, Still wrong reopens", F8.ConfirmsRef(confirm) && !F8.ConfirmsRef(reopen) && F8.Reopens(reopen) && !F8.Reopens(confirm) && F8.PressRef(reopen) == 11);
    var rich = JObject.Parse("""
    { "Event": "f8", "Flags": ["wrong-place", "not-advancing"], "Note": "lever", "Scope": "panel", "ScopePanel": "Inventory", "WatchFor": "only the stash",
      "Hints": ["path-ok", "panel-only"], "WatchNext": true, "Ref": 11, "Verdict": "still-wrong",
      "Suggested": [ { "Id": "quest-moved", "Label": "Quest moved, step didn't", "Reason": "Q went to 3", "Certain": true, "Kept": true },
                     { "Id": "blocked", "Label": "No way through", "Reason": "path blocked", "Certain": true, "Kept": false },
                     { "Id": "stuck", "Label": "Stuck here 4 min", "Certain": false, "Kept": false } ] }
    """);
    var section = F8.PressSection(rich, 12, "Moved the lever step to the next room.");
    Check("f8 brief: reopened press leads with the earlier answer", section.StartsWith("**This reopens #11") && section.Contains("Moved the lever step to the next room."), section);
    Check("f8 brief: flags with their meaning, kept and rejected suggestions",
        section.Contains("**Wrong place** (wrong-place)") && section.Contains("Quest moved, step didn't") && section.Contains("PLAYER REJECTED") && section.Contains("No way through")
        && !section.Contains("Stuck here"), section);
    Check("f8 brief: scope, hints, watch-for and the capture wait",
        section.Contains("Scope: **panel**") && section.Contains("Inventory") && section.Contains("do not check pathing") && section.Contains("only the stash")
        && section.Contains("pubsub_wait kinds=[\"feedback.capture\"] since=12"), section);
    Check("f8 brief: nothing extra for an old press", F8.PressSection(JObject.Parse("""{ "Event": "note", "Note": "x" }"""), 3, null) == "");
    Check("f8 press: what was asked", F8.What(rich) == "Wrong place, Not advancing" && F8.What(JObject.Parse("""{ "Event": "Wrong place" }""")) == "Wrong place"
        && F8.What(JObject.Parse("""{ "Flags": ["something-else"], "Note": "the door" }""")) == "the door");
    var richPress = new JObject { ["seq"] = 12, ["kind"] = "feedback", ["at"] = "2026-10-12T10:00:00Z", ["data"] = rich };
    var briefNew = FeedbackKit.Brief(richPress, [], [], @"C:\m\fa.md", @"C:\live", null, "Moved the lever step.");
    Check("f8 brief: the press section comes first, stages and route_agent named",
        briefNew.IndexOf("This reopens #11", StringComparison.Ordinal) < briefNew.IndexOf("## First", StringComparison.Ordinal)
        && briefNew.Contains("stage=") && briefNew.Contains("route_agent action=set ref=12"), briefNew);

    // route_agent validation (AgentRouteKit)
    ExileApiMcp.Tools.AgentRouteStepArg Step(string target, string label, string? kind = null, string? value = null) =>
        new() { Target = target, Label = label, Done = kind == null ? null : new ExileApiMcp.Tools.AgentRouteDoneRule { Kind = kind, Value = value } };
    Throws<ArgumentException>("route_agent: no steps refused", () => AgentRouteKit.Set("a:1", "t", "r", [], null, null, null));
    Throws<ArgumentException>("route_agent: 9 steps refused", () => AgentRouteKit.Set("a:1", "t", "r", Enumerable.Range(0, 9).Select(i => Step("waypoint", $"s{i}")).ToList(), null, null, null));
    try { AgentRouteKit.Set("a:1", "t", "r", [], null, null, null); } catch (ArgumentException ex) { Check("route_agent: bad steps says bad_steps", ex.Message.StartsWith("bad_steps")); }
    Throws<ArgumentException>("route_agent: a title is required", () => AgentRouteKit.Set("a:1", " ", "r", [Step("waypoint", "go")], null, null, null));
    Throws<ArgumentException>("route_agent: a reason is required", () => AgentRouteKit.Set("a:1", "t", null, [Step("waypoint", "go")], null, null, null));
    Throws<ArgumentException>("route_agent: a step label is required", () => AgentRouteKit.Set("a:1", "t", "r", [Step("waypoint", "")], null, null, null));
    Throws<ArgumentException>("route_agent: unknown done kind", () => AgentRouteKit.Set("a:1", "t", "r", [Step("waypoint", "go", "Reach")], null, null, null));
    Throws<ArgumentException>("route_agent: Flag needs a value", () => AgentRouteKit.Set("a:1", "t", "r", [Step("", "go", "flag")], null, null, null));
    Throws<ArgumentException>("route_agent: Near needs a target", () => AgentRouteKit.Set("a:1", "t", "r", [Step("", "go", "Near")], null, null, null));
    Throws<ArgumentException>("route_agent: bad handBack", () => AgentRouteKit.Set("a:1", "t", "r", [Step("waypoint", "go")], null, "Stay", null));
    Throws<ArgumentException>("route_agent: highlight must be an object", () => AgentRouteKit.Set("a:1", "t", "r",
        [new() { Target = "waypoint", Label = "go", Highlight = System.Text.Json.JsonDocument.Parse("\"x\"").RootElement }], null, null, null));
    var cmd = AgentRouteKit.Set("route-f8-12:abc123", new string('T', 60), new string('R', 200),
        [Step("click:Lever", new string('L', 60)), Step("", "Press Did it once pulled"), Step("", "Kill it", "dead", "MonkeyChiefJungle")], 9999, "hold", 12);
    Check("route_agent: defaults (Near with a target, Manual without), canonical kinds, limits",
        cmd.Steps![0].Done!.Kind == "Near" && cmd.Steps[1].Done!.Kind == "Manual" && cmd.Steps[2].Done!.Kind == "Dead" && cmd.Steps[2].Done!.Value == "MonkeyChiefJungle"
        && cmd.Title!.Length <= 40 && cmd.Reason!.Length <= 120 && cmd.Steps[0].Label.Length <= 40 && cmd.TtlSec == 1800 && cmd.HandBack == "Hold" && cmd.Ref == 12);
    Check("route_agent: ttl default 600", AgentRouteKit.Set("a:1", "t", "r", [Step("waypoint", "go")], null, null, null).TtlSec == 600);
    var routeWire = ExileApiMcp.Tools.F8Wire.ToJObject(cmd);
    Check("route_agent: route.agent data is PascalCase with Op set, no Owner (the bridge stamps it)",
        routeWire["Op"]?.ToString() == "set" && routeWire["Id"]?.ToString() == "route-f8-12:abc123" && routeWire["Steps"]?[2]?["Done"]?["Kind"]?.ToString() == "Dead"
        && routeWire["Steps"]?[0]?["Target"]?.ToString() == "click:Lever" && !routeWire.ContainsKey("Owner") && !routeWire.ContainsKey("Hue") && !routeWire.ContainsKey("Text")
        && routeWire["Steps"]?[0] is JObject s0 && !s0.ContainsKey("Highlight"), routeWire.ToString());
    var id = AgentRouteKit.NewId("route-f8-12");
    Check("route_agent: default id is <label>:<6 hex>", System.Text.RegularExpressions.Regex.IsMatch(id, "^route-f8-12:[0-9a-f]{6}$") && id != AgentRouteKit.NewId("route-f8-12"), id);
    Check("route_agent: progress outcome", AgentRouteKit.Outcome(["started", "step-done", "done"]) == ("done", false)
        && AgentRouteKit.Outcome(["started", "left"]) == (null, true) && AgentRouteKit.Outcome(["started", "left", "resumed"]) == (null, false)
        && AgentRouteKit.Outcome(["started", "expired"]) == ("expired", false) && AgentRouteKit.Outcome(["started", "replaced", "started"]) == (null, false));
}


// ── HUD availability: "not now" vs broken from hud-state.json (Bridge/HudState.cs) ──
{
    HudStateFile S(string phase, double sinceSec, bool inGame = false) => new()
    {
        Schema = 1, Game = "poe2", Phase = phase, Since = now.AddSeconds(-sinceSec), Heartbeat = now.AddSeconds(-1), TickAt = now.AddSeconds(-1),
        InGame = inGame, Loading = phase == "loading", LastArea = "The Riverbank", LastLoadMs = 3400, GamePid = 11, HudPid = 22, TypicalStartupSec = 40,
    };
    ExileApiMcp.Bridge.HudAvailabilityFacts F(HudStateFile? s, string? reason = "hud_loading") => new()
        { Game = "poe2", Now = now, State = s, ErrorReason = reason, GameAlive = true, HudAlive = true, HudStartedAt = now.AddHours(-1) };
    var L = ExileApiMcp.Bridge.HudAvailabilityLogic.Diagnose;

    Check("availability: no file, no outage = unknown (today's behaviour)", L(F(null)) == null);
    var a1 = L(F(S("loading", 4, inGame: true)));
    Check("availability: a 4 s load is not now, from the area, wait with area.ready",
        a1 is { State: "notNow", Reason: "loading", From: "The Riverbank" } && a1.WaitWith!.Contains("area.ready") && a1.Detail.Contains("4 s") && a1.ExpectedBackSec == 1, a1?.Detail);
    Check("availability: text says not now, normal", ExileApiMcp.Bridge.HudAvailabilityLogic.Text(a1!).StartsWith("Not now (normal): load screen"), ExileApiMcp.Bridge.HudAvailabilityLogic.Text(a1!));
    Check("availability: a load over a minute with the game alive is broken stuckLoading", L(F(S("loading", 90, true))) is { State: "broken", Reason: "stuckLoading" });
    var fg = F(S("ready", 30, true)); fg.GameAlive = false;
    Check("availability: game process gone is broken gameGone", L(fg) is { State: "broken", Reason: "gameGone" });
    var fh = F(S("closed", 30)); fh.HudAlive = false;
    Check("availability: HUD gone with nothing announced is broken hudGone", L(fh) is { State: "broken", Reason: "hudGone" } h && h.WaitWith!.Contains("hud_restart"));
    var fr = F(S("restarting", 20)); fr.HudAlive = false;
    Check("availability: HUD closed for a restart 20 s ago is not now restarting", L(fr) is { State: "notNow", Reason: "restarting" });
    var fu = F(S("user-restart", 200)); fu.HudAlive = false;
    Check("availability: HUD closed for the user's restart is not now user-restart", L(fu) is { State: "notNow", Reason: "user-restart" });
    var fs = F(S("closed", 600), "unavailable"); fs.HudStartedAt = now.AddSeconds(-12); fs.State!.Heartbeat = now.AddSeconds(-90);
    var a2 = L(fs);
    Check("availability: a HUD process newer than the heartbeat is starting, usually ~40 s (typical), 12 s in, never 'plugins compiling'",
        a2 is { State: "notNow", Reason: "starting" } && a2.Detail.Contains("usually ~40 s, up to ~100 s") && !a2.Detail.Contains("compiling")
        && a2.ExpectedBackSec == 28 && a2.SinceSec == 12, a2?.Detail);
    Check("availability: phase starting is not now starting (a bridge reload in an old HUD counts in the phase)",
        L(F(S("starting", 5), "unavailable")) is { State: "notNow", Reason: "starting", SinceSec: 5 }, L(F(S("starting", 5), "unavailable"))?.Detail);
    var fn = F(null, "unavailable"); fn.HudStartedAt = now.AddSeconds(-20);
    var a3 = L(fn);
    Check("availability: no file (older bridge), HUD process 20 s old, listener down = starting, usually ~35 s, back in ~15 s",
        a3 is { State: "notNow", Reason: "starting", ExpectedBackSec: 15, SinceSec: 20 } && a3.Detail.Contains("usually ~35 s, up to ~100 s"), a3?.Detail);
    fn.HudStartedAt = now.AddSeconds(-120);
    Check("availability: 120 s into a start is still not now (up to ~100 s, stuck only after 150 s)", L(fn) is { State: "notNow", Reason: "starting", ExpectedBackSec: 1 });
    fn.HudStartedAt = now.AddSeconds(-160);
    Check("availability: 160 s with no listener and no file is broken stuckStarting", L(fn) is { State: "broken", Reason: "stuckStarting" } st && st.WaitWith!.Contains("hud_log"), L(fn)?.Detail);
    var fsk = F(S("closed", 600), "unavailable"); fsk.HudStartedAt = now.AddSeconds(-160); fsk.State!.Heartbeat = now.AddSeconds(-300);
    Check("availability: 160 s with only the previous HUD's file is broken stuckStarting", L(fsk) is { State: "broken", Reason: "stuckStarting" });
    var fl = F(null, "hud_timeout"); fl.HudStartedAt = now.AddSeconds(-20);
    Check("availability: no file and the listener answered (timeout) stays today's error", L(fl) == null);
    Check("availability: not-now results tell agents not to toast", a1!.Note!.Contains("Don't guide.log") && a3!.Note!.Contains("starting")
        && ExileApiMcp.Bridge.HudAvailabilityLogic.Text(a3).Contains("Don't guide.log"));
    Check("availability: menu is not now, back when the user enters a character", L(F(S("menu", 30))) is { State: "notNow", Reason: "menu" } m && m.Detail.Contains("character"));
    var ft = F(S("ready", 30, true), "hud_timeout"); ft.TickAgeMs = 8000;
    Check("availability: ready but the main thread silent 8 s is broken frozen", L(ft) is { State: "broken", Reason: "frozen" });
    Check("availability: ready and ticking is nothing to add", L(F(S("ready", 30, true), "hud_timeout")) == null);
    var fb = F(S("ready", 30, true)); fb.State!.Heartbeat = now.AddSeconds(-40);
    Check("availability: heartbeat 40 s old while the HUD runs is broken frozen", L(fb) is { State: "broken", Reason: "frozen" } fz && fz.Detail.Contains("heartbeat"));
    var fo = F(S("menu", 30)); fo.OutageId = "i-1"; fo.OutagePhase = "waitingGame";
    Check("availability: an open outage is not now user-restart with its id", L(fo) is { State: "notNow", Reason: "user-restart", OutageId: "i-1" });
    var fo2 = F(null, "unavailable"); fo2.OutageId = "i-2"; fo2.OutagePhase = "down"; fo2.HudAlive = false;
    Check("availability: an outage without a file (older bridge) still says user-restart", L(fo2) is { Reason: "user-restart", OutageId: "i-2" });

    var parsed = ExileApiMcp.Bridge.HudStateReader.Parse("""{ "schema": 1, "game": "poe2", "phase": "loading", "since": "2026-10-11T11:59:56Z", "heartbeat": "2026-10-11T11:59:59Z", "inGame": true, "lastArea": "Clearfell", "lastLoadMs": 3100, "startup": { "sec": 41.2 }, "future": 7 }""");
    Check("hud-state.json: parsed, times UTC, unknown fields kept", parsed is { Phase: "loading", LastArea: "Clearfell", LastLoadMs: 3100, InGame: true }
        && parsed.Since == now.AddSeconds(-4) && parsed.Extra!.ContainsKey("startup") && parsed.Extra.ContainsKey("future"));
    Check("hud-state.json: empty text is unknown", ExileApiMcp.Bridge.HudStateReader.Parse(" ") == null);

    var loading = new ExileApiMcp.Bridge.BridgeException("poe2", "HUD is on a load screen", -32010, JObject.Parse("""{ "reason": "hud_loading", "sinceMs": 3100 }"""));
    var timeout = new ExileApiMcp.Bridge.BridgeException("poe2", "timed out", -32000, JObject.Parse("""{ "reason": "hud_timeout", "tickAgeMs": 9000 }"""));
    Check("bridge error: -32010 is a load screen (code alone too)", loading.IsHudLoading && loading.Reason == "hud_loading"
        && new ExileApiMcp.Bridge.BridgeException("poe2", "x", -32010).IsHudLoading && !timeout.IsHudLoading && timeout.Reason == "hud_timeout"
        && new ExileApiMcp.Bridge.BridgeException("poe2", "old bridge").Reason == null);

    var pause = new ExileApiMcp.Bridge.HudPause();
    var notNow = new ExileApiMcp.Bridge.HudUnavailableException(a1!, "x");
    var broken = new ExileApiMcp.Bridge.HudUnavailableException(L(fg)!, "y");
    var until = now.AddSeconds(10);
    Check("pause: not now pauses and moves the deadline; broken and other errors don't",
        pause.Holds(notNow) && pause.Extend(until, TimeSpan.FromSeconds(5)) == until.AddSeconds(5) && !pause.Holds(broken) && !pause.Holds(new ModelContextProtocol.McpException("z")));
    pause.Extend(until, ExileApiMcp.Bridge.HudPause.Max);
    Check("pause: capped at 10 min in all", !pause.Holds(notNow));
}

// ── "Back" means the game is in (IncidentAuto.BackVerdict) ──
{
    HudStateFile S(string phase, double sinceSec, bool inGame) => new() { Phase = phase, Since = now.AddSeconds(-sinceSec), Heartbeat = now.AddSeconds(-2), InGame = inGame };
    Check("back: no file (older bridge) resolves on the answer", IncidentAuto.BackVerdict(null, "poe2", now).Resolve);
    Check("back: in game resolves", IncidentAuto.BackVerdict(S("ready", 3, true), "poe2", now).Resolve);
    var menu = IncidentAuto.BackVerdict(S("menu", 300, false), "poe2", now);
    Check("back: at the menu keeps it open as 'HUD back, game not in yet (phase menu)'", !menu.Resolve && menu.KeepTitle == "PoE2 HUD back, game not in yet (phase menu)", menu.KeepTitle);
    var late = IncidentAuto.BackVerdict(S("menu", 31 * 60, false), "poe2", now);
    Check("back: 30 min at the menu resolves, saying so", late.Resolve && late.Note!.Contains("31 min"), late.Note);
    var stale = S("menu", 60, false); stale.Heartbeat = now.AddMinutes(-5);
    Check("back: a stale file (an older bridge answering now) resolves", IncidentAuto.BackVerdict(stale, "poe2", now).Resolve);
}

// ── Responses keyed by session id, "Label (2)" ──
{
    var i = new Incident { Id = "i-r", Kind = "custom", Key = "custom:x", Title = "x", State = "open" };
    i.Responses.Add(new IncidentResponse { Who = "Claude Desktop", Status = "ok", At = now });   // written before ids existed
    IncidentLogic.Respond(i, "Claude Desktop", "blocked", null, null, now, "Claude Desktop#100");
    Check("respond: an id-less response under the same label is replaced", i.Responses.Count == 1 && i.Responses[0].SessionId == "Claude Desktop#100");
    IncidentLogic.Respond(i, "Claude Desktop", "waiting", null, null, now, "Claude Desktop#200");
    Check("respond: another session with the same label keeps its own response", i.Responses.Count == 2);
    IncidentLogic.Respond(i, "Claude Desktop", "ok", "done", null, now, "Claude Desktop#100");
    Check("respond: the same session replaces only its own", i.Responses.Count == 2 && i.Responses.Single(r => r.SessionId == "Claude Desktop#100").Status == "ok"
        && i.Responses.Single(r => r.SessionId == "Claude Desktop#200").Status == "waiting");
    var names = IncidentLogic.ResponseNames(i);
    Check("respond: colliding labels display as 'Label' and 'Label (2)'", names.Count == 2 && names.Contains("Claude Desktop") && names.Contains("Claude Desktop (2)"), string.Join(",", names));
    Check("respond: one line shows the display names", IncidentLogic.OneLine(i, now).Contains("Claude Desktop (2): "), IncidentLogic.OneLine(i, now));
    var back = IncidentLogic.Parse(IncidentJson.Serialize(new IncidentFile { Incidents = [i] }));
    Check("respond: sessionId round-trips; absent when none", back.Incidents[0].Responses.All(r => r.SessionId != null)
        && !IncidentJson.Serialize(new IncidentResponse { Who = "a" }).Contains("sessionId"));
    Check("display names: same id twice is one session", IncidentLogic.DisplayNames([("a", "1"), ("a", "1"), ("a", "2"), ("b", null)]).SequenceEqual(["a", "a", "a (2)", "b"]));
}

// ── Outage incidents (Incidents/Outage.cs) ──
{
    var f = new IncidentFile { Rev = 41 };
    var (o, created) = OutageLogic.Raise(f, "poe2", "patch 0.4.1", "Claude Desktop#7", "Claude Desktop", 7, now);
    var info = OutageLogic.Info(o)!;
    Check("outage: raised as kind outage, key outage:poe2, phase announced, announcedRev = the write's rev",
        created && o is { Kind: "outage", Key: "outage:poe2", Scope: "poe2" } && info is { Phase: "announced", AnnouncedRev: 42, StopOnly: true, RequestedByLabel: "Claude Desktop" } && o.OwnerPid == 7);
    Check("outage: a second raise joins the open one", OutageLogic.Raise(f, "poe2", "again", "x#1", "x", 8, now) is { Created: false } again && again.Incident == o);
    Check("outage: open for its game only", OutageLogic.OpenFor(f, "poe2") == o && OutageLogic.OpenFor(f, "poe1") == null);
    OutageSession B(string id, string label) => new(id, label, "measuring", null);
    Check("outage: busy agents hold an announced outage", OutageLogic.Decide(info, null, 1, null, null, now) == OutageStep.Hold);
    info.Phase = "holding";
    Check("outage: holding with busy agents waits", OutageLogic.Decide(info, null, 2, null, null, now) == OutageStep.None);
    Check("outage: nobody busy alone never stops the HUD", OutageLogic.Decide(info, null, 0, null, null, now) == OutageStep.None);
    Check("outage: Restart now starts the countdown over busy agents", OutageLogic.Decide(info, "now", 3, null, null, now) == OutageStep.Countdown);
    Check("outage: Not now cancels", OutageLogic.Decide(info, "notNow", 0, null, null, now) == OutageStep.Cancel);
    var cd = new OutageInfo { Game = "poe2", Phase = "down", PhaseAt = now, StopAt = now.AddSeconds(8), Answer = "now" };
    Check("outage: counting down to stopAt waits", OutageLogic.Decide(cd, null, 0, null, null, now.AddSeconds(3)) == OutageStep.None);
    Check("outage: Cancel during the countdown cancels (outage.answer notNow)", OutageLogic.Decide(cd, "notNow", 0, null, null, now.AddSeconds(8)) == OutageStep.Cancel);
    Check("outage: at stopAt with no Cancel the HUD stops", OutageLogic.Decide(cd, "now", 0, null, null, now.AddSeconds(8)) == OutageStep.Stop
                                                          && OutageLogic.Decide(cd, null, 0, null, null, now.AddSeconds(9)) == OutageStep.Stop);
    Check("outage: the countdown ignores a fresh in-game file (the HUD still runs)",
        OutageLogic.Decide(cd, null, 0, new HudStateFile { Phase = "ready", InGame = true, Heartbeat = now.AddSeconds(2) }, true, now.AddSeconds(3)) == OutageStep.None);
    cd.StoppedAt = now.AddSeconds(9);
    Check("outage: after the stop, a heartbeat from before the stop is the old HUD",
        OutageLogic.Decide(cd, null, 0, new HudStateFile { Phase = "ready", InGame = true, Heartbeat = now.AddSeconds(5) }, true, now.AddSeconds(14)) == OutageStep.None
        && OutageLogic.Decide(cd, null, 0, new HudStateFile { Phase = "ready", InGame = true, Heartbeat = now.AddSeconds(60) }, true, now.AddSeconds(61)) == OutageStep.Back);
    {
        var fc = new IncidentFile { Rev = 1 };
        var (oc, _) = OutageLogic.Raise(fc, "poe1", "patch", "s#1", "S", 9, now);
        Check("outage: BeginCountdown -> phase down, stopAt, answer now, detail names the countdown",
            OutageLogic.BeginCountdown(oc, "t", now, now.AddSeconds(8)) && OutageLogic.Info(oc) is { Phase: "down", StoppedAt: null, Answer: "now" } ic
            && ic.StopAt == now.AddSeconds(8) && oc.Detail.Contains("Cancel") && oc.IsOpen, oc.Detail);
        Check("outage: MarkStopped records the stop", OutageLogic.MarkStopped(oc, now.AddSeconds(9)) && OutageLogic.Info(oc)!.StoppedAt == now.AddSeconds(9)
            && oc.Detail.Contains("restart-hud.ps1 -StopOnly"));
        Check("outage: stopAt round-trips in the incident JSON", IncidentJson.Serialize(fc).Contains("\"stopAt\""));
    }
    info.Phase = "down"; info.PhaseAt = now.AddMinutes(-2);   // no stopAt: an older runner's down (already stopped)
    var oldFile = new HudStateFile { Phase = "user-restart", Heartbeat = now.AddMinutes(-3) };
    var menuFile = new HudStateFile { Phase = "menu", Heartbeat = now.AddSeconds(-1), Since = now.AddSeconds(-20) };
    var readyFile = new HudStateFile { Phase = "ready", InGame = true, Heartbeat = now.AddSeconds(-1) };
    Check("outage: down stays down on the old HUD's last file", OutageLogic.Decide(info, null, 0, oldFile, true, now) == OutageStep.None);
    Check("outage: down -> waitingGame when a new bridge is at the menu", OutageLogic.Decide(info, null, 0, menuFile, true, now) == OutageStep.WaitingGame);
    Check("outage: down -> back when the new bridge is in game", OutageLogic.Decide(info, null, 0, readyFile, true, now) == OutageStep.Back);
    Check("outage: no HUD process, no progress", OutageLogic.Decide(info, null, 0, readyFile, false, now) == OutageStep.None);
    info.Phase = "waitingGame";
    Check("outage: waitingGame -> back on ready", OutageLogic.Decide(info, null, 0, readyFile, null, now) == OutageStep.Back
                                                 && OutageLogic.Decide(info, null, 0, menuFile, true, now) == OutageStep.None);

    Check("outage: participants recorded with unique display names", OutageLogic.SetParticipants(o, null, [B("a#1", "Claude Desktop")], [new("a#2", "Claude Desktop", null, null), new("w#3", "route-act2", null, null)], now)
        && OutageLogic.Info(o)!.Participants["a#2"].Display == "Claude Desktop (2)" && OutageLogic.Info(o)!.Participants["a#1"].State == "busy");
    Check("outage: the same readiness again writes nothing", !OutageLogic.SetParticipants(o, null, [B("a#1", "Claude Desktop")], [new("a#2", "Claude Desktop", null, null), new("w#3", "route-act2", null, null)], now.AddSeconds(2)));
    Check("outage: holding phase", OutageLogic.SetPhase(o, "holding", "t", now) && OutageLogic.Info(o)!.Phase == "holding" && o.IsOpen && !OutageLogic.SetPhase(o, "holding", "t", now));
    Check("outage: back resolves with resolution back", OutageLogic.SetPhase(o, "back", "t", now) && !o.IsOpen && o.Get("resolution")?.ToString() == "back"
        && OutageLogic.Info(o)!.ClosedAt == now && OutageLogic.OpenFor(f, "poe2") == null);
    var (o2, _) = OutageLogic.Raise(f, "poe2", "patch", "x#1", "x", 8, now);
    Check("outage: cancelled resolves with the reason; a new raise after back is a new incident", o2 != o && OutageLogic.SetPhase(o2, "cancelled", "t", now, "cancelled: the user pressed Not now")
        && o2.Get("resolution")?.ToString() == "cancelled: the user pressed Not now");
    Check("outage: kind outage passes validation", IncidentDraft.Kinds.Contains("outage"));

    var list = JObject.Parse("""
    { "sessions": [
        { "id": "a#1", "label": "route-act2", "connected": true, "doing": ["measuring perf"] },
        { "id": "b#2", "label": "Claude Desktop", "connected": true, "doing": [], "queued": [ { "id": "q1" } ] },
        { "id": "c#3", "label": "wisp", "connected": true, "leases": 1 },
        { "id": "d#4", "label": "idle-one", "connected": true },
        { "id": "e#5", "label": "control center", "connected": true, "infra": true },
        { "id": "f#6", "label": "gone", "connected": false, "doing": ["x"] },
        { "id": "me#9", "label": "me", "connected": true } ] }
    """);
    var (busy, ready) = OutageLogic.FromSessionList(list, "me#9");
    Check("readiness: doing, an attention item or a lease is busy; other connected agents ready; infra, disconnected and the requester skipped",
        busy.Select(b => b.Session).SequenceEqual(["a#1", "b#2", "c#3"]) && ready.Select(r => r.Session).SequenceEqual(["d#4"]) && busy[0].Doing == "measuring perf",
        $"busy {string.Join(",", busy.Select(b => b.Session))} ready {string.Join(",", ready.Select(r => r.Session))}");
    var st = OutageLogic.FromOutageState(JObject.Parse("""{ "id": "i-1", "phase": "holding", "answer": "now", "busy": [ { "session": "a#1", "label": "route-act2", "doing": "perf", "lease": "l-1" } ], "ready": [] }"""));
    Check("readiness: the bridge's outage.state", st is { } s && s.Answer == "now" && s.Busy.Single().Lease == "l-1" && s.Ready.Count == 0);
    Check("readiness: an older bridge's reply is no outage.state", OutageLogic.FromOutageState(JObject.Parse("""{ "game": "poe2", "query": "outage.state" }""")) == null);
}

// ── Deploys: a dead owner pid with a live same-label server is not orphaned ──
{
    var i = new Incident { Id = "i-d", Kind = "deploy", Key = "deploy:X", RaisedBy = "hud-not-now", RaisedAt = now.AddHours(-1), State = "open" };
    i.Set("ownerPid", 100); i.Set("owner", "hud-not-now");
    var beats = new Dictionary<int, ServerBeat>
    {
        [200] = new() { Label = "hud-not-now", Kind = "mcp", At = now.AddSeconds(-20) },
        [300] = new() { Label = "other", Kind = "mcp", At = now.AddSeconds(-20) },
        [400] = new() { Label = "hud-not-now", Kind = "mcp", At = now.AddMinutes(-5) },
    };
    Check("same label: a fresh beat of another pid with the label", DeployIncidents.SameLabelServer(beats, "hud-not-now", 100, (_, _) => true, now) == 200
        && DeployIncidents.SameLabelServer(beats, "hud-not-now", 200, (_, _) => true, now) == null
        && DeployIncidents.SameLabelServer(beats, "hud-not-now", 100, (p, _) => p != 200, now) == null);
    var (orph, why) = DeployIncidents.Judge(i, now, (_, _) => false, now, (_, _) => 200);
    Check("same label: owner gone but its label's server beats -> not orphaned, it adopts", !orph && why.Contains("pid 200"), why);
    Check("same label: no such server -> orphaned as before", DeployIncidents.Judge(i, now, (_, _) => false, now, (_, _) => null).Orphaned);
}


// ── Store: outage on disk, responses by session id, server heartbeats (temp board) ──
{
    var dir = Path.Combine(Path.GetTempPath(), "hexile-incidents-test-" + Guid.NewGuid().ToString("N")[..8]);
    Environment.SetEnvironmentVariable("HEXILE_INCIDENT_DIR", dir);
    try
    {
        var (o, created) = IncidentStore.Update(f => OutageLogic.Raise(f, "poe1", "patch", "s#1", "s", 1234, DateTime.UtcNow));
        var board = IncidentStore.Read();
        var stored = IncidentLogic.Find(board, o.Id)!;
        Check("store outage: written, announcedRev is the rev it was written at", created && OutageLogic.Info(stored)?.AnnouncedRev == board.Rev && stored.ChangedRev == board.Rev,
            $"announcedRev {OutageLogic.Info(stored)?.AnnouncedRev} rev {board.Rev}");
        Check("store outage: held for its game", OutageLogic.OpenFor(board, "poe1")?.Id == o.Id && ExileApiMcp.Tools.SessionTools.HeldBy("poe1") is { Error: "held_by_outage", Phase: "announced" } h && h.Id == o.Id);
        IncidentStore.Respond(o.Id, "ok", null, null, "Claude Desktop", "cd#1");
        IncidentStore.Respond(o.Id, "blocked", null, null, "Claude Desktop", "cd#2");
        Check("store respond: two sessions, one label, two responses", IncidentLogic.Find(IncidentStore.Read(), o.Id)!.Responses.Count == 2);
        IncidentStore.Update(f => IncidentLogic.Find(f, o.Id) is { } i && OutageLogic.SetPhase(i, "cancelled", "t", DateTime.UtcNow, "cancelled: test"));
        Check("store outage: cancelled releases the hold", ExileApiMcp.Tools.SessionTools.HeldBy("poe1") == null);
        IncidentStore.BeatServer(4242, "hud-not-now", "mcp");
        IncidentStore.BeatServer(4243, "other", "mcp-http");
        var beats = IncidentStore.ReadServerBeats();
        Check("server beats: per pid with label and kind", beats.Count == 2 && beats[4242].Label == "hud-not-now" && beats[4243].Kind == "mcp-http" && beats[4242].At > DateTime.UtcNow.AddMinutes(-1));
    }
    finally
    {
        Environment.SetEnvironmentVariable("HEXILE_INCIDENT_DIR", null);
        try { Directory.Delete(dir, true); } catch { }
    }
}

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) FAILED.");
return failures;
