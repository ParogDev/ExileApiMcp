using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Bridge;

/// <summary>One serialized member of a bridge DTO (bridge Shared/ContractSchema.cs, method contract.schema).</summary>
public sealed record BridgeContractField(string Json, string Type, bool Nullable, bool OmitNull, string? Clr, string? ClrType);

/// <summary>One bridge DTO type or enum.</summary>
public sealed record BridgeContractType(string Name, string Kind, bool Open, IReadOnlyList<BridgeContractField> Fields, IReadOnlyList<string> Values);

/// <summary>The bridge's DTO contract: the committed snapshot (Bridge/bridge-contract.json) or a live contract.schema reply.</summary>
public sealed class BridgeSchema
{
    public int Version { get; init; }
    public string Hash { get; init; } = "";
    public IReadOnlyDictionary<string, BridgeContractType> Types { get; init; } = new Dictionary<string, BridgeContractType>();

    public static BridgeSchema Parse(JToken t)
    {
        var types = new Dictionary<string, BridgeContractType>(StringComparer.Ordinal);
        foreach (var x in t["types"] as JArray ?? [])
        {
            var name = x["name"]?.ToString() ?? "";
            types[name] = new BridgeContractType(name, x["kind"]?.ToString() ?? "object", x["open"]?.Value<bool?>() == true,
                (x["fields"] as JArray ?? []).Select(f => new BridgeContractField(f["json"]?.ToString() ?? "", f["type"]?.ToString() ?? "any",
                    f["nullable"]?.Value<bool?>() == true, f["omitNull"]?.Value<bool?>() == true, f["clr"]?.ToString(), f["clrType"]?.ToString())).ToList(),
                (x["values"] as JArray ?? []).Select(v => v.ToString()).ToList());
        }
        return new BridgeSchema { Version = t["version"]?.Value<int?>() ?? 0, Hash = t["hash"]?.ToString() ?? "", Types = types };
    }

    private static readonly Lazy<(BridgeSchema? Schema, string? Error)> SnapshotLazy = new(() =>
    {
        try
        {
            // A test of the live path: another snapshot file (e.g. one with a different hash) in place of the embedded one.
            if (Environment.GetEnvironmentVariable("HEXILE_BRIDGE_CONTRACT") is { Length: > 0 } file)
                return (Parse(JToken.Parse(File.ReadAllText(file))), null);
            using var s = typeof(BridgeSchema).Assembly.GetManifestResourceStream(BridgeContract.SnapshotResource);
            if (s == null) return (null, $"embedded resource {BridgeContract.SnapshotResource} missing from this build");
            using var r = new StreamReader(s);
            return (Parse(JToken.Parse(r.ReadToEnd())), null);
        }
        catch (Exception ex) { return (null, $"{BridgeContract.SnapshotResource}: {ex.GetType().Name}: {ex.Message}"); }
    });

    /// <summary>The snapshot committed with this MCP build (refreshed by <c>dotnet run --project Tests -- --refresh-contract</c>).</summary>
    public static BridgeSchema? Snapshot => SnapshotLazy.Value.Schema;
    public static string? SnapshotError => SnapshotLazy.Value.Error;
}

/// <summary>
/// One mirror: an MCP DTO that reads the JSON of a bridge DTO. Types they reach through fields are paired by themselves
/// (field by field), so only the roots are declared. <paramref name="Narrow"/> = the MCP keeps only part of the bridge
/// type on purpose (the rest passes through [JsonExtensionData] Extra): bridge fields it lacks aren't drift; the reason
/// says why. <paramref name="McpOnly"/> = fields the MCP adds itself (never on the bridge's wire). <paramref name="BridgeOnly"/>
/// = bridge fields the mirror leaves out on purpose (handled before the mirror reads the reply, or never on the wire).
/// <paramref name="McpWrites"/> = the other direction: the MCP writes this JSON and the bridge reads it (e.g. incidents.json).
/// A bridge field the MCP lacks is then drift the same way, an MCP field the bridge ignores is not, and nullability is
/// judged from the writer's side (the MCP may write null where the bridge declares a non-null field).
/// </summary>
public sealed record ContractMirror(Type Mcp, string Bridge, string? Narrow = null, string[]? McpOnly = null, string[]? BridgeOnly = null, bool McpWrites = false);

/// <summary>A difference between an MCP mirror and the bridge DTO it reads, named at the field.</summary>
public sealed record ContractDrift(string McpType, string BridgeType, string Field, string Kind, string? BridgeShape, string? McpShape)
{
    /// <summary>missing_in_bridge (the MCP reads a field the bridge never writes) | missing_in_mcp (the bridge writes a field
    /// the mirror lacks and the mirror isn't declared narrow) | type_mismatch | nullability_mismatch (the writer may send null
    /// where the reader declares the field non-null: a value type fails to deserialize, a reference type breaks the
    /// reader's non-null assumption) | bridge_type_gone (the declared bridge type isn't in the contract).</summary>
    public string Text => Kind switch
    {
        "bridge_type_gone" => $"{McpType}: bridge type {BridgeType} is gone from the contract",
        "nullability_mismatch" => $"{McpType}.{Field}{Of}: nullability mismatch (bridge {BridgeShape}, MCP {McpShape})",
        "missing_in_bridge" => $"{McpType}.{Field}{Of}: missing in the bridge (MCP {McpShape}; the bridge never writes it)",
        "missing_in_mcp" => $"{McpType}.{Field}{Of}: extra on the bridge (bridge {BridgeShape}; not in the MCP mirror, which isn't declared narrow)",
        _ => $"{McpType}.{Field}{Of}: type mismatch (bridge {BridgeShape}, MCP {McpShape})",
    };

    private string Of => McpType == BridgeType || McpType + "Dto" == BridgeType ? "" : $" (bridge {BridgeType})";
}

/// <summary>
/// The MCP side of the bridge's DTO contract: which MCP DTOs mirror which bridge DTOs (<see cref="Mirrors"/>), the shape
/// of an MCP DTO under System.Text.Json's rules as the MCP reads it (<see cref="Dto"/> options: camelCase, case-insensitive),
/// and the field-by-field comparison. The Tests project runs <see cref="Check"/> against the committed snapshot; bridge_status
/// and hud_health_report run <see cref="LiveAsync"/> when a bridge's hello.contract.hash differs from it.
/// </summary>
public static class BridgeContract
{
    public const string SnapshotResource = "bridge/bridge-contract.json";

    // ── Mirrors ───────────────────────────────────────────────────────────────────────────────────────────────────────
    // A mirror whose bridge type has a field the MCP doesn't declare is drift unless it is narrow (with the reason).
    // Nested types pair automatically through the fields of these roots.
    public static readonly ContractMirror[] Mirrors =
    [
        // hello (bridge_status, hud_health_report); its reflection preflight pairs through the field
        new(typeof(Tools.BridgeHello), "HelloDto"),
        // query: player | area | entities | npcdialog | mapdata | ui | stash | all: each tool reads one query type's slice
        new(typeof(Tools.GameOverviewResult), "BridgeResponse", Narrow: "get_all: the 'all' query's fields only", McpOnly: ["_truncated"]),
        new(typeof(Tools.PlayerResult), "BridgeResponse", Narrow: "get_player: the 'player' query's field only"),
        new(typeof(Tools.AreaResult), "BridgeResponse", Narrow: "get_area: the 'area' query's field only"),
        new(typeof(Tools.EntitiesResult), "BridgeResponse", Narrow: "get_entities: the entities queries' fields only", McpOnly: ["_truncated"]),
        new(typeof(Tools.NpcDialogResult), "BridgeResponse", Narrow: "get_npc_dialog: the 'npcdialog' query's field only"),
        new(typeof(Tools.UiPanelsInfo), "UiDto"),
        new(typeof(Tools.StashTabInfo), "StashTabDto"),
        // recording.* (record_*, recording_*)
        new(typeof(Tools.RecordingSummaryResult), "RecordingSummaryDto"),
        new(typeof(Tools.RecordingInfoResult), "RecordingLoadResponse"),
        // RecordingStatusResult is the union of record:start / record:stop / recording status: each pair lists what the other two
        // supply. interrupted never reaches it: CutByRestart answers that case (a restart cut the recording) first.
        new(typeof(Tools.RecordingStatusResult), "RecordingStatusResponse", McpOnly: ["sizeBytes"], BridgeOnly: ["interrupted"]),
        new(typeof(Tools.RecordingStatusResult), "RecordingStartResponse", McpOnly: ["isRecording", "frames", "durationMs", "sizeBytes"]),
        new(typeof(Tools.RecordingStatusResult), "RecordingStopResponse", McpOnly: ["isRecording", "intervalMs"]),
        new(typeof(Tools.RecordingSearchResult), "RecordingSearchResponse"),
        new(typeof(Tools.RecordingFrameResult), "SnapshotResponse"),
        new(typeof(Tools.SnapshotResult), "SnapshotCaptureResponse"),
        new(typeof(Tools.RecordingFileInfo), "RecordingFileDto"),
        // stats.* (get_stat, stats_page, set_stat_*, stats_ui_state)
        new(typeof(Tools.StatDetailResult), "StatDetailResponse", BridgeOnly: ["error"]),   // an error reply is a tool error (TypedReply) before the mirror reads it
        new(typeof(Tools.StatsMutationResult), "StatsMutationResponse"),
        new(typeof(Tools.StatsPageResult), "StatsPageResponse"),
        new(typeof(Tools.StatsUiStateResult), "StatsUiStateResponse"),
        // guide.verdicts, observe.layers, observe.ui_catalogue
        new(typeof(Tools.VerdictInfo), "WhatsAnAiBridge.HighlightVerdict"),
        new(typeof(Tools.LayerSpec), "WhatsAnAiBridge.LayerSpec"),
        new(typeof(Tools.UiCatalogueEntry), "WhatsAnAiBridge.UiCatalogueEntry", BridgeOnly: ["viewHashes"]),   // persisted only: observe.ui_catalogue removes it from the reply
        // <bridge dir>\hud-state.json (Bridge/HudState.cs, read with Newtonsoft): not now vs broken
        // error / reloading / startup are diagnosis detail the availability logic doesn't judge by (they pass through Extra).
        new(typeof(HudStateFile), "HudStateDto", BridgeOnly: ["error", "reloading", "startup"]),
        // object.find / object.snapshot (find_in_object, watch_object)
        new(typeof(Tools.ObjectFindResponse), "ObjectFindResponse"),
        new(typeof(Tools.ObjectSnapshotResponse), "ObjectSnapshotResponse"),
        new(typeof(Tools.ObjectPathError), "ObjectPathErrorDto"),
        // script.run / script.result (run_csharp)
        new(typeof(Tools.ScriptResult), "ScriptResultDto"),
        // hud.reload_plugin / hud.reload_status (reload_plugin, Tools/ReloadDtos.cs)
        new(typeof(Tools.ReloadQueuedReply), "ReloadQueuedResponse"),
        new(typeof(Tools.ReloadStatusReply), "ReloadStatusResponse"),
        // outage.state (Incidents/Outage.cs readiness for hud_restart mode=user): the card's own fields aren't read
        new(typeof(Incidents.OutageStateReply), "OutageStateDto", Narrow: "readiness: busy / ready lists and the card's answer only"),
        new(typeof(Incidents.OutageParty), "OutageParty", Narrow: "readiness: who and what they are doing; since / until (the card's timer) not read"),
        // incidents.json the other way round: the MCP writes it (Incidents/IncidentStore.cs), the bridge's in-game incident
        // card reads it. The incident's "outage" field is written through Extra (OutageLogic.SetInfo), so it pairs as its own root.
        new(typeof(Incidents.IncidentFile), "IncidentBoardDto", McpWrites: true),
        new(typeof(Incidents.Incident), "IncidentDto", BridgeOnly: ["outage"], McpWrites: true),
        new(typeof(Incidents.OutageInfo), "IncidentOutageDto", McpWrites: true),
        // Not mirrored: HudPluginsResponse (hud.plugins): the MCP never calls it; hud_plugins reads the HUD folders on disk
        // and instrument.state.
    ];

    /// <summary>Compare every mirror (and the types its fields reach) with a bridge schema.</summary>
    public static List<ContractDrift> Check(BridgeSchema schema, IEnumerable<ContractMirror>? mirrors = null, List<string>? compared = null)
    {
        var drift = new List<ContractDrift>();
        var declared = (mirrors ?? Mirrors).ToList();
        var narrow = declared.Where(m => m.Narrow != null).Select(m => (m.Mcp, m.Bridge)).ToHashSet();
        var mcpOnly = declared.Where(m => m.McpOnly != null).ToDictionary(m => (m.Mcp, m.Bridge), m => m.McpOnly!.ToHashSet(StringComparer.OrdinalIgnoreCase));
        var bridgeOnly = declared.Where(m => m.BridgeOnly != null).ToDictionary(m => (m.Mcp, m.Bridge), m => m.BridgeOnly!.ToHashSet(StringComparer.OrdinalIgnoreCase));
        var writes = declared.Where(m => m.McpWrites).Select(m => (m.Mcp, m.Bridge)).ToHashSet();
        var queue = new Queue<(Type Mcp, string Bridge)>(declared.Select(m => (m.Mcp, m.Bridge)));
        var seen = new HashSet<(Type, string)>();
        while (queue.Count > 0)
        {
            var (mt, bn) = queue.Dequeue();
            if (!seen.Add((mt, bn))) continue;
            compared?.Add($"{mt.Name}={bn}");
            if (!schema.Types.TryGetValue(bn, out var bt)) { drift.Add(new(mt.Name, bn, "", "bridge_type_gone", null, null)); continue; }
            if (bt.Kind == "enum") continue;
            var mFields = McpFields(mt);
            var only = mcpOnly.GetValueOrDefault((mt, bn));
            var mcpWrites = writes.Contains((mt, bn));
            var nested = new Queue<(Type, string)>();
            foreach (var bf in bt.Fields)
            {
                // The MCP reads case-insensitively (Dto.Options), so a casing difference is not drift.
                if (!mFields.TryGetValue(bf.Json, out var mf))
                {
                    if (!narrow.Contains((mt, bn)) && bridgeOnly.GetValueOrDefault((mt, bn))?.Contains(bf.Json) != true)
                        drift.Add(new(mt.Name, bn, bf.Json, "missing_in_mcp", bf.Type, null));
                    continue;
                }
                var why = Compare(bf.Type, mf.Shape, mf.Type, schema, nested);
                if (why != null) { drift.Add(new(mt.Name, bn, bf.Json, "type_mismatch", bf.Type, why)); continue; }
                // Nullability, from the reader's side: the writer may send null (the bridge writes nulls unless omitNull)
                // where the reader declares the field non-null.
                if (mcpWrites ? mf.Nullable && !bf.Nullable : bf.Nullable && !bf.OmitNull && !mf.Nullable)
                    drift.Add(new(mt.Name, bn, bf.Json, "nullability_mismatch", NullText(bf.Type, bf.Nullable), NullText(mf.Shape, mf.Nullable)));
            }
            // Types reached through this pair's fields inherit its direction.
            foreach (var n in nested) { queue.Enqueue(n); if (mcpWrites) writes.Add(n); }
            // A reader that ignores a field the MCP writes is fine; a field the MCP reads that the bridge never writes is not.
            if (!mcpWrites)
                foreach (var (json, mf) in mFields)
                    if (!bt.Fields.Any(f => string.Equals(f.Json, json, StringComparison.OrdinalIgnoreCase)) && only?.Contains(json) != true && !bt.Open)
                        drift.Add(new(mt.Name, bn, json, "missing_in_bridge", null, mf.Shape));
        }
        return drift;
    }

    private static string NullText(string shape, bool nullable) => nullable ? $"{shape} nullable" : $"{shape} non-null";

    /// <summary>Null when the MCP shape reads what the bridge writes; else the MCP shape as reported. Pairs nested types.</summary>
    private static string? Compare(string b, string m, Type? mType, BridgeSchema schema, Queue<(Type, string)> queue)
    {
        if (m == "any" || b == m && !b.StartsWith("object:") && !b.StartsWith("list<") && !b.StartsWith("map<")) return null;
        if (b.StartsWith("external:") || b == "any") return null;   // nothing of ours to compare
        if (b == "integer" && m == "number") return null;            // widening read
        if (b.StartsWith("enum:") && (m == "integer" || m == "number" || m.StartsWith("enum:"))) return null;   // enums go out as integers
        if (b == "integer" && m.StartsWith("enum:")) return null;
        if ((b == "datetime" && m == "string") || (b == "string" && m == "datetime")) return null;   // ISO text either way
        if (b.StartsWith("list<") && m.StartsWith("list<"))
            return Compare(Inner(b), Inner(m), ElementType(mType), schema, queue) is { } e ? $"list<{e}>" : null;
        if (b.StartsWith("map<") && m.StartsWith("map<"))
            return Compare(Inner(b), Inner(m), MapValueType(mType), schema, queue) is { } e ? $"map<{e}>" : null;
        if (b.StartsWith("object:") && m.StartsWith("map<")) return null;   // read as a bag of fields
        if (b.StartsWith("object:") && m.StartsWith("object:") && mType != null)
        {
            var bn = b["object:".Length..];
            queue.Enqueue((Unwrap(mType), bn));
            return null;
        }
        return m;
    }

    private static string Inner(string s) => s[(s.IndexOf('<') + 1)..^1];
    private static Type Unwrap(Type t) => Nullable.GetUnderlyingType(t) ?? t;

    // ── MCP shapes (System.Text.Json, Dto.Options) ──────────────────────────────────────────────────────────────────

    /// <param name="Nullable">The property may hold null: Nullable&lt;T&gt;, a T? reference, or a reference whose nullability
    /// isn't annotated (unknown counts as nullable, never as drift).</param>
    public sealed record McpField(string Shape, Type Type, bool Nullable = true);

    /// <summary>The JSON fields of an MCP DTO, keyed case-insensitively (Dto.Options reads that way).</summary>
    public static Dictionary<string, McpField> McpFields(Type t)
    {
        var d = new Dictionary<string, McpField>(StringComparer.OrdinalIgnoreCase);
        var nullability = new NullabilityInfoContext();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.GetMethod == null || !p.CanWrite && !IsCollection(p.PropertyType)) continue;
            // Both serializers' attributes: most mirrors are System.Text.Json (Dto.Options), file readers such as
            // HudStateFile are Newtonsoft (case-insensitive by default, so camelCase is how they match too).
            if (p.IsDefined(typeof(JsonExtensionDataAttribute)) || p.IsDefined(typeof(Newtonsoft.Json.JsonExtensionDataAttribute))) continue;
            if (p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }
                || p.IsDefined(typeof(Newtonsoft.Json.JsonIgnoreAttribute))) continue;
            var name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                       ?? p.GetCustomAttribute<Newtonsoft.Json.JsonPropertyAttribute>()?.PropertyName
                       ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            d[name] = new McpField(Shape(p.PropertyType), p.PropertyType, nullability.Create(p).ReadState != NullabilityState.NotNull);
        }
        return d;
    }

    public static string Shape(Type t)
    {
        t = Unwrap(t);
        if (t == typeof(string) || t == typeof(char) || t == typeof(Guid) || t == typeof(TimeSpan)) return "string";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "datetime";
        if (t.IsEnum) return $"enum:{t.Name}";
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)
            || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort)) return "integer";
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "number";
        if (t == typeof(object) || t == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(t) || typeof(JToken).IsAssignableFrom(t)) return "any";
        if (MapValueType(t) is { } v) return $"map<{Shape(v)}>";
        if (ElementType(t) is { } e) return $"list<{Shape(e)}>";
        return $"object:{t.Name}";
    }

    private static bool IsCollection(Type t) => t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t);

    private static Type? ElementType(Type? t)
    {
        if (t == null) return null;
        t = Unwrap(t);
        if (t == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(t) || MapValueType(t) != null) return null;
        if (t.IsArray) return t.GetElementType();
        return t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0]
               ?? typeof(object);
    }

    private static Type? MapValueType(Type? t)
    {
        if (t == null) return null;
        t = Unwrap(t);
        var d = t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType &&
            (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        return d?.GetGenericArguments()[1];
    }

    // ── Live check ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Results per live hash: the comparison is pure (schema x this build's mirrors), so one per hash is enough.</summary>
    private static readonly ConcurrentDictionary<string, Tools.BridgeContractStatus> LiveByHash = new();

    /// <summary>hello.contract against the snapshot: equal hashes = in step (the tests check the snapshot); a moved hash
    /// fetches contract.schema once (cached per hash) and names every mirror field that disagrees.</summary>
    public static async Task<Tools.BridgeContractStatus> LiveAsync(BridgeClient bridge, JToken? hello, CancellationToken ct)
    {
        var snap = BridgeSchema.Snapshot;
        var live = hello?["contract"];
        var hash = live?["hash"]?.ToString();
        if (string.IsNullOrEmpty(hash))
            return Lined(new Tools.BridgeContractStatus { SnapshotHash = snap?.Hash, Error = live?["error"]?.ToString() ?? "hello has no contract (a bridge from before contract.schema): update / restart the bridge" });
        if (snap == null) return Lined(new Tools.BridgeContractStatus { LiveHash = hash, Error = $"no snapshot in this MCP build ({BridgeSchema.SnapshotError})" });
        if (hash == snap.Hash) return Lined(new Tools.BridgeContractStatus { State = "in_step", LiveHash = hash, SnapshotHash = snap.Hash, Mirrors = Mirrors.Length });
        if (LiveByHash.TryGetValue(hash, out var cached)) return cached;
        try
        {
            var schemaToken = await bridge.SendRequestAsync("contract.schema", new JObject(), ct);
            if (schemaToken["error"] != null)
                return Lined(new Tools.BridgeContractStatus { LiveHash = hash, SnapshotHash = snap.Hash, Error = $"contract.schema answered {schemaToken["error"]}: {schemaToken["message"]}" });
            var schema = BridgeSchema.Parse(schemaToken);
            var drift = Check(schema);
            var status = new Tools.BridgeContractStatus
            {
                State = "checked", LiveHash = hash, SnapshotHash = snap.Hash, Mirrors = Mirrors.Length,
                Drift = drift.Select(d => "contract drift: " + d.Text).ToList(),
            };
            return LiveByHash[hash] = Lined(status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Lined(new Tools.BridgeContractStatus { LiveHash = hash, SnapshotHash = snap.Hash, Error = $"contract.schema failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    /// <summary>The one text line a report shows for the status.</summary>
    private static Tools.BridgeContractStatus Lined(Tools.BridgeContractStatus s)
    {
        s.Line = s.State switch
        {
            "in_step" => $"Bridge contract: in step with the MCP's snapshot ({s.LiveHash}, {s.Mirrors} mirrors checked by its tests).",
            "checked" when s.Drift is { Count: > 0 } d =>
                $"Bridge contract: {d.Count} drift(s) against the MCP mirrors (bridge {s.LiveHash}, snapshot {s.SnapshotHash}): {string.Join("; ", d.Take(3))}{(d.Count > 3 ? "; ..." : "")}",
            "checked" => $"Bridge contract: hash moved ({s.SnapshotHash} -> {s.LiveHash}) but every MCP mirror still matches; refresh the snapshot (Tests --refresh-contract).",
            _ => $"Bridge contract: unknown ({s.Error}).",
        };
        return s;
    }

    // ── Snapshot refresh and mirror generator (Tests: --refresh-contract, --emit-mirror) ────────────────────────────

    /// <summary>Fetch contract.schema from a running bridge and write it, indented, as the snapshot file.</summary>
    public static async Task<string> RefreshSnapshotAsync(string game, string path, CancellationToken ct)
    {
        var dir = BridgeRegistry.Layout().FirstOrDefault(l => l.Game == game).Dir
                  ?? throw new InvalidOperationException($"no bridge folder for game '{game}' (BridgeRegistry.Layout)");
        using var client = new BridgeClient(game, dir, 50900);
        await client.EnsureConnectedAsync(ct);
        var schema = await client.SendRequestAsync("contract.schema", new JObject(), ct);
        if (schema["error"] != null) throw new InvalidOperationException($"contract.schema answered {schema["error"]}: {schema["message"]}");
        if (schema["types"] is not JArray { Count: > 0 }) throw new InvalidOperationException("contract.schema returned no types");
        ((JObject)schema).Remove("ms");   // timing of that load: noise in a committed file
        File.WriteAllText(path, schema.ToString(Newtonsoft.Json.Formatting.Indented) + "\n");
        return $"{schema["typeCount"]} types, hash {schema["hash"]}";
    }

    /// <summary>A C# mirror (System.Text.Json, Extra kept) of one bridge type and the types it reaches, for a new Tools/*Dtos.cs
    /// family: a starting point, to be given summaries and narrowed by hand.</summary>
    public static string EmitMirror(BridgeSchema schema, string bridgeType, string prefix = "")
    {
        var sb = new System.Text.StringBuilder();
        var todo = new Queue<string>([bridgeType]);
        var done = new HashSet<string>();
        string Name(string n) => prefix + (n.EndsWith("Dto") ? n[..^3] : n).Replace(".", "");
        string Cs(string shape) => shape switch
        {
            "string" or "datetime" => "string?",
            "integer" => "long?",
            "number" => "double?",
            "bool" => "bool?",
            _ when shape.StartsWith("enum:") => "int?",
            _ when shape.StartsWith("list<") => $"List<{Cs(Inner(shape)).TrimEnd('?')}>?",
            _ when shape.StartsWith("map<") => $"Dictionary<string, {Cs(Inner(shape)).TrimEnd('?')}>?",
            _ when shape.StartsWith("object:") => Enqueue(shape["object:".Length..]),
            _ => "JsonElement?",
        };
        string Enqueue(string n) { todo.Enqueue(n); return Name(n) + "?"; }
        while (todo.Count > 0)
        {
            var n = todo.Dequeue();
            if (!done.Add(n) || !schema.Types.TryGetValue(n, out var t) || t.Kind != "object") continue;
            sb.AppendLine($"/// <summary>Mirror of bridge {n}.</summary>");
            sb.AppendLine($"public sealed class {Name(n)}").AppendLine("{");
            foreach (var f in t.Fields)
            {
                var prop = char.ToUpperInvariant(f.Json[0]) + f.Json[1..];
                var attr = JsonNamingPolicy.CamelCase.ConvertName(prop) == f.Json ? "" : $"[JsonPropertyName(\"{f.Json}\")] ";
                sb.AppendLine($"    {attr}public {Cs(f.Type)} {prop} {{ get; set; }}");
            }
            sb.AppendLine("    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }").AppendLine("}").AppendLine();
        }
        return sb.ToString();
    }
}
