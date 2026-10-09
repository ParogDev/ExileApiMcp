using System.Collections.Concurrent;
using ExileApiMcp.Tools;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Bridge;

/// <summary>
/// The latest HUD health report per game, refreshed on a cadence only while someone watches. Two ways to watch:
///   - subscriptions/listen on exile://perf/{game}/report (true push: resources/updated per new report);
///   - perf_watch {since}: a call that returns as soon as a report newer than since exists. MCP Apps can't subscribe
///     (ext-apps 2.0 has no subscribe), so the performance panel loops on it instead of a timer.
/// Each report runs a 3 s pipeline trace (the bridge patches the HUD for those 3 s), so the cadence is a duty cycle:
/// 15 s by default. The loop stops 30 s after the last watcher leaves. State is the report and its sequence number,
/// nothing per client.
/// </summary>
public sealed class PerfHub(BridgeRegistry bridges) : IResourceHub
{
    public const string Prefix = "exile://perf/";
    private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(30);

    private sealed class State
    {
        public long Seq;
        public DateTimeOffset? At;
        public JObject? Report;
        public string? Text;
        public double IntervalSec = 15;
        public DateTime WantedUntil;
        public Task? Loop;
        public TaskCompletionSource Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly object Lock = new();
    }

    private readonly ConcurrentDictionary<string, State> _games = new();
    private readonly ConcurrentDictionary<long, (HashSet<string> uris, Action<string> signal)> _listeners = new();
    private long _nextId;

    public bool Handles(string uri) => uri.StartsWith(Prefix, StringComparison.Ordinal) && GameOf(uri) is "poe1" or "poe2";
    private static string? GameOf(string uri) => uri.Length > Prefix.Length ? uri[Prefix.Length..].Split('/')[0] : null;
    public static string ReportUri(string game) => $"{Prefix}{game}/report";

    public IDisposable Listen(IEnumerable<string> uris, Action<string> signal)
    {
        var set = uris.Where(Handles).ToHashSet(StringComparer.Ordinal);
        var id = Interlocked.Increment(ref _nextId);
        _listeners[id] = (set, signal);
        foreach (var g in set.Select(GameOf).OfType<string>().Distinct()) Want(g, null);
        return new Handle(() => _listeners.TryRemove(id, out _));
    }

    /// <summary>The latest report once it is newer than since (or at timeout); starts the cadence if it isn't running.</summary>
    public async Task<PerfSnapshot> WaitAsync(string game, long since, TimeSpan timeout, double? intervalSec, CancellationToken ct)
    {
        var s = Want(game, intervalSec);
        Task next;
        lock (s.Lock) { if (s.Seq > since && s.Report != null) return Snapshot(game, s, fresh: true); next = s.Next.Task; }
        var done = await Task.WhenAny(next, Task.Delay(timeout, ct));
        ct.ThrowIfCancellationRequested();
        Want(game, null);   // still watching: keep the cadence alive past this wait
        lock (s.Lock) return Snapshot(game, s, fresh: done == next && s.Seq > since);
    }

    /// <summary>The latest report, running one now if there is none yet.</summary>
    public Task<PerfSnapshot> LatestAsync(string game, CancellationToken ct) => WaitAsync(game, 0, TimeSpan.FromSeconds(20), null, ct);

    private State Want(string game, double? intervalSec)
    {
        var s = _games.GetOrAdd(game, _ => new State());
        lock (s.Lock)
        {
            if (intervalSec is { } iv) s.IntervalSec = Math.Clamp(iv, 5, 300);
            s.WantedUntil = DateTime.UtcNow + IdleStop;
            if (s.Loop == null || s.Loop.IsCompleted) s.Loop = Task.Run(() => Run(game, s));
        }
        return s;
    }

    private bool Watched(string game, State s) =>
        DateTime.UtcNow < s.WantedUntil || _listeners.Values.Any(l => l.uris.Any(u => GameOf(u) == game));

    private async Task Run(string game, State s)
    {
        while (Watched(game, s))
        {
            try
            {
                var (report, text) = await HealthReportTools.Build(bridges, game, series: true, CancellationToken.None);
                TaskCompletionSource fire;
                lock (s.Lock)
                {
                    s.Report = report; s.Text = text; s.At = DateTimeOffset.UtcNow; s.Seq++;
                    fire = s.Next; s.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                fire.TrySetResult();
                var uri = ReportUri(game);
                foreach (var (uris, signal) in _listeners.Values) if (uris.Contains(uri)) signal(uri);
            }
            catch (Exception) { /* the bridge is down or restarting: try again next round */ }
            double wait;
            lock (s.Lock) wait = s.IntervalSec;
            var until = DateTime.UtcNow.AddSeconds(wait);
            while (DateTime.UtcNow < until && Watched(game, s)) await Task.Delay(500);
        }
    }

    private static PerfSnapshot Snapshot(string game, State s, bool fresh) => new()
    {
        Game = game, Seq = s.Seq, At = s.At, Fresh = fresh, IntervalSec = s.IntervalSec, Text = s.Text,
        Report = s.Report == null ? null : Dto.From<HealthReport>(s.Report),
    };

    private sealed class Handle(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) dispose(); }
    }
}
