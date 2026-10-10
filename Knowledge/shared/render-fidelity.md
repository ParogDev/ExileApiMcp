# Overlay accuracy and HUD performance (both HUDs)

How to find out why an overlay drawing (health bar, path, circle) doesn't sit on the game image, and where a plugin's frame time goes. Verified on PoE2, 2026-10-09; full write-up in the scaffolding's `research/render-fidelity.md` and `research/plugin-performance.md`.

## What we know (measured)
- **Data is fresh.** The camera and entity positions plugins use are 1-2 ms old when the frame is presented. An earlier "camera one frame old" claim was a measurement bug, now retracted.
- **Visible lag is mostly judder.**
  - At the default TargetFps 60 the HUD draws at ~57 fps, while the game image moves every few ms on a high-refresh display. Overlay markers stay on the image on average (bias ~0) but jitter by ~3 px at walking pan speed.
  - At TargetFps 240 (~200 fps) the jitter halves, but the overlay then **leads** the image by ~5-8 ms, the game's own image latency.
  - Drawing the fresh state from **~5 ms ago** (`delayMs`) removes the lead.
- **Plugins add their own effects.**
  - HealthBars: the player's bar has a 40 px deadzone at `PlayerSmoothingFactor` 1.0, and ImGui floors text to whole pixels.
  - Radar: the path is drawn from the live feet position to a plan made from the integer grid cell, so it starts **backwards in 63-80% of steps**, and it's jagged (74-92° of turning per 100 units, against 14-20 for a smoothed, trimmed path).
- **UI rects on the world map drift sideways as it pans** (PoE2 verified 2026-10-10, PoE1 unverified; finding `ui.worldmap.pan-x-underscaled`). For elements under `IngameUi.WorldMap[0]` (the pan container of the waypoint Teleport and caravan Travel maps), `GetClientRect().X` is off by `pan * (W/2560 - H/1600)`: the HUD scales x by H/1600, the game pans by W/2560. 31.7 px at the Act 2 map's pan limit (+-422.4) on 1920x1080; 0 on 16:10; vertical exact.
  - Fix: screen x = WorldMap.rect.X + pan.X * W/2560 + local.X * H/1600, y = WorldMap.rect.Y + (pan.Y + local.Y) * H/1600, where local = the sum of Position from the element up to (not including) WorldMap[0] (fixed per node). Read the **pan raw** every frame: two floats at WorldMap[0] +0x100 on PoE2 (finding `ui.worldmap.pan-offset`; calibrate by matching its Position at rest when the map opens), through the leaf memory backend, not the page cache. Everything cached trails a drag: `WorldMap[0].Position` and the rects built from it were 9.4 px avg, 52 px max behind a raw read while dragging.
  - Done in the bridge's highlights (`Shared/GuideHighlight.cs` `WorldMapPan` / `CalibratePan`, boxes moved every frame in `HighlightSnapshot`, per-game hint `WorldMapPanOffset`) and Whats A Route (`WorldMapReader.CalibratePan` / `FreshPan`). Any plugin drawing on that map needs it.
- **Per-frame plugin work scales with HUD fps.** Classify at a low fixed rate, and project positions per frame (Azmeri Wisp: 33x less CPU).

## Tools (most need the bridge setting "Allow HUD Instrumentation")
| Question | Tool |
|---|---|
| fps, frame cost, data age at Present, per-plugin Tick/Render cost | `pipeline_trace` |
| which method of a plugin is hot | `profile_plugin name=...` |
| how far HUD drawings are from fresh memory, per frame, in px | `overlay_accuracy` (`entityId=`, `path=` for a static anchor, `delayMs=`, `draw=true` for markers) |
| how far drawings are from the **image** | `tools\fidelity` (FidelityLab): `capture` + `measure` against a static object during a camera pan; `--follow` for walking players. Validated by `FidelityLab selftest` |
| start a measurement when someone walks by (without moving the character) | `await_motion` |
| a real offset when PoE2 hides it | `hud_runtime_layout path=...` (runtime reflection) |
| path drawing quality, offline | `render_lab compare=true target=waypoint` |
| try better renderers | `render_lab walls=true path=true` (raycast walls, smoothed glowing path, fresh and time-aligned) |

## Method
1. **Find the cost first.**
   - `pipeline_trace` while things move.
   - If a plugin's Tick or Render is large, run `profile_plugin` on it.
2. **Separate data error from display error.**
   - `overlay_accuracy` near 0 px but the user still sees wobble → it's frame rate or time alignment, not data.
   - Measure on the image with FidelityLab.
3. **Change one variable per run:** TargetFps, delayMs, a plugin setting. Compare bias (`lagPx.avg`) and jitter (`lagPx.sd`).

## Rules
- **The overlay is invisible in captures while the display is off** (Windows idle timeout): the HUD runs normally but isn't composited. `bridge_status` reports it under `desktop` (`displayLikelyOff`, `gameForeground`, with a warning). Check it before debugging "nothing draws".
- **The HUD overlay only shows while the game is the foreground window.** Focus it before capturing. Background games also throttle their frame rate.
- **Never move the character unless the user granted it for this session**, and then sparingly. Prefer `await_motion` and moments the user creates.
- **Runtime changes to HUD settings** (TargetFps via `run_csharp`) are saved when the HUD closes. Put them back.
