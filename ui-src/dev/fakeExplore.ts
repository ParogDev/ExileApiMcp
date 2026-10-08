// In-memory stand-in for explore_object / show_data_explorer / watch_object / eval_path, for the dev
// harness. Fixtures in dev/explore/*.json are real PoE2 responses captured live (0 = GameController,
// 1 = Player, 2 = Life, 3 = Life.Health, 4 = Player.Stats, 5 = Player.Buffs, 6 = Buffs[0], 7 = IngameUi,
// 8 = Player.Pos, 9 = Entities, 10 = Entities[2]). Any other path is synthesised from how its parent
// listed it, so every node expands, and a few states are injected so the UI can show them:
//   - Player gets a blocked member (NativeHandle) and a member whose getter throws (Mods)
//   - IngameUi's last members arrive as `skipped` (time budget), loadable one by one
//   - Life / Health values drift between reads (refresh, auto-refresh, watch)
//   - unknown member names fail like the bridge does: "No public property or field 'X' on type 'Y'"

import type { CallToolResult } from "@modelcontextprotocol/client";
import type { ExploreChild, ExploreNode, WatchResult } from "../src/explorer/types";
import { childPath, parentPath, segmentLabel, splitPath } from "../src/explorer/paths";
import type { CallLogEntry } from "./fakeServer";
import f0 from "./explore/0.json";
import f1 from "./explore/1.json";
import f2 from "./explore/2.json";
import f3 from "./explore/3.json";
import f4 from "./explore/4.json";
import f5 from "./explore/5.json";
import f6 from "./explore/6.json";
import f7 from "./explore/7.json";
import f8 from "./explore/8.json";
import f9 from "./explore/9.json";
import f10 from "./explore/10.json";

export type ExploreScenario = "live" | "offline" | "flaky";

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const FIXTURES = [f0, f1, f2, f3, f4, f5, f6, f7, f8, f9, f10] as unknown as ExploreNode[];

export class FakeExplorer {
  scenario: ExploreScenario = "live";
  latencyMs = 60;
  /** Life values move between reads. */
  drift = true;
  log: CallLogEntry[] = [];
  onChange?: () => void;
  private fixtures = new Map<string, ExploreNode>(FIXTURES.map((f) => [f.path!, f]));
  private vitals = { hp: 356, mana: 175, es: 22 };
  private reads = 0;

  async handle(name: string, args: Record<string, unknown>): Promise<CallToolResult> {
    const t0 = performance.now();
    const jitter = this.scenario === "flaky" ? Math.random() * 600 : Math.random() * 20;
    const entry: CallLogEntry = { at: Date.now(), name, args, ms: 0, outcome: "ok" };
    try {
      if (name === "watch_object") await sleep(Math.min(Number(args.durationMs ?? 5000), 1500));
      else await sleep(this.latencyMs + jitter);
      if (this.scenario === "offline" || (this.scenario === "flaky" && Math.random() < 0.3)) {
        throw new Error("The poe2 HUD bridge is not reachable (connection refused on 127.0.0.1:50900). Is the HUD running?");
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

  /** Simulated combat for the harness: life and mana move. */
  tick() {
    if (!this.drift) return;
    const v = this.vitals;
    const hit = Math.random() < 0.4;
    this.vitals = {
      hp: hit ? Math.max(40, v.hp - Math.round(Math.random() * 90)) : Math.min(356, v.hp + 35),
      mana: Math.min(175, Math.max(0, v.mana + (Math.random() < 0.5 ? -24 : 18))),
      es: hit ? 0 : Math.min(22, v.es + 6),
    };
    this.onChange?.();
  }

  bump() { this.vitals = { ...this.vitals, hp: Math.max(40, this.vitals.hp - 60) }; this.onChange?.(); }

  private dispatch(name: string, a: Record<string, unknown>): CallToolResult {
    switch (name) {
      case "show_data_explorer":
        return this.explore(String(a.path ?? "GameController"), 0, 50);
      case "explore_object":
        return this.explore(String(a.path ?? "GameController"), Number(a.offset ?? 0), Math.min(200, Math.max(1, Number(a.limit ?? 50))));
      case "watch_object":
        return json(this.watch(String(a.expression ?? "")));
      case "eval_path": {
        const r = this.explore(String(a.expression ?? ""), 0, 200);
        if (r.isError) return r;
        const value = toValue(r.structuredContent as unknown as ExploreNode);
        return json(value !== null && typeof value === "object" ? value : { value });
      }
      default:
        throw new Error(`Unknown tool: ${name}`);
    }
  }

  private explore(path: string, offset: number, limit: number): CallToolResult {
    this.reads++;
    const node = this.nodeAt(path);
    if ("error" in node) return json(node, true);
    const out: ExploreNode = { ...node, elapsedMs: node.elapsedMs ?? (node.children?.length ?? 0) > 60 ? 9 : Math.round(Math.random() * 3) };
    if (out.children && (out.kind === "list" || out.kind === "dictionary")) {
      const total = out.count ?? out.children.length;
      const all = out.children.length >= total ? out.children : this.synthItems(out, total);
      out.children = all.slice(offset, offset + limit);
      out.page = { offset, limit, total };
    }
    return json(out);
  }

  /** The node at a path: a fixture (with injected states), or one synthesised from its parent's listing. */
  private nodeAt(path: string): ExploreNode | { error: string } {
    const fixture = this.fixtures.get(path);
    if (fixture) return this.decorate(structuredClone(fixture));
    const parent = parentPath(path);
    if (!parent) return { error: `No root object '${path}'. Paths start at GameController.` };
    const parentNode = this.nodeAt(parent);
    if ("error" in parentNode) return parentNode;
    const segs = splitPath(path);
    const last = segs[segs.length - 1];
    // Members a fixture lists but the decorated parent moved into `skipped` are still real: find them in the raw capture.
    const raw = this.fixtures.get(parent)?.children?.find((c) => c.path === path);
    const listed = raw ?? parentNode.children?.find((c) => c.path === path)
      ?? parentNode.components?.find((c) => c.path === path)
      ?? (last.startsWith("[") ? this.synthItems(parentNode, parentNode.count ?? 0).find((c) => c.path === path) : undefined);
    if (!listed) {
      const member = segmentLabel(last);
      return { error: `No public property or field '${member}' on type '${parentNode.type ?? "Object"}'` };
    }
    return this.synthesize(path, listed as ExploreChild, parentNode);
  }

  private decorate(n: ExploreNode): ExploreNode {
    if (n.path === "GameController.Player" && n.children) {
      const injected: ExploreChild[] = [
        { name: "Mods", type: "List<String>", kind: "object", preview: "", path: "GameController.Player.Mods", csharp: "GameController?.Player?.Mods", error: "NullReferenceException: Object reference not set to an instance of an object." },
        { name: "NativeHandle", type: "IntPtr", kind: "blocked", preview: "", path: "GameController.Player.NativeHandle", csharp: "GameController?.Player?.NativeHandle" },
      ];
      n.children = [...n.children, ...injected].sort((a, b) => a.name.localeCompare(b.name));
    }
    if (n.path === "GameController.IngameState.IngameUi" && n.children) {
      const keep = n.children.filter((c) => c.name < "Su" || c.name.startsWith("i"));
      const skipped = n.children.filter((c) => !keep.includes(c)).map((c) => c.name);
      n.children = keep;
      n.skipped = { reason: "time budget (60 ms)", members: skipped };
    }
    if (this.drift && n.children) {
      const v = this.vitals;
      const set = (name: string, value: number | string) => { const c = n.children!.find((k) => k.name === name); if (c) c.preview = String(value); };
      if (n.path === "GameController.Player.GetComponent<Life>()") {
        set("CurHP", v.hp); set("CurMana", v.mana); set("CurES", v.es);
        set("HPPercentage", (v.hp / 356).toFixed(3)); set("MPPercentage", (v.mana / 175).toFixed(3)); set("ESPercentage", (v.es / 22).toFixed(3));
      }
      if (n.path === "GameController.Player.GetComponent<Life>().Health") set("Current", v.hp);
      if (n.path === "GameController") set("DeltaTime", (16 + Math.random() * 2).toFixed(3));
    }
    return n;
  }

  private synthesize(path: string, listed: ExploreChild, parent: ExploreNode): ExploreNode {
    const cs = listed.csharp ?? path;
    const base: ExploreNode = { path, csharp: cs, type: listed.type, kind: listed.kind, preview: listed.preview, count: listed.count, expandable: listed.expandable, namespace: guessNamespace(listed.type) };
    const member = (name: string, type: string, kind: ExploreNode["kind"], preview: string, extra: Partial<ExploreChild> = {}): ExploreChild =>
      ({ name, type, kind, preview, path: childPath(path, name), csharp: `${cs}?.${name}`, ...extra });
    const h = hash(path);
    switch (listed.kind) {
      case "struct": {
        const pairs = [...(listed.preview ?? "").matchAll(/(\w+)=("(?:[^"\\]|\\.)*"|\S+)/g)];
        base.children = pairs.map(([, k, v]) => member(k, v.startsWith('"') ? "String" : v.includes(".") ? "Single" : "Int32", v.startsWith('"') ? "string" : "number", v));
        if (!base.children.length) base.children = [member("Value", "Int64", "number", String(h % 1000))];
        return base;
      }
      case "list": case "dictionary": case "sequence":
        base.children = [];
        return base;
      case "component": {
        const t = listed.name;
        base.type = t; base.kind = "object"; base.preview = t; base.namespace = "ExileCore2.PoEMemory.Components";
        base.children = [
          member("Address", "Int64", "number", String(3622000000000 + (h % 999999))),
          member("Owner", "Entity", "object", parent.preview ?? "Entity", { expandable: true }),
          ...componentMembers(t, member),
        ].sort((a, b) => a.name.localeCompare(b.name));
        return base;
      }
      case "object": {
        const t = listed.type ?? "Object";
        const members = objectMembers(t, h, member, listed);
        // Real captures below a synthesised node stay reachable (GameController.IngameState -> IngameUi fixture).
        for (const f of this.fixtures.values()) {
          if (parentPath(f.path!) === path && !members.some((m) => m.path === f.path)) {
            const { children: _c, components: _k, page: _p, namespace: _n, elapsedMs: _e, ...rest } = f;
            members.push({ ...rest, name: segmentLabel(splitPath(f.path!).pop()!) });
          }
        }
        base.children = members.sort((a, b) => a.name.localeCompare(b.name));
        return base;
      }
      default:
        return base; // scalar: a leaf node, no children
    }
  }

  /** Items of a collection, deterministic per path: [i] for lists, [Key] for dictionaries. */
  private synthItems(n: ExploreNode, total: number): ExploreChild[] {
    const elem = /<(?:[^,>]+,\s*)?([^>]+)>$/.exec(n.type ?? "")?.[1] ?? "Object";
    const out: ExploreChild[] = [];
    const existing = n.children ?? [];
    for (let i = 0; i < total; i++) {
      if (existing[i] && n.kind === "list") { out.push(existing[i]); continue; }
      if (n.kind === "dictionary") {
        const key = existing[i]?.name.slice(1, -1) ?? `${elem === "QuestState" ? "Quest" : "Key"}${i}`;
        const stringKey = (n.type ?? "").startsWith("Dictionary<String");
        out.push(existing[i] ?? {
          name: `[${key}]`, type: elem, kind: elem === "Int32" ? "number" : elem === "String" ? "string" : "object",
          preview: elem === "Int32" ? String((hash(key) % 2000) - 500) : elem === "String" ? `"${key}"` : `${elem} Id="${key}"`,
          path: `${n.path}["${key}"]`, csharp: stringKey ? `${n.csharp}?["${key}"]` : `${n.csharp}?[${/^Dictionary<(\w+)/.exec(n.type ?? "")?.[1] ?? "Key"}.${key}]`,
          expandable: elem !== "Int32" && elem !== "String" ? true : undefined,
        });
        continue;
      }
      const h = hash(`${n.path}[${i}]`);
      const strings = elem === "String";
      out.push({
        name: `[${i}]`, type: elem, kind: strings ? "string" : elem === "Int32" ? "number" : "object",
        preview: strings ? `"${SAMPLE_CHAT[h % SAMPLE_CHAT.length]}"` : elem === "Entity" ? `Entity Path="${SAMPLE_META[h % SAMPLE_META.length]}"` : elem === "LabelOnGround" ? `LabelOnGround ${h % 3 ? "visible" : "hidden"}` : `${elem} ${h % 4 ? "hidden" : "visible"}`,
        path: `${n.path}[${i}]`, csharp: `${n.csharp}?[${i}]`, expandable: strings || elem === "Int32" ? undefined : true,
      });
    }
    return out;
  }

  private watch(expression: string): WatchResult {
    const under = (p: string) => expression === p || expression.startsWith(p + ".") || expression.startsWith(p + "[");
    const life = "GameController.Player.GetComponent<Life>()";
    const before = { ...this.vitals };
    this.tick(); this.tick();
    const after = this.vitals;
    const rel = (full: string) => (full === expression ? "" : full.slice(expression.length + 1));
    const changes: WatchResult["changes"] = [];
    if (under(life) || expression === "GameController.Player" || expression === "GameController") {
      const prefix = expression === "GameController" ? "Player.GetComponent<Life>()." : expression === "GameController.Player" ? "GetComponent<Life>()." : "";
      const add = (leaf: string, first: unknown, last: unknown, n: number, noisy = false) => {
        const full = `${life}.${leaf}`;
        if (!under(expression) || full.startsWith(expression)) changes.push({ path: expression.startsWith(life) ? rel(full) : prefix + leaf, changes: n, first, last, firstChangeAtMs: 240, lastChangeAtMs: 4760, noisy: noisy || undefined });
      };
      if (before.hp !== after.hp) { add("CurHP", before.hp, after.hp, 7); add("Health.Current", before.hp, after.hp, 7); add("HPPercentage", +(before.hp / 356).toFixed(3), +(after.hp / 356).toFixed(3), 7); }
      if (before.mana !== after.mana) add("CurMana", before.mana, after.mana, 3);
      if (before.es !== after.es) add("CurES", before.es, after.es, 2);
    }
    if (expression === "GameController") changes.push({ path: "DeltaTime", changes: 19, first: 16.2, last: 17.1, firstChangeAtMs: 250, lastChangeAtMs: 5000, noisy: true });
    if (expression.startsWith("GameController.Player.Pos")) changes.push({ path: expression.endsWith("Pos") ? "X" : "", changes: 12, first: 5662.3, last: 5701.9, firstChangeAtMs: 500, lastChangeAtMs: 4900 });
    return { samples: 20, changes: changes.filter((c) => c.path !== undefined), note: changes.some((c) => c.noisy) ? "Noisy fields change on nearly every sample (timers, positions)." : undefined };
  }
}

// ── Synthesis helpers ─────────────────────────────────────────────

type Member = (name: string, type: string, kind: ExploreNode["kind"], preview: string, extra?: Partial<ExploreChild>) => ExploreChild;

function componentMembers(t: string, m: Member): ExploreChild[] {
  switch (t) {
    case "Actor": return [m("Action", "ActionFlags", "enum", "None"), m("ActorSkills", "List<ActorSkill>", "list", "Count = 9", { count: 9, expandable: true }), m("AnimationId", "Int32", "number", "0"), m("CurrentAction", "ActionWrapper", "null", "null"), m("DeployedObjects", "List<DeployedObject>", "list", "Count = 0", { count: 0 }), m("isMoving", "Boolean", "bool", "false")];
    case "Positioned": return [m("GridPos", "Vector2i", "struct", "X=1087 Y=943", { expandable: true }), m("GridPosition", "Vector2", "struct", "X=1087.4 Y=943.1", { expandable: true }), m("Reaction", "Int32", "number", "0"), m("Rotation", "Single", "number", "2.071"), m("Scale", "Single", "number", "1"), m("Size", "Int32", "number", "3"), m("WorldPosition", "Vector3", "struct", "X=5662.3 Y=4906.9 Z=0", { expandable: true })];
    case "Render": return [m("Bounds", "Vector3", "struct", "X=20 Y=20 Z=82", { expandable: true }), m("Height", "Single", "number", "82"), m("Name", "String", "string", '"Mercenary"'), m("Pos", "Vector3", "struct", "X=5662.3 Y=4906.9 Z=0", { expandable: true }), m("RotationRadians", "Single", "number", "2.071"), m("TerrainHeight", "Single", "number", "0")];
    case "Buffs": return [m("BuffsList", "List<Buff>", "list", "Count = 2", { count: 2, expandable: true })];
    case "Stats": return [m("StatDictionary", "Dictionary<GameStat, Int32>", "dictionary", "Count = 271", { count: 271, expandable: true })];
    case "Player": return [m("AllocatedPassives", "List<Passive>", "list", "Count = 41", { count: 41, expandable: true, slowMs: 12 }), m("Level", "UInt32", "number", "16"), m("PlayerName", "String", "string", '"Hexile"'), m("XP", "UInt32", "number", "92711")];
    case "Pathfinding": return [m("IsMoving", "Boolean", "bool", "false"), m("PathingNodes", "List<Vector2i>", "list", "Count = 0", { count: 0 }), m("TargetMovePos", "Vector2i", "struct", "X=0 Y=0", { expandable: true })];
    case "Targetable": return [m("isTargetable", "Boolean", "bool", "true"), m("isTargeted", "Boolean", "bool", "false")];
    case "MinimapIcon": return [m("Name", "String", "string", '"Waypoint"'), m("Settings", "MinimapIconSettings", "null", "null")];
    default: return [m("IsValid", "Boolean", "bool", "true"), m("Name", "String", "string", `"${t}"`)];
  }
}

function objectMembers(t: string, h: number, m: Member, listed: ExploreChild): ExploreChild[] {
  if (t === "Entity") {
    return [m("Address", "Int64", "number", String(3622000000000 + (h % 999999))), m("Id", "UInt32", "number", String(1000 + (h % 9000))),
      m("Path", "String", "string", listed.preview?.replace(/^Entity Path=/, "") ?? '"Metadata/Monsters/..."'), m("RenderName", "String", "string", '"Monster"'),
      m("Pos", "Vector3", "struct", `X=${5000 + (h % 900)}.2 Y=${4000 + (h % 700)}.7 Z=0`, { expandable: true }), m("DistancePlayer", "Single", "number", String((h % 120) + 4)),
      m("IsAlive", "Boolean", "bool", h % 3 ? "true" : "false"), m("IsHostile", "Boolean", "bool", h % 2 ? "true" : "false"), m("Rarity", "MonsterRarity", "enum", ["White", "Magic", "Rare", "Unique"][h % 4]),
      m("Type", "EntityType", "enum", ["Monster", "Chest", "Npc", "WorldItem"][h % 4]), m("Buffs", "List<Buff>", "list", "Count = 1", { count: 1, expandable: true }), m("Stats", "Dictionary<GameStat, Int32>", "dictionary", "Count = 14", { count: 14, expandable: true })];
  }
  if (t === "Element" || /Panel|Window|Element|Dialog|Ui$|Menu|Button|Grid|Bar$|Tooltip|Popup/i.test(t)) {
    const visible = listed.preview?.endsWith(" visible");
    return [m("Address", "Int64", "number", String(3622000000000 + (h % 999999))), m("Children", "List<Element>", "list", `Count = ${h % 9}`, { count: h % 9, expandable: h % 9 ? true : undefined }),
      m("ChildCount", "Int64", "number", String(h % 9)), m("GetClientRect()", "RectangleF", "struct", `X=${h % 1900} Y=${h % 1000} Width=${100 + (h % 400)} Height=${40 + (h % 200)}`, { expandable: true }),
      m("Height", "Single", "number", String(40 + (h % 200))), m("Width", "Single", "number", String(100 + (h % 400))),
      m("IsVisible", "Boolean", "bool", visible ? "true" : "false"), m("IsVisibleLocal", "Boolean", "bool", visible ? "true" : "false"), m("IsValid", "Boolean", "bool", "true"),
      m("Parent", "Element", "object", "Element visible", { expandable: true }), m("PathFromRoot", "String", "string", `"1.${h % 40}.${h % 7}"`),
      m("Text", "String", h % 2 ? "string" : "null", h % 2 ? `"${t.replace(/([a-z])([A-Z])/g, "$1 $2")}"` : "null"), m("Tooltip", "Element", "null", "null"),
      m("Type", "ElementType", "enum", String(h % 30)), m("Scale", "Single", "number", "1")];
  }
  if (t === "BuffDefinition") {
    return [m("Id", "String", "string", listed.preview?.replace(/^BuffDefinition Id=/, "") ?? '"buff"'), m("Name", "String", "string", '"Crossbow aim"'), m("IsBuff", "Boolean", "bool", "true"), m("IsNetBuff", "Boolean", "bool", "false"), m("Visible", "Boolean", "bool", "false")];
  }
  if (t === "AreaController") {
    return [m("CurrentArea", "AreaInstance", "object", 'AreaInstance Name="Clearfell Encampment"', { expandable: true }), m("CurrentArea.Name", "String", "string", '"Clearfell Encampment"'), m("ActIndex", "Int32", "number", "1"), m("AreaLevel", "Int32", "number", "16"), m("IsHideout", "Boolean", "bool", "false"), m("IsTown", "Boolean", "bool", "true"), m("RealLevel", "Int32", "number", "16")];
  }
  // Generic object: a handful of plausible members, one of them slow.
  return [m("Address", "Int64", "number", String(3622000000000 + (h % 999999))), m("IsValid", "Boolean", "bool", "true"), m("Name", "String", "string", `"${t}"`),
    m("Parent", "Object", "null", "null"), m("Items", `List<${t}Item>`, "list", `Count = ${h % 6}`, { count: h % 6, expandable: h % 6 ? true : undefined, slowMs: 7 }), m("Flags", `${t}Flags`, "enum", String(h % 16))];
}

const SAMPLE_CHAT = ["Welcome to Path of Exile 2.", ": Trade chat is disabled in this area.", "Hexile: anyone selling a +2 crossbow?", ": You have entered Clearfell Encampment.", "Finn: Good to see you again, exile.", ": Your flasks are refilled."];
const SAMPLE_META = ["Metadata/Monsters/Zombies/ZombieRanged", "Metadata/MiscellaneousObjects/DoodadNoBlocking", "Metadata/Chests/Chest1", "Metadata/NPC/Act1/Finn", "Metadata/Monsters/Rats/Rat1", "Metadata/MiscellaneousObjects/Waypoint"];

function guessNamespace(type?: string): string | undefined {
  if (!type) return undefined;
  if (/^(List|Dictionary|ReadOnlyCollection)</.test(type)) return "System.Collections.Generic";
  if (/^Vector\d/.test(type)) return "System.Numerics";
  if (/Struct$|^Rectangle/.test(type)) return "GameOffsets2";
  if (/Element|Panel|Window|Dialog|Ui$|Entity|AreaInstance|AreaController/.test(type)) return "ExileCore2.PoEMemory.MemoryObjects";
  return "ExileCore2.PoEMemory.Components";
}

function hash(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; }
  return h >>> 0;
}

/** eval_path-like JSON for a node: scalars parsed, objects as {member: preview}. */
function toValue(n: ExploreNode): unknown {
  const scalar = (c: ExploreNode): unknown => {
    const p = c.preview ?? "";
    switch (c.kind) {
      case "null": return null;
      case "bool": return p === "true";
      case "number": return Number(p);
      case "string": return p.replace(/^"(.*)"$/, "$1");
      default: return p;
    }
  };
  if (!n.children) return scalar(n);
  const o: Record<string, unknown> = {};
  for (const c of n.children) o[c.name] = c.children ? toValue(c) : scalar(c);
  return n.kind === "list" ? Object.values(o) : o;
}

function json(obj: object, isError = false): CallToolResult {
  return {
    content: [{ type: "text", text: JSON.stringify(obj) }],
    structuredContent: obj as Record<string, unknown>,
    ...(isError ? { isError: true } : {}),
  };
}
