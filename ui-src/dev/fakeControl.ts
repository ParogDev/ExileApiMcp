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
const OFFLINE_TOOLS = new Set(["hud_catalog", "bridge_status", "hud_find_types", "hud_type", "hud_plugins", "hud_log", "knowledge", "findings", "hud_api_diff", "hud_property_map", "recording_list", "recording_info", "recording_frame", "recording_range", "recording_search", "recording_summary", "observe_timeline", "experiment_presets", "code_struct_layout", "find_field_access", "game_data", "find_in_game_data"]);

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
const SERVER_UNITS: [string, string | null][] = [["0x2368", "Gold"], ["0x2370", null], ["0x1a90", "CharacterLevel"], ["0x1aa0", "Experience"], ["0x2398", null], ["0x23a0", null], ["0x0f10", "PassiveSkillPoints"]];
const STAT_UNITS = ["base_maximum_life", "fire_damage_resistance_%", "movement_velocity_+%", "experience_gain_+%", "level"];

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
    { spec: { id: "server", path: "GameController.IngameState.ServerData", mode: "struct", hz: 4, enabled: true, key: null }, events: 0, ticks: 0, costMs: 0.08, unitsChanged: 0, noisyUnits: 1, bytes: 9800, namedRanges: 212 },
    { spec: { id: "stats", path: "GameController.Player.GetComponent<Player>().Stats.StatDictionary", mode: "dict", hz: 4, enabled: true, key: null }, events: 0, ticks: 0, costMs: 0.31, unitsChanged: 0, noisyUnits: 0 },
    { spec: { id: "buffs", path: "GameController.Player.GetComponent<Buffs>().BuffsList", mode: "list", hz: 10, enabled: false, key: "Address" }, events: 12, ticks: 1200, costMs: 0.12, unitsChanged: 6, noisyUnits: 0 },
  ];
  private unitCounts = new Map<string, { changes: number; first: number; last: number; lastValue: string }>();
  private listeners = new Set<{ uris: string[]; cb: (uri: string) => void }>();
  private pushTimer?: ReturnType<typeof setInterval>;
  private perfPushTimer?: ReturnType<typeof setInterval>;
  private perfSeq = 0;
  private lastPerf?: Record<string, unknown>;

  constructor() {
    this.perf.traceMs = 900;
    this.perf.watchMs = 3000;
    for (let i = 0; i < 40; i++) this.emit(true);
    this.restartPush();
  }

  // ── Harness hooks ────────────────────────────────────────────────

  /** The harness ticks every 2 s: an event now and then while observing. */
  tick() {
    this.stats.tick();
    if (this.observing && this.scenario !== "push" && Math.random() < 0.7) this.emit();
  }

  /** Emit one event now (the live feed's entering animation). */
  emit(seed = false) {
    const kind = Math.random();
    const at = seed ? new Date(this.t0 + (this.seq / 40) * 14 * 60_000).toISOString() : new Date().toISOString();
    const base = { seq: ++this.seq, at, t: Date.now() - this.t0, frame: 10_000 + this.seq * 37 };
    let e: ObserveEvent;
    if (kind < 0.45) {
      const [unit, name] = SERVER_UNITS[Math.floor(Math.random() * SERVER_UNITS.length)];
      const old = Math.floor(Math.random() * 5000), nw = old + Math.floor(Math.random() * 40) - 10;
      e = { ...base, kind: "layer", layer: "server", mode: "struct", unit, name, old: String(old), new: String(nw), delta: nw - old, i32: String(nw) };
      this.bump("server", unit, name, String(nw));
      this.layers[0].events++; this.layers[0].unitsChanged = this.countUnits("server");
    } else if (kind < 0.65) {
      const unit = STAT_UNITS[Math.floor(Math.random() * STAT_UNITS.length)];
      const old = Math.floor(Math.random() * 300), nw = old + Math.floor(Math.random() * 20) - 5;
      e = { ...base, kind: "layer", layer: "stats", mode: "dict", unit, old: String(old), new: String(nw), delta: nw - old };
      this.bump("stats", unit, null, String(nw));
      this.layers[1].events++; this.layers[1].unitsChanged = this.countUnits("stats");
    } else if (kind < 0.85) {
      const [index, mapped] = PANELS[Math.floor(Math.random() * PANELS.length)];
      const visible = Math.random() < 0.6;
      e = { ...base, kind: "ui", index, visible, mapped, firstSeen: !mapped && Math.random() < 0.3, texts: visible ? ["Inventory", "Flasks"] : null };
    } else if (kind < 0.92) {
      const i = Math.floor(Math.random() * (AREAS.length - 1));
      e = { ...base, kind: "area", from: AREAS[i], to: AREAS[i + 1] };
    } else if (kind < 0.96) {
      e = { ...base, kind: "level", from: "15", to: "16", area: AREAS[2] };
    } else {
      e = { ...base, kind: "entity", type: "Monster", entityType: "Metadata/Monsters/Rhoa/RhoaUnique" };
    }
    this.events = [...this.events.slice(-999), e];
    for (const l of this.layers) l.ticks += 4;
    if (!seed) this.notify(`exile://observe/poe2/events`, `exile://observe/poe2/layers`);
    this.onChange?.();
  }

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
    if (uri.endsWith("/events")) data = { enabled: this.observing, seq: this.seq, events: this.events.slice(-100) };
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
        const c = { ...CATALOG, server: { ...CATALOG.server, gamesUp } };
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
        const list = this.events.filter((e) => e.seq > since && (!kinds || kinds.has(e.kind))).slice(0, limit);
        return json({ enabled: this.observing, seq: this.seq, events: list }, `${list.length} events after ${since}; seq ${this.seq}`);
      }
      case "observe_wait": {
        const since = Number(a.since ?? 0);
        const list = this.events.filter((e) => e.seq > since && ["ui", "area", "level"].includes(e.kind)).slice(0, 100);
        return json({ enabled: this.observing, seq: this.seq, events: list }, list.length ? `${list.length} noteworthy events` : "Nothing noteworthy yet");
      }
      case "observe_layers": return this.observeLayers(a);
      case "observe_layer_map": return json(this.layerMap(String(a.layer ?? "server"), !!a.unmappedOnly, Number(a.limit ?? 60)));
      case "observe_timeline": return json({ journalEvents: this.events.length, windowMs: Number(a.windowMs ?? 1000), layer: a.layer ?? "server", unit: a.unit ?? "0x2368", changes: 14, companions: [{ event: "ui [3] opened IngameUi.InventoryPanel", count: 11, avgDtMs: -420 }, { event: "stats fire_damage_resistance_%", count: 3, avgDtMs: 120 }], recentChanges: this.events.filter((e) => e.kind === "layer").slice(-5) });
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
    return { modes: ["struct", "props", "dict", "list"], layers: this.layers.map((l) => ({ ...l, spec: { ...l.spec } })), layer: null, preflight: null, removed: null, ...extra };
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
