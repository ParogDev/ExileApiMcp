# ExileCore2 (PoE2) vs ExileCore (PoE1)

Most ExileCore knowledge carries over; these differences don't. Confirm member names with `hud_type` (offline) rather than memory: ExileCore2 ships without source.

## Project and runtime
- **Target `net8.0-windows`.** The ExileCore2 host runs on .NET 8, and a net10 plugin will not load.
- **References:** `$(exilecore2Package)\ExileCore2.dll` and `GameOffsets2.dll`, not `$(exapiPackage)` / `ExileCore.dll`.
- **The PoE2 HUD rewrites `<TargetFramework>` in a plugin .csproj to net8 when it compiles.** A plugin that builds for both games must set the framework in `Directory.Build.props`.
- **The HUD builds plugins through its `Plugins\Source` junction path,** so `..\` paths in a .csproj resolve relative to `<HUD>\Plugins\Source`, not the repo.

## API
- **Namespaces:** `ExileCore2.*` and `GameOffsets2`. Common aliases:
  - `Color = System.Drawing.Color`;
  - `RectangleF = ExileCore2.Shared.RectangleF`;
  - `System.Numerics.Vector2/3` instead of SharpDX.
- **`Tick()` returns `void`,** not `Job`.
- **`Entity` exposes directly:** `Pos` (Vector3 world), `GridPos` (Vector2), `Buffs`, `Rarity`, `Path`/`Metadata`, `DistancePlayer`, and `Stats`.
  - Property names lose PoE1's `Num` suffix: `GridPosNum`/`PosNum`/`BoundsNum` become `GridPos`/`Pos`/`Bounds`.
- **`Entity.Stats` on PoE2 is the stat dictionary** (`Dictionary<GameStat,int>`). The Stats *component*, with `ActiveWeaponSetIndex` (weapon set 0/1), comes from `GetComponent<Stats>()`.
- **Skill names:** `ActorSkill.InternalName`, and the display name via `GrantedEffect.ActiveSkill`. PoE1's `EffectsPerLevel.SkillGemWrapper` path doesn't exist.
- **Graphics:**
  - `DrawBox(RectangleF, Color)` and `DrawFrame(RectangleF, Color, int)`.
  - For the minimap, use `GridToMap(grid, grid, VisibleSubMap.Large)` and draw in screen space. `DrawLineOnLargeMap` is obsolete.
- **UI panels missing on PoE2:** `MapDeviceWindow`, `VillageRewardWindow` (Settlers), `MercenaryEncounterWindow` and `ZanaMissionChoice`. Stash tabs have no `Affinity`.
- **Known-bad value:** `ServerData.DialogDepth` reads garbage on PoE2 (stale offset).

## Stats text
- **Translated stat text uses link markup** `[Target|Label]` or `[Label]`; show only the label.
- **Stats without a description** come back as `<unknown Name:value>`.

## Same on both games
- **The HUD internals agents use:** `Core.Current.GameController`, `Core.Current.pluginManager`, the internal `PluginManager.ReloadSourcePlugin(compiledPath, beforeReload)`, and `CorePluginSettings.AvoidLockingDllFiles`.
- **Plugin and settings basics:** `BaseSettingsPlugin<T>`, `ISettings` nodes (`ToggleNode`, `RangeNode<T>`, ...), and PluginBridge `SaveMethod`/`GetMethod`.
