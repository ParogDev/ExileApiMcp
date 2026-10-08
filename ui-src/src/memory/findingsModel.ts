// Findings (Knowledge/findings.json via the `findings` tool) laid over a struct: which findings talk about the
// struct on screen, the offsets and bits they name, and bit tables ("3 Currency, 4 Unique, ...") for a field.
// This is "the HUD doesn't map this, but we found something": the second primary colour of the struct map.

import type { Finding, FindingGame, FindingStatus, Game } from "./types";

/** One place a finding points at in the struct. */
export interface FindingMark {
  finding: Finding;
  status: FindingStatus;
  off: number;
  /** Bit within the byte at `off` (a flag), when the finding names one. */
  bit?: number;
  /** Width in bytes when known ("+63 (32-bit)" -> 4). */
  size?: number;
}

/** Names for the bits of a field: "Affinity" -> { 3: "Currency", 4: "Unique", ... }. */
export interface BitTable {
  finding: Finding;
  status: FindingStatus;
  field: string;
  names: Map<number, string>;
}

export interface StructFindings {
  all: Finding[];
  marks: FindingMark[];
  tables: BitTable[];
  /** Bit names by field leaf name, merged over tables. */
  bitNames: Map<string, Map<number, string>>;
}

const STATUS_RANK: Record<FindingStatus, number> = { verified: 0, differs: 1, unverified: 2, "n/a": 3 };

/** Findings whose subject or check path names the struct / object / collection on screen. */
export function findingsFor(findings: Finding[], ctx: { struct?: string; object?: string; path?: string }, game: Game | undefined): StructFindings {
  const keys = [ctx.struct, ctx.object].filter((s): s is string => !!s).map((s) => s.split(".").pop()!.toLowerCase());
  const path = ctx.path?.replace(/\[\d+\]$/, "").toLowerCase();
  const all = findings.filter((f) => {
    const subj = f.subject.toLowerCase();
    if (keys.some((k) => subj.includes(k))) return true;
    const p = f.check?.path?.toLowerCase();
    return !!p && !!path && (path.startsWith(p) || p.startsWith(path));
  });
  const marks: FindingMark[] = [];
  const tables: BitTable[] = [];
  for (const f of all) {
    const g = pick(f, game);
    if (!g?.where) continue;
    const table = parseBitTable(g.where);
    if (table) {
      const field = f.subject.split(".").pop() ?? "";
      tables.push({ finding: f, status: g.status, field, names: table });
      continue;
    }
    for (const m of parseWhere(g.where)) marks.push({ finding: f, status: g.status, ...m });
  }
  const bitNames = new Map<string, Map<number, string>>();
  for (const t of tables) {
    const cur = bitNames.get(t.field) ?? new Map<number, string>();
    for (const [b, n] of t.names) if (!cur.has(b)) cur.set(b, n);
    bitNames.set(t.field, cur);
  }
  marks.sort((a, b) => a.off - b.off || (a.bit ?? -1) - (b.bit ?? -1));
  return { all, marks, tables, bitNames };
}

/** The game's entry when it has a `where`, else the best other game's (a hypothesis here). */
export function pick(f: Finding, game: Game | undefined): FindingGame | undefined {
  const own = game ? f.games[game] : undefined;
  if (own?.where) return own;
  const others = Object.entries(f.games).filter(([g]) => g !== game).map(([, v]) => v!).filter((v) => v.where)
    .sort((a, b) => STATUS_RANK[a.status] - STATUS_RANK[b.status]);
  return others[0] ?? own;
}

/** "+61 bit 6" | "+63 (32-bit)" | "+0x178 / +0x1C8 / +0x210" -> offsets (+bit / size). */
export function parseWhere(where: string): { off: number; bit?: number; size?: number }[] {
  const out: { off: number; bit?: number; size?: number }[] = [];
  const re = /\+(0x[0-9a-f]+|\d+)(?:\s+bit\s+(\d+))?(?:\s*\((\d+)-bit\))?/gi;
  let m: RegExpExecArray | null;
  while ((m = re.exec(where))) {
    const off = m[1].toLowerCase().startsWith("0x") ? parseInt(m[1], 16) : Number(m[1]);
    const o: { off: number; bit?: number; size?: number } = { off };
    if (m[2] !== undefined) o.bit = Number(m[2]);
    if (m[3] !== undefined) o.size = Number(m[3]) / 8;
    out.push(o);
  }
  return out;
}

/** "3 Currency, 4 Unique, 5 Map" -> Map(3 -> Currency, ...). Undefined when the text is not a bit table. */
export function parseBitTable(where: string): Map<number, string> | undefined {
  const parts = where.split(/,\s*/);
  if (parts.length < 2) return undefined;
  const names = new Map<number, string>();
  for (const p of parts) {
    const m = /^(\d+)\s+([A-Za-z][\w' -]*)$/.exec(p.trim());
    if (!m) return undefined;
    names.set(Number(m[1]), m[2].trim());
  }
  return names;
}

/** Marks that touch a byte range. */
export function marksIn(sf: StructFindings | undefined, off: number, size: number): FindingMark[] {
  if (!sf) return [];
  return sf.marks.filter((m) => m.off >= off && m.off < off + size);
}

/** Status for the game, "unverified" when the game has no entry. */
export function statusOn(f: Finding, game: Game): FindingStatus {
  return f.games[game]?.status ?? "unverified";
}

/** Verified on some other game, not on this one. */
export function isToCheck(f: Finding, game: Game): boolean {
  return statusOn(f, game) === "unverified" && Object.entries(f.games).some(([g, v]) => g !== game && v?.status === "verified");
}

/** Short subject for a row: "ServerStashTab (ServerStashTabOffsets)" -> "ServerStashTab". */
export function subjectLabel(subject: string): string {
  return subject.replace(/\s*\(.*\)\s*$/, "");
}
