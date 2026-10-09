// Pure helpers for the HUD performance panel: the spike / GC coincidence analysis, the frame split, the
// ArrayPool check, and number formatting. No React, no I/O.

import type { Gc, PluginCost, Report, Series, Stats, Trace } from "./types";

/** One frame of the timeline, derived from the parallel series. */
export interface Frame {
  i: number;
  tMs: number;
  /** Time until the next frame; undefined for the last one. */
  intervalMs?: number;
  workMs: number;
  pluginsMs: number;
  coreMs: number;
  gcPauseMs: number;
}

export function framesOf(s: Series | undefined): Frame[] {
  if (!s) return [];
  const n = Math.min(s.tMs.length, s.intervalMs.length, s.workMs.length, s.pluginsMs.length, s.gcPauseMs.length);
  const out: Frame[] = [];
  for (let i = 0; i < n; i++) {
    const iv = s.intervalMs[i];
    out.push({
      i, tMs: s.tMs[i], intervalMs: iv === null || iv === undefined ? undefined : iv,
      workMs: s.workMs[i], pluginsMs: s.pluginsMs[i], coreMs: Math.max(0, s.workMs[i] - s.pluginsMs[i]), gcPauseMs: s.gcPauseMs[i],
    });
  }
  return out;
}

/** The frame time the HUD is pacing to: 1000 / fps, which is what the server calls "expected". */
export function expectedMs(t: Trace): number {
  return t.hudFps > 0 ? 1000 / t.hudFps : t.frameIntervalMs.p50 ?? t.frameIntervalMs.avg ?? 16.7;
}

/** A frame counts as a spike at 1.25x the expected interval (the server's finding uses 1.5x for "spikes up to"). */
export const SPIKE_FACTOR = 1.25;

export interface SpikeAnalysis {
  expectedMs: number;
  thresholdMs: number;
  spikes: number;
  spikesWithGc: number;
  /** Longest frame among those without a GC pause. */
  maxWithoutGcMs: number;
  /** Longest frame overall. */
  maxMs: number;
  framesWithGc: number;
  gcPauseTotalMs: number;
  /** The one-line verdict the panel leads with. */
  verdict: string;
  tone: "good" | "gc" | "work" | "mixed";
}

export function analyseSpikes(t: Trace, frames: Frame[]): SpikeAnalysis {
  const expected = expectedMs(t);
  const threshold = expected * SPIKE_FACTOR;
  let spikes = 0, spikesWithGc = 0, maxWithoutGc = 0, max = 0, framesWithGc = 0, gcTotal = 0;
  for (const f of frames) {
    const gc = f.gcPauseMs > 0;
    if (gc) { framesWithGc++; gcTotal += f.gcPauseMs; }
    if (f.intervalMs === undefined) continue;
    max = Math.max(max, f.intervalMs);
    if (!gc) maxWithoutGc = Math.max(maxWithoutGc, f.intervalMs);
    if (f.intervalMs > threshold) { spikes++; if (gc) spikesWithGc++; }
  }
  let verdict: string, tone: SpikeAnalysis["tone"];
  if (spikes === 0) {
    verdict = `No frame went over ${fmtMs(threshold)} (expected ${fmtMs(expected)}): frame pacing is steady.`;
    tone = "good";
  } else if (spikesWithGc === spikes) {
    verdict = `${spikes} frame${spikes === 1 ? "" : "s"} over ${fmtMs(threshold)}, and every one had a GC pause. Frames without a pause never exceeded ${fmtMs(maxWithoutGc)}: the stutter is garbage collection, not plugin work.`;
    tone = "gc";
  } else if (spikesWithGc === 0) {
    verdict = `${spikes} frame${spikes === 1 ? "" : "s"} over ${fmtMs(threshold)}, none with a GC pause: the spikes are frame work (a plugin or the HUD core), not garbage collection.`;
    tone = "work";
  } else {
    verdict = `${spikes} frames over ${fmtMs(threshold)}: ${spikesWithGc} had a GC pause, ${spikes - spikesWithGc} did not (up to ${fmtMs(maxWithoutGc)} without one). Both garbage collection and frame work contribute.`;
    tone = "mixed";
  }
  return { expectedMs: expected, thresholdMs: threshold, spikes, spikesWithGc, maxWithoutGcMs: maxWithoutGc, maxMs: max, framesWithGc, gcPauseTotalMs: gcTotal, verdict, tone };
}

/** Where an average frame goes: plugins, HUD core, and the rest (waiting for the game / present / vsync). */
export interface Split {
  intervalMs: number;
  pluginsMs: number;
  coreMs: number;
  restMs: number;
}

export function splitOf(t: Trace): Split {
  const interval = t.frameIntervalMs.avg ?? expectedMs(t);
  const plugins = t.pluginsMs.avg ?? 0;
  const core = t.coreMs.avg ?? Math.max(0, (t.updateMs.avg ?? 0) - plugins);
  return { intervalMs: interval, pluginsMs: plugins, coreMs: core, restMs: Math.max(0, interval - plugins - core) };
}

/** The ArrayPool finding, in plain words: the HUD cycles more pages per frame than the shared pool keeps. */
export interface PoolCheck {
  state: "churn" | "ok" | "tuned" | "unknown";
  keeps?: number;
  cycles?: number;
  env?: number | null;
  broken?: string | null;
}

export const POOL_ENV = "DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXARRAYSPERPARTITION=256";

export function poolCheck(gc: Gc | undefined): PoolCheck {
  const p = gc?.sharedArrayPool;
  if (!gc || !p) return { state: "unknown" };
  if (p.broken) return { state: "unknown", broken: p.broken };
  const keeps = p.arraysPerSize, cycles = gc.fetchedPagesPerFrame;
  if (keeps === undefined || cycles === undefined) return { state: "unknown", env: p.envMaxArraysPerPartition };
  if (cycles > keeps && gc.allocMBPerSecond > 50) return { state: "churn", keeps, cycles, env: p.envMaxArraysPerPartition };
  return { state: p.envMaxArraysPerPartition ? "tuned" : "ok", keeps, cycles, env: p.envMaxArraysPerPartition };
}

/** Plugins costliest first (the server already sorts; keep it stable if a client doesn't). */
export function pluginsByTime(r: Report): PluginCost[] {
  return [...(r.plugins ?? [])].sort((a, b) => b.tickMs + b.renderMs - (a.tickMs + a.renderMs));
}
export function pluginsByAlloc(r: Report): PluginCost[] {
  return [...(r.plugins ?? [])].sort((a, b) => b.allocKBPerFrame - a.allocKBPerFrame);
}

/** What the server's text said about a missing trace, e.g. "bridge unreachable (...)" or the instrumentation message. */
export function traceNote(text: string | undefined): string | undefined {
  const m = text?.split("\n").find((l) => l.startsWith("Trace:"));
  return m ? m.slice(6).trim() : undefined;
}

export function isInstrumentationOff(note: string | undefined): boolean {
  return !!note && /instrumentation/i.test(note);
}
export function isUnreachable(note: string | undefined): boolean {
  return !!note && /unreachable|not reachable|refused|no bridge|timed out/i.test(note);
}

// ── Formatting ───────────────────────────────────────────────────────

export function fmtMs(v: number | undefined, unit = true): string {
  if (v === undefined || Number.isNaN(v)) return "-";
  const s = v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v >= 1 ? v.toFixed(2) : v.toFixed(3);
  return unit ? `${s} ms` : s;
}
export function fmtNum(v: number | undefined, digits = 1): string {
  if (v === undefined || Number.isNaN(v)) return "-";
  return v.toLocaleString("en-US", { maximumFractionDigits: digits, minimumFractionDigits: 0 });
}
export function fmtInt(v: number | undefined): string {
  return v === undefined ? "-" : Math.round(v).toLocaleString("en-US");
}
export function fmtKB(v: number | undefined): string {
  if (v === undefined) return "-";
  if (v >= 1024) return `${(v / 1024).toFixed(v >= 10240 ? 0 : 1)} MB`;
  return `${v >= 100 ? v.toFixed(0) : v.toFixed(1)} KB`;
}
export function fmtPct(part: number, whole: number): string {
  if (whole <= 0) return "-";
  const p = (part / whole) * 100;
  return `${p >= 10 ? p.toFixed(0) : p.toFixed(1)}%`;
}
export function fmtStats(s: Stats | undefined): string {
  if (!s || !s.n) return "-";
  return `avg ${fmtMs(s.avg)} · p50 ${fmtMs(s.p50)} · p95 ${fmtMs(s.p95)} · max ${fmtMs(s.max)}`;
}
/** 1800000 ns -> "1.8 ms", 2400 -> "2.4 us", 330 -> "330 ns". */
export function fmtNs(ns: number): string {
  if (ns >= 1e6) return `${(ns / 1e6).toFixed(1)} ms`;
  if (ns >= 1000) return `${(ns / 1000).toFixed(ns >= 10000 ? 0 : 1)} us`;
  return `${ns} ns`;
}
export function fmtUs(us: number): string {
  if (us >= 1000) return `${(us / 1000).toFixed(2)} ms`;
  return `${us >= 100 ? us.toFixed(0) : us >= 10 ? us.toFixed(1) : us.toFixed(2)} us`;
}
export function fmtBytes(b: number): string {
  if (b >= 1024 * 1024) return `${(b / 1024 / 1024).toFixed(1)} MB`;
  if (b >= 1024) return `${(b / 1024).toFixed(b >= 10240 ? 0 : 1)} KB`;
  return `${b} B`;
}
export function fmtElapsed(ms: number): string {
  return ms < 10_000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms / 1000)} s`;
}

/** Compiler-generated names made readable: "<>c.<.ctor>b__8_0" -> "lambda in .ctor". */
export function methodLabel(m: string): string {
  const lambda = m.match(/<([^>]*)>b__\d+(?:_\d+)?$/);
  if (lambda) return `lambda in ${lambda[1] || "?"}`;
  const dc = m.match(/<>c__DisplayClass\d+_\d+\.<([^>]*)>/);
  if (dc) return `closure in ${dc[1] || "?"}`;
  const sm = m.match(/<([^>]*)>d__\d+\.(\w+)/);
  if (sm) return `${sm[1]} (state machine).${sm[2]}`;
  return m.replace("..ctor", " constructor").replace(/\.\.ctor$/, " constructor");
}
