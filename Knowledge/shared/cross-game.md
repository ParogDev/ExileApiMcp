# Same data, two HUDs (PoE1 ExileCore vs PoE2 ExileCore2)

What differs when you read the same game data from both HUDs, and how to write code that works in both. Verified live on both HUDs on 2026-10-08 by running `explore_object` on the same paths (PoE1 Witch, PoE2 Mercenary). Project-level differences (framework, references, `Tick`, graphics calls) are in `poe2/api-differences`.

## Rules for code that runs in both
1. **Keep shared code free of HUD types.** Put anything that touches a differing member in per-game partial classes that declare the same members (the AI Bridge's `Shared/` + `Poe1/` + `Poe2/` layout). Namespaces come from per-game global usings.
2. **Positions: use `System.Numerics` on both.** PoE1 `Entity.Pos`, `GridPos` and `BoundsCenterPos` are **SharpDX** vectors; `PosNum`, `GridPosNum` and `BoundsCenterPosNum` hold the same values as `System.Numerics`. PoE2 has only `Pos`, `GridPos` and `BoundsCenterPos`, already `System.Numerics`. So: PoE1 `PosNum` = PoE2 `Pos`, behind one per-game accessor.
3. **Colors:** PoE1 UI elements use `SharpDX.ColorBGRA` (e.g. `Element.BgColor`); PoE2 uses `System.Drawing.Color`.
4. **Stats:** `Entity.Stats` is `Dictionary<GameStat, Int32>` on both, and common keys share names (`MaximumLife`, `FireDamageResistancePct`, `UncappedFireDamageResistancePct`, `BaseFireDamageResistancePct`...). But `GameStat` is a separate enum per game: a key that exists on one may not compile on the other. Use `TryGetValue`, never the indexer, for keys you haven't checked on both (`find_in_object path=GameController.Player.Stats name=<part> game=poe1|poe2`).
5. **Max resistance is not in `Entity.Stats` at the default cap, on either game.** PoE1 at 75/75/75 has no `Maximum*ResistancePct` key. The 75% base lives in the stats source, not the player stat vector (see `poe2/stats-layout`). Treat a missing key as "default cap", and say so in the UI; the stats panel shows "75% cap assumed".
6. **Feature-test, don't assume.** Use `HasComponent<T>()` or a null check for anything game-specific, and omit what a game lacks; never fake a default.

## Verified differences
| Path | PoE1 only | PoE2 only |
|---|---|---|
| `GameController` | `SleepingEntityListWrapper`, `ElapsedMs`, `MultiThreadManager`, `Disposed` | `IsUsingController`, `IsWaitingForDelayedAreaChange` |
| `Entity` | `PosNum`, `GridPosNum`, `BoundsCenterPosNum` (Numerics twins), `TheGame` | none |
| Player components | none | `BuffOrbs` (no HUD type) |
| Doodad components | `StateMachine` | `HideoutDoodad`, `Preload` |
| `Life` | none | `Ward`, `Invulnerable`, `SpiritPerWeaponSet` |
| `Life.Health` (`VitalStruct`, in `GameOffsets` / `GameOffsets2`) | none | `RegenPerMinuteStat`, `NoRegenStat` |
| `Buff` | `Charges` (Byte; `BuffCharges` exists on both) | none |
| `IngameUi` | League and atlas panels: `Atlas`, `AtlasPanel`, `MapDeviceWindow`, `DelveWindow`, `HeistWindow`, `HarvestWindow`, `BetrayalWindow`/`SyndicatePanel`, `IncursionWindow`, `MirageWishesPanel`, `NecropolisMonsterPanel`, `Village*` (Settlers), `Voyage*`, `Labyrinth*`, `ZanaMissionChoice`, `GemLvlUpPanel`, quest methods `GetQuests()`... | `SkillsWindow`, `Gemcutting*`, `ReforgingBench`, `SalvageBench`, `DisenchantWindow`, `Expedition2Window`, `Temple*`, `WellOfSoulsWindow`, `MapReceptacleWindow`, `RitualStateElement`, quest properties `QuestStates`, `CompletedQuests`... |

**Identical on both:**
- `Life.CurHP/MaxHP/CurES/MaxES/CurMana/MaxMana`;
- the `Health`/`Mana`/`EnergyShield` `VitalStruct`s (`Current`, `Max`, `Unreserved`, `Reserved*`, `Regen`);
- `Buff.Name/Timer/MaxTime/BuffCharges/BuffDefinition`;
- the player component set (`Actor, Animated, Buffs, Inventories, Life, Pathfinding, Player, Positioned, Render, Stats, Targetable`);
- `PlayerClass` without a HUD type;
- `GameController.Entities` (a `ReadOnlyCollection<Entity>`).

## Tool behaviour across the games
- **All MCP tools work on both,** with `game=poe1|poe2`. The smoke test passes on both HUDs.
- **`get_map_image` works on both games.**
  - PoE2's Radar registers `Radar.GetMapImage`, which the tool uses. PoE1's Radar registers only `Radar.LookForRoute` and `Radar.ClusterTarget`.
  - Without `GetMapImage`, the bridge draws the area from `IngameState.Data.RawPathfindingData` and reports `source: "pathfinding"`.
  - The pathfinding grid is `int[][]` indexed **`[y][x]`**, with values 0 = blocked and 1-5 = walkable (5 = open floor, 1-4 = near a wall). The PoE1 hideout is 760×759.
- **`run_csharp` needs "Allow C# Scripts"** ticked per HUD; `reload_plugin` needs "Avoid locking plugin dlls" per HUD.
- **`TheGame`** appears as a member on many PoE1 objects; it's plumbing, and `explore_object` doesn't expand it.
