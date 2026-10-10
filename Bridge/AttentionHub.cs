using System.Collections.Concurrent;
using ExileApiMcp.Tools;

namespace ExileApiMcp.Bridge;

/// <summary>
/// resources/updated for the attention queue (Tools/AttentionTools.cs): exile://attention/{game}/queue changes whenever the
/// bridge's attention.state seq moves (a turn handed over, an item queued or ended, a restart waiting, the player entering
/// or leaving combat). One loop per game, alive only while someone listens: it polls attention.state at 1 Hz on the pooled
/// connection and signals on a seq change. No per-client state: listeners are the open listen requests, the cursor is the
/// bridge's seq. resources/read of the URI asks the bridge directly (AttentionTools.Queue), with or without a listener.
/// </summary>
public sealed class AttentionHub(BridgeRegistry bridges) : IResourceHub
{
    public const string Prefix = "exile://attention/";
    private readonly ConcurrentDictionary<long, (HashSet<string> uris, Action<string> signal)> _listeners = new();
    private readonly ConcurrentDictionary<string, Task> _pumps = new();
    private long _nextId;

    public bool Handles(string uri) => uri.StartsWith(Prefix, StringComparison.Ordinal) && GameOf(uri) is "poe1" or "poe2" && uri.EndsWith("/queue", StringComparison.Ordinal);

    private static string? GameOf(string uri) => uri.Length > Prefix.Length ? uri[Prefix.Length..].Split('/')[0] : null;

    public static string QueueUri(string game) => $"{Prefix}{game}/queue";

    public IDisposable Listen(IEnumerable<string> uris, Action<string> signal)
    {
        var set = uris.Where(Handles).ToHashSet(StringComparer.Ordinal);
        var id = Interlocked.Increment(ref _nextId);
        _listeners[id] = (set, signal);
        foreach (var game in set.Select(GameOf).OfType<string>().Distinct())
            _pumps.GetOrAdd(game, g => Task.Run(() => Pump(g)));
        return new Handle(() => _listeners.TryRemove(id, out _));
    }

    private bool Wanted(string game) => _listeners.Values.Any(l => l.uris.Any(u => GameOf(u) == game));

    private async Task Pump(string game)
    {
        long? seen = null;
        try
        {
            while (Wanted(game))
            {
                try
                {
                    var state = await AttentionTools.StateAsync(bridges, game, CancellationToken.None);
                    // The first read sets the cursor; any other seq (higher, or lower after a HUD restart) is a change.
                    if (seen != null && state.Seq != seen)
                    {
                        var uri = QueueUri(game);
                        foreach (var (uris, signal) in _listeners.Values) if (uris.Contains(uri)) signal(uri);
                    }
                    seen = state.Seq;
                    await Task.Delay(1000);
                }
                catch (Exception) { await Task.Delay(2000); }   // HUD restarting, or a bridge without the queue: keep trying while listened to
            }
        }
        finally
        {
            _pumps.TryRemove(game, out _);
            if (Wanted(game)) _ = _pumps.GetOrAdd(game, g => Task.Run(() => Pump(g)));   // a listener arrived as this one stopped
        }
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) dispose(); }
    }
}
