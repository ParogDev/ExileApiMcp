# Which tool answers which question

This server has ~90 tools. Start from the question; cheapest and most direct first. Prompts are workflows; packs are background reading (`knowledge topic=...`).

## Is everything up?
| Question | Tool |
|---|---|
| Which game runs, is the bridge reachable, can the user see the HUD (display on, game in front)? | `bridge_status` |
| Did plugins compile, what did they log? | `hud_plugins`, `hud_log` (offline) |
| The HUD is slow, laggy or not drawing | `hud_health_report` |

## Game state now
| Question | Tool |
|---|---|
| Player, area, entities, panels, stash, NPC dialog, map | `get_player`, `get_area`, `get_entities`, `deep_scan`, `get_ui_panels`, `get_stash`, `get_npc_dialog`, `get_map_data`, `get_map_image`, `get_all` |
| One value or member list at a path | `eval_path`, `describe_type` |
| What exists under an object, with the C# accessor | `explore_object`, `find_in_object` (`show_data_explorer` for the user) |
| Player stats (by Stats.dat key) | `stats_page`, `get_stat`, `get_player_stats_raw`; shared view with the user: `select_stat`, `set_stat_pinned`, `set_stats_filter`, `set_stats_view`, `show_player_stats` |
| Logic over live data | `run_csharp` (setting "Allow C# Scripts") |

## The HUD API (offline)
| Question | Tool |
|---|---|
| Members of a HUD type (incl. non-public), where a member is | `hud_type`, `hud_find_types` |
| Which memory a property reads | `hud_property_map` (PoE1; IL); on PoE2 use `hud_runtime_layout` |
| What a HUD update changed and what it breaks | `hud_api_diff` |

## Memory (read-only)
| Question | Tool |
|---|---|
| Struct vs live memory, unmapped ranges | `memory_layout` (`show_memory_view`) |
| Real offsets of an obfuscated PoE2 struct, property → offset | `hud_runtime_layout` |
| Raw bytes, where an address lives | `memory_read`, `memory_where` |
| Which bytes change when X happens | `watch_memory`, `watch_object` |
| Evidence across many objects | `memory_correlate`, `memory_population`, `memory_snapshot` / `memory_compare` |
| Code that touches a field (Ghidra) | `find_field_access`, `code_struct_layout` |
| Values in game data tables | `game_data`, `find_in_game_data` |
| Verified facts per game | `findings`, `verify_finding` |
Method: prompt `probe_memory`, packs `shared/memory-mapping`, `poe2/offsets`.

## With the user, in game
| Question | Tool |
|---|---|
| What does action X change? | prompt `guided_experiment`: `await_change` (instruction shown in game), `experiment_*` |
| The user may be away | `experiment_queue` (+ `experiment_queue_wait` in the background) |
| Show where to click | `highlight`; multi-step tasks: `guide_flow`; plain messages: `guide`, `guide_state` |
| Learn while they play | `observe`, `observe_wait`, `observe_events` |
| Record and replay a session | `record_*`, `recording_*` |
Pack `shared/working-with-users`. Never send input.

## Plugin development
| Question | Tool |
|---|---|
| Edit → compile → verify | `reload_plugin` (`perf=true` for cost before/after); prompt `plugin_dev_loop` |
| Settings, perf, cross-plugin API of loaded plugins | `hud_plugin_settings`, `hud_plugin_perf`, `plugin_bridge_methods` |
| Expensive HUD calls or allocations on Tick/Render (offline) | `hud_plugin_lint` |
| Per-frame cost, GC, frame timing | `pipeline_trace` |
| Which method is hot / allocates | `profile_plugin` (also `assembly=` for PoE1's core) |
| The bridge's own cost | `bridge_self_perf` |
Prompt `optimize_plugin`, packs `shared/api-costs`, `shared/dev-loop`.

## Overlay accuracy
| Question | Tool |
|---|---|
| How far drawings are from fresh memory, per frame | `overlay_accuracy` (`delayMs`, `path=`, `entityId=`) |
| How far from the image | `tools\fidelity` (FidelityLab) in the scaffolding |
| Start when someone walks by | `await_motion` |
| Better renderers to compare | `render_lab` (walls, path, bars; `compare=true` for a path A/B) |
Pack `shared/render-fidelity`.
