using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>
/// Guided flows (bridge guide.flow): multi-step in-game tasks where the HUD shows the next step from the game's state.
/// The bridge owns the running flow (any client can read it); this server is stateless: it expands reusable recipes
/// (Knowledge/flows.json) into flows - params plus values derived from the game's data tables in, a full flow out.
/// </summary>
[McpServerToolType]
public static class FlowTools
{
    private static readonly Lazy<JObject> Recipes = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("knowledge/flows.json");
        return s == null ? new JObject { ["recipes"] = new JArray() } : JObject.Parse(new StreamReader(s).ReadToEnd());
    });

    [McpServerTool(Name = "guide_flow", Title = "Guide the user through a multi-step task", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(FlowStateResult), IconSource = ExileApiMcp.Hosting.IconSet.GuideLight)]
    [Description("Guide the user through a multi-step in-game task: the HUD highlights the next thing to do, re-evaluated from " +
                 "the game state every 100 ms (current step = first step not done; it goes back if the user navigates away; for " +
                 "each step the first option that's possible right now is shown, e.g. right-click the tab if it's in view, else " +
                 "click it in the tab list, else open the tab list). Use a recipe (list them with action=recipes) or pass a flow " +
                 "JSON. action=state reads progress, action=stop ends it (your own flow only). Never clicks anything itself. The card is shared by " +
                 "every agent on the HUD: start waits for your turn (attention queue, inside timeoutSec, with progress notifications) " +
                 "and the turn is the flow's until it ends.")]
    public static async Task<CallToolResult> GuideFlow(BridgeRegistry bridges, IProgress<ProgressNotificationValue> progress,
        [Description("start | state | stop | recipes | expand (returns the flow without starting it)")] string action = "start",
        [Description("Recipe id from Knowledge/flows.json, e.g. stash-tab-affinity")] string? recipe = null,
        [Description("Recipe params as JSON object or 'k=v;k=v', e.g. tab=DUMP;affinity=Ritual;set=false")] JsonElement? args = null,
        [Description("A full flow instead of a recipe: {title, goal?, steps:[{label, done?, options:[{target, label?, when?}]}]}")] JsonElement? flow = null,
        [Description("Stop the flow after this many seconds (default 600)")] int timeoutSec = 600,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        switch (action)
        {
            case "recipes":
                var recipes = new JObject { ["recipes"] = new JArray((Recipes.Value["recipes"] as JArray ?? []).OfType<JObject>()
                    .Select(r => new JObject { ["id"] = r["id"], ["title"] = r["title"], ["games"] = r["games"], ["params"] = r["params"] })) };
                return Dto.Result(TypedReply.Parse<FlowRecipeList>(recipes), recipes.ToString(Formatting.None));
            case "state":
                return TypedReply.Of<FlowStateResult>(Need((await bridges.CallAsync(game, "guide.flow_state", new JObject(), ct)).Result));
            case "stop":
                return TypedReply.Of<FlowStateResult>(Need((await bridges.CallAsync(game, "guide.flow", new JObject { ["stop"] = true }, ct)).Result));
        }
        JObject f;
        if (flow is { ValueKind: JsonValueKind.Object or JsonValueKind.String } fl)
            f = JObject.Parse(fl.ValueKind == JsonValueKind.String ? fl.GetString()! : fl.GetRawText());
        else if (recipe != null)
            f = await ExpandAsync(bridges, game, recipe, ParseArgs(args), ct);
        else throw new McpException("Pass recipe (+ args) or flow. action=recipes lists the recipes.");
        // The flow itself: its shape is the recipe's (FlowStateResult only describes the keys it shares, e.g. title and steps[].label).
        if (action == "expand") return Dto.Result(TypedReply.Parse<JsonElement>(f), f.ToString(Formatting.None));
        // The card is shared by every agent on the HUD: wait for our turn (attention queue) inside the flow's own time limit.
        // The turn is the flow's from then on: the bridge ends it when the flow ends (goal, timeout or stop).
        var turn = await AttentionTurn.TakeAsync(bridges, game, "flow", f["title"]?.ToString() ?? recipe ?? "Guided task", TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 10, 3600)), null, progress, ct);
        if (!turn.Granted) throw new McpException($"guide_flow: {turn.Note}");
        f["timeoutSec"] = Math.Max(10, timeoutSec - (int)turn.Waited.TotalSeconds);
        JToken r;
        try { r = Need((await bridges.CallAsync(game, "guide.flow", f, ct)).Result); }
        catch { await turn.ReleaseAsync(bridges, game); throw; }
        if (r["error"] != null) await turn.ReleaseAsync(bridges, game);
        else
        {
            r["next"] = "The HUD guides the user step by step. Read progress with guide_flow action=state; it ends by itself when the goal holds.";
            if (turn.Id != null) r["attention"] = turn.Id;
            if (turn.Waited.TotalSeconds >= 1) r["waitedSec"] = (int)turn.Waited.TotalSeconds;
        }
        return TypedReply.Of<FlowStateResult>(r);
    }

    /// <summary>Expand a recipe: derive values from game data, then substitute ${...} everywhere (keys too).</summary>
    internal static async Task<JObject> ExpandAsync(BridgeRegistry bridges, string? game, string id, Dictionary<string, string> args, CancellationToken ct)
    {
        var rec = (Recipes.Value["recipes"] as JArray ?? []).OfType<JObject>().FirstOrDefault(r => r["id"]?.ToString() == id)
                  ?? throw new McpException($"No recipe '{id}' (guide_flow action=recipes lists them).");
        foreach (var p in (rec["params"] as JObject)?.Properties() ?? [])
            if (!args.ContainsKey(p.Name)) throw new McpException($"Recipe '{id}' needs {p.Name}: {p.Value}");
        var values = new Dictionary<string, string>(args, StringComparer.OrdinalIgnoreCase);
        foreach (var d in (rec["derive"] as JObject)?.Properties() ?? [])
        {
            var spec = (JObject)d.Value;
            var table = spec["table"]!.ToString();
            var want = Substitute(spec["equals"]!.ToString(), values);
            var (_, data) = await bridges.CallAsync(game, "data.read", new JObject { ["file"] = table, ["offset"] = 0, ["limit"] = 500 }, ct);
            if (data["error"] != null) throw new McpException($"Recipe '{id}': {table}: {data["message"]}");
            var textKey = $"+{spec["textAt"]}:";
            var row = (data["rows"] as JArray ?? []).OfType<JObject>().FirstOrDefault(r =>
                (r["strings"] as JArray ?? []).Any(s => string.Equals(s.ToString(), textKey + want, StringComparison.OrdinalIgnoreCase)))
                ?? throw new McpException($"Recipe '{id}': no row of {table} has '{want}' at +{spec["textAt"]}. Known: " +
                    string.Join(", ", (data["rows"] as JArray ?? []).SelectMany(r => (r["strings"] as JArray ?? []).Select(s => s.ToString()))
                        .Where(s => s.StartsWith(textKey)).Select(s => s[textKey.Length..])));
            string? value = null;
            if (spec["refAt"] != null)
            {
                var refKey = $"+{spec["refAt"]}:";
                var reference = (row["refs"] as JArray ?? []).Select(x => x.ToString()).FirstOrDefault(x => x.StartsWith(refKey));
                var m = reference == null ? null : Regex.Match(reference, @"\[(\d+)\]");
                value = m is { Success: true } ? m.Groups[1].Value : throw new McpException($"Recipe '{id}': {table} row has no reference at {refKey}");
            }
            else value = row["index"]!.ToString();
            values[d.Name] = value;
            if (long.TryParse(value, out var bit) && bit is >= 0 and < 64) values[d.Name + "_mask"] = (1L << (int)bit).ToString();
        }
        var flow = (JObject)rec["flow"]!.DeepClone();
        return (JObject)Walk(flow, values);
    }

    private static JToken Walk(JToken t, Dictionary<string, string> v) => t switch
    {
        JObject o => new JObject(o.Properties().Select(p => new JProperty(Substitute(p.Name, v), Walk(p.Value, v)))),
        JArray a => new JArray(a.Select(x => Walk(x, v))),
        JValue { Type: JTokenType.String } s => Typed(Substitute(s.ToString(), v)),
        _ => t.DeepClone(),
    };

    // Numbers stay numbers after substitution (masks, offsets).
    private static JToken Typed(string s) => Regex.IsMatch(s, @"^-?\d+$") && long.TryParse(s, out var n) ? new JValue(n) : new JValue(s);

    /// <summary>${name} and ${flag?yes:no}, innermost first (so ${set?${bit_mask}:0} works).</summary>
    internal static string Substitute(string s, Dictionary<string, string> v)
    {
        var rx = new Regex(@"\$\{([^${}]+)\}");
        for (int guard = 0; guard < 20 && rx.IsMatch(s); guard++)
            s = rx.Replace(s, m =>
            {
                var expr = m.Groups[1].Value;
                var q = expr.IndexOf('?');
                if (q > 0)
                {
                    var colon = expr.IndexOf(':', q);
                    var flag = v.TryGetValue(expr[..q], out var fv) && fv.Equals("true", StringComparison.OrdinalIgnoreCase);
                    return colon < 0 ? (flag ? expr[(q + 1)..] : "") : flag ? expr[(q + 1)..colon] : expr[(colon + 1)..];
                }
                return v.TryGetValue(expr, out var val) ? val : throw new McpException($"Unknown value ${{{expr}}} in recipe.");
            });
        return s;
    }

    private static Dictionary<string, string> ParseArgs(JsonElement? args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args is not { } a) return d;
        if (a.ValueKind == JsonValueKind.Object)
            foreach (var p in a.EnumerateObject()) d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
        else if (a.ValueKind == JsonValueKind.String)
        {
            var s = a.GetString()!.Trim();
            if (s.StartsWith('{')) return ParseArgs(JsonDocument.Parse(s).RootElement.Clone());
            foreach (var kv in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var i = kv.IndexOf('=');
                if (i > 0) d[kv[..i].Trim()] = kv[(i + 1)..].Trim();
            }
        }
        return d;
    }

    private static JToken Need(JToken? r)
    {
        if (r is not JObject o || (o["ok"] == null && o["error"] == null))
            throw new McpException("This HUD's bridge plugin has no guided flows yet: update What's an AI Bridge and restart the HUD.");
        return r;
    }
}
