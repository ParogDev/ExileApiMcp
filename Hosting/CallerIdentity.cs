namespace ExileApiMcp.Hosting;

/// <summary>
/// Who is calling, per request, on the shared HTTP server (port 50910). That server belongs to no session: tools\mcp-call.ps1
/// starts it on demand and every agent's shell calls go through it. So the session label can't come from where the server
/// runs; each request names its caller instead, in headers mcp-call sets:
///   X-Hexile-Agent   the caller's label (HEXILE_AGENT, else its cwd's git branch without claude/), URI-escaped
///   X-Hexile-Branch  its git branch (optional), X-Hexile-Cwd its working directory (optional), both URI-escaped
/// Set for the request by <see cref="UseCallerIdentity"/>; SessionIdentity answers with it while it is set, and
/// BridgeRegistry gives each caller its own bridge connections, so hud_sessions, the guide card, leases, toasts and the
/// agent log name the agent that asked. Requests without the header (the control center, other HTTP clients) are the
/// server's own identity. stdio servers never set it: one session per process, as before.
/// </summary>
public sealed record CallerIdentity(string Label, string? Branch, string? Cwd)
{
    public const string AgentHeader = "X-Hexile-Agent", BranchHeader = "X-Hexile-Branch", CwdHeader = "X-Hexile-Cwd";

    /// <summary>The shared server's own machinery calling as a named caller (the F8 route-dispatcher), not an agent: its
    /// session.hello says role infra, so the HUD's agent count leaves it out.</summary>
    public bool Infra { get; init; }

    private static readonly AsyncLocal<CallerIdentity?> Scope = new();

    /// <summary>The caller of the request being handled, or null (no header, a background task, stdio).</summary>
    public static CallerIdentity? Current => Scope.Value;

    // HTTP requests still running per caller session id (SessionIdentity.IdOf): a blocking call (incident_wait, observe_wait)
    // keeps its caller present to the HUD through BridgeRegistry's session.touch, and its connections open.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Open = new(StringComparer.Ordinal);

    /// <summary>How many of this caller's requests are running now.</summary>
    public static int OpenCalls(string sessionId) => Open.TryGetValue(sessionId, out var n) ? n : 0;

    /// <summary>Counts one running request of <paramref name="sessionId"/> until the returned scope is disposed.</summary>
    public static IDisposable Enter(string sessionId)
    {
        Open.AddOrUpdate(sessionId, 1, (_, n) => n + 1);
        return new Leave(sessionId);
    }

    private sealed class Leave(string id) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            // Removed at zero only if nobody entered meanwhile (a value-checked remove).
            if (Open.AddOrUpdate(id, 0, (_, n) => Math.Max(0, n - 1)) == 0)
                ((ICollection<KeyValuePair<string, int>>)Open).Remove(new KeyValuePair<string, int>(id, 0));
        }
    }

    /// <summary>Runs <paramref name="body"/> as <paramref name="caller"/> (tests; the HTTP middleware sets it per request).</summary>
    public static async Task RunAsAsync(CallerIdentity? caller, Func<Task> body)
    {
        var before = Scope.Value;
        Scope.Value = caller;
        try { await body(); }
        finally { Scope.Value = before; }
    }

    /// <summary>The caller named by a request's headers, or null when it names none (or only garbage).</summary>
    public static CallerIdentity? FromHeaders(string? agent, string? branch, string? cwd)
    {
        var label = Clean(agent, 40);
        if (label == null) return null;
        return new CallerIdentity(label, Clean(branch, 200), Clean(cwd, 400));
    }

    private static string? Clean(string? raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s;
        try { s = Uri.UnescapeDataString(raw); } catch (UriFormatException) { s = raw; }
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length == 0) return null;
        return s.Length > max ? s[..max] : s;
    }
}

public static class CallerIdentityMiddleware
{
    /// <summary>After the token check: the request runs as the caller its X-Hexile-Agent header names (CallerIdentity).</summary>
    public static IApplicationBuilder UseCallerIdentity(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var h = ctx.Request.Headers;
        var caller = CallerIdentity.FromHeaders(h[CallerIdentity.AgentHeader], h[CallerIdentity.BranchHeader], h[CallerIdentity.CwdHeader]);
        if (caller == null) { await next(ctx); return; }
        // Only POSTs count as an open call (a tool call, a blocking wait): a GET is a long-lived stream that would keep its
        // caller present forever.
        using (HttpMethods.IsPost(ctx.Request.Method) ? CallerIdentity.Enter(SessionIdentity.IdOf(caller)) : null)
            await CallerIdentity.RunAsAsync(caller, () => next(ctx));
    });
}
