using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ExileApiMcp.Incidents;

/// <summary>
/// Why a game's bridge doesn't answer, named at the broken link, in the order the links are walked:
///   1. a coordinated restart is stopping / relaunching the HUD (restart-state.json, Bridge/HudRestartState.cs);
///   2. the HUD process (Loader.exe from that HUD folder) isn't running, or was stopped on purpose (restart -StopOnly);
///   3. the bridge port is inside a Windows excluded port range (Hyper-V / WinNAT reserve ranges at boot);
///   4. the HUD runs but the bridge wrote no token (plugin off, failed to load, server failed to start);
///   5. the HUD runs but nothing listens on the bridge port;
///   6. something listens but doesn't answer.
/// <see cref="Diagnose"/> is pure (facts in, verdict out) and unit-tested; <see cref="Gather"/> collects the facts.
/// </summary>
public static class BridgeDiagnosis
{
    /// <summary>Bridge ports: 50900 and the 9 the bridge falls back to (TcpBridgeServer PortFallbackRange); 50910 is the MCP HTTP port.</summary>
    public const int BridgeFirstPort = 50900, BridgePortCount = 10, McpHttpPort = 50910;

    public const string PortsFix =
        "The user must run, in an elevated terminal: net stop winnat; netsh int ipv4 add excludedportrange protocol=tcp startport=50900 numberofports=11; net start winnat " +
        "(this reserves 50900-50910 for the HUD bridges and the MCP so Windows stops handing them to Hyper-V/WinNAT), then restart the HUD (hud_restart).";

    /// <summary>An excluded range. Administered ranges ("*" in netsh: added with "netsh int ipv4 add excludedportrange", e.g. the
    /// fix above) keep Windows from handing the ports out but don't stop an application binding them, so they never block the
    /// bridge (the bridge runs on 50900 inside the administered 50900-50910 here). The ranges Hyper-V / WinNAT reserve at boot
    /// (no "*") do.</summary>
    public sealed record PortRange(int Start, int End, bool Administered = false)
    {
        public bool Contains(int port) => port >= Start && port <= End;
        public override string ToString() => (Start == End ? $"{Start}" : $"{Start}-{End}") + (Administered ? " (administered)" : "");
    }

    public enum Probe { NotTried, Refused, Accepted, Timeout }

    public sealed class Facts
    {
        public string Game = "";
        public string BridgeDir = "";
        public string HudDir = "";
        /// <summary>null: unknown (process list unreadable).</summary>
        public bool? HudRunning;
        public int? HudPid;
        public DateTime? HudStartedAt;
        public bool TokenExists;
        public DateTime? TokenWrittenAt;
        /// <summary>The port the bridge said it bound (bridge-port.txt), else the default.</summary>
        public int Port = BridgeFirstPort;
        public bool PortFromFile;
        public List<PortRange>? Excluded;   // null: netsh unavailable
        public string? NetshError;
        public Probe Probe = Probe.NotTried;
        // restart-state.json
        public string? RestartPhase, RestartWho, RestartReason;
        public DateTime? RestartStartedAt, RestartWrittenAt;
        public bool RestartStopOnly;
        public string? Error;   // the bridge client's own message
        public DateTime Now = DateTime.UtcNow;
    }

    public sealed class Verdict
    {
        public string Kind = "bridge-down";
        public string Scope = "";
        /// <summary>restarting | hud-stopped | hud-not-running | ports-reserved | token-missing | not-listening | not-answering</summary>
        public string Cause = "";
        public string Title = "";
        public string Detail = "";
        public string? Fix;
        public DateTime? Eta;
        public string Key => $"{Kind}:{Scope}";
    }

    // ── Pure ──

    private static readonly Regex RangeLine = new(@"^\s*(\d{1,5})\s+(\d{1,5})\b(\s*\*)?", RegexOptions.Compiled);

    /// <summary>`netsh int ipv4 show excludedportrange protocol=tcp` output (any UI language): every "start end [*]" line.</summary>
    public static List<PortRange> ParseExcludedRanges(string netshOutput)
    {
        var list = new List<PortRange>();
        foreach (var raw in netshOutput.Split('\n'))
        {
            var m = RangeLine.Match(raw);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var a) && int.TryParse(m.Groups[2].Value, out var b) && a <= b && b <= 65535)
                list.Add(new PortRange(a, b, m.Groups[3].Success));
        }
        return list;
    }

    /// <summary>The bridge ports (50900-50909) inside a system-reserved (not administered) range, with the range that covers each.</summary>
    public static List<(int Port, PortRange Range)> BlockedBridgePorts(IEnumerable<PortRange> excluded, int first = BridgeFirstPort, int count = BridgePortCount)
    {
        var ranges = excluded.Where(r => !r.Administered).ToList();
        var hits = new List<(int, PortRange)>();
        for (var p = first; p < first + count; p++)
            if (ranges.FirstOrDefault(r => r.Contains(p)) is { } r) hits.Add((p, r));
        return hits;
    }

    /// <summary>What broke, from the facts. Null when nothing is wrong that we can name (the probe found a live bridge).</summary>
    public static Verdict? Diagnose(Facts f)
    {
        var G = f.Game == "poe1" ? "PoE1" : f.Game == "poe2" ? "PoE2" : f.Game;
        var v = new Verdict { Scope = f.Game is "poe1" or "poe2" ? f.Game : "machine" };
        var restartAge = f.RestartWrittenAt is { } w ? f.Now - w : TimeSpan.MaxValue;

        // 1. A restart in progress.
        if (f.RestartPhase == "stopping" && restartAge < TimeSpan.FromMinutes(3) && !f.RestartStopOnly)
        {
            v.Kind = "hud-restart"; v.Cause = "restarting";
            v.Title = $"{G} HUD restart by {f.RestartWho ?? "someone"}";
            v.Detail = $"restart-state.json in the bridge folder says phase 'stopping' since {Hm(f.RestartStartedAt)}" +
                       (f.RestartReason != null ? $" ({f.RestartReason})" : "") + ": the bridge is down until the new HUD starts it.";
            v.Fix = "Nothing: wait for it. Bridge calls made meanwhile wait for the HUD to come back.";
            v.Eta = (f.RestartStartedAt ?? f.Now).AddSeconds(60);
            if (v.Eta < f.Now) v.Eta = f.Now.AddSeconds(30);
            return v;
        }

        // 2. The HUD process.
        if (f.HudRunning == false)
        {
            if (f.RestartStopOnly && restartAge < TimeSpan.FromHours(12))
            {
                v.Cause = "hud-stopped";
                v.Title = $"{G} HUD was stopped on purpose by {f.RestartWho ?? "someone"}";
                v.Detail = $"No Loader.exe from {f.HudDir} runs; restart-state.json says a stop-only restart at {Hm(f.RestartStartedAt)}" +
                           (f.RestartReason != null ? $" ({f.RestartReason})" : "") + ".";
                v.Fix = $"Start it again when the user agrees: hud_restart game={f.Game} (or the user starts Loader.exe).";
                return v;
            }
            v.Cause = "hud-not-running";
            v.Title = $"{G} HUD is not running";
            v.Detail = $"No Loader.exe from {f.HudDir} runs (checked the process list), so its bridge can't answer." +
                       (f.TokenExists ? " A bridge-token.txt is left over from an earlier run." : "");
            v.Fix = $"The user starts the {G} HUD, or an agent runs hud_restart game={f.Game} (it launches a HUD that isn't running).";
            return v;
        }

        // 3. Windows reserved the bridge ports.
        if (f.Excluded != null)
        {
            var blocked = BlockedBridgePorts(f.Excluded);
            var portBlocked = blocked.Any(b => b.Port == f.Port);
            if (blocked.Count == BridgePortCount || (portBlocked && f.Probe != Probe.Accepted))
            {
                v.Kind = "ports-reserved"; v.Scope = "machine"; v.Cause = "ports-reserved";
                var ranges = blocked.Select(b => b.Range).Distinct().ToList();
                v.Title = blocked.Count == BridgePortCount
                    ? $"Windows reserved the HUD bridge ports {BridgeFirstPort}-{BridgeFirstPort + BridgePortCount - 1}: no bridge can start"
                    : $"Windows reserved bridge port {f.Port} ({blocked.Count} of {BridgePortCount} bridge ports blocked)";
                v.Detail = $"netsh int ipv4 show excludedportrange protocol=tcp lists {string.Join(", ", ranges)}, covering bridge port(s) " +
                           $"{Compact(blocked.Select(b => b.Port))}. The bridge can't bind there (seen as '{(f.TokenExists ? "nothing listening" : "no bridge token")}' " +
                           $"by {G}); Hyper-V / WinNAT reserve such ranges at boot." +
                           (f.Excluded.Any(r => !r.Administered && r.Contains(McpHttpPort)) ? $" The MCP HTTP port {McpHttpPort} is reserved too." : "");
                v.Fix = PortsFix;
                return v;
            }
        }

        // 4. No token: the bridge never started in this HUD.
        var staleToken = f.TokenExists && f.HudStartedAt is { } hs && f.TokenWrittenAt is { } tw && tw < hs.AddSeconds(-5);
        if (!f.TokenExists || staleToken)
        {
            v.Cause = "token-missing";
            v.Title = $"{G} HUD runs but its bridge didn't start";
            v.Detail = $"Loader.exe{(f.HudPid != null ? $" (pid {f.HudPid})" : "")} from {f.HudDir} runs, but " +
                       (staleToken ? $"bridge-token.txt ({Hm(f.TokenWrittenAt)}) is older than the HUD process ({Hm(f.HudStartedAt)}): the bridge hasn't started in this run."
                                   : $"{f.BridgeDir} has no bridge-token.txt: 'Whats An AI Bridge' is off, failed to compile/load, or its server failed to start.") +
                       (f.Excluded == null && f.NetshError != null ? $" (Port reservations not checked: {f.NetshError}.)" : "");
            v.Fix = $"hud_plugins plugin=bridge game={f.Game} and hud_log plugin=\"Whats An AI Bridge\" game={f.Game} say why; enable the plugin in the HUD menu, or hud_restart game={f.Game}.";
            return v;
        }

        // 5./6. Token present: what's on the port.
        switch (f.Probe)
        {
            case Probe.Refused:
                v.Cause = "not-listening";
                v.Title = $"{G} HUD runs but nothing listens on bridge port {f.Port}";
                v.Detail = $"Connecting to 127.0.0.1:{f.Port} ({(f.PortFromFile ? "bridge-port.txt" : "default port")}) is refused while Loader.exe" +
                           $"{(f.HudPid != null ? $" (pid {f.HudPid})" : "")} runs and a token from {Hm(f.TokenWrittenAt)} exists: the bridge server stopped or failed to bind.";
                v.Fix = $"hud_log plugin=\"Whats An AI Bridge\" game={f.Game} shows why; hud_restart game={f.Game} usually fixes it.";
                return v;
            case Probe.Timeout:
            case Probe.Accepted when f.Error != null:
                v.Cause = "not-answering";
                v.Title = $"{G} bridge accepts connections on {f.Port} but doesn't answer";
                v.Detail = $"127.0.0.1:{f.Port} {(f.Probe == Probe.Timeout ? "doesn't complete a connect within 3 s" : "accepts a connection")}, but the bridge call failed: {f.Error}. " +
                           "The HUD may be hung, loading, or busy compiling.";
                v.Fix = $"Retry in a minute; if it persists, hud_health_report game={f.Game} or hud_restart game={f.Game}.";
                return v;
            default:
                return null;
        }
    }

    private static string Hm(DateTime? t) => t is { } x ? $"{x:HH:mm:ss}Z" : "?";

    private static string Compact(IEnumerable<int> ports)
    {
        var list = ports.OrderBy(p => p).ToList();
        if (list.Count == 0) return "";
        var parts = new List<string>();
        int start = list[0], prev = list[0];
        foreach (var p in list.Skip(1).Append(int.MaxValue))
        {
            if (p == prev + 1) { prev = p; continue; }
            parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
            start = prev = p;
        }
        return string.Join(", ", parts);
    }

    // ── Facts from the machine ──

    private static (DateTime At, List<PortRange>? Ranges, string? Error)? _netsh;
    private static readonly object NetshLock = new();

    /// <summary>The excluded TCP port ranges, cached for 60 s (one netsh process per minute at most).</summary>
    public static (List<PortRange>? Ranges, string? Error) ExcludedRanges(bool fresh = false)
    {
        lock (NetshLock)
        {
            if (!fresh && _netsh is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(60)) return (c.Ranges, c.Error);
            List<PortRange>? ranges = null; string? error = null;
            try
            {
                var psi = new ProcessStartInfo("netsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
                foreach (var a in new[] { "int", "ipv4", "show", "excludedportrange", "protocol=tcp" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                p.StandardInput.Close();
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } error = "netsh timed out"; }
                else if (p.ExitCode != 0) error = $"netsh exit {p.ExitCode}";
                else ranges = ParseExcludedRanges(outTask.Result);
            }
            catch (Exception ex) { error = $"netsh could not run: {ex.Message}"; }
            _netsh = (DateTime.UtcNow, ranges, error);
            return (ranges, error);
        }
    }

    public static Facts Gather(Bridge.BridgeClient bridge, string? error)
    {
        var f = new Facts { Game = bridge.Game, BridgeDir = bridge.BridgeDir, Error = error, Port = bridge.DefaultPort };
        f.HudDir = Path.GetDirectoryName(Path.GetFullPath(bridge.BridgeDir).TrimEnd('\\', '/')) ?? bridge.BridgeDir;

        var token = Path.Combine(bridge.BridgeDir, "bridge-token.txt");
        f.TokenExists = File.Exists(token);
        if (f.TokenExists) f.TokenWrittenAt = File.GetLastWriteTimeUtc(token);
        var portFile = Path.Combine(bridge.BridgeDir, "bridge-port.txt");
        try
        {
            if (File.Exists(portFile) && int.TryParse(File.ReadAllText(portFile).Trim(), out var port)) { f.Port = port; f.PortFromFile = true; }
        }
        catch (IOException) { }

        var restartPath = Path.Combine(bridge.BridgeDir, "restart-state.json");
        if (File.Exists(restartPath) && Bridge.HudRestartState.Read(bridge) is { } r)
        {
            f.RestartPhase = r.Phase; f.RestartWho = r.Who; f.RestartReason = r.Reason; f.RestartStartedAt = r.StartedAt;
            f.RestartWrittenAt = File.GetLastWriteTimeUtc(restartPath);
            try { f.RestartStopOnly = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(restartPath))["stopOnly"]?.ToObject<bool>() == true; } catch { }
        }

        (f.HudRunning, f.HudPid, f.HudStartedAt) = FindHud(f.HudDir);
        (f.Excluded, f.NetshError) = ExcludedRanges();
        if (f.HudRunning != false && f.TokenExists) f.Probe = ProbePort(f.Port);
        // The client's own connect saw the refusal (10061) even if our probe ran out of time.
        if (f.Probe == Probe.Timeout && error?.Contains("actively refused", StringComparison.OrdinalIgnoreCase) == true) f.Probe = Probe.Refused;
        return f;
    }

    /// <summary>Connect to 127.0.0.1:port for at most 3 s: refused, accepted or timed out (nothing is sent). Windows takes
    /// about 2 s to report a refused loopback connect (it retries the SYN), so a shorter wait reads "refused" as a timeout.</summary>
    public static Probe ProbePort(int port)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync(System.Net.IPAddress.Loopback, port);
            if (!t.Wait(3000)) return Probe.Timeout;
            return Probe.Accepted;
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException) { return Probe.Refused; }
        catch (SocketException) { return Probe.Refused; }
    }

    /// <summary>Is a Loader.exe from this HUD folder running? The HUD runs elevated, so its path is read with
    /// QueryFullProcessImageName on a limited-information handle (allowed across elevation).</summary>
    public static (bool? Running, int? Pid, DateTime? StartedAt) FindHud(string hudDir)
    {
        try
        {
            var want = Path.GetFullPath(Path.Combine(hudDir, "Loader.exe"));
            var unknown = false;
            foreach (var p in Process.GetProcessesByName("Loader"))
            {
                using (p)
                {
                    var path = ImagePath(p.Id);
                    if (path == null) { unknown = true; continue; }
                    if (!string.Equals(Path.GetFullPath(path), want, StringComparison.OrdinalIgnoreCase)) continue;
                    DateTime? started = null;
                    try { started = p.StartTime.ToUniversalTime(); } catch { }
                    return (true, p.Id, started);
                }
            }
            return (unknown ? null : false, null, null);
        }
        catch { return (null, null, null); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);

    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }
}
