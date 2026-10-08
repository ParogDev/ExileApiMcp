# Plugin dev loop (both HUDs)

How a HUD turns plugin source into running code, and the fastest verified loop for an agent.

## How the HUD loads source plugins
- **Source plugins live in `<HUD>\Plugins\Source\<Folder>\`.** Each folder holds one `.csproj`, which the HUD compiles with MSBuild into `<HUD>\Plugins\Temp\<Folder>\<Assembly>.dll` and loads.
- **Compiles happen only at HUD startup, or when a plugin's Reload button in the HUD menu is pressed.** There is **no compile on save**: the HUD's file watcher only reloads compiled DLLs.
- **"Cache Plugin Compilation Results" (Core → Plugin Settings)** skips compiling unchanged plugins. The log then says `Skipping compilation of ... due to` cache, instead of `Plugins from directory X compiled and loaded`.
- **A failed compile writes `<Folder>\Errors.txt`, and the HUD never deletes it.** An `Errors.txt` older than the last successful compile is history, not a current error. `hud_plugins` flags these files as `stale`.
- **A new plugin folder is only discovered at HUD startup.**

## Fast loop (verified on PoE2, 2026-10-08)
1. **Edit** the source, optionally running `dotnet build` on the plugin first to catch compile errors without touching the HUD.
2. **`reload_plugin`** does the Reload-button step in the running HUD.
   - It returns `ok`, compiler `diagnostics` (file/line/col/code), and what the plugin logged on load.
   - A typical reload takes 0.2–0.6 s with the compile cache, more after real changes.
3. **Check the log:** `hud_log plugin=<folder> level=error`.
4. **Verify live** with the `get_*` tools, `eval_path`, or `run_csharp`.

## Things that need a full HUD restart (`tools/restart-hud.ps1 -Game poe1|poe2` in the scaffolding repo)
- a new plugin folder;
- a change to *Whats An AI Bridge* itself, which can't reload itself;
- HUD core updates.

## The setting reloads depend on
**Core → Plugin Settings → "Avoid locking plugin dlls"** is off by default on both HUDs.
- **Off:** each plugin DLL is loaded from its file and stays locked. Recompiling changed code then fails to copy the new DLL, and by then the HUD has already unloaded the plugin, so it stays off until a restart. The bridge refuses such reloads with `dll_locked`.
- **On:** DLLs load from a memory stream, and reloads always work. It applies to plugins loaded after it is turned on, so restart once after ticking it.

## Logs
- **Location:** `<HUD>\Logs\`.
  - **PoE1** writes `Error*/Warning*/Info*/Verbose*.log` per day.
  - **PoE2** writes only `Verbose*.log`, which holds every level.
- **Run boundaries:** each HUD run starts with `=============== Start hud at ...` (PoE1) or `Start ExileCore2 at ...` (PoE2) and ends with `Close ...`.
- **Plugin lines:** plugins log as `[Folder] message`, `Project -> message`, or `Project, Method -> exception`.
- `hud_log` reads the latest run, collapses repeated lines, shortens paths outside the HUD and drops .NET runtime stack frames.

## Plugin code rules that bite
- **ASCII only in string literals:** non-ASCII renders as `?` in the HUD.
- **DTO classes + Newtonsoft `JsonConvert`** for any JSON; never build JSON by hand.
- **Never send synthetic input** (F13–F24 or any SendInput from tools): anti-cheat risk.
