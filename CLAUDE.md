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
| `Hud/HudInstall.cs` | A HUD folder on disk: Serilog log parsing (latest run), source plugins, path redaction, stack compaction |
| `Tools/HudDevTools.cs` | `hud_plugins`, `hud_log`: offline dev-loop tools (no bridge) |
| `Prompts/DevPrompts.cs` | Workflow prompts (`plugin_dev_loop`, `investigate_stat`). Keep them in step with the tools they name |
| `Apps/PlayerStatsApp.cs` | `ui://exile/player-stats` resource; HTML embedded from `ui/player-stats.html` |
| `ui-src/` | The app's TypeScript/React source, dev harness and basic-host container. Built in Docker by `ui-src/build.ps1`. **Read `ui-src/README.md` before touching the UI** |
| `run.cmd` | Launcher used by `.mcp.json` and Claude Desktop: builds, then runs a private copy so `bin\` is never locked |

## Rules

- **Every game tool takes an optional `game`** (`BridgeRegistry.GameParamDescription`).
- **Return `ToolResults.Json(...)`**, which gives JSON text plus structuredContent and sets isError for bridge errors.
- **stdio:** never write to stdout. Logs go to stderr only.
- **Stateless:** no per-connection or per-session state in the server. Shared state lives in the HUD plugin (`stats.*`); recordings are addressed by file name.
- **Client capabilities in stateless HTTP:** `server.ClientCapabilities` is null. Read `context.JsonRpcRequest.Context?.ClientCapabilities ?? server.ClientCapabilities`.
- **Build:** `dotnet build`. Clients launched through `run.cmd` run from `bin\launch\run-*` copies and never lock `bin\Debug`. A server started with `dotnet run` does lock it; then verify with `dotnet build -o <temp dir>`.
- **Text from the user's machine (logs, Errors.txt, stack traces) goes through `HudInstall.ForAgent`.** It shortens paths outside the HUD folder; the HUD's build tree must never reach tool output, since agents copy it into commits.
- **UI changes:** edit `ui-src/`, run `ui-src/build.ps1`, and commit `ui/player-stats.html` with the source. CI fails on a stale bundle.
- **Seeing the UI:** for layout and states use the harness (`build.ps1 -Serve`, then `http://127.0.0.1:5174/harness.html?...`; drive it with `window.harness`). For real data use `ui-src/basic-host.ps1` against `run.cmd --http`. Screenshot both themes and a 380px width before calling a UI change done.
- **Tool result shapes are the UI's contract:** `ui-src/src/types.ts` mirrors the stats tools' `structuredContent`. Change both together.
