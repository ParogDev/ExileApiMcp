using System.Text.Json;
using ExileApiMcp.Bridge;
using ExileApiMcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;

namespace ExileApiMcp.Hosting;

/// <summary>
/// The Hexile control center outside Claude: the same single-file page as the ui://exile/control-center MCP App,
/// served at http://127.0.0.1:&lt;port&gt;/app. tools\control-center.ps1 opens /app#t=&lt;token&gt;: the token travels in
/// the fragment (never sent to a server, never logged), the page keeps it in sessionStorage and speaks MCP to /mcp with
/// it, including subscriptions/listen for push.
///   GET  /app                  the page; no token needed (it holds none), strict headers (no framing, no caching).
///   POST /app/api/settings     {game?, plugin, path, value}: like hud_settings_set, but may change permission settings
///                              (the page asks the user to confirm first). Bearer token required, like /mcp. MCP tools
///                              never pass allowPermission, so an agent can't grant itself power through the server.
/// </summary>
public static class ControlCenterRoutes
{
    public const string PagePath = "/app";

    public static void MapControlCenter(this WebApplication app)
    {
        app.MapGet(PagePath, (HttpContext ctx) =>
        {
            var h = ctx.Response.Headers;
            h.CacheControl = "no-store";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Content-Type-Options"] = "nosniff";
            // Connections only back to this server (MCP over HTTP); fonts per the brand.
            h.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
                                      "font-src 'self' https://fonts.gstatic.com data:; img-src 'self' data: blob:; connect-src 'self'; frame-ancestors 'none'";
            return Results.Content(Apps.PlayerStatsApp.LoadEmbedded("ui/control-center.html") ?? Placeholder, "text/html; charset=utf-8");
        });

        app.MapPost($"{PagePath}/api/settings", async (HttpContext ctx, BridgeRegistry bridges) =>
        {
            JObject body;
            try { body = JObject.Parse(await new StreamReader(ctx.Request.Body).ReadToEndAsync()); }
            catch (Exception) { return Results.BadRequest(new { error = "bad_request", message = "Send JSON: {game?, plugin, path, value}." }); }
            var game = body["game"]?.ToString();
            var args = new JObject { ["plugin"] = body["plugin"], ["path"] = body["path"], ["value"] = body["value"], ["allowPermission"] = true };
            try
            {
                var (_, r) = await bridges.CallAsync(game, "settings.set", args, ctx.RequestAborted);
                if (r["error"] != null) return Results.Json(new { error = r["error"]?.ToString(), message = r["message"]?.ToString() }, statusCode: 409);
                return Results.Content(JsonSerializer.Serialize(Dto.From<SettingChangeResult>(r), Dto.Options), "application/json");
            }
            catch (ModelContextProtocol.McpException ex) { return Results.Json(new { error = "bridge_unreachable", message = ex.Message }, statusCode: 502); }
        });
    }

    /// <summary>The page itself carries no secret: let GET /app through without the bearer token (it is in the fragment).</summary>
    public static bool IsPublicPage(HttpRequest r) => HttpMethods.IsGet(r.Method) && (r.Path == PagePath || r.Path == PagePath + "/");

    private const string Placeholder = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Hexile control center</title></head>
        <body style="font-family:system-ui;padding:16px">
          <h3>Hexile control center</h3>
          <p>The control center UI hasn't been built into this server yet (ui-src: npm run build).</p>
        </body></html>
        """;
}
