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
| `Hud/HudTypes.cs`, `Tools/HudTypeTools.cs` | `hud_find_types`, `hud_type`: HUD DLL metadata via MetadataLoadContext (cached per HUD folder, rebuilt when ExileCore*/GameOffsets* change) |
| `Tools/HudDevTools.cs` | `hud_plugins`, `hud_log`: offline dev-loop tools (no bridge) |
| `Prompts/DevPrompts.cs` | Workflow prompts (`plugin_dev_loop`, `investigate_stat`, `probe_memory`, `guided_experiment`). Keep them in step with the tools they name and with knowledge pack `shared/working-with-users` (how agents ask the user for in-game help) |
| `Knowledge/{shared,poe1,poe2}/*.md`, `Tools/KnowledgeTools.cs` | Knowledge packs, embedded and served by the `knowledge` tool and `exile://knowledge/{game}/{topic}`. Add a pack when you verify something agents would otherwise rediscover. Keep it short, factual, and free of paths to private source trees |
| `Apps/PlayerStatsApp.cs` | `ui://exile/player-stats` resource; HTML embedded from `ui/player-stats.html` |
| `ui-src/` | The app's TypeScript/React source, dev harness and basic-host container. Built in Docker by `ui-src/build.ps1`. **Read `ui-src/README.md` before touching the UI** |
| `run.cmd` | Launcher used by `.mcp.json` and Claude Desktop: builds, then runs a private copy so `bin\` is never locked; starts the supervisor |
| `Supervisor/` | Its own small project: the process clients launch. Runs the server as a worker and swaps it for a newly deployed build without restarting the session or cutting a call. **Read `Supervisor/README.md`** |
| `Incidents/`, `Tools/IncidentTools.cs` | The machine-wide incident board (`%LOCALAPPDATA%\Hexile\incidents.json`, contract shared with the scaffolding's `tools\incident-lib.ps1`): store, bridge diagnosis, automatic raise/resolve, deploy leases, background monitor; `InstrumentIncidents.cs`: measurement patches left behind (`instrument:<game>`) and the `instrument.cancel` sent when a measurement tool call is cancelled |
| `Tests/` | Unit checks without a framework: `dotnet run --project Tests` (CI runs it). Set `HEXILE_INCIDENT_DIR` to a temp folder in anything that writes the board: never the live one |
| `Dispatch/`, `Tools/PubSubTools.cs` | Plugin pub/sub through the bridge (`pubsub_*`, `feedback_reply`, `feedback_dispatcher`) and the F8 feedback dispatcher, a hosted service of the shared HTTP server only: one fresh Claude Code session per Whats A Route press (fire and forget: a background session with Remote Control the user sees in Claude, stopped once its answer is on the card, headless `claude -p` as the fallback; both start slim: the standing brief as system prompt, no CLAUDE.md or memory, route-fix tools only) (`FeedbackKit.cs` is its pure, unit-tested half). A fix a handler couldn't merge (a permission check refused it) is asked on the in-game card, Merge it / Close it / Leave it, and the dispatcher acts on the click: merge on green, pointer bump, HUD sync (`UnmergedFix.cs`) |
| `Dispatch/F8.cs`, `Dispatch/F8Replies.cs`, `Tools/F8Dtos.cs` | The F8 progress contract (pure, unit-tested): stages / phases / WaitingOn, legacy status both ways, the `feedback.reply` data (legacy `ref`/`status`/`text` plus PascalCase stage fields), the press's brief section, `AgentRouteKit` (route_agent validation); publishing and reading a press's stage, and the handler stage inference (C4) the call filter in `Hosting/McpSetup.cs` runs for `route-f8-<seq>` callers (a cache only: the bridge's replies are the record) |
| `Tools/AgentRouteTools.cs`, `Tools/AgentStatusTools.cs` | `route_agent` (agent routes on Whats A Route: `route.agent` out, `route.agent.progress` back) and `agent_status` (the caller's agents-board row, bridge `work.set`) |
| `Dispatch/FeedbackSessions.cs`, `Tools/FeedbackSessionsTools.cs`, `Bridge/FeedbackHub.cs` | `feedback_sessions`: every F8 press with its session (from the dispatcher's files, merged with the bridge's press and reply events), typed in `Tools/FeedbackSessionDtos.cs`; `exile://feedback/sessions` pushes when those files change. The control center's **F8 sessions** page |
| `deploy.ps1`, `Tools/ServerTools.cs`, `Hosting/ServerBuild.cs` | Deploying a build (`%LOCALAPPDATA%\ExileApiMcp\current.json`), `mcp_deploy` / `mcp_status`, and the build info each server sends the HUD |

## Rules

- **Every game tool takes an optional `game`** (`BridgeRegistry.GameParamDescription`).
- **Typed results.** A tool returning data declares `UseStructuredContent = true, OutputSchemaType = typeof(TheDto)` and returns `Dto.Result(value, text)` (`Tools/Dto.cs` has the pattern). The DTO mirrors the JSON on the wire (MCP Apps parse it), lives in `Tools/<Family>Dtos.cs` with a family-prefixed name (one namespace for all DTOs: two branches once each defined `DesktopInfo` and broke main), and keeps unknown fields in `[JsonExtensionData] Extra`, so fields the bridge adds later pass through. Bridge errors stay isError results carrying the error object (`Tools/TypedReply.cs`; apps read the codes). Open data stays `JsonElement`. `ToolResults.Json` remains for the few tools whose result has several shapes.
- **stdio:** never write to stdout. Logs go to stderr only.
- **Stateless:** no per-connection or per-session state in the server (the F8 dispatcher keeps its state in the bridge's claims and `handlers\` files, so a swapped worker carries on). Shared state lives in the HUD plugin (`stats.*`); recordings are addressed by file name. The one exception is identity on the shared HTTP server: a request's `X-Hexile-Agent` header makes `SessionIdentity` answer as that caller, and `BridgeRegistry` keeps bridge connections per caller (`Hosting/CallerIdentity.cs`). Read the session through `SessionIdentity`, never cache it in a static.
- **Client capabilities in stateless HTTP:** `server.ClientCapabilities` is null. Read `context.JsonRpcRequest.Context?.ClientCapabilities ?? server.ClientCapabilities`.
- **After an MCP change is merged and bumped in the scaffolding repo, deploy it (`mcp_deploy`).** Every supervised session switches within seconds; running calls finish on the old build. Develop against your own build with `HEXILE_MCP_LOCAL=1` or `dotnet build -o <temp>` plus `--http --port <spare>`, never by deploying an unmerged commit (unless testing it on purpose: say so in the reason).
- **Build:** `dotnet build`. Clients launched through `run.cmd` run from `bin\launch\run-*` copies and never lock `bin\Debug`. A server started with `dotnet run` does lock it; then verify with `dotnet build -o <temp dir>`.
- **Text from the user's machine (logs, Errors.txt, stack traces) goes through `HudInstall.ForAgent`.** It shortens paths outside the HUD folder; the HUD's build tree must never reach tool output, since agents copy it into commits.
- **UI changes:** edit `ui-src/`, run `ui-src/build.ps1`, and commit `ui/player-stats.html` with the source. CI fails on a stale bundle.
- **Seeing the UI:** for layout and states use the harness (`build.ps1 -Serve`, then `http://127.0.0.1:5174/harness.html?...`; drive it with `window.harness`). For real data use `ui-src/basic-host.ps1` against the HTTP server (the shared one on 50910 starts on demand). Screenshot both themes and a 380px width before calling a UI change done.
- **Tool result shapes are the UI's contract:** `ui-src/src/types.ts` mirrors the stats tools' `structuredContent`. Change both together.
