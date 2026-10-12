// ControlStore: framework-free state for the control center. Owns the catalog, which HUDs are up, the live health
// report (push where the host can listen, held perf_watch calls elsewhere), the observer (status, events, layers, a
// layer map), every plugin's settings per game with an undo of the last change, the tool runs made from the Tools
// page, the route and toasts. Immutable snapshots for useSyncExternalStore.

import type { Toast } from "../sync";
import { errorText, isUnreachable, type Host, type ListenState, type ToolResult } from "./host";
import type {
  BridgeEntry, Catalog, CatalogTool, FeedbackSession, FeedbackSessionsResult, Game, HealthBarsPreviewResult, HealthBarsState, LayerMapResult, LayersResult, ObserveEvent, ObserveEventsResult, ObserveStatus,
  PerfSnapshot, PluginPerfResult, PluginPerfRow, RouteError, RouteState, SeriesResult, SettingChangeResult, SettingNode, SettingsResult, TimelineResult,
} from "./types";
import { HEALTHBARS_PLUGIN } from "./types";

export type Page = "overview" | "tools" | "settings" | "observer" | "route" | "f8" | "healthbars" | "perf" | "memory" | "explorer" | "stats";
export interface Route { page: Page; id?: string; sub?: string }

export const PAGES: { page: Page; label: string; standaloneOnly?: boolean }[] = [
  { page: "overview", label: "Overview" },
  { page: "tools", label: "Tools" },
  { page: "settings", label: "Settings" },
  { page: "observer", label: "Observer" },
  { page: "route", label: "Route" },
  { page: "f8", label: "F8 sessions" },
  { page: "healthbars", label: "Health Bars" },
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

/** The timeline view: the selected event and what was read about it (observe_timeline around / companions, observe_series). */
export interface TimelineState {
  /** seq of the selected event. */
  selected?: number;
  around?: { seq: number; windowMs: number; loading: boolean; result?: TimelineResult; error?: string };
  companions?: { layer: string; unit: string; windowMs: number; loading: boolean; result?: TimelineResult; error?: string };
  series?: { layer: string; unit: string; loading: boolean; result?: SeriesResult; error?: string };
  /** Events the journal returned that the memory ring no longer has (older sessions): drawn too, keyed by seq. */
  journal: Readonly<Record<number, ObserveEvent>>;
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

/** The Route page: the route plugin's state (route_state), polled at 1 Hz while the page is open and visible. */
export interface RouteNavState {
  state?: RouteState;
  /** When the state was last read; `ms` is the call's round trip. */
  at?: number;
  ms?: number;
  loading: boolean;
  /** The last call's error: the route_state error object (by code) or the thrown message. */
  error?: RouteError;
  /** Polling on (the page is open and Live is on). */
  live: boolean;
  /** How many reads so far in this session. */
  reads: number;
}

/** The F8 sessions page: feedback_sessions, pushed over exile://feedback/sessions standalone, polled every 3 s inside a host. */
export interface F8State {
  result?: FeedbackSessionsResult;
  at?: number;
  ms?: number;
  loading: boolean;
  error?: string;
  /** Following on (the page is open and Live is on). */
  live: boolean;
  /** How updates arrive while live. */
  via: "push" | "poll" | "off";
  /** The selected press's brief and stderr (feedback_sessions seq=), keyed by seq. */
  detail?: { seq: number; loading: boolean; brief?: string | null; stderr?: string | null; error?: string };
}

/** The Health Bars page: the health bar plugin's state (healthbars_state), polled at 1 Hz while the page is open and
 *  visible; every 5th read also hud_plugin_perf for the HUD's own counters of this plugin and the stock HealthBars. */
export interface HealthBarsNavState {
  state?: HealthBarsState;
  at?: number;
  ms?: number;
  loading: boolean;
  /** The last call's error: the healthbars_state error object (by code) or the thrown message. */
  error?: RouteError;
  live: boolean;
  reads: number;
  /** hud_plugin_perf rows for this plugin and the stock HealthBars plugin, when the server offers the tool. */
  perf?: { ours?: PluginPerfRow; stock?: PluginPerfRow; at: number; note?: string | null };
  /** A healthbars_preview call in flight. */
  previewing: boolean;
  lastPreview?: HealthBarsPreviewResult;
  previewAt?: number;
}

export interface Snapshot {
  route: Route;
  nav: RouteNavState;
  f8: F8State;
  hb: HealthBarsNavState;
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
  timeline: TimelineState;
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
/** The bridge's own ring holds 1000 events: keep as many, so the timeline shows everything the HUD still has. */
export const MAX_EVENTS = 1000;
/** observe_events caps limit at 500; the first poll catches up in two rounds at most. */
const EVENTS_POLL_LIMIT = 500;

const EMPTY_TIMELINE: TimelineState = { journal: {} };
/** route_state every second while the Route page is visible; 5 s between retries while it fails. */
export const ROUTE_POLL_MS = 1000;
const ROUTE_RETRY_MS = 5000;
/** feedback_sessions every 3 s while the F8 page is visible and the host can't push; 10 s between retries while it fails. */
export const F8_POLL_MS = 3000;
const F8_RETRY_MS = 10_000;
export const F8_URI = "exile://feedback/sessions";
/** healthbars_state every second while the Health Bars page is visible; 5 s between retries while it fails; hud_plugin_perf every 5th read. */
export const HB_POLL_MS = 1000;
const HB_RETRY_MS = 5000;
const HB_PERF_EVERY = 5;
/** The stock health bar plugin's name in the HUD's perf counters (the comparison on the Health Bars page). */
export const STOCK_HEALTHBARS = "HealthBars";

export class ControlStore {
  private snap: Snapshot = {
    route: { page: "overview" }, nav: { loading: false, live: false, reads: 0 }, f8: { loading: false, live: false, via: "off" }, hb: { loading: false, live: false, reads: 0, previewing: false }, loadingCatalog: false, games: [],
    health: { watching: false, loading: false, via: "off", history: [] },
    observer: { events: [], seq: 0, busy: false },
    timeline: EMPTY_TIMELINE,
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
  private navTimer?: ReturnType<typeof setTimeout>;
  private navGen = 0;
  private f8Timer?: ReturnType<typeof setTimeout>;
  private f8Gen = 0;
  private hbTimer?: ReturnType<typeof setTimeout>;
  private hbGen = 0;
  /** Polls so far (reads and failures alike): every HB_PERF_EVERY-th one adds hud_plugin_perf. */
  private hbPolls = 0;

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
    // The Route page mounts (and switches its poll on) before the shell's effect starts the store: resume it here.
    if (this.snap.nav.live) void this.pollRoute(++this.navGen);
    if (this.snap.f8.live) this.startF8(++this.f8Gen);
    if (this.snap.hb.live) void this.pollHealthBars(++this.hbGen);
  }

  stop() {
    this.running = false;
    this.gen++;
    this.healthWatchGen++;
    clearTimeout(this.eventsTimer);
    clearTimeout(this.gamesTimer);
    this.setRouteLive(false);
    this.setF8Live(false);
    this.setHealthBarsLive(false);
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
    const m = /^#\/([a-z0-9]+)(?:\/([^/]+))?(?:\/([^/]+))?/.exec(location.hash);
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
    this.set({ game, observer: { events: [], seq: 0, busy: false }, timeline: EMPTY_TIMELINE, health: { ...this.snap.health, snapshot: undefined, history: [] }, nav: { ...this.snap.nav, state: undefined, at: undefined, error: undefined }, hb: { ...this.snap.hb, state: undefined, at: undefined, error: undefined, perf: undefined } });
    this.resubscribe();
    void this.pollEvents();
    if (this.snap.nav.live) void this.pollRoute(++this.navGen);
    if (this.snap.hb.live) void this.pollHealthBars(++this.hbGen);
  }

  // ── Health Bars (the health bar plugin's state and preview) ──────

  /** Poll healthbars_state at 1 Hz while on (the Health Bars page mounts with it on, unmounts with it off); nothing while hidden. */
  setHealthBarsLive(on: boolean) {
    if (on === this.snap.hb.live) return;
    this.hbGen++;
    clearTimeout(this.hbTimer);
    this.set({ hb: { ...this.snap.hb, live: on } });
    if (on) void this.pollHealthBars(this.hbGen);
  }

  /** One read now (also what the poll does). `withPerf` adds hud_plugin_perf for the comparison with the stock plugin. */
  async refreshHealthBars(withPerf = false): Promise<void> {
    if (!this.snap.game || !this.hasTool("healthbars_state")) return;
    const gen = this.hbGen;
    this.set({ hb: { ...this.snap.hb, loading: true } });
    const t0 = Date.now();
    try {
      const perfCall = withPerf && this.hasTool("hud_plugin_perf") ? this.call("hud_plugin_perf", this.gameArgs(), { quiet: true }).catch(() => undefined) : undefined;
      const r = await this.call("healthbars_state", this.gameArgs(), { quiet: true });
      if (gen !== this.hbGen) return;
      if (r.isError) {
        const d = r.data as RouteError | undefined;
        this.set({ hb: { ...this.snap.hb, loading: false, error: d?.error ? d : { error: "error", message: r.text || "healthbars_state failed" } } });
        return;
      }
      const state = r.data as HealthBarsState | undefined;
      if (!state || typeof state !== "object" || !state.counts || !state.timings) throw new Error("healthbars_state returned no state");
      let perf = this.snap.hb.perf;
      const p = perfCall ? await perfCall : undefined;
      if (p && !p.isError && (p.data as PluginPerfResult | undefined)?.plugins) {
        const rows = (p.data as PluginPerfResult).plugins;
        perf = { ours: rows.find((x) => x.name === HEALTHBARS_PLUGIN), stock: rows.find((x) => x.name === STOCK_HEALTHBARS), at: Date.now(), note: (p.data as PluginPerfResult).note };
      }
      if (gen !== this.hbGen) return;
      this.set({ hb: { ...this.snap.hb, state, at: Date.now(), ms: r.ms ?? Date.now() - t0, loading: false, error: undefined, reads: this.snap.hb.reads + 1, perf } });
    } catch (e) {
      if (gen !== this.hbGen) return;
      this.set({ hb: { ...this.snap.hb, loading: false, error: { error: isUnreachable(errorText(e)) ? "unreachable" : "error", message: errorText(e) } } });
    }
  }

  private async pollHealthBars(gen: number) {
    clearTimeout(this.hbTimer);
    if (gen !== this.hbGen || !this.snap.hb.live || !this.running) return;
    if (!document.hidden && this.snap.game && this.snap.catalog) await this.refreshHealthBars(this.hbPolls++ % HB_PERF_EVERY === 0);
    if (gen !== this.hbGen || !this.snap.hb.live || !this.running) return;
    this.hbTimer = setTimeout(() => void this.pollHealthBars(gen), this.snap.hb.error ? HB_RETRY_MS : HB_POLL_MS);
  }

  /** Show the plugin's sample bars in game (healthbars_preview): the focused kind first, `stress` extra bars for a cost test. */
  async previewHealthBars(seconds = 8, focus?: string, stress = 0, quiet = false): Promise<HealthBarsPreviewResult | undefined> {
    if (!this.snap.game) { this.toast("error", "No HUD is selected", "Preview in game"); return undefined; }
    if (!this.hasTool("healthbars_preview")) { this.toast("error", "This server has no healthbars_preview tool: deploy a newer build (mcp_deploy).", "Preview in game"); return undefined; }
    this.set({ hb: { ...this.snap.hb, previewing: true } });
    try {
      const args = this.gameArgs({ seconds, ...(focus ? { focus } : {}), ...(stress > 0 ? { stress } : {}) });
      const r = await this.call("healthbars_preview", args);
      if (r.isError) {
        const d = r.data as RouteError | undefined;
        throw new Error(d?.message ?? r.text ?? "healthbars_preview failed");
      }
      const res = r.data as HealthBarsPreviewResult;
      this.set({ hb: { ...this.snap.hb, previewing: false, lastPreview: res, previewAt: Date.now() } });
      const how = res.via === "pubsub" ? `published healthbars.preview${res.seq != null ? ` #${res.seq}` : ""}` : "called WhatsAHealthBar.Preview directly";
      // Auto previews ("live in game") stay quiet: the header badge shows them; only errors toast.
      if (!quiet) this.toast("success", `${seconds} s${focus ? `, ${focus} first` : ""}${stress > 0 ? `, ${stress} stress bars` : ""} · ${how}`, "Showing in game");
      // The state shows the preview running within a second; read it now so the badge appears at once.
      void this.refreshHealthBars();
      return res;
    } catch (e) {
      this.set({ hb: { ...this.snap.hb, previewing: false } });
      this.toast("error", errorText(e), "Preview in game");
      return undefined;
    }
  }

  // ── Route (the navigation plugin's state) ────────────────────────

  /** Poll route_state at 1 Hz while on (the Route page mounts with it on, unmounts with it off); nothing while hidden. */
  setRouteLive(on: boolean) {
    if (on === this.snap.nav.live) return;
    this.navGen++;
    clearTimeout(this.navTimer);
    this.set({ nav: { ...this.snap.nav, live: on } });
    if (on) void this.pollRoute(this.navGen);
  }

  /** One read now (also what the poll does). */
  async refreshRoute(): Promise<void> {
    if (!this.snap.game || !this.hasTool("route_state")) return;
    const gen = this.navGen;
    this.set({ nav: { ...this.snap.nav, loading: true } });
    const t0 = Date.now();
    try {
      const r = await this.call("route_state", this.gameArgs(), { quiet: true });
      if (gen !== this.navGen) return;
      if (r.isError) {
        const d = r.data as RouteError | undefined;
        this.set({ nav: { ...this.snap.nav, loading: false, error: d?.error ? d : { error: "error", message: r.text || "route_state failed" } } });
        return;
      }
      const state = r.data as RouteState | undefined;
      if (!state || typeof state !== "object" || !Array.isArray(state.steps)) throw new Error("route_state returned no route state");
      this.set({ nav: { ...this.snap.nav, state, at: Date.now(), ms: r.ms ?? Date.now() - t0, loading: false, error: undefined, reads: this.snap.nav.reads + 1 } });
    } catch (e) {
      if (gen !== this.navGen) return;
      this.set({ nav: { ...this.snap.nav, loading: false, error: { error: isUnreachable(errorText(e)) ? "unreachable" : "error", message: errorText(e) } } });
    }
  }

  private async pollRoute(gen: number) {
    clearTimeout(this.navTimer);
    if (gen !== this.navGen || !this.snap.nav.live || !this.running) return;
    if (!document.hidden && this.snap.game && this.snap.catalog) await this.refreshRoute();
    if (gen !== this.navGen || !this.snap.nav.live || !this.running) return;
    const wait = this.snap.nav.error ? ROUTE_RETRY_MS : ROUTE_POLL_MS;
    this.navTimer = setTimeout(() => void this.pollRoute(gen), wait);
  }

  // ── F8 sessions (the feedback dispatcher's presses) ──────────────

  /** Follow feedback_sessions while on: push over exile://feedback/sessions where the host can listen (the server
   *  watches the dispatcher's files), else a 3 s poll while visible. The F8 page mounts with it on, unmounts with it off. */
  setF8Live(on: boolean) {
    if (on === this.snap.f8.live) return;
    this.f8Gen++;
    clearTimeout(this.f8Timer);
    this.set({ f8: { ...this.snap.f8, live: on, via: on ? (this.host.listen ? "push" : "poll") : "off" } });
    if (this.host.listen) this.resubscribe();
    if (on) this.startF8(this.f8Gen);
  }

  private startF8(gen: number) {
    if (this.host.listen) { void this.refreshF8(); return; }   // then the subscription re-reads on every change
    void this.pollF8(gen);
  }

  /** One read now (what the poll and the push do too). seq: also that press's brief and stderr, into `detail`. */
  async refreshF8(): Promise<void> {
    if (this.snap.catalog && !this.hasTool("feedback_sessions")) return;
    const gen = this.f8Gen;
    this.set({ f8: { ...this.snap.f8, loading: true } });
    const t0 = Date.now();
    try {
      // The bridge merge needs a HUD; with none up (or none known yet) the files alone are the answer.
      const r = await this.call("feedback_sessions", { limit: 100, merge: this.gamesUp.length > 0 }, { quiet: true });
      if (gen !== this.f8Gen) return;
      if (r.isError) throw new Error(r.text || "feedback_sessions failed");
      this.applyF8(r.data as FeedbackSessionsResult, r.ms ?? Date.now() - t0);
    } catch (e) {
      if (gen !== this.f8Gen) return;
      this.set({ f8: { ...this.snap.f8, loading: false, error: errorText(e) } });
    }
  }

  private applyF8(result: FeedbackSessionsResult | undefined, ms?: number) {
    if (!result || !Array.isArray(result.sessions) || !result.dispatcher) throw new Error("feedback_sessions returned no sessions");
    this.set({ f8: { ...this.snap.f8, result, at: Date.now(), ms, loading: false, error: undefined } });
  }

  private async pollF8(gen: number) {
    clearTimeout(this.f8Timer);
    if (gen !== this.f8Gen || !this.snap.f8.live || !this.running) return;
    if (!document.hidden && this.snap.catalog) await this.refreshF8();
    if (gen !== this.f8Gen || !this.snap.f8.live || !this.running) return;
    this.f8Timer = setTimeout(() => void this.pollF8(gen), this.snap.f8.error ? F8_RETRY_MS : F8_POLL_MS);
  }

  /** The press shown in the detail pane (#/f8/<seq>); reads its brief and stderr once. */
  selectF8(seq: number | undefined) {
    this.go("f8", seq === undefined ? undefined : String(seq));
    if (seq === undefined || this.snap.f8.detail?.seq === seq) return;
    void this.loadF8Detail(seq);
  }

  async loadF8Detail(seq: number) {
    this.set({ f8: { ...this.snap.f8, detail: { seq, loading: true } } });
    try {
      const r = await this.call("feedback_sessions", { seq, merge: false }, { quiet: true });
      if (this.snap.f8.detail?.seq !== seq) return;
      if (r.isError) throw new Error(r.text || "feedback_sessions failed");
      const s = (r.data as FeedbackSessionsResult | undefined)?.sessions?.[0] as FeedbackSession | undefined;
      this.set({ f8: { ...this.snap.f8, detail: { seq, loading: false, brief: s?.brief ?? null, stderr: s?.stderr ?? null } } });
    } catch (e) {
      if (this.snap.f8.detail?.seq !== seq) return;
      this.set({ f8: { ...this.snap.f8, detail: { seq, loading: false, error: errorText(e) } } });
    }
  }

  /** Does the server offer this tool? (observe_series may not exist yet on an older server.) */
  hasTool(name: string): boolean { return !!this.snap.catalog?.tools.some((t) => t.name === name); }

  get gamesUp(): Game[] { return this.snap.games.filter((g) => g.status === "connected").map((g) => g.game as Game); }

  private gameArgs(extra: Record<string, unknown> = {}): Record<string, unknown> {
    return this.snap.game ? { game: this.snap.game, ...extra } : extra;
  }

  // ── Push (standalone) ────────────────────────────────────────────

  /** Subscribe to the observer and (while watching) the perf report of the selected game, when the host can listen. */
  private resubscribe() {
    if (!this.host.listen || !this.running) return;
    const g = this.snap.game;
    const uris = [
      ...(g ? [`exile://observe/${g}/events`, `exile://observe/${g}/layers`, ...(this.snap.health.watching ? [`exile://perf/${g}/report`] : [])] : []),
      ...(this.snap.f8.live ? [F8_URI] : []),
    ];
    if (uris.join() === this.subscribed.join()) return;
    this.unlisten?.();
    this.subscribed = uris;
    if (!uris.length) { this.set({ listen: "off" }); return; }
    this.unlisten = this.host.listen(uris, (uri) => void this.onUpdated(uri), (s) => this.set({ listen: s }));
    // What is there now: a subscription only says when something changes.
    for (const u of uris) if (!u.includes("/perf/") && u !== F8_URI) void this.onUpdated(u);
  }

  private async onUpdated(uri: string) {
    if (!this.host.readResource) return;
    try {
      if (uri === F8_URI) {
        const t0 = Date.now();
        const { json } = await this.host.readResource(uri);
        if (this.snap.f8.live) this.applyF8(json as FeedbackSessionsResult, Date.now() - t0);
      } else if (uri.endsWith("/events")) {
        // The resource carries the newest 100; the first time, fill the ring from the tool (up to 1000, the bridge's own).
        if (this.snap.observer.seq === 0) await this.catchUpEvents();
        const { json } = await this.host.readResource(uri);
        const r = json as ObserveEventsResult;
        // More than 100 events since the last read (a burst of stat changes does that): the resource skipped some, so
        // page through them with the tool first.
        const oldest = r?.events?.[0]?.seq;
        if (oldest !== undefined && oldest > this.snap.observer.seq + 1) await this.catchUpEvents();
        this.applyEvents(r);
      } else if (uri.endsWith("/layers")) {
        const { json } = await this.host.readResource(uri);
        const r = json as LayersResult;
        if (r?.layers) this.set({ observer: { ...this.snap.observer, layers: r, layersAt: Date.now() } });
      } else if (uri.includes("/perf/")) {
        const { json } = await this.host.readResource(uri);
        this.applyHealth(json as PerfSnapshot);
      }
    } catch (e) {
      this.toast("error", `${uri}: ${errorText(e)}`, "Could not read");
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
      this.toast(on ? "success" : "info", on ? "The HUD records what happens in game (read-only)" : "Nothing is recorded any more", on ? "Observing" : "Observation stopped");
      await this.refreshObserver();
    } catch (e) {
      this.set({ observer: { ...this.snap.observer, busy: false } });
      this.toast("error", errorText(e));
    }
  }

  /** Everything the bridge still holds after our seq: observe_events {since, limit: 500}, oldest first, page by page. */
  private async catchUpEvents() {
    const gen = this.gen;
    for (let round = 0; round < 4 && this.snap.game; round++) {
      try {
        const r = await this.call("observe_events", this.gameArgs({ since: this.snap.observer.seq, limit: EVENTS_POLL_LIMIT }), { quiet: true });
        if (gen !== this.gen || r.isError) return;
        const d = r.data as ObserveEventsResult;
        this.applyEvents(d);
        if (!d?.events || d.events.length < EVENTS_POLL_LIMIT) return;
      } catch { return; }
    }
  }

  /** The live events feed: push re-reads the resource; otherwise observe_events {since} every 2.5 s while visible. */
  private async pollEvents() {
    clearTimeout(this.eventsTimer);
    if (!this.running) return;
    const gen = this.gen;
    if (!this.host.listen && !document.hidden && this.snap.game && this.snap.catalog) {
      try {
        const r = await this.call("observe_events", this.gameArgs({ since: this.snap.observer.seq, limit: EVENTS_POLL_LIMIT }), { quiet: true });
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
    // r.seq is the bridge's newest, but a full page stops short of it: continue from the last event received, or the
    // next page would skip everything in between (the timeline then showed a stale batch and nothing after it).
    const lastSeen = r.events.length ? r.events[r.events.length - 1].seq : o.seq;
    const seq = r.events.length >= EVENTS_POLL_LIMIT ? Math.max(o.seq, lastSeen) : Math.max(o.seq, lastSeen, r.seq ?? 0);
    this.set({ observer: { ...o, events, seq, status, lastEventAt: fresh.length ? Date.now() : o.lastEventAt } });
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
      if (d?.preflight) this.toast("error", `${spec.id}: ${d.preflight}`, "Stored, preflight failed");
      else this.toast(spec.enabled ? "success" : "info", spec.id, spec.enabled ? "Layer watching" : "Layer paused");
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
      this.toast("info", id, "Layer removed");
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

  // ── Timeline ─────────────────────────────────────────────────────

  private patchTimeline(patch: Partial<TimelineState>) { this.set({ timeline: { ...this.snap.timeline, ...patch } }); }

  /** Every event the timeline knows: the memory ring plus what journal reads brought back, by seq. */
  eventBySeq(seq: number): ObserveEvent | undefined {
    return this.snap.observer.events.find((e) => e.seq === seq) ?? this.snap.timeline.journal[seq];
  }

  /** Select an event (undefined clears). Reads what happened around it; for a layer event also its unit's companions
   *  and, when the server offers observe_series, its series. Reads for the previous selection are dropped. */
  selectTimelineEvent(seq: number | undefined, windowMs = 1000) {
    if (seq === this.snap.timeline.selected) return;
    this.patchTimeline({ selected: seq, around: undefined, companions: undefined, series: undefined });
    if (seq === undefined) return;
    void this.loadAround(seq, windowMs);
    const e = this.eventBySeq(seq);
    if (e?.kind === "layer" && e.layer && e.unit) {
      void this.loadCompanions(e.layer, e.unit, windowMs);
      if (this.hasTool("observe_series")) void this.loadSeries(e.layer, e.unit);
    }
  }

  async loadAround(seq: number, windowMs = 1000) {
    this.patchTimeline({ around: { seq, windowMs, loading: true, result: this.snap.timeline.around?.seq === seq ? this.snap.timeline.around.result : undefined } });
    try {
      const r = await this.call("observe_timeline", this.gameArgs({ around: seq, windowMs }));
      if (this.snap.timeline.selected !== seq) return;
      if (r.isError) throw new Error(r.text || "observe_timeline failed");
      const result = r.data as TimelineResult;
      // Journal events the ring no longer has become drawable too.
      const known = new Set(this.snap.observer.events.map((e) => e.seq));
      let journal = this.snap.timeline.journal;
      for (const e of result?.events ?? []) if (!known.has(e.seq) && !journal[e.seq]) journal = { ...journal, [e.seq]: e };
      this.patchTimeline({ around: { seq, windowMs, loading: false, result }, journal });
    } catch (e) {
      if (this.snap.timeline.selected !== seq) return;
      this.patchTimeline({ around: { seq, windowMs, loading: false, error: errorText(e) } });
    }
  }

  async loadCompanions(layer: string, unit: string, windowMs = 1000) {
    const selected = this.snap.timeline.selected;
    this.patchTimeline({ companions: { layer, unit, windowMs, loading: true } });
    try {
      const r = await this.call("observe_timeline", this.gameArgs({ layer, unit, windowMs }));
      if (this.snap.timeline.selected !== selected) return;
      if (r.isError) throw new Error(r.text || "observe_timeline failed");
      this.patchTimeline({ companions: { layer, unit, windowMs, loading: false, result: r.data as TimelineResult } });
    } catch (e) {
      if (this.snap.timeline.selected !== selected) return;
      this.patchTimeline({ companions: { layer, unit, windowMs, loading: false, error: errorText(e) } });
    }
  }

  async loadSeries(layer: string, unit: string, windowMs?: number) {
    const selected = this.snap.timeline.selected;
    this.patchTimeline({ series: { layer, unit, loading: true } });
    try {
      const r = await this.call("observe_series", this.gameArgs(windowMs ? { layer, unit, windowMs } : { layer, unit }));
      if (this.snap.timeline.selected !== selected) return;
      if (r.isError) throw new Error(r.text || "observe_series failed");
      this.patchTimeline({ series: { layer, unit, loading: false, result: r.data as SeriesResult } });
    } catch (e) {
      if (this.snap.timeline.selected !== selected) return;
      this.patchTimeline({ series: { layer, unit, loading: false, error: errorText(e) } });
    }
  }

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
      this.toast("error", errorText(e), node.label);
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
    if (ok) this.toast("success", u.label, "Undone");
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

  toast(kind: Toast["kind"], text: string, title?: string) {
    const id = ++this.toastSeq;
    this.set({ toasts: [...this.snap.toasts, { id, kind, text, title }] });
    setTimeout(() => this.dismissToast(id), kind === "error" ? 8000 : 4000);
  }

  dismissToast(id: number) { this.set({ toasts: this.snap.toasts.filter((t) => t.id !== id) }); }
}
