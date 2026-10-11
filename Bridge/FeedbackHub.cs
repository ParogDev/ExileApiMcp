using System.Security.Cryptography;
using System.Text;
using ExileApiMcp.Dispatch;

namespace ExileApiMcp.Bridge;

/// <summary>
/// exile://feedback/sessions for subscriptions/listen: signalled when the F8 dispatcher's files change in a way the
/// view shows (handlers\dispatcher.json without its every-second updatedAt and cursor; every handlers\&lt;seq&gt;\state.json;
/// the size of output.json and stderr.log). A FileSystemWatcher on the handlers folder triggers a re-check (debounced
/// 300 ms), a 3 s timer backs it up (watchers miss events on some file systems, and the folder may not exist yet). No
/// per-client state: listeners are the open listen requests. The dispatcher may run in another process (the shared
/// server) than the one serving this page, so files, not in-process events, are the source.
/// </summary>
public sealed class FeedbackHub : IResourceHub, IDisposable
{
    public const string Uri = "exile://feedback/sessions";
    private readonly object _gate = new();
    private readonly Dictionary<long, Action<string>> _listeners = new();
    private long _nextId;
    private FileSystemWatcher? _fsw;
    private Timer? _poll, _debounce;
    private string? _fingerprint;

    public bool Handles(string uri) => uri == Uri;

    public IDisposable Listen(IEnumerable<string> uris, Action<string> signal)
    {
        if (!uris.Contains(Uri)) return new Handle(() => { });
        long id;
        lock (_gate)
        {
            id = ++_nextId;
            _listeners[id] = signal;
            if (_listeners.Count == 1) Start();
        }
        return new Handle(() =>
        {
            lock (_gate)
            {
                _listeners.Remove(id);
                if (_listeners.Count == 0) Stop();
            }
        });
    }

    private void Start()
    {
        _fingerprint = Fingerprint();
        _poll = new Timer(_ => { EnsureWatcher(); Check(); }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        EnsureWatcher();
    }

    private void Stop()
    {
        _poll?.Dispose(); _poll = null;
        _debounce?.Dispose(); _debounce = null;
        _fsw?.Dispose(); _fsw = null;
    }

    private void EnsureWatcher()
    {
        lock (_gate)
        {
            if (_fsw != null || _listeners.Count == 0) return;
            try
            {
                var dir = FeedbackDispatcher.HandlersDir;
                if (!Directory.Exists(dir)) return;   // the timer tries again once the dispatcher creates it
                var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.DirectoryName };
                w.Changed += (_, _) => Bump();
                w.Created += (_, _) => Bump();
                w.Renamed += (_, _) => Bump();
                w.Deleted += (_, _) => Bump();
                w.Error += (_, _) => { lock (_gate) { _fsw?.Dispose(); _fsw = null; } };
                w.EnableRaisingEvents = true;
                _fsw = w;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[Feedback] no file watcher on the handlers folder ({ex.Message}); polling every 3 s"); }
        }
    }

    /// <summary>A burst of writes (state.json, output.json, the dispatcher's file) becomes one check.</summary>
    private void Bump()
    {
        lock (_gate)
        {
            if (_listeners.Count == 0) return;
            _debounce?.Dispose();
            _debounce = new Timer(_ => Check(), null, 300, Timeout.Infinite);
        }
    }

    private void Check()
    {
        string fp;
        try { fp = Fingerprint(); } catch { return; }
        Action<string>[] fire;
        lock (_gate)
        {
            if (fp == _fingerprint) return;
            _fingerprint = fp;
            fire = _listeners.Values.ToArray();
        }
        foreach (var f in fire) { try { f(Uri); } catch { } }
    }

    /// <summary>What the view depends on, hashed: the dispatcher's file without its once-a-second fields, every state.json,
    /// and the sizes of output.json and stderr.log per handler.</summary>
    public static string Fingerprint() => Fingerprint(FeedbackDispatcher.HandlersDir, FeedbackDispatcher.StatusFile);

    public static string Fingerprint(string handlersDir, string statusFile)
    {
        var sb = new StringBuilder();
        try
        {
            if (File.Exists(statusFile))
            {
                var s = System.Text.Json.JsonSerializer.Deserialize<Tools.FeedbackDispatcherState>(File.ReadAllText(statusFile), Tools.Dto.Options);
                if (s != null)
                {
                    s.UpdatedAt = default; s.Cursor = 0;
                    sb.Append(System.Text.Json.JsonSerializer.Serialize(s, Tools.Dto.Options));
                }
            }
        }
        catch (Exception) { sb.Append("status:unreadable"); }
        if (Directory.Exists(handlersDir))
        {
            foreach (var d in Directory.EnumerateDirectories(handlersDir).OrderBy(x => x, StringComparer.Ordinal))
            {
                sb.Append('|').Append(Path.GetFileName(d));
                try { var st = Path.Combine(d, "state.json"); if (File.Exists(st)) sb.Append(':').Append(File.ReadAllText(st)); } catch (IOException) { sb.Append(":busy"); }
                foreach (var f in new[] { "output.json", "stderr.log", "brief.md" })
                {
                    try { var p = Path.Combine(d, f); if (File.Exists(p)) sb.Append(',').Append(new FileInfo(p).Length); } catch (IOException) { }
                }
            }
        }
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public void Dispose() { lock (_gate) { Stop(); _listeners.Clear(); } }

    private sealed class Handle(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) dispose(); }
    }
}
