// The control center's contracts. Mirrors Tools/ControlDtos.cs (hud_catalog, hud_settings, hud_settings_set),
// Tools/ObserveDtos.cs and Tools/PerfDtos.cs; change both together. Unknown fields pass through untouched.

export type Game = "poe1" | "poe2";

// ── hud_catalog ─────────────────────────────────────────────────────

export interface CatalogIcon { src: string; mimeType?: string | null; theme?: "light" | "dark" | string | null }

export interface JsonSchema {
  type?: string | string[];
  description?: string;
  default?: unknown;
  enum?: unknown[];
  properties?: Record<string, JsonSchema>;
  required?: string[];
  items?: JsonSchema;
  minimum?: number;
  maximum?: number;
  additionalProperties?: boolean | JsonSchema;
  [k: string]: unknown;
}

export interface CatalogTool {
  name: string;
  title?: string | null;
  description?: string | null;
  family: string;
  readOnly: boolean;
  destructive: boolean;
  idempotent: boolean;
  openWorld: boolean;
  icons?: CatalogIcon[] | null;
  inputSchema?: JsonSchema | null;
  outputSchema?: JsonSchema | null;
  appUri?: string | null;
}

export interface CatalogResource {
  uri?: string | null;
  uriTemplate?: string | null;
  name: string;
  title?: string | null;
  description?: string | null;
  mimeType?: string | null;
  subscribable: boolean;
  icons?: CatalogIcon[] | null;
}

export interface CatalogPromptArgument { name: string; description?: string | null; required: boolean }
export interface CatalogPrompt { name: string; title?: string | null; description?: string | null; arguments?: CatalogPromptArgument[] | null }

export interface CatalogServer { name: string; title?: string | null; version: string; icons?: CatalogIcon[] | null; gamesUp: string[] }

export interface Catalog {
  server: CatalogServer;
  tools: CatalogTool[];
  resources: CatalogResource[];
  prompts: CatalogPrompt[];
}

// ── hud_settings / hud_settings_set ─────────────────────────────────

export type SettingKind = "toggle" | "range" | "text" | "list" | "color" | "hotkey" | "button" | "unknown";

export interface SettingNode {
  path: string;
  label: string;
  group?: string | null;
  description?: string | null;
  kind: SettingKind | string;
  value?: unknown;
  min?: number | null;
  max?: number | null;
  options?: string[] | null;
  /** Grants agents power: never changeable through MCP tools. */
  permission: boolean;
  /** Shown but not editable from outside the game (hotkeys). */
  readOnly: boolean;
}

export interface PluginSettings { plugin: string; enabled: boolean; settings: SettingNode[] }
export interface SettingsResult { game: string; plugins: PluginSettings[] }
export interface SettingChangeResult { plugin: string; setting: SettingNode; previous?: unknown }

// ── Observer (Tools/ObserveDtos.cs) ─────────────────────────────────

export interface ObserveEvent {
  seq: number;
  at: string;
  t?: number | null;
  frame?: number | null;
  kind: string;
  layer?: string | null;
  mode?: string | null;
  unit?: string | null;
  name?: string | null;
  old?: string | null;
  new?: string | null;
  delta?: number | null;
  change?: string | null;
  group?: string | null;
  note?: string | null;
  index?: number | null;
  visible?: boolean | null;
  mapped?: string | null;
  firstSeen?: boolean | null;
  texts?: string[] | null;
  from?: string | null;
  to?: string | null;
  area?: string | null;
  type?: string | null;
  entityType?: string | null;
  // struct mode
  off?: string | null;
  len?: number | null;
  i32?: string | null;
  i64?: string | null;
  // hud: the HUD's own hiccups (spike | reload), so they aren't mistaken for game events
  cause?: string | null;
  intervalMs?: number | null;
  typicalMs?: number | null;
  gcMs?: number | null;
  gen0?: number | null;
  gen1?: number | null;
  gen2?: number | null;
  suppressed?: number | null;
  plugin?: string | null;
  ok?: boolean | null;
  durationMs?: number | null;
  // agent: what an agent asked of the HUD or the user
  method?: string | null;
  params?: Record<string, unknown> | null;
  [k: string]: unknown;
}

export interface ObserveEventsResult { enabled: boolean; seq: number; events: ObserveEvent[] }

/** observe_timeline: around=<seq> gives centre + events; layer + unit gives changes, companions and recentChanges. */
export interface TimelineCompanion { event: string; count: number; avgDtMs: number }
export interface TimelineResult {
  journalEvents: number;
  windowMs: number;
  centre?: number | null;
  events?: ObserveEvent[] | null;
  layer?: string | null;
  unit?: string | null;
  changes?: number | null;
  companions?: TimelineCompanion[] | null;
  recentChanges?: ObserveEvent[] | null;
  [k: string]: unknown;
}

/** observe_series {layer, unit, windowMs?} (Tools/ObserveSeries.cs): one unit's values over time, its shape, and how
 *  it relates to other events. An older server may not offer the tool: the UI checks the catalog first. */
export interface SeriesPoint { at: string; seq?: number | null; value?: string | null; number?: number | null; [k: string]: unknown }
export interface SeriesRelation {
  event: string;
  layer?: string | null;
  unit?: string | null;
  name?: string | null;
  /** How many of this unit's changes had the other event within the window. */
  together?: number | null;
  /** How many of those pairs were numeric on both sides. */
  numeric?: number | null;
  /** same value | same step | step xK … */
  relation?: string | null;
  /** How many pairs the relation held in. */
  holds?: number | null;
  strength?: number | null;
  [k: string]: unknown;
}
export interface SeriesResult {
  layer: string;
  unit: string;
  name?: string | null;
  journalEvents?: number | null;
  changes?: number | null;
  windowMs?: number | null;
  numeric?: boolean | null;
  distinct?: number | null;
  /** toggle | states | counter | timer | continuous | text */
  shape?: string | null;
  min?: number | null;
  max?: number | null;
  stepTypical?: number | null;
  intervalMedianMs?: number | null;
  intervalRegular?: boolean | null;
  topValues?: { value: string; count: number }[] | null;
  relations?: SeriesRelation[] | null;
  points?: SeriesPoint[] | null;
  [k: string]: unknown;
}

export interface LayerSpec { id: string; path: string; mode: string; hz: number; enabled: boolean; key?: string | null; props?: string[] | null }

export interface LayerStatus {
  spec: LayerSpec;
  events: number;
  ticks: number;
  costMs: number;
  unitsChanged: number;
  noisyUnits: number;
  bytes?: number | null;
  namedRanges?: number | null;
  slowProps?: string[] | null;
  broken?: string | null;
  notNow?: string | null;
}

export interface LayersResult { modes: string[]; layers: LayerStatus[]; layer?: LayerSpec | null; preflight?: string | null; removed?: string | null }

export interface LayerUnit { unit: string; name?: string | null; changes: number; perMinute: number; firstT: number; lastT: number; last: string; logged: boolean }
export interface LayerMapResult { t: number; layer: LayerStatus; units: LayerUnit[] }

/** observe action=status (the bridge's observe.status, untyped). */
export interface ObserveStatus {
  ok?: boolean;
  enabled: boolean;
  since?: string | null;
  seq: number;
  counts?: Record<string, number>;
  unmappedPanelsSeen?: number;
  entityTypesSeen?: number;
  layers?: LayerStatus[];
  journal?: string;
}

// ── Performance (Tools/PerfDtos.cs) ─────────────────────────────────

export interface PerfSnapshot {
  game: string;
  seq: number;
  at?: string | null;
  fresh: boolean;
  intervalSec: number;
  report?: HealthReportLite | null;
  text?: string | null;
}

/** Only what the overview needs from a health report; the perf app has the full shape (src/perf/types.ts). */
export interface HealthReportLite {
  desktop?: { displayLikelyOff?: boolean; gameForeground?: boolean; warning?: string } | null;
  trace?: {
    hudFps?: number;
    frames?: number;
    durationMs?: number;
    frameIntervalMs?: { avg?: number; p95?: number; max?: number };
    gc?: { gen0?: number; gen1?: number; gen2?: number; pauseMsTotal?: number; allocMBPerSecond?: number };
    series?: { intervalMs?: (number | null)[]; gcPauseMs?: number[] };
    error?: string;
    message?: string;
  } | null;
  findings?: string[] | null;
  plugins?: { name: string; tickMs: number; renderMs: number; allocKBPerFrame: number }[] | null;
}

// ── bridge_status (untyped) ─────────────────────────────────────────

export interface BridgeEntry {
  game: string;
  status: "connected" | "not running" | "unreachable" | string;
  port?: number;
  error?: string;
  hello?: Record<string, unknown>;
  findingsToCheck?: string;
  hudApiChanged?: string;
}
