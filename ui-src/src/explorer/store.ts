// ExplorerStore: framework-free state for the data explorer. Owns the per-path cache of explore_object
// results, what is expanded / selected / ticked, root history, watch results and auto-refresh.
// Immutable snapshots for useSyncExternalStore; every mutation replaces the snapshot object.

import type { Toast } from "../sync";
import { generateSnippet, type Snippet, type SnippetItem } from "./codegen";
import { childPath } from "./paths";
import type { ExploreChild, ExploreComponent, ExploreError, ExploreNode, Game, NodeKind, WatchResult } from "./types";

export type CallTool = (name: string, args: Record<string, unknown>) => Promise<{ data: unknown; isError: boolean; text?: string }>;

export const PAGE_SIZE = 50;
export const WATCH_MS = 5000;
export const AUTO_REFRESH_MS = 2000;
/** How long a changed value flashes / a watch hit stays marked. */
export const FLASH_MS = 1600;
export const WATCH_HIT_MS = 30_000;

/** Everything known about one path. Children accumulate across pages and skipped-member loads. */
export interface Entry {
  path: string;
  node?: ExploreNode;
  children?: ExploreChild[];
  components?: ExploreComponent[];
  page?: ExploreNode["page"];
  skipped?: ExploreNode["skipped"];
  loading: boolean;
  loadingMore?: boolean;
  /** Members being loaded from `skipped`. */
  loadingSkipped?: boolean;
  error?: string;
  loadedAt?: number;
  elapsedMs?: number;
}

export interface WatchState {
  path: string;
  status: "running" | "done" | "error";
  startedAt: number;
  durationMs: number;
  result?: WatchResult;
  error?: string;
}

export interface Snapshot {
  game?: Game;
  /** The path shown at the top of the tree. Undefined until the first node arrives. */
  root?: string;
  history: string[];
  historyIndex: number;
  entries: ReadonlyMap<string, Entry>;
  expanded: ReadonlySet<string>;
  /** Row id (path, or parent#name for unaddressable members). */
  selected?: string;
  checked: ReadonlyMap<string, SnippetItem>;
  filter: string;
  /** Row path -> when its preview last changed on a refresh. */
  changed: ReadonlyMap<string, number>;
  watch?: WatchState;
  /** Absolute paths watch_object reported as changed -> when. */
  watchHits: ReadonlyMap<string, number>;
  autoRefresh: boolean;
  conn: "idle" | "live" | "offline";
  lastError?: string;
  calls: number;
  toasts: Toast[];
}

export class ExplorerStore {
  private snap: Snapshot = {
    history: [], historyIndex: -1, entries: new Map(), expanded: new Set(), checked: new Map(), filter: "",
    changed: new Map(), watchHits: new Map(), autoRefresh: false, conn: "idle", calls: 0, toasts: [],
  };
  private listeners = new Set<() => void>();
  private gameArg?: Game;
  private startTimer?: ReturnType<typeof setTimeout>;
  private refreshTimer?: ReturnType<typeof setInterval>;
  private toastSeq = 0;
  private inflight = new Map<string, Promise<Entry>>();

  constructor(private call: CallTool) {}

  subscribe = (fn: () => void) => { this.listeners.add(fn); return () => { this.listeners.delete(fn); }; };
  getSnapshot = () => this.snap;

  private set(patch: Partial<Snapshot>) {
    this.snap = { ...this.snap, ...patch };
    for (const l of this.listeners) l();
  }

  // ── Lifecycle ────────────────────────────────────────────────────

  private requestedPath?: string;

  /** The arguments of the tool that opened the app (show_data_explorer {path?, game?}). */
  setToolInput(args: Record<string, unknown> | undefined) {
    const game = args?.game;
    if (game === "poe1" || game === "poe2") { this.gameArg = game; this.set({ game }); }
    if (typeof args?.path === "string" && args.path.trim()) this.requestedPath = args.path.trim();
  }

  /** The host's tool result (show_data_explorer): the root node, already loaded. */
  seed(data: unknown) {
    const node = asNode(data);
    if (!node?.path) {
      const err = asError(data);
      if (!err) return;
      // The path the user asked for failed: show that, with the message, rather than silently falling back.
      const path = this.requestedPath ?? "GameController";
      const entries = new Map(this.snap.entries);
      entries.set(path, { path, loading: false, error: err.message ?? err.error });
      clearTimeout(this.startTimer);
      this.set({ entries, root: path, history: [path], historyIndex: 0, selected: path, lastError: err.message ?? err.error });
      return;
    }
    clearTimeout(this.startTimer);
    const entry = this.entryFrom(node.path, node, this.snap.entries.get(node.path));
    const entries = new Map(this.snap.entries); entries.set(node.path, entry);
    this.set({ entries, root: node.path, history: [node.path], historyIndex: 0, selected: node.path, conn: "live" });
  }

  /** Hosts that never send a tool result (or send an error) still get a tree. */
  start() {
    if (this.snap.root || this.startTimer) return;
    this.startTimer = setTimeout(() => { if (!this.snap.root) void this.navigate("GameController"); }, 1200);
  }

  stop() {
    clearTimeout(this.startTimer);
    this.startTimer = undefined;
    this.setAutoRefresh(false);
  }

  // ── Navigation ───────────────────────────────────────────────────

  async navigate(path: string, pushHistory = true) {
    path = path.trim();
    if (!path) return;
    let history = this.snap.history, index = this.snap.historyIndex;
    if (pushHistory && history[index] !== path) {
      history = [...history.slice(0, index + 1), path].slice(-50);
      index = history.length - 1;
    }
    this.set({ root: path, history, historyIndex: index, selected: path, filter: "" });
    await this.load(path);
  }

  back() { if (this.snap.historyIndex > 0) this.go(this.snap.historyIndex - 1); }
  forward() { if (this.snap.historyIndex < this.snap.history.length - 1) this.go(this.snap.historyIndex + 1); }
  private go(index: number) {
    this.set({ historyIndex: index });
    void this.navigate(this.snap.history[index], false);
  }

  select(id: string | undefined) { if (id !== this.snap.selected) this.set({ selected: id }); }
  setFilter(filter: string) { this.set({ filter }); }

  toggle(path: string) {
    const expanded = new Set(this.snap.expanded);
    if (expanded.has(path)) expanded.delete(path);
    else { expanded.add(path); if (!this.snap.entries.get(path)?.children) void this.load(path); }
    this.set({ expanded });
  }
  expand(path: string) { if (!this.snap.expanded.has(path)) this.toggle(path); }
  collapse(path: string) { if (this.snap.expanded.has(path)) this.toggle(path); }

  // ── Loading ──────────────────────────────────────────────────────

  /** explore_object at depth 1; replaces the entry's children (first page) and marks changed previews. */
  load(path: string, opts: { silent?: boolean } = {}): Promise<Entry> {
    const running = this.inflight.get(path);
    if (running) return running;
    const prev = this.snap.entries.get(path);
    if (!opts.silent || !prev) this.put({ ...(prev ?? { path }), path, loading: true, error: undefined });
    const p = (async () => {
      const r = await this.explore({ path });
      if ("error" in r) {
        const entry: Entry = { ...(prev ?? { path }), path, loading: false, error: r.error };
        this.put(entry);
        return entry;
      }
      const entry = this.entryFrom(path, r.node, prev);
      this.put(entry);
      return entry;
    })().finally(() => this.inflight.delete(path));
    this.inflight.set(path, p);
    return p;
  }

  refresh(path: string) { return this.load(path, { silent: true }); }

  async loadMore(path: string, all = false) {
    const e = this.snap.entries.get(path);
    if (!e?.children || e.loadingMore) return;
    const total = e.page?.total ?? e.node?.count;
    const offset = e.children.length;
    const limit = all && total !== undefined ? Math.min(200, Math.max(1, total - offset)) : PAGE_SIZE;
    this.put({ ...e, loadingMore: true });
    const r = await this.explore({ path, offset, limit });
    const cur = this.snap.entries.get(path) ?? e;
    if ("error" in r) { this.put({ ...cur, loadingMore: false, error: r.error }); return; }
    this.put({ ...cur, loadingMore: false, children: [...(cur.children ?? []), ...(r.node.children ?? [])], page: r.node.page ?? cur.page, loadedAt: Date.now() });
  }

  /** Read the members the bridge skipped for time, one call each so each stays within budget. */
  async loadSkipped(path: string) {
    const e = this.snap.entries.get(path);
    if (!e?.skipped?.members.length || e.loadingSkipped) return;
    this.put({ ...e, loadingSkipped: true });
    for (const name of e.skipped.members) {
      const cp = childPath(path, name);
      const r = await this.explore({ path: cp });
      const cur = this.snap.entries.get(path) ?? e;
      const child: ExploreChild = "error" in r
        ? { name, kind: "blocked", path: cp, error: r.error }
        : { ...stripRoot(r.node), name, path: cp };
      const children = [...(cur.children ?? []).filter((c) => c.name !== name), child].sort(byName);
      const members = cur.skipped?.members.filter((m) => m !== name) ?? [];
      this.put({ ...cur, children, skipped: members.length && cur.skipped ? { ...cur.skipped, members } : undefined });
      if (!("error" in r)) {
        const entries = new Map(this.snap.entries);
        entries.set(cp, this.entryFrom(cp, r.node, entries.get(cp)));
        this.set({ entries });
      }
    }
    const done = this.snap.entries.get(path);
    if (done) this.put({ ...done, loadingSkipped: false });
  }

  private async explore(args: { path: string; offset?: number; limit?: number }): Promise<{ node: ExploreNode } | { error: string }> {
    try {
      const r = await this.call("explore_object", { ...args, depth: 1, limit: args.limit ?? PAGE_SIZE, ...(this.gameArg ? { game: this.gameArg } : {}) });
      this.set({ calls: this.snap.calls + 1 });
      if (r.isError) {
        const err = asError(r.data);
        return { error: err?.message ?? err?.error ?? r.text ?? "The bridge returned an error" };
      }
      const node = asNode(r.data);
      if (!node) return { error: "Unexpected result shape (no node)" };
      const game = (r.data as { game?: Game } | undefined)?.game;
      this.set({ conn: "live", lastError: undefined, ...(game && !this.snap.game ? { game } : {}) });
      return { node };
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      this.set({ conn: "offline", lastError: msg, calls: this.snap.calls + 1 });
      return { error: msg };
    }
  }

  private entryFrom(path: string, node: ExploreNode, prev?: Entry): Entry {
    // Mark values whose preview moved since the last load, so the rows can flash.
    if (prev?.children && node.children) {
      const before = new Map(prev.children.map((c) => [c.name, c.preview]));
      const changed = new Map(this.snap.changed);
      const now = Date.now();
      for (const c of node.children) {
        const old = before.get(c.name);
        if (old !== undefined && old !== c.preview) changed.set(c.path ?? `${path}#${c.name}`, now);
      }
      if (changed.size !== this.snap.changed.size || [...changed].some(([k, v]) => this.snap.changed.get(k) !== v)) this.snap = { ...this.snap, changed };
    }
    if (prev?.node && prev.node.preview !== node.preview) {
      const changed = new Map(this.snap.changed); changed.set(path, Date.now());
      this.snap = { ...this.snap, changed };
    }
    return {
      path, node, children: node.children, components: node.components, page: node.page, skipped: node.skipped,
      loading: false, error: undefined, loadedAt: Date.now(), elapsedMs: node.elapsedMs,
    };
  }

  private put(entry: Entry) {
    const entries = new Map(this.snap.entries);
    entries.set(entry.path, entry);
    this.set({ entries });
  }

  // ── Lookups ──────────────────────────────────────────────────────

  /** The freshest view of a node: its own loaded result, else how its parent listed it. */
  nodeAt(path: string): ExploreNode | undefined {
    const own = this.snap.entries.get(path)?.node;
    if (own) return own;
    for (const e of this.snap.entries.values()) {
      const c = e.children?.find((k) => k.path === path) ?? e.components?.find((k) => k.path === path);
      if (c) return c;
    }
    return undefined;
  }
  kindOf = (path: string): NodeKind | undefined => this.nodeAt(path)?.kind;
  namespaceOf = (path: string): string | undefined => this.snap.entries.get(path)?.node?.namespace;

  snippet(): Snippet {
    return generateSnippet([...this.snap.checked.values()], { kindOf: this.kindOf, namespaceOf: this.namespaceOf });
  }

  // ── Selection for code ───────────────────────────────────────────

  setChecked(item: SnippetItem, on: boolean) {
    const checked = new Map(this.snap.checked);
    if (on) checked.set(item.path, item); else checked.delete(item.path);
    this.set({ checked });
  }
  clearChecked() { this.set({ checked: new Map() }); }

  // ── Watch ────────────────────────────────────────────────────────

  async watch(path: string) {
    if (this.snap.watch?.status === "running") return;
    const startedAt = Date.now();
    this.set({ watch: { path, status: "running", startedAt, durationMs: WATCH_MS } });
    try {
      const r = await this.call("watch_object", { expression: path, durationMs: WATCH_MS, intervalMs: 250, ...(this.gameArg ? { game: this.gameArg } : {}) });
      this.set({ calls: this.snap.calls + 1 });
      const data = (r.data ?? {}) as WatchResult;
      if (r.isError) {
        this.set({ watch: { path, status: "error", startedAt, durationMs: WATCH_MS, error: data.message ?? data.error ?? r.text ?? "watch_object failed" } });
        return;
      }
      const hits = new Map(this.snap.watchHits);
      const now = Date.now();
      for (const c of data.changes ?? []) hits.set(c.path.startsWith("[") ? `${path}${c.path}` : c.path ? `${path}.${c.path}` : path, now);
      this.set({ watch: { path, status: "done", startedAt, durationMs: WATCH_MS, result: data }, watchHits: hits, conn: "live" });
      // The watched subtree moved: refresh what is loaded there so the tree shows the latest values.
      if (this.snap.entries.get(path)?.children) void this.refresh(path);
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      this.set({ watch: { path, status: "error", startedAt, durationMs: WATCH_MS, error: msg }, conn: "offline", lastError: msg });
    }
  }
  clearWatch() { this.set({ watch: undefined, watchHits: new Map() }); }

  /** eval_path: the full JSON value at a path (for the inspector's "full value"). */
  async evalPath(path: string): Promise<{ data?: unknown; error?: string }> {
    try {
      const r = await this.call("eval_path", { expression: path, ...(this.gameArg ? { game: this.gameArg } : {}) });
      this.set({ calls: this.snap.calls + 1 });
      if (r.isError) { const err = asError(r.data); return { error: err?.message ?? err?.error ?? r.text ?? "eval_path failed" }; }
      return { data: r.data ?? r.text };
    } catch (e) {
      return { error: e instanceof Error ? e.message : String(e) };
    }
  }

  // ── Auto refresh ─────────────────────────────────────────────────

  setAutoRefresh(on: boolean) {
    clearInterval(this.refreshTimer);
    this.refreshTimer = undefined;
    if (on) {
      this.refreshTimer = setInterval(() => {
        if (document.hidden || this.snap.conn === "offline") return;
        const target = this.snap.selected && this.snap.entries.has(this.snap.selected) ? this.snap.selected : this.selectedPath();
        if (target) void this.refresh(target);
      }, AUTO_REFRESH_MS);
    }
    if (on !== this.snap.autoRefresh) this.set({ autoRefresh: on });
  }

  /** The selected row's path (row ids are paths except for unaddressable members). */
  selectedPath(): string | undefined {
    const s = this.snap.selected;
    return s && !s.includes("#") ? s : undefined;
  }

  // ── Toasts ───────────────────────────────────────────────────────

  toast(kind: Toast["kind"], text: string, title?: string) {
    const toast: Toast = { id: ++this.toastSeq, kind, text, title };
    this.set({ toasts: [...this.snap.toasts, toast].slice(-3) });
    setTimeout(() => this.dismiss(toast.id), kind === "error" ? 6000 : 2500);
  }
  dismiss(id: number) { this.set({ toasts: this.snap.toasts.filter((t) => t.id !== id) }); }
}

// ── Helpers ────────────────────────────────────────────────────────

function asNode(data: unknown): ExploreNode | undefined {
  if (!data || typeof data !== "object") return undefined;
  const d = data as Record<string, unknown>;
  return typeof d.kind === "string" && typeof d.path === "string" ? (d as unknown as ExploreNode) : undefined;
}

function asError(data: unknown): ExploreError | undefined {
  if (!data || typeof data !== "object") return undefined;
  const d = data as Record<string, unknown>;
  return typeof d.error === "string" ? { error: d.error, message: typeof d.message === "string" ? d.message : undefined } : undefined;
}

/** A loaded node as a child: keep the summary fields, not the children (they live in the node's own entry). */
function stripRoot(n: ExploreNode): ExploreNode {
  const { children: _c, components: _k, page: _p, skipped: _s, namespace: _n, elapsedMs: _e, ...rest } = n;
  return rest;
}

const byName = (a: ExploreChild, b: ExploreChild) => {
  // List items by index, everything else by name.
  const ai = /^\[(\d+)\]$/.exec(a.name), bi = /^\[(\d+)\]$/.exec(b.name);
  if (ai && bi) return Number(ai[1]) - Number(bi[1]);
  return a.name.localeCompare(b.name);
};
