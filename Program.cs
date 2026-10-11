using System.Net;
using System.Threading.RateLimiting;
using ExileApiMcp.Hosting;
using ModelContextProtocol.AspNetCore;

// ExileApi MCP server - one executable, two transports:
//   (default) stdio : launched by the client (Claude Code .mcp.json, Claude Desktop config).
//                     Claude Desktop renders the MCP App UI only for local servers over stdio.
//   --http          : stateless Streamable HTTP (MCP 2026-07-28) on 127.0.0.1:<port>/mcp, with
//                     Host/Origin checks and a bearer token (see Hosting/LocalHttpSecurity.cs).
//                     Options: --port N (default 50910, or MCP_HTTP_PORT).

if (args.Contains("--http"))
    await RunHttpAsync(args);
else
    await RunStdioAsync(args);

static async Task RunStdioAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);
    // stdout carries the protocol: every log line goes to stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    builder.Services.AddExileApiMcp().WithStdioServerTransport();
    var host = builder.Build();
    // Exit when the launcher exits, even if stdin never reaches end-of-file (Hosting/ParentWatch.cs).
    ParentWatch.Start(() => host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication());
    AnnounceToBridges(host.Services);
    await host.RunAsync();
}

// Say hello to the running HUDs at start instead of at the first game call: the HUD then counts this session (and, after
// a swap to a newly deployed build, sees which build it runs) even before it asks anything. Quiet when no HUD runs.
static void AnnounceToBridges(IServiceProvider services) => _ = Task.Run(async () =>
{
    var registry = services.GetRequiredService<ExileApiMcp.Bridge.BridgeRegistry>();
    foreach (var b in registry.Bridges.Where(b => b.LooksAvailable))
        try { await b.EnsureConnectedAsync(); }
        catch (Exception ex) { Console.Error.WriteLine($"[Bridge:{b.Game}] not reachable at start ({ex.Message}); connecting on the first call"); }
});

// Supervised (Supervisor/HttpSupervisor.cs): the supervisor owns the public port and writes "drain" (or closes our stdin)
// when a newer build takes over. Stop accepting, let running requests finish (a blocking observe_wait may take an hour),
// then exit.
static void DrainOnSupervisorSignal(WebApplication app) => _ = Task.Run(async () =>
{
    string? line;
    while ((line = await Console.In.ReadLineAsync()) != null && line.Trim() != "drain") { }
    Console.Error.WriteLine("[ExileApiMcp] the supervisor retired this worker (a newer build took over, or it stops): finishing running requests, then exiting");
    app.Lifetime.StopApplication();
});

static async Task RunHttpAsync(string[] args)
{
    var port = PortFromArgs(args)
               ?? (int.TryParse(Environment.GetEnvironmentVariable("MCP_HTTP_PORT"), out var envPort) ? envPort : 50910);

    SessionIdentity.Kind = "mcp-http";   // shared by every client of this instance; requests name their agent (Hosting/CallerIdentity.cs)
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port)); // never 0.0.0.0
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
            RateLimitPartition.GetFixedWindowLimiter("all", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600, // ~10/s: app polling (1/s) + agent calls with plenty of headroom
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    });

    builder.Services.AddExileApiMcp().WithHttpTransport(o =>
    {
        // Stateless for 2026-07-28 clients; sessions only for older clients that still send
        // initialize (dual-era), so current Claude clients work either way.
        o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
    });

    var supervised = ServerBuild.Mode != "unsupervised";
    // Draining after a swap waits for running requests: blocking tools run up to an hour.
    if (supervised) builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMinutes(65));

    // Under the supervisor, clients connect to its public port; this one is private.
    var publicPort = int.TryParse(Environment.GetEnvironmentVariable("HEXILE_PUBLIC_PORT"), out var pp) ? pp : port;
    // The F8 feedback dispatcher lives on the shared server only (one per machine; Dispatch/FeedbackDispatcher.cs).
    var dispatching = ExileApiMcp.Dispatch.FeedbackDispatcher.Enabled(publicPort);
    if (dispatching) builder.Services.AddHostedService<ExileApiMcp.Dispatch.FeedbackDispatcher>();

    var app = builder.Build();
    var token = LocalHttpSecurity.LoadOrCreateToken();
    app.UseLocalHttpSecurity(token);
    app.UseCallerIdentity();   // after the token check: X-Hexile-Agent names the agent a request is for (tools\mcp-call.ps1 sends it)
    app.UseRateLimiter();
    app.MapMcp("/mcp");
    app.MapControlCenter();
    // An open connection that keeps the supervisor's --idle-exit away: the dispatcher holds one while a HUD is up, so a
    // press always finds it. Answers after sec (max 600) or as soon as this worker stops (a drain must not wait for it).
    app.MapGet("/keepalive", async (HttpContext ctx, IHostApplicationLifetime life) =>
    {
        var sec = int.TryParse(ctx.Request.Query["sec"], out var s) ? Math.Clamp(s, 1, 600) : 300;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, life.ApplicationStopping);
        try { await Task.Delay(TimeSpan.FromSeconds(sec), cts.Token); } catch (OperationCanceledException) { }
        return Results.NoContent();
    });

    Console.Error.WriteLine($"[ExileApiMcp] {McpSetup.Version} HTTP on http://127.0.0.1:{publicPort}/mcp{(publicPort != port ? $" (worker port {port})" : "")} (bearer token: {LocalHttpSecurity.TokenFilePath})");
    if (supervised)
    {
        DrainOnSupervisorSignal(app);
        ParentWatch.Start(app.Lifetime.StopApplication);
    }
    // No hello at start: this server is shared (tools\mcp-call.ps1 starts it on demand) and each caller says hello as itself
    // on its first game call (Hosting/CallerIdentity.cs); announcing the server would show the HUD an agent that is nobody.
    await app.RunAsync();
}

static int? PortFromArgs(string[] args)
{
    var i = Array.IndexOf(args, "--port");
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var p) ? p : null;
}
