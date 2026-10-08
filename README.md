# ExileApiMcp

MCP server that exposes live **Path of Exile 1 and 2** game state as [Model Context Protocol](https://modelcontextprotocol.io/) tools, for developing and debugging HUD plugins with an AI assistant. It talks to the [What's an AI Bridge?](https://github.com/ParogDev/WhatsAnAiBridge) plugin running inside the PoE1 HUD (ExileApi / ExileCore) or the PoE2 HUD (ExileCore2).

> **What does it do?** Your AI assistant can't see your game. With this server it can read your character's stats, nearby monsters, UI panels and stash. It can walk the HUD's object model by reflection, record gameplay, and point you at things in a shared stats panel that the in-game HUD and Claude both show.

## How it works

```
 PoE1 HUD (ExileApi)              PoE2 HUD (ExileCore2)
   What's an AI Bridge?             What's an AI Bridge?
   PoeHelper\claude-bridge          halp2\claude-bridge
        \  JSON-RPC over 127.0.0.1, per-launch token  /
                      ExileApiMcp (this project)
                stdio (default)  |  --http (127.0.0.1, bearer token)
                      Claude Code / Claude Desktop / any MCP client
```

- **One server, both games.** Tools take an optional `game` argument (`poe1` | `poe2`). Without it, the one HUD that is running is used. `bridge_status` shows which bridges are up and what each game's HUD *cannot* provide; those fields are omitted, never faked.
- **MCP spec 2026-07-28, stateless** (C# SDK 2.2). Over HTTP, current clients use no sessions. Older clients that still send `initialize` get a session (dual-era).
- **MCP App.** `show_player_stats` opens an interactive stats panel in clients that render MCP Apps (Claude Desktop chat, with the server configured locally over stdio). Other clients get a text summary.
  - The panel shows vitals, resistance bars against their caps, pinned stats, and a searchable, sortable table of every stat.
  - It follows the host's light/dark theme.
  - It stays in sync with the in-game panel: pin a stat in either, or let Claude pin it, and both show it.
  - UI source, dev harness and how to test it: [ui-src/README.md](ui-src/README.md).

## Requirements

| | |
|---|---|
| HUD | PoE1 ExileApi and/or PoE2 ExileCore2, with **What's an AI Bridge?** enabled |
| .NET | .NET 10 SDK (to build and run) |
| Client | Any MCP client: Claude Code, Claude Desktop, VS Code, … |

## Configure

Start the server with **`run.cmd`**. It builds into `bin\launch\build`, then runs a private copy, so several clients (Claude Code sessions, Claude Desktop, an `--http` instance) can run it while you keep rebuilding. (`dotnet run` locks `bin\`, and a second client's build then fails.) It needs the .NET 10 SDK.

### Claude Code (stdio) - `.mcp.json`

```json
{
  "mcpServers": {
    "exileapi": {
      "command": "cmd",
      "args": ["/c", "C:\\path\\to\\ExileApiMcp\\run.cmd"]
    }
  }
}
```

Bridge folders default to `%USERPROFILE%\Documents\PoeHelper\claude-bridge` (PoE1) and `%USERPROFILE%\Documents\halp2\claude-bridge` (PoE2). Override them with `POE1_BRIDGE_DIR` / `POE2_BRIDGE_DIR` in `env`.

The legacy single-HUD settings `BRIDGE_DIR` (+ `BRIDGE_PORT`) still work. With them, the game is detected from the bridge.

### Claude Desktop extension (one-click install)

`desktop-extension\pack.ps1` builds `desktop-extension\dist\exileapi-desktop-<version>.mcpb`:
- It bundles a framework-dependent publish of the server (needs the .NET 10 runtime) and runs the official `@anthropic-ai/mcpb` packer in a container.
- Double-click the file, or use Claude Desktop → Settings → Extensions, to install it.
- Desktop manages installed extensions itself (enable, disable, update), so the config-file caveats below don't apply.
- The bundle is a snapshot: re-pack and reinstall after changing the server.

### Claude Desktop (stdio, renders the stats app)

1. Add the same entry under `mcpServers` in `%APPDATA%\Claude\claude_desktop_config.json` (Settings → Developer → Edit config), with the absolute path to `run.cmd`.
2. Restart Claude Desktop.
3. In a **Chat** conversation, ask "show my player stats".

Claude Desktop renders MCP Apps for local servers configured this way. Servers added by URL are reached from the cloud and can't see `127.0.0.1`.

### HTTP (local agents, MCP Inspector, ext-apps basic-host)

```
run.cmd --http [--port 50910]
```
- Listens on `http://127.0.0.1:50910/mcp` only.
- Requires `Authorization: Bearer <token>`. The token is created on first run in `%LOCALAPPDATA%\ExileApiMcp\http-token.txt`; set `MCP_HTTP_TOKEN` to use your own.
- For Claude Code:
  ```json
  { "mcpServers": { "exileapi-http": { "type": "http", "url": "http://127.0.0.1:50910/mcp",
      "headers": { "Authorization": "Bearer ${EXILEAPI_MCP_TOKEN}" } } } }
  ```

## Tools

| Group | Tools |
|---|---|
| Status | `bridge_status` |
| Game state | `get_player`, `get_area`, `get_entities`, `deep_scan`, `get_npc_dialog`, `get_map_data`, `get_ui_panels`, `get_stash`, `get_all`, `get_player_stats_raw` |
| Introspection | `eval_path`, `describe_type` |
| Player stats | `show_player_stats` (opens the app), `stats_page`, `get_stat`, `stats_ui_state` (app polling) |
| Shared stats view | `set_stat_pinned`, `set_stats_filter`, `select_stat`, `set_stats_view` |
| Recording | `record_start`, `record_stop`, `record_status`, `snapshot`, `recording_list`, `recording_info`, `recording_frame`, `recording_range`, `recording_search`, `recording_summary` |
| HUD API reference (offline) | `hud_find_types` (types by name, or which type has a member), `hud_type` (fields with `[FieldOffset]`, properties, method signatures, enum values; optionally non-public and inherited) |
| Knowledge | `knowledge` lists, reads and searches short verified packs: the dev loop, PoE2 API differences, PoE2 decoy offsets, player stats. They are also resources at `exile://knowledge/{game}/{topic}`. Source: `Knowledge/{shared,poe1,poe2}/*.md`, embedded |
| Code in the HUD | `run_csharp` compiles and runs a C# script inside the running HUD (Roslyn): LINQ over entities, reflection into internals, `dynamic` calls into other plugins. It returns the last expression's value, `Log()` output and line-mapped compile diagnostics. **Off until you tick "Allow C# Scripts"** in the bridge settings |
| Plugin dev loop (live) | `reload_plugin` recompiles one source plugin in the running HUD, like its menu Reload button, and returns `ok`, compiler `diagnostics` (file/line/col/code) and whatever the plugin logged on load |
| Plugin dev loop (offline) | `hud_plugins` (did each source plugin compile and load in the HUD's latest run; compiler errors; stale `Errors.txt` detection), `hud_log` (the latest run's log, deduplicated, filtered by level/plugin/text) |

**Prompts:**
- `plugin_dev_loop(plugin, game?)`: edit → restart → `hud_plugins` → `hud_log` → verify live.
- `investigate_stat(key, game?)`: explain a stat from live evidence and point the user at it in the shared view.

The dev-loop tools read the HUD folders on disk (next to the bridge folders), so they work with the game and HUD closed. Two things are rewritten to save tokens and keep machine-specific source locations out of agents' notes:
- paths outside the HUD folder are shortened;
- .NET runtime stack frames are collapsed.

The HUD compiles source plugins **at startup** (or from the menu's Reload button), not on save. `reload_plugin` does the Reload-button step for you, without a HUD restart.

> **Turn on "Avoid locking plugin dlls"** (HUD menu → Core → Plugin Settings) in each HUD and restart it once.
> - It's off by default. While it's off, a loaded plugin's DLL stays locked, so recompiling changed code fails, and the plugin stays unloaded until a restart.
> - While it's off, `reload_plugin` refuses with `dll_locked` and says so; `force=true` reloads unchanged code anyway.
> - Reloads aren't possible for the bridge plugin itself (restart the HUD), and a brand-new plugin folder also needs a restart.

**API reference tools.**
- **What they read:** `hud_find_types` and `hud_type` read the HUD's DLL metadata with `MetadataLoadContext`. Nothing is loaded for execution, and the game and HUD can be closed.
- **Why it matters for PoE2:** ExileCore2 ships without source, so this is its API reference.
- **Beyond the live walker:** unlike `describe_type`, they see non-public members and types not reachable from `GameController`.
- **Decoy offsets:** offsets that can't be real, such as GameOffsets2's obfuscated decoys, are flagged `suspect`.

- **Stats** are keyed by Stats.dat key (e.g. `fire_damage_resistance_%`), which is stable across patches and between games, and include the in-game text and a category.
- **The stats view state** (pins, filter, selection, sort) lives in the HUD plugin and is versioned by `rev`. The in-game panel, the app and agents all change the same state. Mutators accept an optional `expectedRev` and answer `rev_mismatch` if someone else changed it first.
- **Recordings** are addressed by file name, so playback calls are stateless.
- **All tools are read-only toward the game** and never send input. Tools that change state are annotated (`readOnlyHint: false`) and only affect the shared stats view or recordings.

## Security

The bridge and this server listen on loopback only. Any local account can reach loopback, so **tokens are the boundary**:
- **Bridge:** every request carries the HUD's per-launch token from `bridge-token.txt`.
- **HTTP transport:**
  - Host header allowlist (blocks DNS rebinding; 421)
  - Origin check (403)
  - bearer token (401)
  - global rate limit
  - no CORS

The server adds no network egress, and the MCP App loads no external origins.
