using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Bridge;

/// <summary>
/// Turns the bridge observer's journal into resource-updated signals for subscriptions/listen (Hosting/Subscriptions.cs).
/// One loop per game, alive only while someone listens to one of its URIs: it asks the bridge for events after the last
/// seen sequence every 250 ms (one call on the pooled connection) and signals, per batch, the URIs the batch touches:
///   exile://observe/{game}/events               any event
///   exile://observe/{game}/layers               any layer event (layer statuses changed)
///   exile://observe/{game}/layers/{layer}       an event of that layer (its map changed)
/// No per-client state: listeners are the open listen requests; the cursor is the bridge's own sequence number.
/// </summary>
public sealed class ObserveHub(BridgeRegistry bridges) : IResourceHub
{
    public const string Prefix = "exile://observe/";
    private readonly ConcurrentDictionary<long, (HashSet<string> uris, Action<string> signal)> _listeners = new();
    private readonly ConcurrentDictionary<string, Task> _pumps = new();
    private long _nextId;

    public bool Handles(string uri) => IsObserveUri(uri);

    public static bool IsObserveUri(string uri) => uri.StartsWith(Prefix, StringComparison.Ordinal) && GameOf(uri) is "poe1" or "poe2";

    private static string? GameOf(string uri) => uri.Length > Prefix.Length ? uri[Prefix.Length..].Split('/')[0] : null;

    /// <summary>Signal calls for each listened URI that changed, until the returned handle is disposed.</summary>
    public IDisposable Listen(IEnumerable<string> uris, Action<string> signal)
    {
        var set = uris.Where(IsObserveUri).ToHashSet(StringComparer.Ordinal);
        var id = Interlocked.Increment(ref _nextId);
        _listeners[id] = (set, signal);
        foreach (var game in set.Select(GameOf).OfType<string>().Distinct())
            _pumps.GetOrAdd(game, g => Task.Run(() => Pump(g)));
        return new Handle(() => _listeners.TryRemove(id, out _));
    }

    private bool Wanted(string game) => _listeners.Values.Any(l => l.uris.Any(u => GameOf(u) == game));

    private async Task Pump(string game)
    {
        long? since = null;
        try
        {
            while (Wanted(game))
            {
                try
                {
                    var (_, r) = await bridges.CallAsync(game, "observe.events", new JObject { ["since"] = since ?? long.MaxValue, ["limit"] = 500 }, CancellationToken.None);
                    var seq = r["seq"]?.Value<long>() ?? 0;
                    if (since == null || seq < since) { since = seq; }   // first call, or the bridge restarted with a lower sequence
                    else if (r["events"] is JArray { Count: > 0 } events)
                    {
                        since = events.Last!["seq"]?.Value<long>() ?? seq;
                        var touched = new HashSet<string>(StringComparer.Ordinal) { $"{Prefix}{game}/events" };
                        foreach (var e in events)
                        {
                            var kind = e["kind"]?.ToString();
                            var layer = e["layer"]?.ToString() ?? (kind is "server" or "server.noisy" ? "server" : null);
                            if (layer == null) continue;
                            touched.Add($"{Prefix}{game}/layers");
                            touched.Add($"{Prefix}{game}/layers/{layer}");
                        }
                        foreach (var (uris, signal) in _listeners.Values)
                            foreach (var u in touched)
                                if (uris.Contains(u)) signal(u);
                    }
                    await Task.Delay(250);
                }
                catch (Exception) { await Task.Delay(2000); }   // HUD restarting or bridge not up: keep trying while listened to
            }
        }
        finally
        {
            _pumps.TryRemove(game, out _);
            if (Wanted(game)) _pumps.GetOrAdd(game, g => Task.Run(() => Pump(g)));   // a listener arrived as this one stopped
        }
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) dispose(); }
    }
}
