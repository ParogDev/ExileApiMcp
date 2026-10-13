// The bridge DTO contract (Bridge/BridgeContract.cs): the MCP's mirrors against the committed snapshot of the bridge's
// contract.schema, the comparison rules on canned schemas, and the CLI that refreshes the snapshot / emits a mirror.
using ExileApiMcp.Bridge;
using Newtonsoft.Json.Linq;

internal static class ContractChecks
{
    public static void Run(Action<string, bool, string?> check)
    {
        void C(string n, bool ok, string? detail = null) => check(n, ok, detail);
        var snap = BridgeSchema.Snapshot;
        C("contract: snapshot embedded and parsed", snap is { Types.Count: > 0 }, BridgeSchema.SnapshotError);
        if (snap != null)
        {
            var pairs = new List<string>();
            var drift = BridgeContract.Check(snap, compared: pairs);
            Console.WriteLine($"      compared {pairs.Count} type pairs: {string.Join(", ", pairs)}");
            C($"contract: {BridgeContract.Mirrors.Length} MCP mirrors match bridge snapshot {snap.Hash}", drift.Count == 0,
                string.Join("\n      ", drift.Select(d => d.Text)));
        }

        // The rules, on a canned schema: each kind of drift is named at its field, narrow mirrors and MCP-only fields aren't.
        var schema = BridgeSchema.Parse(JToken.Parse("""
        { "version": 1, "hash": "x", "types": [
          { "name": "RootDto", "kind": "object", "fields": [
            { "json": "count", "type": "integer" }, { "json": "ratio", "type": "number" }, { "json": "name", "type": "string" },
            { "json": "child", "type": "object:ChildDto" }, { "json": "items", "type": "list<object:ChildDto>" },
            { "json": "extra", "type": "string" }, { "json": "kind", "type": "enum:KindEnum" },
            { "json": "maybe", "type": "integer", "nullable": true }, { "json": "omitted", "type": "integer", "nullable": true, "omitNull": true } ] },
          { "name": "BoardDto", "kind": "object", "open": true, "fields": [ { "json": "must", "type": "string", "nullable": false }, { "json": "fine", "type": "string", "nullable": true } ] },
          { "name": "ChildDto", "kind": "object", "fields": [ { "json": "id", "type": "string" } ] },
          { "name": "KindEnum", "kind": "enum", "values": ["A", "B"] } ] }
        """));
        var d = BridgeContract.Check(schema, [new ContractMirror(typeof(FixtureRoot), "RootDto")]);
        C("contract rule: integer read as double is fine, number read as int is a mismatch",
            !d.Any(x => x.Field == "count") && d.Any(x => x.Field == "ratio" && x.Kind == "type_mismatch" && x.BridgeShape == "number" && x.McpShape == "integer"),
            string.Join("; ", d.Select(x => x.Text)));
        C("contract rule: bridge field the mirror lacks is named", d.Any(x => x.Field == "extra" && x.Kind == "missing_in_mcp"));
        C("contract rule: MCP field the bridge never writes is named", d.Any(x => x.Field == "ghost" && x.Kind == "missing_in_bridge"));
        C("contract rule: nested types pair through fields (child.id int vs string)",
            d.Any(x => x.McpType == nameof(FixtureChild) && x.Field == "id" && x.Kind == "type_mismatch" && x.BridgeShape == "string" && x.McpShape == "integer"));
        C("contract rule: enum read as int is fine", !d.Any(x => x.Field == "kind"));
        C("contract rule: drift text names type, field, bridge and MCP shape",
            d.First(x => x.Field == "ratio").Text == "FixtureRoot.ratio (bridge RootDto): type mismatch (bridge number, MCP integer)", d.First(x => x.Field == "ratio").Text);
        var narrow = BridgeContract.Check(schema, [new ContractMirror(typeof(FixtureRoot), "RootDto", Narrow: "fixture", McpOnly: ["ghost"])]);
        C("contract rule: a narrow mirror and its MCP-only fields aren't drift", !narrow.Any(x => x.Field is "extra" or "ghost"));
        C("contract rule: a gone bridge type is named",
            BridgeContract.Check(schema, [new ContractMirror(typeof(FixtureChild), "GoneDto")]).Single().Kind == "bridge_type_gone");
        C("contract rule: a nullable bridge field read into a non-null MCP field is a nullability drift, named apart from type drift",
            d.SingleOrDefault(x => x.Field == "maybe") is { Kind: "nullability_mismatch" } m
            && m.Text == "FixtureRoot.maybe (bridge RootDto): nullability mismatch (bridge integer nullable, MCP integer non-null)",
            string.Join("; ", d.Where(x => x.Field == "maybe").Select(x => x.Text)));
        C("contract rule: an omitNull bridge field never sends null, so a non-null MCP field is fine", !d.Any(x => x.Field == "omitted"));
        var w = BridgeContract.Check(schema, [new ContractMirror(typeof(FixtureBoard), "BoardDto", McpWrites: true)]);
        C("contract rule: a written mirror: MCP may write null where the bridge reads non-null; extra MCP fields aren't drift",
            w.Count == 1 && w[0] is { Field: "must", Kind: "nullability_mismatch" }, string.Join("; ", w.Select(x => x.Text)));
        // reload_plugin reads hud.reload_status through its mirror: a finished result, unknown fields kept.
        var rs = JObject.Parse("""
        { "last": { "plugin": "Whats A Mirage", "queuedAt": "2026-10-13T20:00:00Z", "startedAt": "2026-10-13T20:00:01Z",
          "finishedAt": "2026-10-13T20:00:04Z", "durationMs": 3012, "ok": true, "initialized": true, "instances": 1,
          "warnings": [], "staleBridgeMethods": ["Mirage.Get"], "newField": 1 }, "pending": [] }
        """).ToObject<ExileApiMcp.Tools.ReloadStatusReply>();
        C("contract: hud.reload_status reads into its mirror",
            rs?.Last is { Plugin: "Whats A Mirage", Ok: true, DurationMs: 3012, Initialized: true, FinishedAt: not null } l
            && l.StartedAt.ToUniversalTime() == new DateTime(2026, 10, 13, 20, 0, 1, DateTimeKind.Utc)
            && l.StaleBridgeMethods?.Single() == "Mirage.Get" && l.Extra?.ContainsKey("newField") == true && rs.Pending?.Count == 0);
        var emitted = BridgeContract.EmitMirror(schema, "RootDto", "Fx");
        C("contract: mirror generator emits the type and what it reaches",
            emitted.Contains("public sealed class FxRoot") && emitted.Contains("public FxChild? Child") && emitted.Contains("List<FxChild>? Items") && emitted.Contains("[JsonExtensionData]"), emitted);
    }

    /// <summary>--refresh-contract [poe1|poe2]: write Bridge/bridge-contract.json from the running bridge.
    /// --emit-mirror &lt;BridgeType&gt; [prefix]: print a C# mirror of a snapshot type.</summary>
    public static async Task<int?> Cli(string[] args)
    {
        if (args.Length == 0) return null;
        if (args[0] == "--refresh-contract")
        {
            var game = args.Length > 1 ? args[1] : "poe2";
            var path = Path.Combine(RepoRoot(), "Bridge", "bridge-contract.json");
            Console.WriteLine($"{game}: {await BridgeContract.RefreshSnapshotAsync(game, path, CancellationToken.None)} -> {path}");
            Console.WriteLine("Rebuild and run the tests: a mirror that disagrees with the new snapshot fails, naming the field.");
            return 0;
        }
        if (args[0] == "--suggest-mirrors")
        {
            // Bridge types and the MCP DTOs whose JSON fields overlap them most: candidates for BridgeContract.Mirrors.
            if (BridgeSchema.Snapshot is not { } s) { Console.Error.WriteLine(BridgeSchema.SnapshotError); return 1; }
            var mcp = typeof(BridgeContract).Assembly.GetTypes()
                .Where(t => t.Namespace == "ExileApiMcp.Tools" && t.IsClass && t.IsPublic && !t.IsAbstract)
                .Select(t => (t, f: BridgeContract.McpFields(t).Keys.Select(k => k.ToLowerInvariant()).ToHashSet()))
                .Where(x => x.f.Count > 0).ToList();
            var declared = BridgeContract.Mirrors.Select(m => m.Bridge).ToHashSet();
            foreach (var bt in s.Types.Values.Where(t => t.Kind == "object").OrderBy(t => t.Name))
            {
                var bf = bt.Fields.Select(f => f.Json.ToLowerInvariant()).ToHashSet();
                var best = mcp.Select(x => (x.t, inter: x.f.Intersect(bf).Count(), j: (double)x.f.Intersect(bf).Count() / Math.Max(1, x.f.Union(bf).Count())))
                    .Where(x => x.inter >= 2).OrderByDescending(x => x.j).Take(3)
                    .Select(x => $"{x.t.Name} j={x.j:F2} {x.inter}/{bf.Count}");
                Console.WriteLine($"{(declared.Contains(bt.Name) ? "*" : " ")} {bt.Name} ({bf.Count}): {string.Join("; ", best)}");
            }
            return 0;
        }
        if (args[0] == "--emit-mirror" && args.Length > 1)
        {
            if (BridgeSchema.Snapshot is not { } s) { Console.Error.WriteLine(BridgeSchema.SnapshotError); return 1; }
            if (!s.Types.ContainsKey(args[1])) { Console.Error.WriteLine($"no bridge type {args[1]} in snapshot {s.Hash}"); return 1; }
            Console.WriteLine(BridgeContract.EmitMirror(s, args[1], args.Length > 2 ? args[2] : ""));
            return 0;
        }
        return null;
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ExileApiMcp.csproj"))) return d.FullName;
        throw new InvalidOperationException("ExileApiMcp.csproj not found above " + AppContext.BaseDirectory);
    }
}

internal sealed class FixtureRoot
{
    public long? Count { get; set; }
    public int? Ratio { get; set; }
    public string? Name { get; set; }
    public FixtureChild? Child { get; set; }
    public List<FixtureChild>? Items { get; set; }
    public int? Kind { get; set; }
    public string? Ghost { get; set; }
    public int Maybe { get; set; }
    public int Omitted { get; set; }
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

internal sealed class FixtureChild
{
    public int? Id { get; set; }
}

/// <summary>A file the MCP writes and the bridge reads (McpWrites).</summary>
internal sealed class FixtureBoard
{
    public string? Must { get; set; }
    public string Fine { get; set; } = "";
    public string? OursOnly { get; set; }
}
