using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ExileApiMcp.Hosting;

/// <summary>
/// Hardening for the localhost HTTP transport (the SDK itself does no Host/Origin/auth checks):
///   1. Kestrel binds 127.0.0.1 only (see Program).
///   2. Host header allowlist (localhost / 127.0.0.1 / [::1]) - blocks DNS-rebinding. -> 421
///   3. Origin, when present, must be a loopback origin (spec: servers MUST validate Origin). -> 403
///   4. Bearer token on every request (except GET /app, the control center page, which holds none), constant-time compare. -> 401
///      Token: MCP_HTTP_TOKEN env var, else %LOCALAPPDATA%\ExileApiMcp\http-token.txt (created with
///      a random value on first run; only your account can read your LocalAppData). Loopback is
///      reachable by every local account, so this token - not the bind address - is the boundary.
///   5. Global rate limit (configured in Program).
/// No CORS policy: browsers are not clients of this server.
/// </summary>
internal static class LocalHttpSecurity
{
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]", "::1"];

    public static string TokenFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExileApiMcp", "http-token.txt");

    public static string LoadOrCreateToken()
    {
        if (Environment.GetEnvironmentVariable("MCP_HTTP_TOKEN") is { Length: >= 16 } fromEnv)
            return fromEnv.Trim();

        var path = TokenFilePath;
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 16) return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(path, token);
        return token;
    }

    public static IApplicationBuilder UseLocalHttpSecurity(this IApplicationBuilder app, string token)
    {
        var expected = Encoding.UTF8.GetBytes("Bearer " + token);

        return app.Use(async (ctx, next) =>
        {
            var host = ctx.Request.Host.Host;
            if (!LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }

            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !IsLoopbackOrigin(origin))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // The control center page carries no secret (its token comes in the URL fragment): served without one.
            if (ControlCenterRoutes.IsPublicPage(ctx.Request)) { await next(); return; }

            var auth = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(auth, expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"ExileApiMcp\"";
                return;
            }

            await next();
        });
    }

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)));
}
