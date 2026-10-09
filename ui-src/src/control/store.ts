// ControlStore: framework-free state for the control center. Owns the catalog, which HUDs are up, the live health
// report (push where the host can listen, held perf_watch calls elsewhere), the observer (status, events, layers, a
// layer map), every plugin's settings per game with an undo of the last change, the tool runs made from the Tools
// page, the route and toasts. Immutable snapshots for useSyncExternalStore.

import type { Toast } from "../sync";
import { errorText, isUnreachable, type Host, type ListenState, type ToolResult } from "./host";
import type {
  BridgeEntry, Catalog, CatalogTool, Game, LayerMapResult, LayersResult, ObserveEvent, ObserveEventsResult, ObserveStatus,
  PerfSnapshot, SettingChangeResult, SettingNode, SettingsResult,
} from "./types";

export type Page = "overview" | "tools" | "settings" | "observer" | "perf" | "memory" | "explorer" | "stats";
export interface Route { page: Page; id?: string; sub?: string }

export const PAGES: { page: Page; label: string; standaloneOnly?: boolean }[] = [
  { page: "overview", label: "Overview" },
  { page: "tools", label: "Tools" },
  { page: "settings", label: "Settings" },
  { page: "observer", label: "Observer" },
  { page: "perf", label: "Performance", standaloneOnly: true },
  { page: "memory", label: "Memory", standaloneOnly: true },
  { page: "explorer", label: "Explorer", standaloneOnly: true },
  { page: "stats", label: "Player stats", standaloneOnly: true },
];

export interface ToolRun {
  id: number;
  tool: string;
  args: Record<string, unknown>;
  startedAt: number;
  endedAt?: number;
  result?: ToolResult;
  error?: string;
  /** The store's abort, for held calls (observe_wait, perf_watch, await_*). */
  abort?: AbortController;
}

export interface SettingsState {
  result?: SettingsResult;
  pulledAt?: number;
  loading: boolean;
  error?: string;
  /** Keys "plugin\u0000path" of settings with a change in flight. */
  pending: ReadonlySet<string>;
}

export interface UndoEntry { plugin: string; path: string; label: string; previous: unknown; current: unknown; at: number; permission: boolean }

export interface ObserverState {
  status?: ObserveStatus;
  statusAt?: number;
  events: ObserveEvent[];
  seq: number;
  layers?: LayersResult;
  layersAt?: number;
  map?: { layer: string; result?: LayerMapResult; loading: boolean; error?: string };
  busy: boolean;
  error?: string;
  /** A layer whose add/preflight just happened: highlighted once. */
  justSet?: { id: string; preflight?: string | null; at: number };
  lastEventAt?: number;
}

export interface HealthState {
  snapshot?: PerfSnapshot;
  at?: number;
  watching: boolean;
  loading: boolean;
  error?: string;
  /** How updates arrive: push (subscriptions), held perf_watch calls, or nothing (not watching). */
  via: "push" | "held" | "off";
  history: { at: number; fps: number }[];
}

export interface Snapshot {
  route: Route;
  catalog?: Catalog;
  catalogError?: string;
  catalogAt?: number;
  loadingCatalog: boolean;
  games: BridgeEntry[];
  gamesAt?: number;
  /** The game the pages act on: a HUD that is up, else the user's pick. */
  game?: Game;
  health: HealthState;
  observer: ObserverState;
  settings: Record<string, SettingsState>;
  undo?: UndoEntry;
  runs: ToolRun[];
  /** Tool search text (shared so a tour can set it). */
  toolQuery: string;
  toolFamily?: string;
  listen: ListenState;
  toasts: Toast[];
  calls: number;
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
export const EVENTS_POLL_MS = 2500;
export const GAMES_POLL_MS = 20_000;
export const MAX_EVENTS = 300;

export class ControlStore {
  private snap: Snapshot = {
    route: { page: "overview" }, loadingCatalog: false, games: [],
    health: { watching: false, loading: false, via: "off", history: [] },
    observer: { events: [], seq: 0, busy: false },
    settings: {}, runs: [], toolQuery: "", listen: "off", toasts: [], calls: 0,
  };
  private listeners = new Set<() => void>();
  private toastSeq = 0;
  private runSeq = 0;
  private running = false;
  private gen = 0;
  private unlisten?: () => void;
  private subscribed: string[] = [];
  private healthWatchGen = 0;
  private eventsTimer?: ReturnType<typeof setTimeout>;
  private gamesTimer?: ReturnType<typeof setTimeout>;

  constructor(public readonly host: Host) {}

  subscribe = (fn: () => void) => { this.listeners.add(fn); return () => { this.listeners.delete(fn); }; };
  getSnapshot = () => this.snap;

  private set(patch: Partial<Snapshot>) {
    this.snap = { ...this.snap, ...patch };
    for (const l of this.listeners) l();
  }

  // ── Lifecycle ────────────────────────────────────────────────────

  start() {
    if (this.running) return;
    this.running = true;
    this.gen++;
    void this.loadCatalog();
    void this.refreshGames(true);
    void this.refreshObserver();
    this.resubscribe();
    void this.pollEvents();
  }

  stop() {
    this.running = false;
    this.gen++;
    this.healthWatchGen++;
    clearTimeout(this.eventsTimer);
    clearTimeout(this.gamesTimer);
    this.unlisten?.();
    this.unlisten = undefined;
    for (const r of this.snap.runs) r.abort?.abort();
  }

  // ── Routing ──────────────────────────────────────────────────────

  go(page: Page, id?: string, sub?: string) {
    const route: Route = { page, id, sub };
    this.set({ route });
    if (this.host.mode === "standalone") {
      const hash = "#/" + [page, id, sub].filter((x) => x !== undefined).map((x) => encodeURIComponent(String(x))).join("/");
      if (location.hash !== hash) history.replaceState(null, "", location.pathname + location.search + hash);
    }
  }

  /** Standalone: the route from the hash (#/tools/observe_layers), after the token was taken out. */
  routeFromHash() {
    const m = /^#\/([a-z]+)(?:\/([^/]+))?(?:\/([^/]+))?/.exec(location.hash);
    if (!m) return;
    const page = PAGES.find((p) => p.page === m[1])?.page;
    if (page) this.set({ route: { page, id: m[2] ? decodeURIComponent(m[2]) : undefined, sub: m[3] ? decodeURIComponent(m[3]) : undefined } });
  }

  setToolQuery(q: string) { this.set({ toolQuery: q }); }
  setToolFamily(f: string | undefined) { this.set({ toolFamily: f }); }

  // ── Catalog and games ────────────────────────────────────────────

  async loadCatalog() {
    this.set({ loadingCatalog: true, catalogError: undefined });
    try {
      const r = await this.call("hud_catalog", {});
      const c = r.data as Catalog | undefined;
      if (r.isError || !c?.tools) throw new Error(r.text || "hud_catalog returned no catalog");
      const gamesUp = c.server.gamesUp.filter((g): g is Game => g === "poe1" || g === "poe2");
      this.set({ catalog: c, catalogAt: Date.now(), loadingCatalog: false, game: this.snap.game ?? gamesUp[0] });
      this.resubscribe();
    } catch (e) {
      this.set({ loadingCatalog: false, catalogError: errorText(e) });
    }
  }

  /** The host's tool result when the app was opened by hud_catalog: no second call needed. */
  seedCatalog(data: unknown) {
    const c = data as Catalog | undefined;
    if (!c?.tools || !Array.isArray(c.tools) || !c.server) return;
    const gamesUp = c.server.gamesUp.filter((g): g is Game => g === "poe1" || g === "poe2");
    this.set({ catalog: c, catalogAt: Date.now(), loadingCatalog: false, catalogError: undefined, game: this.snap.game ?? gamesUp[0] });
    this.resubscribe();
  }

  tool(name: string): CatalogTool | undefined { return this.snap.catalog?.tools.find((t) => t.name === name); }

  async refreshGames(scheduleNext = false) {
    clearTimeout(this.gamesTimer);
    const gen = this.gen;
    try {
      const r = await this.call("bridge_status", {});
      const d = r.data as { bridges?: BridgeEntry[] } | BridgeEntry[] | undefined;
      const games = Array.isArray(d) ? d : d?.bridges ?? [];
      if (gen !== this.gen) return;
      const up = games.filter((g) => g.status === "connected").map((g) => g.game as Game);
      const game = this.snap.game && up.includes(this.snap.game) ? this.snap.game : up[0] ?? this.snap.game;
      const gameChanged = game !== this.snap.game;
      this.set({ games, gamesAt: Date.now(), game });
      if (gameChanged) this.resubscribe();
    } catch (e) {
      if (gen !== this.gen) return;
      this.set({ gamesAt: Date.now(), games: this.snap.games.map((g) => ({ ...g, status: "unreachable", error: errorText(e) })) });
    }
    if (scheduleNext && this.running) this.gamesTimer = setTimeout(() => void this.refreshGames(true), GAMES_POLL_MS);
  }

  setGame(game: Game) {
    if (game === this.snap.game) return;
    this.set({ game, observer: { events: [], seq: 0, busy: false }, health: { ...this.snap.health, snapshot: undefined, history: [] } });
    this.resubscribe();
    void this.pollEvents();
  }

  get gamesUp(): Game[] { return this.snap.games.filter((g) => g.status === "connected").map((g) => g.game as Game); }

  private gameArgs(extra: Record<string, unknown> = {}): Record<string, unknown> {
    return this.snap.game ? { game: this.snap.game, ...extra } : extra;
  }

  // ── Push (standalone) ────────────────────────────────────────────

  /** Subscribe to the observer and (while watching) the perf report of the selected game, when the host can listen. */
  private resubscribe() {
    if (!this.host.listen || !this.running) return;
    const g = this.snap.game;
    const uris = g ? [`exile://observe/${g}/events`, `exile://observe/${g}/layers`, ...(this.snap.health.watching ? [`exile://perf/${g}/report`] : [])] : [];
    if (uris.join() === this.subscribed.join()) return;
    this.unlisten?.();
    this.subscribed = uris;
    if (!uris.length) { this.set({ listen: "off" }); return; }
    this.unlisten = this.host.listen(uris, (uri) => void this.onUpdated(uri), (s) => this.set({ listen: s }));
    // What is there now: a subscription only says when something changes.
    for (const u of uris) if (!u.includes("/perf/")) void this.onUpdated(u);
  }

  private async onUpdated(uri: string) {
    if (!this.host.readResource) return;
    try {
      if (uri.endsWith("/events")) {
        const { json } = await this.host.readResource(uri);
        this.applyEvents(json as ObserveEventsResult);
      } else if (uri.endsWith("/layers")) {
        const { json } = await this.host.readResource(uri);
        const r = json as LayersResult;
        if (r?.layers) this.set({ observer: { ...this.snap.observer, layers: r, layersAt: Date.now() } });
      } else if (uri.includes("/perf/")) {
        const { json } = await this.host.readResource(uri);
        this.applyHealth(json as PerfSnapshot);
      }
    } catch (e) {
      this.toast("error", `Could not read ${uri}: ${errorText(e)}`);
    }
  }

  // ── Health (perf) ────────────────────────────────────────────────

  /** Watch the HUD's health: push where possible (the server traces on its cadence while anyone listens), else held
   *  perf_watch calls. Each report traces the HUD for 3 s, so this is opt-in. */
  setWatchHealth(on: boolean) {
    this.healthWatchGen++;
    const push = !!this.host.listen;
    this.set({ health: { ...this.snap.health, watching: on, via: on ? (push ? "push" : "held") : "off", loading: on && !this.snap.health.snapshot } });
    if (push) {
      this.resubscribe();
      // The first report: ask for it rather than wait for the cadence.
      if (on) void this.pullHealth(0);
    } else if (on) void this.watchHealthHeld(this.healthWatchGen);
  }

  /** One report now (perf_watch since=0 returns the latest, running one if there is none). */
  async pullHealth(since = 0) {
    this.set({ health: { ...this.snap.health, loading: true, error: undefined } });
    try {
      const r = await this.call("perf_watch", this.gameArgs({ since, timeoutSec: 25 }));
      if (r.isError) throw new Error(r.text || "perf_watch failed");
      this.applyHealth(r.data as PerfSnapshot);
    } catch (e) {
      this.set({ health: { ...this.snap.health, loading: false, error: errorText(e) } });
    }
  }

  private async watchHealthHeld(gen: number) {
    while (gen === this.healthWatchGen && this.running) {
      if (document.hidden) { await sleep(1000); continue; }
      const since = this.snap.health.snapshot?.seq ?? 0;
      try {
        const r = await this.call("perf_watch", this.gameArgs({ since, timeoutSec: 25 }));
        if (gen !== this.healthWatchGen) return;
        if (r.isError) throw new Error(r.text || "perf_watch failed");
        this.applyHealth(r.data as PerfSnapshot);
      } catch (e) {
        if (gen !== this.healthWatchGen) return;
        this.set({ health: { ...this.snap.health, loading: false, error: errorText(e) } });
        await sleep(5000);
      }
    }
  }

  private applyHealth(s: PerfSnapshot | undefined) {
    if (!s || typeof s.seq !== "number") return;
    const h = this.snap.health;
    if (h.snapshot && s.seq < h.snapshot.seq) return;
    const fps = s.report?.trace?.hudFps;
    const history = typeof fps === "number" && s.fresh !== false && s.seq !== h.snapshot?.seq ? [...h.history.slice(-29), { at: Date.now(), fps }] : h.history;
    this.set({ health: { ...h, snapshot: s, at: Date.now(), loading: false, error: undefined, history } });
  }

  // ── Observer ─────────────────────────────────────────────────────

  async refreshObserver() {
    const gen = this.gen;
    this.set({ observer: { ...this.snap.observer, busy: true, error: undefined } });
    try {
      const [st, ly] = await Promise.all([this.call("observe", this.gameArgs({ action: "status" })), this.call("observe_layers", this.gameArgs({ action: "list" }))]);
      if (gen !== this.gen) return;
      const status = st.isError ? undefined : (st.data as ObserveStatus);
      const layers = ly.isError ? undefined : (ly.data as LayersResult);
      const err = st.isError ? st.text : ly.isError ? ly.text : undefined;
      this.set({ observer: { ...this.snap.observer, status: status ?? this.snap.observer.status, statusAt: status ? Date.now() : this.snap.observer.statusAt, layers: layers ?? this.snap.observer.layers, layersAt: layers ? Date.now() : this.snap.observer.layersAt, busy: false, error: err } });
    } catch (e) {
      if (gen !== this.gen) return;
      this.set({ observer: { ...this.snap.observer, busy: false, error: errorText(e) } });
    }
  }

  async setObserving(on: boolean) {
    this.set({ observer: { ...this.snap.observer, busy: true } });
    try {
      const r = await this.call("observe", this.gameArgs({ action: on ? "start" : "stop" }));
      if (r.isError) throw new Error(r.text || "observe failed");
      this.toast("info", on ? "Observing: the HUD records what happens in game (read-only)" : "Observation stopped");
      await this.refreshObserver();
    } catch (e) {
      this.set({ observer: { ...this.snap.observer, busy: false } });
      this.toast("error", errorText(e));
    }
  }

  /** The live events feed: push re-reads the resource; otherwise observe_events {since} every 2.5 s while visible. */
  private async pollEvents() {
    clearTimeout(this.eventsTimer);
    if (!this.running) return;
    const gen = this.gen;
    if (!this.host.listen && !document.hidden && this.snap.game && this.snap.catalog) {
      try {
        const r = await this.call("observe_events", this.gameArgs({ since: this.snap.observer.seq, limit: 200 }), { quiet: true });
        if (gen !== this.gen) return;
        if (!r.isError) this.applyEvents(r.data as ObserveEventsResult);
      } catch { /* the bridge is down: the games poll shows it */ }
    }
    if (gen === this.gen && this.running) this.eventsTimer = setTimeout(() => void this.pollEvents(), EVENTS_POLL_MS);
  }

  private applyEvents(r: ObserveEventsResult | undefined) {
    if (!r || !Array.isArray(r.events)) return;
    const o = this.snap.observer;
    const fresh = r.events.filter((e) => e.seq > o.seq);
    const events = fresh.length ? [...o.events, ...fresh].slice(-MAX_EVENTS) : o.events;
    const status = o.status && o.status.enabled !== r.enabled ? { ...o.status, enabled: r.enabled } : o.status;
    this.set({ observer: { ...o, events, seq: Math.max(o.seq, r.seq ?? 0), status, lastEventAt: fresh.length ? Date.now() : o.lastEventAt } });
  }

  async setLayer(spec: { id: string; path: string; mode: string; hz: number; enabled: boolean; key?: string }): Promise<{ ok: boolean; preflight?: string | null; error?: string }> {
    this.set({ observer: { ...this.snap.observer, busy: true } });
    try {
      const args: Record<string, unknown> = { action: "set", id: spec.id, path: spec.path, mode: spec.mode, hz: spec.hz, enabled: spec.enabled };
      if (spec.key) args.key = spec.key;
      const r = await this.call("observe_layers", this.gameArgs(args));
      if (r.isError) throw new Error(r.text || "observe_layers set failed");
      const d = r.data as LayersResult;
      this.set({ observer: { ...this.snap.observer, layers: d?.layers ? d : this.snap.observer.layers, layersAt: Date.now(), busy: false, justSet: { id: spec.id, preflight: d?.preflight, at: Date.now() } } });
      if (d?.preflight) this.toast("error", `Layer ${spec.id} stored, but its preflight failed: ${d.preflight}`);
      else this.toast("info", `Layer ${spec.id} ${spec.enabled ? "is watching" : "paused"}`);
      return { ok: true, preflight: d?.preflight };
    } catch (e) {
      this.set({ observer: { ...this.snap.observer, busy: false } });
      const error = errorText(e);
      this.toast("error", error);
      return { ok: false, error };
    }
  }

  async removeLayer(id: string) {
    this.set({ observer: { ...this.snap.observer, busy: true } });
    try {
      const r = await this.call("observe_layers", this.gameArgs({ action: "remove", id }));
      if (r.isError) throw new Error(r.text || "observe_layers remove failed");
      const d = r.data as LayersResult;
      const map = this.snap.observer.map?.layer === id ? undefined : this.snap.observer.map;
      this.set({ observer: { ...this.snap.observer, layers: d?.layers ? d : this.snap.observer.layers, layersAt: Date.now(), busy: false, map } });
      this.toast("info", `Removed layer ${id}`);
    } catch (e) {
      this.set({ observer: { ...this.snap.observer, busy: false } });
      this.toast("error", errorText(e));
    }
  }

  async loadLayerMap(layer: string, unmappedOnly = false) {
    this.set({ observer: { ...this.snap.observer, map: { layer, loading: true, result: this.snap.observer.map?.layer === layer ? this.snap.observer.map.result : undefined } } });
    try {
      const r = await this.call("observe_layer_map", this.gameArgs({ layer, unmappedOnly, limit: 200 }));
      if (r.isError) throw new Error(r.text || "observe_layer_map failed");
      this.set({ observer: { ...this.snap.observer, map: { layer, loading: false, result: r.data as LayerMapResult } } });
    } catch (e) {
      this.set({ observer: { ...this.snap.observer, map: { layer, loading: false, error: errorText(e) } } });
    }
  }

  closeLayerMap() { this.set({ observer: { ...this.snap.observer, map: undefined } }); }

  // ── Settings ─────────────────────────────────────────────────────

  settingsFor(game: string | undefined): SettingsState {
    return this.snap.settings[game ?? "?"] ?? { loading: false, pending: new Set() };
  }

  private patchSettings(game: string, patch: Partial<SettingsState>) {
    const cur = this.settingsFor(game);
    this.set({ settings: { ...this.snap.settings, [game]: { ...cur, ...patch } } });
  }

  async pullSettings() {
    const game = this.snap.game ?? "?";
    this.patchSettings(game, { loading: true, error: undefined });
    try {
      const r = await this.call("hud_settings", this.gameArgs());
      if (r.isError) throw new Error(r.text || "hud_settings failed");
      const d = r.data as SettingsResult;
      if (!d?.plugins) throw new Error("hud_settings returned no plugins");
      this.patchSettings(game, { result: d, pulledAt: Date.now(), loading: false });
    } catch (e) {
      this.patchSettings(game, { loading: false, error: errorText(e) });
    }
  }

  /** Change one setting: optimistic, then hud_settings_set (or, for a permission, the control center's own route). */
  async changeSetting(plugin: string, node: SettingNode, value: unknown, opts: { viaRoute?: boolean; isUndo?: boolean } = {}): Promise<boolean> {
    const game = this.snap.game ?? "?";
    const st = this.settingsFor(game);
    if (!st.result) return false;
    const key = `${plugin}\u0000${node.path}`;
    const previous = node.value;
    const apply = (v: unknown, extra?: Partial<SettingNode>) => {
      const cur = this.settingsFor(game);
      if (!cur.result) return;
      const result: SettingsResult = {
        ...cur.result,
        plugins: cur.result.plugins.map((p) => (p.plugin !== plugin ? p : { ...p, settings: p.settings.map((s) => (s.path !== node.path ? s : { ...s, ...extra, value: v })) })),
      };
      this.patchSettings(game, { result });
    };
    apply(value);
    this.patchSettings(game, { pending: new Set([...st.pending, key]) });
    try {
      let res: SettingChangeResult;
      if (node.permission || opts.viaRoute) {
        if (!this.host.postSettings) throw new Error("Permission settings can only be changed from the standalone control center or in game.");
        res = await this.host.postSettings({ plugin, path: node.path, value, ...(this.snap.game ? { game: this.snap.game } : {}) });
      } else {
        const r = await this.call("hud_settings_set", this.gameArgs({ plugin, path: node.path, value }));
        if (r.isError) throw new Error(r.text || "hud_settings_set failed");
        res = r.data as SettingChangeResult;
      }
      if (res?.setting) apply(res.setting.value, res.setting);
      const cur = this.settingsFor(game);
      this.patchSettings(game, { pending: new Set([...cur.pending].filter((k) => k !== key)) });
      if (!opts.isUndo) {
        this.set({ undo: { plugin, path: node.path, label: node.label, previous: res?.previous !== undefined ? res.previous : previous, current: res?.setting?.value ?? value, at: Date.now(), permission: node.permission } });
      } else this.set({ undo: undefined });
      return true;
    } catch (e) {
      apply(previous);
      const cur = this.settingsFor(game);
      this.patchSettings(game, { pending: new Set([...cur.pending].filter((k) => k !== key)) });
      this.toast("error", `${node.label}: ${errorText(e)}`);
      return false;
    }
  }

  async undoLast(): Promise<boolean> {
    const u = this.snap.undo;
    if (!u) return false;
    const st = this.settingsFor(this.snap.game);
    const node = st.result?.plugins.find((p) => p.plugin === u.plugin)?.settings.find((s) => s.path === u.path);
    if (!node) return false;
    const ok = await this.changeSetting(u.plugin, node, u.previous, { isUndo: true, viaRoute: u.permission });
    if (ok) this.toast("info", `Undid ${u.label}`);
    return ok;
  }

  dismissUndo() { this.set({ undo: undefined }); }

  // ── Tool runs (the Tools page) ───────────────────────────────────

  async runTool(tool: string, args: Record<string, unknown>): Promise<ToolRun> {
    const abort = new AbortController();
    const run: ToolRun = { id: ++this.runSeq, tool, args, startedAt: Date.now(), abort };
    this.set({ runs: [run, ...this.snap.runs].slice(0, 50) });
    const update = (patch: Partial<ToolRun>) => {
      const next = { ...run, ...patch };
      this.set({ runs: this.snap.runs.map((r) => (r.id === run.id ? next : r)) });
      return next;
    };
    try {
      const result = await this.call(tool, args, { signal: abort.signal });
      return update({ result, endedAt: Date.now(), abort: undefined });
    } catch (e) {
      return update({ error: errorText(e), endedAt: Date.now(), abort: undefined });
    }
  }

  cancelRun(id: number) {
    this.snap.runs.find((r) => r.id === id)?.abort?.abort();
  }

  clearRuns(tool?: string) {
    this.set({ runs: tool ? this.snap.runs.filter((r) => r.tool !== tool) : [] });
  }

  // ── Plumbing ─────────────────────────────────────────────────────

  private async call(name: string, args: Record<string, unknown>, opts: { signal?: AbortSignal; quiet?: boolean } = {}): Promise<ToolResult> {
    this.snap = { ...this.snap, calls: this.snap.calls + 1 };
    try {
      return await this.host.callTool(name, args, { signal: opts.signal });
    } catch (e) {
      const msg = errorText(e);
      // A bridge that just went away: reflect it in the HUD chips without waiting for the next games poll.
      if (isUnreachable(msg) && this.snap.game) {
        const games = this.snap.games.map((g) => (g.game === this.snap.game && g.status === "connected" ? { ...g, status: "unreachable", error: msg } : g));
        if (games.some((g, i) => g !== this.snap.games[i])) this.set({ games });
      }
      throw e;
    }
  }

  toast(kind: Toast["kind"], text: string) {
    const id = ++this.toastSeq;
    this.set({ toasts: [...this.snap.toasts, { id, kind, text }] });
    setTimeout(() => this.dismissToast(id), kind === "error" ? 8000 : 4000);
  }

  dismissToast(id: number) { this.set({ toasts: this.snap.toasts.filter((t) => t.id !== id) }); }
}
