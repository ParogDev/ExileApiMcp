# ExileApiMcp

MCP server (C# SDK 2.2, spec 2026-07-28) exposing live PoE1/PoE2 HUD state through the "What's an AI Bridge?" plugin.

## Layout

| Path | What |
|---|---|
| `Program.cs` | stdio (default) or `--http [--port N]` (127.0.0.1, StatefulForInitializeClients = stateless for 2026-07-28 clients) |
| `Hosting/McpSetup.cs` | server info, instructions, tool/resource registration, MCP Apps |
| `Hosting/LocalHttpSecurity.cs` | Host allowlist (421), Origin (403), bearer token (401) - the SDK does none of these |
| `Bridge/BridgeClient.cs` | one bridge connection; re-reads port/token files each connect; 5 s connect budget |
| `Bridge/BridgeRegistry.cs` | poe1/poe2 bridges, `game` resolution, bridge errors -> McpException |
| `Tools/*.cs` | static tool classes; inject `BridgeRegistry`; annotate ReadOnly/Destructive/Idempotent/OpenWorld |
| `Apps/PlayerStatsApp.cs` | `ui://exile/player-stats` resource; HTML embedded from `ui/player-stats.html` |

## Rules

- **Every game tool takes an optional `game`** (`BridgeRegistry.GameParamDescription`).
- **Return `ToolResults.Json(...)`**, which gives JSON text plus structuredContent and sets isError for bridge errors.
- **stdio:** never write to stdout. Logs go to stderr only.
- **Stateless:** no per-connection or per-session state in the server. Shared state lives in the HUD plugin (`stats.*`); recordings are addressed by file name.
- **Client capabilities in stateless HTTP:** `server.ClientCapabilities` is null. Read `context.JsonRpcRequest.Context?.ClientCapabilities ?? server.ClientCapabilities`.
- **Build:** `dotnet build`. The bin folder is locked while a client runs the server, so to verify a build use `dotnet build -o <temp dir>`.
