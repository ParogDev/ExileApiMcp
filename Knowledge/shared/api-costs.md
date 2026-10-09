# What HUD API calls cost (write fast plugins)

Measured on PoE2 (ExileCore2), 2026-10-09, in town with 610 entities loaded: `run_csharp` timing 2,000-20,000 calls each on a nearby player. These are main-thread costs while the frame cache is warm. Ratios matter more than absolute numbers; re-measure on PoE1 before relying on exact values.

| Call | ns | Cheaper equivalent |
|---|---|---|
| `GameController.Entities` (enumerate) | **11,900** | `EntityListWrapper.ValidEntitiesByType[type]` (6 ns, then iterate the list) |
| `Entity.DistancePlayer` | **837** | cache the player's grid position once per frame and compute the distance yourself; or test the cheap properties (`Path`, `Type`) first and only then the distance |
| `Entity.GridPos` | 394 | `Positioned.GridPosition` on a cached component (70) |
| `Camera.WorldToScreen` | 391 | `Camera.Snapshot` once per frame (134), then `snapshot.WorldToScreen` per point |
| `Life.HPPercentage` | 329 | `CurHP` / `MaxHP` (61 each) |
| `Entity.Pos` | 319 | `Render.Pos` on a cached `Render` (37), **9x** faster |
| `Entity.IsAlive` | 314 | check once per entity per frame, keep the result |
| `Entity.Stats` | 254 | read once per frame per entity; it's a dictionary |
| `Entity.Buffs` | 190 | the same |
| `Entity.GetComponent<T>()` | 150 | look it up once and keep the reference: it returns the same object every time for the entity's lifetime |
| `Positioned.GridPosition` | 70 | |
| `Life.CurHP`, `Life.MaxES` | 60 | |
| `Render.Pos`, `Render.Bounds` | 37 | |
| `Entity.HasComponent<T>()` | 22 | |
| `Entity.Path`, `Type`, `Rarity`, `IsValid` | 5-7 | cached on the entity: filter on these first |

## Patterns
- **Filter cheap, then expensive.** Test `Path`/`Type`/`Rarity`/`IsValid` before anything that reads a component, especially `DistancePlayer`. (Whats An Azmeri Wisp: 2.9 → 0.1 ms per frame.)
- **Classify slowly, project per frame.** Category, mods and buffs change rarely: compute them at 10-20 Hz. Only positions need every frame, and use `Render.Pos` for those.
- **Hold component references** (`Life`, `Render`, `Positioned`) per tracked entity instead of calling `GetComponent` or `Entity.*` conveniences repeatedly.
- **One camera snapshot per frame** for every projection in that frame. It's also consistent: every point uses the same camera.
- At TargetFps 200+ every per-frame cost is multiplied by ~3.5. Measure with `pipeline_trace` (plugin Tick/Render) and `profile_plugin` (per method). See `shared/render-fidelity`.
