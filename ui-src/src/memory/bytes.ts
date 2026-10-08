// Pure byte helpers for the memory view: the region model (what covers each byte), address arithmetic,
// offset formatting, alternative interpretations of a byte range and .NET-type decoding for live reads.

import type { Candidate, ChangedRange, HexRow, LayoutField, LayoutResult, ReadResult, ReadSlot } from "./types";

// ── Region: one offset-ordered cover of the bytes ────────────────────

export type SegKind = "field" | "cand" | "gap" | "slot" | "zeros";

export interface Seg {
  /** Stable id within the region: "f:424", "c:40", "g:16", "s:8", "z:96". */
  id: string;
  off: number;
  size: number;
  kind: SegKind;
  field?: LayoutField;
  cand?: Candidate;
  slot?: ReadSlot;
  /** Nested-struct group ("Health" for "Health.Max"); gaps and candidates enclosed by a group carry it too. */
  group?: string;
  /** zeros: how many 8-byte slots the run collapses. */
  count?: number;
}

export interface Region {
  address: string;
  size: number;
  bytes: Uint8Array;
  /** 1 where the byte was actually read (hex rows can stop short of the declared size). */
  known: Uint8Array;
  segs: Seg[];
  /** Byte offset -> index into segs (-1: none). */
  cover: Int32Array;
  /** Layout views: the declared struct end; bytes past it come from `extend`. */
  structSize?: number;
}

export function bytesFromHex(rows: HexRow[], size: number): { bytes: Uint8Array; known: Uint8Array } {
  const bytes = new Uint8Array(size);
  const known = new Uint8Array(size);
  for (const row of rows) {
    const parts = row.bytes.trim().split(/\s+/).filter(Boolean);
    for (let i = 0; i < parts.length; i++) {
      const o = row.off + i;
      if (o >= size) break;
      bytes[o] = parseInt(parts[i], 16);
      known[o] = 1;
    }
  }
  return { bytes, known };
}

export function groupOf(name: string): string | undefined {
  const i = name.lastIndexOf(".");
  return i > 0 ? name.slice(0, i) : undefined;
}

export function leafOf(name: string): string {
  const i = name.lastIndexOf(".");
  return i > 0 ? name.slice(i + 1) : name;
}

/** Layout result -> region: fields and candidates as segments, every uncovered byte range as a gap. */
export function regionFromLayout(r: LayoutResult): Region {
  const last = r.hex.length ? r.hex[r.hex.length - 1] : undefined;
  const hexEnd = last ? last.off + last.bytes.trim().split(/\s+/).filter(Boolean).length : 0;
  const gapEnd = Math.max(0, ...r.gaps.map((g) => g.off + g.size));
  const size = Math.max(r.structSize, hexEnd, gapEnd);
  const { bytes, known } = bytesFromHex(r.hex, size);
  const cover = new Int32Array(size).fill(-1);
  const segs: Seg[] = [];

  const place = (s: Seg) => {
    if (s.off < 0 || s.size <= 0 || s.off >= size) return false;
    const end = Math.min(size, s.off + s.size);
    for (let i = s.off; i < end; i++) if (cover[i] !== -1) return false;
    const idx = segs.push({ ...s, size: end - s.off }) - 1;
    for (let i = s.off; i < end; i++) cover[i] = idx;
    return true;
  };
  for (const f of [...r.fields].sort((a, b) => a.off - b.off || b.size - a.size)) place({ id: `f:${f.off}`, off: f.off, size: f.size, kind: "field", field: f, group: groupOf(f.name) });
  for (const c of [...r.candidates].sort((a, b) => a.off - b.off || b.size - a.size)) place({ id: `c:${c.off}`, off: c.off, size: c.size, kind: "cand", cand: c });
  // Gaps: whatever is left, split at the struct end so "past the end" reads as its own range.
  let i = 0;
  while (i < size) {
    if (cover[i] !== -1) { i++; continue; }
    let j = i;
    while (j + 1 < size && cover[j + 1] === -1 && !(j + 1 === r.structSize)) j++;
    place({ id: `g:${i}`, off: i, size: j - i + 1, kind: "gap" });
    i = j + 1;
  }
  segs.sort((a, b) => a.off - b.off);
  for (let k = 0; k < segs.length; k++) for (let b = segs[k].off; b < segs[k].off + segs[k].size; b++) cover[b] = k;
  assignGroups(segs);
  return { address: r.address, size, bytes, known, segs, cover, structSize: r.structSize };
}

/** Gaps and candidates between two fields of the same nested struct belong to that struct visually. */
function assignGroups(segs: Seg[]) {
  let prevGroup: string | undefined;
  for (let k = 0; k < segs.length; k++) {
    const s = segs[k];
    if (s.kind === "field") { prevGroup = s.group; continue; }
    if (!prevGroup) continue;
    let next: Seg | undefined;
    for (let m = k + 1; m < segs.length; m++) if (segs[m].kind === "field") { next = segs[m]; break; }
    if (next?.group === prevGroup) s.group = prevGroup; else prevGroup = undefined;
  }
}

/** Read result -> region: one segment per classified 8-byte slot; runs of zero slots collapse. */
export function regionFromRead(r: ReadResult): Region {
  const size = r.size;
  const { bytes, known } = bytesFromHex(r.hex, size);
  const segs: Seg[] = [];
  const slots = [...r.slots].sort((a, b) => a.off - b.off);
  for (let i = 0; i < slots.length; i++) {
    const s = slots[i];
    if (s.off >= size) break;
    if (s.kind === "zero") {
      let j = i;
      while (j + 1 < slots.length && slots[j + 1].kind === "zero" && slots[j + 1].off === slots[j].off + 8) j++;
      if (j > i) {
        segs.push({ id: `z:${s.off}`, off: s.off, size: Math.min(size, slots[j].off + 8) - s.off, kind: "zeros", count: j - i + 1 });
        i = j;
        continue;
      }
    }
    segs.push({ id: `s:${s.off}`, off: s.off, size: Math.min(8, size - s.off), kind: "slot", slot: s });
  }
  const covered = segs.length ? segs[segs.length - 1].off + segs[segs.length - 1].size : 0;
  if (covered < size) segs.push({ id: `g:${covered}`, off: covered, size: size - covered, kind: "gap" });
  const cover = new Int32Array(size).fill(-1);
  segs.forEach((s, k) => { for (let b = s.off; b < s.off + s.size; b++) cover[b] = k; });
  return { address: r.address, size, bytes, known, segs, cover };
}

export function segAt(region: Region, off: number): Seg | undefined {
  if (off < 0 || off >= region.size) return undefined;
  const k = region.cover[off];
  return k >= 0 ? region.segs[k] : undefined;
}

// ── Addresses and offsets ────────────────────────────────────────────

export function parseAddress(s: string | number | undefined | null): bigint | undefined {
  if (s === undefined || s === null) return undefined;
  if (typeof s === "number") return Number.isFinite(s) ? BigInt(Math.trunc(s)) : undefined;
  const t = s.trim();
  if (!t) return undefined;
  try {
    if (/^0x[0-9a-f]+$/i.test(t)) return BigInt(t);
    if (/^[0-9a-f]+$/i.test(t) && /[a-f]/i.test(t)) return BigInt("0x" + t);
    if (/^\d+$/.test(t)) return BigInt(t);
    return undefined;
  } catch { return undefined; }
}

export function hexAddr(v: bigint): string {
  return "0x" + v.toString(16).toUpperCase();
}

/** base + off as a hex address string. */
export function addrPlus(base: string, off: number): string {
  const b = parseAddress(base);
  return b === undefined ? base : hexAddr(b + BigInt(off));
}

/** Looks like a canonical user-mode x64 pointer. */
export function isPointerLike(v: bigint): boolean {
  return v >= 0x10000n && v < 0x7fffffffffffn && (v & 0x7n) === 0n;
}

/** Short form for crumbs: "…850530". */
export function shortAddr(a: string): string {
  const t = a.replace(/^0x/i, "").toUpperCase();
  return t.length > 6 ? `…${t.slice(-6)}` : `0x${t}`;
}

/** Whether the text names a path (walker) rather than an address. */
export function isPathText(s: string): boolean {
  return parseAddress(s) === undefined;
}

export function hexOff(off: number): string {
  return "0x" + off.toString(16).toUpperCase();
}

/** "+0x1A8 (424)" style pair for titles. */
export function offPair(off: number): string {
  return `+${hexOff(off)} (${off})`;
}

export function hexByte(b: number): string {
  return b.toString(16).toUpperCase().padStart(2, "0");
}

export function hexOf(bytes: Uint8Array, off: number, size: number): string {
  const out: string[] = [];
  for (let i = off; i < Math.min(bytes.length, off + size); i++) out.push(hexByte(bytes[i]));
  return out.join(" ");
}

export function asciiOf(bytes: Uint8Array, off: number, size: number): string {
  let s = "";
  for (let i = off; i < Math.min(bytes.length, off + size); i++) {
    const b = bytes[i];
    s += b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : ".";
  }
  return s;
}

/** Short label for a walker path: the last segment, GetComponent<Life>() -> "Life". */
export function pathLabel(path: string): string {
  const segs = path.split(/\.(?![^<]*>)(?![^(]*\))/);
  const last = segs[segs.length - 1] ?? path;
  const gc = /^GetComponent<(.+)>\(\)$/.exec(last);
  if (gc) return gc[1];
  // Keep a trailing indexer with its parent: "PlayerStashTabs[0]".
  return last;
}

/** Last two segments of a struct name: "GameOffsets.LifeComponentOffsets" -> "LifeComponentOffsets". */
export function typeLabel(t: string): string {
  const i = t.lastIndexOf(".");
  return i >= 0 ? t.slice(i + 1) : t;
}

// ── Interpretations ──────────────────────────────────────────────────

export interface Interp {
  label: string;
  value: string;
  /** For pointer-like 8-byte values: the address to follow. */
  address?: string;
  mono?: boolean;
  dim?: boolean;
}

function view(bytes: Uint8Array, off: number, size: number): DataView | undefined {
  if (off < 0 || off + size > bytes.length) return undefined;
  return new DataView(bytes.buffer, bytes.byteOffset + off, size);
}

const fmtNum = (n: number) => n.toLocaleString("en-US", { maximumFractionDigits: 6 });

function fmtFloat(v: number): string {
  if (!Number.isFinite(v)) return String(v);
  if (v !== 0 && (Math.abs(v) < 1e-4 || Math.abs(v) >= 1e9)) return v.toExponential(4);
  return fmtNum(Math.round(v * 1e6) / 1e6);
}

/** Every reading of the bytes at [off, off+size) that fits in them, little-endian. */
export function interpret(bytes: Uint8Array, off: number, size: number): Interp[] {
  const out: Interp[] = [];
  const n = Math.min(size, bytes.length - off);
  if (n <= 0) return out;
  if (n >= 1 && n < 2) {
    const dv = view(bytes, off, 1)!;
    out.push({ label: "uint8", value: fmtNum(dv.getUint8(0)) }, { label: "int8", value: fmtNum(dv.getInt8(0)) });
  }
  if (n >= 2) {
    const dv = view(bytes, off, 2)!;
    out.push({ label: "uint16", value: fmtNum(dv.getUint16(0, true)) });
    const s16 = dv.getInt16(0, true);
    if (s16 < 0) out.push({ label: "int16", value: fmtNum(s16) });
  }
  if (n >= 4) {
    const dv = view(bytes, off, 4)!;
    const u = dv.getUint32(0, true), s = dv.getInt32(0, true);
    out.push({ label: "uint32", value: fmtNum(u) });
    if (s !== u) out.push({ label: "int32", value: fmtNum(s) });
    const f = dv.getFloat32(0, true);
    out.push({ label: "float", value: fmtFloat(f), dim: !Number.isFinite(f) || (f !== 0 && (Math.abs(f) < 1e-6 || Math.abs(f) > 1e8)) });
  }
  if (n >= 8) {
    const dv = view(bytes, off, 8)!;
    const u = dv.getBigUint64(0, true), s = dv.getBigInt64(0, true);
    out.push({ label: "uint64", value: u.toLocaleString("en-US") });
    if (s < 0n) out.push({ label: "int64", value: s.toLocaleString("en-US") });
    const d = dv.getFloat64(0, true);
    out.push({ label: "double", value: fmtFloat(d), dim: !Number.isFinite(d) || (d !== 0 && (Math.abs(d) < 1e-6 || Math.abs(d) > 1e12)) });
    const ptr = isPointerLike(u) || (u >= 0x10000n && u < 0x7fffffffffffn);
    out.push({ label: "pointer", value: hexAddr(u), mono: true, address: ptr ? hexAddr(u) : undefined, dim: !ptr });
  }
  if (n >= 2) {
    const u16 = utf16Of(bytes, off, n);
    if (u16) out.push({ label: "UTF-16", value: JSON.stringify(u16), mono: true });
  }
  const ascii = asciiPrintable(bytes, off, n);
  if (ascii) out.push({ label: "ASCII", value: JSON.stringify(ascii), mono: true });
  return out;
}

/** Printable UTF-16LE prefix, if the bytes read as text (at least two chars, mostly printable). */
export function utf16Of(bytes: Uint8Array, off: number, size: number): string | undefined {
  let s = "";
  for (let i = off; i + 1 < off + size && i + 1 < bytes.length; i += 2) {
    const c = bytes[i] | (bytes[i + 1] << 8);
    if (c === 0) break;
    // Printable Latin, Latin extended, Greek and Cyrillic only: anything else is bytes, not text.
    if (!((c >= 0x20 && c < 0x7f) || (c >= 0xa0 && c <= 0x24f) || (c >= 0x370 && c <= 0x4ff))) return undefined;
    s += String.fromCharCode(c);
  }
  return s.length >= 2 ? s : undefined;
}

function asciiPrintable(bytes: Uint8Array, off: number, size: number): string | undefined {
  let s = "";
  for (let i = off; i < off + size && i < bytes.length; i++) {
    const b = bytes[i];
    if (b === 0) break;
    if (b < 0x20 || b >= 0x7f) return undefined;
    s += String.fromCharCode(b);
  }
  return s.length >= 3 ? s : undefined;
}

/** Set bits of the little-endian unsigned integer at [off, off+size) (size <= 8). */
export function setBits(bytes: Uint8Array, off: number, size: number): number[] {
  const bits: number[] = [];
  for (let i = 0; i < Math.min(size, 8); i++) {
    const b = bytes[off + i] ?? 0;
    for (let bit = 0; bit < 8; bit++) if (b & (1 << bit)) bits.push(i * 8 + bit);
  }
  return bits;
}

/**
 * Bits of a selection [off, off+size) that a watch saw flip, numbered relative to the selection's first byte.
 * A range's bitsFlipped count from its covering field's first byte when `bitsRelativeTo` names a field (the
 * selection is then that field), else from the range start.
 */
export function flippedBitsFor(sel: { off: number; size: number }, ranges: ChangedRange[]): number[] {
  const out = new Set<number>();
  for (const c of ranges) {
    if (!c.bitsFlipped || c.off + c.size <= sel.off || c.off >= sel.off + sel.size) continue;
    const m = /\(\+(\d+)\)/.exec(c.bitsRelativeTo ?? "");
    const base = c.bitsRelativeTo?.startsWith("field") && m ? Number(m[1]) : c.off;
    for (const b of c.bitsFlipped) {
      const bit = (base - sel.off) * 8 + b;
      if (bit >= 0 && bit < sel.size * 8) out.add(bit);
    }
  }
  return [...out].sort((a, b) => a - b);
}

/** The unsigned little-endian integer at [off, off+size) as a bigint (size <= 8). */
export function uintAt(bytes: Uint8Array, off: number, size: number): bigint {
  let v = 0n;
  for (let i = Math.min(size, 8) - 1; i >= 0; i--) v = (v << 8n) | BigInt(bytes[off + i] ?? 0);
  return v;
}

// ── .NET type decoding (live re-reads keep field values current) ─────

export function decodeDotNet(type: string, bytes: Uint8Array, off: number, size: number): unknown {
  const dv = view(bytes, off, size);
  if (!dv) return undefined;
  switch (type) {
    case "Byte": return size >= 1 ? dv.getUint8(0) : undefined;
    case "SByte": return size >= 1 ? dv.getInt8(0) : undefined;
    case "Boolean": return size >= 1 ? dv.getUint8(0) !== 0 : undefined;
    case "Char": return size >= 2 ? String.fromCharCode(dv.getUint16(0, true)) : undefined;
    case "Int16": return size >= 2 ? dv.getInt16(0, true) : undefined;
    case "UInt16": return size >= 2 ? dv.getUint16(0, true) : undefined;
    case "Int32": return size >= 4 ? dv.getInt32(0, true) : undefined;
    case "UInt32": return size >= 4 ? dv.getUint32(0, true) : undefined;
    case "Single": return size >= 4 ? fmtFloat(dv.getFloat32(0, true)) : undefined;
    case "Double": return size >= 8 ? fmtFloat(dv.getFloat64(0, true)) : undefined;
    case "Int64": case "IntPtr": case "UInt64": case "UIntPtr": {
      if (size < 8) return undefined;
      const u = dv.getBigUint64(0, true);
      return isPointerLike(u) || u > 0xffffffffn ? hexAddr(u) : Number(type === "Int64" ? dv.getBigInt64(0, true) : u);
    }
    default: return undefined;
  }
}

/** Is the type one whose bits are worth a grid (integers up to 8 bytes)? */
export function isIntegerType(type: string | undefined): boolean {
  return !!type && /^(S?Byte|U?Int(16|32|64)|U?IntPtr|Boolean)$/.test(type);
}

export function fmtValue(v: unknown): string {
  if (v === undefined || v === null) return "—";
  if (typeof v === "number") return fmtNum(v);
  if (typeof v === "boolean") return v ? "true" : "false";
  if (typeof v === "string") return v;
  return JSON.stringify(v);
}

export function fmtBytes(n: number): string {
  return n === 1 ? "1 byte" : `${n.toLocaleString("en-US")} bytes`;
}
