# PoE2 memory offsets: GameOffsets2 is obfuscated

**Don't trust `[FieldOffset]` values in `GameOffsets2.dll`.** Many of the published offsets are decoys. For example, `GameOffsets2.StatsComponentOffsets.ActiveWeaponSetIndex` is declared at `0x7F3F028F`, about 2 GB into a struct. `hud_type` flags offsets like this as `suspect`.

PoE1's `GameOffsets.dll` is genuine. For example, `StatsComponentOffsets.SubStatsPtr @0x20` is real; the stat vector is a StdVector of (id, int) at SubStats `+0xF0`.

## Runtime truth (2026-10-09)
The decoys are in the **metadata on disk** (what `hud_type` and MetadataLoadContext read). Inside the running HUD the CLR uses the real layout, and reflection there returns the real `[FieldOffset]`. `hud_runtime_layout` measures every field of a struct at runtime (sentinel bytes in a boxed instance), and with `path=` maps each property of the live object to its field and offset and checks it against fresh memory. Verified on PoE2: ServerStashTabOffsets (Flags +0x3D, TabType +0x34, Color +0x2C), Positioned (GridPosition +0x444, WorldPosition +0x490), Render t13978 (Pos +0x138, Bounds +0x144), Life t44615 (Max/Current: life +0x1DC/+0x1E0, mana +0x234/+0x238, ES +0x274/+0x278), Camera t32679 (Width +0x270, matrix +0x100). Use it before the Ghidra route below.
UI elements: the visibility flag (`IsVisibleLocal`) is bit 11 of the element's flags word at **+0x168** on PoE2 (PoE1: +0x1E8), found 2026-10-09 by matching `IsVisibleLocal` on all 124 children of IngameUi (one unique match).

## What to do instead
1. **Use the HUD's managed API, not raw offsets.** ExileCore2 itself reads the real layout, so `GetComponent<Stats>()`, `Entity.Stats` and the like are correct even when the published struct isn't.
2. **To see a value,** use `eval_path` (paths from `GameController`), or `run_csharp` with reflection on the component object.
3. **Recovered so far:** the Stats component, its stat containers and the resistance math, in `poe2/stats-layout`. That pack is also the worked example of the method.
4. **To recover a real layout,** combine live reads with the Ghidra project. Both clients are analysed, under `Documents\Ghidra`; start the server with `tools\ghidra-headless.ps1`, no GUI needed.
   1. Use `run_csharp` to scan the live object for the data you expect.
   2. Read the object's vtable (`*address`) and convert it to a Ghidra address: `0x140000000 + (runtime - Memory.AddressOfProcess)`.
   3. Follow the vtable's xrefs to the constructor, which shows every field it sets.
   4. Compare against the PoE1 layout, and confirm live before relying on it.
5. **Record confirmed offsets** as Ghidra comments and in this knowledge pack, together with an AOB signature so the next patch can find them again.

## Rules
- Analyse only a **copy** of `PathOfExile.exe`, never the live install.
- **No debugger attach, injection or extra handles on the live game.** Verify live only through the HUD bridge.
