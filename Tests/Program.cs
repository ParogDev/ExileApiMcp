// Unit checks for the incident board (Incidents/): parsing, raise / respond / resolve / prune, the bridge diagnosis from
// canned facts and netsh output, deploy orphan judgement, and the store's mutex + atomic writes against a temp folder.
// No test framework: each Check prints pass/FAIL; the exit code is the number of failures.
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
    const string agentsJson = """
        [ { "pid": 1, "kind": "interactive", "sessionId": "s0", "status": "busy" },
          { "pid": 26424, "id": "98b400ca", "kind": "background", "sessionId": "98b400ca-5279", "status": "idle", "state": "done" },
          { "id": "c37e5f7f", "kind": "background", "sessionId": "c37e5f7f-3394", "state": "done" } ]
        exit 0
        """;
    var live = ExileApiMcp.Dispatch.FeedbackKit.ParseAgent(agentsJson, "98b400ca");
    var stopped = ExileApiMcp.Dispatch.FeedbackKit.ParseAgent(agentsJson, "c37e5f7f");
    Check("agents: state, session id, alive by pid", live is { SessionId: "98b400ca-5279", State: "done", Alive: true } && stopped is { Alive: false });
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

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) FAILED.");
return failures;
