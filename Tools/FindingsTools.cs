using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Findings with a status per game (Knowledge/findings.json, embedded): what was verified where, how, and a check that
/// re-runs it. A finding verified on PoE1 is a hypothesis on PoE2 until verify_finding says otherwise - switching games
/// surfaces the list (bridge_status findingsToCheck).
/// </summary>
[McpServerToolType]
public static class FindingsTools
{
    private static readonly Lazy<JObject> Registry = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("knowledge/findings.json");
        return s == null ? new JObject { ["findings"] = new JArray() } : JObject.Parse(new StreamReader(s).ReadToEnd());
    });

    private static IEnumerable<JObject> All => (Registry.Value["findings"] as JArray ?? []).OfType<JObject>();

    private static string Status(JObject f, string game) => f["games"]?[game]?["status"]?.ToString() ?? "unverified";

    /// <summary>Ids verified on some other game but not on this one.</summary>
    internal static List<string> ToCheck(string game) =>
        All.Where(f => Status(f, game) == "unverified"
                       && (f["games"] as JObject)?.Properties().Any(p => p.Name != game && p.Value["status"]?.ToString() == "verified") == true)
           .Select(f => f["id"]!.ToString()).ToList();

    [McpServerTool(Name = "findings", Title = "Verified findings per game", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Facts about game data and memory with their status per game (verified / differs / unverified / n/a), " +
                 "where they hold (offsets, bits) and the evidence. Use it before relying on a known offset or bit on a game, " +
                 "and after switching games: 'toCheck' lists findings verified on the other game but not this one - run " +
                 "verify_finding for each. Source: Knowledge/findings.json in ExileApiMcp (update it with the results).")]
    public static CallToolResult Findings(
        [Description("Game to focus on: 'poe1' or 'poe2' (optional)")] string? game = null,
        [Description("Only findings with this status for the game: verified | differs | unverified | n/a")] string? status = null,
        [Description("Text filter on id/title/subject")] string? filter = null)
    {
        var rows = All.Where(f => filter == null || $"{f["id"]} {f["title"]} {f["subject"]}".Contains(filter, StringComparison.OrdinalIgnoreCase))
                      .Where(f => status == null || game == null || Status(f, game) == status).ToList();
        var sb = new StringBuilder();
        foreach (var f in rows)
        {
            sb.Append(f["id"]).Append(": ").AppendLine(f["title"]?.ToString());
            foreach (var g in (f["games"] as JObject)?.Properties() ?? [])
            {
                if (game != null && g.Name != game) continue;
                sb.Append("  ").Append(g.Name).Append(": ").Append(g.Value["status"]);
                if (g.Value["where"] != null) sb.Append(" @ ").Append(g.Value["where"]);
                if (g.Value["date"] != null) sb.Append(" (").Append(g.Value["date"]).Append(')');
                if (g.Value["note"] != null) sb.Append(" - ").Append(g.Value["note"]);
                sb.AppendLine();
            }
            sb.Append("  check: ").AppendLine(f["check"]?["kind"]?.ToString());
        }
        var result = new JObject { ["about"] = Registry.Value["about"], ["findings"] = new JArray(rows) };
        if (game != null && ToCheck(game) is { Count: > 0 } pending)
        {
            result["toCheck"] = new JArray(pending);
            sb.AppendLine().Append($"To check on {game} (verified elsewhere only): {string.Join(", ", pending)}. Run verify_finding id=<id> game={game}.");
        }
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.Length > 0 ? sb.ToString().TrimEnd() : "No findings match." }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(result.ToString(Formatting.None)),
        };
    }

    [McpServerTool(Name = "verify_finding", Title = "Re-run a finding's check on a game", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Run a finding's check against the live game and report pass / moved / fail with the evidence, plus the " +
                 "status entry to record in Knowledge/findings.json. Automatic for correlate/stored/eval checks; for manual " +
                 "ones it returns the experiment to run (with the user).")]
    public static async Task<CallToolResult> VerifyFinding(BridgeRegistry bridges,
        [Description("Finding id (see findings)")] string id,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var f = All.FirstOrDefault(x => x["id"]?.ToString() == id) ?? throw new McpException($"No finding '{id}'. Call findings to list them.");
        var check = f["check"] as JObject ?? throw new McpException($"Finding '{id}' has no check.");
        // Resolve which game we are on (the bridge that answers).
        var (bridge, _) = await bridges.QueryAsync(game, "hello", ct);
        var g = bridge.Game;
        var recorded = f["games"]?[g] as JObject;
        var o = new JObject { ["id"] = id, ["game"] = g, ["kind"] = check["kind"], ["recorded"] = recorded };
        string verdict, where = "", evidence;

        switch (check["kind"]?.ToString())
        {
            case "correlate":
            case "stored":
            {
                var label = check["label"]!.ToString();
                var res = await ProbeTools.MemoryCorrelate(bridges, check["path"]!.ToString(), [label], 0, 0, 500, g, ct);
                var data = JObject.Parse(System.Text.Json.JsonSerializer.Serialize(res.StructuredContent));
                var finding = data["findings"]?.FirstOrDefault() as JObject;
                if (check["kind"]!.ToString() == "stored")
                {
                    var stored = finding?["storedAt"] as JArray ?? [];
                    where = string.Join(", ", stored.Select(s => $"+{s["offset"]} ({s["type"]})"));
                    verdict = stored.Count > 0 ? "pass" : "fail";
                    evidence = $"memory_correlate storedAt over {data["items"]} items: {(stored.Count > 0 ? where : "value not found in the struct range")}";
                }
                else
                {
                    var expect = check["expect"]!.ToString();
                    var hit = (finding?["bitsExplained"] as JArray ?? []).OfType<JObject>().FirstOrDefault(b => b["equals"]?.ToString() == expect);
                    var near = (finding?["nearMisses"] as JArray ?? []).OfType<JObject>().FirstOrDefault(b => b["equals"]?.ToString() == expect);
                    if (hit != null)
                    {
                        where = $"+{hit["byte"]} bit {hit["bit"]}";
                        verdict = "pass";
                        evidence = $"memory_correlate over {data["items"]} items: {hit["evidence"]}, 0 counterexamples";
                    }
                    else if (near != null)
                    {
                        where = $"+{near["byte"]} bit {near["bit"]}";
                        verdict = "differs";
                        evidence = $"near miss with {near["counterexamples"]} counterexample(s): {near["counterexampleItems"]?.ToString(Formatting.None)}";
                    }
                    else
                    {
                        verdict = "fail";
                        evidence = $"no bit equals '{expect}' with enough evidence over {data["items"]} items" +
                                   (finding?["tooLittleEvidence"] != null ? $" ({finding["tooLittleEvidence"]}: the population may lack variety)" : "");
                    }
                }
                o["correlate"] = finding;
                break;
            }
            case "code":
            {
                using var offsetDoc = JsonDocument.Parse(check["offset"]!.ToString(Formatting.None));
                var res = await CodeAccessTools.FindFieldAccess(bridges, offsetDoc.RootElement.Clone(), check["path"]?.ToString(),
                    check["bit"]?.Value<int>(), knownOffsets: null, minKnown: 2, decompile: 6, game: g, ct: ct);
                var data = JObject.Parse(System.Text.Json.JsonSerializer.Serialize(res.StructuredContent));
                var expectAll = (check["expectAll"] as JArray ?? []).Select(x => x.ToString()).ToList();
                // Compare without whitespace: decompiler spacing varies between versions.
                static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
                var hit = (data["decompiled"] as JArray ?? []).OfType<JObject>()
                    .FirstOrDefault(d => expectAll.All(e => Squash(d["excerpt"]?.ToString() ?? "").Contains(Squash(e), StringComparison.OrdinalIgnoreCase)));
                verdict = hit != null ? "pass" : "fail";
                where = hit != null ? $"{hit["function"]} in {data["program"]}" : "";
                evidence = hit != null
                    ? $"find_field_access: {hit["function"]} contains {string.Join(" and ", expectAll.Select(e => $"'{e}'"))}"
                    : $"no decompiled excerpt of the struct's code contains {string.Join(" and ", expectAll.Select(e => $"'{e}'"))} " +
                      $"({(data["functions"] as JArray)?.Count ?? 0} functions found): offsets or code changed, or the struct differs on this game";
                o["functions"] = data["functions"];
                break;
            }
            case "eval":
            {
                var expr = check["expressionByGame"]?[g]?.ToString() ?? check["expression"]?.ToString()
                           ?? throw new McpException($"Finding '{id}' has no expression for {g}.");
                var (_, r) = await bridges.QueryAsync(g, $"eval:{expr}", ct);
                var type = r["type"]?.ToString() ?? "";
                var expect = check["expect"]?.ToString() ?? "";
                var needle = expect.StartsWith("type contains ", StringComparison.Ordinal) ? expect["type contains ".Length..] : null;
                verdict = r["error"] != null ? "fail" : needle == null || type.Contains(needle, StringComparison.Ordinal) ? "pass" : "differs";
                where = $"{expr}: {type}";
                evidence = r["error"] != null ? $"eval failed: {r["error"]}" : $"eval_path {expr} -> {type}";
                break;
            }
            default:
                return ToolResults.Json(new JObject
                {
                    ["id"] = id, ["game"] = g, ["kind"] = "manual", ["recorded"] = recorded,
                    ["howToVerify"] = check["how"],
                    ["next"] = "Run this experiment (prompt probe_memory describes the method), then record the result in Knowledge/findings.json.",
                });
        }

        var recordedWhere = recorded?["where"]?.ToString();
        // "moved" only for offset-based checks: their 'where' is an offset, comparable across runs.
        if (verdict == "pass" && check["kind"]?.ToString() is "correlate" or "stored" && recordedWhere != null && recorded?["status"]?.ToString() == "verified"
            && !string.Equals(Normalize(recordedWhere), Normalize(where), StringComparison.OrdinalIgnoreCase))
            verdict = "moved";
        o["verdict"] = verdict;
        o["where"] = where;
        o["evidence"] = evidence;
        o["record"] = new JObject
        {
            ["status"] = verdict is "pass" or "moved" ? "verified" : verdict == "differs" ? "differs" : "unverified",
            ["date"] = DateTime.Now.ToString("yyyy-MM-dd"), ["where"] = where, ["evidence"] = evidence,
        };
        o["next"] = verdict switch
        {
            "pass" => $"Holds on {g}. Set games.{g} in Knowledge/findings.json to 'record' (if not already).",
            "moved" => $"Holds on {g} but at a different place than recorded ({recordedWhere}): the struct moved. Update games.{g}.where and check the HUD's offsets.",
            "differs" => $"Mostly holds on {g} with counterexamples: inspect them (probe_memory) before recording 'differs'.",
            _ => $"Not found on {g}: the population may lack variety (e.g. no tab with an affinity), the struct may differ, or the fact is game-specific. Investigate with probe_memory.",
        };
        return ToolResults.Json(o);
    }

    private static string Normalize(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
