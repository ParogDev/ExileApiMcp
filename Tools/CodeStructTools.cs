using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExileApiMcp.Bridge;
using ExileApiMcp.Ghidra;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// "What does the game's code say this struct looks like?" - derived from one function (best: the struct's network
/// (de)serializer or constructor, which touch every member in order) and diffed against the HUD's struct. Static, from
/// the Ghidra snapshot; never the running game. Heuristic parsing of decompiled C: results are evidence to confirm.
/// </summary>
[McpServerToolType]
public static partial class CodeStructTools
{
    [McpServerTool(Name = "code_struct_layout", Title = "Struct layout from the game's code vs the HUD", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(CodeStructLayoutResult))]
    [Description("Derive a struct's layout from one game function and compare it with the HUD's struct: every offset the " +
                 "function reads or writes through the struct pointer, with its size (from casts and (de)serializer size " +
                 "arguments) and the flag test that gates it, matched to the HUD's fields - mapped, mapped with a different " +
                 "size, or UNMAPPED (a field the HUD lacks). Best input: the struct's network serializer/deserializer or " +
                 "constructor (find_field_access ranks functions and names roles). Static, from Ghidra; heuristic parsing.")]
    public static async Task<CallToolResult> CodeStructLayout(BridgeRegistry bridges,
        [Description("Function: name like FUN_141d4d1f0 or an address 0x141d4d1f0")] string function,
        [Description("Walker path to one object of the struct, for the HUD comparison (e.g. GameController.IngameState.ServerData.PlayerStashTabs[0])")] string? path = null,
        [Description("The pointer variable that holds the struct in the decompiled code (default: the parameter with most accesses)")] string? @base = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var addr = Regex.Match(function, "(?:FUN_|0x)?([0-9a-fA-F]{6,16})$") is { Success: true } mm ? "0x" + mm.Groups[1].Value : function;
        JObject? layout = null;
        if (path != null)
        {
            var (bridge, l) = await bridges.CallAsync(game, "memory.layout", new JObject { ["path"] = path }, ct);
            game = bridge.Game;
            if (l is JObject lo && lo["error"] == null) layout = lo;
        }
        if (game == null) game = (await bridges.QueryAsync(null, "hello", ct)).Bridge.Game;
        var ghidra = await GhidraClient.ForGameAsync(game, ct);
        var code = (await ghidra.DecompileAsync(addr, ct)).Replace("\r", "");
        var lines = code.Split('\n');

        // Element size of pointer-typed parameters: "undefined8 *param_2" -> 8 (param_2 + 7 means +0x38).
        var elem = new Dictionary<string, int>();
        foreach (Match p in ParamDecl().Matches(lines.FirstOrDefault(l => l.Contains('(')) ?? ""))
            elem[p.Groups["name"].Value] = SizeOf(p.Groups["type"].Value) is var s && s > 0 ? s : 1;

        var accesses = new List<(string var, int off, int size, string gate, int line, string text)>();
        var gates = new Stack<(int depth, string cond)>();
        int depth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            var gate = gates.Count > 0 ? gates.Peek().cond : "";
            foreach (var (v, off, size) in Accesses(line, elem))
                accesses.Add((v, off, size, gate, i, trimmed));
            // Track if-blocks: "if (cond) {" opens a gate until its closing brace.
            if (IfOpen().Match(trimmed) is { Success: true } im)
            {
                var condText = im.Groups["cond"].Value.Trim();
                var cond = condText.Length == 0 ? "" : CondFlag().Match(condText) is { Success: true } cf
                    ? $"{(cf.Groups["neg"].Value == "==" ? "!" : "")}(+0x{cf.Groups["off"].Value} & {cf.Groups["mask"].Value})"
                    : condText.Length > 60 ? condText[..57] + "..." : condText;
                gates.Push((depth, cond));
            }
            depth += trimmed.Count(c => c == '{') - trimmed.Count(c => c == '}');
            while (gates.Count > 0 && depth <= gates.Peek().depth) gates.Pop();
        }
        if (accesses.Count == 0) throw new McpException($"No struct accesses recognised in {function}. Pass the struct pointer variable as base, or pick a function that reads/writes the struct (find_field_access).");
        var baseVar = @base ?? accesses.GroupBy(a => a.var).OrderByDescending(g => g.Select(a => a.off).Distinct().Count()).First().Key;

        var fields = (layout?["fields"] as JArray ?? []).OfType<JObject>()
            .Select(f => (off: f["off"]!.Value<int>(), size: f["size"]!.Value<int>(), name: f["name"]!.ToString())).ToList();
        var rows = new JArray();
        foreach (var g in accesses.Where(a => a.var == baseVar).GroupBy(a => a.off).OrderBy(g => g.Key))
        {
            var size = g.Max(a => a.size);
            var gatesFor = g.Select(a => a.gate.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            var hud = fields.Where(f => g.Key < f.off + f.size && f.off < g.Key + Math.Max(size, 1)).ToList();
            string status = hud.Count == 0 ? "UNMAPPED"
                : hud.Any(f => f.off == g.Key && f.size == size) || size == 0 ? "mapped"
                : hud.Any(f => f.off == g.Key) ? $"mapped, code uses {size} B vs HUD {hud.First(f => f.off == g.Key).size} B"
                : "overlaps a HUD field at a different offset";
            rows.Add(new JObject
            {
                ["off"] = g.Key, ["hex"] = $"0x{g.Key:X}", ["size"] = size == 0 ? null : size,
                ["hud"] = hud.Count > 0 ? string.Join(", ", hud.Select(f => $"{f.name} (+{f.off}, {f.size} B)")) : null,
                ["status"] = layout == null ? null : status, ["gate"] = gatesFor.Count > 0 ? string.Join(" or ", gatesFor) : null,
                ["lines"] = new JArray(g.Select(a => a.text).Distinct().Take(3)),
            });
        }
        foreach (var r in rows.OfType<JObject>()) foreach (var p in r.Properties().Where(p => p.Value.Type == JTokenType.Null).ToList()) p.Remove();
        var hudOnly = fields.Where(f => !accesses.Any(a => a.var == baseVar && a.off < f.off + f.size && f.off < a.off + Math.Max(a.size, 1)))
            .Select(f => $"{f.name} (+{f.off})").ToList();
        var result = new JObject
        {
            ["function"] = function, ["program"] = ghidra.Program, ["base"] = baseVar, ["struct"] = layout?["struct"],
            ["offsets"] = rows, ["hudFieldsNotTouched"] = new JArray(hudOnly),
            ["note"] = "Heuristic: offsets from the decompiled C (casts, pointer strides, (de)serializer size arguments). " +
                       "A gate is the flag test of the enclosing if-block. Confirm discoveries with memory_correlate or a guided experiment.",
        };
        return Dto.Result(Dto.From<CodeStructLayoutResult>(result), Outline(result, layout != null));
    }

    // ── Parsing decompiled C ─────────────────────────────────────────

    [GeneratedRegex(@"(?<type>[\w ]+?)\s*\*\s*(?<name>param_\d+)")]
    private static partial Regex ParamDecl();

    // *(TYPE *)((longlong)VAR + 0xNN)  /  *(TYPE *)(VAR + 0xNN)
    [GeneratedRegex(@"\*\((?<type>[\w ]+?)\s*\*\)\s*\(\s*(?:\(\w+\)\s*)?(?<var>\w+)\s*\+\s*0x(?<off>[0-9a-fA-F]+)\s*\)")]
    private static partial Regex CastAccess();

    // FUNC(x, (longlong)VAR + 0xNN, SIZE)  - (de)serializer copy of SIZE bytes
    [GeneratedRegex(@"[(,]\s*(?:\(\w+\)\s*)?(?<var>param_\d+)\s*\+\s*0x(?<off>[0-9a-fA-F]+)\s*,\s*(?<size>\d+)\s*\)")]
    private static partial Regex SizedArg();

    // *(TYPE *)(VAR + N) with a decimal N on a typed pointer: N elements, i.e. N * element size bytes
    [GeneratedRegex(@"\*\((?<type>[\w ]+?)\s*\*\)\s*\(\s*(?<var>param_\d+)\s*\+\s*(?<n>\d+)\s*\)")]
    private static partial Regex CastStride();

    // VAR[N] / *(VAR + N) on a typed pointer parameter
    [GeneratedRegex(@"(?<var>param_\d+)\s*\[\s*(?<n>\d+)\s*\]|\*\s*\(\s*(?<var2>param_\d+)\s*\+\s*(?<n2>\d+)\s*\)")]
    private static partial Regex IndexAccess();

    // *VAR  (offset 0)
    [GeneratedRegex(@"(?<![\w)])\*(?<var>param_\d+)\b")]
    private static partial Regex DerefZero();

    [GeneratedRegex(@"^(?:\}\s*)?(?:else\s+)?if\s*\((?<cond>.*)\)\s*\{\s*$")]
    private static partial Regex IfOpen();

    [GeneratedRegex(@"\+\s*0x(?<off>[0-9a-fA-F]+)\)\s*&\s*(?<mask>0x[0-9a-fA-F]+|\d+)\)\s*(?<neg>[!=]=)\s*0")]
    private static partial Regex CondFlag();

    private static IEnumerable<(string var, int off, int size)> Accesses(string line, Dictionary<string, int> elem)
    {
        var seen = new HashSet<(string, int)>();
        foreach (Match m in CastAccess().Matches(line))
        {
            var off = Convert.ToInt32(m.Groups["off"].Value, 16);
            if (seen.Add((m.Groups["var"].Value, off))) yield return (m.Groups["var"].Value, off, SizeOf(m.Groups["type"].Value));
        }
        foreach (Match m in SizedArg().Matches(line))
        {
            var off = Convert.ToInt32(m.Groups["off"].Value, 16);
            // A sized copy states the field width outright: it wins over a narrower cast on the same line.
            seen.Add((m.Groups["var"].Value, off));
            yield return (m.Groups["var"].Value, off, int.Parse(m.Groups["size"].Value));
        }
        foreach (Match m in CastStride().Matches(line))
        {
            var v = m.Groups["var"].Value;
            var off = int.Parse(m.Groups["n"].Value) * elem.GetValueOrDefault(v, 8);
            if (seen.Add((v, off))) yield return (v, off, SizeOf(m.Groups["type"].Value));
        }
        foreach (Match m in IndexAccess().Matches(line))
        {
            var v = m.Groups["var"].Success && m.Groups["var"].Value.Length > 0 ? m.Groups["var"].Value : m.Groups["var2"].Value;
            var n = int.Parse(m.Groups["n"].Success && m.Groups["n"].Value.Length > 0 ? m.Groups["n"].Value : m.Groups["n2"].Value);
            var es = elem.GetValueOrDefault(v, 8);
            if (seen.Add((v, n * es))) yield return (v, n * es, es);
        }
        foreach (Match m in DerefZero().Matches(line))
            if (seen.Add((m.Groups["var"].Value, 0))) yield return (m.Groups["var"].Value, 0, elem.GetValueOrDefault(m.Groups["var"].Value, 8));
    }

    private static int SizeOf(string type)
    {
        var t = type.Trim().ToLowerInvariant().Replace("unsigned ", "u");
        return t switch
        {
            "byte" or "char" or "uchar" or "undefined1" or "bool" or "undefined" => 1,
            "short" or "ushort" or "u_short" or "undefined2" or "wchar_t" or "word" => 2,
            "int" or "uint" or "undefined4" or "float" or "dword" or "long" or "ulong" => 4,
            "longlong" or "ulonglong" or "undefined8" or "double" or "qword" or "pointer" => 8,
            _ when t.EndsWith('*') => 8,
            _ => 0,
        };
    }

    private static string Outline(JObject r, bool compared)
    {
        var sb = new StringBuilder($"{r["function"]} in {r["program"]}: struct pointer {r["base"]}" + (r["struct"] != null ? $", compared with {r["struct"]}" : "") + "\n");
        foreach (var o in ((JArray)r["offsets"]!).OfType<JObject>())
        {
            sb.Append($"  +{o["off"],-4} {o["hex"],-6} {(o["size"] != null ? $"{o["size"]} B" : "?"),-5}");
            if (compared) sb.Append($" {o["status"]}");
            if (o["hud"] != null) sb.Append($"  [{o["hud"]}]");
            if (o["gate"]?.ToString() is { } gate && !string.IsNullOrWhiteSpace(gate)) sb.Append($"  if {gate}");
            sb.AppendLine();
        }
        var notTouched = (JArray)r["hudFieldsNotTouched"]!;
        if (compared && notTouched.Count > 0) sb.AppendLine($"HUD fields this function doesn't touch: {string.Join(", ", notTouched.Take(20))}");
        sb.Append(r["note"]);
        return sb.ToString();
    }
}
