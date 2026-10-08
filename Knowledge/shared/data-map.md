# Mapping HUD data (both HUDs)

How to find where a value lives in the live object model and turn it into plugin code. Paths verified on both HUDs, 2026-10-08. The entry points below are the same on PoE1. Where they differ (vector types, `PosNum`, UI panels, Life extras), see `shared/cross-game`.

## Workflow
1. **`explore_object path=GameController`**, then follow paths. One call gives one level as `name: type = preview` lines. `depth=2` expands nested objects (back-references such as `Owner`, and memory plumbing such as `M`, are listed but not expanded).
2. **`find_in_object`** finds a value by what you see in game. For example, `value=356` (the life shown) points at `Stats["MaximumLife"]`, `Life.CurHP`, `Life.MaxHP` and `Life.Health.Current`; `name=resist` lists every resistance member. Several hits for one number? Change it in game and use `watch_object`.
3. **`eval_path`** gives a full value, serialized 2 levels deep. A result over 64 KB fails (e.g. `GameController.IngameState`), so explore those instead.
4. **`watch_object`** finds the field that reflects an in-game event: run it while the user does the thing.
5. **Code:** the structured result of `explore_object` carries each node's null-safe C# (`GameController?.Player?.GetComponent<Life>()?.CurHP`) and its namespace (for the `using`). Copy it rather than guessing member names. `hud_type` shows declarations, including non-public ones.

## Entry points
| Path | What |
|---|---|
| `GameController.Player` | The player `Entity`. Its components are listed by `explore_object`, e.g. `Actor, Animated, Buffs, Inventories, Life, Pathfinding, Player, Positioned, Render, Stats, Targetable` |
| `GameController.Player.GetComponent<Life>()` | `CurHP/MaxHP/CurES/CurMana`. `Health`, `Mana`, `EnergyShield` and `Ward` are `VitalStruct` (`Current`, `Max`, `Unreserved`, `Reserved*`, `Regen`) |
| `GameController.Player.Stats` | `Dictionary<GameStat, Int32>` of about 270 entries. C#: `Stats?[GameStat.MaximumLife]`; walker: `Stats["MaximumLife"]`. The `stats_page` / `get_stat` tools give the same data by Stats.dat key with in-game text |
| `GameController.Player.Buffs` | `List<Buff>`: `Name`, `BuffDefinition.Id`, `Timer`/`MaxTime` (Infinity = permanent), `BuffCharges`, `SourceEntity` |
| `GameController.Player.GridPos` / `.Pos` | `Vector2` grid cell / `Vector3` world position. `get_map_image` uses grid coordinates |
| `GameController.Entities` | Every loaded entity, about 600 in a town, mostly doodads. Filter by `Type`, `Path` (metadata) or `RenderName` in `run_csharp`; `get_entities` already filters by distance |
| `GameController.IngameState.IngameUi` | UI panels as `Element`s. The preview shows `visible`/`hidden`; `Children[i]` walks the element tree |
| `GameController.Area` / `.Files` / `.PluginBridge` | Area info, game data files, the cross-plugin API (`plugin_bridge_methods` lists it) |

## Gotchas
- **Component names in `CacheComp` without a HUD wrapper type** (PoE2: `BuffOrbs`, `PlayerClass`, `BaseEvents`, `InteractionAction`) can't be read with `GetComponent<T>()`. Read their memory with `run_csharp` (see `poe2/stats-layout` for the method).
- **`Entity.Stats` (dictionary) is not the `Stats` component.** The component is the raw stat container; the dictionary is its decoded view.
- **Nested indexers (`a[0][1]`) aren't walker syntax.** Name the member in between (`Children[3].Children[1]`).
- **Exploring reads getters on the game's main thread under a 60 ms budget.** Members not read in time are listed under `skipped`; explore them by path. Getters that took 5 ms or more are marked `slow`; avoid calling them every frame in a plugin.
