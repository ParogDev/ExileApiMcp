# MCP App UIs (`ui://exile/*`)

Source for the interactive panels that ExileApiMcp serves as MCP Apps. The server embeds the built single-file HTML from `../ui/`; this folder exists only to produce it.

| Stack | TypeScript, React 19, Tailwind 4, Vite 8 + `vite-plugin-singlefile`, `@modelcontextprotocol/ext-apps` 2.0.1 |
|---|---|
| Toolchain | Docker only. No Node or npm on Windows; `node_modules` lives in a named Docker volume |
| Output | `../ui/player-stats.html` (`ui://exile/player-stats`), `../ui/data-explorer.html` (`ui://exile/data-explorer`) and `../ui/memory-view.html` (`ui://exile/memory-view`), all committed |

Three apps share `src/styles.css`, `src/components.tsx` and `src/icons.tsx`:

| App | Entry | Source | Opened by |
|---|---|---|---|
| Player stats | `player-stats.html` → `src/main.tsx` | `src/*.tsx`, `src/sync.ts` | `show_player_stats` |
| Data explorer | `data-explorer.html` → `src/explorer/main.tsx` | `src/explorer/*` | `show_data_explorer {path?, game?}` |
| Memory view | `memory-view.html` → `src/memory/main.tsx` | `src/memory/*` | `show_memory_view {path?, address?, type?, game?}` |

`vite build` takes one input per run (single-file plugin), so `npm run build` runs it four times: default (stats), `--mode explorer`, `--mode memory`, `--mode harness`. Tailwind scans the whole source tree, so a class added to one app can change another app's CSS: rebuild and commit all bundles together.

## Commands

From the scaffolding root (Windows PowerShell 5.1):

| What | Command |
|---|---|
| Build (typecheck, app, harness) and copy into `../ui/` | `powershell -NoProfile -ExecutionPolicy Bypass -File MCP\ExileApiMcp\ui-src\build.ps1` |
| Same, then serve the dev harness on http://127.0.0.1:5174/harness.html | `... build.ps1 -Serve` (blocks; run it in the background) |
| npm without npm, e.g. add a pinned package | `... build.ps1 -Npm "install -E pkg@1.2.3"` |
| Real host against the real server: ext-apps **basic-host** on http://localhost:8080 | start the server with `run.cmd --http`, then `... ui-src\basic-host.ps1` (`-Stop`, `-Rebuild`) |

After a UI build, restart the MCP server to embed the new HTML. `run.cmd` rebuilds on every start. **CI rebuilds the UI and fails if any of `ui/player-stats.html`, `ui/data-explorer.html` or `ui/memory-view.html` doesn't match its source**, so commit the rebuilt files with the source change.

## Three ways to see the apps, fastest first

1. **Dev harness** (`dist/harness.html`): a fake host plus a fake server per app. No game, HUD or server needed. Every state is reachable from the URL, so screenshots are deterministic. `app=stats` (default), `app=explorer` or `app=memory` picks the app; `bare=1` renders only the app's iframe, filling the viewport (pixel-exact captures in a narrow browser pane).
   ```
   /harness.html?app=stats&theme=dark&width=380&scenario=offline&latency=400&display=fullscreen&vars=none&simulate=0
   /harness.html?app=explorer&path=GameController.Player&theme=dark&width=380&scenario=flaky&bare=1
   /harness.html?app=memory&path=GameController.IngameState.ServerData.PlayerStashTabs[0]&theme=dark&width=380
   /harness.html?app=memory&address=0x41137889400&display=fullscreen
   ```
   - `scenario`: stats: `live`, `offline`, `not-in-game`, `empty`, `flaky` (30% errors plus jitter); explorer: `live`, `offline`, `flaky`; memory: those plus `ghidra-down` (every `find_field_access` fails like the real server does without the headless Ghidra).
   - `vars=none` drops the host style variables to test the app's own fallbacks.
   - `simulate=0` stops the simulated game (random vitals and stat changes; drifting Life values).
   - `path=` (explorer) is the `show_data_explorer` argument: where the tree opens. A bad path shows the error state.
   - `path=` / `address=` / `type=` (memory) are the `show_memory_view` arguments. Error states: a path below Life such as `...GetComponent<Life>().CurHP` → `no_address`; `GameController` → `no_struct`; `address=0x0` → `unreadable`.

   The **stats** fake server is built from a real PoE2 character (`dev/poe2-stats.json`). The **explorer** fake server (`dev/fakeExplore.ts`) answers `explore_object`, `show_data_explorer`, `watch_object` and `eval_path` from `dev/explore/*.json`: 11 real PoE2 responses captured live (0 GameController, 1 Player, 2 Life component, 3 Life.Health, 4 Player.Stats, 5 Player.Buffs, 6 Buffs[0], 7 IngameUi, 8 Player.Pos, 9 Entities, 10 Entities[2]). Any other path is synthesised from how its parent listed it, so every node expands; unknown members fail like the bridge ("No public property or field 'X' on type 'Y'"). States injected so they can be seen without a game: `Player.Mods` (getter throws), `Player.NativeHandle` (blocked), IngameUi's last members arrive as budget-`skipped` (the "Load them" row), `Stats` / `Entities` / `ChatMessages` page, Life values drift between reads (refresh, auto-refresh, watch).

   The **memory** fake server (`dev/fakeMemory.ts`) answers `show_memory_view`, `memory_layout`, `memory_read`, `memory_where`, `watch_memory`, `memory_population`, `memory_correlate`, `memory_compare`, `memory_snapshot`, `findings` and `verify_finding` from `dev/memory/*.json`, real PoE1 captures: `layout-life.json` (580-byte Life component: 16 mapped fields, 27 candidates, 64 bytes past the end), `layout-stashtab.json` (67-byte server stash tab: `Flags` bits 1,6, `Affinity` bit 12, inline UTF-16 name), `read-life.json` / `read-entity.json` (classified 8-byte slots; the entity is what Life's `Owner` pointer at +8 reaches), `where-vtable.json`, `watch-life.json` (nothing changed), `watch-stashtab.json` (a 60 s watch while stash tab affinities were toggled: `Flags` bit 6, `Affinity` bits 5, 10, 11 flipped — the hero example for the diff state), `population-stashtabs.json` (71 stash tabs × 67 bytes with Name / Affinity / TabType), `correlate-stashtabs.json` (`+61 bit 6 = Affinity != 0`, `+61 bit 1 = TabType != 0`, near misses at +47), `compare-affinity.json` + `snapshots-list.json` (the `aff-0-baseline … aff-7-mercenary` series: one affinity ticked on tab "19" per step, one Affinity bit each), `findings.json` and `verify-affinity.json`, and `find_field_access` from `field-access-stash-flags.json` (Flags +61 bit 6: 48 functions, the network serializer / deserializer pair gating +0x00, +0x3C and +0x3F on bits 5, 4, 6, a UI function testing bit 6) and `field-access-stash-affinity.json` (Affinity +63). Any other address reads as a synthesised object, so every pointer can be followed; Life's Health/Mana drift between reads; Life watches report the vitals plus an unmapped bit flip and a noisy timer; the stash tab watch applies its last bytes so the reload after it shows them; other collection paths fail like the bridge; snapshots saved in the harness join the list. Code lookups for other offsets are synthesised (a struct copy, the serializer pair, a few writers, a bit-tester for 1-byte fields) and "scan" first: `scanMs` (4 s) per offset not scanned yet, the stash tab's fields pre-scanned, so the long-running state is reachable (`harness.server.scanMs = 0` makes them instant); `minKnown` ≥ 4 on a synthesised offset gives the "no anchored functions" state, offset 0 the server's refusal.

   Agents can drive it from a browser console or JS tool through `window.harness`:
   ```js
   // stats
   harness.hud.pin("cold_damage_resistance_%")  // a change made in the in-game panel
   harness.hud.filter("life", "vitals"); harness.hud.select("level"); harness.hud.sort("value", true)
   harness.server.bumpRandomStat("fire_damage_resistance_%")  // value change -> delta + flash
   // explorer
   harness.server.bump()          // life drops by 60: refresh / watch show it
   harness.server.drift = false   // freeze values
   // memory
   harness.server.bump()            // life drops by 500: Live mode / Read flash the bytes
   harness.server.toggleAffinity()  // stash tab Flags bit 6 + Affinity bit 11
   harness.server.quiet = true      // watches report nothing (the empty-diff state)
   harness.server.scanMs = 0        // code lookups answer at once (default 4000 ms per unscanned offset)
   // all
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

## How the memory view works

A byte-level view of one game object for checking that the HUD's struct still fits live memory after a patch and for finding members the HUD doesn't map: what is mapped, what isn't, what in the unmapped bytes looks like structure, and what changes while the user does something in game.

| File | Role |
|---|---|
| `src/memory/main.tsx` | ext-apps wiring, same pattern as the other apps. Seeds the first view from `show_memory_view`'s result (`ontoolresult`: a `memory_layout` or `memory_read` result, or an error); `ontoolinput` gives the requested path / address / type and game |
| `src/memory/types.ts` | The `memory_layout` / `memory_read` / `memory_where` / `watch_memory` structuredContent shapes, and `find_field_access`'s. **Mirrors `Tools/MemoryTools.cs`, `Tools/CodeAccessTools.cs` and the bridge's `MemoryInspector`; change both together** |
| `src/memory/bytes.ts` | Pure helpers: the **region model** (one offset-ordered cover of the bytes: `field`, `cand`, `gap`, `slot`, collapsed `zeros`, with a per-byte cover array and nested-struct groups), BigInt address arithmetic, hex/decimal offsets, alternative interpretations of a byte range, .NET type decoding for live re-reads, and the mapping of `bitsFlipped` onto a selection |
| `src/memory/paint.ts` | One colour vocabulary: mapped = blue (`--color-m-field`), candidate = violet (`--color-m-cand`), changed = magenta (`--color-m-change`), heap pointer = teal, numbers/text reuse the explorer kinds, unmapped = hatched. Check states: unusual/suspicious = warning, invalid = danger |
| `src/memory/store.ts` | `MemoryStore`, framework-free: a history of views (a `layout` or a `read`, each with its target, crumb label, how it was reached and its region), selection and hover, watch results as per-byte changes, live re-reads, `memory_where`, toasts |
| `src/memory/MemoryView.tsx` | Header (game, struct, connection, Live, fullscreen), inline vs fullscreen layout, coverage summary chips, error states per bridge code, model context and "Ask Claude" prompts |
| `src/memory/TargetBar.tsx` | Back / forward, the pointer chain as crumbs (a followed pointer shows its source field), path-or-address input with Go, and the options row (struct type to overlay, `extend` for layouts, size for reads) |
| `src/memory/StructMap.tsx` | The **strip** (the whole region as one byte-accurate bar, change ticks above, struct end marked, dashed violet "used by code" ticks on unmapped bytes the code touches), the legend, and the **rows**: offset (hex + decimal), size, swatch, name with the nested prefix dimmed and a sticky group header per nested struct, type, live value, set bits, finding and code chips, check badge, Δ badge; double-click / Enter follows a pointer |
| `src/memory/HexView.tsx` | 16 bytes per row with an ASCII column; bytes tinted by what covers them, selection and hover shared with the map, watch changes in magenta by recency, live changes flash. Click selects the covering field / candidate / slot, or an aligned 8-byte window in a gap; shift-click widens |
| `src/memory/Inspector.tsx` | The selection: kind, name, offset, size, type, value and check, raw bytes, address and Ghidra address (copyable), **Follow pointer** / **Where is it?**, Send to Claude / Ask Claude, what changed here during the watch, a "used by code" note when another lookup saw code touch these bytes, the **bit grid** (one cell per bit, set bits lit, flipped bits outlined and labelled; **click a bit to pick it** for the code lookup, bits a hovered instruction names light up amber), and the same bytes read as uint/int 8-64, float, double, pointer, UTF-16, ASCII |
| `src/memory/codeModel.ts` | `find_field_access` read for a human, all pure: functions ranked (the ones testing the picked bit, then decompiled ones, then by known fields touched), a role guess from the pseudocode (`htons` → network serializer, `ntohs` → deserializer, `L"<` → UI text builder, read+write pairs on two bases → copies the struct), the excerpt **tokenised** (struct offsets through the base variable, with widths from casts, pointer strides from the signature and serializer helper sizes; masks on the target; other objects dimmed), **gates** pattern-matched from `if ((... + 0x3d & 0x40) != 0) {` blocks ("bit 6 set → +0x3F (4 B)"), and **code marks**: every offset the session's lookups saw code use, merged per struct, for the map |
| `src/memory/CodePanel.tsx` | The **Code** card for the selection: what will be looked up (`Flags +0x3D (61) bit 6`), "seen in code" cross-references from earlier lookups, the lookup with its long-running state (elapsed, estimated phase, Stop waiting, a note that later runs are instant), the result (functions, confidence, kinds, also-touched fields, kind-coloured instructions with copyable Ghidra addresses and bit chips, the annotated excerpt with HUD field names and "unmapped: the code uses it" links that select the bytes, the "Explained by the code" gates, heuristic-labelled), the states (Ghidra not running with the exact command to copy, offset 0, no anchored functions with minKnown / knownOffsets suggestions, snapshot missing, errors), options (minKnown, decompile), **Ask Claude to explain this field** and **Send to Claude** |
| `src/memory/WatchPanel.tsx` | Watch 5 / 15 / 60 s (`watch_memory` at 100 ms), progress while sampling, then the changed ranges sorted discoveries-first: unmapped ranges, then mapped ranges with bit flips, noisy ones last; each row shows first → last bytes, ×changes and the flipped bits as chips. Clicking a range selects its field (or the raw bytes) |
| `src/memory/findingsModel.ts` | Findings laid over a struct: which findings talk about the struct on screen (subject / check path), the offsets and bits their `where` names (`+61 bit 6`, `+63 (32-bit)`, `+0x178 / …`) and **bit tables** (`3 Currency, 4 Unique, …` on a `Subject.Field`), which name a field's bits |
| `src/memory/Population.tsx` | **Population** mode: every item of a collection as a row and every byte as a column on a canvas (200 × 1024 stays cheap), the HUD's struct as a ribbon over the columns, a heatmap of the bits a label explains (`memory_correlate`; faint = varies between items), and a side panel for the picked byte/bit: who has it set, the 8 bits of the byte, the explanation with evidence and counterexamples, a breakdown by each label's values, and every explained bit as chips. Rows regroup by a label; a picked bit sorts the groups that have it first |
| `src/memory/Experiments.tsx` | **Experiments** mode: saved snapshots (`memory_compare` with no names) picked in order, **Take a snapshot** (`memory_snapshot`), then the comparison as a timeline of steps and a matrix with one row per (item, byte) that ever changed and one column per step — bits turning on (▲) and off (▽), numbered relative to the covering field. The step detail decodes label deltas (`Affinity 0 → 32` = bit 5) and marks byte bits that match them |
| `src/memory/Findings.tsx` | **Findings** mode: the registry as a matrix with a status badge per game (verified / differs / unverified / n/a; dashed "hypothesis" when verified on the other game only), "to check" highlighted, per-game details (where, evidence, bit tables), **Verify on this game** (`verify_finding`: pass / moved / differs / fail with the record to paste into `Knowledge/findings.json`), manual checks shown as the experiment to run, "show in struct" |

- **Modes** (tabs under the header): **Struct** (one object), **Population** (every item of a collection), **Experiments** (snapshots compared step by step) and **Findings** (what is known, per game). Findings load once with the first struct view and annotate the map: a mapped field keeps its blue, but bits a finding names get violet chips (`b6 the tab has at least one affinity`), bit tables name the set bits (`Delve` instead of `bits 12`), and a gap a finding points at is painted violet like a candidate. The legend reads **HUD maps this** / **found, not mapped** / **unmapped**: that split is the point of the view.
- **Calls:** `memory_layout {path|address, type?, extend}` for a struct view, `memory_read {path|address, offset?, size}` for a raw view and for every followed pointer (256 bytes), `watch_memory {path|address, size: the region, durationMs, intervalMs: 100}`, `memory_where {address}`; `memory_population {path, labels, limit: 200}` then `memory_layout {address: items[0].address, type: struct}` and `memory_correlate {path, labels}` for a population; `memory_compare {}` / `{names}` and `memory_snapshot {name, path, labels}` for experiments; `findings {}` and `verify_finding {id}`; `find_field_access {offset, bit?, path | knownOffsets, minKnown?, decompile?}` from the Code card. **Live** mode (off by default) re-reads the region with one `memory_read` per second and decodes the mapped fields client-side from the bytes (`decodeDotNet`), because the bridge runs on the game thread; it pauses while a watch runs or the panel is hidden.
- **Code** (`find_field_access`, static analysis of the Ghidra copy of the exe, never the running game): the Code card looks up the selected field, candidate or gap byte, and the picked bit when one is picked in the bit grid (a bit beyond the first byte moves the lookup to its byte: `Affinity bit 12` → `+0x40 bit 4`). The path's HUD struct supplies the fingerprint; a view without a path passes the layout's field offsets as `knownOffsets`; a raw read without a struct can't look up. The call can't be aborted through the host, so **Stop waiting** stops the panel and keeps the result quietly when it lands (the server caches the scan). Results are kept per struct, offset and bit for the session (`snap.code`), and every result feeds the **code marks**: offsets the code touches that the HUD doesn't map get a dashed violet **used by code** tick on the strip and a `FUN_… · 8 B` chip on their row (a mapped field only gets a chip when the code reads it wider than the HUD's field); clicking one selects exactly the bytes the code uses. The `Explained by the code` list and the inline `unmapped: the code uses it` links are the discovery hook: the stash tab's serializer shows `bit 5 → +0x00 (8 B)`, `bit 4 → +0x3C (1 B)`, `bit 6 → +0x3F Affinity (4 B)`, and `htons(+0x3A)`.
- **Selection** is a byte range (`off`, `size`) plus the segment id when it is a field / candidate / slot, plus an optional picked bit (`bitSel`, numbered from the selection's first byte). The strip, the rows, the hex view, the watch list and the code links all set it; the inspector and the Code card read it. A changed range selects the one field that covers it, so the bit grid shows the flipped bits in the field's own numbering (`bitsRelativeTo`).
- **Layout:** inline: target bar, summary, the map card (strip, legend, filter All / Mapped / Candidates / Changed, rows at ≤ 20 rem), the inspector, the Code card, the hex card (collapsible), the watch card. Fullscreen: map | hex | inspector + code + watch at `lg`; map | (inspector, code, watch, hex) between `sm` and `lg`; stacked below. A watch that found changes switches the map filter to Changed once.
- **Keyboard:** in the rows, arrows / Home / End move the selection, Enter follows a pointer, Escape clears.
- **Model context:** the view (struct, address, size, coverage), the selection (offset, address, kind, name, type, value, bytes, check, Ghidra address, bits), the last watch's changed ranges and the selection's code lookup (top functions with roles and confidence, the gates, two excerpts), debounced and deduplicated. "Ask Claude" builds a question from the selection, e.g. "What is the unmapped std::vector at +40 in LifeComponentOffsets? Check it in Ghidra at 0x1435A64B0", and adds what changed there during the watch. The Code card's "Ask Claude to explain this field" sends the field, the ranked functions, the gates and the top two excerpts as fenced C, and asks what each bit means and what the unmapped offsets the code reads are.

## Conventions

- **Pin exact versions, at least two weeks old** (supply chain). Change them with `-Npm "install -E ..."` so `package-lock.json` stays in sync. Install scripts are disabled in the image and in CI.
- **The bundle must stay self-contained:** no external origins, fonts or CDNs. The app declares no CSP domains and talks only to the host.
- **Keep the app working without optional host features.** `ui/message` hides "Ask Claude", fullscreen hides its button, and missing `structuredContent` falls back to parsing the JSON text.
