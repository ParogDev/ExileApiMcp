// Turns ticked nodes into a ready-to-paste plugin snippet. Simple and deterministic: one hoisted
// local for the deepest common reference-type parent, one `var` per value, the `using` lines the
// involved types need, and a plain path list for eval_path / watch_object.

import { splitCSharp, splitPath, joinPath, segmentLabel } from "./paths";
import type { NodeKind } from "./types";

export interface SnippetItem {
  name: string;
  path: string;
  csharp: string;
  type?: string;
  kind: NodeKind;
  preview?: string;
}

/** What the generator may look up about already-loaded nodes (kind + namespace by path). */
export interface SnippetContext {
  kindOf: (path: string) => NodeKind | undefined;
  namespaceOf: (path: string) => string | undefined;
}

export interface Snippet {
  csharp: string;
  paths: string;
  /** The hoisted parent, if any (for the UI to explain the snippet). */
  hoisted?: { path: string; variable: string };
}

const RESERVED = new Set(["abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while", "var"]);

const GENERIC = new Set(["Max", "Min", "Current", "Value", "Count", "Name", "Id", "X", "Y", "Z", "W", "Width", "Height", "Address", "Text", "Type", "Size", "Index", "Length", "Total", "Regen", "Reserved", "IsValid", "IsVisible"]);

export function generateSnippet(items: SnippetItem[], ctx: SnippetContext): Snippet {
  const sorted = [...items].sort((a, b) => a.path.localeCompare(b.path));
  const paths = sorted.map((i) => i.path).join("\n");
  if (sorted.length === 0) return { csharp: "", paths };

  const segs = sorted.map((i) => splitPath(i.path));
  const cs = sorted.map((i) => splitCSharp(i.csharp));

  // Deepest common prefix. Never hoist a selected value itself (the local would just alias it),
  // never hoist a struct (its members are read through "." and a null check means nothing),
  // never hoist GameController alone (the plugin already has it).
  let common = 0;
  const minLen = Math.min(...segs.map((s) => s.length));
  while (common < minLen && segs.every((s) => s[common] === segs[0][common])) common++;
  if (segs.some((s) => s.length === common)) common = Math.max(0, Math.min(common, minLen - 1));
  while (common > 0) {
    const kind = ctx.kindOf(joinPath(segs[0].slice(0, common)));
    if (kind === "struct" || kind === "sequence") common--; else break;
  }
  if (common <= 1) common = 0;

  const used = new Set<string>();
  const lines: string[] = [];
  const usings = new Set<string>();
  let hoisted: Snippet["hoisted"];
  const csOk = cs.every((c, i) => c.length === segs[i].length);

  if (common > 0 && csOk) {
    const hoistPath = joinPath(segs[0].slice(0, common));
    const variable = uniqueName(variableName(segs[0].slice(0, common)), used);
    const expr = cs[0].slice(0, common).join("");
    hoisted = { path: hoistPath, variable };
    const ns = ctx.namespaceOf(hoistPath);
    if (ns) usings.add(ns);
    lines.push(`var ${variable} = ${expr};`);
    lines.push(`if (${variable} == null) return;`);
  }

  for (let i = 0; i < sorted.length; i++) {
    const item = sorted[i];
    const name = uniqueName(variableName(segs[i].slice(hoisted ? common : 0), segs[i]), used);
    let expr: string;
    if (hoisted && csOk) {
      // Relative to the local: the first hop is unconditional (we just null-checked), the rest keep
      // the server's operators, except after a struct, where "?." would not compile.
      const rest = cs[i].slice(common).map((seg, j) => {
        const parent = joinPath(segs[i].slice(0, common + j));
        const parentKind = j === 0 ? "object" : ctx.kindOf(parent);
        return parentKind === "struct" || j === 0 ? seg.replace(/^\?/, "") : seg;
      });
      expr = hoisted.variable + rest.join("");
    } else {
      expr = csOk
        ? cs[i].map((seg, j) => (j > 0 && ctx.kindOf(joinPath(segs[i].slice(0, j))) === "struct" ? seg.replace(/^\?/, "") : seg)).join("")
        : item.csharp;
    }
    const ns = ctx.namespaceOf(item.path);
    if (ns) usings.add(ns);
    lines.push(`var ${name} = ${expr};${comment(item)}`);
  }

  // Align the trailing comments when they exist.
  const code = lines.filter((l) => l.includes("  //"));
  if (code.length > 0) {
    const width = Math.max(...code.map((l) => l.indexOf("  //")));
    for (let i = 0; i < lines.length; i++) {
      const at = lines[i].indexOf("  //");
      if (at >= 0) lines[i] = lines[i].slice(0, at).padEnd(width) + lines[i].slice(at);
    }
  }

  const header = [...usings].filter((u) => u !== "System").sort().map((u) => `using ${u};`);
  const csharp = (header.length ? header.join("\n") + "\n\n" : "") + lines.join("\n");
  return { csharp, paths, hoisted };
}

function comment(item: SnippetItem): string {
  const parts: string[] = [];
  if (item.type) parts.push(item.type);
  if (item.preview && item.kind !== "object" && item.kind !== "component" && item.kind !== "struct") {
    const p = item.preview.length > 40 ? item.preview.slice(0, 39) + "…" : item.preview;
    parts.push(item.kind === "list" || item.kind === "dictionary" ? p.toLowerCase() : `now ${p}`);
  }
  return parts.length ? `  // ${parts.join(", ")}` : "";
}

/** camelCase local from the last meaningful segments: GetComponent<Life>() -> life, Stats["X"] -> x, Entities[2] -> entities2. */
function variableName(relative: string[], full: string[] = relative): string {
  const segs = relative.length ? relative : full;
  const last = segs[segs.length - 1];
  let base: string;
  if (last.startsWith("[")) {
    const key = segmentLabel(last);
    const owner = segs.length > 1 ? segmentLabel(segs[segs.length - 2]) : "item";
    base = /^\d+$/.test(key) ? `${owner}${key}` : key;
  } else {
    base = segmentLabel(last);
    // "Max", "Current", "X" say nothing alone: prefix the owner ("healthMax", "posX").
    if (GENERIC.has(base) && segs.length > 1) {
      const owner = segmentLabel(segs[segs.length - 2]).replace(/[^A-Za-z0-9_]/g, "");
      base = owner + base.charAt(0).toUpperCase() + base.slice(1);
    }
  }
  base = base.replace(/[^A-Za-z0-9_]/g, "");
  if (!base) base = "value";
  let name = base.charAt(0).toLowerCase() + base.slice(1);
  // All-caps names (CurHP, ES) read better lowercased in full.
  if (/^[A-Z0-9]+$/.test(base)) name = base.toLowerCase();
  if (/^\d/.test(name)) name = `v${name}`;
  if (RESERVED.has(name)) name = `${name}Value`;
  return name;
}

function uniqueName(name: string, used: Set<string>): string {
  let n = name, i = 2;
  while (used.has(n)) n = `${name}${i++}`;
  used.add(n);
  return n;
}
