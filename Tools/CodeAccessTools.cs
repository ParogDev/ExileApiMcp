using System.ComponentModel;
using System.Globalization;
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
/// "What code accesses this field?" answered statically, from the Ghidra copy of the game exe - never by attaching to
/// the running game. A struct's code is found by fingerprint: functions that access several of its known field offsets
/// through the same base register. Within them, the instructions touching the target offset (read / write / bit test,
/// with the bits a mask touches) are listed and the functions decompiled around those lines.
/// </summary>
[McpServerToolType]
public static partial class CodeAccessTools
{
    [McpServerTool(Name = "find_field_access", Title = "Find code that accesses a struct field (static, Ghidra)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Find the game code that reads, writes or bit-tests a struct field - to explain an unmapped or unknown field " +
                 "(e.g. stash tab Flags +61 bit 6) from the code that uses it. Static analysis of the Ghidra snapshot matching " +
                 "the installed exe; never touches the running game. The struct's functions are found by fingerprint: code " +
                 "that accesses >= minKnown of the struct's known field offsets (from the HUD's own struct, via path) through " +
                 "the same base register. Returns those functions, the target instructions (kind, width, bits a mask " +
                 "touches) and decompiled excerpts. Needs tools/ghidra-headless.ps1 running. The first query per offset scans " +
                 "the whole program (~30 s per offset, cached afterwards).")]
    public static async Task<CallToolResult> FindFieldAccess(BridgeRegistry bridges,
        [Description("Field offset in the struct, bytes (e.g. 61 or 0x3D)")] JsonElement offset,
        [Description("Walker path to one object of the struct (its HUD struct supplies the known offsets), e.g. GameController.IngameState.ServerData.PlayerStashTabs[0]")] string? path = null,
        [Description("Bit within the byte at offset (0-7) to focus on, e.g. 6 for Flags bit 6")] int? bit = null,
        [Description("Known field offsets of the same struct (instead of path), e.g. [52,56,63]")] int[]? knownOffsets = null,
        [Description("Known offsets that must be accessed through the same base register (default 2)")] int minKnown = 2,
        [Description("Functions to decompile (0-6, default 3); bit-testing functions first (they explain flags)")] int decompile = 3,
        [Description("Decompile exactly these functions (names from a previous result, e.g. [\"FUN_141d4d020\"]) instead of picking")] string[]? decompileFunctions = null,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var target = ParseOffset(offset) ?? throw new McpException("offset: a number like 61 or \"0x3D\".");
        if (target <= 0) throw new McpException("Offset 0 can't be searched by displacement ([reg] has none); pick a field at +1 or above.");

        // Game and known offsets: from the HUD's struct for the object when a path is given.
        string? structName = null;
        var known = new SortedDictionary<int, string>();
        if (path != null)
        {
            var (bridge, layout) = await bridges.CallAsync(game, "memory.layout", new JObject { ["path"] = path }, ct);
            game = bridge.Game;
            if (layout is not JObject l || l["error"] != null) throw new McpException($"memory.layout failed: {layout?["message"] ?? layout?["error"]}");
            structName = l["struct"]?.ToString();
            foreach (var f in (l["fields"] as JArray ?? []).OfType<JObject>())
            {
                int fo = f["off"]!.Value<int>(), fs = f["size"]!.Value<int>();
                if (fo >= 8 && (target < fo || target >= fo + fs)) known.TryAdd(fo, f["name"]!.ToString());
            }
        }
        foreach (var k in knownOffsets ?? []) if (k >= 8 && k != target) known.TryAdd(k, $"+{k}");
        if (game == null)
        {
            var (bridge, _) = await bridges.QueryAsync(null, "hello", ct);
            game = bridge.Game;
        }
        if (known.Count < minKnown) throw new McpException($"Need at least {minKnown} known offsets of the struct: pass path (an object of this struct) or knownOffsets.");
        var ghidra = await GhidraClient.ForGameAsync(game, ct);
        var targetMatches = await ghidra.SearchOperandAsync($"+ 0x{target:x}]", ct)
            ?? throw new McpException($"+0x{target:X} is used by over 50000 instructions program-wide: too common to search by displacement. Try a neighbouring field.");
        var targetHits = Parse(targetMatches, target);

        // Anchors: the known fields nearest the target (code that touches the target usually touches its neighbours),
        // skipping displacements too common to identify anything (e.g. +0x30, +0x38: over 50000 uses each).
        var anchors = new Dictionary<int, string>();
        var byKey = new Dictionary<(string fn, string reg), HashSet<int>>();
        var anchorInfo = new JArray();
        foreach (var (off, name) in known.OrderBy(k => Math.Abs(k.Key - target)).Take(12))
        {
            if (anchors.Count >= 6) break;
            var matches = await ghidra.SearchOperandAsync($"+ 0x{off:x}]", ct);
            if (matches == null) { anchorInfo.Add(new JObject { ["offset"] = off, ["field"] = name, ["skipped"] = "too common (50000+ uses)" }); continue; }
            var hits = Parse(matches, off);
            anchors[off] = name;
            anchorInfo.Add(new JObject { ["offset"] = off, ["field"] = name, ["accesses"] = hits.Count });
            foreach (var h in hits)
                (byKey.TryGetValue((h.Function, h.Base), out var set) ? set : byKey[(h.Function, h.Base)] = []).Add(off);
        }
        if (anchors.Count < minKnown) throw new McpException($"Only {anchors.Count} of the struct's known fields are distinctive enough to fingerprint its code; pass more knownOffsets or lower minKnown.");

        var scored = targetHits
            .Select(h => (h, known: byKey.TryGetValue((h.Function, h.Base), out var s) ? s : []))
            .Select(x => (x.h, x.known, bitMatch: bit != null && x.h.Bits?.Contains(bit.Value) == true))
            .OrderByDescending(x => x.known.Count).ThenByDescending(x => x.bitMatch).ThenBy(x => x.h.Address)
            .ToList();
        var anchored = scored.Where(x => x.known.Count >= minKnown).ToList();

        var accesses = new JArray(anchored.Take(60).Select(x => new JObject
        {
            ["function"] = x.h.Function, ["address"] = x.h.Address, ["instruction"] = $"{x.h.Mnemonic} {x.h.Operands}",
            ["kind"] = x.h.Kind, ["width"] = x.h.Width, ["base"] = x.h.Base,
            ["bits"] = x.h.Bits == null ? null : new JArray(x.h.Bits), ["matchesBit"] = bit == null ? null : x.bitMatch,
            ["knownFieldsAlsoAccessed"] = new JArray(x.known.OrderBy(o => o).Select(o => $"+{o} {anchors.GetValueOrDefault(o)}")),
            ["confidence"] = x.known.Count >= 4 ? "high" : x.known.Count >= 3 ? "medium" : "low",
        }));
        foreach (var a in accesses.OfType<JObject>()) foreach (var p in a.Properties().Where(p => p.Value.Type == JTokenType.Null).ToList()) p.Remove();

        // Decompile the best functions (bit-matching first) and keep the lines around the target offset.
        var decompiled = new JArray();
        var hexToken = new Regex($@"0x{target:x}\b", RegexOptions.IgnoreCase);
        var picks = decompileFunctions is { Length: > 0 }
            ? anchored.Where(x => decompileFunctions.Contains(x.h.Function, StringComparer.OrdinalIgnoreCase)).GroupBy(x => x.h.Function).Select(g => g.First())
            // Bit-matching first, then any bit-testing function (flag handling: serializers, UI), then the best-anchored.
            : anchored.OrderByDescending(x => x.bitMatch).ThenByDescending(x => x.h.Kind == "bit-test").ThenByDescending(x => x.known.Count)
                      .GroupBy(x => x.h.Function).Select(g => g.First()).Take(Math.Clamp(decompile, 0, 6));
        foreach (var x in picks)
        {
            string code;
            try { code = await ghidra.DecompileAsync(x.h.Address, ct); }
            catch (McpException ex) { decompiled.Add(new JObject { ["function"] = x.h.Function, ["error"] = ex.Message }); continue; }
            var lines = code.Replace("\r", "").Split('\n');
            var keep = new SortedSet<int>();
            for (int i = 0; i < lines.Length; i++)
                if (hexToken.IsMatch(lines[i])) for (int j = Math.Max(0, i - 2); j <= Math.Min(lines.Length - 1, i + 2); j++) keep.Add(j);
            if (keep.Count == 0) for (int i = 0; i < Math.Min(lines.Length, 20); i++) keep.Add(i);
            var excerpt = new StringBuilder();
            int prev = -2;
            foreach (var i in keep.Take(40)) { if (i != prev + 1) excerpt.AppendLine("  ..."); excerpt.AppendLine(lines[i]); prev = i; }
            decompiled.Add(new JObject
            {
                ["function"] = x.h.Function, ["signature"] = lines.FirstOrDefault(s => s.Contains('(')) ?? "", ["excerpt"] = excerpt.ToString().TrimEnd(),
                ["lineCount"] = lines.Length,
            });
        }

        var result = new JObject
        {
            ["game"] = game, ["program"] = ghidra.Program, ["struct"] = structName, ["path"] = path,
            ["target"] = bit == null
                ? new JObject { ["offset"] = target, ["hex"] = $"0x{target:X}" }
                : new JObject { ["offset"] = target, ["hex"] = $"0x{target:X}", ["bit"] = bit },
            ["anchors"] = anchorInfo, ["minKnown"] = minKnown,
            ["programWideAccesses"] = targetHits.Count,
            ["functions"] = new JArray(anchored.GroupBy(x => x.h.Function).Select(g => new JObject
            {
                ["function"] = g.Key, ["accesses"] = g.Count(), ["kinds"] = string.Join(",", g.Select(x => x.h.Kind).Distinct()),
                ["knownFields"] = g.Max(x => x.known.Count), ["bitMatch"] = g.Any(x => x.bitMatch),
            })),
            ["accesses"] = accesses,
            ["decompiled"] = decompiled,
            ["unanchored"] = $"{scored.Count - anchored.Count} other instructions use +0x{target:X} on a base that touches fewer than {minKnown} known fields (other structs, or code we can't tie to this one)",
        };
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Outline(result) }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(result.ToString(Formatting.None)),
        };
    }

    // ── Instruction parsing ──────────────────────────────────────────

    private sealed record Access(string Function, string Address, string Mnemonic, string Operands, string Base, int Width, string Kind, int[]? Bits);

    [GeneratedRegex(@"(?:(?<w>byte|word|dword|qword|xmmword|ymmword|tword) ptr )?\[(?<base>[^\]]*?) \+ 0x(?<d>[0-9a-f]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex MemOperand();

    private static readonly HashSet<string> ReadOnlyMnemonics = new(StringComparer.OrdinalIgnoreCase) { "CMP", "TEST", "BT", "UCOMISS", "COMISS", "UCOMISD", "COMISD", "PUSH" };

    private static List<Access> Parse(JArray matches, int disp)
    {
        var list = new List<Access>();
        foreach (var m in matches.OfType<JObject>())
        {
            var ops = m["operands"]?.ToString() ?? "";
            var mnemonic = m["mnemonic"]?.ToString() ?? "";
            var mem = MemOperand().Matches(ops).FirstOrDefault(x => Convert.ToInt32(x.Groups["d"].Value, 16) == disp);
            if (mem == null) continue;
            var baseExpr = mem.Groups["base"].Value.Trim();
            var baseReg = baseExpr.Split(' ')[0].ToUpperInvariant();
            if (baseReg is "RSP" or "RBP" or "ESP" or "EBP" or "RIP") continue; // stack frames / RIP-relative: not a struct field
            int width = mem.Groups["w"].Value.ToLowerInvariant() switch { "byte" => 1, "word" => 2, "dword" => 4, "qword" => 8, "xmmword" => 16, "ymmword" => 32, _ => 0 };
            var parts = SplitOperands(ops);
            bool memFirst = parts.Count > 0 && parts[0].Contains('[');
            string kind = mnemonic.Equals("LEA", StringComparison.OrdinalIgnoreCase) ? "address-of"
                : ReadOnlyMnemonics.Contains(mnemonic) ? (mnemonic.StartsWith("BT", StringComparison.OrdinalIgnoreCase) || mnemonic.Equals("TEST", StringComparison.OrdinalIgnoreCase) ? "bit-test" : "read")
                : memFirst ? (mnemonic.ToUpperInvariant() is "OR" or "BTS" ? "set-bits" : mnemonic.ToUpperInvariant() is "AND" or "BTR" ? "clear-bits" : "write")
                : "read";
            int[]? bits = null;
            if (parts.Count >= 2 && TryImmediate(parts[^1], out var imm))
            {
                if (mnemonic.ToUpperInvariant() is "BT" or "BTS" or "BTR" or "BTC") bits = [(int)imm];
                else if (mnemonic.ToUpperInvariant() is "TEST" or "OR" or "XOR")
                    bits = Enumerable.Range(0, Math.Max(8, width * 8)).Where(b => (imm >> b & 1) == 1).ToArray();
                else if (mnemonic.ToUpperInvariant() == "AND" && memFirst)
                    bits = Enumerable.Range(0, Math.Max(8, width * 8)).Where(b => (imm >> b & 1) == 0).ToArray(); // bits cleared
                else if (mnemonic.ToUpperInvariant() == "AND")
                    bits = Enumerable.Range(0, Math.Max(8, width * 8)).Where(b => (imm >> b & 1) == 1).ToArray();
                if (bits is { Length: 0 or > 16 }) bits = null;
            }
            list.Add(new Access(m["function"]?.ToString() ?? "?", AddressOf(m["address"]), mnemonic, ops, baseExpr, width, kind, bits));
        }
        return list;
    }

    private static string AddressOf(JToken? a) => a is JObject o ? o["address"]?.ToString() ?? "" : a?.ToString() ?? "";

    private static List<string> SplitOperands(string ops)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < ops.Length; i++)
        {
            if (ops[i] == '[') depth++;
            else if (ops[i] == ']') depth--;
            else if (ops[i] == ',' && depth == 0) { parts.Add(ops[start..i].Trim()); start = i + 1; }
        }
        parts.Add(ops[start..].Trim());
        return parts;
    }

    private static bool TryImmediate(string s, out long v)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }

    private static int? ParseOffset(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.GetInt32();
        var s = e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : "";
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        return int.TryParse(s, out var d) ? d : null;
    }

    private static string Outline(JObject r)
    {
        var sb = new StringBuilder();
        var t = r["target"]!;
        sb.Append($"Code accessing {r["struct"] ?? "struct"} +{t["offset"]} ({t["hex"]})");
        if (t["bit"]?.Type == JTokenType.Integer) sb.Append($" bit {t["bit"]}");
        sb.AppendLine($" in {r["program"]} (static, Ghidra)");
        sb.AppendLine($"Fingerprint: functions touching >= {r["minKnown"]} of: " +
                      string.Join(", ", ((JArray)r["anchors"]!).Where(a => a["skipped"] == null).Select(a => $"+{a["offset"]} {a["field"]}")));
        var skipped = ((JArray)r["anchors"]!).Where(a => a["skipped"] != null).Select(a => $"+{a["offset"]} {a["field"]}").ToList();
        if (skipped.Count > 0) sb.AppendLine($"(too common to fingerprint with: {string.Join(", ", skipped)})");
        var fns = (JArray)r["functions"]!;
        sb.AppendLine($"{fns.Count} function(s) of this struct access the field; {r["programWideAccesses"]} instructions program-wide use the displacement.");
        foreach (var a in ((JArray)r["accesses"]!).OfType<JObject>().Take(25))
        {
            sb.Append($"  {a["function"]} @{a["address"]}  {a["instruction"]}  [{a["kind"]}");
            if (a["bits"] is JArray b) sb.Append($", bits {string.Join(",", b)}");
            sb.Append($"; {a["confidence"]}: also +{string.Join(" +", ((JArray)a["knownFieldsAlsoAccessed"]!).Select(x => x.ToString().TrimStart('+')))}]");
            if (a["matchesBit"]?.Value<bool>() == true) sb.Append("  <- tests the bit");
            sb.AppendLine();
        }
        foreach (var d in ((JArray)r["decompiled"]!).OfType<JObject>())
        {
            sb.AppendLine().AppendLine($"-- {d["function"]}: {d["signature"]}");
            sb.AppendLine(d["excerpt"]?.ToString() ?? d["error"]?.ToString());
        }
        sb.AppendLine().Append(r["unanchored"]).Append(". Name the field from what the code does with it; confirm with a one-variable experiment (probe_memory).");
        return sb.ToString();
    }
}
