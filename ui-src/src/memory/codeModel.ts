// find_field_access results, read for a human: functions ranked, instructions grouped, decompiled excerpts
// tokenised so struct offsets can be named and the unmapped ones flagged, and the field-gating a serializer
// reveals ("flags & 0x40 gates the 4-byte read at +0x3f") inferred by pattern. Everything here is heuristic
// text analysis of Ghidra's pseudocode; the UI labels it as such.

import type { Access, AccessFunction, AccessKind, Confidence, Decompiled, FieldAccessResult, MemoryError } from "./types";

// ── Queries kept per offset for the session ──────────────────────────

export type CodeErrorKind = "ghidra" | "offline" | "offset0" | "anchors" | "snapshot" | "error";

export interface CodeQuery {
  /** `${structKey}|${offset}|${bit ?? "-"}` */
  key: string;
  structKey: string;
  offset: number;
  bit?: number;
  /** The arguments sent (path or knownOffsets, minKnown, decompile). */
  args: Record<string, unknown>;
  status: "running" | "done" | "error" | "cancelled";
  startedAt: number;
  finishedAt?: number;
  /** Earlier query for the same offset existed: the server answers from its cache. */
  expectCached: boolean;
  result?: FieldAccessResult;
  error?: { kind: CodeErrorKind; message: string };
}

export function codeKey(structKey: string, offset: number, bit?: number): string {
  return `${structKey}|${offset}|${bit ?? "-"}`;
}

export function asFieldAccess(data: unknown): FieldAccessResult | undefined {
  if (!data || typeof data !== "object") return undefined;
  const d = data as Record<string, unknown>;
  return d.target && typeof d.target === "object" && Array.isArray(d.accesses) && Array.isArray(d.functions) ? (d as unknown as FieldAccessResult) : undefined;
}

export function classifyCodeError(e: MemoryError): CodeErrorKind {
  const m = `${e.error} ${e.message ?? ""}`;
  if (e.error === "offline") return "offline";
  if (/ghidra isn't running|ghidra-headless/i.test(m)) return "ghidra";
  if (/offset 0 can't be searched/i.test(m)) return "offset0";
  if (/need at least \d+ known offsets/i.test(m)) return "anchors";
  if (/no ghidra snapshot|patched since|couldn't load/i.test(m)) return "snapshot";
  return "error";
}

/** The command to start Ghidra, lifted from the error text when present. */
export function ghidraCommand(message?: string): string {
  const m = /(powershell[^()]*ghidra-headless\.ps1)/i.exec(message ?? "");
  return m ? m[1].trim() : "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\ghidra-headless.ps1";
}

// ── Functions, ranked ────────────────────────────────────────────────

export const KIND_ORDER: AccessKind[] = ["bit-test", "set-bits", "clear-bits", "write", "read", "address-of"];
const CONF_RANK: Record<Confidence, number> = { high: 0, medium: 1, low: 2 };

export interface FnView {
  fn: AccessFunction;
  accesses: Access[];
  confidence: Confidence;
  kinds: AccessKind[];
  /** Known fields the function also touches, "+63 Affinity", union over its accesses, by offset. */
  also: string[];
  /** Decompiled excerpt, when the server returned one. */
  dec?: Decompiled;
  /** What the function looks like, from its pseudocode: "network serializer", "copies the struct"… */
  role?: string;
  /** Bits its masks touch at the target. */
  bits: number[];
}

/** Functions ranked: those testing the selected bit first, then by how many known fields they touch, then by accesses. */
export function rankFunctions(r: FieldAccessResult, bit: number | undefined): FnView[] {
  const byFn = new Map<string, Access[]>();
  for (const a of r.accesses) (byFn.get(a.function) ?? byFn.set(a.function, []).get(a.function)!).push(a);
  const decs = new Map(r.decompiled.map((d) => [d.function, d]));
  const views: FnView[] = r.functions.map((fn) => {
    const accesses = [...(byFn.get(fn.function) ?? [])].sort((a, b) => Number(!!b.matchesBit) - Number(!!a.matchesBit) || a.address.localeCompare(b.address));
    const confidence = accesses.reduce<Confidence>((best, a) => (CONF_RANK[a.confidence] < CONF_RANK[best] ? a.confidence : best), "low");
    const kinds = KIND_ORDER.filter((k) => fn.kinds.split(",").includes(k) || accesses.some((a) => a.kind === k));
    const also = [...new Set(accesses.flatMap((a) => a.knownFieldsAlsoAccessed))].sort((a, b) => offOf(a) - offOf(b));
    const dec = decs.get(fn.function);
    const bits = [...new Set(accesses.flatMap((a) => a.bits ?? []))].sort((a, b) => a - b);
    return { fn, accesses, confidence, kinds, also, dec, role: roleOf(dec, accesses), bits };
  });
  views.sort((a, b) =>
    (bit !== undefined ? Number(b.fn.bitMatch) - Number(a.fn.bitMatch) : 0)
    || Number(!!b.dec) - Number(!!a.dec)
    || b.fn.knownFields - a.fn.knownFields
    || CONF_RANK[a.confidence] - CONF_RANK[b.confidence]
    || b.fn.accesses - a.fn.accesses
    || a.fn.function.localeCompare(b.fn.function));
  return views;
}

function offOf(known: string): number {
  const m = /^\+(\d+)/.exec(known);
  return m ? Number(m[1]) : 0;
}

/** "+63 Affinity" -> { off: 63, name: "Affinity" }. */
export function parseKnown(known: string): { off: number; name: string } {
  const m = /^\+(\d+)\s*(.*)$/.exec(known);
  return m ? { off: Number(m[1]), name: m[2] } : { off: 0, name: known };
}

/** A short guess at what a function is, from its pseudocode and instruction mix. Labelled "looks like" in the UI. */
export function roleOf(dec: Decompiled | undefined, accesses: Access[]): string | undefined {
  const code = dec?.excerpt ?? "";
  if (/\bhtons\(/.test(code)) return "network serializer";
  if (/\bntohs\(/.test(code)) return "network deserializer";
  if (/L"</.test(code)) return "UI text builder";
  const kinds = new Set(accesses.map((a) => a.kind));
  const bases = new Set(accesses.map((a) => a.base));
  if (kinds.has("read") && kinds.has("write") && bases.size >= 2 && accesses.length >= 2) return "copies the struct";
  if (kinds.has("bit-test") && kinds.size === 1) return "tests bits";
  if (kinds.has("set-bits") || kinds.has("clear-bits")) return "sets or clears bits";
  if (kinds.has("write") && !kinds.has("read")) return "writes it";
  if (kinds.has("read") && !kinds.has("write")) return "reads it";
  return undefined;
}

/** "looks like a network serializer" vs "copies the struct": nouns take an article, verb phrases don't. */
export function roleLabel(role: string): { prefix: string; role: string } {
  return /serializer|builder/.test(role) ? { prefix: "looks like a ", role } : { prefix: "", role };
}

/** Access widths at the target (address-of excluded) -> count, widest first. */
export function widthsAtTarget(accesses: Access[]): { width: number; count: number }[] {
  const m = new Map<number, number>();
  for (const a of accesses) if (a.width > 0) m.set(a.width, (m.get(a.width) ?? 0) + 1);
  return [...m].map(([width, count]) => ({ width, count })).sort((a, b) => b.count - a.count || b.width - a.width);
}

// ── Pseudocode excerpts, tokenised ───────────────────────────────────

export type Token =
  | { t: "text"; s: string }
  /** A struct offset through one of the base variables; width in bytes when a cast or helper size says so. */
  | { t: "off"; s: string; off: number; width?: number; target: boolean; base: string }
  /** A mask applied to the target: the bits it names. */
  | { t: "mask"; s: string; bits: number[] }
  | { t: "fn"; s: string; name: string }
  /** A reference through a variable that isn't the struct (another object, a local). */
  | { t: "other"; s: string };

export interface ExcerptLine {
  tokens: Token[];
  /** "  ..." separator between runs. */
  gap: boolean;
  /** Line has a reference to the target offset. */
  hasTarget: boolean;
}

export interface OffsetRef {
  off: number;
  width?: number;
  /** read | write | io (passed to a helper by address) | zeroed */
  kind: "read" | "write" | "io" | "zeroed";
  line: number;
}

/** "bit 6 set gates a 4-byte read at +0x3f": a condition on the target's bits and what happens inside it. */
export interface Gate {
  bits: number[];
  /** The code runs when the bits are set ("!= 0") or clear ("== 0"). */
  when: "set" | "clear";
  off: number;
  width?: number;
  kind: OffsetRef["kind"];
  fn: string;
}

export interface ExcerptModel {
  lines: ExcerptLine[];
  /** Variables the target offset is read through: the struct's base pointer(s) in this function. */
  bases: string[];
  refs: OffsetRef[];
  gates: Gate[];
}

const WIDTH_OF: Record<string, number> = {
  byte: 1, undefined1: 1, char: 1, bool: 1, uchar: 1, undefined2: 2, short: 2, ushort: 2, u_short: 2, word: 2,
  undefined4: 4, int: 4, uint: 4, float: 4, dword: 4, undefined8: 8, longlong: 8, ulonglong: 8, double: 8, qword: 8,
};

/** Pointer stride per variable from the signature: "undefined8 *param_2" -> 8, "longlong param_1" -> 1 (an integer address). */
function scalesFrom(signature: string | undefined): Map<string, number> {
  const m = new Map<string, number>();
  const params = /\((.*)\)/.exec(signature ?? "")?.[1] ?? "";
  for (const p of params.split(",")) {
    const mm = /([\w]+)\s*(\**)\s*(\w+)\s*$/.exec(p.trim());
    if (!mm) continue;
    const [, type, stars, name] = mm;
    if (stars.length === 1) m.set(name, WIDTH_OF[type] ?? 0);
    else if (stars.length === 0 && (type === "longlong" || type === "ulonglong" || type === "undefined8")) m.set(name, 1);
  }
  return m;
}

const num = (s: string) => (s.toLowerCase().startsWith("0x") ? parseInt(s, 16) : Number(s));
const bitsOfMask = (mask: number): number[] => { const b: number[] = []; for (let i = 0; i < 32; i++) if ((mask >>> i) & 1) b.push(i); return b; };

/**
 * One regex pass per line. Order matters: casts before bare arithmetic.
 *  1  *(TYPE *)((longlong)VAR + OFF)      byte offset through a cast
 *  2  *(TYPE *)(VAR + OFF)                scaled (pointer arithmetic) or byte offset (integer base)
 *  3  (longlong)VAR + OFF                 byte offset, no cast (a helper argument)
 *  4  *VAR                                deref of the base: offset 0
 *  5  FUN_xxxxxxxx                        a function
 *  6  VAR + OFF                           bare arithmetic (helper argument on an integer base)
 *  7  VAR, N)                              the base itself handed to a helper with a size: offset 0
 */
const TOKEN_RE = /\*\((\w+) \*\)\(\(longlong\)(\w+) \+ (0x[0-9a-f]+|\d+)\)|\*\((\w+) \*\)\((\w+) \+ (0x[0-9a-f]+|\d+)\)|\(longlong\)(\w+) \+ (0x[0-9a-f]+|\d+)|\*(param_\d+|p\w*Var\d+)(?![\w(])|(FUN_[0-9a-fA-F]+)|\b(param_\d+|p\w*Var\d+) \+ (0x[0-9a-f]+|\d+)|\b(param_\d+|p\w*Var\d+)(?=\s*,\s*\d+\s*\))/g;

export function parseExcerpt(dec: Decompiled, targetOff: number): ExcerptModel {
  const raw = (dec.excerpt ?? "").replace(/\r/g, "").split("\n");
  const scales = scalesFrom(dec.signature);
  // Pass 1: which variables carry the target offset.
  const bases = new Set<string>();
  for (const line of raw) {
    for (const m of line.matchAll(TOKEN_RE)) {
      const [, , v1, o1, , v2, o2, v3, o3, , , v6, o6] = m;
      if (v1 && num(o1) === targetOff) bases.add(v1);
      else if (v3 && num(o3) === targetOff) bases.add(v3);
      else if (v2 && byteOff(v2, o2, scales) === targetOff) bases.add(v2);
      else if (v6 && byteOff(v6, o6, scales) === targetOff) bases.add(v6);
    }
  }
  // Pass 2: tokens, refs, gates.
  const lines: ExcerptLine[] = [];
  const refs: OffsetRef[] = [];
  const gates: Gate[] = [];
  let gate: { bits: number[]; when: "set" | "clear"; depth: number } | undefined;
  let depth = 0;
  raw.forEach((line, li) => {
    if (line.trim() === "...") { lines.push({ tokens: [{ t: "text", s: line }], gap: true, hasTarget: false }); gate = undefined; return; }
    const tokens: Token[] = [];
    let last = 0, hasTarget = false, lineBits: number[] | undefined;
    const lineRefs: OffsetRef[] = [];
    for (const m of line.matchAll(TOKEN_RE)) {
      const s = m[0], at = m.index!;
      if (at > last) tokens.push({ t: "text", s: line.slice(last, at) });
      last = at + s.length;
      const [, c1, v1, o1, c2, v2, o2, v3, o3, v4, fn, v6, o6, v7] = m;
      let ref: { base: string; off: number; width?: number } | undefined;
      if (v1) ref = { base: v1, off: num(o1), width: WIDTH_OF[c1] };
      else if (v2) { const off = byteOff(v2, o2, scales); if (off !== undefined) ref = { base: v2, off, width: WIDTH_OF[c2] }; }
      else if (v3) ref = { base: v3, off: num(o3), width: helperSize(line, last) };
      else if (v4) { const sc = scales.get(v4); if (sc && sc > 1) ref = { base: v4, off: 0, width: sc }; }
      else if (v6) { const off = byteOff(v6, o6, scales); if (off !== undefined) ref = { base: v6, off, width: helperSize(line, last) }; }
      else if (v7) ref = { base: v7, off: 0, width: helperSize(line, last) };
      if (fn) { tokens.push({ t: "fn", s, name: fn }); continue; }
      if (!ref || !bases.has(ref.base)) { tokens.push({ t: "other", s }); continue; }
      const target = ref.off === targetOff;
      if (target) hasTarget = true;
      tokens.push({ t: "off", s, off: ref.off, width: ref.width, target, base: ref.base });
      // A mask right after the target: "& 0x40)".
      if (target) {
        const mm = /^\s*&\s*(0x[0-9a-f]+|\d+)\)/i.exec(line.slice(last));
        if (mm) { lineBits = bitsOfMask(num(mm[1])); tokens.push({ t: "text", s: line.slice(last, last + mm.index + mm[0].indexOf(mm[1])) }); tokens.push({ t: "mask", s: mm[1], bits: lineBits }); last += mm.index + mm[0].indexOf(mm[1]) + mm[1].length; }
      } else {
        lineRefs.push({ off: ref.off, width: ref.width, kind: refKind(line, at, last), line: li });
      }
    }
    if (last < line.length) tokens.push({ t: "text", s: line.slice(last) });
    refs.push(...lineRefs);
    // Gates: "if ((... target & MASK) != 0) {" opens a block; refs inside it are gated by those bits.
    const open = /^\s*(?:\}\s*else\s*)?if\s*\(/.test(line) && lineBits && /\{\s*$/.test(line);
    if (open) gate = { bits: lineBits!, when: /==\s*0\)/.test(line) ? "clear" : "set", depth };
    else if (gate) for (const r of lineRefs) gates.push({ bits: gate.bits, when: gate.when, off: r.off, width: r.width, kind: r.kind, fn: dec.function });
    for (const ch of line) { if (ch === "{") depth++; else if (ch === "}") { depth--; if (gate && depth <= gate.depth) gate = undefined; } }
    lines.push({ tokens, gap: false, hasTarget });
  });
  return { lines, bases: [...bases], refs, gates: dedupeGates(gates) };
}

function byteOff(v: string, o: string, scales: Map<string, number>): number | undefined {
  const sc = scales.get(v);
  if (sc === undefined || sc === 0) return undefined;
  return num(o) * sc;
}

/** "(longlong)param_2 + 0x3f,4)" -> 4: the size a serializer helper is told to read or write. */
function helperSize(line: string, from: number): number | undefined {
  const m = /^\s*,\s*(\d+)\s*\)/.exec(line.slice(from));
  return m ? Number(m[1]) : undefined;
}

function refKind(line: string, at: number, end: number): OffsetRef["kind"] {
  const before = line.slice(0, at), after = line.slice(end);
  if (/^\s*=\s*0\s*;/.test(after) && /^\s*$/.test(before)) return "zeroed";
  if (/^\s*=[^=]/.test(after) && /^\s*$/.test(before)) return "write";
  if (/FUN_[0-9a-fA-F]+\([^)]*$/.test(before)) return "io";
  return "read";
}

function dedupeGates(gates: Gate[]): Gate[] {
  const out = new Map<string, Gate>();
  for (const g of gates) {
    const k = `${g.bits.join(",")}|${g.off}|${g.fn}`;
    const cur = out.get(k);
    if (!cur) out.set(k, { ...g });
    else {
      if ((g.width ?? 0) > (cur.width ?? 0)) cur.width = g.width;
      // A write or io inside the gate says more than a read; "zeroed" on the clear branch is its own story.
      if (g.kind === "io" || (g.kind === "write" && cur.kind === "read")) cur.kind = g.kind;
      if (g.when === "set") cur.when = "set";
    }
  }
  return [...out.values()].sort((a, b) => a.bits[0] - b.bits[0] || a.off - b.off);
}

/** Gates merged over all excerpts of a result: one line per (bits, offset). */
export function gatesOf(r: FieldAccessResult): (Gate & { fns: string[] })[] {
  const out = new Map<string, Gate & { fns: string[] }>();
  for (const d of r.decompiled) {
    if (!d.excerpt) continue;
    for (const g of parseExcerpt(d, r.target.offset).gates) {
      const k = `${g.bits.join(",")}|${g.off}`;
      const cur = out.get(k);
      if (!cur) out.set(k, { ...g, fns: [g.fn] });
      else {
        if (!cur.fns.includes(g.fn)) cur.fns.push(g.fn);
        if ((g.width ?? 0) > (cur.width ?? 0)) cur.width = g.width;
        if (g.kind === "io" || (g.kind === "write" && cur.kind === "read")) cur.kind = g.kind;
      }
    }
  }
  return [...out.values()].sort((a, b) => a.bits[0] - b.bits[0] || a.off - b.off);
}

// ── Marks on the struct map: offsets the code uses ───────────────────

export interface CodeMark {
  off: number;
  /** Widths seen (bytes), from casts, helper sizes and instruction widths. */
  widths: number[];
  fns: string[];
  /** Bits of another field that gate this access, "+61 bit 6". */
  gatedBy: { fromOff: number; bits: number[]; when: "set" | "clear" }[];
  /** The lookups (target offsets) this came from. */
  fromTargets: number[];
  /** This offset was itself looked up. */
  isTarget: boolean;
}

export type CodeMarks = Map<number, CodeMark>;

/** Every offset the session's lookups for this struct saw code use, with widths and gates. */
export function codeMarks(queries: Iterable<CodeQuery>, structKey: string): CodeMarks {
  const marks: CodeMarks = new Map();
  const get = (off: number) => marks.get(off) ?? marks.set(off, { off, widths: [], fns: [], gatedBy: [], fromTargets: [], isTarget: false }).get(off)!;
  const add = (off: number, width: number | undefined, fn: string, from: number) => {
    const m = get(off);
    if (width && !m.widths.includes(width)) m.widths.push(width);
    if (fn !== "?" && !m.fns.includes(fn)) m.fns.push(fn);
    if (!m.fromTargets.includes(from)) m.fromTargets.push(from);
  };
  for (const q of queries) {
    if (q.structKey !== structKey || q.status !== "done" || !q.result) continue;
    const r = q.result, t = r.target.offset;
    get(t).isTarget = true;
    // Widths at the target only from confident accesses: low-confidence copies are often other structs.
    for (const a of r.accesses) add(t, a.confidence !== "low" ? a.width || undefined : undefined, a.function, t);
    for (const d of r.decompiled) {
      if (!d.excerpt) continue;
      const model = parseExcerpt(d, t);
      for (const ref of model.refs) add(ref.off, ref.width, d.function, t);
      for (const g of model.gates) {
        const m = get(g.off);
        if (!m.gatedBy.some((x) => x.fromOff === t && x.bits.join() === g.bits.join())) m.gatedBy.push({ fromOff: t, bits: g.bits, when: g.when });
      }
    }
  }
  for (const m of marks.values()) m.widths.sort((a, b) => b - a);
  return marks;
}

/** Marks touching a byte range. */
export function marksIn(cm: CodeMarks | undefined, off: number, size: number): CodeMark[] {
  if (!cm) return [];
  const out: CodeMark[] = [];
  for (const m of cm.values()) if (m.off >= off && m.off < off + size) out.push(m);
  return out.sort((a, b) => a.off - b.off);
}

/** Span a mark covers: its widest access, at least 1 byte. */
export function markSize(m: CodeMark): number {
  return Math.max(1, m.widths[0] ?? 1);
}

export const KIND_LABEL: Record<AccessKind, string> = {
  read: "read", write: "write", "bit-test": "bit test", "set-bits": "sets bits", "clear-bits": "clears bits", "address-of": "address of",
};

/** "FUN_141d4d020" -> "141d4d020" for a Ghidra address; "?" stays. */
export function fnAddress(fn: string): string | undefined {
  const m = /^FUN_([0-9a-fA-F]+)$/.exec(fn);
  return m ? "0x" + m[1] : undefined;
}

/** The elapsed-time phase of a scan, for the progress copy. */
export function scanPhase(elapsedMs: number, anchors: number, cached: boolean): { label: string; pct: number } {
  if (cached) return { label: "Reading the cached scan", pct: Math.min(0.9, elapsedMs / 3000) };
  // ~30 s per searched offset: the target first, then up to 6 anchors, then decompiling.
  const per = 30_000, total = per * (1 + anchors) + 8_000;
  const pct = Math.min(0.97, elapsedMs / total);
  if (elapsedMs < per) return { label: "Searching the program for the target displacement", pct };
  if (elapsedMs < per * (1 + anchors)) return { label: `Searching anchor field ${Math.min(anchors, Math.floor(elapsedMs / per))} of ${anchors} (fingerprint)`, pct };
  return { label: "Decompiling the best functions", pct };
}
