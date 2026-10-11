using ExileApiMcp.Bridge;
using Microsoft.Extensions.Hosting;

namespace ExileApiMcp.Incidents;

/// <summary>
/// Watches incidents.json (any writer: this server, other sessions' servers, tools\incident.ps1, the hook) and signals a
/// rev change at once: incident_wait and the exile://incidents subscription push on it instead of polling. A 2 s re-read
/// backs the file watcher up (watchers miss events on some file systems and after a directory is recreated).
/// </summary>
public sealed class IncidentWatcher : IDisposable
{
    private FileSystemWatcher? _fsw;
    private int _lastRev = -1;
    private readonly object _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Timer _poll;

    /// <summary>Fires with the new rev whenever the board's rev changes.</summary>
    public event Action<int>? Changed;

    public IncidentWatcher()
    {
        IncidentStore.Written += _ => Check();
        _poll = new Timer(_ => { EnsureWatcher(); Check(); }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        EnsureWatcher();
    }

    private void EnsureWatcher()
    {
        if (_fsw != null) return;
        try
        {
            Directory.CreateDirectory(IncidentStore.Dir);
            var w = new FileSystemWatcher(IncidentStore.Dir, "incidents.json*")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, IncludeSubdirectories = false,
            };
            w.Changed += (_, _) => Check();
            w.Created += (_, _) => Check();
            w.Renamed += (_, _) => Check();
            w.Error += (_, _) => { lock (_gate) { _fsw?.Dispose(); _fsw = null; } };
            w.EnableRaisingEvents = true;
            _fsw = w;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Incidents] no file watcher on {IncidentStore.Dir} ({ex.Message}); polling every 2 s"); }
    }

    public int Rev => IncidentStore.Read().Rev;

    private void Check()
    {
        int rev;
        try { rev = IncidentStore.Read().Rev; } catch { return; }
        TaskCompletionSource? fire = null;
        lock (_gate)
        {
            if (rev == _lastRev) return;
            var first = _lastRev < 0;
            _lastRev = rev;
            if (first) return;
            fire = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        fire.TrySetResult();
        try { Changed?.Invoke(rev); } catch { }
    }

    /// <summary>Wait until the board's rev differs from <paramref name="since"/> (returns at once if it already does). True on a change.</summary>
    public async Task<bool> WaitAsync(int since, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task signal;
            lock (_gate) signal = _changed.Task;
            if (IncidentStore.Read().Rev != since) return true;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return false;
            await Task.WhenAny(signal, Task.Delay(left < TimeSpan.FromSeconds(2) ? left : TimeSpan.FromSeconds(2), ct));
            ct.ThrowIfCancellationRequested();
        }
    }

    public void Dispose()
    {
        _poll.Dispose();
        _fsw?.Dispose();
    }
}

/// <summary>exile://incidents for subscriptions/listen: signalled on every rev change.</summary>
public sealed class IncidentHub(IncidentWatcher watcher) : IResourceHub
{
    public const string Uri = "exile://incidents";

    public bool Handles(string uri) => uri == Uri;

    public IDisposable Listen(IEnumerable<string> uris, Action<string> signal)
    {
        if (!uris.Contains(Uri)) return new Noop();
        Action<int> h = _ => signal(Uri);
        watcher.Changed += h;
        return new Unsub(() => watcher.Changed -= h);
    }

    private sealed class Noop : IDisposable { public void Dispose() { } }
    private sealed class Unsub(Action a) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) a(); }
    }
}

/// <summary>
/// The background half of the board, in every server: every 10 s it raises hud-restart from a restart-state.json that
/// says "stopping", checks whether the bridges behind open bridge-down / hud-restart incidents answer again (resolve)
/// or now fail for another reason (re-diagnose), lifts ports-reserved once netsh no longer lists the bridge ports, and
/// lets a "HUD not running" nobody hit for 30 min go. Every 30 s it beats for the deploys this session owns; every 5 min
/// it scans the main checkout for orphaned deploys. Several servers doing this at once is fine: raises dedupe by key
/// under the board's mutex and a no-op writes nothing.
/// </summary>
public sealed class IncidentMonitor(BridgeRegistry bridges, IncidentWatcher watcher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        _ = watcher;   // created with the host, so the file watcher runs from the start
        var tick = 0;
        try { await Task.Delay(TimeSpan.FromSeconds(3), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                foreach (var b in bridges.Bridges) IncidentAuto.CheckRestartState(b);
                await CheckBridges(stop);
                CheckPorts();
                if (tick % 3 == 0) Beat();
                if (tick % 30 == 0) await Task.Run(() => DeployIncidents.Scan(force: true), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[Incidents] monitor: {ex.Message}"); }
            tick++;
            try { await Task.Delay(TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckBridges(CancellationToken stop)
    {
        var board = IncidentStore.Read();
        foreach (var b in bridges.Bridges.Where(b => b.Game is "poe1" or "poe2"))
        {
            var open = board.Incidents.Where(i => i.IsOpen && IncidentAuto.BridgeKeys(b.Game).Contains(i.Key)).ToList();
            if (open.Count == 0) continue;
            string? error = null;
            try
            {
                if (!b.LooksAvailable) error = "no bridge token";
                else
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    budget.CancelAfter(TimeSpan.FromSeconds(8));
                    await b.EnsureConnectedAsync(budget.Token);
                    await b.SendRequestAsync("ping", null, budget.Token);
                }
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { error = "no answer within 8 s"; }
            catch (BridgeException ex) { error = ex.Message; }
            if (error == null) { IncidentAuto.BridgeOk(b, force: true); continue; }

            // Still down: a restart in progress keeps its incident; otherwise re-diagnose (the restart may have failed, the
            // HUD may have started without its bridge...). Unchanged verdicts write nothing.
            var restarting = open.Any(i => i.Kind == "hud-restart") && File.Exists(Path.Combine(b.BridgeDir, "restart-state.json"))
                             && DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(b.BridgeDir, "restart-state.json")) < TimeSpan.FromMinutes(3);
            if (restarting) continue;
            var facts = BridgeDiagnosis.Gather(b, error);
            var v = BridgeDiagnosis.Diagnose(facts);
            if (v == null) continue;
            var cur = open.FirstOrDefault(i => i.Key == v.Key);
            if (v.Cause == "hud-not-running" && cur?.Get("cause")?.ToString() == "hud-not-running"
                && DeployIncidents.ParseIso(cur.Get("lastSeenAt")) is { } seen && DateTime.UtcNow - seen > TimeSpan.FromMinutes(30))
            {
                IncidentStore.Resolve(cur.Id, "no session needed this HUD for 30 min; raised again on the next call that does");
                continue;
            }
            if (cur == null || cur.Get("cause")?.ToString() != v.Cause) IncidentAuto.RaiseVerdict(v, b.Game);
        }
    }

    private static void CheckPorts()
    {
        var open = IncidentStore.Read().Incidents.FirstOrDefault(i => i.IsOpen && i.Kind == "ports-reserved");
        if (open == null) return;
        var (ranges, _) = BridgeDiagnosis.ExcludedRanges();
        if (ranges == null) return;
        if (BridgeDiagnosis.BlockedBridgePorts(ranges).Count == 0)
            IncidentStore.Resolve(open.Id, $"Windows no longer reserves the bridge ports {BridgeDiagnosis.BridgeFirstPort}-{BridgeDiagnosis.BridgeFirstPort + BridgeDiagnosis.BridgePortCount - 1} (netsh)");
    }

    private static void Beat()
    {
        var board = IncidentStore.Read();
        var mine = DeployIncidents.Mine(board);
        if (mine.Count == 0) return;
        IncidentStore.Beat(mine, board.Incidents.Where(i => i.IsOpen).Select(i => i.Id));
    }
}
