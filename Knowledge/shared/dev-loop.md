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

## Things that need a full HUD restart (`hud_restart reason=...`)
- a new plugin folder;
- a change to *Whats An AI Bridge* itself, which can't reload itself;
- HUD core updates.

**Restart only through `hud_restart`.** Several agents may share the HUD (Claude Code sessions in worktrees, Claude Desktop, scripts), and a restart during another agent's measurement or piloted test ruins it. The bridge knows every connected session by name (your MCP server identifies itself by its working directory's git branch) and what each is doing:
- The request **waits** while a measurement (`pipeline_trace`, `profile_plugin`, `hud_health_report`), a compile, a recording, a queued step, a guided flow, an instruction the user is acting on, or an explicit lease runs, and goes ahead when they finish. Nothing blocking: the in-game card counts down a few seconds with Not now.
- The user sees the request on the guide card with the blockers and can press **Restart now** (over them) or **Not now** (`denied`: say in chat what you need the restart for, or retry later). A second request **merges** into one already granted: wait for the HUD to come back, don't restart again.
- `hud_sessions` shows who else is connected and what they do. Before a before/after comparison or a piloted session that spans several calls, `hud_lease acquire` keeps others' restarts away; release it when done.
- While the HUD is down, bridge calls fail with "the HUD is being restarted by X (reason), retry": wait ~20 s instead of debugging.

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
