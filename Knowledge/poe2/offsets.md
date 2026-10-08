# PoE2 memory offsets: GameOffsets2 is obfuscated

**Don't trust `[FieldOffset]` values in `GameOffsets2.dll`.** Many of the published offsets are decoys. For example, `GameOffsets2.StatsComponentOffsets.ActiveWeaponSetIndex` is declared at `0x7F3F028F`, about 2 GB into a struct. `hud_type` flags offsets like this as `suspect`.

PoE1's `GameOffsets.dll` is genuine. For example, `StatsComponentOffsets.SubStatsPtr @0x20` is real; the stat vector is a StdVector of (id, int) at SubStats `+0xF0`.

## What to do instead
1. **Use the HUD's managed API, not raw offsets.** ExileCore2 itself reads the real layout, so `GetComponent<Stats>()`, `Entity.Stats` and the like are correct even when the published struct isn't.
2. **To see a value,** use `eval_path` (paths from `GameController`), or `run_csharp` with reflection on the component object.
3. **To recover a real layout,** use the Ghidra project (both clients are analysed, under `Documents\Ghidra`). Anchor on the component's name string (for example `"Stats"`), follow xrefs to the constructor and vtable, and compare against the PoE1 layout. Confirm live before relying on it.
4. **Record confirmed offsets** as Ghidra comments and in this knowledge pack, together with an AOB signature so the next patch can find them again.

## Rules
- Analyse only a **copy** of `PathOfExile.exe`, never the live install.
- **No debugger attach, injection or extra handles on the live game.** Verify live only through the HUD bridge.
