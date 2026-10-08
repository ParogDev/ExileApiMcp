# PoE2 Stats component: real memory layout and resistance math

The real Stats layout in the PoE2 client, recovered live through the HUD (read-only `run_csharp` memory reads) and confirmed in Ghidra. Client build 20261007-984e7bb2; offsets move with patches, so re-verify with the recipe below.

## Layout (client 20261007-984e7bb2)

**Stats component** (allocated as 0x200 bytes):

| Offset | Content |
|---|---|
| `+0x000` | vtable |
| `+0x008` | owner `Entity*`, as in PoE1 |
| `+0x1C8` | player `StatContainer*`. A make_shared pair: `+0x1D0` is the control block |
| `+0x1D8` | weapon-set `StatContainer*`, with its control block at `+0x1E0`. `+0x160` points to the same object |

**StatContainer** (same vtable for the player and weapon-set containers):

| Offset | Content |
|---|---|
| `+0x0E8` | shared_ptr to its **stats source** (below) |
| `+0x0F8` / `+0x100` / `+0x108` | begin / end / capacity of `std::vector<{int32 id; int32 value}>`. ids are Stats.dat row indexes, **sorted and unique** |

**Reference values, live on 2026-10-08:**
- player container: 244 pairs, including `level`=16 and `fire_damage_resistance_%`=26;
- weapon-set container: 72 pairs with the weapon-local stats (`local_physical_damage_+%`, reload, ...);
- ExileCore2's `StatDictionary` (271 entries) is these merged.
- A third vector at **player container `+0x238`** holds per-hand stats (`main_hand_*` / `off_hand_*` damage, 82 pairs).

**Stats source** (hierarchical; the lookup recurses into a parent):

| Offset | Content |
|---|---|
| `+0x0F8` | parent source; lookups fall through to it |
| `+0x1E8` / `+0x1F0` | sorted 0x28-byte records `{int32 id; int32 pad; int32 value @+8; contributions begin/end/cap @+0x10/+0x18/+0x20}` |
| `+0x260` | bitset of the stat ids this level defines (PoE2 has 0x6A91 stats) |

**PoE1, for comparison:**
- Stats+0x20 → SubStats; vector at `+0xF0`.
- PoE1's GameOffsets are real.
- PoE2's `GameOffsets2.StatsComponentOffsets` is a decoy: it declares `ActiveWeaponSetIndex` at `0x7F3F028F`.

## Resistance math (decompiled; Ghidra `CalculateFireResistance`, `SumMaxFireResTerms`)

```
fire_damage_resistance_% =
  0                                    if your_elemental_resistances_do_not_exist
  fire_resist_override_from_parent     if use_fire_resist_override_from_parent      (minions)
  min( max(uncapped_fire_damage_resistance_%, -200), cap )
cap = elemental_resistances_are_limited_by_highest_maximum_elemental_resistance
        ? highest_maximum_elemental_resistance_%
        : maximum_fire_damage_resistance_%
```

- **Resistances floor at -200%.**
- **`maximum_fire_damage_resistance_%` is itself derived.** It sums `base_maximum_fire_damage_resistance_%` and several keystone or override terms.
- **The default caps are not in the player's stat vector.** They come from the stats source: on the live character, `maximum_{fire,cold,lightning,chaos}_damage_resistance_%` = **75** in the StatContainer's source at `+0xE8`, with no contributions. Read the cap from there instead of assuming 75.

## Verify or re-find after a patch

1. **Live, read-only, with `run_csharp`.** Scan the Stats component (`GetComponent<Stats>().Address`) for pointers to an object whose `+0xF8`/`+0x100` pair bounds a vector of 8-byte `(id, value)` pairs containing `level`. That's id 1; resolve ids with `GameController.Files.Stats.recordsById`.
2. **Static, with Ghidra.** Take the container's vtable from `*(container)`, convert it to a Ghidra address (`0x140000000 + (runtime - GameController.Memory.AddressOfProcess)`), and follow the xrefs to the constructors.
3. **AOB** for the Stats factory's container setup (PoE2 only; it does not match PoE1). The int32s at match+54, +61 and +68 are the player-container, control-block and weapon-container offsets:
   ```
   C7 40 08 01 00 00 00 C7 40 0C 01 00 00 00 48 8D 05 ?? ?? ?? ?? 48 89 03 48 8D 55 10 48 8D 4B 10
   4C 8D 44 24 ?? E8 ?? ?? ?? ?? EB ?? 49 8B DE 48 8D 43 10 48 89 87 ?? ?? ?? ?? 48 89 9F ?? ?? ?? ??
   4C 89 B7 ?? ?? ?? ??
   ```
4. **Stat-id immediates** find the calculators: for example `MOV EDX, 0x8b` (`maximum_fire_damage_resistance_%`) inside a function whose result is cached at `ctx[0] + statId*4`.

**Rules:** only analyse a copy of the client in Ghidra; live checks are memory reads through the HUD, never writes or input.
