// Kind-aware rendering of a node's one-line preview: numbers, strings, enums and booleans in their
// colour, struct previews as key=value pairs, objects by their distinguishing fields and visibility,
// collections as a count badge. Shared by the tree rows and the inspector.

import type { ReactNode } from "react";
import { Icon } from "../icons";
import type { ExploreChild, ExploreNode, NodeKind } from "./types";

export const KIND_LABEL: Record<NodeKind, string> = {
  null: "null", bool: "bool", number: "number", string: "string", enum: "enum", struct: "struct", object: "object",
  list: "list", dictionary: "dictionary", sequence: "sequence", blocked: "blocked", component: "component",
};

/** Text colour class for a scalar kind. */
export const KIND_TEXT: Partial<Record<NodeKind, string>> = {
  number: "text-k-num", string: "text-k-str", bool: "text-k-bool", enum: "text-k-enum", null: "text-fg-3 italic",
};

/** Marker dot colour for a kind (inspector header, legend). */
export const KIND_DOT: Record<NodeKind, string> = {
  number: "bg-k-num", string: "bg-k-str", bool: "bg-k-bool", enum: "bg-k-enum", null: "bg-fg-3",
  struct: "bg-fg-2", object: "bg-fg-2", component: "bg-info", list: "bg-fg-2", dictionary: "bg-fg-2", sequence: "bg-fg-2", blocked: "bg-danger",
};

export interface Pair { k: string; v: string }

/** "X=5662.3 Y=4906.9 Z=0" / "RenderName=\"Mercenary\"" -> pairs; words that are not pairs come back in `rest`. */
export function parsePreview(s: string): { pairs: Pair[]; rest: string[] } {
  const pairs: Pair[] = [];
  const rest: string[] = [];
  const re = /([A-Za-z_][\w.]*)=("(?:[^"\\]|\\.)*"|\S+)|(\S+)/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(s))) {
    if (m[1] !== undefined) pairs.push({ k: m[1], v: m[2] });
    else rest.push(m[3]);
  }
  return { pairs, rest };
}

/** An object's preview minus its leading type name ("Entity RenderName=..." -> "RenderName=..."). */
export function objectDetail(node: ExploreNode): string {
  const p = node.preview ?? "";
  const type = node.type ?? "";
  if (!p || p === type) return "";
  if (p.startsWith(type + " ")) return p.slice(type.length + 1);
  // Runtime type differs from the one in the preview (declaredType cases): drop a leading identifier.
  const first = p.indexOf(" ");
  if (first > 0 && /^[A-Za-z_][\w<>,` ]*$/.test(p.slice(0, first)) && !p.slice(0, first).includes("=")) return p.slice(first + 1);
  return p;
}

/** Whether the row's preview says more than the type (decides if the type column is worth repeating). */
export function hasDetail(node: ExploreNode): boolean {
  return node.kind !== "object" || objectDetail(node) !== "";
}

export function CountBadge({ count, className = "" }: { count: number; className?: string }) {
  return (
    <span className={`tnum inline-flex h-4 min-w-4 items-center justify-center rounded px-1 font-code text-[10px] leading-none ${count === 0 ? "text-fg-3" : "bg-surface-3 text-fg-2"} ${className}`}
      title={`${count.toLocaleString("en-US")} item${count === 1 ? "" : "s"}`}>
      {count === 0 ? "empty" : count.toLocaleString("en-US")}
    </span>
  );
}

function Pairs({ pairs, max }: { pairs: Pair[]; max?: number }) {
  const shown = max ? pairs.slice(0, max) : pairs;
  return (
    <>
      {shown.map((p, i) => (
        <span key={i} className="inline-flex items-baseline whitespace-nowrap">
          <span className="text-fg-3">{p.k}=</span>
          <span className={p.v.startsWith('"') ? "text-k-str" : /^-?\d/.test(p.v) ? "tnum text-k-num" : "text-fg-2"}>{p.v}</span>
        </span>
      ))}
      {max && pairs.length > max && <span className="text-fg-3">+{pairs.length - max}</span>}
    </>
  );
}

function StateWord({ word }: { word: string }) {
  if (word === "hidden") return <span className="inline-flex items-center gap-1 text-fg-3"><Icon name="eyeOff" className="size-3" />hidden</span>;
  if (word === "visible") return <span className="inline-flex items-center gap-1 text-success"><Icon name="eye" className="size-3" />visible</span>;
  return <span className="text-fg-2">{word}</span>;
}

/**
 * One-line preview for a tree row (truncates) or the inspector (`full`: wraps and shows every pair).
 */
export function Preview({ node, full = false, className = "" }: { node: ExploreNode | ExploreChild; full?: boolean; className?: string }) {
  const kind = node.kind;
  const base = `${full ? "code-wrap" : "truncate"} font-code ${className}`;
  const err = (node as ExploreChild).error;
  if (err) {
    return (
      <span className={`${base} inline-flex items-center gap-1 text-danger`} title={err}>
        <Icon name="warning" className="size-3 shrink-0" />{err}
      </span>
    );
  }
  let body: ReactNode;
  switch (kind) {
    case "null": body = <span className="italic text-fg-3">null</span>; break;
    case "bool": body = <span className="text-k-bool">{node.preview}</span>; break;
    case "number": body = <span className="tnum text-k-num">{node.preview}</span>; break;
    case "string": body = <span className="text-k-str">{node.preview}</span>; break;
    case "enum": body = <span className="text-k-enum">{node.preview}</span>; break;
    case "list": case "dictionary": case "sequence":
      body = node.count !== undefined ? <CountBadge count={node.count} /> : <span className="text-fg-3">{node.preview}</span>;
      break;
    case "blocked":
      body = <span className="inline-flex items-center gap-1 italic text-fg-3"><Icon name="lock" className="size-3" />not readable</span>;
      break;
    case "component":
      body = node.path ? <span className="text-fg-3">GetComponent&lt;{node.type ?? "T"}&gt;()</span>
        : <span className="italic text-fg-3" title={node.note}>no HUD wrapper type</span>;
      break;
    case "struct": {
      const { pairs, rest } = parsePreview(node.preview ?? "");
      body = pairs.length ? <span className={`inline-flex ${full ? "flex-wrap" : ""} gap-x-2`}><Pairs pairs={pairs} max={full ? undefined : 4} /></span>
        : <span className="text-fg-2">{rest.join(" ") || node.preview}</span>;
      break;
    }
    default: {
      const detail = objectDetail(node);
      // No distinguishing fields: the type is the most useful thing to say (the type column hides at narrow widths).
      if (!detail) { body = <span className="text-fg-3">{node.type}</span>; break; }
      const { pairs, rest } = parsePreview(detail);
      body = (
        <span className={`inline-flex ${full ? "flex-wrap" : ""} items-baseline gap-x-2`}>
          {pairs.length > 0 && <Pairs pairs={pairs} max={full ? undefined : 3} />}
          {rest.map((w, i) => <StateWord key={i} word={w} />)}
        </span>
      );
    }
  }
  return <span className={`${base} inline-flex max-w-full items-baseline gap-1.5 ${full ? "" : "overflow-hidden"}`}>{body}</span>;
}
