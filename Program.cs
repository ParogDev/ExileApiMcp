using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ExileApiMcp;
using ExileApiMcp.Tools;
using ModelContextProtocol.Server;

// Read config from environment or defaults
var port = int.TryParse(Environment.GetEnvironmentVariable("BRIDGE_PORT"), out var p) ? p : 50900;
var bridgeDir = Environment.GetEnvironmentVariable("BRIDGE_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PoeHelper", "claude-bridge");

// Check for port file (ephemeral port support)
var portFilePath = Path.Combine(bridgeDir, "bridge-port.txt");
if (File.Exists(portFilePath) && int.TryParse(File.ReadAllText(portFilePath).Trim(), out var filePort))
    port = filePort;

Console.Error.WriteLine($"[ExileApiMcp] Connecting to plugin on 127.0.0.1:{port}");
Console.Error.WriteLine($"[ExileApiMcp] Bridge directory: {bridgeDir}");

var builder = Host.CreateApplicationBuilder(args);

// Log to stderr only -- stdout is reserved for MCP protocol
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Warning;
});

// Register BridgeClient as singleton -- connect eagerly
var client = new BridgeClient(port, bridgeDir);
builder.Services.AddSingleton(client);

// Register MCP server with stdio transport and tool classes
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "ExileApi MCP",
            Version = "2.0.0",
        };
    })
    .WithStdioServerTransport()
    .WithTools<GameStateTools>()
    .WithTools<RecordingTools>()
    .WithTools<EvalTools>();

var host = builder.Build();

// Connect to plugin in background (don't block MCP server startup)
_ = Task.Run(async () =>
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(cts.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[ExileApiMcp] Initial connection failed: {ex.Message}");
        Console.Error.WriteLine("[ExileApiMcp] Tools will attempt reconnection on first use");
    }
});

await host.RunAsync();
