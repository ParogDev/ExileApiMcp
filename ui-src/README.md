# MCP App UIs (`ui://exile/*`)

Source for the interactive panels that ExileApiMcp serves as MCP Apps. The server embeds the built single-file HTML from `../ui/`; this folder exists only to produce it.

| Stack | TypeScript, React 19, Tailwind 4, Vite 8 + `vite-plugin-singlefile`, `@modelcontextprotocol/ext-apps` 2.0.1 |
|---|---|
| Toolchain | Docker only. No Node or npm on Windows; `node_modules` lives in a named Docker volume |
| Output | `../ui/player-stats.html` (`ui://exile/player-stats`) and `../ui/data-explorer.html` (`ui://exile/data-explorer`), both committed |

Two apps share `src/styles.css`, `src/components.tsx` and `src/icons.tsx`:

| App | Entry | Source | Opened by |
|---|---|---|---|
| Player stats | `player-stats.html` → `src/main.tsx` | `src/*.tsx`, `src/sync.ts` | `show_player_stats` |
| Data explorer | `data-explorer.html` → `src/explorer/main.tsx` | `src/explorer/*` | `show_data_explorer {path?, game?}` |

`vite build` takes one input per run (single-file plugin), so `npm run build` runs it three times: default (stats), `--mode explorer`, `--mode harness`. Tailwind scans the whole source tree, so a class added to one app can change the other app's CSS: rebuild and commit both bundles together.

## Commands

From the scaffolding root (Windows PowerShell 5.1):

| What | Command |
|---|---|
| Build (typecheck, app, harness) and copy into `../ui/` | `powershell -NoProfile -ExecutionPolicy Bypass -File MCP\ExileApiMcp\ui-src\build.ps1` |
| Same, then serve the dev harness on http://127.0.0.1:5174/harness.html | `... build.ps1 -Serve` (blocks; run it in the background) |
| npm without npm, e.g. add a pinned package | `... build.ps1 -Npm "install -E pkg@1.2.3"` |
| Real host against the real server: ext-apps **basic-host** on http://localhost:8080 | start the server with `run.cmd --http`, then `... ui-src\basic-host.ps1` (`-Stop`, `-Rebuild`) |

After a UI build, restart the MCP server to embed the new HTML. `run.cmd` rebuilds on every start. **CI rebuilds the UI and fails if `ui/player-stats.html` or `ui/data-explorer.html` doesn't match its source**, so commit the rebuilt files with the source change.

## Three ways to see the apps, fastest first

1. **Dev harness** (`dist/harness.html`): a fake host plus a fake server per app. No game, HUD or server needed. Every state is reachable from the URL, so screenshots are deterministic. `app=stats` (default) or `app=explorer` picks the app; `bare=1` renders only the app's iframe, filling the viewport (pixel-exact captures in a narrow browser pane).
   ```
   /harness.html?app=stats&theme=dark&width=380&scenario=offline&latency=400&display=fullscreen&vars=none&simulate=0
   /harness.html?app=explorer&path=GameController.Player&theme=dark&width=380&scenario=flaky&bare=1
   ```
   - `scenario`: stats: `live`, `offline`, `not-in-game`, `empty`, `flaky` (30% errors plus jitter); explorer: `live`, `offline`, `flaky`.
   - `vars=none` drops the host style variables to test the app's own fallbacks.
   - `simulate=0` stops the simulated game (random vitals and stat changes; drifting Life values).
   - `path=` (explorer) is the `show_data_explorer` argument: where the tree opens. A bad path shows the error state.

   The **stats** fake server is built from a real PoE2 character (`dev/poe2-stats.json`). The **explorer** fake server (`dev/fakeExplore.ts`) answers `explore_object`, `show_data_explorer`, `watch_object` and `eval_path` from `dev/explore/*.json`: 11 real PoE2 responses captured live (0 GameController, 1 Player, 2 Life component, 3 Life.Health, 4 Player.Stats, 5 Player.Buffs, 6 Buffs[0], 7 IngameUi, 8 Player.Pos, 9 Entities, 10 Entities[2]). Any other path is synthesised from how its parent listed it, so every node expands; unknown members fail like the bridge ("No public property or field 'X' on type 'Y'"). States injected so they can be seen without a game: `Player.Mods` (getter throws), `Player.NativeHandle` (blocked), IngameUi's last members arrive as budget-`skipped` (the "Load them" row), `Stats` / `Entities` / `ChatMessages` page, Life values drift between reads (refresh, auto-refresh, watch).

   Agents can drive it from a browser console or JS tool through `window.harness`:
   ```js
   // stats
   harness.hud.pin("cold_damage_resistance_%")  // a change made in the in-game panel
   harness.hud.filter("life", "vitals"); harness.hud.select("level"); harness.hud.sort("value", true)
   harness.server.bumpRandomStat("fire_damage_resistance_%")  // value change -> delta + flash
   // explorer
   harness.server.bump()          // life drops by 60: refresh / watch show it
   harness.server.drift = false   // freeze values
   // both
   harness.setScenario("offline"); harness.setTheme("dark"); harness.setWidth(380)
   harness.log()        // every tool call the app made: name, args, ms, outcome
   harness.context()    // what the app sent with ui/update-model-context
   harness.messages()   // what "Ask Claude" posted with ui/message
   ```
   The explorer's rows carry `data-id` (the walker path) inside the app iframe, e.g. `document.querySelector("iframe").contentDocument.querySelector('[data-id="GameController.Player"] button').click()` expands Player.
2. **basic-host** (the ext-apps reference host) against `run.cmd --http`: real tools/call round trips, real HUD state. Use it to check sync with the in-game panel: `tools\bridge-query.ps1 -Game poe2 'stats.set_filter|text=life'` shows up in the app within a second.
3. **Claude Desktop, Chat tab**, with `exileapi` in `claude_desktop_config.json` over stdio (see the server README). Ask "show my player stats" or "open the data explorer at GameController.Player". This is the only Claude surface that renders MCP Apps for a local server; Claude Code shows the text summary.

## How the player-stats app works

| File | Role |
|---|---|
| `src/main.tsx` | ext-apps wiring: `useApp`, host theme/fonts, tool calls through the host, capability checks (`updateModelContext`, `message`, fullscreen) |
| `src/sync.ts` | `StatsStore`, framework-free: polling, optimistic overlays, backoff, value deltas, vitals history, and which view changes came from elsewhere (`remote`, `selectionSource`). Shared with the harness |
| `src/PlayerStats.tsx` | Top bar (game, level, weapon set, sync pill), banners, inline vs fullscreen layout, model-context updates, "Ask Claude" prompt |
| `src/components.tsx` | Sync pill + popover, vitals tiles (segmented bars, sparkline trace), resistance tiles (cap tick, over-cap hatch), pin chips, banners, toasts, skeletons, empty states |
| `src/StatTable.tsx` | Search / category / "changed" / raw-keys toolbar, sortable single-line rows with flash and Δ, keyboard navigation, and the detail sheet host |
| `src/StatDetail.tsx` | One stat in depth: value, key + copy, Stats.dat record details, resistance layers (base / total / uncapped / cap), pin, Ask Claude |
| `src/icons.tsx`, `src/format.ts` | Inline SVG icon set; labels, units, resistance helpers |
| `src/styles.css` | Host CSS variables mapped to semantic Tailwind tokens (`bg-surface`, `text-fg-2`, `border-line`…), with fallbacks; game hues; animations (all disabled under `prefers-reduced-motion`) |
| `dev/` | Harness host and fake server |

**Layout.** Glanceable card first, depth on demand. Inline (380-760 px): top bar, vitals tiles, four resistance tiles, pin chips, then a ~17 rem stat list; selecting a stat opens a sheet over the bottom of the list, so nothing above moves. Fullscreen: a sidebar (vitals, resistances, pins, detail panel) next to a list that fills the height. The `xs` breakpoint (30 rem) and `sm` (40 rem) add the raw key next to the in-game text and widen the tiles.

- **State:** the HUD plugin owns the shared view (pins, filter, category, selection, sort) and versions it with `rev`.
  - The app polls `stats_ui_state(sinceRev)` every second. When idle the answer is `{unchanged:true}` plus vitals.
  - It refreshes the full stat list every 3 s with `stats_page`, pageSize 200, and filters and sorts locally. Typing never waits on a round trip, and the shared filter syncs after a 350 ms pause.
  - Polling pauses while the panel is hidden and backs off to 15 s while the bridge is down.
- **Writes are optimistic.** Each change is an overlay on the last confirmed state until its mutator answers with the new `{rev, state}`. On `ok:false` or an error, the overlay is dropped and a toast says why. A poll that started before a mutation landed can't roll the state back.
- **Changes from elsewhere.** A poll whose state differs from the last confirmed one was changed by the HUD panel or an agent (the app's own writes reconcile through the mutator's reply, never through a poll). The sync pill shows "Synced pins/selection/…" for 3 s, a remotely selected row scrolls to the centre and pulses, and the detail carries a "selected elsewhere" badge.
- **Model context.** When the selection or pins change, the app tells the model through `ui/update-model-context`, debounced and deduplicated. "This stat" in the next prompt then needs no tool call.
- **Local-only preferences:** the raw-keys toggle (persisted in `localStorage` when the sandbox allows it) and the "changed since the panel opened" quick filter. Everything else in the toolbar is shared state.
- **Keyboard:** `/` focuses search; in the list, arrows / Home / End move the selection, Enter pins, Escape clears, and typing starts a search.
- **Theme:** only semantic tokens, never raw colours, except the fixed game hues (life, mana, ES, elements). These are tuned to read in light and dark.

## How the data explorer works

A devtools-style object inspector over the live HUD object model, for mapping data and turning it into plugin code.

| File | Role |
|---|---|
| `src/explorer/main.tsx` | ext-apps wiring, same pattern as the stats app. Seeds the tree from `show_data_explorer`'s result (`ontoolresult`); `ontoolinput` gives the requested path and game |
| `src/explorer/types.ts` | The `explore_object` / `watch_object` structuredContent shapes. **Mirrors `Tools/ExploreTools.cs` and the bridge's `object.explore`; change both together** |
| `src/explorer/store.ts` | `ExplorerStore`, framework-free: per-path cache of `explore_object` results (depth 1, lazily expanded), root history, selection, ticked values, paging, skipped-member loads, watch, auto-refresh, toasts |
| `src/explorer/rows.ts` | Flattens the cache into visible rows (nodes, component group, load-more, skipped, error, empty) and resolves a row id back to its node |
| `src/explorer/paths.ts` | Splits walker paths and C# accessors into matching segments (breadcrumbs, code generation) |
| `src/explorer/codegen.ts` | Ticked values -> C# snippet: hoists the deepest common reference-type parent into a local with a null check, one `var` per value with type and current value as a comment, `using` lines from loaded namespaces; also the plain path list |
| `src/explorer/Explorer.tsx` | Header (game, connection, auto-refresh, fullscreen), path bar, inline vs fullscreen layout, model context, "Ask Claude" |
| `src/explorer/Tree.tsx`, `Preview.tsx` | The rows: kind-coloured values, struct previews as `k=v` pairs, object previews by their distinguishing fields and visible/hidden, count badges, slow/blocked/error markers, hover tick box; keyboard navigation |
| `src/explorer/Inspector.tsx` | Selected node: kind, type, namespace, full preview, path and C# (copyable), Re-root, Refresh, Watch 5 s (`watch_object`, hits highlighted in the tree), Full value (`eval_path`), Add to snippet, Send to Claude, Ask Claude |
| `src/explorer/PathBar.tsx` | Back / forward, breadcrumbs that re-root on click, editable path with Go |
| `src/explorer/Snippet.tsx` | The ticked values as C# or as paths, with Copy |

- **Calls:** `explore_object {path, depth: 1, offset, limit: 50, game}` per expansion / page / refresh; `watch_object {expression, durationMs: 5000, intervalMs: 250}`; `eval_path {expression}`. Nothing polls: auto-refresh (off by default) re-reads only the selected node every 2 s, because the bridge runs on the game thread with a time budget.
- **Row ids** are walker paths; members the walker cannot address (no `path`) get `parent#name` and show a lock. The C# accessor is always the server's `csharp`, never derived (dictionary keys are typed enums).
- **Layout:** inline: path bar, a 22 rem tree, the snippet panel (when something is ticked), then the inspector card. Fullscreen: the tree fills the height next to a 20-26 rem column with inspector and snippet; below `sm` the column stacks under the tree. The type column hides below `xs`; object rows then show the type as their preview.
- **Keyboard:** `/` focuses the filter; in the tree arrows move, Right/Left expand/collapse (Left on a leaf goes to the parent), Enter opens (or ticks a leaf), Space ticks, Home/End, Escape clears the filter, typing starts a filter.
- **Model context:** the selected node (path, C#, type, value, namespace) and the ticked paths, debounced and deduplicated, so "this value" in the next prompt needs no tool call. "Send to Claude" sends it immediately.

## Conventions

- **Pin exact versions, at least two weeks old** (supply chain). Change them with `-Npm "install -E ..."` so `package-lock.json` stays in sync. Install scripts are disabled in the image and in CI.
- **The bundle must stay self-contained:** no external origins, fonts or CDNs. The app declares no CSP domains and talks only to the host.
- **Keep the app working without optional host features.** `ui/message` hides "Ask Claude", fullscreen hides its button, and missing `structuredContent` falls back to parsing the JSON text.
