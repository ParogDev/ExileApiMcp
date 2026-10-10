// The timeline's model, all pure: which lane an event belongs to, how it is drawn (a mark: shape, tone, hollow for
// unmapped), the time scale (a viewport of end + span in epoch ms), nice axis ticks, the HUD-spike and after-agent
// windows that explain or discount events, and the one-line wording of an event shared with the events feed.

import type { LayerStatus, ObserveEvent } from "../types";

// ── Wording (Tools/ObserveDtos.cs Line()) ────────────────────────────

export const KINDS = ["layer", "layer.noisy", "ui", "area", "level", "entity", "hud", "agent"];
export const KIND_TONE: Record<string, "info" | "accent" | "warning" | "success" | "violet" | "neutral" | "danger"> = {
  layer: "info", "layer.noisy": "neutral", ui: "accent", area: "success", level: "success", entity: "violet", hud: "warning", agent: "neutral",
};

/** A compact view of an agent call's params: the first few scalar fields as k=v. */
export function agentParams(p: Record<string, unknown> | null | undefined, max = 3): string {
  if (!p || typeof p !== "object") return "";
  const parts: string[] = [];
  for (const [k, v] of Object.entries(p)) {
    if (parts.length >= max) { parts.push("…"); break; }
    if (v === null || v === undefined) continue;
    const s = typeof v === "object" ? (Array.isArray(v) ? `[${v.length}]` : "{…}") : String(v);
    parts.push(`${k}=${s.length > 28 ? s.slice(0, 27) + "…" : s}`);
  }
  return parts.join(" ");
}

/** One line per event, as Tools/ObserveDtos.cs Line() words it. */
export function eventLine(e: ObserveEvent): string {
  switch (e.kind) {
    case "layer": case "server": return `${e.layer ?? "server"} ${e.unit ?? e.off ?? ""}${e.name ? ` ${e.name}` : e.mode === "struct" ? " (unmapped)" : ""} ${e.old ?? "-"} → ${e.new ?? "-"}${e.delta != null ? `  ${e.delta > 0 ? "+" : ""}${e.delta}` : ""}${e.change ? `  ${e.change}` : ""}`;
    case "layer.noisy": case "server.noisy": return `${e.layer ?? "server"} ${e.group ?? e.unit ?? ""} is noisy: counted in its layer map, not logged`;
    case "ui": return `ui [${e.index}] ${e.visible ? "opened" : "closed"} ${e.mapped ?? "UNMAPPED"}${e.firstSeen ? " (first time)" : ""}${e.texts?.length ? ` · ${e.texts.slice(0, 3).join(" | ")}` : ""}`;
    case "area": return `area ${e.from} → ${e.to}`;
    case "level": return `level ${e.from} → ${e.to}${e.area ? ` in ${e.area}` : ""}`;
    case "entity": return `entity ${e.type}${e.entityType ? ` (${e.entityType})` : ""}`;
    case "hud": return e.cause === "reload"
      ? `hud: ${e.plugin} reloaded ${e.ok === false ? "FAILED" : "ok"} in ${e.durationMs ?? "?"} ms (the HUD paused)`
      : `hud: frame ${e.intervalMs ?? "?"} ms (typical ${e.typicalMs ?? "?"}), GC ${e.gcMs ?? 0} ms (gen0 ${e.gen0 ?? 0}, gen1 ${e.gen1 ?? 0}, gen2 ${e.gen2 ?? 0})${e.suppressed ? `, ${e.suppressed} more spike(s) since the last one` : ""}`;
    case "agent": return `agent ${e.method ?? "?"} ${agentParams(e.params)}`.trimEnd();
    default: return e.kind;
  }
}

/** The short name of an event for a mark's tooltip or a list: the unit, panel, method or cause. */
export function eventShort(e: ObserveEvent): string {
  switch (e.kind) {
    case "layer": return `${e.name ?? e.unit ?? ""}${e.change ? ` ${e.change}` : e.old != null || e.new != null ? ` ${e.old ?? "-"} → ${e.new ?? "-"}` : ""}`;
    case "layer.noisy": return `${e.group ?? e.unit ?? ""} noisy`;
    case "ui": return `${e.mapped ?? `[${e.index}] unmapped`} ${e.visible ? "opened" : "closed"}`;
    case "area": return `${e.to}`;
    case "level": return `level ${e.to}`;
    case "entity": return `${e.type}`;
    case "hud": return e.cause === "reload" ? `${e.plugin} reloaded` : `spike ${fmtMs(e.intervalMs)}`;
    case "agent": return `${e.method ?? "agent"}`;
    default: return e.kind;
  }
}

export function fmtMs(ms: number | null | undefined): string {
  if (ms == null || !Number.isFinite(ms)) return "–";
  const a = Math.abs(ms);
  if (a < 1000) return `${Math.round(ms)} ms`;
  if (a < 60_000) return `${(ms / 1000).toFixed(a < 10_000 ? 1 : 0)} s`;
  if (a < 3_600_000) return `${Math.round(ms / 60_000)} min`;
  return `${(ms / 3_600_000).toFixed(1)} h`;
}

/** A relative time with its sign, for "what happened around it" (−420 ms, +1.2 s). */
export function fmtDt(ms: number): string {
  if (Math.abs(ms) < 0.5) return "0";
  return (ms < 0 ? "−" : "+") + fmtMs(Math.abs(ms));
}

// ── Lanes ────────────────────────────────────────────────────────────

export type LaneKind = "layer" | "ui" | "world" | "entity" | "hud" | "agent";

export interface Lane {
  /** "layer:<id>" or the fixed lane's kind. */
  id: string;
  kind: LaneKind;
  label: string;
  /** The layer's mode, for the label. */
  mode?: string;
  /** Not in the layer list any more, or paused / broken. */
  state?: "paused" | "broken" | "not-now" | "gone";
  count: number;
  noisy: number;
  /** struct layers: how many of the events were unmapped units. */
  unmapped: number;
}

export const FIXED_LANES: { kind: LaneKind; label: string }[] = [
  { kind: "ui", label: "ui" },
  { kind: "world", label: "area · level" },
  { kind: "entity", label: "entity" },
  { kind: "hud", label: "hud" },
  { kind: "agent", label: "agent" },
];

export function laneOf(e: ObserveEvent): string {
  switch (e.kind) {
    case "layer": case "layer.noisy": return `layer:${e.layer ?? "server"}`;
    case "server": case "server.noisy": return "layer:server";
    case "area": case "level": return "world";
    case "ui": case "entity": case "hud": case "agent": return e.kind;
    default: return "agent";
  }
}

/** One lane per layer (spec order, then layers only seen in events), then the fixed lanes; counts from the events. */
export function buildLanes(events: readonly ObserveEvent[], layers: readonly LayerStatus[] | undefined): Lane[] {
  const byId = new Map<string, Lane>();
  for (const l of layers ?? []) {
    byId.set(`layer:${l.spec.id}`, { id: `layer:${l.spec.id}`, kind: "layer", label: l.spec.id, mode: l.spec.mode, state: l.broken ? "broken" : l.notNow ? "not-now" : !l.spec.enabled ? "paused" : undefined, count: 0, noisy: 0, unmapped: 0 });
  }
  for (const e of events) {
    const id = laneOf(e);
    if (!id.startsWith("layer:")) continue;
    let lane = byId.get(id);
    if (!lane) { lane = { id, kind: "layer", label: id.slice(6), mode: e.mode ?? undefined, state: layers ? "gone" : undefined, count: 0, noisy: 0, unmapped: 0 }; byId.set(id, lane); }
    if (e.kind.endsWith(".noisy")) lane.noisy++; else lane.count++;
    if (e.mode === "struct" && !e.name && !e.kind.endsWith(".noisy")) lane.unmapped++;
    if (!lane.mode && e.mode) lane.mode = e.mode;
  }
  const fixed = FIXED_LANES.map<Lane>((f) => ({ id: f.kind, kind: f.kind, label: f.label, count: 0, noisy: 0, unmapped: 0 }));
  for (const e of events) {
    const id = laneOf(e);
    const f = fixed.find((l) => l.id === id);
    if (f) { f.count++; if (e.kind === "ui" && !e.mapped) f.unmapped++; }
  }
  return [...byId.values(), ...fixed];
}

// ── Marks ────────────────────────────────────────────────────────────

/** How an event is drawn. tone is a CSS custom property name (resolved by the canvas, so both themes work). */
export type MarkShape = "tick" | "add" | "remove" | "open" | "close" | "diamond" | "rule" | "block";
export interface Mark { shape: MarkShape; tone: string; hollow?: boolean }

export const TONE = {
  mapped: "--color-m-field",
  unmapped: "--color-fg-3",
  added: "--color-success",
  removed: "--color-danger",
  ui: "--color-ring",
  uiUnmapped: "--color-warning",
  world: "--color-success",
  entity: "--color-m-cand",
  spike: "--color-p-spike",
  reload: "--color-p-gc",
  agent: "--color-fg",
  noisy: "--color-fg-3",
} as const;

export function markOf(e: ObserveEvent): Mark {
  switch (e.kind) {
    case "layer": case "server":
      if (e.change === "added") return { shape: "add", tone: TONE.added };
      if (e.change === "removed") return { shape: "remove", tone: TONE.removed };
      if (e.mode === "struct" && !e.name) return { shape: "tick", tone: TONE.unmapped, hollow: true };
      return { shape: "tick", tone: TONE.mapped };
    case "layer.noisy": case "server.noisy": return { shape: "tick", tone: TONE.noisy, hollow: true };
    case "ui": return { shape: e.visible ? "open" : "close", tone: e.mapped ? TONE.ui : TONE.uiUnmapped, hollow: !e.mapped };
    case "area": return { shape: "rule", tone: TONE.world };
    case "level": return { shape: "rule", tone: TONE.world, hollow: true };
    case "entity": return { shape: "tick", tone: TONE.entity };
    case "hud": return e.cause === "reload" ? { shape: "block", tone: TONE.reload } : { shape: "block", tone: TONE.spike };
    case "agent": return { shape: "diamond", tone: TONE.agent };
    default: return { shape: "tick", tone: TONE.unmapped, hollow: true };
  }
}

// ── Time ─────────────────────────────────────────────────────────────

export const atMs = (e: ObserveEvent): number => Date.parse(e.at);

/** The visible window: it ends at `end` and shows `span` ms. */
export interface Viewport { end: number; span: number }

export const MIN_SPAN = 500;
export const MAX_SPAN = 12 * 3_600_000;
export const SPAN_PRESETS: { label: string; span: number }[] = [
  { label: "10 s", span: 10_000 }, { label: "1 min", span: 60_000 }, { label: "10 min", span: 600_000 }, { label: "1 h", span: 3_600_000 },
];

export const xOf = (t: number, vp: Viewport, width: number): number => ((t - (vp.end - vp.span)) / vp.span) * width;
export const tOf = (x: number, vp: Viewport, width: number): number => vp.end - vp.span + (x / width) * vp.span;

/** Zoom by a factor keeping the time under x fixed. */
export function zoomAt(vp: Viewport, factor: number, x: number, width: number): Viewport {
  const span = Math.max(MIN_SPAN, Math.min(MAX_SPAN, vp.span * factor));
  const anchor = tOf(x, vp, width);
  const frac = width > 0 ? x / width : 1;
  return { span, end: anchor + (1 - frac) * span };
}

export function panBy(vp: Viewport, dx: number, width: number): Viewport {
  return { ...vp, end: vp.end - (dx / width) * vp.span };
}

/** The span that shows every event, with a margin. */
export function fitSpan(events: readonly ObserveEvent[], now: number): Viewport {
  if (!events.length) return { end: now, span: 60_000 };
  const first = atMs(events[0]), last = Math.max(atMs(events[events.length - 1]), now - 1000);
  const span = Math.max(MIN_SPAN * 4, Math.min(MAX_SPAN, (last - first) * 1.08 + 2000));
  return { end: last + span * 0.04, span };
}

const STEPS = [100, 250, 500, 1000, 2000, 5000, 10_000, 15_000, 30_000, 60_000, 120_000, 300_000, 600_000, 900_000, 1_800_000, 3_600_000, 7_200_000, 4 * 3_600_000];

/** Axis ticks at a nice step (≥ minPx apart), plus how to format them. */
export function axisTicks(vp: Viewport, width: number, minPx = 72): { t: number; major: boolean; label: string }[] {
  if (width <= 0) return [];
  const pxPerMs = width / vp.span;
  const step = STEPS.find((s) => s * pxPerMs >= minPx) ?? STEPS[STEPS.length - 1];
  const start = vp.end - vp.span;
  const first = Math.ceil(start / step) * step;
  const out: { t: number; major: boolean; label: string }[] = [];
  const majorEvery = step < 1000 ? 1000 : step < 60_000 ? 60_000 : 3_600_000;
  for (let t = first; t <= vp.end; t += step) {
    const major = t % majorEvery === 0;
    out.push({ t, major, label: fmtAxis(t, step) });
    if (out.length > 200) break;
  }
  return out;
}

export function fmtAxis(t: number, step: number): string {
  const d = new Date(t);
  const hh = String(d.getHours()).padStart(2, "0"), mm = String(d.getMinutes()).padStart(2, "0"), ss = String(d.getSeconds()).padStart(2, "0");
  if (step < 1000) return `${ss}.${String(Math.floor(d.getMilliseconds() / 100))}`;
  if (step < 60_000) return `${hh}:${mm}:${ss}`;
  return `${hh}:${mm}`;
}

export function fmtClock(t: number, withMs = true): string {
  const d = new Date(t);
  const base = `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}:${String(d.getSeconds()).padStart(2, "0")}`;
  return withMs ? `${base}.${String(d.getMilliseconds()).padStart(3, "0")}` : base;
}

// ── Windows that explain or discount events ─────────────────────────

/** A spike: the frame ran [t − intervalMs, t]; anything the layers saw in it was measured late. A reload pauses the HUD
 *  for durationMs before t. */
export interface Window { from: number; to: number; seq: number; kind: "spike" | "reload" | "agent"; label: string }

/** How long after an agent asked something of the user its effects are "the user's action after an agent prompt". */
export const AGENT_AFTER_MS = 10_000;
const AGENT_PROMPTS = /^(guide\.|highlight|experiment\.|await_)/;

export function windowsOf(events: readonly ObserveEvent[]): Window[] {
  const out: Window[] = [];
  for (const e of events) {
    const t = atMs(e);
    if (e.kind === "hud") {
      const len = e.cause === "reload" ? e.durationMs ?? 0 : e.intervalMs ?? 0;
      out.push({ from: t - len, to: t, seq: e.seq, kind: e.cause === "reload" ? "reload" : "spike", label: eventShort(e) });
    } else if (e.kind === "agent" && e.method && AGENT_PROMPTS.test(e.method)) {
      out.push({ from: t, to: t + AGENT_AFTER_MS, seq: e.seq, kind: "agent", label: `after ${e.method}` });
    }
  }
  return out;
}

/** Is t inside a window of this kind? (returns the window) */
export function inWindow(windows: readonly Window[], t: number, kind: Window["kind"] | "hud"): Window | undefined {
  for (const w of windows) {
    if (kind === "hud" ? w.kind === "agent" : w.kind !== kind) continue;
    if (t >= w.from - 1 && t <= w.to + 1) return w;
  }
  return undefined;
}

// ── Filters and visible events ──────────────────────────────────────

export interface Filters {
  /** Lane ids hidden (collapsed to a thin row). */
  hiddenLanes: ReadonlySet<string>;
  /** Event kinds hidden (the chips). */
  hiddenKinds: ReadonlySet<string>;
  /** Dim layer events that fall inside a HUD spike or reload. */
  dimInSpikes: boolean;
  /** Mark events that follow an agent prompt. */
  markAfterAgent: boolean;
}

export const DEFAULT_FILTERS: Filters = { hiddenLanes: new Set(), hiddenKinds: new Set(), dimInSpikes: true, markAfterAgent: true };

export function isShown(e: ObserveEvent, f: Filters): boolean {
  return !f.hiddenKinds.has(e.kind) && !f.hiddenLanes.has(laneOf(e));
}

/** Events in time order with the journal's extra events merged in (deduped by seq). */
export function mergeEvents(ring: readonly ObserveEvent[], journal: Readonly<Record<number, ObserveEvent>>): ObserveEvent[] {
  const extra = Object.values(journal);
  if (!extra.length) return [...ring];
  const seen = new Set(ring.map((e) => e.seq));
  const all = [...ring, ...extra.filter((e) => !seen.has(e.seq))];
  all.sort((a, b) => atMs(a) - atMs(b) || a.seq - b.seq);
  return all;
}

/** The field/value pairs worth listing for one event, by kind, in a stable order. */
export function eventFields(e: ObserveEvent): [string, unknown][] {
  const pick = (keys: string[]) => keys.filter((k) => e[k] !== undefined && e[k] !== null).map((k): [string, unknown] => [k, e[k]]);
  switch (e.kind) {
    case "layer": return pick(["layer", "mode", "unit", "name", "old", "new", "delta", "change", "off", "len", "i32", "i64"]);
    case "layer.noisy": return pick(["layer", "group", "unit", "note"]);
    case "ui": return pick(["index", "visible", "mapped", "firstSeen", "texts"]);
    case "area": return pick(["from", "to"]);
    case "level": return pick(["from", "to", "area"]);
    case "entity": return pick(["type", "entityType", "area"]);
    case "hud": return pick(["cause", "intervalMs", "typicalMs", "gcMs", "gen0", "gen1", "gen2", "suppressed", "plugin", "ok", "durationMs"]);
    case "agent": return pick(["method", "params"]);
    default: return Object.entries(e).filter(([k]) => !["seq", "at", "t", "frame", "kind"].includes(k));
  }
}
