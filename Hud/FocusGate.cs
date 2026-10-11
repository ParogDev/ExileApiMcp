using ExileApiMcp.Bridge;
using ExileApiMcp.Tools;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Hud;

/// <summary>
/// Measurements that need the game in front (traces, profiles, overlay accuracy) check it before they start instead of
/// running anyway and reporting afterwards that the numbers are worthless. While the game is behind another window the
/// HUD hides its overlay (plugins don't render) and the game may cap its fps, so the measurement waits for the game to
/// come to the front (one toast says so), and is skipped with error game_not_in_front if it doesn't. Read locally with
/// DesktopState (the foreground window's class), so a skipped measurement never touches the HUD.
/// </summary>
public static class FocusGate
{
    public const string Error = "game_not_in_front";
    public const string WaitParamDescription = "Seconds to wait for the game to be the foreground window before measuring (0-600, default 60; 0 = skip at once). The measurement never runs with the game behind another window";
    public const int DefaultWaitSec = 60;

    /// <summary>Is the game the foreground window? Null when it can't be read (then don't block on it).</summary>
    public static bool? GameInFront() => DesktopState.Read()["gameForeground"]?.Value<bool?>();

    /// <summary>
    /// Null when the game is in front (now, or within waitSec); otherwise the error to return instead of measuring.
    /// Waiting shows one toast so the user knows why nothing happens; waitSec 0 checks once, silently.
    /// </summary>
    public static async Task<JObject?> WaitAsync(BridgeRegistry bridges, string? game, string what, int waitSec, CancellationToken ct)
    {
        if (GameInFront() != false) return null;
        waitSec = Math.Clamp(waitSec, 0, 600);
        if (waitSec > 0)
        {
            await GuideTools.LogAsync(bridges, game, $"{what} waits for the game: bring it to the front to start", "warn", ct);
            var until = DateTime.UtcNow.AddSeconds(waitSec);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(500, ct);
                if (GameInFront() != false)
                {
                    await Task.Delay(500, ct);   // the overlay comes back and the game lifts its background fps cap
                    if (GameInFront() != false) return null;
                }
            }
        }
        return new JObject
        {
            ["error"] = Error,
            ["message"] = $"{what} not run: the game isn't the foreground window" + (waitSec > 0 ? $" (waited {waitSec} s)" : "") +
                          ", so the overlay is hidden and the game may cap its fps. Run it again when the user has the game in front; focus_game brings it forward if they agree.",
            ["waitedSec"] = waitSec,
        };
    }
}
