using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Incidents;

/// <summary>
/// The pure half of the incident board: parsing, raise / respond / resolve and pruning on an <see cref="IncidentFile"/>,
/// with the clock passed in. No IO, so it is unit-tested (Tests/) and shared by every writer in this server.
/// </summary>
public static class IncidentLogic
{
    /// <summary>Resolved incidents are dropped after this (tools\incident-lib.ps1 keeps the same 7 days); listings show the last 24 h.</summary>
    public static readonly TimeSpan KeepResolved = TimeSpan.FromDays(7);
    public static readonly TimeSpan ShowResolved = TimeSpan.FromHours(24);

    /// <summary>The file's text as an <see cref="IncidentFile"/>; empty text is an empty board; malformed JSON throws JsonException.</summary>
    public static IncidentFile Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new IncidentFile();
        var f = JsonConvert.DeserializeObject<IncidentFile>(json, IncidentJson.Settings) ?? new IncidentFile();
        f.Incidents ??= [];
        foreach (var i in f.Incidents) i.Responses ??= [];
        f.Incidents.RemoveAll(i => i == null);
        return f;
    }

    public static string NewId(DateTime now) =>
        $"i-{now.ToUniversalTime():yyyyMMddHHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";

    public static Incident? Find(IncidentFile f, string idOrKey, bool openOnly = false)
    {
        var q = idOrKey.Trim();
        // An id is unique; a key picks the open incident first, else the newest resolved one.
        return f.Incidents.FirstOrDefault(i => i.Id == q && (!openOnly || i.IsOpen))
               ?? f.Incidents.Where(i => string.Equals(i.Key, q, StringComparison.OrdinalIgnoreCase) && (!openOnly || i.IsOpen))
                   .OrderByDescending(i => i.IsOpen).ThenByDescending(i => i.RaisedAt).FirstOrDefault();
    }

    public static Incident? OpenByKey(IncidentFile f, string key) =>
        f.Incidents.FirstOrDefault(i => i.IsOpen && string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Raise: an open incident with the same key is updated (title, detail, fix, eta, fields; id, raisedAt and responses
    /// kept), else a new one is added. Returns it and whether it is new.
    /// </summary>
    public static (Incident Incident, bool Created) Raise(IncidentFile f, IncidentDraft d, string who, DateTime now)
    {
        Validate(d);
        var key = d.EffectiveKey;
        var i = OpenByKey(f, key);
        var created = i == null;
        if (i == null)
        {
            i = new Incident { Id = NewId(now), Key = key, RaisedBy = who, RaisedAt = now, State = "open" };
            f.Incidents.Add(i);
        }
        var before = created ? null : IncidentJson.SerializeCompact(i);
        i.Kind = d.Kind; i.Scope = d.Scope; i.Title = d.Title.Trim();
        i.Detail = d.Detail; i.Fix = d.Fix; i.Eta = d.Eta;
        foreach (var (k, v) in d.Fields) i.Set(k, v);
        // Updated by key (tools\incident-lib.ps1 does the same): who touched it last, only when something changed.
        if (!created && before != IncidentJson.SerializeCompact(i))
        {
            i.Set("updatedAt", IsoUtcConverter.Iso(now));
            i.Set("updatedBy", who);
        }
        return (i, created);
    }

    public static void Validate(IncidentDraft d)
    {
        if (!IncidentDraft.Kinds.Contains(d.Kind)) throw new ArgumentException($"kind '{d.Kind}' is not one of {string.Join(" | ", IncidentDraft.Kinds)}");
        if (!IncidentDraft.Scopes.Contains(d.Scope)) throw new ArgumentException($"scope '{d.Scope}' is not one of {string.Join(" | ", IncidentDraft.Scopes)}");
        if (string.IsNullOrWhiteSpace(d.Title)) throw new ArgumentException("title is empty: one line saying what is wrong");
    }

    /// <summary>
    /// One response per session: a session's new response replaces its previous one. With a session id, that is the
    /// response with the same id (plus an older id-less one under the same label, written before ids existed); without
    /// one, the id-less response with the same label. Two sessions sharing a label (two Claude Desktop conversations,
    /// two servers on one branch) keep a response each; <see cref="DisplayNames"/> tells them apart.
    /// </summary>
    public static IncidentResponse Respond(Incident i, string who, string status, string? note, DateTime? until, DateTime now, string? sessionId = null)
    {
        if (status is not ("ok" or "blocked" or "waiting")) throw new ArgumentException($"status '{status}' is not ok | blocked | waiting");
        var id = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        i.Responses.RemoveAll(r => id != null && r.SessionId == id
                                   || r.SessionId == null && string.Equals(r.Who, who, StringComparison.OrdinalIgnoreCase));
        var r = new IncidentResponse { Who = who, SessionId = id, Status = status, Note = note, Until = until, At = now };
        i.Responses.Add(r);
        return r;
    }

    /// <summary>
    /// Display names unique per label, in order: the first session with a label shows it plain, the next different session
    /// with the same label "Label (2)", then "Label (3)". Entries without an id are their label's own session each.
    /// </summary>
    public static List<string> DisplayNames(IReadOnlyList<(string Label, string? Id)> items)
    {
        var seen = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(items.Count);
        foreach (var (label, id) in items)
        {
            if (!seen.TryGetValue(label, out var ids)) seen[label] = ids = [];
            var k = id != null ? ids.IndexOf(id) : -1;
            if (k < 0) { ids.Add(id); k = ids.Count - 1; }
            names.Add(k == 0 ? label : $"{label} ({k + 1})");
        }
        return names;
    }

    /// <summary>The responses' display names (<see cref="DisplayNames"/>), in the same order.</summary>
    public static List<string> ResponseNames(Incident i) => DisplayNames(i.Responses.Select(r => (r.Who, r.SessionId)).ToList());

    /// <summary>Resolve an open incident (false when it was resolved already).</summary>
    public static bool Resolve(Incident i, string who, string? note, DateTime now)
    {
        if (!i.IsOpen) return false;
        i.State = "resolved"; i.ResolvedAt = now; i.ResolvedBy = who;
        if (!string.IsNullOrWhiteSpace(note)) i.Set("resolution", note);
        return true;
    }

    /// <summary>Drop incidents resolved more than 7 days ago. Returns how many went.</summary>
    public static int Prune(IncidentFile f, DateTime now) =>
        f.Incidents.RemoveAll(i => !i.IsOpen && (i.ResolvedAt ?? i.RaisedAt ?? DateTime.MinValue) < now - KeepResolved);

    /// <summary>Open incidents that concern a scope: its own plus the machine-wide ones (scope null: all).</summary>
    public static IEnumerable<Incident> OpenFor(IncidentFile f, string? scope) =>
        f.Incidents.Where(i => i.IsOpen && (scope == null || scope == "auto" || i.Scope == scope || i.Scope == "machine"));

    /// <summary>"Open incidents (poe2): [ports-reserved] Bridge ports ... (by X, 3 min ago; fix: ...; a: blocked)" or "".</summary>
    public static string Summary(IncidentFile f, string? scope, DateTime now)
    {
        var open = OpenFor(f, scope).ToList();
        if (open.Count == 0) return "";
        return $"Open incidents{(scope is "poe1" or "poe2" ? $" ({scope})" : "")}: " + string.Join(" | ", open.Select(i => OneLine(i, now)));
    }

    public static string OneLine(Incident i, DateTime now)
    {
        var parts = new List<string> { $"by {i.RaisedBy}, {Ago(i.RaisedAt, now)}" };
        if (i.Eta is { } eta) parts.Add(eta > now ? $"eta {Math.Max(1, (int)Math.Ceiling((eta - now).TotalMinutes))} min" : "eta passed");
        if (!string.IsNullOrWhiteSpace(i.Fix)) parts.Add("fix: " + Clip(i.Fix!, 160));
        if (i.Responses.Count > 0)
        {
            var names = ResponseNames(i);
            parts.Add(string.Join(", ", i.Responses.Select((r, k) => $"{names[k]}: {r.Status}" + (r.Until is { } u && u > now ? $" until {u:HH:mm}Z" : ""))));
        }
        return $"[{i.Kind}] {i.Title} ({string.Join("; ", parts)}) id={i.Id}";
    }

    public static string Ago(DateTime? t, DateTime now)
    {
        if (t == null) return "?";
        var s = (now - t.Value).TotalSeconds;
        return s < 90 ? $"{Math.Max(0, (int)s)} s ago" : s < 5400 ? $"{(int)(s / 60)} min ago" : $"{s / 3600:0.#} h ago";
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..(max - 3)] + "..." : s;
}

/// <summary>
/// The incident board on disk: %LOCALAPPDATA%\Hexile\incidents.json (HEXILE_INCIDENT_DIR overrides it for tests, the same
/// variable as tools\incident-lib.ps1),
/// machine-wide, readable while the HUD and the bridge are down. Every read-modify-write holds the named mutex
/// "Local\HexileIncidents" (5 s), writes incidents.json.tmp and replaces the file, and bumps rev; a mutation that changes
/// nothing writes nothing (so several servers raising the same incident don't churn rev). Reads take no mutex and
/// retry once on a missing or partial file.
/// </summary>
public static class IncidentStore
{
    public const string MutexName = @"Local\HexileIncidents";
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(5);

    public static string Dir => Environment.GetEnvironmentVariable("HEXILE_INCIDENT_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexile");
    public static string FilePath => Path.Combine(Dir, "incidents.json");
    /// <summary>Deploy heartbeats (incident id → last beat), kept apart so a heartbeat every 30 s doesn't bump rev and wake every waiter.</summary>
    public static string HeartbeatPath => Path.Combine(Dir, "deploy-heartbeats.json");

    /// <summary>Raised (with a ping) after every write this process makes; IncidentWatcher also sees other writers.</summary>
    public static event Action<int>? Written;

    private static (DateTime Stamp, long Len, IncidentFile File)? _cache;
    private static readonly object CacheLock = new();

    /// <summary>The board without the mutex: a missing file is an empty board; a partial one is retried once, then the last good read.</summary>
    public static IncidentFile Read()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (!fi.Exists) return new IncidentFile();
                lock (CacheLock)
                    if (_cache is { } c && c.Stamp == fi.LastWriteTimeUtc && c.Len == fi.Length) return Clone(c.File);
                var f = IncidentLogic.Parse(ReadShared(FilePath));
                lock (CacheLock) _cache = (fi.LastWriteTimeUtc, fi.Length, f);
                return Clone(f);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                if (attempt == 0) { Thread.Sleep(60); continue; }
                lock (CacheLock) if (_cache is { } c) return Clone(c.File);
                Console.Error.WriteLine($"[Incidents] {FilePath} unreadable: {ex.Message}");
                return new IncidentFile();
            }
        }
    }

    private static IncidentFile Clone(IncidentFile f) => IncidentLogic.Parse(IncidentJson.SerializeCompact(f));

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Read-modify-write under the mutex. <paramref name="mutate"/> changes the board in place and returns its result;
    /// resolved incidents older than 7 days are pruned. Written (rev + 1, changed incidents stamped with it) only when
    /// something changed.
    /// </summary>
    public static T Update<T>(Func<IncidentFile, T> mutate)
    {
        Directory.CreateDirectory(Dir);
        using var mutex = new Mutex(false, MutexName);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(MutexTimeout); }
            catch (AbandonedMutexException) { owned = true; }   // a writer died holding it: the file is still whole (tmp + replace)
            if (!owned) throw new IOException($"The incident board is locked by another writer (mutex {MutexName} not free within {MutexTimeout.TotalSeconds:0} s).");

            var text = File.Exists(FilePath) ? ReadShared(FilePath) : "";
            IncidentFile f;
            var mustWrite = text.Length == 0;
            try { f = IncidentLogic.Parse(text); }
            catch (JsonException ex)
            {
                // A corrupt board must not block every session: keep the bad copy for a look and start a new one (written
                // now, whatever the mutation does, so the next writer doesn't copy it again). rev continues from the last
                // good read, so waiters never see it go back.
                var bad = FilePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Copy(FilePath, bad, overwrite: true);
                Console.Error.WriteLine($"[Incidents] {FilePath} was not valid JSON ({ex.Message}); kept as {Path.GetFileName(bad)}, starting a new board");
                f = new IncidentFile();
                lock (CacheLock) if (_cache is { } c) f.Rev = c.File.Rev;
                mustWrite = true;
            }
            // Repair what other writers may leave: an incident without an id gets one (ids address incidents in every tool).
            foreach (var i in f.Incidents.Where(i => string.IsNullOrWhiteSpace(i.Id)))
            {
                i.Id = IncidentLogic.NewId(DateTime.UtcNow);
                mustWrite = true;
            }
            // Compared by reference: ids may repeat in a hand-edited board.
            var before = new Dictionary<Incident, string>(ReferenceEqualityComparer.Instance);
            foreach (var i in f.Incidents) before[i] = IncidentJson.SerializeCompact(i);
            var beforeCount = f.Incidents.Count;
            var result = mutate(f);
            IncidentLogic.Prune(f, DateTime.UtcNow);

            var changed = f.Incidents.Where(i => !before.TryGetValue(i, out var b) || b != IncidentJson.SerializeCompact(i)).ToList();
            if (changed.Count == 0 && f.Incidents.Count == beforeCount && !mustWrite) return result;

            f.Rev++;
            foreach (var i in changed) i.Set("rev", f.Rev);
            WriteAtomic(FilePath, IncidentJson.Serialize(f));
            lock (CacheLock) _cache = null;
            Written?.Invoke(f.Rev);
            return result;
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    /// <summary>tmp + replace; a reader holding the file open (no FILE_SHARE_DELETE) makes Replace fail briefly, so retry.</summary>
    internal static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
                else File.Move(tmp, path);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20) { Thread.Sleep(50); }
        }
    }

    // ── The operations the tools and the automatic checks use ──

    public static (Incident Incident, bool Created) Raise(IncidentDraft d, string? who = null) =>
        Update(f => IncidentLogic.Raise(f, d, who ?? Hosting.SessionIdentity.Label, DateTime.UtcNow));

    /// <param name="sessionId">The answering session (default: this server's <see cref="Hosting.SessionIdentity.ResponseId"/>).</param>
    public static Incident Respond(string idOrKey, string status, string? note, DateTime? until, string? who = null, string? sessionId = null) =>
        Update(f =>
        {
            var i = IncidentLogic.Find(f, idOrKey) ?? throw new ArgumentException(NotFound(f, idOrKey));
            IncidentLogic.Respond(i, who ?? Hosting.SessionIdentity.Label, status, note, until, DateTime.UtcNow, sessionId ?? Hosting.SessionIdentity.ResponseId);
            return i;
        });

    /// <summary>Resolve by id or key; null when there is none open (already resolved incidents come back unchanged).</summary>
    public static Incident? Resolve(string idOrKey, string? note, string? who = null, bool throwIfMissing = false) =>
        Update<Incident?>(f =>
        {
            var i = IncidentLogic.Find(f, idOrKey);
            if (i == null) return throwIfMissing ? throw new ArgumentException(NotFound(f, idOrKey)) : null;
            IncidentLogic.Resolve(i, who ?? Hosting.SessionIdentity.Label, note, DateTime.UtcNow);
            return i;
        });

    /// <summary>Resolve every open incident whose key is one of these (cheap no-op when none is open: no mutex).</summary>
    public static void ResolveKeys(IEnumerable<string> keys, string note)
    {
        var wanted = keys.ToList();
        if (!Read().Incidents.Any(i => i.IsOpen && wanted.Contains(i.Key, StringComparer.OrdinalIgnoreCase))) return;
        Update(f =>
        {
            foreach (var i in f.Incidents.Where(i => i.IsOpen && wanted.Contains(i.Key, StringComparer.OrdinalIgnoreCase)).ToList())
                IncidentLogic.Resolve(i, Hosting.SessionIdentity.Label, note, DateTime.UtcNow);
            return 0;
        });
    }

    public static string Summary(string? scope) => IncidentLogic.Summary(Read(), scope, DateTime.UtcNow);

    private static string NotFound(IncidentFile f, string idOrKey) =>
        $"No incident with id or key '{idOrKey}' on the board (rev {f.Rev}, {f.Incidents.Count(i => i.IsOpen)} open: " +
        $"{string.Join(", ", f.Incidents.Where(i => i.IsOpen).Select(i => $"{i.Id} {i.Key}").DefaultIfEmpty("none"))}).";

    // ── Deploy heartbeats (sidecar) ──

    public static Dictionary<string, DateTime> ReadHeartbeats()
    {
        try
        {
            if (!File.Exists(HeartbeatPath)) return [];
            // DateParseHandling.None: the values stay the ISO strings we wrote (JObject.Parse would make DateTimes whose
            // ToString() is culture-dependent).
            var o = JsonConvert.DeserializeObject<JObject>(ReadShared(HeartbeatPath), IncidentJson.Settings) ?? new JObject();
            var d = new Dictionary<string, DateTime>();
            foreach (var p in o.Properties())
                if (DeployIncidents.ParseIso(p.Value) is { } t) d[p.Name] = t;
            return d;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Beat for these incident ids (and drop beats of incidents no longer open), under the board's mutex.</summary>
    public static void Beat(IEnumerable<string> ids, IEnumerable<string> openIds)
    {
        Directory.CreateDirectory(Dir);
        using var mutex = new Mutex(false, MutexName);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(MutexTimeout); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) return;
            var beats = ReadHeartbeats();
            var open = openIds.ToHashSet();
            foreach (var k in beats.Keys.Where(k => !open.Contains(k)).ToList()) beats.Remove(k);
            foreach (var id in ids) beats[id] = DateTime.UtcNow;
            var o = new JObject();
            foreach (var (k, v) in beats) o[k] = IsoUtcConverter.Iso(v);
            WriteAtomic(HeartbeatPath, o.ToString(Formatting.Indented));
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }
}
