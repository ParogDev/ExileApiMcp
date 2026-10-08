# Checking and extending memory mappings (both HUDs)

How to see what the HUD maps in a game struct, what it doesn't, and confirm it in Ghidra. Use it after a patch, or to find a member the HUD lacks (the stash tab affinity bit mask was found this way). Worked example verified on PoE1, 2026-10-08. All of this is read-only toward the game.

## Tools
- **`memory_layout path=<object>`** overlays the struct the HUD itself reads on live memory.
  - The struct is found by reflection: the wrapper's private `CachedValue<T>` field, e.g. `Life._life` → `LifeComponentOffsets`. On PoE2 that is the real, obfuscated struct (`t44615`), not the `GameOffsets2` decoys.
  - You get each mapped field with a sanity check and set bits for flag fields, plus the unmapped ranges and the **structure candidates** inside them: `std::vector` triplets, vtables, `self` pointers, pointers to objects, text.
  - `extend=N` reads past the struct's declared end.
- **`memory_read address=<hex>`** gives any region as classified 8-byte slots. Use it to follow pointers.
- **`watch_memory path=<object>`** shows the bytes and bits that change while the user does one thing in game, labelled with field names. **Unmapped changes are the discoveries.**
- **`memory_where address=<hex>`** tells you module, section and RVA, or heap.
- **`show_memory_view`** opens the same map as an interactive view for the user.

## Ghidra
- **Addresses:** `ghidra` in the results = the exe's image base (0x140000000) + RVA, which is the address in the Ghidra project. The project's PoE1 snapshot must match the installed exe; `tools/ghidra-analyze.ps1` labels snapshots `<date>-<sha8>`.
- **Finding the layout:** a vtable at +0 leads to its constructor. Run `get_xrefs_to <vtable>` (the DATA reference is the constructor writing it), then `decompile_function`. The constructor writes every member in order, which is the ground truth for offsets and for what lies in the gaps.
- **REST without the MCP tools:** start `tools/ghidra-headless.ps1` in the background, then:
  - POST `open_project {"path": "<Documents>\\Ghidra\\projects\\PathOfExile.gpr"}`;
  - POST `load_program_from_project {"path": "/poe1/<snapshot>/PathOfExile_poe1_<snapshot>.exe"}`;
  - then call `get_xrefs_to?address=`, `decompile_function?address=` and `read_memory?address=&length=`.
  - Use bearer `GHIDRA_MCP_AUTH_TOKEN` on every call.

## Getting to empirical truth (the method)
Don't name a byte from one sample. Each step below produces counts and counterexamples:
1. **Hypothesis** from one observation (`memory_layout`, `watch_memory`).
2. **Population check:** `memory_correlate path=<collection> labels=[<known properties>]`.
   - It tests every bit of the range against each label (non-zero, each of its bits, each common value, "function of the value") and finds where the label itself is stored.
   - A finding needs ≥ 3 items on each side; single-item matches are coincidence-prone (text, pointers) and are only counted.
   - Near-misses list their counterexamples, so look at those items.
3. **One-variable experiment:** `memory_snapshot name=baseline`, the user changes exactly one thing, then `memory_snapshot name=after`, then `memory_compare names=[baseline, after]`. Change it back and snapshot again: the answer is the bit that flips on and back with nothing else.
4. **Look for counterexamples** in states the population doesn't cover (e.g. several affinities on one tab), then repeat 2.
5. **Confirm the meaning in Ghidra** where code tests the bit, and record it here.

## Worked example: PoE1 stash tab affinities (`ServerStashTabOffsets`)
- **`Affinity` (+63, UInt32) is a bit mask; a tab can hold several** (1, 2 and 4 seen).
- **Each affinity belongs to one tab only.** Ticking it elsewhere clears it from the previous tab, as `watch_memory` and the snapshots showed.
- **The bit index is the game's tab-type id.**
  - On all 9 tabs with a dedicated type, affinity bit = `TabType` value (population check): Currency 3, Unique 4, Divination 6, Essence 8, Delve 12, Blight 13, Metamorph 14, Delirium 15, Gem 18.
  - From tabs named after their affinity: Fragment 9, Flask 17.
  - The HUD's `InventoryTabType` enum names bits 7 and 16 "Quad" and "Folder", which are not affinities, and has no name for 11 or 21. Name those with the one-variable experiment.
- **`Flags` (+61):**
  - bit 6 = has an affinity (11 vs 60 tabs, 0 counterexamples, any number of affinities);
  - bit 1 = tab has a type (`TabType` ≠ 0; 63 vs 8, 0 counterexamples).
  - Neither is named by the HUD.
- **How it was found:** `watch_memory path=...PlayerStashTabs[33] size=96 durationMs=60000` while the user ticked and confirmed affinities in the tab's settings. The change lands only after the confirm button (the server applies it).
- **Pitfall: the tab's address can change.** The array was reallocated while the settings window was open. Watch by path, which is re-resolved on every sample, not by a stored address.

## Worked example: PoE1 `Life` (`LifeComponentOffsets`, 580 bytes mapped)
- **What the HUD maps (16 fields, all plausible):** `Owner` at +0x8, and the `Health`/`Mana`/`EnergyShield` values (`VitalStruct` fields) from +0x188/+0x1D8/+0x220 on.
- **Constructor** `FUN_141e678d0` (writes vtable `0x1435A7340`):
  - **+0x0 / +0x18 / +0x20:** vtables (multiple inheritance). `memory_layout` labels them `vtable`.
  - **+0x28:** a `std::vector` with inline storage at +0x40 (First = this+0x40). The same pattern is at +0xC0, with storage at +0xD8, holding 7 object pointers.
  - **+0x160:** the constructor's second argument. **+0x168, +0x170:** looked up in a table by indices read from that argument.
  - **+0x178 / +0x1C8 / +0x210:** the Health / Mana / ES sub-objects. Each is built by `FUN_141e65df0(sub, this, statId x5)`: its own vtable, then a back-pointer to the Life object (`self` in `memory_layout`), with the mapped `VitalStruct` fields after them. Health's stat ids are 0xF7, 0x58E, 0x791, 0x2546, 0x3C68.
  - **+0x258 .. +0x26E:** members past the HUD's 580-byte struct. Use `memory_layout ... extend=64` to see them.
- **Patterns to recognise:**
  - an object pointer whose target starts with a `.rdata` vtable;
  - std::vector = First ≤ Last ≤ End, all heap (inline storage right after it is common);
  - embedded sub-objects = vtable + back-pointer pairs;
  - flags = few bits set in a byte or int. For example, stash tab `Affinity` reads 4096 (bit 12) on a tab named "Delv"; what each bit means comes from toggling affinities while `watch_memory` runs.
