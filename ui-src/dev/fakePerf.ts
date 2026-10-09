// In-memory stand-in for show_hud_performance / profile_plugin / hud_plugin_lint / overlay_accuracy, for the
// dev harness. Fixtures in dev/perf/*.json are real PoE2 captures: show-hud-performance.json (a 3 s trace with
// 169 frames, 11 spikes, all with a GC pause), profile-reagent / -healthbars / -skilldps.json, profile-error.json
// (no such plugin), lint-healthbars / -ninjapricer.json. Other plugins' profiles and lints are synthesised in the
// same shapes. Each refresh perturbs the series a little so a refresh visibly changes the panel.
//
// Scenarios: live (display on), display-off (the capture as taken: displayLikelyOff), offline (bridge
// unreachable: the server's own text-only result), instrumentation-off, flaky (30% errors plus jitter),
// fps200 (~600 synthesised frames at 200 fps, to test density), clean (nothing stands out: the empty states).

import type { CallToolResult } from "@modelcontextprotocol/client";
import type { Report, Series, Trace } from "../src/perf/types";
import type { CallLogEntry } from "./fakeServer";
import perfFixture from "./perf/show-hud-performance.json";
import profReagent from "./perf/profile-reagent.json";
import profHealthBars from "./perf/profile-healthbars.json";
import profSkillDps from "./perf/profile-skilldps.json";
import profError from "./perf/profile-error.json";
import lintHealthBars from "./perf/lint-healthbars.json";
import lintNinja from "./perf/lint-ninjapricer.json";

export type PerfScenario = "live" | "display-off" | "offline" | "instrumentation-off" | "flaky" | "fps200" | "clean";

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const BASE = perfFixture as unknown as Report;

const UNREACHABLE = "The poe2 HUD bridge is not reachable (connection refused on 127.0.0.1:50900). Is the HUD running?";
const INSTR_OFF = "Enable 'Allow HUD Instrumentation' in the bridge settings (Dev Loop) to trace the render pipeline.";

export class FakePerf {
  scenario: PerfScenario = "live";
  latencyMs = 60;
  /** How long a refresh "traces" (the real one is ~3.5 s). */
  traceMs = 3500;
  /** How long a profile_plugin run takes (the real one is ~4.8 s). */
  profileMs = 1800;
  log: CallLogEntry[] = [];
  onChange?: () => void;
  private refreshes = 0;

  async handle(name: string, args: Record<string, unknown>): Promise<CallToolResult> {
    const t0 = performance.now();
    const jitter = this.scenario === "flaky" ? Math.random() * 600 : Math.random() * 20;
    const entry: CallLogEntry = { at: Date.now(), name, args, ms: 0, outcome: "ok" };
    try {
      if (name === "show_hud_performance") await sleep(this.latencyMs + jitter + (this.scenario === "offline" || this.scenario === "instrumentation-off" ? 0 : this.traceMs));
      else if (name === "profile_plugin") await sleep(this.latencyMs + jitter + this.profileMs);
      else if (name === "overlay_accuracy") await sleep(this.latencyMs + jitter + 2500);
      else await sleep(this.latencyMs + jitter);
      // The health report never throws for an unreachable bridge: it returns the desktop line and "Trace: bridge unreachable".
      if (name !== "show_hud_performance" && (this.scenario === "offline" || (this.scenario === "flaky" && Math.random() < 0.3))) {
        throw new Error(UNREACHABLE);
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

  tick() { /* nothing drifts between calls; each refresh is a new trace */ }

  private dispatch(name: string, a: Record<string, unknown>): CallToolResult {
    switch (name) {
      case "show_hud_performance": return this.report();
      case "profile_plugin": return this.profile(String(a.name ?? ""));
      case "hud_plugin_lint": return this.lint(String(a.plugin ?? ""));
      case "overlay_accuracy": return json(this.overlay());
      default: throw new Error(`Unknown tool: ${name}`);
    }
  }

  // ── show_hud_performance ───────────────────────────────────────────

  private report(): CallToolResult {
    this.refreshes++;
    const desktop = this.scenario === "display-off" ? BASE.desktop : { idleSeconds: 3, displayTimeoutSeconds: 900, displayLikelyOff: false, gameForeground: true };
    const screen = desktop?.warning ? `Screen: ${desktop.warning}` : "Screen: display on, game in front.";
    if (this.scenario === "offline" || (this.scenario === "flaky" && Math.random() < 0.3)) {
      return text({ desktop }, `${screen}\nTrace: bridge unreachable (${UNREACHABLE})`);
    }
    if (this.scenario === "instrumentation-off") return text({ desktop }, `${screen}\nTrace: ${INSTR_OFF}`);
    const r: Report = this.scenario === "fps200" ? synthFps200(this.refreshes) : this.scenario === "clean" ? synthClean(this.refreshes) : perturb(BASE, this.refreshes);
    r.desktop = desktop;
    return json(r as unknown as Record<string, unknown>, `${screen}\nFrames: ${r.trace!.hudFps} fps ...`);
  }

  // ── profile_plugin ─────────────────────────────────────────────────

  private profile(name: string): CallToolResult {
    const k = key(name);
    if (k === "reagent") return json(profReagent as Record<string, unknown>);
    if (k === "healthbars") return json(profHealthBars as Record<string, unknown>);
    if (k === "skilldps" || k === "skilldpscore") return json(profSkillDps as Record<string, unknown>);
    if (k === "nosuchplugin" || !name) return json(profError as Record<string, unknown>, undefined, true);
    const known = (BASE.plugins ?? []).find((p) => key(p.name) === k);
    if (!known) return json({ error: "not_found", message: `No loaded plugin named '${name}'. Loaded: ${(BASE.plugins ?? []).map((p) => p.name).join(", ")}` }, undefined, true);
    // Synthesised in profile-skilldps.json's shape: a Render, a per-item lambda, a formatter.
    const perSec = (known.tickMs + known.renderMs) * 56.4;
    const allocKBs = known.allocKBPerFrame * 56.4;
    const top = [
      { method: `${name}.Render`, calls: 169, selfMsPerSecond: round(perSec * 0.55), inclMsPerSecond: round(perSec), selfUsPerCall: round((perSec * 0.55 * 1000) / 169), allocSelfKBPerSecond: round(allocKBs * 0.2), allocInclKBPerSecond: round(allocKBs) },
      { method: `<>c.<Render>b__6_0`, calls: 1352, selfMsPerSecond: round(perSec * 0.3), inclMsPerSecond: round(perSec * 0.3), selfUsPerCall: round((perSec * 0.3 * 1000) / 1352), allocSelfKBPerSecond: round(allocKBs * 0.7), allocInclKBPerSecond: round(allocKBs * 0.7) },
      { method: `${name}.FormatLabel`, calls: 1352, selfMsPerSecond: round(perSec * 0.15), inclMsPerSecond: round(perSec * 0.15), selfUsPerCall: round((perSec * 0.15 * 1000) / 1352), allocSelfKBPerSecond: round(allocKBs * 0.1), allocInclKBPerSecond: round(allocKBs * 0.1) },
    ];
    return json({
      id: "synth" + this.refreshes, status: "done", plugin: name, durationMs: 4000, methodsPatched: 23, methodsCapped: false, patchMs: 31, refused: [],
      calls: 2873, selfTotalMsPerSecond: round(perSec), hookOverhead: { usPerCall: 0.31, bytesPerCall: 96.4 },
      note: "self = inclusive minus profiled callees (BCL/HUD calls count as self). The hooks' own cost (hookOverhead, measured on an empty method) is subtracted per call.",
      top, allocTotalKBPerSecond: round(allocKBs),
      topAlloc: top.map((m) => ({ method: m.method, calls: m.calls, allocSelfKBPerSecond: m.allocSelfKBPerSecond, bytesPerCall: Math.round((m.allocSelfKBPerSecond * 1024) / m.calls) })).sort((a, b) => b.allocSelfKBPerSecond - a.allocSelfKBPerSecond),
    });
  }

  // ── hud_plugin_lint ────────────────────────────────────────────────

  private lint(plugin: string): CallToolResult {
    const k = key(plugin);
    if (k === "healthbars") return json(lintHealthBars as Record<string, unknown>);
    if (k === "ninjapricer") return json(lintNinja as Record<string, unknown>);
    if (k === "reagent") return json({ plugins: [{ game: "poe2", plugin: "ReAgent", findings: [] }] });   // the empty state
    if (!plugin) return json({ plugins: [(lintHealthBars as { plugins: unknown[] }).plugins[0], (lintNinja as { plugins: unknown[] }).plugins[0]] });
    return json({ plugins: [{ game: "poe2", plugin, findings: [
      { method: `${plugin}.Render`, call: "Entity.GetComponent", count: 2, inLoop: true, costNs: 240, advice: "look the component up once per entity and keep the reference" },
      { method: `${plugin}.Render`, call: "String.Concat", count: 1, inLoop: true, costNs: 0, advice: "builds a new string per item per frame: cache it and rebuild only when the value changes" },
    ] }] });
  }

  // ── overlay_accuracy (synthesised: the real result has these fields and more) ──

  private overlay(): Record<string, unknown> {
    return {
      status: "done", durationMs: 5000, frames: 282, entities: 1, projectionSelfCheckPx: 0.0,
      errorPx: { avg: 0.9, p50: 0.6, p95: 2.8, max: 6.1, framesOver1Px: 61 },
      cameraSharePx: { avg: 0.7, p95: 2.4 }, positionSharePx: { avg: 0.2, p95: 0.6 },
      stale: { cameraFrames: 48, positionFrames: 3 },
      note: "Pixel distance between where plugins draw (the HUD's cached camera and position) and a fresh projection read from game memory at that moment. Meaningful only while the camera moves.",
    };
  }
}

// ── Synthesis ────────────────────────────────────────────────────────

function key(s: string): string { return s.toLowerCase().replace(/[^a-z0-9]/g, ""); }
function round(v: number, d = 3): number { return Math.round(v * 10 ** d) / 10 ** d; }

/** A deterministic pseudo-random for a seed, so screenshots of a given refresh repeat. */
function rng(seed: number): () => number {
  let s = seed * 9301 + 49297;
  return () => { s = (s * 9301 + 49297) % 233280; return s / 233280; };
}

function stats(values: number[]): Trace["frameIntervalMs"] {
  const v = values.filter((x) => Number.isFinite(x)).sort((a, b) => a - b);
  if (!v.length) return { n: 0 };
  const avg = v.reduce((a, b) => a + b, 0) / v.length;
  const sd = Math.sqrt(v.reduce((a, b) => a + (b - avg) ** 2, 0) / v.length);
  const q = (p: number) => v[Math.min(v.length - 1, Math.floor(p * v.length))];
  return { n: v.length, avg: round(avg), p50: round(q(0.5)), p95: round(q(0.95)), max: round(v[v.length - 1]), sd: round(sd) };
}

/** The real capture with the series nudged: same frames, small noise, a GC pause moved to another frame. */
function perturb(base: Report, seed: number): Report {
  const r = structuredClone(base);
  if (seed <= 1 || !r.trace?.series) return r;
  const rnd = rng(seed);
  const s = r.trace.series;
  const n = s.tMs.length;
  for (let i = 0; i < n; i++) {
    const iv = s.intervalMs[i];
    if (iv !== null && iv !== undefined) s.intervalMs[i] = round(Math.max(15.5, iv + (rnd() - 0.5) * 0.8), 2);
    s.workMs[i] = round(Math.max(1.2, s.workMs[i] + (rnd() - 0.5) * 0.3));
    s.pluginsMs[i] = round(Math.min(s.workMs[i], Math.max(0.8, s.pluginsMs[i] + (rnd() - 0.5) * 0.2)));
  }
  // Move one pause to a frame that had none, and make that frame a spike too (the finding must stay true).
  const from = s.gcPauseMs.findIndex((g, i) => g > 3 && i > 60);
  const to = Math.min(n - 2, 100 + Math.floor(rnd() * 50));
  if (from >= 0 && s.gcPauseMs[to] === 0) {
    s.gcPauseMs[to] = s.gcPauseMs[from]; s.gcPauseMs[from] = 0;
    s.intervalMs[to] = round(17.5 + s.gcPauseMs[to], 2); s.intervalMs[from] = 17.42;
    s.workMs[to] = round(2 + s.gcPauseMs[to]); s.workMs[from] = 2.1;
  }
  const ivs = s.intervalMs.filter((x): x is number => x !== null);
  r.trace.frameIntervalMs = stats(ivs);
  r.trace.updateMs = stats(s.workMs);
  r.trace.pluginsMs = stats(s.pluginsMs);
  r.trace.coreMs = stats(s.workMs.map((w, i) => w - s.pluginsMs[i]));
  r.trace.hudFps = round(1000 / (r.trace.frameIntervalMs.avg ?? 17.7), 1);
  if (r.trace.gc) {
    r.trace.gc.pauseMsTotal = round(s.gcPauseMs.reduce((a, b) => a + b, 0), 1);
    r.trace.gc.allocMBPerSecond = round(r.trace.gc.allocMBPerSecond + (rnd() - 0.5) * 20, 1);
    r.trace.gc.fetchedMBPerSecond = round(r.trace.gc.fetchedMBPerSecond + (rnd() - 0.5) * 20, 1);
  }
  return r;
}

/** ~600 frames at 200 fps: 5 ms intervals, a few GC pauses, one work spike without a pause (the "mixed" verdict). */
function synthFps200(seed: number): Report {
  const r = structuredClone(BASE);
  const rnd = rng(seed + 7);
  const n = 600;
  const s: Series = { tMs: [], intervalMs: [], workMs: [], pluginsMs: [], gcPauseMs: [] };
  let t = 0;
  for (let i = 0; i < n; i++) {
    let gc = 0;
    if (i % 71 === 20) gc = round(4 + rnd() * 6, 2);
    const work = round(gc + 1.1 + rnd() * 0.5 + (i === 333 ? 9 : 0));
    const iv = round(Math.max(5, 5.0 + (rnd() - 0.5) * 0.4 + (gc > 0 ? gc : 0) + (i === 333 ? 9 : 0)), 2);
    s.tMs.push(round(t, 2)); s.intervalMs.push(i === n - 1 ? null : iv); s.workMs.push(work);
    s.pluginsMs.push(round(Math.min(work, 0.7 + rnd() * 0.3 + (i === 333 ? 8.5 : 0)))); s.gcPauseMs.push(gc);
    t += iv;
  }
  const tr = r.trace!;
  tr.series = s; tr.frames = n; tr.durationMs = 3000;
  const ivs = s.intervalMs.filter((x): x is number => x !== null);
  tr.frameIntervalMs = stats(ivs); tr.updateMs = stats(s.workMs); tr.pluginsMs = stats(s.pluginsMs); tr.coreMs = stats(s.workMs.map((w, i) => w - s.pluginsMs[i]));
  tr.hudFps = round(1000 / (tr.frameIntervalMs.avg ?? 5), 1);
  tr.gc = { ...tr.gc!, gen0: 9, gen1: 8, gen2: 0, pauseMsTotal: round(s.gcPauseMs.reduce((a, b) => a + b, 0), 1), allocMBPerSecond: 1210.4, fetchedMBPerSecond: 1180.2, fetchedPagesPerFrame: 1604 };
  r.findings = [
    `frame spikes up to ${tr.frameIntervalMs.max} ms (expected 5): GC pauses or a heavy plugin frame`,
    "the shared ArrayPool keeps 512 pages per size but the page cache cycles ~1604 per frame, so most become garbage: start the HUD with DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXARRAYSPERPARTITION=256 (decimal; scaffolding: <HUD>\\hud-env.txt, research/hud-gc.md)",
    "ReAgent costs 0.67 ms per frame: profile_plugin name=\"ReAgent\" (prompt optimize_plugin)",
  ];
  return r;
}

/** Nothing stands out: steady 60 fps, pages reused, no plugin over 0.3 ms, no findings; the empty states. */
function synthClean(seed: number): Report {
  const rnd = rng(seed + 3);
  const n = 180;
  const s: Series = { tMs: [], intervalMs: [], workMs: [], pluginsMs: [], gcPauseMs: [] };
  let t = 0;
  for (let i = 0; i < n; i++) {
    const iv = round(16.67 + (rnd() - 0.5) * 0.6, 2);
    const work = round(0.9 + rnd() * 0.3);
    s.tMs.push(round(t, 2)); s.intervalMs.push(i === n - 1 ? null : iv); s.workMs.push(work); s.pluginsMs.push(round(work * 0.45)); s.gcPauseMs.push(i === 95 ? 1.4 : 0);
    t += iv;
  }
  const ivs = s.intervalMs.filter((x): x is number => x !== null);
  const trace: Trace = {
    id: "clean" + seed, status: "done", durationMs: 3000, frames: n, hudFps: round(1000 / (stats(ivs).avg ?? 16.7), 1),
    frameIntervalMs: stats(ivs), updateMs: stats(s.workMs), pluginsMs: stats(s.pluginsMs), coreMs: stats(s.workMs.map((w, i) => w - s.pluginsMs[i])),
    series: s,
    gc: { gen0: 1, gen1: 0, gen2: 0, pauseMsTotal: 1.4, allocMBPerSecond: 11.2, fetchedMBPerSecond: 310.5, fetchedPagesPerFrame: 1590, sharedArrayPool: { partitions: 16, maxArraysPerPartition: 256, arraysPerSize: 4096, envMaxArraysPerPartition: 256, broken: null } },
    pluginTickMs: { HealthBars: { n: 180, avg: 0.12, p50: 0.1, p95: 0.2, max: 0.6, sd: 0.05 } },
    pluginRenderMs: { HealthBars: { n: 180, avg: 0.15, p50: 0.13, p95: 0.25, max: 0.7, sd: 0.06 }, Radar: { n: 180, avg: 0.03, p50: 0.027, p95: 0.05, max: 0.09, sd: 0.01 }, WhatsAnAiBridge: { n: 180, avg: 0.18, p50: 0.17, p95: 0.29, max: 0.45, sd: 0.06 } },
    pluginAllocKBPerFrame: { render: { HealthBars: 3.1, Radar: 0.4, WhatsAnAiBridge: 4.8 }, tick: { HealthBars: 1.2 } },
  };
  return {
    trace,
    plugins: [
      { name: "HealthBars", tickMs: 0.12, renderMs: 0.15, allocKBPerFrame: 4.3 },
      { name: "WhatsAnAiBridge", tickMs: 0, renderMs: 0.18, allocKBPerFrame: 4.8 },
      { name: "Radar", tickMs: 0, renderMs: 0.03, allocKBPerFrame: 0.4 },
    ],
    findings: [], lint: [],
    actions: [{ label: "Check drawing lag", tool: "overlay_accuracy", args: {} }],
  };
}

function json(data: Record<string, unknown>, textOverride?: string, isError = false): CallToolResult {
  const txt = textOverride ?? JSON.stringify(data);
  return { content: [{ type: "text", text: txt }], structuredContent: data, isError: isError || undefined };
}

/** The health report's shape when the trace failed: text with the reason, structuredContent without `trace`. */
function text(data: Record<string, unknown>, txt: string): CallToolResult {
  return { content: [{ type: "text", text: txt }], structuredContent: data };
}
