// In-memory stand-in for ExileApiMcp + the HUD bridge, for the dev harness.
// Mirrors the real tools' semantics (rev, sinceRev/unchanged, idempotent mutators, unknown_stat,
// ok:false results, JSON-RPC errors when the bridge is down) so UI states can be reproduced
// without a game running. Fixture: a real PoE2 level-16 character (dev/poe2-stats.json).

import type { CallToolResult } from "@modelcontextprotocol/client";
import fixture from "./poe2-stats.json";
import type { CategoryFilter, SortBy, StatItem, ViewState, Vitals } from "../src/types";

export type ScenarioName = "live" | "offline" | "not-in-game" | "empty" | "flaky";

export interface CallLogEntry {
  at: number;
  name: string;
  args: Record<string, unknown>;
  ms: number;
  outcome: "ok" | "error" | "throw";
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export class FakeServer {
  scenario: ScenarioName = "live";
  latencyMs = 40;
  stats: StatItem[] = (fixture as StatItem[]).map((s) => ({ ...s, text: s.text ?? undefined }));
  state: ViewState = {
    rev: 7, pinnedStatKeys: ["fire_damage_resistance_%", "base_maximum_life"], filter: "", category: "all",
    sortBy: "category", sortDesc: false, panelOpen: true,
  };
  vitals: Vitals = { hp: 356, maxHp: 356, es: 22, maxEs: 22, mana: 175, maxMana: 175, weaponSet: 0 };
  log: CallLogEntry[] = [];
  onChange?: () => void;

  /** Changes made "in the HUD panel" (or by an agent): they bump rev like the real plugin. */
  hud = {
    pin: (key: string, pinned = true) => this.apply({ pin: [key, pinned] }),
    filter: (text?: string, category?: CategoryFilter) => this.apply({ filter: text, category }),
    select: (key: string | null) => this.apply({ select: key }),
    sort: (sortBy: SortBy, sortDesc: boolean) => this.apply({ sortBy, sortDesc }),
  };

  async handle(name: string, args: Record<string, unknown>): Promise<CallToolResult> {
    const t0 = performance.now();
    const jitter = this.scenario === "flaky" ? Math.random() * 600 : Math.random() * 20;
    await sleep(this.latencyMs + jitter);
    const entry: CallLogEntry = { at: Date.now(), name, args, ms: 0, outcome: "ok" };
    try {
      if (this.scenario === "offline" || (this.scenario === "flaky" && Math.random() < 0.3)) {
        throw new Error("The poe2 HUD bridge is not reachable (connection refused on 127.0.0.1:50900). Is the HUD running?");
      }
      const result = this.dispatch(name, args);
      entry.outcome = result.isError ? "error" : "ok";
      return result;
    } catch (e) {
      entry.outcome = "throw";
      throw e;
    } finally {
      entry.ms = Math.round(performance.now() - t0);
      this.log = [...this.log.slice(-199), entry];
      this.onChange?.();
    }
  }

  private dispatch(name: string, a: Record<string, unknown>): CallToolResult {
    const inGame = this.scenario !== "not-in-game";
    const stats = this.scenario === "empty" ? [] : this.stats;
    const withPins = (s: StatItem) => (this.state.pinnedStatKeys.includes(s.key) ? { ...s, pinned: true } : s);
    switch (name) {
      case "stats_ui_state":
        return a.sinceRev === this.state.rev
          ? json({ game: "poe2", rev: this.state.rev, unchanged: true, vitals: this.vitals, inGame })
          : json({ game: "poe2", rev: this.state.rev, state: this.state, vitals: this.vitals, inGame });
      case "stats_page": {
        const page = Number(a.page ?? 0), size = Math.min(200, Number(a.pageSize ?? 50));
        const q = String(a.filter ?? this.state.filter).toLowerCase();
        const cat = String(a.category ?? this.state.category);
        const all = stats.filter((s) => (cat === "all" || s.category === cat) && (!q || s.key.includes(q) || s.text?.toLowerCase().includes(q)));
        const categories: Record<string, number> = {};
        for (const s of stats) categories[s.category] = (categories[s.category] ?? 0) + 1;
        return json({
          game: "poe2", rev: this.state.rev, total: all.length, page, pageSize: size,
          items: all.slice(page * size, (page + 1) * size).map(withPins), categories,
          pinned: stats.filter((s) => this.state.pinnedStatKeys.includes(s.key)).map(withPins),
        });
      }
      case "get_stat": {
        const s = stats.find((x) => x.key === a.key);
        if (!s) return json({ error: "unknown_stat", message: `'${a.key}' is not a Stats.dat key in this poe2 build` }, true);
        return json({ game: "poe2", stat: withPins(s), recordType: s.key.endsWith("%") ? "Percents" : "Int", isWeaponLocal: false, present: true });
      }
      case "set_stat_pinned":
        return this.mutation({ pin: [String(a.key), a.pinned !== false] }, a.expectedRev);
      case "set_stats_filter":
        return this.mutation({ filter: a.text as string | undefined, category: a.category as CategoryFilter | undefined }, a.expectedRev);
      case "select_stat":
        return this.mutation({ select: (a.key as string | undefined) ?? null }, a.expectedRev);
      case "set_stats_view":
        return this.mutation({ sortBy: a.sortBy as SortBy | undefined, sortDesc: a.sortDesc as boolean | undefined }, a.expectedRev);
      default:
        throw new Error(`Unknown tool: ${name}`);
    }
  }

  private mutation(change: Change, expectedRev: unknown): CallToolResult {
    if (typeof expectedRev === "number" && expectedRev !== this.state.rev) {
      return json({ ok: false, rev: this.state.rev, state: this.state, error: "rev_mismatch", message: "The stats view changed since rev " + expectedRev }, true);
    }
    if (change.pin && !this.stats.some((s) => s.key === change.pin![0])) {
      return json({ ok: false, rev: this.state.rev, state: this.state, error: "unknown_stat", message: `'${change.pin[0]}' is not a Stats.dat key in this poe2 build` }, true);
    }
    this.apply(change);
    return json({ ok: true, rev: this.state.rev, state: this.state });
  }

  private apply(c: Change) {
    const s = { ...this.state };
    if (c.pin) {
      const [key, on] = c.pin;
      s.pinnedStatKeys = on ? (s.pinnedStatKeys.includes(key) ? s.pinnedStatKeys : [...s.pinnedStatKeys, key]) : s.pinnedStatKeys.filter((k) => k !== key);
    }
    if (c.filter !== undefined) s.filter = c.filter;
    if (c.category !== undefined) s.category = c.category;
    if (c.select !== undefined) s.selectedStatKey = c.select;
    if (c.sortBy !== undefined) s.sortBy = c.sortBy;
    if (c.sortDesc !== undefined) s.sortDesc = c.sortDesc;
    // Idempotent like the plugin: no rev bump when nothing changed.
    const { rev: _r, ...before } = this.state;
    const { rev: _n, ...after } = s;
    if (JSON.stringify(before) !== JSON.stringify(after)) this.state = { ...s, rev: this.state.rev + 1 };
    this.onChange?.();
  }

  /** Simulated combat: life/mana drain and recover, and a random stat moves now and then. */
  tick() {
    const v = this.vitals;
    const hit = Math.random() < 0.35;
    this.vitals = {
      ...v,
      hp: hit ? Math.max(1, v.hp - Math.round(Math.random() * 120)) : Math.min(v.maxHp, v.hp + 40),
      es: hit ? 0 : Math.min(v.maxEs, v.es + 6),
      mana: Math.min(v.maxMana, Math.max(0, v.mana + (Math.random() < 0.5 ? -30 : 25))),
    };
    if (Math.random() < 0.4) this.bumpRandomStat();
  }

  bumpRandomStat(key?: string) {
    const i = key ? this.stats.findIndex((s) => s.key === key) : Math.floor(Math.random() * this.stats.length);
    if (i < 0) return;
    const s = this.stats[i];
    const delta = Math.round((Math.random() - 0.4) * Math.max(4, Math.abs(s.value) * 0.2)) || 1;
    this.stats = this.stats.map((x, j) => (j === i ? { ...x, value: x.value + delta } : x));
    this.onChange?.();
  }
}

interface Change {
  pin?: [string, boolean];
  filter?: string;
  category?: CategoryFilter;
  select?: string | null;
  sortBy?: SortBy;
  sortDesc?: boolean;
}

function json(obj: object, isError = false): CallToolResult {
  return {
    content: [{ type: "text", text: JSON.stringify(obj) }],
    structuredContent: obj as Record<string, unknown>,
    ...(isError ? { isError: true } : {}),
  };
}
