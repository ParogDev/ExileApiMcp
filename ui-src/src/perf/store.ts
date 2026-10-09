// PerfStore: framework-free state for the HUD performance panel. Owns the latest report (seeded from
// show_hud_performance's result, refreshed through the same tool), the one-click action runs (profile_plugin,
// hud_plugin_lint, overlay_accuracy) with their results, auto-refresh, and toasts. Immutable snapshots for
// useSyncExternalStore; every mutation replaces the snapshot object.

import type { Toast } from "../sync";
import { isInstrumentationOff, isUnreachable, traceNote } from "./model";
import { asError, type Action, type Game, type Report } from "./types";

export type CallTool = (name: string, args: Record<string, unknown>) => Promise<{ data: unknown; isError: boolean; text?: string }>;

/** The trace itself is 3 s; the server waits 3.3 s and polls, plus patching: what a refresh usually takes. */
export const EXPECTED_REFRESH_MS = 3600;
/** Auto-refresh spacing: each refresh patches the HUD for 3 s, so leave it alone between runs. */
export const AUTO_REFRESH_MS = 15_000;
/** How long profile_plugin usually takes (4 s run + patching). */
export const EXPECTED_ACTION_MS: Record<string, number> = { profile_plugin: 4800, hud_plugin_lint: 1500, overlay_accuracy: 5500 };

export type Conn = "idle" | "live" | "offline" | "no-instrumentation";

export interface ActionRun {
  key: string;
  action: Action;
  status: "running" | "done" | "error";
  startedAt: number;
  endedAt?: number;
  data?: unknown;
  text?: string;
  error?: string;
}

/** One past refresh, for the small trend behind the headline numbers. */
export interface HistoryPoint {
  at: number;
  fps: number;
  p95Ms?: number;
  maxMs?: number;
  gcPauseMs?: number;
}

export interface Snapshot {
  game?: Game;
  report?: Report;
  reportText?: string;
  reportAt?: number;
  /** Why the trace is missing, from the text ("bridge unreachable (...)" / the instrumentation message). */
  traceNote?: string;
  loading: boolean;
  loadStartedAt?: number;
  conn: Conn;
  lastError?: string;
  autoRefresh: boolean;
  runs: ReadonlyMap<string, ActionRun>;
  /** Newest first. */
  runOrder: string[];
  selectedPlugin?: string;
  history: HistoryPoint[];
  calls: number;
  toasts: Toast[];
}

export class PerfStore {
  private snap: Snapshot = { loading: false, conn: "idle", autoRefresh: false, runs: new Map(), runOrder: [], history: [], calls: 0, toasts: [] };
  private listeners = new Set<() => void>();
  private gameArg?: Game;
  private startTimer?: ReturnType<typeof setTimeout>;
  private autoTimer?: ReturnType<typeof setInterval>;
  private toastSeq = 0;
  private seeded = false;
  private refreshing?: Promise<void>;

  constructor(private call: CallTool) {}

  subscribe = (fn: () => void) => { this.listeners.add(fn); return () => { this.listeners.delete(fn); }; };
  getSnapshot = () => this.snap;

  private set(patch: Partial<Snapshot>) {
    this.snap = { ...this.snap, ...patch };
    for (const l of this.listeners) l();
  }

  // ── Lifecycle ────────────────────────────────────────────────────

  private gotToolInput = false;

  /** The arguments of the tool that opened the app (show_hud_performance {game?}). The host sends them when the tool
   *  starts, ~3.5 s before its result: the panel shows the trace in progress from then on. */
  setToolInput(args: Record<string, unknown> | undefined) {
    const game = args?.game;
    if (game === "poe1" || game === "poe2") { this.gameArg = game; this.set({ game }); }
    this.gotToolInput = true;
    if (!this.snap.report && !this.snap.loading) this.set({ loading: true, loadStartedAt: Date.now() });
  }

  /** The host's tool result (show_hud_performance): the first report. */
  seed(data: unknown, text?: string) {
    clearTimeout(this.startTimer);
    this.seeded = true;
    this.applyReport(data, text);
  }

  /** Called once the host is connected: if no tool result arrives (the app was reopened), fetch one. A host that sent
   *  the tool input is still tracing (~3.5 s), so that case waits longer before giving up on it. */
  start() {
    clearTimeout(this.startTimer);
    const arm = () => {
      this.startTimer = setTimeout(() => {
        if (this.seeded || this.snap.report) return;
        if (this.gotToolInput && Date.now() - (this.snap.loadStartedAt ?? 0) < 8000) { arm(); return; }
        if (this.snap.loading) this.set({ loading: false });
        void this.refresh();
      }, 2000);
    };
    arm();
  }

  stop() {
    clearTimeout(this.startTimer);
    clearInterval(this.autoTimer);
    this.autoTimer = undefined;
  }

  setAutoRefresh(on: boolean) {
    clearInterval(this.autoTimer);
    this.autoTimer = undefined;
    if (on) this.autoTimer = setInterval(() => { if (!document.hidden) void this.refresh(); }, AUTO_REFRESH_MS);
    this.set({ autoRefresh: on });
  }

  // ── The report ───────────────────────────────────────────────────

  /** Re-run show_hud_performance: ~3.5 s, during which the HUD is traced. One at a time. */
  refresh(): Promise<void> {
    if (this.refreshing) return this.refreshing;
    this.set({ loading: true, loadStartedAt: Date.now(), calls: this.snap.calls + 1 });
    this.refreshing = (async () => {
      try {
        const r = await this.call("show_hud_performance", this.gameArg ? { game: this.gameArg } : {});
        this.applyReport(r.data, r.text);
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        this.set({ loading: false, conn: "offline", lastError: msg });
      } finally {
        this.refreshing = undefined;
        if (this.snap.loading) this.set({ loading: false });
      }
    })();
    return this.refreshing;
  }

  private applyReport(data: unknown, text?: string) {
    const err = asError(data);
    if (err) {
      this.set({ loading: false, conn: "offline", lastError: err.message ?? err.error, reportText: text });
      return;
    }
    const report = (data && typeof data === "object" ? data : {}) as Report;
    const note = traceNote(text);
    const conn: Conn = report.trace ? "live" : isInstrumentationOff(note) ? "no-instrumentation" : "offline";
    const history = report.trace
      ? [...this.snap.history, { at: Date.now(), fps: report.trace.hudFps, p95Ms: report.trace.frameIntervalMs.p95, maxMs: report.trace.frameIntervalMs.max, gcPauseMs: report.trace.gc?.pauseMsTotal }].slice(-24)
      : this.snap.history;
    this.set({
      report: report.trace ? report : { ...(this.snap.report ?? {}), desktop: report.desktop ?? this.snap.report?.desktop, ...(this.snap.report?.trace ? {} : report) },
      reportText: text, reportAt: Date.now(), traceNote: report.trace ? undefined : note, loading: false, conn,
      lastError: report.trace ? undefined : note ?? (isUnreachable(note) ? note : this.snap.lastError), history,
      selectedPlugin: this.snap.selectedPlugin && report.plugins?.some((p) => p.name === this.snap.selectedPlugin) ? this.snap.selectedPlugin : undefined,
    });
  }

  selectPlugin(name: string | undefined) {
    this.set({ selectedPlugin: this.snap.selectedPlugin === name ? undefined : name });
  }

  // ── Actions ──────────────────────────────────────────────────────

  static runKey(a: Action): string {
    return `${a.tool}:${JSON.stringify(a.args ?? {})}`;
  }

  /** Run one of the report's next steps through the host; the result lands in `runs`. */
  async run(action: Action): Promise<void> {
    const key = PerfStore.runKey(action);
    const existing = this.snap.runs.get(key);
    if (existing?.status === "running") return;
    const run: ActionRun = { key, action, status: "running", startedAt: Date.now() };
    this.putRun(run, true);
    this.set({ calls: this.snap.calls + 1 });
    try {
      const args = { ...(action.args ?? {}) };
      if (this.gameArg && !("game" in args)) args.game = this.gameArg;
      const r = await this.call(action.tool, args);
      const err = asError(r.data);
      if (err || r.isError) {
        this.putRun({ ...run, status: "error", endedAt: Date.now(), error: err?.message ?? err?.error ?? r.text ?? "The tool reported an error", data: r.data, text: r.text });
      } else {
        this.putRun({ ...run, status: "done", endedAt: Date.now(), data: r.data, text: r.text });
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      this.putRun({ ...run, status: "error", endedAt: Date.now(), error: msg });
      if (isUnreachable(msg)) this.set({ conn: "offline", lastError: msg });
    }
  }

  dismissRun(key: string) {
    const runs = new Map(this.snap.runs);
    runs.delete(key);
    this.set({ runs, runOrder: this.snap.runOrder.filter((k) => k !== key) });
  }

  private putRun(run: ActionRun, toFront = false) {
    const runs = new Map(this.snap.runs);
    runs.set(run.key, run);
    const order = toFront ? [run.key, ...this.snap.runOrder.filter((k) => k !== run.key)] : this.snap.runOrder;
    this.set({ runs, runOrder: order });
  }

  // ── Toasts ───────────────────────────────────────────────────────

  toast(kind: Toast["kind"], text: string) {
    const id = ++this.toastSeq;
    this.set({ toasts: [...this.snap.toasts, { id, kind, text }] });
    setTimeout(() => this.dismissToast(id), kind === "error" ? 6000 : 2500);
  }
  dismissToast(id: number) {
    this.set({ toasts: this.snap.toasts.filter((t) => t.id !== id) });
  }
}
