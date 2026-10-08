// MemoryStore: framework-free state for the memory view. Owns the history of views (a struct layout or a raw
// read, each with its region model), the selection and hover, watch results as per-byte changes, live re-reads
// and toasts. Immutable snapshots for useSyncExternalStore; every mutation replaces the snapshot object.

import type { Toast } from "../sync";
import { addrPlus, bytesFromHex, isPathText, parseAddress, pathLabel, regionFromLayout, regionFromRead, shortAddr, type Region } from "./bytes";
import type { ChangedRange, CompareResult, CorrelateResult, FindingStatus, FindingsResult, Game, LayoutResult, MemoryError, PopulationResult, ReadResult, SnapshotList, SnapshotSaved, VerifyResult, WatchResult, WhereResult } from "./types";
import { asFieldAccess, classifyCodeError, codeKey, type CodeQuery } from "./codeModel";

export type CallTool = (name: string, args: Record<string, unknown>) => Promise<{ data: unknown; isError: boolean; text?: string }>;

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
    exp: { listing: false, selected: [], comparing: false, step: 0, saving: false },
    fnd: { loading: false, filter: "", status: "all", verifying: new Set(), verified: new Map(), open: new Set() },
    views: [], index: -1, code: new Map(), changes: new Map(), live: false, liveFlash: new Map(), conn: "idle", calls: 0, toasts: [],
  };
  private listeners = new Set<() => void>();
  private gameArg?: Game;
  private startTimer?: ReturnType<typeof setTimeout>;
  private liveTimer?: ReturnType<typeof setInterval>;
  private liveBusy = false;
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

  /** Arguments of the tool that opened the app: show_memory_view {path?, address?, type?, game?}. */
  setToolInput(args: Record<string, unknown> | undefined) {
    const game = args?.game;
    if (game === "poe1" || game === "poe2") { this.gameArg = game; this.set({ game }); }
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

  private async tool(name: string, args: Record<string, unknown>): Promise<{ data: unknown } | { error: MemoryError }> {
    try {
      const r = await this.call(name, { ...args, ...(this.gameArg ? { game: this.gameArg } : {}) });
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
    if (mode === "experiments" && !this.snap.exp.list && !this.snap.exp.listing) void this.listSnapshots();
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
  private patchFnd(p: Partial<FndState>) { this.set({ fnd: { ...this.snap.fnd, ...p } }); }

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
    this.toast("info", `Saved snapshot "${saved.saved}" (${saved.what})`);
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

  toast(kind: Toast["kind"], text: string) {
    const toast: Toast = { id: ++this.toastSeq, kind, text };
    this.set({ toasts: [...this.snap.toasts, toast].slice(-3) });
    setTimeout(() => this.dismiss(toast.id), kind === "error" ? 6000 : 2500);
  }
  dismiss(id: number) { this.set({ toasts: this.snap.toasts.filter((t) => t.id !== id) }); }
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
