using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Hud;

/// <summary>
/// Is the user's screen showing the HUD right now? Two silent reasons it is not, both seen in practice:
/// - the display turned off (Windows' idle timeout): the HUD keeps running and reporting, but its overlay is not
///   composited, so screenshots show the game without any overlay;
/// - the game is not the foreground window: the HUD hides its overlay, and the game may throttle its frame rate.
/// Read with plain Win32 (no WMI): seconds since the last input vs the power plan's display timeout, and the foreground
/// window's class (POEWindowClass for both games).
/// </summary>
public static class DesktopState
{
    public static JObject Read()
    {
        var o = new JObject();
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref lii))
            {
                var idle = (Environment.TickCount64 & 0xFFFFFFFF) - lii.dwTime;
                if (idle < 0) idle += 0x100000000;
                o["idleSeconds"] = idle / 1000;
                if (DisplayTimeoutSeconds() is { } t)
                {
                    o["displayTimeoutSeconds"] = t;
                    o["displayLikelyOff"] = t > 0 && idle / 1000 >= t;
                }
            }
            var fg = GetForegroundWindow();
            var cls = new StringBuilder(256);
            GetClassName(fg, cls, cls.Capacity);
            o["gameForeground"] = cls.ToString() == "POEWindowClass";
            if (o["displayLikelyOff"]?.Value<bool>() == true)
                o["warning"] = "The display is probably off (idle past the power plan's timeout): the HUD runs, but its overlay is not composited, so screenshots won't show it.";
            else if (o["gameForeground"]?.Value<bool>() == false)
                o["warning"] = "The game is not the foreground window: the HUD hides its overlay and the game may throttle its frame rate.";
        }
        catch (Exception ex) { o["error"] = ex.Message; }
        return o;
    }

    /// <summary>The active power plan's "turn off the display after" (AC), in seconds; null if unreadable.</summary>
    private static uint? DisplayTimeoutSeconds()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var scheme) != 0) return null;
        try
        {
            var sub = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");   // SUB_VIDEO
            var setting = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");   // VIDEOIDLE
            return PowerReadACValueIndex(IntPtr.Zero, scheme, ref sub, ref setting, out var v) == 0 ? v : null;
        }
        finally { LocalFree(scheme); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, IntPtr scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
}
