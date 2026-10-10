using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Bridge;

/// <summary>
/// A HUD restart that cut a running tool. tools\restart-hud.ps1 writes &lt;bridge dir&gt;\restart-state.json before it stops
/// the HUD: who asked, why, when it started (startedAt), who granted it (auto | user | force | uncoordinated) and over
/// which blockers. A tool that waits on the HUD (pipeline_trace, profile_plugin, hud_health_report, await_change,
/// experiment_step_start) checks it while waiting: a restart that started after the tool did means the work is lost, so
/// the tool returns "interrupted by a HUD restart requested by X (reason)" instead of a bare connection loss or a stale id.
/// </summary>
public sealed record HudRestartInfo(string Game, string Who, string? Reason, DateTime StartedAt, string Phase, string? GrantedBy, string? Over)
{
    /// <summary>"interrupted by a HUD restart requested by X (reason), which the user allowed over: ..." (no trailing period).</summary>
    public string Text(string what) =>
        $"{what} was interrupted by a HUD restart requested by {Who}{(Reason != null ? $" ({Reason})" : "")} at {StartedAt:HH:mm:ss} UTC" +
        GrantedBy switch
        {
            "user" => Over != null ? $"; the user pressed Restart now over: {Over}" : "; the user pressed Restart now",
            "force" => "; it skipped the coordination (-Force)",
            "uncoordinated" => "; the bridge couldn't be asked first",
            _ => "",
        };

    public JObject Json() => new()
    {
        ["game"] = Game, ["who"] = Who, ["reason"] = Reason, ["startedAt"] = StartedAt.ToString("O"), ["phase"] = Phase,
        ["grantedBy"] = GrantedBy, ["over"] = Over,
    };
}

public static class HudRestartState
{
    /// <summary>The last restart of this bridge's HUD as the script recorded it; null when there is none (or it's unreadable).</summary>
    public static HudRestartInfo? Read(BridgeClient bridge)
    {
        try
        {
            var path = Path.Combine(bridge.BridgeDir, "restart-state.json");
            if (!File.Exists(path)) return null;
            var o = JObject.Parse(File.ReadAllText(path));
            var at = o["startedAt"]?.Value<DateTime>() ?? o["at"]?.Value<DateTime>() ?? File.GetLastWriteTimeUtc(path);
            return new HudRestartInfo(o["game"]?.ToString() ?? bridge.Game, o["who"]?.ToString() ?? "someone",
                Str(o["reason"]), at.ToUniversalTime(), o["phase"]?.ToString() ?? "?", Str(o["grantedBy"]), Str(o["over"]));
        }
        catch { return null; }
    }

    private static string? Str(JToken? t) => t == null || t.Type == JTokenType.Null || t.ToString().Length == 0 ? null : t.ToString();

    /// <summary>A restart of <paramref name="game"/>'s HUD that started at or after <paramref name="sinceUtc"/> (when a tool began).</summary>
    public static HudRestartInfo? Since(BridgeRegistry bridges, string? game, DateTime sinceUtc)
    {
        foreach (var b in bridges.Bridges)
        {
            if (!string.IsNullOrEmpty(game) && game != "auto" && b.Game != "auto" && b.Game != game) continue;
            if (Read(b) is { } r && r.StartedAt >= sinceUtc.AddSeconds(-1)) return r;
        }
        return null;
    }

    /// <summary>The tool result for work a restart cut: isError, structured {error: hud_restarted, interrupted, restart, message}.</summary>
    public static CallToolResult Interrupted(HudRestartInfo r, string what, JObject? partial = null)
    {
        var message = r.Text(what) + ". Its results are lost; run it again once the HUD is back (bridge_status).";
        var o = new JObject { ["error"] = "hud_restarted", ["interrupted"] = true, ["message"] = message, ["restart"] = r.Json() };
        if (partial != null) o["partial"] = partial;
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = message }],
            StructuredContent = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(o.ToString(Newtonsoft.Json.Formatting.None)),
            IsError = true,
        };
    }
}

/// <summary>Thrown inside a waiting loop when a restart cut the work; the tool turns it into <see cref="HudRestartState.Interrupted"/>.</summary>
public sealed class HudRestartedException(HudRestartInfo restart, string what) : McpException(restart.Text(what) + ".")
{
    public HudRestartInfo Restart { get; } = restart;
    public string What { get; } = what;
}
