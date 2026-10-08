# Player stats (both games)

How stats are identified, how resistances are layered and capped, and how the shared stats view works.

## Identity
- **Stats are keyed by their Stats.dat key,** for example `fire_damage_resistance_%`.
  - The key is stable across patches and the same in both games.
  - The numeric id and the `GameStat` enum name are not stable: PoE1 has about 23k enum members and PoE2 about 27k, and the numbering shifts every patch. Never persist ids.
- **Metadata:** `Files.Stats.recordsById[id]` gives `Key`, `IsLocal` (weapon-local) and the record `Type`.
- **Text:** `StatDescriptions` translates `(stat, value)` to in-game text. PoE2 adds link markup; see the poe2 api-differences pack.
- **A stat missing from the player's dictionary counts as 0.**

## Resistances
- **Elemental caps are 75% by default.** `maximum_*_damage_resistance_%` stats raise them; PoE2 often doesn't expose these in the player's dictionary.
- **PoE2 shows three layers:** `base_*_resistance_%`, `*_resistance_%` and `uncapped_*_resistance_%`.
  - At low level all three can be equal.
  - Over-cap is `uncapped - cap`. Negative resistance is real, for example the act penalties.
- **Chaos resistance** is often simply absent, meaning 0.

## The shared stats view (HUD panel, MCP App, agents)
- **Ownership:** the AI Bridge plugin owns `StatsUiState` (pins, filter, category, selection, sort) and persists it with its settings.
- **Revisions:** every change bumps `rev`. `stats_ui_state(sinceRev)` returns `{unchanged:true}` when nothing changed.
- **Mutators are intent-level and idempotent:** `set_stat_pinned`, `set_stats_filter`, `select_stat`, `set_stats_view`. Each takes an optional `expectedRev`, which fails with `rev_mismatch` if the state moved on.
- **Point the user at what you're discussing.** `select_stat` and `set_stat_pinned` show up in the in-game panel and in the stats app within about 1 s. The view state is scratch, so changing it is fine.
