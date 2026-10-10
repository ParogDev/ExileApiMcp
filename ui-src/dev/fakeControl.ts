// In-memory stand-in for ExileApiMcp as the control center sees it: hud_catalog from the real capture
// (dev/control/catalog.json, 95 tools), bridge_status, synthesised hud_settings / hud_settings_set (the shapes of
// Tools/ControlDtos.cs, the bridge plugin's real settings plus three other plugins), the observer (status, events that
// keep coming while observing, layers with preflight, a layer map), perf through FakePerf, and the explorer / memory /
// stats fakes behind their tools so the standalone pages work. Any other catalog tool answers with a sample built from
// its output schema (typed) or an echo of its arguments.
//
// Scenarios: live, offline (every game tool fails like an unreachable bridge; offline tools work), no-hud (no bridge
// is running), flaky (30% failures plus jitter), push (events every 400 ms, perf reports every 2 s: the push states).
//
// Standalone harness: readResource / listen / postSettings mirror the server's resources, subscriptions/listen and
// POST /app/api/settings.

import type { CallToolResult } from "@modelcontextprotocol/client";
import type { ListenState } from "../src/control/host";
import type { Catalog, JsonSchema, LayerMapResult, LayerStatus, LayersResult, ObserveEvent, PluginSettings, SettingChangeResult, SettingNode, SettingsResult } from "../src/control/types";
import catalogFixture from "./control/catalog.json";
import { FakeExplorer } from "./fakeExplore";
import { FakeMemory } from "./fakeMemory";
import { FakePerf } from "./fakePerf";
import { FakeServer, type CallLogEntry } from "./fakeServer";

export type ControlScenario = "live" | "offline" | "no-hud" | "flaky" | "push";

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const CATALOG = catalogFixture as unknown as Catalog;
const UNREACHABLE = "The poe2 HUD bridge is not reachable (connection refused on 127.0.0.1:50900). Is the HUD running?";
const OFFLINE_TOOLS = new Set(["hud_catalog", "bridge_status", "hud_find_types", "hud_type", "hud_plugins", "hud_log", "knowledge", "findings", "hud_api_diff", "hud_property_map", "recording_list", "recording_info", "recording_frame", "recording_range", "recording_search", "recording_summary", "observe_timeline", "observe_series", "experiment_presets", "code_struct_layout", "find_field_access", "game_data", "find_in_game_data"]);
/** observe_series (PR #100) may postdate the catalog capture: the fake offers it like the server does. */
const SERIES_TOOL = { name: "observe_series", title: "One unit's values over time, its shape and relations", description: "The series of one observer unit from the journal: points, shape (toggle | states | counter | timer | continuous | text), top values, and how it relates to other units that change at the same moments.", family: "Observe", readOnly: true, destructive: false, idempotent: true, openWorld: false, inputSchema: { type: "object", properties: { layer: { type: "string" }, unit: { type: "string" }, windowMs: { type: "integer" }, game: { type: "string" } }, required: ["layer", "unit"] } };

function json(data: unknown, text?: string, isError = false): CallToolResult {
  return { content: [{ type: "text", text: text ?? JSON.stringify(data, null, 2) }], structuredContent: data as Record<string, unknown>, isError };
}

// ── Settings fixture (the bridge's real settings class, plus three plugins with the other kinds) ────────────────

type Def = [path: string, label: string, group: string, kind: SettingNode["kind"], value: unknown, extra?: Partial<SettingNode>];
const BRIDGE: Def[] = [
  ["Enable", "Enable", "", "toggle", true],
  ["BridgeDirectory", "Bridge directory", "Bridge", "text", "claude-bridge", { description: "Folder under the HUD for the port and token files agents read." }],
  ["PollIntervalMs", "Poll interval", "Bridge", "range", 250, { min: 50, max: 2000, description: "ms between file-IPC polls." }],
  ["EnableTcp", "Enable TCP", "TCP server", "toggle", true],
  ["TcpPort", "TCP port", "TCP server", "range", 50900, { min: 49152, max: 65535 }],
  ["EnableFileIpc", "Enable file IPC", "TCP server", "toggle", true],
  ["ShowStatusHud", "Show status HUD", "Status HUD", "toggle", true],
  ["HudX", "HUD X", "Status HUD", "range", 10, { min: 0, max: 3840 }],
  ["HudY", "HUD Y", "Status HUD", "range", 200, { min: 0, max: 2160 }],
  ["MaxEntityRange", "Max entity range", "Limits", "range", 200, { min: 50, max: 9999 }],
  ["MaxDeepStats", "Max deep stats", "Limits", "range", 80, { min: 10, max: 500 }],
  ["MaxUiChildren", "Max UI children", "Limits", "range", 300, { min: 50, max: 500 }],
  ["RecordingIntervalMs", "Recording interval", "Recording", "range", 200, { min: 50, max: 2000 }],
  ["RecordingEntityRange", "Recording entity range", "Recording", "range", 200, { min: 50, max: 9999 }],
  ["AutoDeepScanBosses", "Auto deep-scan bosses", "Recording", "toggle", true],
  ["RecordingMaxDeepStats", "Recording max deep stats", "Recording", "range", 200, { min: 10, max: 500 }],
  ["AllowPluginReload", "Allow plugin reload", "Dev loop", "toggle", true, { permission: true, description: "Let agents recompile a source plugin in place (reload_plugin). The HUD pauses while it compiles." }],
  ["AllowCSharpScripts", "Allow C# scripts", "Dev loop", "toggle", false, { permission: true, description: "Let agents run C# scripts inside the HUD (run_csharp). Off by default: it is arbitrary code in the HUD process." }],
  ["AllowHudInstrumentation", "Allow HUD instrumentation", "Dev loop", "toggle", false, { permission: true, description: "Let pipeline.trace patch the HUD's own unprotected code (Harmony) for a few seconds. Never the game." }],
  ["StatsPanelVitalsOpen", "Vitals section open", "Stats panel", "toggle", true],
  ["StatsPanelResistsOpen", "Resistances section open", "Stats panel", "toggle", true],
  ["StatsPanelPinnedOpen", "Pinned section open", "Stats panel", "toggle", true],
  ["StatsPanelShowKeys", "Show raw keys", "Stats panel", "toggle", false],
  ["ShowAgentGuide", "Show agent guide", "Agent guide", "toggle", true],
  ["GuideLogOpen", "Log section open", "Agent guide", "toggle", true],
  ["AgentLogHotkey", "Agent log hotkey", "Agent guide", "hotkey", "None", { readOnly: true, description: "Toggles the agent log sheet under the guide card. Set it in game." }],
];
const OTHERS: [string, boolean, Def[]][] = [
  ["Health Bars", true, [
    ["Enable", "Enable", "", "toggle", true],
    ["ShowPlayerBar", "Show player bar", "Player", "toggle", true],
    ["PlayerColor", "Player colour", "Player", "color", "#e2554bff"],
    ["ShowMonsters", "Show monsters", "Monsters", "toggle", true],
    ["UniqueColor", "Unique colour", "Monsters", "color", "#f2a33cff"],
    ["BarWidth", "Bar width", "Monsters", "range", 120, { min: 40, max: 300 }],
    ["TextSize", "Text size", "Monsters", "list", "Normal", { options: ["Small", "Normal", "Large"] }],
  ]],
  ["ReAgent", true, [
    ["Enable", "Enable", "", "toggle", true],
    ["ProfileName", "Profile", "", "text", "default"],
    ["DumpState", "Dump state to file", "Debug", "button", null, { description: "Writes the current rule state to the plugin folder." }],
    ["ToggleHotkey", "Toggle hotkey", "", "hotkey", "F6", { readOnly: true }],
  ]],
  ["Ninja Pricer", false, [
    ["Enable", "Enable", "", "toggle", false],
    ["League", "League", "", "list", "Mercenaries", { options: ["Standard", "Hardcore", "Mercenaries", "Hardcore Mercenaries"] }],
    ["RefreshMinutes", "Refresh every", "", "range", 30, { min: 5, max: 120, description: "Minutes between poe.ninja fetches." }],
    ["ShowOnHover", "Show on hover", "", "toggle", true],
  ]],
];
function nodes(defs: Def[]): SettingNode[] {
  return defs.map(([path, label, group, kind, value, extra]) => ({ path, label, group: group || null, kind, value, permission: false, readOnly: false, ...extra }));
}

// ── Observer fixtures ────────────────────────────────────────────────

const PANELS = [[3, "IngameUi.InventoryPanel"], [7, "IngameUi.StashElement"], [12, "IngameUi.Map"], [19, "IngameUi.SkillBar"], [42, null], [57, null]] as const;
const AREAS = ["The Coast", "Mud Flats", "The Submerged Passage", "The Ledge", "Lioneye's Watch"];
const SERVER_UNITS: [string, string | null][] = [["0x2368", "Gold"], ["0x2370", null], ["0x1a90", "CharacterLevel"], ["0x1aa0", "Experience"], ["0x2398", null], ["0x23a0", null], ["0x0f10", "PassiveSkillPoints"], ["0x7100", null], ["0x6500", null]];
const STAT_UNITS = ["base_maximum_life", "fire_damage_resistance_%", "movement_velocity_+%", "experience_gain_+%", "level"];
const BUFFS = ["flask_effect_life", "onslaught", "fortify", "phasing", "curse_vulnerability"];
const INVENTORIES = ["MainInventory", "Flask", "Weapon", "Offhand", "Cursor"];
const AGENT_CALLS: [string, Record<string, unknown>][] = [
  ["guide.highlight", { target: "Stash", tier: 1, text: "Ctrl-click the Currency tab" }],
  ["guide.set", { title: "Open your inventory", status: "waiting", step: 2, steps: 4 }],
  ["experiment.start", { experiment: "stash-ctrl-click", step: 3, timeoutMs: 120000 }],
  ["hud.reload_plugin", { plugin: "Whats An AI Bridge" }],
  ["settings.set", { plugin: "Health Bars", path: "ShowMonsters", value: true }],
  ["profile.plugin", { name: "ReAgent", durationMs: 3000 }],
];
type EventKind = "server" | "server-pair" | "stats" | "life" | "buff" | "inventory" | "noisy" | "ui" | "area" | "level" | "entity" | "spike" | "reload" | "agent";
const RANDOM_KINDS: [EventKind, number][] = [["server", 0.26], ["stats", 0.14], ["life", 0.14], ["buff", 0.08], ["inventory", 0.06], ["noisy", 0.03], ["ui", 0.14], ["area", 0.04], ["level", 0.02], ["entity", 0.03], ["spike", 0.04], ["agent", 0.02]];
function pickKind(): EventKind { let r = Math.random(); for (const [k, p] of RANDOM_KINDS) { r -= p; if (r <= 0) return k; } return "server"; }

export class FakeControl {
  private _scenario: ControlScenario = "live";
  get scenario(): ControlScenario { return this._scenario; }
  set scenario(s: ControlScenario) { this._scenario = s; this.restartPush(); }
  latencyMs = 60;
  log: CallLogEntry[] = [];
  onChange?: () => void;
  readonly perf = new FakePerf();
  readonly explorer = new FakeExplorer();
  readonly memory = new FakeMemory();
  readonly stats = new FakeServer();

  private settings: PluginSettings[] = [{ plugin: "Whats An AI Bridge", enabled: true, settings: nodes(BRIDGE) }, ...OTHERS.map(([plugin, enabled, defs]) => ({ plugin, enabled, settings: nodes(defs) }))];
  observing = true;
  private observeSince = new Date(Date.now() - 14 * 60_000).toISOString();
  private seq = 0;
  events: ObserveEvent[] = [];
  private t0 = Date.now() - 14 * 60_000;
  private layers: LayerStatus[] = [
    { spec: { id: "server", path: "GameController.IngameState.ServerData", mode: "struct", hz: 10, enabled: true, key: null }, events: 0, ticks: 0, costMs: 0.08, unitsChanged: 0, noisyUnits: 1, bytes: 9800, namedRanges: 212 },
    { spec: { id: "stats", path: "GameController.Player.GetComponent<Player>().Stats.StatDictionary", mode: "dict", hz: 4, enabled: true, key: null }, events: 0, ticks: 0, costMs: 0.31, unitsChanged: 0, noisyUnits: 0 },
    { spec: { id: "life", path: "GameController.Player.GetComponent<Life>()", mode: "props", hz: 4, enabled: true, key: null }, events: 0, ticks: 0, costMs: 0.05, unitsChanged: 0, noisyUnits: 0 },
    { spec: { id: "buffs", path: "GameController.Player.GetComponent<Buffs>().BuffsList", mode: "list", hz: 10, enabled: true, key: "Name" }, events: 0, ticks: 0, costMs: 0.12, unitsChanged: 0, noisyUnits: 0 },
    { spec: { id: "inventories", path: "GameController.IngameState.ServerData.PlayerInventories", mode: "each", hz: 4, enabled: true, key: "TypeId", props: ["Inventory.Hash", "Inventory.ItemCount"] }, events: 0, ticks: 0, costMs: 0.2, unitsChanged: 0, noisyUnits: 0 },
  ];
  private unitCounts = new Map<string, { changes: number; first: number; last: number; lastValue: string }>();
  private listeners = new Set<{ uris: string[]; cb: (uri: string) => void }>();
  private pushTimer?: ReturnType<typeof setInterval>;
  private perfPushTimer?: ReturnType<typeof setInterval>;
  private perfSeq = 0;
  private lastPerf?: Record<string, unknown>;
  /** The bridge keeps this many events in memory; the journal (observe_timeline / observe_series) has them all. */
  static readonly RING = 300;
  private values = new Map<string, number>();
  private lastSpikeAt = 0;

  constructor() {
    this.perf.traceMs = 900;
    this.perf.watchMs = 3000;
    this.seed();
    this.restartPush();
  }

  /** 14 minutes of history: a Poisson-ish stream with clusters (a panel opens, 300-600 ms later a server unit and a
   *  stat change; 0x7100 and 0x6500 move together), a HUD spike every ~70 s, one reload, and agent prompts followed by
   *  what the user did. Deterministic enough for screenshots: the shapes are always there, the exact spots vary. */
  private seed() {
    let t = this.t0;
    const end = Date.now() - 1500;
    let nextSpike = t + 20_000, reloadDone = false, nextAgent = t + 95_000;
    while (t < end) {
      t += 800 + Math.random() * 9000;
      if (t >= nextSpike) { this.emitAt(t, "spike"); nextSpike = t + 50_000 + Math.random() * 40_000; if (!reloadDone && t > this.t0 + 6 * 60_000) { this.emitAt(t + 4000, "reload"); reloadDone = true; } continue; }
      if (t >= nextAgent) { this.emitAt(t, "agent", 0); this.emitAt(t + 2600 + Math.random() * 1500, "ui", 1); this.emitAt(t + 4200 + Math.random() * 2000, "inventory"); nextAgent = t + 150_000 + Math.random() * 60_000; continue; }
      const r = Math.random();
      if (r < 0.22) { this.emitAt(t, "ui", 1); this.emitAt(t + 300 + Math.random() * 300, "server-pair"); this.emitAt(t + 420 + Math.random() * 200, "stats"); }
      else if (r < 0.3) { for (let i = 0; i < 4 + Math.floor(Math.random() * 6); i++) this.emitAt(t + i * 90, "life"); }
      else this.emitAt(t, pickKind());
    }
    this.events.sort((a, b) => Date.parse(a.at) - Date.parse(b.at));
    this.events.forEach((e, i) => { e.seq = i + 1; });
    this.seq = this.events.length;
  }

  // ── Harness hooks ────────────────────────────────────────────────

  /** The harness ticks every 2 s: an event now and then while observing. */
  tick() {
    this.stats.tick();
    if (this.observing && this.scenario !== "push" && Math.random() < 0.7) this.emit();
  }

  /** Emit one event now (the live feed's entering animation). `kind` forces one (e.g. harness.server.emit(false, "spike")). */
  emit(seed = false, kind?: EventKind) {
    this.emitAt(Date.now(), kind ?? pickKind());
    if (!seed) this.notify(`exile://observe/poe2/events`, `exile://observe/poe2/layers`);
    this.onChange?.();
  }

  private next(key: string, start: number, step: number): [number, number] {
    const old = this.values.get(key) ?? start;
    const nw = Math.max(0, old + Math.round((Math.random() * 2 - 0.7) * step));
    this.values.set(key, nw);
    return [old, nw];
  }

  private layerEvent(base: { seq: number; at: string; t: number; frame: number }, layer: string, mode: string, unit: string, name: string | null, old: number, nw: number, extra: Partial<ObserveEvent> = {}): ObserveEvent {
    const li = this.layers.findIndex((l) => l.spec.id === layer);
    this.bump(layer, unit, name, String(nw));
    if (li >= 0) { this.layers[li].events++; this.layers[li].unitsChanged = this.countUnits(layer); }
    return { ...base, kind: "layer", layer, mode, unit, name, old: String(old), new: String(nw), delta: nw - old, ...extra };
  }

  private emitAt(atMs: number, kind: EventKind, variant?: number) {
    const at = new Date(atMs).toISOString();
    const base = { seq: ++this.seq, at, t: atMs - this.t0, frame: Math.round((atMs - this.t0) / 16.7) };
    let e: ObserveEvent;
    switch (kind) {
      case "server": {
        const [unit, name] = SERVER_UNITS[Math.floor(Math.random() * 7)];
        const [old, nw] = this.next(`server:${unit}`, name === "Gold" ? 4200 : name === "Experience" ? 180_000 : 12, name === "Experience" ? 900 : 30);
        e = this.layerEvent(base, "server", "struct", unit, name, old, nw, { i32: String(nw), off: unit, len: 4 });
        break;
      }
      case "server-pair": {
        // Two unmapped offsets that always take the same value: the series' "same value" relation.
        const [old, nw] = this.next("server:0x7100", 3, 2);
        e = this.layerEvent(base, "server", "struct", "0x7100", null, old, nw, { i32: String(nw), off: "0x7100", len: 4 });
        const e2 = this.layerEvent({ ...base, seq: ++this.seq, at: new Date(atMs + 40).toISOString(), t: atMs + 40 - this.t0 }, "server", "struct", "0x6500", null, old, nw, { i32: String(nw), off: "0x6500", len: 4 });
        this.events.push(e2);
        break;
      }
      case "stats": {
        const unit = STAT_UNITS[Math.floor(Math.random() * STAT_UNITS.length)];
        const [old, nw] = this.next(`stats:${unit}`, 75, 12);
        e = this.layerEvent(base, "stats", "dict", unit, null, old, nw);
        break;
      }
      case "life": {
        const unit = Math.random() < 0.7 ? "CurHP" : "CurMana";
        const [old, nw] = this.next(`life:${unit}`, unit === "CurHP" ? 3100 : 420, 260);
        e = this.layerEvent(base, "life", "props", unit, null, old, nw);
        break;
      }
      case "buff": {
        const unit = BUFFS[Math.floor(Math.random() * BUFFS.length)];
        const added = Math.random() < 0.55;
        e = this.layerEvent(base, "buffs", "list", unit, null, added ? 0 : 1, added ? 1 : 0, { old: added ? null : unit, new: added ? unit : null, delta: null, change: added ? "added" : "removed" });
        break;
      }
      case "inventory": {
        const inv = INVENTORIES[Math.floor(Math.random() * INVENTORIES.length)];
        const [old, nw] = this.next(`inventories:${inv}.Inventory.ItemCount`, 24, 2);
        e = this.layerEvent(base, "inventories", "each", `${inv}.Inventory.ItemCount`, null, old, nw);
        this.events.push(this.layerEvent({ ...base, seq: ++this.seq }, "inventories", "each", `${inv}.Inventory.Hash`, null, 0, 0, { old: (0x7a3f1000 + old * 977).toString(16), new: (0x7a3f1000 + nw * 977).toString(16), delta: null }));
        break;
      }
      case "noisy": e = { ...base, kind: "layer.noisy", layer: "server", mode: "struct", group: "0x2380-0x23c0", unit: "0x2398", note: "changed on 92% of ticks: a timer; counted in the layer map" }; break;
      case "ui": {
        const [index, mapped] = variant === 1 ? PANELS[Math.random() < 0.6 ? 0 : 1] : PANELS[Math.floor(Math.random() * PANELS.length)];
        const visible = variant === 1 ? true : Math.random() < 0.6;
        e = { ...base, kind: "ui", index, visible, mapped, firstSeen: !mapped && Math.random() < 0.3, texts: visible ? (index === 3 ? ["Inventory", "Flasks"] : index === 7 ? ["Stash", "Currency"] : ["Panel"]) : null };
        break;
      }
      case "area": { const i = Math.floor(Math.random() * (AREAS.length - 1)); e = { ...base, kind: "area", from: AREAS[i], to: AREAS[i + 1] }; break; }
      case "level": { const [old, nw] = this.next("level", 15, 1); e = { ...base, kind: "level", from: String(old), to: String(Math.max(old, nw)), area: AREAS[2] }; break; }
      case "entity": e = { ...base, kind: "entity", type: "Monster", entityType: ["Metadata/Monsters/Rhoa/RhoaUnique", "Metadata/Monsters/Skeletons/SkeletonBoss", "Metadata/Monsters/Bandits/BanditBossOak"][Math.floor(Math.random() * 3)] }; break;
      case "spike": {
        // A real one: a full GC inside one frame (97.5 ms against a typical 18.2).
        const full = Math.random() < 0.4;
        e = full
          ? { ...base, kind: "hud", cause: "spike", intervalMs: 97.5, typicalMs: 18.2, gcMs: 81.3, gen0: 1, gen1: 1, gen2: 1, suppressed: 0 }
          : { ...base, kind: "hud", cause: "spike", intervalMs: Math.round((40 + Math.random() * 80) * 10) / 10, typicalMs: 16.9, gcMs: Math.random() < 0.6 ? Math.round(Math.random() * 30 * 10) / 10 : 0, gen0: Math.random() < 0.6 ? 1 : 0, gen1: 0, gen2: 0, suppressed: this.lastSpikeAt && atMs - this.lastSpikeAt < 30_000 ? Math.floor(Math.random() * 3) : 0 };
        this.lastSpikeAt = atMs;
        break;
      }
      case "reload": e = { ...base, kind: "hud", cause: "reload", plugin: "Whats An AI Bridge", ok: true, durationMs: 2140 }; break;
      case "agent": { const [method, params] = AGENT_CALLS[variant ?? Math.floor(Math.random() * AGENT_CALLS.length)]; e = { ...base, kind: "agent", method, params }; break; }
    }
    this.events.push(e);
    if (this.events.length > 2000) this.events = this.events.slice(-2000);
    for (const l of this.layers) l.ticks += 1;
  }

  /** What the bridge still has in memory (the last RING events); the journal is `events`. */
  private ring(): ObserveEvent[] { return this.events.slice(-FakeControl.RING); }

  private bump(layer: string, unit: string, _name: string | null, value: string) {
    const k = `${layer}:${unit}`;
    const t = Date.now() - this.t0;
    const u = this.unitCounts.get(k);
    if (u) { u.changes++; u.last = t; u.lastValue = value; } else this.unitCounts.set(k, { changes: 1, first: t, last: t, lastValue: value });
  }
  private countUnits(layer: string) { return [...this.unitCounts.keys()].filter((k) => k.startsWith(layer + ":")).length; }

  private restartPush() {
    clearInterval(this.pushTimer);
    this.pushTimer = this.scenario === "push" ? setInterval(() => { if (this.observing) this.emit(); }, 400) : undefined;
  }

  // ── Standalone host: resources, subscriptions, the settings route ───

  listen(uris: string[], cb: (uri: string) => void, onState?: (s: ListenState) => void): () => void {
    const l = { uris, cb };
    this.listeners.add(l);
    onState?.("connecting");
    const t = setTimeout(() => onState?.("live"), 300);
    if (uris.some((u) => u.includes("/perf/"))) this.startPerfPush();
    return () => { clearTimeout(t); this.listeners.delete(l); if (![...this.listeners].some((x) => x.uris.some((u) => u.includes("/perf/")))) this.stopPerfPush(); onState?.("off"); };
  }

  private notify(...uris: string[]) {
    for (const l of this.listeners) for (const u of uris) if (l.uris.includes(u)) setTimeout(() => l.cb(u), 30);
  }

  private startPerfPush() {
    if (this.perfPushTimer) return;
    const run = async () => { const r = await this.perf.handle("perf_watch", { since: this.perfSeq }); this.lastPerf = r.structuredContent as Record<string, unknown>; this.perfSeq = Number(this.lastPerf?.seq ?? this.perfSeq); this.notify("exile://perf/poe2/report"); };
    void run();
    this.perfPushTimer = setInterval(() => void run(), this.scenario === "push" ? 2000 : 6000);
  }
  private stopPerfPush() { clearInterval(this.perfPushTimer); this.perfPushTimer = undefined; }

  async readResource(uri: string): Promise<{ text?: string; json?: unknown }> {
    await sleep(this.latencyMs);
    let data: unknown;
    if (uri.endsWith("/events")) data = { enabled: this.observing, seq: this.seq, events: this.ring().slice(-100) };
    else if (uri.endsWith("/layers")) data = this.layersResult();
    else if (/\/layers\/[^/]+$/.test(uri)) data = this.layerMap(uri.split("/").pop()!, false, 200);
    else if (uri.includes("/perf/")) data = this.lastPerf ?? (await this.perf.handle("perf_watch", { since: 0 })).structuredContent;
    else throw new Error(`No resource at ${uri}`);
    return { json: data, text: JSON.stringify(data) };
  }

  async postSettings(body: { plugin: string; path: string; value: unknown }): Promise<SettingChangeResult> {
    await sleep(this.latencyMs + 200);
    const node = this.findSetting(body.plugin, body.path);
    if (!node) throw new Error(`404: No setting '${body.path}' on plugin '${body.plugin}'`);
    const previous = node.value;
    node.value = body.value;
    this.onChange?.();
    return { plugin: body.plugin, setting: { ...node }, previous };
  }

  // ── Tools ────────────────────────────────────────────────────────

  async handle(name: string, args: Record<string, unknown>): Promise<CallToolResult> {
    const t0 = performance.now();
    const jitter = this.scenario === "flaky" ? Math.random() * 500 : Math.random() * 20;
    const entry: CallLogEntry = { at: Date.now(), name, args, ms: 0, outcome: "ok" };
    try {
      const gameTool = !OFFLINE_TOOLS.has(name);
      if (name === "perf_watch" || name === "show_hud_performance" || name === "profile_plugin" || name === "hud_plugin_lint" || name === "overlay_accuracy") {
        if (gameTool && (this.scenario === "offline" || this.scenario === "no-hud")) { await sleep(this.latencyMs); throw new Error(UNREACHABLE); }
        this.perf.scenario = this.scenario === "flaky" ? "flaky" : "live";
        const r = await this.perf.handle(name, args);
        if (name === "perf_watch") { this.lastPerf = r.structuredContent as Record<string, unknown>; this.perfSeq = Number(this.lastPerf?.seq ?? 0); }
        return r;
      }
      if (/^(explore_object|show_data_explorer|watch_object|eval_path)$/.test(name)) { this.explorer.scenario = this.scenario === "offline" || this.scenario === "no-hud" ? "offline" : this.scenario === "flaky" ? "flaky" : "live"; return await this.explorer.handle(name, args); }
      if (/^(show_memory_view|memory_|watch_memory|findings|verify_finding|find_field_access|experiment_|guide|await_change)/.test(name)) { this.memory.scenario = this.scenario === "offline" || this.scenario === "no-hud" ? "offline" : this.scenario === "flaky" ? "flaky" : "live"; return await this.memory.handle(name, args); }
      if (/^(stats_|get_stat$|set_stat|select_stat|set_stats|show_player_stats)/.test(name)) { this.stats.scenario = this.scenario === "offline" || this.scenario === "no-hud" ? "offline" : this.scenario === "flaky" ? "flaky" : "live"; return await this.stats.handle(name === "show_player_stats" ? "stats_ui_state" : name, args); }
      if (name === "observe_wait") await sleep(Math.min(3000, Number(args.timeoutSec ?? 3) * 1000));
      else await sleep(this.latencyMs + jitter);
      if (gameTool && (this.scenario === "offline" || this.scenario === "no-hud" || (this.scenario === "flaky" && Math.random() < 0.3))) throw new Error(UNREACHABLE);
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

  private dispatch(name: string, a: Record<string, unknown>): CallToolResult {
    switch (name) {
      case "hud_catalog": {
        const gamesUp = this.scenario === "no-hud" || this.scenario === "offline" ? [] : ["poe2"];
        const tools = CATALOG.tools.some((t) => t.name === SERIES_TOOL.name) ? CATALOG.tools : [...CATALOG.tools, SERIES_TOOL as unknown as Catalog["tools"][number]];
        const c = { ...CATALOG, tools, server: { ...CATALOG.server, gamesUp } };
        return json(c, `${c.server.title} ${c.server.version}: ${c.tools.length} tools in 31 families, ${c.resources.length} resources (4 subscribable), ${c.prompts.length} prompts. HUDs up: ${gamesUp.join(", ") || "none"}`);
      }
      case "bridge_status": {
        const poe2 = this.scenario === "no-hud" ? { game: "poe2", bridgeDir: "C:\\...\\halp2\\claude-bridge", status: "not running" }
          : this.scenario === "offline" ? { game: "poe2", bridgeDir: "C:\\...\\halp2\\claude-bridge", status: "unreachable", error: UNREACHABLE }
          : { game: "poe2", bridgeDir: "C:\\...\\halp2\\claude-bridge", status: "connected", port: 50900, hello: { game: "poe2", hudBuild: "0.3.1.2-4e1c", protocolVersion: 9, cannotProvide: ["npcdialog"] }, findingsToCheck: this.scenario === "push" ? undefined : "1 finding(s) verified on another game but not on poe2: stash-tab-affinity-bit. Run verify_finding before relying on them (findings lists all)." };
        return json({ bridges: [{ game: "poe1", bridgeDir: "C:\\...\\PoeHelper\\claude-bridge", status: "not running" }, poe2], desktop: { idleSeconds: 4, displayLikelyOff: false, gameForeground: true } });
      }
      case "hud_settings":
        return json({ game: "poe2", plugins: this.settings.map((p) => ({ ...p, settings: p.settings.map((s) => ({ ...s })) })) } satisfies SettingsResult, `${this.settings.length} plugins, ${this.settings.reduce((n, p) => n + p.settings.length, 0)} settings`);
      case "hud_settings_set": {
        const node = this.findSetting(String(a.plugin), String(a.path));
        if (!node) return json({ error: "not_found", message: `No setting '${a.path}' on plugin '${a.plugin}'. Plugins: ${this.settings.map((p) => p.plugin).join(", ")}` }, undefined, true);
        if (node.permission) return json({ error: "permission", message: `'${node.label}' grants agents power and is never changed through MCP tools. Change it in the control center (standalone) or in game: HUD menu, Whats An AI Bridge, Dev loop.` }, undefined, true);
        if (node.readOnly) return json({ error: "read_only", message: `'${node.label}' (${node.kind}) can only be set in game.` }, undefined, true);
        const previous = node.value;
        let value = a.value;
        if (node.kind === "range" && typeof value === "number") value = Math.max(node.min ?? -Infinity, Math.min(node.max ?? Infinity, value));
        if (node.kind === "button") value = null; else node.value = value;
        return json({ plugin: String(a.plugin), setting: { ...node }, previous } satisfies SettingChangeResult, `${a.plugin}.${a.path}: ${JSON.stringify(previous)} -> ${JSON.stringify(node.value)}`);
      }
      case "observe": {
        const action = String(a.action ?? "status");
        if (action === "start") { this.observing = true; this.observeSince = new Date().toISOString(); this.restartPush(); }
        if (action === "stop") this.observing = false;
        const counts: Record<string, number> = {};
        for (const e of this.events) counts[e.kind] = (counts[e.kind] ?? 0) + 1;
        return json({ ok: true, enabled: this.observing, since: this.observing ? this.observeSince : null, seq: this.seq, counts, unmappedPanelsSeen: 2, entityTypesSeen: 7, layers: this.layers, journal: "C:\\...\\halp2\\claude-bridge\\observe\\journal.jsonl" });
      }
      case "observe_events": {
        const since = Number(a.since ?? 0), limit = Math.min(500, Number(a.limit ?? 100));
        const kinds = Array.isArray(a.kinds) ? new Set(a.kinds as string[]) : undefined;
        const list = this.ring().filter((e) => e.seq > since && (!kinds || kinds.has(e.kind))).slice(0, limit);
        return json({ enabled: this.observing, seq: this.seq, events: list }, `${list.length} events after ${since}; seq ${this.seq}`);
      }
      case "observe_wait": {
        const since = Number(a.since ?? 0);
        const list = this.ring().filter((e) => e.seq > since && ["ui", "area", "level"].includes(e.kind)).slice(0, 100);
        return json({ enabled: this.observing, seq: this.seq, events: list }, list.length ? `${list.length} noteworthy events` : "Nothing noteworthy yet");
      }
      case "observe_layers": return this.observeLayers(a);
      case "observe_layer_map": return json(this.layerMap(String(a.layer ?? "server"), !!a.unmappedOnly, Number(a.limit ?? 60)));
      case "observe_timeline": return this.observeTimeline(a);
      case "observe_series": return this.observeSeries(a);
      case "knowledge": return json({ topic: a.topic ?? "index", text: "# Knowledge\n\nPacks: shared/dev-loop, shared/api-costs, poe2/stats-layout ..." }, "# Knowledge\n\nPacks: shared/dev-loop, shared/api-costs, poe2/stats-layout ...");
      case "run_csharp": return json({ ok: false, error: "scripts_disabled", message: "Enable 'Allow C# Scripts' in the bridge settings (Dev loop) to run scripts." }, undefined, true);
      case "reload_plugin": return json({ ok: true, plugin: a.plugin ?? "Whats An AI Bridge", compiledInMs: 2140, diagnostics: [] }, `Reloaded ${a.plugin ?? "Whats An AI Bridge"} in 2.1 s, no diagnostics`);
      default: {
        const tool = CATALOG.tools.find((t) => t.name === name);
        if (!tool) throw new Error(`Unknown tool: ${name}`);
        if (tool.outputSchema) { const s = sampleOf(tool.outputSchema, name); return json(s, `${name}: sample result (${Object.keys(s as object).length} fields)`); }
        return json({ ok: true, tool: name, arguments: a, note: "The harness has no fixture for this tool: it echoes the call." }, `${name} called with ${JSON.stringify(a)}`);
      }
    }
  }

  private findSetting(plugin: string, path: string): SettingNode | undefined {
    return this.settings.find((p) => p.plugin.toLowerCase() === plugin.toLowerCase())?.settings.find((s) => s.path === path);
  }

  private layersResult(extra: Partial<LayersResult> = {}): LayersResult {
    return { modes: ["struct", "props", "dict", "list", "each"], layers: this.layers.map((l) => ({ ...l, spec: { ...l.spec } })), layer: null, preflight: null, removed: null, ...extra };
  }

  private observeLayers(a: Record<string, unknown>): CallToolResult {
    const action = String(a.action ?? "list");
    if (action === "list") return json(this.layersResult(), this.layers.map((l) => `${l.spec.id} ${l.spec.mode} ${l.spec.hz} Hz ${l.spec.path}`).join("\n"));
    if (action === "remove") {
      const id = String(a.id ?? "");
      if (!this.layers.some((l) => l.spec.id === id)) return json({ error: "not_found", message: `No layer '${id}'. Layers: ${this.layers.map((l) => l.spec.id).join(", ")}` }, undefined, true);
      this.layers = this.layers.filter((l) => l.spec.id !== id);
      this.notify("exile://observe/poe2/layers");
      return json(this.layersResult({ removed: id }), `Removed ${id}`);
    }
    const id = String(a.id ?? ""), path = String(a.path ?? "");
    if (!id) return json({ error: "bad_request", message: "set needs an id" }, undefined, true);
    const existing = this.layers.find((l) => l.spec.id === id);
    const spec = { id, path: path || existing?.spec.path || "", mode: String(a.mode ?? existing?.spec.mode ?? "props"), hz: Number(a.hz ?? 4), enabled: a.enabled !== false, key: (a.key as string | undefined) ?? existing?.spec.key ?? null };
    // Preflight like the bridge: resolve the path in game and name the broken link.
    let preflight: string | null = null;
    const m = /\.(\w+)(?:\(\))?(?:\[\d+\])?$/.exec(spec.path);
    if (!spec.path.startsWith("GameController")) preflight = `Path must start at GameController (got '${spec.path.split(".")[0]}')`;
    else if (/Missing|Nope|Typo/i.test(spec.path)) preflight = `No public property or field '${m?.[1] ?? "?"}' on type 'Entity'`;
    else if (spec.mode === "struct" && !/ServerData|Component|Element/.test(spec.path)) preflight = `struct mode needs an object with a cached offsets struct; '${m?.[1] ?? spec.path}' has none`;
    else if (spec.mode === "dict" && !/Dictionary/.test(spec.path)) preflight = `dict mode needs an IDictionary; '${m?.[1] ?? spec.path}' is not one`;
    const status: LayerStatus = existing ? { ...existing, spec, broken: preflight } : { spec, events: 0, ticks: 0, costMs: 0, unitsChanged: 0, noisyUnits: 0, broken: preflight, notNow: spec.mode === "list" && !preflight && Math.random() < 0.3 ? "the collection is empty right now (no player)" : null };
    this.layers = existing ? this.layers.map((l) => (l.spec.id === id ? status : l)) : [...this.layers, status];
    this.notify("exile://observe/poe2/layers");
    return json(this.layersResult({ layer: spec, preflight }), preflight ? `Stored ${id}; preflight FAILED: ${preflight}` : `Layer ${id}: ${spec.mode} ${spec.hz} Hz ${spec.path}`);
  }

  // ── observe_timeline / observe_series over the journal (every event, not just the ring) ──────────────────────────

  /** ObserveEvent.Key(): what happened, without values. */
  private static key(e: ObserveEvent): string {
    switch (e.kind) {
      case "layer": return `${e.layer} ${e.unit}` + (e.name ? ` ${e.name}` : "");
      case "layer.noisy": return `${e.layer} ${e.group ?? e.unit} noisy`;
      case "ui": return `ui [${e.index}] ${e.visible ? "opened" : "closed"} ${e.mapped ?? "unmapped"}`;
      case "area": return "area change";
      case "level": return "level up";
      case "entity": return `entity ${e.type}`;
      case "hud": return e.cause === "reload" ? `hud reload ${e.plugin}` : `hud ${e.cause}`;
      case "agent": return `agent ${e.method}`;
      default: return e.kind;
    }
  }

  private near(t: number, win: number): ObserveEvent[] {
    return this.events.filter((e) => Math.abs(Date.parse(e.at) - t) <= win);
  }

  private observeTimeline(a: Record<string, unknown>): CallToolResult {
    const windowMs = Math.max(10, Math.min(60_000, Number(a.windowMs ?? 1000)));
    const keep = Array.isArray(a.kinds) && a.kinds.length ? new Set(a.kinds as string[]) : undefined;
    const journalEvents = this.events.length + 4812; // older sessions on disk
    if (a.unit != null) {
      const layer = String(a.layer ?? "server"), unit = String(a.unit);
      const hits = this.events.filter((e) => e.kind === "layer" && e.layer === layer && e.unit?.toLowerCase() === unit.toLowerCase());
      if (!hits.length) return json({ journalEvents, windowMs, layer, unit, changes: 0, companions: [], recentChanges: [] }, `No logged change of ${layer} ${unit} in the last ${journalEvents} journal events (a noisy unit is counted, not logged: see observe_layer_map).`);
      const self = FakeControl.key(hits[0]);
      const tally = new Map<string, { n: number; sum: number }>();
      for (const h of hits) {
        const seen = new Set<string>();
        const t = Date.parse(h.at);
        for (const e of this.near(t, windowMs)) {
          if (e.seq === h.seq || (keep && !keep.has(e.kind))) continue;
          const k = FakeControl.key(e);
          if (k === self || seen.has(k)) continue;
          seen.add(k);
          const dt = Date.parse(e.at) - t;
          const x = tally.get(k); if (x) { x.n++; x.sum += dt; } else tally.set(k, { n: 1, sum: dt });
        }
      }
      const companions = [...tally.entries()].sort((x, y) => y[1].n - x[1].n).slice(0, 40).map(([event, v]) => ({ event, count: v.n, avgDtMs: Math.round((v.sum / v.n) * 10) / 10 }));
      return json({ journalEvents, windowMs, layer, unit, changes: hits.length, companions, recentChanges: hits.slice(-20) }, `${self} changed ${hits.length} times in the journal; within ${windowMs} ms of those changes:\n` + companions.map((c) => `  ${c.count}/${hits.length}  ${c.event}  (avg ${Math.round(c.avgDtMs)} ms)`).join("\n"));
    }
    const centre = a.around != null ? this.events.find((e) => e.seq === Number(a.around)) : this.events[this.events.length - 1];
    if (!centre) throw new Error(a.around != null ? `Event #${a.around} is not in the last ${journalEvents} journal events.` : "The journal is empty.");
    const t = Date.parse(centre.at);
    const list = this.near(t, windowMs).filter((e) => !keep || keep.has(e.kind) || e.seq === centre.seq).slice(0, 500);
    return json({ journalEvents, windowMs, centre: centre.seq, events: list }, `${list.length} events within ${windowMs} ms of #${centre.seq}`);
  }

  /** Tools/ObserveSeries.cs, reduced: points, shape, top values and the relations to units that change in the same windows. */
  private observeSeries(a: Record<string, unknown>): CallToolResult {
    const layer = String(a.layer ?? "server"), unit = String(a.unit ?? ""), windowMs = Math.max(10, Math.min(60_000, Number(a.windowMs ?? 1000)));
    const hits = this.events.filter((e) => e.kind === "layer" && e.layer === layer && e.unit?.toLowerCase() === unit.toLowerCase());
    if (!hits.length) return json({ error: "not_found", message: `No logged change of ${layer} ${unit} in the journal. Layers: ${this.layers.map((l) => l.spec.id).join(", ")}; see observe_layer_map ${layer} for its units.` }, undefined, true);
    const points = hits.map((e) => { const n = Number(e.new); return { at: e.at, seq: e.seq, value: e.new ?? null, number: e.new != null && e.new !== "" && Number.isFinite(n) ? n : null }; });
    const nums = points.map((p) => p.number).filter((n): n is number => n !== null);
    const numeric = nums.length === points.length && points.length > 0;
    const distinctVals = new Map<string, number>();
    for (const p of points) distinctVals.set(String(p.value), (distinctVals.get(String(p.value)) ?? 0) + 1);
    const distinct = distinctVals.size;
    const steps = numeric ? nums.slice(1).map((n, i) => n - nums[i]).filter((d) => d !== 0) : [];
    const absSteps = steps.map(Math.abs).sort((x, y) => x - y);
    const stepTypical = absSteps.length ? absSteps[Math.floor(absSteps.length / 2)] : null;
    const gaps = hits.slice(1).map((e, i) => Date.parse(e.at) - Date.parse(hits[i].at)).sort((x, y) => x - y);
    const intervalMedianMs = gaps.length ? gaps[Math.floor(gaps.length / 2)] : null;
    const intervalRegular = gaps.length >= 4 && (gaps[Math.floor(gaps.length * 0.8)] - gaps[Math.floor(gaps.length * 0.2)]) / Math.max(1, intervalMedianMs ?? 1) < 0.25;
    const monotone = numeric && steps.length >= 3 && steps.every((d) => d > 0);
    const shape = !numeric ? (distinct <= 2 ? "toggle" : distinct <= 8 ? "states" : "text") : intervalRegular ? "timer" : distinct <= 2 ? "toggle" : monotone ? "counter" : distinct <= 6 ? "states" : "continuous";
    const topValues = [...distinctVals.entries()].sort((x, y) => y[1] - x[1]).slice(0, 8).map(([value, count]) => ({ value, count }));
    // Relations: other layer units in the windows of this unit's changes; "same value" / "same step" when the numbers agree.
    const rel = new Map<string, { e: ObserveEvent; together: number; numeric: number; sameValue: number; sameStep: number }>();
    for (const h of hits) {
      const t = Date.parse(h.at), seen = new Set<string>();
      for (const e of this.near(t, windowMs)) {
        if (e.seq === h.seq || e.kind !== "layer" || !e.unit) continue;
        const k = `${e.layer} ${e.unit}`;
        if (k === `${layer} ${unit}` || seen.has(k)) continue;
        seen.add(k);
        let r = rel.get(k); if (!r) { r = { e, together: 0, numeric: 0, sameValue: 0, sameStep: 0 }; rel.set(k, r); }
        r.together++;
        const a1 = Number(h.new), b1 = Number(e.new);
        if (h.new != null && e.new != null && Number.isFinite(a1) && Number.isFinite(b1)) { r.numeric++; if (a1 === b1) r.sameValue++; if (h.delta != null && e.delta != null && h.delta === e.delta) r.sameStep++; }
      }
    }
    const relations = [...rel.entries()].sort((x, y) => y[1].together - x[1].together).slice(0, 12).map(([event, r]) => {
      const relation = r.numeric && r.sameValue / r.numeric >= 0.6 ? "same value" : r.numeric && r.sameStep / r.numeric >= 0.6 ? "same step" : null;
      const holds = relation === "same value" ? r.sameValue : relation === "same step" ? r.sameStep : 0;
      return { event, layer: r.e.layer, unit: r.e.unit, name: r.e.name ?? null, together: r.together, numeric: r.numeric, relation, holds, strength: hits.length ? Math.round((holds / hits.length) * 100) / 100 : 0 };
    });
    const result = { layer, unit, name: hits[0].name ?? null, journalEvents: this.events.length + 4812, changes: hits.length, windowMs, numeric, distinct, shape, min: numeric ? Math.min(...nums) : null, max: numeric ? Math.max(...nums) : null, stepTypical, intervalMedianMs, intervalRegular, topValues, relations, points: points.slice(-400) };
    return json(result, `${layer} ${unit}: ${hits.length} changes, shape ${shape}${numeric ? ` (${result.min} … ${result.max}, step ${stepTypical})` : ""}; ${relations.filter((r) => r.relation).map((r) => `${r.event} ${r.relation} ${r.holds}/${r.numeric}`).join(", ") || "no relation holds"}`);
  }

  private layerMap(layer: string, unmappedOnly: boolean, limit: number): LayerMapResult {
    const l = this.layers.find((x) => x.spec.id === layer);
    if (!l) throw new Error(`No layer '${layer}'. Layers: ${this.layers.map((x) => x.spec.id).join(", ")}`);
    const names = new Map<string, string | null>(layer === "server" ? SERVER_UNITS : []);
    const units = [...this.unitCounts.entries()].filter(([k]) => k.startsWith(layer + ":")).map(([k, u]) => {
      const unit = k.slice(layer.length + 1);
      const minutes = Math.max(0.1, (u.last - u.first) / 60_000);
      return { unit, name: names.get(unit) ?? null, changes: u.changes, perMinute: u.changes / minutes, firstT: u.first, lastT: u.last, last: u.lastValue, logged: unit !== "0x2370" };
    }).filter((u) => !unmappedOnly || !u.name).sort((a, b) => b.changes - a.changes).slice(0, limit);
    return { t: Date.now() - this.t0, layer: l, units };
  }
}

/** A plausible value for a schema, for tools the harness has no fixture for. */
export function sampleOf(s: JsonSchema, key = "", depth = 0): unknown {
  const type = Array.isArray(s.type) ? s.type.find((t) => t !== "null") : s.type;
  if (s.enum?.length) return s.enum[0];
  switch (type) {
    case "string":
      if (/^at$|time|date/i.test(key)) return new Date().toISOString();
      if (/game/i.test(key)) return "poe2";
      if (/path/i.test(key)) return "GameController.Player";
      if (/plugin|name$/i.test(key)) return "ReAgent";
      if (/^id$/i.test(key)) return "sample";
      return `sample ${key}`.trim();
    case "integer": return /seq|count|n$|frames/i.test(key) ? 42 : 3;
    case "number": return 1.25;
    case "boolean": return !/error|broken|missing/i.test(key);
    case "array": return depth > 3 || !s.items ? [] : [sampleOf(s.items, key, depth + 1), sampleOf(s.items, key, depth + 1)];
    case "object": {
      if (depth > 3 || !s.properties) return {};
      const o: Record<string, unknown> = {};
      for (const [k, v] of Object.entries(s.properties)) o[k] = sampleOf(v, k, depth + 1);
      return o;
    }
    default:
      if (s.properties) return sampleOf({ ...s, type: "object" }, key, depth);
      return null;
  }
}
