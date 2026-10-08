# MCP App UIs (`ui://exile/*`)

Source for the interactive panels that ExileApiMcp serves as MCP Apps. The server embeds the built single-file HTML from `../ui/`; this folder exists only to produce it.

| Stack | TypeScript, React 19, Tailwind 4, Vite 8 + `vite-plugin-singlefile`, `@modelcontextprotocol/ext-apps` 2.0.1 |
|---|---|
| Toolchain | Docker only. No Node or npm on Windows; `node_modules` lives in a named Docker volume |
| Output | `../ui/player-stats.html` (committed, embedded as `ui://exile/player-stats`) |

## Commands

From the scaffolding root (Windows PowerShell 5.1):

| What | Command |
|---|---|
| Build (typecheck, app, harness) and copy into `../ui/` | `powershell -NoProfile -ExecutionPolicy Bypass -File MCP\ExileApiMcp\ui-src\build.ps1` |
| Same, then serve the dev harness on http://127.0.0.1:5174/harness.html | `... build.ps1 -Serve` (blocks; run it in the background) |
| npm without npm, e.g. add a pinned package | `... build.ps1 -Npm "install -E pkg@1.2.3"` |
| Real host against the real server: ext-apps **basic-host** on http://localhost:8080 | start the server with `run.cmd --http`, then `... ui-src\basic-host.ps1` (`-Stop`, `-Rebuild`) |

After a UI build, restart the MCP server to embed the new HTML. `run.cmd` rebuilds on every start. **CI rebuilds the UI and fails if `ui/player-stats.html` doesn't match its source**, so commit the rebuilt file with the source change.

## Three ways to see the app, fastest first

1. **Dev harness** (`dist/harness.html`): a fake host plus a fake server built from a real PoE2 character (`dev/poe2-stats.json`). No game, HUD or server needed. Every state is reachable from the URL, so screenshots are deterministic:
   ```
   /harness.html?theme=dark&width=380&scenario=offline&latency=400&display=fullscreen&vars=none&simulate=0
   ```
   - `scenario`: `live`, `offline`, `not-in-game`, `empty`, or `flaky` (30% errors plus jitter).
   - `vars=none` drops the host style variables to test the app's own fallbacks.
   - `simulate=0` stops the random vitals and stat changes.

   Agents can drive it from a browser console or JS tool through `window.harness`:
   ```js
   harness.hud.pin("cold_damage_resistance_%")  // a change made in the in-game panel
   harness.hud.filter("life", "vitals"); harness.hud.select("level"); harness.hud.sort("value", true)
   harness.server.bumpRandomStat("fire_damage_resistance_%")  // value change -> delta + flash
   harness.setScenario("offline"); harness.setTheme("dark"); harness.setWidth(380)
   harness.log()        // every tool call the app made: name, args, ms, outcome
   harness.context()    // what the app sent with ui/update-model-context
   harness.messages()   // what "Ask Claude" posted with ui/message
   ```
2. **basic-host** (the ext-apps reference host) against `run.cmd --http`: real tools/call round trips, real HUD state. Use it to check sync with the in-game panel: `tools\bridge-query.ps1 -Game poe2 'stats.set_filter|text=life'` shows up in the app within a second.
3. **Claude Desktop, Chat tab**, with `exileapi` in `claude_desktop_config.json` over stdio (see the server README). Ask "show my player stats". This is the only Claude surface that renders MCP Apps for a local server; Claude Code shows the text summary.

## How the player-stats app works

| File | Role |
|---|---|
| `src/main.tsx` | ext-apps wiring: `useApp`, host theme/fonts, tool calls through the host, capability checks (`updateModelContext`, `message`, fullscreen) |
| `src/sync.ts` | `StatsStore`, framework-free: polling, optimistic overlays, backoff, deltas. Shared with the harness |
| `src/PlayerStats.tsx` | Layout, banners, model-context updates |
| `src/components.tsx`, `StatTable.tsx`, `StatDetail.tsx` | Vitals, resistance bars, pinned cards, stat table, inline detail row |
| `src/styles.css` | Host CSS variables mapped to semantic Tailwind tokens (`bg-surface`, `text-fg-2`, `border-line`…), with fallbacks |
| `dev/` | Harness host and fake server |

- **State:** the HUD plugin owns the shared view (pins, filter, category, selection, sort) and versions it with `rev`.
  - The app polls `stats_ui_state(sinceRev)` every second. When idle the answer is `{unchanged:true}` plus vitals.
  - It refreshes the full stat list every 3 s with `stats_page`, pageSize 200, and filters and sorts locally. Typing never waits on a round trip, and the shared filter syncs after a 350 ms pause.
  - Polling pauses while the panel is hidden and backs off to 15 s while the bridge is down.
- **Writes are optimistic.** Each change is an overlay on the last confirmed state until its mutator answers with the new `{rev, state}`. On `ok:false` or an error, the overlay is dropped and a toast says why. A poll that started before a mutation landed can't roll the state back.
- **Model context.** When the selection or pins change, the app tells the model through `ui/update-model-context`, debounced and deduplicated. "This stat" in the next prompt then needs no tool call.
- **Theme:** only semantic tokens, never raw colours, except the fixed game hues (life, mana, ES, elements). These are tuned to read in light and dark.

## Conventions

- **Pin exact versions, at least two weeks old** (supply chain). Change them with `-Npm "install -E ..."` so `package-lock.json` stays in sync. Install scripts are disabled in the image and in CI.
- **The bundle must stay self-contained:** no external origins, fonts or CDNs. The app declares no CSP domains and talks only to the host.
- **Keep the app working without optional host features.** `ui/message` hides "Ask Claude", fullscreen hides its button, and missing `structuredContent` falls back to parsing the JSON text.
