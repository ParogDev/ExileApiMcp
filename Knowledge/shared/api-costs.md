# What HUD API calls cost (write fast plugins)

Measured on PoE2 (ExileCore2), 2026-10-09, in town with ~600 entities loaded, with `run_csharp`. **Two numbers per call:**
- **First per entity per frame** (what a plugin loop pays): one pass over all valid entities, each call made once per entity. This is the number that matters.
- **Warm**: the same call repeated on one object within a frame, after the HUD's per-frame caches are filled. It's only this cheap on repeats.

| Call | first per entity (ns) | warm (ns) | Cheaper equivalent |
|---|---|---|---|
| `Entity.Stats` | **23,000-28,000** | 254 | read only for entities you've already classified as interesting, and at 10-20 Hz |
| `Entity.Buffs` | **5,500-8,700** | 190 | the same |
| `Entity.DistancePlayer` | **2,900** | 837 | **exactly** the distance between `Positioned.GridPosition` of the entity and of the player (verified on all 597 entities, PoE2): read the player's once per frame and compare squared distances. Test cheap members first anyway |
| `Entity.Pos` | **2,300-2,400** | 319 | **exactly** `Render.Pos + (0, 0, Render.Bounds.Z)` on a held `Render` (verified on all 535 entities, PoE2): ~7x cheaper. `Render.Pos` alone is offset in Z by the bounds height |
| `Entity.GridPos` | 530 | 394 | `Positioned.GridPosition` on a held component |
| `Entity.IsAlive` | 100-280 | 314 | |
| `GetComponent<Render>()` | 230-250 | 150 | hold the reference: it returns the same object for the entity's lifetime |
| `GetComponent<Life>()` / `Life.CurHP` | 300-400 (3,000+ on a cold entity) | 150 / 60 | |
| `Camera.WorldToScreen` | - | 391 | `Camera.Snapshot` once per frame (134), then `snapshot.WorldToScreen` per point |
| `Life.HPPercentage` | - | 329 | `CurHP` / `MaxHP` (60 each) |
| `GameController.Entities` (enumerate) | - | 11,900 per enumeration | `EntityListWrapper.ValidEntitiesByType[type]` (6 ns, then iterate) |
| `Entity.Path`, `Type`, `IsValid`, `Rarity` | 8-56 | 5-7 | cached on the entity: filter on these first |

**Worked example.** Whats An Azmeri Wisp read `DistancePlayer` on every entity (~550) in two passes: 550 x 2.9 µs x 2 ≈ 3.2 ms per frame, which matched the profile (2.9 ms). After filtering on `Path`/`Type` first and scanning at 20 Hz it was 33x cheaper.

## Allocation (garbage = GC pauses; research/hud-gc.md)
| Call | allocates | Instead |
|---|---|---|
| `Element.IsVisibleLocal` (first per element per frame) | ~3.6 KB (the HUD caches the whole element struct) | read the flag bit: PoE1 `Flags` +0x1E8, PoE2 +0x168, bit 11 (calibrate by matching `IsVisibleLocal`) |
| `Stats.StatDictionary` (first read per frame; cached within the frame) | ~110 B per stat: 36-44 KB and ~58 us for a player with 317 stats (PoE2) | read at 4 Hz or less and keep your own snapshot; compare it before rebuilding anything derived (the bridge returns the same list when no stat changed) |
| `Memory.Read<T>` on an address whose 4 KB page isn't cached this frame | ~3 KB (a page is rented, and most end up as garbage) | for scattered small reads, read from the leaf backend into a stack buffer (bridge `RawRead<T>`) |
| `Entity.Path.Split(...)`, string building per entity per frame | per entity | cache by path: string work once per path |

## Patterns
- **Filter cheap, then expensive.** `Path`/`Type`/`Rarity`/`IsValid` and your own cached classification come first; `DistancePlayer`, `Buffs`, `Stats` only for the few survivors.
- **Classify slowly, project per frame.** Category, mods, buffs and stats change rarely: compute them at 10-20 Hz. Positions need every frame: `Render.Pos + (0,0,Render.Bounds.Z)` on the held component equals `Entity.Pos`.
- **Hold component references** (`Life`, `Render`, `Positioned`) per tracked entity.
- **One camera snapshot per frame.** It's cheaper per point, and every point uses the same camera.
- **Lint before you profile:** `hud_plugin_lint plugin=<name>` lists these calls on Tick/Render paths offline, flags the ones in loops or per-item lambdas, and gives the cheaper equivalent. It's a static heuristic: code that only runs on demand (e.g. a query handler called from Render) shows up too, so confirm with `profile_plugin`.
- **Benchmark cold, not warm.** A loop repeating one call on one object measures the cache. Time one pass over many entities instead.
- At TargetFps 200+ every per-frame cost is multiplied by ~3.5. Measure with `pipeline_trace` (plugin Tick/Render) and `profile_plugin` (per method). See `shared/render-fidelity`.
