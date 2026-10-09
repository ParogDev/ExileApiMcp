// MemoryStore: framework-free state for the memory view. Owns the history of views (a struct layout or a raw
// read, each with its region model), the selection and hover, watch results as per-byte changes, live re-reads
// and toasts. Immutable snapshots for useSyncExternalStore; every mutation replaces the snapshot object.

import type { Toast } from "../sync";
import { addrPlus, bytesFromHex, isPathText, parseAddress, pathLabel, regionFromLayout, regionFromRead, shortAddr, type Region } from "./bytes";
import type { AwaitResult, ChangedRange, CompareResult, CorrelateResult, ExperimentPreset, ExperimentRecordInfo, FindingStatus, FindingsResult, Game, GuideState, LayoutResult, MemoryError, PopulationResult, PresetsResult, ReadResult, SnapshotList, SnapshotSaved, StatusResult, StepCancelled, StepStarted, StepState, SummaryResult, VerifyResult, WatchResult, WhereResult } from "./types";
import { asFieldAccess, classifyCodeError, codeKey, type CodeQuery } from "./codeModel";
import { evidenceRows, guideExperiment, labelCounts, stepOver, undoList } from "./runModel";

export type CallTool = (name: string, args: Record<string, unknown>) => Promise<{ data: unknown; isError: boolean; text?: string }>;
type ToolOutcome = { data: unknown } | { error: MemoryError };

export const WATCH_MS = 5000;
export const WATCH_INTERVAL_MS = 100;
export const LIVE_MS = 1000;
export const DEFAULT_EXTEND = 64;
export const READ_SIZE = 256;
/** How long a byte changed by a live re-read flashes. */
export const LIVE_FLASH_MS = 1800;

export interface Target {
  path?: string;
  address?: string;
  /** Layout: struct type to overlay instead of the HUD's own. */
  type?: string;
  /** Layout: bytes to read past the struct end. */
  extend?: number;
  /** Read: offset into the target and size. */
  offset?: number;
  size?: number;
}

export interface View {
  id: number;
  mode: "layout" | "read";
  target: Target;
  /** Crumb label: "Life", "PlayerStashTabs[0]", "…889400". */
  label: string;
  /** How this view was reached from the previous one: "+8 Owner" (a followed pointer). */
  via?: string;
  data?: LayoutResult | ReadResult;
  region?: Region;
  loading: boolean;
  error?: MemoryError;
  loadedAt?: number;
}

export interface Selection {
  off: number;
  size: number;
  /** The segment this selection is (field, candidate, gap, slot); absent for an ad-hoc byte range. */
  segId?: string;
}

export interface ByteChange {
  /** 0..1, how late in the watch this byte last changed. */
  recency: number;
  count: number;
  noisy: boolean;
  unmapped: boolean;
}

export interface WatchState {
  viewId: number;
  status: "running" | "done" | "error";
  startedAt: number;
  durationMs: number;
  result?: WatchResult;
  error?: string;
}

export interface WhereState {
  address: string;
  loading: boolean;
  data?: WhereResult;
  error?: string;
}

export type Mode = "struct" | "population" | "experiments" | "findings";

/** Population mode: every item of a collection, bytes × items, with the bits a label explains. */
export interface PopState {
  path: string;
  labels: string[];
  loading: boolean;
  error?: string;
  data?: PopulationResult;
  /** Per-item bytes, parsed from `data.items[i].hex`. */
  bytes?: Uint8Array[];
  /** The struct the HUD reads for one item (field names over the byte columns). */
  layout?: LayoutResult;
  correlating: boolean;
  correlate?: CorrelateResult;
  correlateError?: string;
  /** The byte / bit the user is looking at. */
  bit?: { byte: number; bit: number };
  /** Label the rows are grouped by. */
  groupBy?: string;
  /** Item index under the pointer. */
  hoverItem?: number;
  loadedAt?: number;
}

/** Experiments mode: named snapshots and their step-by-step comparison. */
export interface ExpState {
  /** Which half of the tab: the guided-experiment runner, or the snapshot timeline. */
  view: "run" | "snapshots";
  listing: boolean;
  list?: { name: string; savedAt: string }[];
  error?: string;
  /** Names in experiment order. */
  selected: string[];
  comparing: boolean;
  compare?: CompareResult;
  compareError?: string;
  /** Selected step index into compare.steps. */
  step: number;
  saving: boolean;
  saved?: SnapshotSaved;
  saveError?: string;
}

/**
 * How long a step waits for the user's action. The step runs in the server (experiment_step_start) and the app only
 * polls, so the host's request timeout no longer caps it: up to 5 minutes, 2 by default.
 */
export const RUN_TIMEOUTS_MS = [30_000, 60_000, 120_000, 300_000];
export const RUN_DEFAULT_TIMEOUT_MS = 120_000;
/** Each preset action is asked for this many times before "finish" stops nudging. */
export const RUN_TARGET_REPEATS = 2;
/** experiment_status every second while the runner is open; guide_state every other tick; the record every fourth. */
export const STATUS_POLL_MS = 1000;
export const GUIDE_POLL_TICKS = 2;
export const SUMMARY_POLL_TICKS = 4;
/** In the pick phase, the records list every 5 s: a record growing on its own is someone else's run. */
export const RECORDS_POLL_TICKS = 5;
/** The "Before you start" checklist. The preset's own setup line is the first item. */
export const RUN_CHECKS = ["setup", "focus", "one-action"] as const;

export type RunPhase = "pick" | "setup" | "run" | "done";

export interface RunAttempt {
  at: number;
  label: string;
  /** The step's result, or why it ended without one (cancelled; the server lost it; the start call failed). */
  result: AwaitResult | { error: string } | { cancelled: true };
}

/** The guided-experiment runner: a preset run from the app step by step, or the agent's run followed read-only. */
export interface RunState {
  presetsLoading: boolean;
  presets?: ExperimentPreset[];
  records?: ExperimentRecordInfo[];
  presetsError?: string;
  phase: RunPhase;
  preset?: ExperimentPreset;
  /** Record name the steps go to (defaults to the preset id). */
  experiment: string;
  checks: ReadonlySet<string>;
  timeoutMs: number;
  /** Index into preset.steps of the action the card shows. */
  stepIndex: number;
  /** The step this app started and hasn't seen finish yet. `startedAt` is the server's ISO stamp (matches experiment_status). */
  waiting?: { label: string; startedAt: string; timeoutMs: number; step: number; steps: number; cancelling?: boolean };
  /** Every step this session, newest last. */
  attempts: readonly RunAttempt[];
  /** experiment_status for the chosen experiment: the step the server is running (ours or the agent's), or the last one. */
  step?: StepState;
  /** Client-clock time that step started (from the server's elapsedMs), for the countdown. */
  stepSince?: number;
  /** Whether the server is running a step of this experiment right now. */
  stepRunning?: boolean;
  /** How many steps the record held at the last status poll; growth this app didn't cause means someone else is running it. */
  recordedSteps?: number;
  statusAt?: number;
  summary?: SummaryResult;
  summaryLoading: boolean;
  summaryError?: string;
  summaryAt?: number;
  /** The in-game card, polled read-only (guide_state) while the runner is open. */
  guide?: GuideState;
  guideAt?: number;
  /** When the current guide instruction appeared, as far as this app saw it. */
  guideSince?: number;
  /** Set while the agent (not this app) is running an experiment: the record name. */
  following?: string;
  /** Whether the "done" card was pushed to the in-game guide for the current run. */
  markedDone?: boolean;
}

/** Findings mode: the registry with a status per game, and verification runs. */
export interface FndState {
  loading: boolean;
  data?: FindingsResult;
  error?: string;
  filter: string;
  status: FindingStatus | "all" | "toCheck";
  verifying: ReadonlySet<string>;
  verified: ReadonlyMap<string, VerifyResult | { error: string }>;
  /** Expanded finding ids. */
  open: ReadonlySet<string>;
}

export interface Snapshot {
  game?: Game;
  mode: Mode;
  pop: PopState;
  exp: ExpState;
  run: RunState;
  fnd: FndState;
  views: readonly View[];
  index: number;
  selection?: Selection;
  /** Bit picked in the inspector's bit grid, numbered from the selection's first byte (0 = bit 0 of byte `off`). */
  bitSel?: number;
  /** Bits the code panel is pointing at (same numbering), lit in the bit grid while hovering an instruction. */
  bitHover?: number[];
  /** find_field_access lookups this session, by struct, offset and bit. Cached results stay for later selections. */
  code: ReadonlyMap<string, CodeQuery>;
  hover?: { off: number; size: number };
  watch?: WatchState;
  /** Byte offset -> what the last watch saw there (for the current watch's view). */
  changes: ReadonlyMap<number, ByteChange>;
  live: boolean;
  /** Byte offset -> when a live re-read saw it change. */
  liveFlash: ReadonlyMap<number, number>;
  where?: WhereState;
  conn: "idle" | "live" | "offline";
  lastError?: string;
  calls: number;
  toasts: Toast[];
}

export class MemoryStore {
  private snap: Snapshot = {
    mode: "struct",
    pop: { path: "", labels: [], loading: false, correlating: false },
    exp: { view: "run", listing: false, selected: [], comparing: false, step: 0, saving: false },
    run: { presetsLoading: false, phase: "pick", experiment: "", checks: new Set(), timeoutMs: RUN_DEFAULT_TIMEOUT_MS, stepIndex: 0, attempts: [], summaryLoading: false },
    fnd: { loading: false, filter: "", status: "all", verifying: new Set(), verified: new Map(), open: new Set() },
    views: [], index: -1, code: new Map(), changes: new Map(), live: false, liveFlash: new Map(), conn: "idle", calls: 0, toasts: [],
  };
  private listeners = new Set<() => void>();
  private gameArg?: Game;
  private startTimer?: ReturnType<typeof setTimeout>;
  private liveTimer?: ReturnType<typeof setInterval>;
  private liveBusy = false;
  private runTimer?: ReturnType<typeof setInterval>;
  private runPollBusy = false;
  private runPollTick = 0;
  private startingStep = false;
  private viewSeq = 0;
  private toastSeq = 0;
  private requested?: Target;

  constructor(private call: CallTool) {}

  subscribe = (fn: () => void) => { this.listeners.add(fn); return () => { this.listeners.delete(fn); }; };
  getSnapshot = () => this.snap;

  private set(patch: Partial<Snapshot>) {
    this.snap = { ...this.snap, ...patch };
    for (const l of this.listeners) l();
  }

  get current(): View | undefined { return this.snap.views[this.snap.index]; }

  // ── Lifecycle ────────────────────────────────────────────────────

  /**
   * Arguments of the tool that opened the app: show_memory_view {path?, address?, type?, mode?, preset?, experiment?, game?}.
   * `mode` opens a tab; with experiments, `preset` opens that preset's setup and `experiment` a record's summary.
   */
  setToolInput(args: Record<string, unknown> | undefined) {
    const game = args?.game;
    if (game === "poe1" || game === "poe2") { this.gameArg = game; this.set({ game }); }
    if (args?.mode === "experiments" || args?.mode === "population" || args?.mode === "findings") {
      this.snap = { ...this.snap, mode: args.mode };
      if (typeof args.preset === "string" && args.preset) this.snap = { ...this.snap, run: { ...this.snap.run, experiment: args.preset } };
      if (typeof args.experiment === "string" && args.experiment) this.snap = { ...this.snap, run: { ...this.snap.run, experiment: args.experiment, phase: "done" } };
      if (args.mode === "experiments") { this.setRunPolling(true); void this.loadPresets(); }
    }
    const t: Target = {};
    if (typeof args?.path === "string" && args.path.trim()) t.path = args.path.trim();
    const a = parseAddress(args?.address as string | number | undefined);
    if (a !== undefined) t.address = "0x" + a.toString(16).toUpperCase();
    if (typeof args?.type === "string" && args.type.trim()) t.type = args.type.trim();
    if (t.path || t.address) this.requested = t;
  }

  /** The host's tool result: a memory_layout result, a memory_read result, or an error. */
  seed(data: unknown) {
    clearTimeout(this.startTimer);
    const err = asError(data);
    const target: Target = this.requested ?? { path: "GameController.Player.GetComponent<Life>()" };
    if (err) {
      const mode = target.address && !target.type ? "read" : "layout";
      this.push({ ...this.newView(mode, target), loading: false, error: err });
      // The bridge answered (with an error): the connection itself is fine.
      if (err.error !== "offline") this.set({ conn: "live" });
      return;
    }
    const layout = asLayout(data), read = asRead(data);
    if (layout) {
      const t = { ...target, extend: target.extend ?? DEFAULT_EXTEND };
      if (!t.path && !t.address) t.address = layout.address;
      this.push(this.loaded(this.newView("layout", t), layout));
    } else if (read) {
      const t = { ...target, size: read.size };
      if (!t.path && !t.address) t.address = read.address;
      this.push(this.loaded(this.newView("read", t), read));
    }
  }

  /** Hosts that never send a tool result still get a view. */
  start() {
    if (this.snap.views.length || this.startTimer) return;
    this.startTimer = setTimeout(() => {
      if (!this.snap.views.length) void this.open(this.requested ?? { path: "GameController.Player.GetComponent<Life>()" });
    }, 1200);
  }

  stop() {
    clearTimeout(this.startTimer);
    this.startTimer = undefined;
    this.setLive(false);
    this.setRunPolling(false);
  }

  // ── Views and navigation ─────────────────────────────────────────

  private newView(mode: View["mode"], target: Target, via?: string): View {
    const label = target.path ? pathLabel(target.path) : target.address ? shortAddr(target.address) : "?";
    return { id: ++this.viewSeq, mode, target, label, via, loading: true };
  }

  private loaded(v: View, data: LayoutResult | ReadResult): View {
    const region = "fields" in data ? regionFromLayout(data) : regionFromRead(data);
    const game = data.game;
    if (game && !this.snap.game) this.snap = { ...this.snap, game };
    if (this.snap.conn !== "live") this.snap = { ...this.snap, conn: "live" };
    // Findings annotate the map ("the HUD doesn't map this, but we found something"): fetch them once.
    if (!this.snap.fnd.data && !this.snap.fnd.loading) void this.loadFindings();
    return { ...v, data, region, loading: false, error: undefined, loadedAt: Date.now() };
  }

  private push(v: View) {
    const views = [...this.snap.views.slice(0, this.snap.index + 1), v].slice(-40);
    this.set({ views, index: views.length - 1, selection: undefined, bitSel: undefined, bitHover: undefined, hover: undefined, changes: new Map(), liveFlash: new Map(), where: undefined, watch: this.snap.watch?.status === "running" ? this.snap.watch : undefined });
  }

  private replace(v: View) {
    const views = this.snap.views.map((x) => (x.id === v.id ? v : x));
    this.set({ views });
  }

  /** Open a target in a new history entry. Mode: layout when a path or a type is given, read for a bare address. */
  async open(target: Target, opts: { via?: string; mode?: View["mode"] } = {}) {
    const mode = opts.mode ?? (target.path || target.type ? "layout" : "read");
    const t: Target = { ...target };
    if (mode === "layout") { t.extend = t.extend ?? DEFAULT_EXTEND; delete t.offset; delete t.size; }
    else { t.size = t.size ?? READ_SIZE; delete t.type; delete t.extend; }
    const v = this.newView(mode, t, opts.via);
    this.push(v);
    await this.load(v);
  }

  /** The target bar's text: a walker path or an address. */
  go(text: string, type?: string, extend?: number) {
    const s = text.trim();
    if (!s) return;
    const a = parseAddress(s);
    if (a !== undefined) {
      const address = "0x" + a.toString(16).toUpperCase();
      void this.open(type ? { address, type, extend } : { address }, { mode: type ? "layout" : "read" });
    } else if (isPathText(s)) {
      void this.open({ path: s, type: type || undefined, extend }, { mode: "layout" });
    }
  }

  /** Follow a pointer value: a raw read of the target. */
  follow(address: string, via: string, size = READ_SIZE) {
    void this.open({ address, size }, { via, mode: "read" });
  }

  /** Re-run the current view with a struct type (layout) or without one (read). */
  setType(type: string | undefined) {
    const v = this.current;
    if (!v) return;
    const t = type?.trim() || undefined;
    if (v.mode === "layout" && !t && !v.target.path) { void this.open({ address: v.target.address }, { mode: "read" }); return; }
    void this.open({ ...v.target, type: t }, { mode: "layout" });
  }

  setExtend(extend: number) {
    const v = this.current;
    if (!v || v.mode !== "layout" || v.target.extend === extend) return;
    void this.open({ ...v.target, extend }, { mode: "layout" });
  }

  setReadSize(size: number) {
    const v = this.current;
    if (!v || v.mode !== "read" || v.target.size === size) return;
    void this.open({ ...v.target, size }, { mode: "read" });
  }

  reload() {
    const v = this.current;
    if (v && !v.loading) void this.load(v, { keepSelection: true });
  }

  back() { if (this.snap.index > 0) this.goTo(this.snap.index - 1); }
  forward() { if (this.snap.index < this.snap.views.length - 1) this.goTo(this.snap.index + 1); }
  goTo(index: number) {
    if (index < 0 || index >= this.snap.views.length || index === this.snap.index) return;
    this.set({ index, selection: undefined, bitSel: undefined, bitHover: undefined, hover: undefined, changes: new Map(), liveFlash: new Map(), where: undefined, watch: this.snap.watch?.status === "running" ? this.snap.watch : undefined });
    const v = this.current;
    if (v && !v.data && !v.loading) void this.load(v);
  }

  private async load(v: View, opts: { keepSelection?: boolean } = {}) {
    this.replace({ ...v, loading: true, error: undefined });
    const t = v.target;
    const args: Record<string, unknown> = {};
    if (t.address) args.address = t.address; else if (t.path) args.path = t.path;
    if (v.mode === "layout") {
      if (t.type) args.type = t.type;
      if (t.extend) args.extend = t.extend;
    } else {
      if (t.offset) args.offset = t.offset;
      args.size = t.size ?? READ_SIZE;
    }
    const r = await this.tool(v.mode === "layout" ? "memory_layout" : "memory_read", args);
    const cur = this.snap.views.find((x) => x.id === v.id);
    if (!cur) return;
    if ("error" in r) { this.replace({ ...cur, loading: false, error: r.error }); return; }
    const data = v.mode === "layout" ? asLayout(r.data) : asRead(r.data);
    if (!data) { this.replace({ ...cur, loading: false, error: { error: "shape", message: "Unexpected result shape." } }); return; }
    const next = this.loaded(cur, data);
    this.replace(next);
    if (!opts.keepSelection) this.set({ selection: undefined });
    else if (this.snap.selection && next.region && this.snap.selection.off >= next.region.size) this.set({ selection: undefined });
  }

  /** One tool call through the host. `noGame` leaves the game argument out (tools without one). */
  private async tool(name: string, args: Record<string, unknown>, opts: { noGame?: boolean } = {}): Promise<ToolOutcome> {
    try {
      const r = await this.call(name, { ...args, ...(this.gameArg && !opts.noGame ? { game: this.gameArg } : {}) });
      this.set({ calls: this.snap.calls + 1 });
      if (r.isError) {
        const err = asError(r.data) ?? { error: "error", message: r.text ?? "The bridge returned an error" };
        this.set({ conn: "live" });
        return { error: err };
      }
      this.set({ conn: "live", lastError: undefined });
      return { data: r.data };
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      this.set({ conn: "offline", lastError: msg, calls: this.snap.calls + 1 });
      return { error: { error: "offline", message: msg } };
    }
  }

  // ── Selection ────────────────────────────────────────────────────

  select(sel: Selection | undefined) {
    const cur = this.snap.selection;
    if (sel && cur && cur.off === sel.off && cur.size === sel.size && cur.segId === sel.segId) return;
    this.set({ selection: sel, bitSel: undefined, bitHover: undefined, where: undefined });
  }

  /** Select an exact byte range (what a piece of code reads), as the covering segment when one matches it. */
  selectBytes(off: number, size: number) {
    const v = this.current;
    if (!v?.region) return;
    const k = v.region.cover[off];
    const seg = k >= 0 ? v.region.segs[k] : undefined;
    if (seg && seg.off === off && seg.size === size && seg.kind !== "gap" && seg.kind !== "zeros") this.select({ off, size, segId: seg.id });
    else this.select({ off, size: Math.max(1, Math.min(size, 64, v.region.size - off)) });
  }

  /** Pick (or clear) a bit in the bit grid, numbered from the selection's first byte. */
  pickBit(bit: number | undefined) {
    if (bit === this.snap.bitSel) return;
    this.set({ bitSel: bit });
  }

  hoverBits(bits: number[] | undefined) {
    const cur = this.snap.bitHover;
    if (bits?.join() === cur?.join()) return;
    this.set({ bitHover: bits });
  }

  /** Select the segment covering a byte, or an ad-hoc 8-byte-aligned range inside a gap. */
  selectByte(off: number, extendFrom?: Selection) {
    const v = this.current;
    if (!v?.region) return;
    if (extendFrom) {
      const lo = Math.min(extendFrom.off, off), hi = Math.max(extendFrom.off + extendFrom.size - 1, off);
      this.select({ off: lo, size: Math.min(hi - lo + 1, 64) });
      return;
    }
    const k = v.region.cover[off];
    const seg = k >= 0 ? v.region.segs[k] : undefined;
    if (seg && (seg.kind === "field" || seg.kind === "cand" || seg.kind === "slot")) { this.select({ off: seg.off, size: seg.size, segId: seg.id }); return; }
    // Inside a gap: an aligned 8-byte window clipped to the gap, so the inspector has something to interpret.
    const start = Math.max(seg?.off ?? 0, off & ~7);
    const end = Math.min(seg ? seg.off + seg.size : v.region.size, start + 8);
    this.select({ off: start, size: Math.max(1, end - start) });
  }

  /** Select what a changed range belongs to: the one field covering it, else the raw range. */
  selectRange(c: ChangedRange) {
    const v = this.current;
    if (!v?.region) return;
    const k = v.region.cover[c.off];
    const seg = k >= 0 ? v.region.segs[k] : undefined;
    if (seg && seg.kind !== "gap" && seg.kind !== "zeros" && seg.off <= c.off && seg.off + seg.size >= c.off + c.size) {
      this.select({ off: seg.off, size: seg.size, segId: seg.id });
    } else {
      this.select({ off: c.off, size: Math.min(c.size, 64) });
    }
  }

  hover(h: { off: number; size: number } | undefined) {
    const cur = this.snap.hover;
    if (h?.off === cur?.off && h?.size === cur?.size) return;
    this.set({ hover: h });
  }

  // ── Watch ────────────────────────────────────────────────────────

  async watch(durationMs = WATCH_MS) {
    const v = this.current;
    if (!v?.region || this.snap.watch?.status === "running") return;
    const wasLive = this.snap.live;
    if (wasLive) this.setLive(false);
    const startedAt = Date.now();
    this.set({ watch: { viewId: v.id, status: "running", startedAt, durationMs } });
    const t = v.target;
    const args: Record<string, unknown> = { durationMs, intervalMs: WATCH_INTERVAL_MS, size: v.region.size };
    if (t.address) args.address = t.address; else if (t.path) args.path = t.path;
    if (t.offset) args.offset = t.offset;
    const r = await this.tool("watch_memory", args);
    if (this.current?.id !== v.id) return;
    if ("error" in r) {
      this.set({ watch: { viewId: v.id, status: "error", startedAt, durationMs, error: r.error.message ?? r.error.error } });
      return;
    }
    const result = r.data as WatchResult;
    const changes = new Map<number, ByteChange>();
    const total = Math.max(1, result.durationMs || durationMs);
    for (const c of result.changedRanges ?? []) {
      const unmapped = c.field === "(unmapped)";
      for (let i = c.off; i < c.off + c.size; i++) changes.set(i, { recency: Math.min(1, c.lastChangeAtMs / total), count: c.changes, noisy: !!c.noisy, unmapped });
    }
    this.set({ watch: { viewId: v.id, status: "done", startedAt, durationMs, result }, changes });
    // The bytes moved: re-read so the map shows the latest values.
    void this.load(v, { keepSelection: true });
    if (wasLive) this.setLive(true);
  }

  clearWatch() { this.set({ watch: undefined, changes: new Map() }); }

  // ── Live re-reads ────────────────────────────────────────────────

  setLive(on: boolean) {
    clearInterval(this.liveTimer);
    this.liveTimer = undefined;
    if (on) this.liveTimer = setInterval(() => void this.liveTick(), LIVE_MS);
    if (on !== this.snap.live) this.set({ live: on });
  }

  private async liveTick() {
    const v = this.current;
    if (!v?.region || v.loading || this.liveBusy || document.hidden || this.snap.conn === "offline" || this.snap.watch?.status === "running") return;
    this.liveBusy = true;
    try {
      const t = v.target;
      const args: Record<string, unknown> = { size: Math.min(4096, Math.max(8, v.region.size)) };
      if (t.address) args.address = t.address; else if (t.path) args.path = t.path;
      if (t.offset) args.offset = t.offset;
      const r = await this.tool("memory_read", args);
      if ("error" in r || this.current?.id !== v.id) return;
      const read = asRead(r.data);
      if (!read) return;
      const { bytes, known } = bytesFromHex(read.hex, v.region.size);
      const prev = v.region.bytes;
      const flash = new Map(this.snap.liveFlash);
      const now = Date.now();
      let moved = false;
      for (let i = 0; i < v.region.size; i++) if (known[i] && prev[i] !== bytes[i]) { flash.set(i, now); moved = true; }
      for (const [k, at] of flash) if (now - at > LIVE_FLASH_MS * 4) flash.delete(k);
      if (!moved && flash.size === this.snap.liveFlash.size) return;
      const region: Region = { ...v.region, bytes: moved ? bytes : prev };
      this.replace({ ...v, region, loadedAt: now });
      this.set({ liveFlash: flash });
    } finally {
      this.liveBusy = false;
    }
  }

  // ── Code access (find_field_access) ──────────────────────────────

  /** Key the session's code lookups by: the struct type when known, else the object's path or address. */
  structKeyOf(v: View | undefined): string {
    if (!v) return "?";
    const layout = v.data && "fields" in v.data ? (v.data as LayoutResult) : undefined;
    return layout?.struct ?? v.target.type ?? v.target.path?.replace(/\[\d+\]$/, "") ?? v.target.address ?? "?";
  }

  /** The lookup a selection maps to: the byte the picked bit lives in (or the selection start) and the bit within it. */
  codeTarget(sel: Selection, bitSel: number | undefined): { offset: number; bit?: number } {
    if (bitSel === undefined) return { offset: sel.off };
    return { offset: sel.off + Math.floor(bitSel / 8), bit: bitSel % 8 };
  }

  codeQueryFor(v: View | undefined, sel: Selection | undefined, bitSel: number | undefined): CodeQuery | undefined {
    if (!v || !sel) return undefined;
    const t = this.codeTarget(sel, bitSel);
    return this.snap.code.get(codeKey(this.structKeyOf(v), t.offset, t.bit));
  }

  /**
   * find_field_access for the current selection (and picked bit). Static analysis in Ghidra: ~30 s per searched
   * offset the first time, instant from the server's cache afterwards. The call can't be aborted through the host;
   * cancel stops waiting, and the result is kept quietly when it lands so the next click is instant.
   */
  async findCode(opts: { minKnown?: number; decompile?: number; knownOffsets?: number[] } = {}) {
    const v = this.current, sel = this.snap.selection;
    if (!v?.region || !sel) return;
    const structKey = this.structKeyOf(v);
    const t = this.codeTarget(sel, this.snap.bitSel);
    const key = codeKey(structKey, t.offset, t.bit);
    const prev = this.snap.code.get(key);
    if (prev?.status === "running") return;
    const layout = v.data && "fields" in v.data ? (v.data as LayoutResult) : undefined;
    const args: Record<string, unknown> = { offset: t.offset };
    if (t.bit !== undefined) args.bit = t.bit;
    if (v.target.path) args.path = v.target.path;
    const known = opts.knownOffsets ?? (!v.target.path && layout ? layout.fields.map((f) => f.off).filter((o) => o >= 8 && (o < t.offset || o > t.offset)) : undefined);
    if (known?.length) args.knownOffsets = [...new Set(known)].sort((a, b) => a - b);
    if (opts.minKnown !== undefined) args.minKnown = opts.minKnown;
    if (opts.decompile !== undefined) args.decompile = opts.decompile;
    const expectCached = [...this.snap.code.values()].some((q) => q.structKey === structKey && q.status === "done" && (q.offset === t.offset || q.result?.anchors.some((a) => a.offset === t.offset)));
    const q: CodeQuery = { key, structKey, offset: t.offset, bit: t.bit, args, status: "running", startedAt: Date.now(), expectCached };
    this.setCode(q);
    const r = await this.tool("find_field_access", args);
    const cur = this.snap.code.get(key);
    if (!cur || cur.startedAt !== q.startedAt) return;
    const finishedAt = Date.now();
    if ("error" in r) {
      const kind = classifyCodeError(r.error);
      this.setCode({ ...cur, status: "error", finishedAt, error: { kind, message: r.error.message ?? r.error.error } });
      return;
    }
    const result = asFieldAccess(r.data);
    if (!result) { this.setCode({ ...cur, status: "error", finishedAt, error: { kind: "error", message: "Unexpected result shape from find_field_access." } }); return; }
    this.setCode({ ...cur, status: "done", finishedAt, result, error: undefined });
    if (cur.status === "cancelled") this.toast("info", `Code for +${t.offset} arrived and is cached; select it again to see it`);
  }

  /** Stop waiting for a lookup. The scan finishes on the server and is cached; the result is kept when it lands. */
  cancelCode(key: string) {
    const q = this.snap.code.get(key);
    if (q?.status === "running") this.setCode({ ...q, status: "cancelled" });
  }

  /** Forget a lookup's result in the UI (the server keeps its cache), so it can be re-run with other options. */
  forgetCode(key: string) {
    if (!this.snap.code.has(key)) return;
    const code = new Map(this.snap.code);
    code.delete(key);
    this.set({ code });
  }

  private setCode(q: CodeQuery) {
    const code = new Map(this.snap.code);
    code.set(q.key, q);
    this.set({ code });
  }

  // ── Where ────────────────────────────────────────────────────────

  async where(address: string) {
    this.set({ where: { address, loading: true } });
    const r = await this.tool("memory_where", { address });
    if (this.snap.where?.address !== address) return;
    if ("error" in r) { this.set({ where: { address, loading: false, error: r.error.message ?? r.error.error } }); return; }
    this.set({ where: { address, loading: false, data: r.data as WhereResult } });
  }

  clearWhere() { if (this.snap.where) this.set({ where: undefined }); }

  // ── Modes ────────────────────────────────────────────────────────

  setMode(mode: Mode) {
    if (mode === this.snap.mode) return;
    this.set({ mode });
    this.setRunPolling(mode === "experiments" && this.snap.exp.view === "run");
    if (mode === "experiments" && this.snap.exp.view === "snapshots" && !this.snap.exp.list && !this.snap.exp.listing) void this.listSnapshots();
    if (mode === "experiments" && this.snap.exp.view === "run" && !this.snap.run.presets && !this.snap.run.presetsLoading) void this.loadPresets();
    if (mode === "findings" && !this.snap.fnd.data && !this.snap.fnd.loading) void this.loadFindings();
    if (mode === "population" && !this.snap.pop.data && !this.snap.pop.loading && !this.snap.pop.path) {
      // A sensible first population: the collection the current path belongs to, else the stash tabs.
      const p = this.current?.target.path;
      const coll = p && /\[\d+\]$/.test(p) ? p.replace(/\[\d+\]$/, "") : "GameController.IngameState.ServerData.PlayerStashTabs";
      const labels = /StashTabs/.test(coll) ? ["Name", "Affinity", "TabType"] : ["Path"];
      void this.loadPopulation(coll, labels);
    }
  }

  private patchPop(p: Partial<PopState>) { this.set({ pop: { ...this.snap.pop, ...p } }); }
  private patchExp(p: Partial<ExpState>) { this.set({ exp: { ...this.snap.exp, ...p } }); }
  private patchRun(p: Partial<RunState>) { this.set({ run: { ...this.snap.run, ...p } }); }
  private patchFnd(p: Partial<FndState>) { this.set({ fnd: { ...this.snap.fnd, ...p } }); }

  /** Experiments tab: the guided runner or the snapshot timeline. */
  setExpView(view: ExpState["view"]) {
    if (view === this.snap.exp.view) return;
    this.patchExp({ view });
    this.setRunPolling(this.snap.mode === "experiments" && view === "run");
    if (view === "snapshots" && !this.snap.exp.list && !this.snap.exp.listing) void this.listSnapshots();
    if (view === "run" && !this.snap.run.presets && !this.snap.run.presetsLoading) void this.loadPresets();
  }

  // ── Guided experiments (experiment_presets / experiment_step_start / experiment_status / experiment_summary / guide) ──

  async loadPresets() {
    this.patchRun({ presetsLoading: true, presetsError: undefined });
    const r = await this.tool("experiment_presets", {});
    if ("error" in r) { this.patchRun({ presetsLoading: false, presetsError: r.error.message ?? r.error.error }); return; }
    const data = r.data as PresetsResult;
    const presets = data.presets ?? [];
    const run: Partial<RunState> = { presetsLoading: false, presets, records: data.records ?? [] };
    // Opened with a preset or record name (show_memory_view mode=experiments preset=… / experiment=…): go straight there.
    const { experiment, phase, preset } = this.snap.run;
    if (!preset && experiment) {
      const p = this.presetFor(experiment, presets);
      if (phase === "done") { run.preset = p; void this.loadSummary(experiment); }
      else if (p) { run.preset = p; run.phase = "setup"; run.checks = new Set(); run.stepIndex = 0; if (data.records?.some((x) => x.name === experiment)) void this.loadSummary(experiment); }
    }
    this.patchRun(run);
  }

  /** The preset a record name belongs to: its id, or the id it was derived from ("stash-switch-tab-3"). */
  private presetFor(experiment: string, presets = this.snap.run.presets ?? []): ExperimentPreset | undefined {
    return presets.find((x) => x.id === experiment) ?? presets.find((x) => experiment.startsWith(x.id + "-") || experiment.startsWith(x.id));
  }

  /**
   * The records list again, quietly (no loading state). In the pick phase a record that grew, or appeared, since the
   * last look is a run this app didn't start: follow it.
   */
  private async refreshRecords() {
    const before = this.snap.run.records;
    const r = await this.tool("experiment_presets", {});
    if ("error" in r) return;
    const data = r.data as PresetsResult;
    const records = data.records ?? [];
    this.patchRun({ records, presets: data.presets ?? this.snap.run.presets });
    if (!before || this.snap.run.phase !== "pick") return;
    const grown = records.find((x) => { const prev = before.find((y) => y.name === x.name); return prev ? (x.steps ?? 0) > (prev.steps ?? 0) : (x.steps ?? 0) > 0; });
    if (grown) this.followRun(grown.name);
  }

  /** Pick a preset: the setup checklist comes next. The record name defaults to the preset id. */
  pickPreset(id: string) {
    const preset = this.snap.run.presets?.find((p) => p.id === id);
    if (!preset) return;
    this.patchRun({ phase: "setup", preset, experiment: id, checks: new Set(), stepIndex: 0, attempts: [], summary: undefined, summaryError: undefined, markedDone: false, following: undefined, ...NO_STEP });
    if (this.snap.run.records?.some((r) => r.name === id)) void this.loadSummary(id);
  }

  /** Read a record's summary without running anything (the "finish" view of an earlier run). */
  openRecord(name: string) {
    const preset = this.presetFor(name);
    this.patchRun({ phase: "done", preset, experiment: name, attempts: [], summary: undefined, summaryError: undefined, markedDone: true, following: undefined, ...NO_STEP });
    void this.loadSummary(name);
  }

  setExperimentName(name: string) {
    const experiment = name.trim();
    this.patchRun({ experiment, summary: this.snap.run.summary?.experiment === experiment ? this.snap.run.summary : undefined, ...NO_STEP });
    if (/^[\w.-]{1,64}$/.test(experiment) && this.snap.run.records?.some((r) => r.name === experiment)) void this.loadSummary(experiment);
  }

  toggleCheck(id: string) {
    const checks = new Set(this.snap.run.checks);
    if (checks.has(id)) checks.delete(id); else checks.add(id);
    this.patchRun({ checks });
  }

  setRunTimeout(timeoutMs: number) { this.patchRun({ timeoutMs }); }

  get runReady(): boolean {
    const r = this.snap.run;
    return !!r.preset && RUN_CHECKS.every((c) => r.checks.has(c)) && /^[\w.-]{1,64}$/.test(r.experiment) && !r.following;
  }

  /** Setup done: show the first action. The in-game card gets the setup line until the first step replaces it. */
  startRun() {
    const r = this.snap.run;
    if (!this.runReady || !r.preset) return;
    this.patchRun({ phase: "run", stepIndex: 0, markedDone: false });
    void this.tool("guide", { title: `Experiment: ${r.experiment}`, status: "info", instruction: r.preset.setup, detail: "Run from the Memory View; the first step comes next" });
  }

  setStepIndex(i: number) {
    const n = this.snap.run.preset?.steps.length ?? 0;
    if (!n || this.snap.run.waiting) return;
    this.patchRun({ stepIndex: ((i % n) + n) % n });
  }

  /** How many steps the record holds (the last status poll, else the summary, else what this session captured). */
  private recordedSteps(): number {
    const r = this.snap.run;
    if (r.recordedSteps !== undefined) return r.recordedSteps;
    return r.summary?.experiment === r.experiment ? r.summary.record.steps.length : r.attempts.filter((a) => "changed" in a.result && a.result.changed).length;
  }

  /**
   * One step: experiment_step_start returns at once and the server waits (up to timeoutMs) for the user's action,
   * driving the in-game card; the status poll (runPoll) carries the step's progress into the card and its result into
   * the attempts when it ends.
   */
  async runStep() {
    const r = this.snap.run;
    const action = r.preset?.steps[r.stepIndex];
    if (!r.preset || !action || r.waiting || r.following) return;
    const step = this.recordedSteps() + 1;
    const steps = plannedSteps(r.preset.steps.length, step);
    // A status poll landing between the start and our bookkeeping must not take our own step for someone else's.
    this.startingStep = true;
    let res: ToolOutcome;
    try {
      res = await this.tool("experiment_step_start", {
        experiment: r.experiment, label: action.label, instruction: action.instruction, watch: r.preset.watch,
        step, steps, timeoutMs: r.timeoutMs,
      });
    } finally { this.startingStep = false; }
    if (this.snap.run.experiment !== r.experiment || this.snap.run.waiting) return;
    if ("error" in res) {
      const msg = res.error.message ?? res.error.error;
      // "Already running" means someone else's step: the next status poll follows it rather than fighting it.
      this.toast("error", msg);
      return;
    }
    const started = res.data as StepStarted;
    const now = Date.now();
    const pending: StepState = { experiment: r.experiment, label: action.label, instruction: action.instruction, step, steps, startedAt: started.startedAt, timeoutMs: started.timeoutMs, status: "starting" };
    this.patchRun({ waiting: { label: action.label, startedAt: started.startedAt, timeoutMs: started.timeoutMs, step, steps }, step: pending, stepSince: now, stepRunning: true, guideSince: now });
    // Don't wait a whole tick for the first "waiting" status.
    setTimeout(() => void this.runPoll(), 250);
  }

  /** Stop the step this app started (experiment_step_cancel). Nothing is recorded; the status poll shows "cancelled". */
  async cancelStep() {
    const r = this.snap.run;
    if (!r.waiting || r.waiting.cancelling) return;
    this.patchRun({ waiting: { ...r.waiting, cancelling: true } });
    const res = await this.tool("experiment_step_cancel", { experiment: r.experiment }, { noGame: true });
    const cur = this.snap.run;
    if (!cur.waiting || cur.waiting.startedAt !== r.waiting.startedAt) return;
    if ("error" in res) { this.toast("error", res.error.message ?? res.error.error); this.patchRun({ waiting: { ...cur.waiting, cancelling: false } }); return; }
    const c = res.data as StepCancelled;
    // Not running in the server any more: it finished (the poll shows the result) or the server lost it (the poll ends it as stale).
    if (!c.cancelled) this.toast("info", c.note ?? "No step of this experiment is running");
    setTimeout(() => void this.runPoll(), 250);
  }

  /** The last step's outcome, for the card and the results. */
  get lastAttempt(): RunAttempt | undefined {
    const a = this.snap.run.attempts;
    return a.length ? a[a.length - 1] : undefined;
  }

  nextStep() {
    const r = this.snap.run;
    if (!r.preset || r.waiting) return;
    this.patchRun({ stepIndex: (r.stepIndex + 1) % r.preset.steps.length });
  }

  async loadSummary(experiment: string) {
    if (!experiment || this.snap.run.summaryLoading) return;
    this.patchRun({ summaryLoading: true, summaryError: undefined });
    const r = await this.tool("experiment_summary", { experiment }, { noGame: true });
    if (this.snap.run.experiment !== experiment && this.snap.run.following !== experiment) { this.patchRun({ summaryLoading: false }); return; }
    if ("error" in r) { this.patchRun({ summaryLoading: false, summaryError: r.error.message ?? r.error.error, summary: undefined }); return; }
    this.patchRun({ summaryLoading: false, summary: r.data as SummaryResult, summaryAt: Date.now() });
  }

  /** The one-line result the finish card and the in-game "done" card show. */
  runResultLine(): string {
    const r = this.snap.run;
    const rows = evidenceRows(r.summary, r.preset);
    const total = r.summary?.record.steps.length ?? 0;
    if (!rows.length) return total ? `${total} step${total === 1 ? "" : "s"} recorded` : "No steps captured";
    const every = rows.filter((x) => x.always.length);
    if (!every.length) return `${total} steps; nothing changed in every repeat of an action`;
    const names = [...new Set(every.flatMap((x) => x.always.map((k) => k.name)))];
    return `${names.slice(0, 2).join(" and ")}${names.length > 2 ? ` (+${names.length - 2})` : ""} changed every time (${total} step${total === 1 ? "" : "s"})`;
  }

  /** What is still applied in game: actions captured more often than their undo. */
  runUndo() {
    const r = this.snap.run;
    return undoList(r.preset, labelCounts(r.summary?.experiment === r.experiment ? r.summary.record : undefined));
  }

  /** Finish: the summary, the in-game card set to done with the result, and the undo list. */
  async finishRun() {
    const r = this.snap.run;
    if (r.waiting) return;
    this.patchRun({ phase: "done" });
    await this.loadSummary(r.experiment);
    if (!this.snap.run.markedDone) await this.markDone();
  }

  async markDone() {
    const r = this.snap.run;
    const line = this.runResultLine();
    const undo = this.runUndo();
    const res = await this.tool("guide", { title: `Experiment: ${r.experiment}`, status: "done", instruction: line, detail: undo.length ? `To undo: ${undo.map((u) => u.text).join(" ")}` : "Nothing to undo in game" });
    if (!("error" in res)) this.patchRun({ markedDone: true, guide: res.data as GuideState, guideAt: Date.now() });
  }

  /** Back to the presets (the record on disk stays). */
  resetRun() {
    if (this.snap.run.waiting) return;
    this.patchRun({ phase: "pick", preset: undefined, experiment: "", checks: new Set(), stepIndex: 0, attempts: [], summary: undefined, summaryError: undefined, following: undefined, markedDone: false, ...NO_STEP });
    void this.loadPresets();
  }

  /** Run the same preset again into the same record. */
  runAgain() {
    const r = this.snap.run;
    if (!r.preset) { this.resetRun(); return; }
    this.patchRun({ phase: "setup", checks: new Set(), stepIndex: 0, attempts: [], markedDone: false });
  }

  /** Stop following and go back to the presets; the agent's run itself continues. */
  stopFollowing() {
    this.patchRun({ phase: "pick", preset: undefined, experiment: "", following: undefined, attempts: [], summary: undefined, summaryError: undefined, markedDone: false, ...NO_STEP });
    void this.loadPresets();
  }

  /** Someone else (the agent) is running this experiment: the same screens follow it read-only. */
  private followRun(name: string) {
    const r = this.snap.run;
    if (r.following === name || r.waiting) return;
    this.patchRun({
      following: name, experiment: name, phase: "run", attempts: [], markedDone: false,
      summary: r.summary?.experiment === name ? r.summary : undefined,
      preset: r.preset && (r.preset.id === name || name.startsWith(r.preset.id)) ? r.preset : this.presetFor(name),
      ...(r.experiment === name ? {} : NO_STEP),
    });
  }

  /** Context for the model: the run so far (phase, evidence, undo). */
  describeRun(): { text: string; structured: Record<string, unknown> } | undefined {
    const r = this.snap.run;
    if (!r.experiment) return undefined;
    const rows = evidenceRows(r.summary, r.preset);
    const undo = this.runUndo();
    const who = r.following ? "Claude's run, followed in the app" : "run from the app";
    const text = `Guided experiment "${r.experiment}" (${who}, ${r.phase}): ${this.runResultLine()}. ` +
      rows.map((x) => `${x.label} ×${x.repeats}: every time ${x.always.map((k) => k.name).join(", ") || "nothing"}${x.sometimes.length ? `; sometimes ${x.sometimes.map((k) => `${k.name} (${k.seen}/${k.of})`).join(", ")}` : ""}`).join(". ") +
      (undo.length ? ` To undo in game: ${undo.map((u) => u.text).join(" ")}` : " Nothing to undo in game.");
    return { text, structured: { experiment: r.experiment, phase: r.phase, following: r.following, preset: r.preset?.id, evidence: rows.map((x) => ({ label: x.label, repeats: x.repeats, always: x.always.map((k) => k.full), sometimes: x.sometimes.map((k) => k.full) })), undo: undo.map((u) => u.text) } };
  }

  // Polling, only while the runner is visible. Every second: experiment_status for the chosen experiment (the step the
  // server is running, ours or the agent's, and the record's length). Every other tick: guide_state, read-only, so the
  // card mirrors the in-game one (and names a candidate record in the pick phase). Every fourth: the record itself.
  private setRunPolling(on: boolean) {
    clearInterval(this.runTimer);
    this.runTimer = undefined;
    if (on) { this.runTimer = setInterval(() => void this.runPoll(), STATUS_POLL_MS); void this.runPoll(); }
  }

  private async runPoll() {
    if (this.runPollBusy || document.hidden || this.snap.conn === "offline") return;
    this.runPollBusy = true;
    const tick = ++this.runPollTick;
    try {
      const r0 = this.snap.run;
      if (tick % GUIDE_POLL_TICKS === 0 || !r0.guide) await this.pollGuide();
      const r1 = this.snap.run;
      // Which experiment to ask about: the one chosen here, or, in the pick phase, the one the in-game card names.
      const name = r1.following ?? (r1.phase !== "pick" ? r1.experiment : guideExperiment(r1.guide));
      if (name && /^[\w.-]{1,64}$/.test(name)) await this.pollStatus(name);
      const r = this.snap.run;
      const target = r.following ?? (r.phase !== "pick" ? r.experiment : undefined);
      // The record: every fourth tick, at once when the step count moved past the summary, or when there is none yet.
      const stale = !!r.summary && r.summary.experiment === target && r.recordedSteps !== undefined && r.recordedSteps !== r.summary.record.steps.length;
      // No record on disk yet (a run before its first capture) is not an error worth asking about.
      const haveRecord = r.recordedSteps === undefined ? !!r.summary : r.recordedSteps > 0;
      if (target && haveRecord && !r.summaryLoading && (tick % SUMMARY_POLL_TICKS === 0 || stale || !r.summary)) await this.loadSummary(target);
      if (r.phase === "pick" && tick % RECORDS_POLL_TICKS === 0 && !r.presetsLoading) await this.refreshRecords();
      const f = this.snap.run;
      if (f.following && f.phase === "run" && !f.stepRunning && f.guide && (f.guide.status === "done" || f.guide.status === "idle") && (guideExperiment(f.guide) ?? f.following) === f.following
        && f.guideAt && f.summaryAt && f.summaryAt >= f.guideAt && f.summary?.experiment === f.following) {
        // The agent finished (its card says done, no step running, record read since): the finish view of its record.
        this.patchRun({ phase: "done", markedDone: true });
      }
    } finally {
      this.runPollBusy = false;
    }
  }

  private async pollGuide() {
    const g = await this.tool("guide_state", {});
    if ("error" in g || !g.data || typeof g.data !== "object" || !("status" in (g.data as object))) return;
    const guide = g.data as GuideState;
    const prev = this.snap.run.guide;
    const changed = !prev || prev.rev !== guide.rev;
    const newInstruction = !prev || prev.instruction !== guide.instruction || (prev.status !== guide.status && (guide.status === "waiting" || guide.status === "failed"));
    const patch: Partial<RunState> = { guide, guideAt: Date.now() };
    if (newInstruction && !this.snap.run.waiting) patch.guideSince = Date.now();
    if (changed || patch.guideSince) this.patchRun(patch);
  }

  /** experiment_status: the step's progress into the card, its result into the attempts, and someone else's run into follow-along. */
  private async pollStatus(name: string) {
    const res = await this.tool("experiment_status", { experiment: name }, { noGame: true });
    if ("error" in res) return;
    const st = res.data as StatusResult;
    const r = this.snap.run;
    const now = Date.now();
    const sameExperiment = r.following === name || (r.phase !== "pick" && r.experiment === name);
    const step = st.step;
    const patch: Partial<RunState> = sameExperiment ? { statusAt: now, stepRunning: st.running, recordedSteps: st.recordedSteps } : {};
    if (sameExperiment && step) {
      patch.step = step;
      // The countdown runs on the client clock from the server's own elapsed time; a finished step keeps what it had.
      patch.stepSince = step.elapsedMs !== undefined ? now - step.elapsedMs : r.step?.startedAt === step.startedAt && r.stepSince ? r.stepSince : Date.parse(step.startedAt);
    }
    const ours = !!r.waiting && !!step && step.startedAt === r.waiting.startedAt;
    if (r.waiting && sameExperiment && (ours ? stepOver(step) : !st.running)) {
      // Our step ended (or the server no longer knows it): its outcome becomes the attempt.
      const at = step?.finishedAt ? Date.parse(step.finishedAt) : now;
      const result: RunAttempt["result"] = !ours || !step ? { error: "The server no longer reports this step (was it restarted?). Start it again." }
        : step.status === "cancelled" ? { cancelled: true }
        : step.result ? step.result
        : { error: step.error ?? (step.status === "stale" ? "The server stopped reporting this step (it was restarted mid-step)." : "The step ended without a result.") };
      patch.waiting = undefined;
      patch.attempts = [...r.attempts, { at, label: r.waiting.label, result }];
      this.patchRun(patch);
      if (st.recordedSteps > 0) void this.loadSummary(name);
      return;
    }
    if (Object.keys(patch).length) this.patchRun(patch);
    // A step running that this app didn't start, or steps landing in the record without it asking: someone else's run.
    const foreign = st.running && !r.waiting && !this.startingStep;
    const grew = sameExperiment && !r.waiting && r.recordedSteps !== undefined && st.recordedSteps > r.recordedSteps;
    if ((foreign || grew) && r.following !== name) this.followRun(name);
  }

  // ── Population ───────────────────────────────────────────────────

  /** memory_population, then memory_layout of the first item (field names over the columns), then memory_correlate. */
  async loadPopulation(path: string, labels: string[]) {
    path = path.trim();
    if (!path) return;
    this.patchPop({ path, labels, loading: true, error: undefined, data: undefined, bytes: undefined, layout: undefined, correlate: undefined, correlateError: undefined, bit: undefined, groupBy: labels.find((l) => l !== "Name") ?? labels[0] });
    const r = await this.tool("memory_population", { path, labels, limit: 200 });
    if (this.snap.pop.path !== path) return;
    if ("error" in r) { this.patchPop({ loading: false, error: r.error.message ?? r.error.error }); return; }
    const data = r.data as PopulationResult;
    const bytes = data.items.map((it) => { const parts = it.hex.trim().split(/\s+/); const b = new Uint8Array(data.size); for (let i = 0; i < Math.min(parts.length, data.size); i++) b[i] = parseInt(parts[i], 16); return b; });
    this.patchPop({ loading: false, data, bytes, loadedAt: Date.now() });
    if (data.struct && data.items[0]) {
      const l = await this.tool("memory_layout", { address: data.items[0].address, type: data.struct });
      if (this.snap.pop.path === path && !("error" in l)) this.patchPop({ layout: asLayout(l.data) });
    }
    void this.correlate();
  }

  async correlate() {
    const { path, labels, correlating } = this.snap.pop;
    if (!path || !labels.length || correlating) return;
    this.patchPop({ correlating: true, correlateError: undefined });
    const r = await this.tool("memory_correlate", { path, labels, limit: 500 });
    if (this.snap.pop.path !== path) return;
    if ("error" in r) { this.patchPop({ correlating: false, correlateError: r.error.message ?? r.error.error }); return; }
    this.patchPop({ correlating: false, correlate: r.data as CorrelateResult });
  }

  selectBit(bit: { byte: number; bit: number } | undefined) {
    const cur = this.snap.pop.bit;
    if (bit?.byte === cur?.byte && bit?.bit === cur?.bit) return;
    this.patchPop({ bit });
  }
  setGroupBy(groupBy: string | undefined) { this.patchPop({ groupBy }); }
  hoverItem(i: number | undefined) { if (i !== this.snap.pop.hoverItem) this.patchPop({ hoverItem: i }); }

  /** Open one item of the population in the struct view. */
  openItem(index: number) {
    const { data } = this.snap.pop;
    const it = data?.items.find((x) => x.index === index);
    if (!it) return;
    this.set({ mode: "struct" });
    void this.open({ path: `${data!.path}[${index}]`, type: undefined }, { mode: "layout" });
  }

  // ── Experiments ──────────────────────────────────────────────────

  async listSnapshots() {
    this.patchExp({ listing: true, error: undefined });
    const r = await this.tool("memory_compare", {});
    if ("error" in r) { this.patchExp({ listing: false, error: r.error.message ?? r.error.error }); return; }
    const list = (r.data as SnapshotList).snapshots ?? [];
    // Oldest first: experiments read in the order they were taken.
    list.sort((a, b) => a.savedAt.localeCompare(b.savedAt));
    const exp = { ...this.snap.exp, listing: false, list };
    // First visit: preselect the newest experiment series (same prefix up to the first '-<digit>').
    if (!exp.selected.length && list.length >= 2) {
      const newest = list[list.length - 1].name;
      const prefix = /^(.*?)-\d/.exec(newest)?.[1];
      const series = prefix ? list.filter((s) => s.name.startsWith(prefix + "-")).map((s) => s.name) : [];
      exp.selected = series.length >= 2 ? series : [list[list.length - 2].name, newest];
    }
    this.set({ exp });
    if (exp.selected.length >= 2 && !exp.compare) void this.compareSelected();
  }

  toggleSnapshot(name: string) {
    const sel = this.snap.exp.selected.includes(name) ? this.snap.exp.selected.filter((n) => n !== name) : [...this.snap.exp.selected, name];
    this.patchExp({ selected: sel });
  }
  setSelectedSnapshots(selected: string[]) { this.patchExp({ selected }); }

  async compareSelected() {
    const names = this.snap.exp.selected;
    if (names.length < 2 || this.snap.exp.comparing) return;
    this.patchExp({ comparing: true, compareError: undefined });
    const r = await this.tool("memory_compare", { names });
    if ("error" in r) { this.patchExp({ comparing: false, compareError: r.error.message ?? r.error.error }); return; }
    this.patchExp({ comparing: false, compare: r.data as CompareResult, step: 0 });
  }

  setStep(step: number) {
    const n = this.snap.exp.compare?.steps.length ?? 0;
    if (!n) return;
    this.patchExp({ step: Math.max(0, Math.min(n - 1, step)) });
  }

  async takeSnapshot(name: string, path: string, labels: string[]) {
    if (!name.trim() || !path.trim() || this.snap.exp.saving) return;
    this.patchExp({ saving: true, saveError: undefined, saved: undefined });
    const r = await this.tool("memory_snapshot", { name: name.trim(), path: path.trim(), ...(labels.length ? { labels } : {}) });
    if ("error" in r) { this.patchExp({ saving: false, saveError: r.error.message ?? r.error.error }); return; }
    const saved = r.data as SnapshotSaved;
    this.patchExp({ saving: false, saved, selected: [...this.snap.exp.selected.filter((n) => n !== saved.saved), saved.saved] });
    this.toast("success", `"${saved.saved}" (${saved.what})`, "Snapshot saved");
    await this.listSnapshots();
  }

  // ── Findings ─────────────────────────────────────────────────────

  async loadFindings() {
    this.patchFnd({ loading: true, error: undefined });
    const r = await this.tool("findings", {});
    if ("error" in r) { this.patchFnd({ loading: false, error: r.error.message ?? r.error.error }); return; }
    this.patchFnd({ loading: false, data: r.data as FindingsResult });
  }

  setFindingsFilter(filter: string) { this.patchFnd({ filter }); }
  setFindingsStatus(status: FndState["status"]) { this.patchFnd({ status }); }
  toggleFinding(id: string) {
    const open = new Set(this.snap.fnd.open);
    if (open.has(id)) open.delete(id); else open.add(id);
    this.patchFnd({ open });
  }

  async verify(id: string) {
    if (this.snap.fnd.verifying.has(id)) return;
    this.patchFnd({ verifying: new Set([...this.snap.fnd.verifying, id]), open: new Set([...this.snap.fnd.open, id]) });
    const r = await this.tool("verify_finding", { id });
    const verifying = new Set(this.snap.fnd.verifying); verifying.delete(id);
    const verified = new Map(this.snap.fnd.verified);
    verified.set(id, "error" in r ? { error: r.error.message ?? r.error.error } : (r.data as VerifyResult));
    this.patchFnd({ verifying, verified });
  }

  /** Jump to a finding's offset in the struct view (the current view, or the finding's own object). */
  showInStruct(off: number, size = 1) {
    this.set({ mode: "struct" });
    const v = this.current;
    if (v?.region && off < v.region.size) this.selectByte(off); else this.select({ off, size });
  }

  // ── Helpers ──────────────────────────────────────────────────────

  /** Absolute address of an offset in the current view. */
  addressOf(off: number): string | undefined {
    const v = this.current;
    return v?.region ? addrPlus(v.region.address, off) : undefined;
  }

  toast(kind: Toast["kind"], text: string, title?: string) {
    const toast: Toast = { id: ++this.toastSeq, kind, text, title };
    this.set({ toasts: [...this.snap.toasts, toast].slice(-3) });
    setTimeout(() => this.dismiss(toast.id), kind === "error" ? 6000 : 2500);
  }
  dismiss(id: number) { this.set({ toasts: this.snap.toasts.filter((t) => t.id !== id) }); }
}

/** Forget what experiment_status said about the previous experiment (switching records, presets, or back to the picker). */
const NO_STEP: Pick<RunState, "step" | "stepSince" | "stepRunning" | "recordedSteps" | "statusAt"> = { step: undefined, stepSince: undefined, stepRunning: undefined, recordedSteps: undefined, statusAt: undefined };

/** "step n / m" on the card: every action RUN_TARGET_REPEATS times, growing by whole rounds once the user goes past that. */
export function plannedSteps(actions: number, step: number): number {
  const unit = Math.max(1, actions);
  return Math.max(unit * RUN_TARGET_REPEATS, Math.ceil(step / unit) * unit);
}

// ── Result shape guards ────────────────────────────────────────────

function asError(data: unknown): MemoryError | undefined {
  if (!data || typeof data !== "object") return undefined;
  const d = data as Record<string, unknown>;
  return typeof d.error === "string" ? { error: d.error, message: typeof d.message === "string" ? d.message : undefined } : undefined;
}

export function asLayout(data: unknown): LayoutResult | undefined {
  if (!data || typeof data !== "object") return undefined;
  const d = data as Record<string, unknown>;
  return Array.isArray(d.fields) && typeof d.address === "string" ? (d as unknown as LayoutResult) : undefined;
}

export function asRead(data: unknown): ReadResult | undefined {
  if (!data || typeof data !== "object") return undefined;
  const d = data as Record<string, unknown>;
  return Array.isArray(d.slots) && typeof d.address === "string" && !Array.isArray(d.fields) ? (d as unknown as ReadResult) : undefined;
}
