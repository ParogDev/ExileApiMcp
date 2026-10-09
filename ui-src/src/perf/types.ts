// The structuredContent shapes of show_hud_performance / hud_health_report (series: true), profile_plugin and
// hud_plugin_lint. Mirrors Tools/HealthReportTools.cs, Tools/PipelineTraceTools.cs and Tools/PluginLintTools.cs,
// and the bridge's PipelineTrace / PluginProfiler; change both together.

export type Game = "poe1" | "poe2";

export interface Stats {
  n: number;
  avg?: number;
  p50?: number;
  p95?: number;
  max?: number;
  sd?: number;
}

export interface Desktop {
  idleSeconds?: number;
  displayTimeoutSeconds?: number;
  displayLikelyOff?: boolean;
  gameForeground?: boolean;
  warning?: string;
}

/** Parallel per-frame arrays; the last intervalMs is null (no next frame). */
export interface Series {
  tMs: number[];
  intervalMs: (number | null)[];
  workMs: number[];
  pluginsMs: number[];
  gcPauseMs: number[];
}

export interface SharedArrayPool {
  partitions?: number;
  maxArraysPerPartition?: number;
  arraysPerSize?: number;
  envMaxArraysPerPartition?: number | null;
  broken?: string | null;
}

export interface Gc {
  gen0: number;
  gen1: number;
  gen2: number;
  pauseMsTotal: number;
  allocMBPerSecond: number;
  fetchedMBPerSecond: number;
  fetchedPagesPerFrame: number;
  sharedArrayPool?: SharedArrayPool;
}

export interface Trace {
  id?: string;
  status?: string;
  durationMs: number;
  frames: number;
  events?: number;
  eventsDropped?: number;
  hudFps: number;
  frameIntervalMs: Stats;
  updateMs: Stats;
  pluginsMs: Stats;
  coreMs: Stats;
  drawMs?: Stats;
  presentCallMs?: Stats;
  series?: Series;
  gc?: Gc;
  pluginTickMs?: Record<string, Stats>;
  pluginRenderMs?: Record<string, Stats>;
  pluginAllocKBPerFrame?: { render?: Record<string, number>; tick?: Record<string, number> };
  patches?: { patched: string[]; refused: string[] };
}

export interface PluginCost {
  name: string;
  tickMs: number;
  renderMs: number;
  allocKBPerFrame: number;
}

export interface LintHit {
  plugin: string;
  method: string;
  call: string;
  count: number;
  inLoop?: boolean;
  costNs: number;
  advice: string;
}

export interface Action {
  label: string;
  tool: string;
  args: Record<string, unknown>;
}

/** show_hud_performance's structuredContent. Without `trace` the text's "Trace:" line says why. */
export interface Report {
  desktop?: Desktop;
  trace?: Trace;
  plugins?: PluginCost[];
  findings?: string[];
  lint?: LintHit[];
  actions?: Action[];
}

// ── profile_plugin ───────────────────────────────────────────────────

export interface ProfileMethod {
  method: string;
  calls: number;
  selfMsPerSecond: number;
  inclMsPerSecond: number;
  selfUsPerCall: number;
  allocSelfKBPerSecond: number;
  allocInclKBPerSecond: number;
}

export interface ProfileAlloc {
  method: string;
  calls: number;
  allocSelfKBPerSecond: number;
  bytesPerCall: number;
}

export interface ProfileResult {
  id?: string;
  status: string;
  plugin: string;
  durationMs: number;
  methodsPatched: number;
  methodsCapped?: boolean;
  patchMs?: number;
  refused?: string[];
  calls: number;
  selfTotalMsPerSecond: number;
  hookOverhead?: { usPerCall: number; bytesPerCall: number };
  note?: string;
  top: ProfileMethod[];
  allocTotalKBPerSecond?: number;
  topAlloc?: ProfileAlloc[];
}

// ── hud_plugin_lint ──────────────────────────────────────────────────

export interface LintFinding {
  method: string;
  call: string;
  count: number;
  inLoop: boolean;
  costNs: number;
  advice: string;
}

export interface LintResult {
  plugins: { game?: string; plugin: string; findings: LintFinding[] }[];
}

/** A bridge / tool error as ToolResults.Json flags it (isError). */
export interface ToolError {
  error: string;
  message?: string;
}

export function asError(data: unknown): ToolError | undefined {
  if (data && typeof data === "object" && typeof (data as ToolError).error === "string") return data as ToolError;
  return undefined;
}
