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

## Passive sources first (nobody needs to act)
Most questions about a struct are settled without the user. Use these in this order:
1. **`game_data`:** the game's own tables name ids, enums and bits, and say how they link. Row n is usually id n; foreign keys show as `File[row]`.
   - Stash examples: `StashTabAffinityId.dat` names all 22 affinity bits, and matched every experiment. `StashType.dat` names the 25 tab types; the HUD enum is outdated. `StashTabAffinities.dat` gives the UI order, and `StashTabAffinityByItemClassCategory.dat` maps item class → affinity.
   - **Unknown ids → `find_in_game_data`:** collect the field over a population and pass the values. Every loaded table is scanned, and the column holding (nearly) all of them names them. Unpack tagged values first.
     - Example: Map stash page +0 is `kind | value << 5`. Kind 0 values are `BaseItemTypes.dat` +140 (MapKeyTier1-16, Nightmare, Shaper Guardian), kind 1 values are a unique map key (`MapStashUniqueMapInfo.dat` +32), and kind 3 is a `MapStashSpecialSubstashGroup.dat` row. Findings `stash.child.page-key`.
   - Pointers into data rows also show as `data-row` in `memory_layout` (e.g. `Base` +24 → `ItemVisualIdentity.dat[...]`).
2. **`code_struct_layout`** on the struct's network (de)serializer or constructor (found with `find_field_access`): every member with its size and the flag that gates it, diffed with the HUD's struct.
   - For a stash tab, the deserializer `FUN_141d4d1f0` yields +0 (8 B, bit 5), +40 (inventory id), +58 (2 B), +60 (1 B, bit 4) as UNMAPPED, and Flags as 2 B vs the HUD's 1 B, with no experiment.
3. **`memory_correlate`** over everything loaded (all tabs, all entities): which bits follow known properties.
4. **Only then a guided experiment:** for what needs a change of state, or to settle what the above leave ambiguous.

The one thing the passive sources can't do is create state. Children and inventories only exist after a tab or stash has been opened, so ask the user to open it once.

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
- **All 17 PoE1 affinities, by bit** (verified 2026-10-08):

  | Bit | Affinity | Evidence |
  |---|---|---|
  | 3 | Currency | population: `TabType` 3 |
  | 4 | Unique | population: `TabType` 4 |
  | 5 | Map | experiment |
  | 6 | Divination Card | population: `TabType` 6 |
  | 7 | Settlers | experiment |
  | 8 | Essence | population: `TabType` 8 |
  | 9 | Fragment | tab named Fragments |
  | 10 | Sanctum | experiment |
  | 11 | Mercenary | experiment |
  | 12 | Delve | population: `TabType` 12 |
  | 13 | Blight | population: `TabType` 13 |
  | 14 | Ultimatum | experiment |
  | 15 | Delirium | population: `TabType` 15 |
  | 16 | Breach | experiment |
  | 17 | Flask | tab named Flask |
  | 18 | Gem | population: `TabType` 18 |
  | 21 | Ritual | experiment |

  - **How the experiment ran:** `memory_snapshot` of all tabs, the user ticked one affinity on a Normal tab and confirmed, `memory_compare`. Each step changed exactly one Affinity bit.
  - **Bits 19 and 20 are unused.**
  - **Don't name bits from the HUD's `InventoryTabType` enum.** It matches only where a dedicated tab type exists: bit 7 is "Quad" there (Settlers in game), 14 is "Metamorph" (the game's Ultimatum affinity; ticking Ultimatum took bit 14 from a Metamorph-type tab), and 16 is "Folder" (Breach).
- **A Normal (non-premium, `TabType` 0) tab can hold affinities:** one held 7 at once.
- **Explained later:** unmapped bytes +40 and +62 changed on a tab whose affinity didn't change during the first experiment step. That was the tab's inventory being loaded: +40 = inventory id, and +62 bit 1 = Flags bit 9.
- **What the code says** (`find_field_access offset=61 bit=6`, found in 158 s on first run, cached since):
  - The tab's network serializer `FUN_141d4d020` and deserializer `FUN_141d4d1f0` treat **Flags as 2 bytes (+61..+62)**. The HUD maps 1 byte, so the "unexplained" +62 changes were Flags' high byte.
  - They gate optional members on Flags bits:
    - **bit 6 → Affinity** (4 bytes at +63);
    - **bit 5 → an 8-byte value at +0** (zeroed when clear);
    - **bit 4 → a byte at +60**.
    - The values at +0 and +60 are unmapped by the HUD; their meaning is still unknown.
  - UI code `FUN_140a8e150` tests bit 6 to render the affinity line in bold, and tests bit 0 as well.
- **Child tabs and other Flags bits** (code plus population, 2026-10-08):
  - **Flags bit 5 = child tab**, e.g. a page inside the Map stash. **+58 (u16, unmapped by the HUD) = the parent tab's index**, `0xFFFF` for top-level tabs.
    - Evidence: all 27 Map pages point to the Map stash tab (index 23); the conversion code `FUN_14025ac30` picks the parent link by bit 5.
    - Child tabs exist only after their stash has been opened.
  - **Flags bit 0 = remove-only** (4 of 4 "(Remove-only)" tabs, no others).
  - **+40 = the id of the server inventory holding the tab's items; Flags bit 9 (byte +62 bit 1) = that inventory is loaded.**
    - Evidence: +40 ≠ 0 exactly when bit 9 is set (12/12, 0 counterexamples). The values are consecutive (0x9B-0xA6), assigned as tabs were opened, and each matches a `ServerData.PlayerInventories` `Id` of the right type (Currency, Gem, FlaskStash, MapStashInv for Map pages...).
    - Use: stash tab → its live items. The HUD's stash-tab struct doesn't expose this.
  - **+0 (8 bytes):** only on child tabs, a different value per Map page. Unchanged by viewing the page or moving a map out of it, so it's not view state and not derived from contents. Most likely a stable server-side page id.
  - **+60 (1 byte, gated by bit 4):** copied into the client's tab info but unused on 92 tabs (personal and guild). Possibly stash-folder data, since the HUD's tab-type enum has a Folder type; untested.
  - The client conversion loops over **22 affinity bits** (0-21).
- **`Flags` (+61):**
  - bit 6 = has an affinity (11 vs 60 tabs, 0 counterexamples, any number of affinities). It was also seen clearing when a tab's last affinity was taken;
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
