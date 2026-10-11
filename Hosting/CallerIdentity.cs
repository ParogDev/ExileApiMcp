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

    private static readonly AsyncLocal<CallerIdentity?> Scope = new();

    /// <summary>The caller of the request being handled, or null (no header, a background task, stdio).</summary>
    public static CallerIdentity? Current => Scope.Value;

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
        await CallerIdentity.RunAsAsync(caller, () => next(ctx));
    });
}
