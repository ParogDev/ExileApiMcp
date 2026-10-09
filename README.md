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
| Mapping data | `explore_object` maps the object at a path as a compact outline: `name: type = preview`, structs as `X=… Y=…`, collection counts, an entity's components, and `depth` 1-3. The structured result adds each node's walker path and null-safe C#, with typed enum keys. `show_data_explorer` opens the same tree as an interactive explorer (MCP App). You can expand nodes, filter, watch values change, copy paths and C#, and turn ticked fields into a plugin snippet. `find_in_object` searches under a path for members by name (`name=resist`) or by value (`value=356`: where does the number shown in game live?) and returns each match's path, C# and current value |
| Memory (read-only) | `memory_layout` overlays the offsets struct the HUD itself reads for an object on live memory. It shows mapped fields with sanity checks and flag bits, unmapped ranges, and structure-looking data inside them: vectors, vtables, object and self pointers, text. It reveals stale mappings after a patch and members the HUD doesn't have yet; `extend` reads past the struct. `memory_read` shows any region as classified slots with Ghidra addresses. `watch_memory` lists the bytes and bits that change while you do something in game. `memory_where` identifies one address. `show_memory_view` opens all of it as an interactive view (MCP App). Method and worked example: knowledge pack `shared/memory-mapping` |
| Probing (evidence, not guesses) | `memory_correlate` reads the same bytes from every item of a collection (all stash tabs, all entities). It reports which bits are fully explained by known properties, with counts and counterexamples (≥ 3 items each side to count), and where a property is stored. `memory_snapshot` / `memory_compare` run named one-variable experiments: baseline → change one thing → change it back. The prompt `probe_memory` walks the whole method |
| Guided experiments | The user performs one in-game action per step; the tools never send input. `await_change` captures the watched state, waits until the user's action changes it (flickers that revert are ignored), lets it settle, and diffs it: values, bytes and bits with field names, collection items. Steps go to an experiment record on disk. `experiment_summary` shows what changes in every repeat (evidence) vs only sometimes. `experiment_presets` holds ready-made experiments (stash: switch tab, Ctrl+click an item, hover), from `Knowledge/experiments.json`. The Memory View's **Experiments → Guided run** tab (`show_memory_view`) lets the user run a preset themselves (setup checklist, a card that mirrors the in-game one, results in plain words, the evidence building up, what to undo) or follow the agent's run read-only. **Queued steps:** `experiment_queue` leaves the step on the in-game card. Nothing is recorded until the user presses Start; the HUD then records every repeat itself, with no agent connected. The queue is in `<BridgeDirectory>\experiments\queue.json` and survives restarts. `experiment_queue_status` collects finished steps into the experiment record. How to ask the user for help: knowledge pack `shared/working-with-users` |
| HUD property map | `hud_property_map` reads the IL of the HUD's property getters offline and maps each property to the memory it reads: the offsets struct, field path and byte offset (summing `[FieldOffset]` through nested structs), or a direct `Read<T>(Address + n)`. Forward (`type=Life`) and reverse (`struct=ServerStashTabOffsets offset=61`: who reads it?). `memory_layout` labels fields with the properties that read them. Protected builds (ExileCore2) ship stub IL that only the JIT gets, so they're reported as such instead of mapped. `trace=true` prints a getter's IL |
| HUD API diff | `hud_api_diff` snapshots the installed HUD build's API (public types and members, every offsets-struct field with its `[FieldOffset]`, the IL property map) under `%LOCALAPPDATA%\ExileApiMcp\api-snapshots`, and diffs builds. It reports types and members removed, renamed (unambiguous matches only) or re-signed, offsets that moved or changed type, and properties that now read other memory. The impact scan lists where our plugins (the HUD's `Plugins\Source`) and the MCP's knowledge use a changed name, counted only in files that also mention its type. `bridge_status` flags a new build (`hudApiChanged`). Cross-game diffs (`from=poe1 to=poe2`) normalise the namespaces and show the real PoE1 / PoE2 API differences |
| Render pipeline | `pipeline_trace` times the HUD for a few seconds while things move: fps and frame-interval jitter, frame cost, Present, and for the camera and the nearest players when their data is read and how old it is when the frame reaches the screen (data age at Present), the camera-vs-entity skew, a per-frame timeline, and every plugin's Render cost. It patches the HUD's own unprotected code with Harmony and wraps its memory backend for the duration only, never the game; targets it could not instrument and hooks never hit are named (the broken link after a HUD update). Needs the bridge setting "Allow HUD Instrumentation". Method and findings: scaffolding `research/render-fidelity.md` |
| Runtime layout | `hud_runtime_layout` is reflection inside the running HUD: the CLR's real layout of an offsets struct (each field measured with sentinel bytes; nested game structs flattened), next to the metadata offsets (decoys on PoE2's disk metadata). With `path=` it maps each property of the live object to its field and offset, verified against fresh memory: the PoE2 property map the offline IL reader can't produce. Ambiguous when several fields hold the same value right now |
| Overlay accuracy | `overlay_accuracy` projects the nearest players every HUD frame twice: as plugins draw them (the HUD's cached camera and position) and from the camera matrix and position read fresh from game memory. It reports the pixel error, split into the camera's and the position's share. Offsets self-calibrate by matching the HUD's own values, with a projection self-check. `draw=true` marks both on screen for screenshots. Read-only; run it while the camera pans |
| Highlights | `highlight` points at things on the user's screen without clicking. Targets are items in the inventory or the visible stash tab (by name, every match), any UI element (walker path) or a screen area. Tiers: primary, which is animated, secondary and context. Ordered targets make a numbered sequence (`advance=true`). `await_change`, `experiment_step_start` and `experiment_queue` take `highlight` and show it while the step waits, then clear it |
| Guided flows | `guide_flow` guides the user through a multi-step task. The HUD bridge runs the flow: the current step is the first step not done, re-evaluated from the game state every 100 ms, and for each step the first option that's possible right now is highlighted. That gives a live plan which re-orders as the user acts or navigates away. The server is stateless: it expands reusable recipes from `Knowledge/flows.json` (params plus values from the game's data tables) into flows. `action=state` reads progress from any client. `experiment_queue` takes `recipe` / `flow` too |
| Passive learning | `observe` turns on the HUD's read-only observer: UI panels opening and closing (unmapped ones are saved with texts and a byte snapshot), area and level changes, and new entity kinds. `observe_wait` blocks until something worth learning happens, so an agent run in the background wakes on events instead of polling. `observe_events` reads the journal. Method: knowledge pack `shared/passive-learning` |
| In-game guide | The HUD shows an agent guide panel: a sticky "do this now" card (instruction, step n/m, waiting → detected → captured / failed) and a short log of what the agent is doing. `await_change` drives it; `guide` posts instructions and log lines; every tool call is logged there except polls and the experiment reads (`experiment_summary`, `experiment_presets`), which the Memory View repeats while it follows a run. Toggle: bridge setting "Show Agent Guide" |
| Game data tables | `game_data` lists and reads the game's data files (`Data/*.dat`) as loaded in memory. You get rows with text fields decoded and **foreign keys resolved** (`File[row] "text"`), so ids found in memory get names without a schema. Example: `StashTabAffinityId.dat` row n = stash affinity bit n; `StashType.dat` names tab types. The memory tools also label pointers into data rows (`data-row`). `find_in_game_data` goes the other way: give it ids seen in memory and it ranks the table columns that hold them (e.g. Map stash page keys → `BaseItemTypes.dat` +140) |
| Struct layout from code | `code_struct_layout function=<FUN_...> path=<object>` derives a struct's layout from one game function (best: its network (de)serializer or constructor) and diffs it with the HUD's struct. You get offsets, sizes and flag gates, each marked mapped, different size, or UNMAPPED. Static (Ghidra), heuristic |
| Code that uses a field (static) | `find_field_access` answers "what code reads, writes or bit-tests this struct field?" from the Ghidra copy of the exe, never the running game. It finds the struct's functions by fingerprint (code touching several of its known offsets through the same base register), lists the target instructions (kind, width, bits a mask touches) and decompiles excerpts. Serializers and UI code explain unmapped fields and flag bits. Needs `tools/ghidra-headless.ps1` (scaffolding) and a snapshot matching the installed exe; the first query per struct takes minutes, then it is cached under `%LOCALAPPDATA%\ExileApiMcp\ghidra-cache` |
| Introspection | `eval_path`, `describe_type`, `watch_object` (samples a path for a few seconds and reports only the leaves that changed, with counts and first/last values; noisy timers flagged) |
| Player stats | `show_player_stats` (opens the app), `stats_page`, `get_stat`, `stats_ui_state` (app polling) |
| Shared stats view | `set_stat_pinned`, `set_stats_filter`, `select_stat`, `set_stats_view` |
| Recording | `record_start`, `record_stop`, `record_status`, `snapshot`, `recording_list`, `recording_info`, `recording_frame`, `recording_range`, `recording_search`, `recording_summary` |
| HUD API reference (offline) | `hud_find_types` (types by name, or which type has a member), `hud_type` (fields with `[FieldOffset]`, properties, method signatures, enum values; optionally non-public and inherited) |
| Map | `get_map_image`: the current area's terrain as an image the model can look at, with the player marked. It comes from Radar's PluginBridge method when that Radar build exposes it (PoE2), otherwise from the game's pathfinding grid drawn by the bridge (any game, no plugin). It is trimmed to the terrain or cropped around the player, and includes the grid-to-pixel mapping for placing entities |
| HUD introspection (live, read-only) | `hud_plugin_perf` (each plugin's Tick/Render cost from the HUD's own counters), `hud_plugin_settings` (a plugin's live settings nodes; secret-looking values redacted), `plugin_bridge_methods` (the cross-plugin API registered on PluginBridge, with signatures) |
| Knowledge | `knowledge` lists, reads and searches short verified packs: the dev loop, mapping HUD data (entry points, workflow), memory mapping, working with the user in game (how to word an instruction, what goes on the card vs in chat, pacing, closing), PoE1 vs PoE2 data differences (verified live), PoE2 API differences, PoE2 decoy offsets, player stats. They are also resources at `exile://knowledge/{game}/{topic}`. Source: `Knowledge/{shared,poe1,poe2}/*.md`, embedded |
| Code in the HUD | `run_csharp` compiles and runs a C# script inside the running HUD (Roslyn): LINQ over entities, reflection into internals, `dynamic` calls into other plugins. It returns the last expression's value, `Log()` output and line-mapped compile diagnostics. **Off until you tick "Allow C# Scripts"** in the bridge settings |
| Plugin dev loop (live) | `reload_plugin` recompiles one source plugin in the running HUD, like its menu Reload button, and returns `ok`, compiler `diagnostics` (file/line/col/code) and whatever the plugin logged on load |
| Plugin dev loop (offline) | `hud_plugins` (did each source plugin compile and load in the HUD's latest run; compiler errors; stale `Errors.txt` detection), `hud_log` (the latest run's log, deduplicated, filtered by level/plugin/text) |

**Prompts:**
- `plugin_dev_loop(plugin, game?)`: edit → restart → `hud_plugins` → `hud_log` → verify live.
- `investigate_stat(key, game?)`: explain a stat from live evidence and point the user at it in the shared view.
- `guided_experiment(question, game?)`: find something out *with* the user. They perform one in-game action per step, shown on the HUD's agent guide card; the tools capture what changes, and the steps are repeated until the evidence is clear. Agents are encouraged to work this way whenever an answer depends on game state the user can change. The wording contract (one action, imperative, ~70 characters, key and mouse target; card vs detail vs chat; closing with `guide status=done` and the undo list) is knowledge pack `shared/working-with-users`.
- `probe_memory(path, question, game?)`: the full method for unknown memory (observe → population → guided experiment → code → record).

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
