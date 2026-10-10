using System.ComponentModel;
using ExileApiMcp.Bridge;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Tools;

/// <summary>C# inside the running HUD, through the bridge's script.run (Roslyn scripting).</summary>
[McpServerToolType]
public static class ScriptTools
{
    [McpServerTool(Name = "run_csharp", Title = "Run C# in the HUD", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ScriptResult))]
    [Description("""
        Compile and run a C# script inside the running HUD (Roslyn scripting), for testing a theory against the live
        object model with real code: LINQ over entities, reflection into non-public members, calling into other plugins.
        Prefer eval_path / get_* for single values; use this when you need logic.

        In scope: GameController (the HUD's), Log(object) to print lines, the game's namespaces (ExileCore or
        ExileCore2 + PoEMemory.*, Shared.*), System.Linq/Collections/Numerics/Reflection. The value of the last
        expression (no trailing semicolon) is returned, serialized with depth/size limits. Other plugins: reach them
        through Core.Current.pluginManager.Plugins and use `dynamic` (they live in separate load contexts).
        Example:  GameController.Entities.Where(e => e.IsHostile && e.IsAlive).GroupBy(e => e.Rarity).Select(g => new { g.Key, n = g.Count() })

        Rules: read and inspect; NEVER send keyboard/mouse input or write game memory (anti-cheat). Scripts run on the
        HUD's main thread by default (thread='worker' for pure reads) and cannot be aborted: avoid unbounded loops.
        Off by default in the bridge ('Allow C# Scripts' setting); a scripts_disabled error means the user hasn't
        enabled it. Compile errors come back as diagnostics with line numbers in your code.
        """)]
    public static async Task<CallToolResult> RunCSharp(BridgeRegistry bridges,
        [Description("C# script body (statements; the last expression without ';' is the result)")] string code,
        [Description("'main' (default: the HUD's main thread, safe for any HUD API) or 'worker' (immediate, for reads)")] string thread = "main",
        [Description("Run timeout in ms (100-120000, default 10000); only observed between statements Roslyn controls")] int timeoutMs = 10_000,
        [Description(BridgeRegistry.GameParamDescription)] string? game = null,
        CancellationToken ct = default)
    {
        var r = await RunAsync(bridges, code, thread, timeoutMs, game, ct);
        return r["status"]?.Value<string>() is "done" or "unknown" ? Result(r) : TypedReply.Of<ScriptResult>(r);
    }

    /// <summary>script.run, then script.result until done: the bridge's raw reply (also used by verify_finding's script checks).</summary>
    internal static async Task<JToken> RunAsync(BridgeRegistry bridges, string code, string thread, int timeoutMs, string? game, CancellationToken ct)
    {
        var (bridge, started) = await bridges.CallAsync(game, "script.run",
            new JObject { ["code"] = code, ["thread"] = thread, ["timeoutMs"] = timeoutMs }, ct);
        var id = started["id"]?.Value<string>();
        if (id == null || started["status"]?.Value<string>() == "rejected") return started;

        var g = bridge.Game == "auto" ? game : bridge.Game;
        // Compiling takes ~0.5-3 s (the first script of a HUD run is the slowest), then the run itself.
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 100, 120_000) + 45_000);
        var delay = 150;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay, ct);
            delay = Math.Min(delay + 100, 750);
            JToken r;
            try { (_, r) = await bridges.CallAsync(g, "script.result", new JObject { ["id"] = id }, ct); }
            catch (McpException) { continue; } // the main thread can be busy with the script itself
            if (r["status"]?.Value<string>() is "done" or "unknown") return r;
        }
        return new JObject
        {
            ["id"] = id, ["status"] = "timeout",
            ["message"] = "No result yet. If the script loops forever on the main thread, the HUD is frozen and needs a restart.",
        };
    }

    private static CallToolResult Result(JToken r)
    {
        if (r is JObject o && o["ok"]?.Value<bool>() == false && o["error"] == null) o["error"] = "failed";
        return TypedReply.Of<ScriptResult>(r);
    }
}
