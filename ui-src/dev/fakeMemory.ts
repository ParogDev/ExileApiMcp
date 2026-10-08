// In-memory stand-in for memory_layout / memory_read / memory_where / watch_memory / show_memory_view, for the
// dev harness. Fixtures in dev/memory/*.json are real PoE1 responses captured live:
//   layout-life.json      580-byte Life component: 16 mapped fields, 27 candidates (vtables, std::vectors, object
//                         pointers, self-pointers before each embedded vital sub-object), 64 bytes past the end
//   layout-stashtab.json  67-byte server stash tab: Flags bits [1,6], Affinity bit [12], inline UTF-16 name "Delv"
//   read-life.json        memory_read of the same Life object (classified 8-byte slots)
//   read-entity.json      the Entity Life.Owner points at (follow the pointer at +8)
//   where-vtable.json     memory_where of a vtable address
//   watch-life.json       an empty watch (the player stood still)
//   watch-stashtab.json   a 60 s watch while the user toggled stash tab affinities: Flags bit 6, Affinity bits 5, 10, 11
// Everything else is synthesised: any other address reads as a plausible object (so every pointer can be followed),
// Life's Health/Mana drift between reads, Life watches report the vitals plus an unmapped bit flip and a noisy timer,
// and the stash tab watch applies its last bytes so a reload after the watch shows them. Errors come back like the
// bridge's: {error: "no_address" | "no_struct" | "unreadable", message}.

import type { CallToolResult } from "@modelcontextprotocol/client";
import type { Candidate, CompareResult, CorrelateResult, Finding, FindingsResult, LayoutField, LayoutResult, PopulationResult, ReadResult, ReadSlot, VerifyResult, WatchResult, WhereResult } from "../src/memory/types";
import type { CallLogEntry } from "./fakeServer";
import layoutLife from "./memory/layout-life.json";
import layoutStash from "./memory/layout-stashtab.json";
import readLife from "./memory/read-life.json";
import readEntity from "./memory/read-entity.json";
import whereVtable from "./memory/where-vtable.json";
import watchLife from "./memory/watch-life.json";
import watchStash from "./memory/watch-stashtab.json";
import populationStash from "./memory/population-stashtabs.json";
import correlateStash from "./memory/correlate-stashtabs.json";
import compareAffinity from "./memory/compare-affinity.json";
import snapshotsList from "./memory/snapshots-list.json";
import findingsFixture from "./memory/findings.json";
import verifyAffinity from "./memory/verify-affinity.json";

export type MemoryScenario = "live" | "offline" | "flaky";

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const LIFE = layoutLife as unknown as LayoutResult;
const STASH = layoutStash as unknown as LayoutResult;
const READ_LIFE = readLife as unknown as ReadResult;
const READ_ENTITY = readEntity as unknown as ReadResult;
const LIFE_PATH = "GameController.Player.GetComponent<Life>()";

export class FakeMemory {
  scenario: MemoryScenario = "live";
  latencyMs = 80;
  /** Life values move between reads. */
  drift = true;
  /** Watches report nothing (the player stood still). */
  quiet = false;
  log: CallLogEntry[] = [];
  onChange?: () => void;
  private vitals = { hp: 3184, mana: 91 };
  /** Stash tab state the watch fixture ends in (Flags 0x42, Affinity 0x0800) once applied. */
  private stash = { flags: 0x42, affinity: 0x1000 };
  /** Saved snapshots: the real affinity series plus whatever the harness user saves. */
  private snapshots: { name: string; savedAt: string }[] = (snapshotsList as { snapshots: { name: string; savedAt: string }[] }).snapshots.slice();

  async handle(name: string, args: Record<string, unknown>): Promise<CallToolResult> {
    const t0 = performance.now();
    const jitter = this.scenario === "flaky" ? Math.random() * 600 : Math.random() * 20;
    const entry: CallLogEntry = { at: Date.now(), name, args, ms: 0, outcome: "ok" };
    try {
      if (name === "watch_memory") await sleep(Math.min(Number(args.durationMs ?? 5000), 1500));
      else await sleep(this.latencyMs + jitter);
      if (this.scenario === "offline" || (this.scenario === "flaky" && Math.random() < 0.3)) {
        throw new Error("The poe1 HUD bridge is not reachable (connection refused on 127.0.0.1:50900). Is the HUD running?");
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

  /** Simulated combat: life and mana move. */
  tick() {
    if (!this.drift) return;
    const v = this.vitals;
    const hit = Math.random() < 0.4;
    this.vitals = { hp: hit ? Math.max(400, v.hp - Math.round(Math.random() * 600)) : Math.min(3184, v.hp + 180), mana: Math.min(1468, Math.max(0, v.mana + (Math.random() < 0.5 ? -24 : 60))) };
    this.onChange?.();
  }
  bump() { this.vitals = { ...this.vitals, hp: Math.max(400, this.vitals.hp - 500) }; this.onChange?.(); }
  toggleAffinity() { this.stash = { flags: this.stash.flags ^ 0x40, affinity: this.stash.affinity ^ 0x0800 }; this.onChange?.(); }

  private dispatch(name: string, a: Record<string, unknown>): CallToolResult {
    switch (name) {
      case "show_memory_view":
        if (a.path == null && a.address == null) return this.layout({ path: LIFE_PATH, extend: 64 });
        if (a.address != null && a.type == null) return this.read({ address: a.address, size: 256 });
        return this.layout({ ...a, extend: 64 });
      case "memory_layout": return this.layout(a);
      case "memory_read": return this.read(a);
      case "memory_where": return this.where(String(a.address ?? ""));
      case "watch_memory": return this.watch(a);
      case "memory_population": return this.population(a);
      case "memory_correlate": return this.correlate(a);
      case "memory_compare": return this.compare(a);
      case "memory_snapshot": return this.snapshot(a);
      case "findings": return this.findings(a);
      case "verify_finding": return this.verify(a);
      default: throw new Error(`Unknown tool: ${name}`);
    }
  }

  // ── memory_layout ──────────────────────────────────────────────────

  private layout(a: Record<string, unknown>): CallToolResult {
    const path = typeof a.path === "string" ? a.path.trim() : undefined;
    const type = typeof a.type === "string" ? a.type.trim() : undefined;
    const extend = Math.min(2048, Math.max(0, Number(a.extend ?? 0)));
    let base: LayoutResult | undefined;
    let address: string | undefined;
    if (a.address != null) {
      const v = addr(a.address);
      if (v === undefined) return err("unreadable", `${String(a.address)} is not an address.`);
      if (v < 0x10000n) return err("unreadable", `0x${v.toString(16).toUpperCase()} is not readable (free).`);
      address = "0x" + v.toString(16).toUpperCase();
      if (!type) return err("no_struct", "Pass type with address: the struct to overlay (a struct with [FieldOffset] fields, see hud_type).");
      if (/Life/i.test(type)) base = LIFE;
      else if (/Stash/i.test(type)) base = STASH;
      else return err("no_struct", `Type '${type}' has no [FieldOffset] fields or was not found (hud_type lists GameOffsets structs).`);
    } else if (path) {
      if (/^GameController(\.Player)?(\.GetComponent<Life>\(\))?$/.test(path) && path !== LIFE_PATH) {
        return err("no_struct", path === "GameController" ? "This object caches no offsets struct; pass type (a struct with [FieldOffset] fields, see hud_type)." : "This object caches no offsets struct; pass type (a struct with [FieldOffset] fields, see hud_type).");
      }
      if (/GetComponent<Life>\(\)\.\w/.test(path)) return err("no_address", `'${path}' (Int32) has no Address: it isn't a memory object.`);
      if (/PlayerStashTabs\[\d+\]$/.test(path) || /Stash/i.test(path)) base = STASH;
      else if (path.includes("GetComponent<Life>()")) base = LIFE;
      else if (/IngameState\.IngameUi/.test(path)) return err("no_struct", "This object caches no offsets struct; pass type (a struct with [FieldOffset] fields, see hud_type).");
      else if (!path.startsWith("GameController")) return err("error", `No root object '${path.split(".")[0]}'. Paths start at GameController.`);
      else return err("error", `No public property or field '${path.split(".").pop()}' on type 'GameController'`);
    } else return err("error", "Pass path (a walker path to a memory object) or address.");
    return json(this.decorate(structuredClone(base), extend, address, type));
  }

  /** Apply extend (slice or pad the bytes, recompute the past-end gap) and the drifting values. */
  private decorate(r: LayoutResult, extend: number, address?: string, type?: string): LayoutResult {
    if (address) r.address = address;
    if (type) { r.struct = type.includes(".") ? type : `GameOffsets.${type}`; r.source = `${r.struct} (overlaid on request)`; }
    const total = r.structSize + extend;
    const bytes = new Uint8Array(total);
    const have = new Uint8Array(total);
    for (const row of r.hex) row.bytes.split(" ").forEach((h, i) => { if (row.off + i < total) { bytes[row.off + i] = parseInt(h, 16); have[row.off + i] = 1; } });
    for (let i = 0; i < total; i++) if (!have[i]) bytes[i] = i % 8 === 7 ? 0 : i % 8 >= 5 ? (i % 3 === 0 ? 0x04 : 0x00) : (hash(`${r.struct}:${i}`) & 0xff);
    if (r.struct.includes("Life")) {
      this.patch(bytes, r.fields, "Health.Current", this.vitals.hp);
      this.patch(bytes, r.fields, "Mana.Current", this.vitals.mana);
    } else {
      this.patch(bytes, r.fields, "Flags", this.stash.flags, 1);
      this.patch(bytes, r.fields, "Affinity", this.stash.affinity);
    }
    r.hex = [];
    for (let off = 0; off < total; off += 16) {
      const n = Math.min(16, total - off);
      const slice = bytes.subarray(off, off + n);
      r.hex.push({ off, bytes: [...slice].map((b) => b.toString(16).toUpperCase().padStart(2, "0")).join(" "), ascii: [...slice].map((b) => (b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : ".")).join("") });
    }
    r.gaps = r.gaps.filter((g) => g.off < r.structSize);
    if (extend > 0) r.gaps.push({ off: r.structSize, size: extend });
    r.candidates = r.candidates.filter((c) => c.off + c.size <= total);
    if (extend > 64 && r.struct.includes("Life")) {
      // Past-end structure the fixture never saw: a vtable and a vector.
      const extra: Candidate[] = [
        { off: 648, size: 8, kind: "vtable", value: "0x7FF785EE5B68", section: ".rdata", rva: "0x35A5B68", ghidra: "0x1435A5B68", firstMethod: "0x1401F3CD0" },
        { off: 672, size: 24, kind: "std::vector", detail: "First=0x41137850C00 Last=0x41137850C30 End=0x41137850C80 (48 bytes used, 128 capacity)", first: "0x41137850C00" },
      ].filter((c) => c.off + c.size <= total);
      r.candidates.push(...extra);
    }
    r.summary = `${r.fields.length} fields, ${r.fields.filter((f) => f.check === "ok").length} ok, ${r.fields.filter((f) => f.check === "suspicious" || f.check === "invalid").length} suspicious/invalid, ${r.gaps.length} unmapped ranges, ${r.candidates.length} unmapped slots that look like structure`;
    return r;
  }

  private patch(bytes: Uint8Array, fields: LayoutField[], name: string, value: number, size?: number) {
    const f = fields.find((x) => x.name === name);
    if (!f) return;
    const n = size ?? f.size;
    for (let i = 0; i < n; i++) bytes[f.off + i] = (value >>> (i * 8)) & 0xff;
    f.value = value;
    f.bytes = [...bytes.subarray(f.off, f.off + f.size)].map((b) => b.toString(16).toUpperCase().padStart(2, "0")).join(" ");
    if (f.bits) { f.bits = []; for (let b = 0; b < f.size * 8; b++) if (value & (1 << b)) f.bits.push(b); }
  }

  // ── memory_read ────────────────────────────────────────────────────

  private read(a: Record<string, unknown>): CallToolResult {
    const size = Math.min(4096, Math.max(8, Number(a.size ?? 256)));
    const offset = Number(a.offset ?? 0);
    let address: bigint | undefined;
    let origin = "address";
    if (a.address != null) {
      address = addr(a.address);
      if (address === undefined) return err("unreadable", `${String(a.address)} is not an address.`);
    } else if (typeof a.path === "string") {
      const p = a.path.trim();
      if (/GetComponent<Life>\(\)\.\w/.test(p)) return err("no_address", `'${p}' (Int32) has no Address; pass a path to a memory object or an address.`);
      address = p.includes("Stash") ? addr(STASH.address)! : addr(LIFE.address)!;
      origin = `${p}.Address`;
    } else return err("error", "Pass path (a walker path to a memory object) or address.");
    address += BigInt(offset);
    if (address < 0x10000n || (address >> 47n) !== 0n) return err("unreadable", `0x${address.toString(16).toUpperCase()} is not readable (free).`);
    const hex = "0x" + address.toString(16).toUpperCase();
    let base: ReadResult;
    if (hex === READ_LIFE.address) base = structuredClone(READ_LIFE);
    else if (hex === READ_ENTITY.address) base = structuredClone(READ_ENTITY);
    else base = this.synthRead(hex, size);
    base.origin = origin;
    const r = resize(base, size);
    if (r.address === READ_LIFE.address) {
      const b = bytesOf(r);
      for (let i = 0; i < 4; i++) { if (424 + i < b.length) b[424 + i] = (this.vitals.hp >>> (i * 8)) & 0xff; if (504 + i < b.length) b[504 + i] = (this.vitals.mana >>> (i * 8)) & 0xff; }
      r.hex = toRows(b);
    }
    return json(r);
  }

  /** A plausible object at any address: vtable first, then a mix of heap pointers, ints, floats, text and zeros. */
  private synthRead(hex: string, size: number): ReadResult {
    const h = hash(hex);
    const slots: ReadSlot[] = [];
    const bytes = new Uint8Array(size);
    const put = (off: number, v: bigint) => { for (let i = 0; i < 8 && off + i < size; i++) bytes[off + i] = Number((v >> BigInt(i * 8)) & 0xffn); };
    for (let off = 0; off + 8 <= size; off += 8) {
      const k = hash(`${hex}:${off}`) % 10;
      if (off === 0 || k === 8) {
        const rva = 0x2f00000 + ((h + off * 37) % 0x600000 & ~0x7);
        const v = 0x7ff782940000n + BigInt(rva);
        put(off, v);
        slots.push({ off, hex: "0x" + v.toString(16).toUpperCase(), kind: "vtable", module: "PathOfExile.exe", section: ".rdata", rva: "0x" + rva.toString(16).toUpperCase(), ghidra: "0x" + (0x140000000 + rva).toString(16).toUpperCase(), firstMethod: "0x" + (0x140100000 + (rva % 0x1000000)).toString(16).toUpperCase() });
      } else if (k <= 3) {
        const v = 0x41000000000n + BigInt((hash(`${hex}:${off}:p`) % 0x3ffffff0) & ~0xf);
        put(off, v);
        const points = k === 1 ? `object (vtable 0x${(0x143000000 + (hash(`${hex}:${off}:v`) % 0x600000)).toString(16).toUpperCase()})` : k === 2 ? "text" : undefined;
        slots.push({ off, hex: "0x" + v.toString(16).toUpperCase(), kind: "heap", ...(points ? { points } : {}), ...(k === 2 ? { text: ["Metadata/Monsters/Rat", "Armour", "pasted__", "Delve"][off % 4] } : {}) });
      } else if (k === 4) {
        const v = hash(`${hex}:${off}:i`) % 5000;
        put(off, BigInt(v));
        const bits: number[] = []; for (let b = 0; b < 32; b++) if (v & (1 << b)) bits.push(b);
        slots.push({ off, hex: "0x" + v.toString(16).toUpperCase(), kind: "int", value: String(v), ...(bits.length <= 4 ? { bits } : {}) });
      } else if (k === 5) {
        const f = new Float32Array([1 + (hash(`${hex}:${off}:f`) % 1000) / 7, 0.5]);
        const u = new Uint32Array(f.buffer);
        put(off, BigInt(u[0]) | (BigInt(u[1]) << 32n));
        slots.push({ off, hex: "0x" + ((BigInt(u[1]) << 32n) | BigInt(u[0])).toString(16).toUpperCase(), kind: "float", value: `${f[0].toFixed(3)}, 0.5` });
      } else if (k === 6 && off > 0) {
        const t = "Hexile\0\0";
        for (let i = 0; i < 8; i++) bytes[off + i] = t.charCodeAt(i);
        slots.push({ off, hex: "0x" + [...bytes.subarray(off, off + 8)].reverse().map((b) => b.toString(16).padStart(2, "0")).join("").toUpperCase(), kind: "text", text: "Hexile" });
      } else {
        slots.push({ off, hex: "0x0", kind: "zero" });
      }
    }
    return { address: hex, size, origin: "address", region: "readable (region details unavailable)", module: READ_LIFE.module, slots, hex: toRows(bytes), game: "poe1" };
  }

  // ── memory_where ───────────────────────────────────────────────────

  private where(address: string): CallToolResult {
    const v = addr(address);
    if (v === undefined || v < 0x10000n) return err("unreadable", `${address} is not readable (free).`);
    const hex = "0x" + v.toString(16).toUpperCase();
    if (v >= 0x7ff782940000n && v < 0x7ff782940000n + 0x4d8f000n) {
      const w = structuredClone(whereVtable) as unknown as WhereResult;
      const rva = Number(v - 0x7ff782940000n);
      return json({ ...w, hex, address: hex, rva: "0x" + rva.toString(16).toUpperCase(), ghidra: "0x" + (0x140000000 + rva).toString(16).toUpperCase(), ...(rva < 0x2000000 ? { kind: "module", section: ".text", firstMethod: undefined } : {}) });
    }
    return json({ hex, address: hex, kind: "heap", region: "readable (region details unavailable)", points: `object (vtable 0x${(0x143000000 + (hash(hex) % 0x600000)).toString(16).toUpperCase()})` });
  }

  // ── watch_memory ───────────────────────────────────────────────────

  private watch(a: Record<string, unknown>): CallToolResult {
    const durationMs = Math.min(60000, Math.max(500, Number(a.durationMs ?? 5000)));
    const samples = Math.round(durationMs / Math.max(50, Number(a.intervalMs ?? 100)));
    const path = typeof a.path === "string" ? a.path : undefined;
    const address = a.address != null ? "0x" + (addr(a.address) ?? 0n).toString(16).toUpperCase() : undefined;
    const isStash = (path && /Stash/i.test(path)) || address === STASH.address;
    const isLife = (path && path.includes("GetComponent<Life>()")) || address === LIFE.address;
    const size = Number(a.size ?? (isStash ? 99 : isLife ? 644 : 256));
    if (this.quiet) return json({ ...(structuredClone(watchLife) as unknown as WatchResult), address: address ?? LIFE.address, size, samples, durationMs, struct: isStash ? STASH.struct : isLife ? LIFE.struct : undefined });
    if (isStash) {
      const w = structuredClone(watchStash) as unknown as WatchResult;
      w.address = STASH.address; w.size = size; w.samples = samples; w.durationMs = durationMs;
      w.changedRanges = w.changedRanges.map((c) => ({ ...c, firstChangeAtMs: Math.min(c.firstChangeAtMs, Math.round(durationMs * 0.34)), lastChangeAtMs: Math.min(c.lastChangeAtMs, Math.round(durationMs * 0.96)) }));
      this.stash = { flags: 0x42, affinity: 0x0800 }; // the watch ends in the fixture's last bytes (Mercenary ticked)
      this.onChange?.();
      return json(w);
    }
    if (isLife) {
      const before = { ...this.vitals };
      this.tick(); this.tick();
      if (this.vitals.hp === before.hp) this.vitals = { ...this.vitals, hp: this.vitals.hp - 120 };
      const le = (v: number, n: number) => Array.from({ length: n }, (_, i) => ((v >>> (i * 8)) & 0xff).toString(16).toUpperCase().padStart(2, "0")).join(" ");
      const ranges: WatchResult["changedRanges"] = [
        { off: 424, size: 2, field: "Health.Current", changes: 7, first: le(before.hp, 2), last: le(this.vitals.hp, 2), bitsFlipped: bitsBetween(before.hp, this.vitals.hp, 16), bitsRelativeTo: "field Health.Current (+424)", firstChangeAtMs: Math.round(durationMs * 0.1), lastChangeAtMs: Math.round(durationMs * 0.9) },
        { off: 300, size: 1, field: "(unmapped)", changes: 1, first: "00", last: "08", bitsFlipped: [3], bitsRelativeTo: "range start (+300)", firstChangeAtMs: Math.round(durationMs * 0.42), lastChangeAtMs: Math.round(durationMs * 0.42) },
        { off: 96, size: 4, changes: Math.round(samples * 0.92), field: "(unmapped)", first: "1A 3F 00 00", last: "E3 52 00 00", firstChangeAtMs: 100, lastChangeAtMs: durationMs - 50, noisy: true },
      ];
      if (this.vitals.mana !== before.mana) ranges.push({ off: 504, size: 2, field: "Mana.Current", changes: 3, first: le(before.mana, 2), last: le(this.vitals.mana, 2), bitsFlipped: bitsBetween(before.mana, this.vitals.mana, 16), bitsRelativeTo: "field Mana.Current (+504)", firstChangeAtMs: Math.round(durationMs * 0.2), lastChangeAtMs: Math.round(durationMs * 0.7) });
      return json({ address: LIFE.address, size, samples, durationMs, struct: LIFE.struct, changedRanges: ranges.filter((c) => c.off + c.size <= size).sort((x, y) => x.off - y.off), note: "Noisy ranges change on nearly every sample (timers, positions)." });
    }
    // A raw read: a couple of ranges without field names.
    const h = hash(address ?? "x");
    return json({ address: address ?? "0x0", size, samples, durationMs, changedRanges: [
      { off: 216 % size, size: 1, changes: 2, first: "00", last: "01", bitsFlipped: [0], bitsRelativeTo: `range start (+${216 % size})`, firstChangeAtMs: 900, lastChangeAtMs: 2100 },
      { off: (h % (size / 8)) * 8, size: 4, changes: Math.round(samples * 0.8), first: "10 27 00 00", last: "F0 28 00 00", firstChangeAtMs: 100, lastChangeAtMs: durationMs - 100, noisy: true },
    ] });
  }

  // ── Probing: population, correlate, snapshots, findings ───────────

  private isStashCollection(path: unknown): boolean {
    return typeof path === "string" && /PlayerStashTabs$/.test(path.trim());
  }

  private population(a: Record<string, unknown>): CallToolResult {
    const path = String(a.path ?? "").trim();
    if (!this.isStashCollection(path)) {
      if (!path.startsWith("GameController")) return err("error", `No root object '${path.split(".")[0]}'. Paths start at GameController.`);
      return err("error", `Only 0 readable items under '${path}'; a population needs a collection of memory objects (the harness only knows PlayerStashTabs).`);
    }
    const labels = Array.isArray(a.labels) ? (a.labels as string[]) : ["Name", "Affinity", "TabType"];
    const limit = Math.max(1, Number(a.limit ?? 200));
    const p = structuredClone(populationStash) as unknown as PopulationResult;
    p.items = p.items.slice(0, limit).map((it) => ({ ...it, labels: Object.fromEntries(labels.map((l) => [l, it.labels?.[l] ?? null])) }));
    // Tab "19" carries the state the experiment ended in; keep it in step with toggleAffinity.
    const t19 = p.items.find((it) => it.labels?.Name === "19");
    if (t19) { const b = t19.hex.split(" "); b[61] = (this.stash.flags & 0x40 ? 0x42 : 0x02).toString(16).toUpperCase().padStart(2, "0"); t19.hex = b.join(" "); }
    p.count = p.items.length;
    if (p.items.length < (populationStash as { count: number }).count) p.truncated = `${(populationStash as { count: number }).count - p.items.length} more items not read (limit ${limit})`;
    return json(p);
  }

  private correlate(a: Record<string, unknown>): CallToolResult {
    const path = String(a.path ?? "").trim();
    if (!this.isStashCollection(path)) return err("error", `Only 0 readable items under '${path}'; a correlation needs a population.`);
    const labels = Array.isArray(a.labels) ? (a.labels as string[]) : [];
    if (!labels.length) return err("error", "Pass at least one label (a known property of each item) to correlate against.");
    const c = structuredClone(correlateStash) as unknown as CorrelateResult;
    c.findings = labels.map((l) => c.findings.find((f) => f.label === l) ?? { label: l, distinctValues: 1, bitsExplained: [], storedAt: [], tooLittleEvidence: `'${l}' is not a property of ServerStashTab in the harness` });
    return json(c);
  }

  private compare(a: Record<string, unknown>): CallToolResult {
    const names = Array.isArray(a.names) ? (a.names as string[]) : [];
    if (!names.length) return json({ snapshots: [...this.snapshots].sort((x, y) => y.savedAt.localeCompare(x.savedAt)) });
    if (names.length < 2) return err("error", "Pass 2 or more snapshot names, in order.");
    for (const n of names) if (!this.snapshots.some((s) => s.name === n)) return err("error", `No snapshot '${n}'. Call memory_compare with no names to list them.`);
    const real = compareAffinity as unknown as CompareResult;
    const steps: CompareResult["steps"] = [];
    for (let i = 1; i < names.length; i++) {
      const from = names[i - 1], to = names[i];
      const direct = real.steps.find((s) => s.from === from && s.to === to);
      if (direct) { steps.push(direct); continue; }
      // Non-adjacent real snapshots: merge the steps between them; anything else: no change.
      const ia = real.snapshots.indexOf(from), ib = real.snapshots.indexOf(to);
      if (ia >= 0 && ib > ia) {
        const merged = new Map<string, CompareResult["steps"][number]["changes"][number]>();
        for (const s of real.steps.slice(ia, ib)) for (const c of s.changes) {
          const m = merged.get(c.item) ?? { item: c.item, bytes: [], labels: {} };
          for (const b of c.bytes) { const prev = m.bytes.find((x) => x.off === b.off); if (prev) { prev.to = b.to; prev.bitsOn = [...new Set([...prev.bitsOn.filter((x) => !b.bitsOff.includes(x)), ...b.bitsOn])]; prev.bitsOff = [...new Set([...prev.bitsOff.filter((x) => !b.bitsOn.includes(x)), ...b.bitsOff])]; } else m.bytes.push({ ...b }); }
          for (const [k, v] of Object.entries(c.labels ?? {})) { const cur = m.labels![k]; m.labels![k] = cur ? `${cur.split(" -> ")[0]} -> ${v.split(" -> ")[1]}` : v; }
          merged.set(c.item, m);
        }
        steps.push({ from, to, changes: [...merged.values()].map((c) => ({ ...c, bytes: c.bytes.filter((b) => b.from !== b.to), labels: Object.keys(c.labels ?? {}).length ? c.labels : undefined })).filter((c) => c.bytes.length || c.labels) });
      } else steps.push({ from, to, changes: [] });
    }
    return json({ snapshots: names, steps });
  }

  private snapshot(a: Record<string, unknown>): CallToolResult {
    const name = String(a.name ?? "").trim();
    if (!/^[\w.-]{1,64}$/.test(name)) return err("error", "name: letters, digits, '-', '_' or '.', up to 64 characters.");
    const path = String(a.path ?? "").trim();
    const coll = this.isStashCollection(path);
    if (!coll && !path.includes("GetComponent<Life>()") && !/Stash/i.test(path)) return err("no_address", `'${path}' has no Address; pass a path to a memory object or a collection.`);
    const savedAt = new Date().toISOString();
    this.snapshots = [...this.snapshots.filter((s) => s.name !== name), { name, savedAt }];
    return json({ saved: name, kind: coll ? "collection" : "object", what: coll ? "71 items" : "580 bytes", path, takenAt: savedAt });
  }

  private findings(a: Record<string, unknown>): CallToolResult {
    const f = structuredClone(findingsFixture) as unknown as FindingsResult;
    const game = a.game === "poe2" ? "poe2" : a.game === "poe1" ? "poe1" : undefined;
    if (game) {
      const toCheck = f.findings.filter((x) => (x.games[game]?.status ?? "unverified") === "unverified" && Object.entries(x.games).some(([g, v]) => g !== game && v?.status === "verified")).map((x) => x.id);
      if (toCheck.length) f.toCheck = toCheck;
    }
    return json(f);
  }

  private verify(a: Record<string, unknown>): CallToolResult {
    const id = String(a.id ?? "");
    const f = ((findingsFixture as unknown as FindingsResult).findings as Finding[]).find((x) => x.id === id);
    if (!f) return err("error", `No finding '${id}'. Call findings to list them.`);
    if (id === "stash.flags.has-affinity") return json(verifyAffinity);
    const game = "poe1";
    const recorded = f.games[game];
    if (f.check.kind === "manual") return json({ id, game, kind: "manual", recorded, howToVerify: f.check.how, next: "Run this experiment (prompt probe_memory describes the method), then record the result in Knowledge/findings.json." });
    const where = recorded?.where ?? (f.check.kind === "eval" ? `${f.check.expressionByGame?.poe1 ?? f.check.expression}: Vector3` : "+61 bit 1");
    const o: VerifyResult = {
      id, game, kind: f.check.kind, recorded, verdict: "pass", where,
      evidence: f.check.kind === "eval" ? `eval_path ${f.check.expressionByGame?.poe1 ?? f.check.expression} -> System.Numerics.Vector3` : `memory_correlate over 71 items: 63 items where ${f.check.expect} vs 8 where not, 0 counterexamples`,
      record: { status: "verified", date: new Date().toISOString().slice(0, 10), where, evidence: "memory_correlate over 71 items, 0 counterexamples" },
      next: "Holds on poe1. Set games.poe1 in Knowledge/findings.json to 'record' (if not already).",
    };
    return json(o);
  }
}

// ── Helpers ───────────────────────────────────────────────────────────

function addr(v: unknown): bigint | undefined {
  try {
    if (typeof v === "number") return BigInt(Math.trunc(v));
    const s = String(v).trim();
    if (/^0x[0-9a-f]+$/i.test(s)) return BigInt(s);
    if (/^\d+$/.test(s)) return BigInt(s);
    if (/^[0-9a-f]+$/i.test(s)) return BigInt("0x" + s);
  } catch { /* fall through */ }
  return undefined;
}

function bitsBetween(a: number, b: number, nbits: number): number[] {
  const x = (a ^ b) >>> 0;
  const out: number[] = [];
  for (let i = 0; i < nbits; i++) if (x & (1 << i)) out.push(i);
  return out.slice(0, 16);
}

function bytesOf(r: ReadResult): Uint8Array {
  const b = new Uint8Array(r.size);
  for (const row of r.hex) row.bytes.split(" ").forEach((h, i) => { if (row.off + i < r.size) b[row.off + i] = parseInt(h, 16); });
  return b;
}

function toRows(bytes: Uint8Array): ReadResult["hex"] {
  const rows: ReadResult["hex"] = [];
  for (let off = 0; off < bytes.length; off += 16) {
    const slice = bytes.subarray(off, Math.min(bytes.length, off + 16));
    rows.push({ off, bytes: [...slice].map((b) => b.toString(16).toUpperCase().padStart(2, "0")).join(" "), ascii: [...slice].map((b) => (b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : ".")).join("") });
  }
  return rows;
}

/** Slice or pad a read result to `size` (padding synthesises zero slots). */
function resize(r: ReadResult, size: number): ReadResult {
  if (size === r.size) return r;
  const b = new Uint8Array(size);
  b.set(bytesOf(r).subarray(0, Math.min(size, r.size)));
  const slots = r.slots.filter((s) => s.off + 8 <= size);
  for (let off = slots.length ? slots[slots.length - 1].off + 8 : 0; off + 8 <= size; off += 8) slots.push({ off, hex: "0x0", kind: "zero" });
  return { ...r, size, slots, hex: toRows(b) };
}

function hash(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; }
  return h >>> 0;
}

function err(code: string, message: string): CallToolResult {
  return json({ error: code, message }, true);
}

function json(obj: object, isError = false): CallToolResult {
  return {
    content: [{ type: "text", text: JSON.stringify(obj) }],
    structuredContent: obj as Record<string, unknown>,
    ...(isError ? { isError: true } : {}),
  };
}
