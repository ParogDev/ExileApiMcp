// Sync engine for the shared stats view. Framework-free so the dev harness and React share it.
//
// The HUD plugin owns the view state and versions it with `rev`. This store:
//   - polls stats_ui_state(sinceRev) every second (cheap: {unchanged:true} + vitals when idle);
//   - refreshes the full stat list every few seconds and filters/sorts it locally, so typing
//     and sorting never wait on a round trip;
//   - applies the user's changes optimistically as overlays on top of the last confirmed state,
//     then reconciles to the state each mutator returns (or rolls back and reports the error).

import type {
  Category, CategoryFilter, Game, GetStatResult, MutationResult, ShowResult, SortBy, StatItem,
  StatsPageResult, UiStateResult, ViewState, Vitals,
} from "./types";

export interface ToolResponse {
  /** structuredContent, or the JSON text content parsed when a host drops structuredContent. */
  data: unknown;
  isError: boolean;
  text?: string;
}
export type CallTool = (name: string, args: Record<string, unknown>) => Promise<ToolResponse>;

export type Connection = "connecting" | "live" | "stale" | "offline";

export interface StatChange {
  delta: number;
  at: number;
}

export interface Toast {
  id: number;
  /** The stripe, glyph and default title: error (red), warning (amber), success (green), info (neutral). */
  kind: "error" | "warning" | "success" | "info";
  /** The caps title line; without one the kind is the title (Error, Warning, Done, Note). */
  title?: string;
  /** The message line, set in caps by the component (identifiers keep their case). */
  text: string;
}

/** One vitals poll, kept for the sparklines. */
export interface VitalSample {
  at: number;
  hp: number;
  es: number;
  mana: number;
}

export type ViewField = "selection" | "pins" | "filter" | "sort";

/** A view change that arrived from another surface (the HUD panel or an agent), not from this app. */
export interface RemoteChange {
  at: number;
  fields: ViewField[];
}

export interface Snapshot {
  game?: Game;
  inGame?: boolean;
  /** Last state confirmed by the HUD. */
  confirmed?: ViewState;
  /** confirmed + the user's in-flight changes: what the UI renders. */
  view?: ViewState;
  vitals?: Vitals;
  /** Recent vitals polls, oldest first (bounded). */
  vitalsHistory: VitalSample[];
  /** Last view change that came from the HUD or an agent, for the "synced from HUD" cue. */
  remote?: RemoteChange;
  /** Where the current selection came from: this app, or another surface. */
  selectionSource: "local" | "remote";
  stats: StatItem[];
  statsLoaded: boolean;
  /** Last value change per stat key since the panel opened (for the delta column and flash). */
  changes: Record<string, StatChange>;
  conn: Connection;
  error?: string;
  lastOkAt?: number;
  latencyMs?: number;
  pending: number;
  toasts: Toast[];
  /** Tool calls made so far, by tool name (shown in the debug strip). */
  calls: Record<string, number>;
}

export interface StoreOptions {
  pollMs?: number;
  statsMs?: number;
  /** Clock and timers are injectable so the harness can run fast and tests deterministically. */
  now?: () => number;
}

type Overlay = { id: number; apply: (s: ViewState) => ViewState };

const PAGE_SIZE = 200;
const MAX_BACKOFF_MS = 15_000;
const HISTORY_SAMPLES = 90;

export class StatsStore {
  private snap: Snapshot = {
    stats: [], statsLoaded: false, changes: {}, conn: "connecting", pending: 0, toasts: [], calls: {},
    vitalsHistory: [], selectionSource: "local",
  };
  private listeners = new Set<() => void>();
  private overlays: Overlay[] = [];
  private nextId = 1;
  private failures = 0;
  private stateEpoch = 0;
  private pollTimer?: ReturnType<typeof setTimeout>;
  private statsTimer?: ReturnType<typeof setTimeout>;
  private running = false;
  private gameArg?: string;
  private readonly pollMs: number;
  private readonly statsMs: number;
  private readonly now: () => number;

  constructor(private readonly callTool: CallTool, opts: StoreOptions = {}) {
    this.pollMs = opts.pollMs ?? 1000;
    this.statsMs = opts.statsMs ?? 3000;
    this.now = opts.now ?? (() => Date.now());
  }

  // ── React glue (useSyncExternalStore) ─────────────────────────────

  subscribe = (fn: () => void) => {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  };
  getSnapshot = () => this.snap;

  private set(patch: Partial<Snapshot>) {
    const next = { ...this.snap, ...patch };
    next.view = next.confirmed ? this.overlays.reduce((s, o) => o.apply(s), next.confirmed) : undefined;
    this.snap = next;
    this.listeners.forEach((l) => l());
  }

  // ── Lifecycle ─────────────────────────────────────────────────────

  /** Optional `game` argument forwarded to every tool (from show_player_stats' input). */
  setGameArg(game: string | undefined) {
    this.gameArg = game || undefined;
  }

  /** Seed from the show_player_stats result so the first paint doesn't wait for a poll. */
  seed(r: ShowResult) {
    if (!r) return;
    const patch: Partial<Snapshot> = { game: r.game, inGame: r.inGame, vitals: r.vitals };
    if (r.state && !this.snap.confirmed) patch.confirmed = r.state;
    if (!this.snap.statsLoaded) {
      const byKey = new Map<string, StatItem>();
      for (const s of [...(r.resistances ?? []), ...(r.pinned ?? [])]) byKey.set(s.key, s);
      patch.stats = [...byKey.values()];
    }
    this.set(patch);
  }

  start() {
    if (this.running) return;
    this.running = true;
    void this.poll();
    void this.refreshStats();
  }

  stop() {
    this.running = false;
    clearTimeout(this.pollTimer);
    clearTimeout(this.statsTimer);
  }

  /** Poll right now (e.g. the user clicked retry or the tab became visible). */
  kick() {
    if (!this.running) return;
    clearTimeout(this.pollTimer);
    clearTimeout(this.statsTimer);
    this.failures = 0;
    void this.poll();
    void this.refreshStats();
  }

  // ── Reads ─────────────────────────────────────────────────────────

  private async call(name: string, args: Record<string, unknown>): Promise<ToolResponse> {
    this.snap.calls = { ...this.snap.calls, [name]: (this.snap.calls[name] ?? 0) + 1 };
    return this.callTool(name, this.gameArg ? { ...args, game: this.gameArg } : args);
  }

  private async poll() {
    if (!this.running) return;
    const epoch = this.stateEpoch;
    const t0 = this.now();
    try {
      const args: Record<string, unknown> = {};
      if (this.snap.confirmed) args.sinceRev = this.snap.confirmed.rev;
      const res = await this.call("stats_ui_state", args);
      if (res.isError) throw new Error(errorText(res));
      const r = res.data as UiStateResult;
      this.failures = 0;
      const at = this.now();
      const patch: Partial<Snapshot> = {
        conn: "live", error: undefined, lastOkAt: at, latencyMs: at - t0,
        vitals: r.vitals ?? this.snap.vitals, inGame: r.inGame ?? this.snap.inGame,
      };
      if (r.vitals) {
        const sample = { at, hp: r.vitals.hp, es: r.vitals.es, mana: r.vitals.mana };
        patch.vitalsHistory = [...this.snap.vitalsHistory.slice(-(HISTORY_SAMPLES - 1)), sample];
      }
      if (r.game && this.snap.game && r.game !== this.snap.game) {
        // The other game's HUD answered (one game at a time): start over.
        this.overlays = [];
        Object.assign(patch, { game: r.game, confirmed: r.state, stats: [], statsLoaded: false, changes: {}, vitalsHistory: [], remote: undefined });
        this.set(patch);
        void this.refreshStats();
      } else {
        if (r.game) patch.game = r.game;
        // A mutation that landed while this poll was in flight carries newer state; keep it.
        if (r.state && !r.unchanged && epoch === this.stateEpoch) {
          patch.confirmed = r.state;
          // Anything that differs from what we last confirmed was changed elsewhere (HUD or agent):
          // our own writes reconcile through mutate(), never through a poll.
          const fields = this.snap.confirmed ? diffView(this.snap.confirmed, r.state) : [];
          if (fields.length) {
            patch.remote = { at, fields };
            if (fields.includes("selection")) patch.selectionSource = "remote";
          }
        }
        this.set(patch);
      }
    } catch (e) {
      this.failures++;
      this.set({ conn: this.failures >= 3 ? "offline" : "stale", error: messageOf(e) });
    } finally {
      if (this.running) {
        const delay = this.failures ? Math.min(this.pollMs * 2 ** this.failures, MAX_BACKOFF_MS) : this.pollMs;
        clearTimeout(this.pollTimer);
        this.pollTimer = setTimeout(() => void this.poll(), delay);
      }
    }
  }

  private async refreshStats() {
    if (!this.running) return;
    try {
      const all: StatItem[] = [];
      for (let page = 0; page < 20; page++) {
        const res = await this.call("stats_page", { filter: "", category: "all", page, pageSize: PAGE_SIZE, sortBy: "category" });
        if (res.isError) throw new Error(errorText(res));
        const r = res.data as StatsPageResult;
        all.push(...r.items);
        if (all.length >= r.total || r.items.length === 0) break;
      }
      this.set({ stats: all, statsLoaded: true, changes: this.diff(all) });
    } catch {
      // The poll reports connection problems; the table keeps its last good data.
    } finally {
      if (this.running) {
        clearTimeout(this.statsTimer);
        const delay = this.failures ? Math.min(this.statsMs * 2 ** this.failures, MAX_BACKOFF_MS) : this.statsMs;
        this.statsTimer = setTimeout(() => void this.refreshStats(), delay);
      }
    }
  }

  private diff(next: StatItem[]): Record<string, StatChange> {
    if (!this.snap.statsLoaded) return {};
    const prev = new Map(this.snap.stats.map((s) => [s.key, s.value]));
    const changes = { ...this.snap.changes };
    const at = this.now();
    for (const s of next) {
      const before = prev.get(s.key);
      if (before !== undefined && before !== s.value) changes[s.key] = { delta: s.value - before, at };
    }
    return changes;
  }

  async getStat(key: string): Promise<GetStatResult | undefined> {
    const res = await this.call("get_stat", { key });
    return res.isError ? undefined : (res.data as GetStatResult);
  }

  // ── Writes (optimistic) ───────────────────────────────────────────

  setPinned(key: string, pinned: boolean) {
    return this.mutate("set_stat_pinned", { key, pinned }, (s) => ({
      ...s,
      pinnedStatKeys: pinned
        ? s.pinnedStatKeys.includes(key) ? s.pinnedStatKeys : [...s.pinnedStatKeys, key]
        : s.pinnedStatKeys.filter((k) => k !== key),
    }));
  }

  setFilter(text: string | undefined, category?: CategoryFilter) {
    const args: Record<string, unknown> = {};
    if (text !== undefined) args.text = text;
    if (category !== undefined) args.category = category;
    return this.mutate("set_stats_filter", args, (s) => ({
      ...s,
      ...(text !== undefined ? { filter: text } : {}),
      ...(category !== undefined ? { category } : {}),
    }));
  }

  select(key: string | null) {
    this.set({ selectionSource: "local" });
    return this.mutate("select_stat", key ? { key } : {}, (s) => ({ ...s, selectedStatKey: key }));
  }

  setSort(sortBy: SortBy, sortDesc: boolean) {
    return this.mutate("set_stats_view", { sortBy, sortDesc }, (s) => ({ ...s, sortBy, sortDesc }));
  }

  private async mutate(tool: string, args: Record<string, unknown>, apply: (s: ViewState) => ViewState) {
    const overlay: Overlay = { id: this.nextId++, apply };
    this.overlays.push(overlay);
    this.set({ pending: this.snap.pending + 1 });
    let patch: Partial<Snapshot> = {};
    try {
      const res = await this.call(tool, args);
      const r = res.data as MutationResult | undefined;
      if (r?.state) {
        this.stateEpoch++;
        patch.confirmed = r.state;
      }
      if (res.isError || r?.ok === false) patch.toasts = this.withToast("error", r?.message ?? errorText(res));
    } catch (e) {
      patch.toasts = this.withToast("error", messageOf(e));
    } finally {
      this.overlays = this.overlays.filter((o) => o !== overlay);
      this.set({ ...patch, pending: this.snap.pending - 1 });
    }
  }

  // ── Toasts ────────────────────────────────────────────────────────

  private withToast(kind: Toast["kind"], text: string, title?: string): Toast[] {
    const toast = { id: this.nextId++, kind, text, title };
    setTimeout(() => this.dismiss(toast.id), 6000);
    return [...this.snap.toasts.slice(-2), toast];
  }

  toast(kind: Toast["kind"], text: string, title?: string) {
    this.set({ toasts: this.withToast(kind, text, title) });
  }

  dismiss(id: number) {
    if (this.snap.toasts.some((t) => t.id === id)) this.set({ toasts: this.snap.toasts.filter((t) => t.id !== id) });
  }
}

// ── Pure helpers (shared by the table and the harness) ──────────────

export const CATEGORY_ORDER: Record<Category, number> = {
  vitals: 0, resistances: 1, defense: 2, offense: 3, charges: 4, movement: 5, other: 6,
};

/** Same semantics as the bridge: case-insensitive substring of key or in-game text. */
export function filterStats(stats: StatItem[], text: string, category: CategoryFilter): StatItem[] {
  const q = text.trim().toLowerCase();
  return stats.filter(
    (s) => (category === "all" || s.category === category)
      && (!q || s.key.toLowerCase().includes(q) || (s.text?.toLowerCase().includes(q) ?? false)),
  );
}

export function sortStats(stats: StatItem[], sortBy: SortBy, desc: boolean): StatItem[] {
  const dir = desc ? -1 : 1;
  const cmp = (a: StatItem, b: StatItem) => {
    if (sortBy === "value") return (a.value - b.value) * dir || a.key.localeCompare(b.key);
    if (sortBy === "key") return a.key.localeCompare(b.key) * dir;
    return (CATEGORY_ORDER[a.category] - CATEGORY_ORDER[b.category]) * dir || a.key.localeCompare(b.key);
  };
  return [...stats].sort(cmp);
}

/** Which parts of the shared view differ between two states. */
export function diffView(a: ViewState, b: ViewState): ViewField[] {
  const fields: ViewField[] = [];
  if ((a.selectedStatKey ?? null) !== (b.selectedStatKey ?? null)) fields.push("selection");
  if (a.pinnedStatKeys.join("\n") !== b.pinnedStatKeys.join("\n")) fields.push("pins");
  if (a.filter !== b.filter || a.category !== b.category) fields.push("filter");
  if (a.sortBy !== b.sortBy || a.sortDesc !== b.sortDesc) fields.push("sort");
  return fields;
}

export function countByCategory(stats: StatItem[]): Record<CategoryFilter, number> {
  const counts = { all: stats.length } as Record<CategoryFilter, number>;
  for (const c of Object.keys(CATEGORY_ORDER) as Category[]) counts[c] = 0;
  for (const s of stats) counts[s.category] = (counts[s.category] ?? 0) + 1;
  return counts;
}

function errorText(res: ToolResponse): string {
  const d = res.data as { message?: string; error?: string } | undefined;
  return d?.message ?? d?.error ?? res.text ?? "Tool call failed";
}

function messageOf(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}
