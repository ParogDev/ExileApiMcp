// One vocabulary of colours and labels for what covers a byte, shared by the strip, the map rows, the hex
// view and the inspector so the same thing always looks the same. Colours are theme tokens (styles.css);
// alpha variants are mixed at use so light and dark both read.

import type { IconName } from "../icons";
import type { Seg } from "./bytes";
import type { Check, SlotKind } from "./types";

export type Tone = "field" | "cand" | "ptr" | "num" | "str" | "change" | "warning" | "danger" | "success" | "none";

const VAR: Record<Exclude<Tone, "none">, string> = {
  field: "var(--color-m-field)", cand: "var(--color-m-cand)", ptr: "var(--color-m-ptr)",
  num: "var(--color-k-num)", str: "var(--color-k-str)", change: "var(--color-m-change)", warning: "var(--color-warning)", danger: "var(--color-danger)",
  success: "var(--color-success)",
};

export const TONE_TEXT: Record<Tone, string> = {
  field: "text-m-field", cand: "text-m-cand", ptr: "text-m-ptr", num: "text-k-num", str: "text-k-str", change: "text-m-change",
  warning: "text-warning", danger: "text-danger", success: "text-success", none: "text-fg-3",
};
export const TONE_BG: Record<Tone, string> = {
  field: "bg-m-field", cand: "bg-m-cand", ptr: "bg-m-ptr", num: "bg-k-num", str: "bg-k-str", change: "bg-m-change",
  warning: "bg-warning", danger: "bg-danger", success: "bg-success", none: "bg-fg-3",
};

/**
 * What an instruction does to a field, one colour each so a function's instruction list can be read at a glance:
 * read = teal, write = magenta (it changes the bytes), bit test = amber, set bits = green, clear bits = red,
 * address-of = neutral (the field is handed to a helper).
 */
export const ACCESS_TONE: Record<string, Tone> = {
  read: "ptr", write: "change", "bit-test": "warning", "set-bits": "success", "clear-bits": "danger", "address-of": "none",
};

/** CSS colour: the tone at `pct` % over transparent. */
export function mix(tone: Tone, pct: number): string {
  if (tone === "none") return "transparent";
  return `color-mix(in oklab, ${VAR[tone]} ${pct}%, transparent)`;
}

export function toneVar(tone: Tone): string {
  return tone === "none" ? "var(--color-fg-3)" : VAR[tone];
}

export function checkTone(check: Check | undefined): Tone {
  switch (check) {
    case "suspicious": case "unusual": return "warning";
    case "invalid": return "danger";
    default: return "field";
  }
}

export const CHECK_LABEL: Record<Check, string> = { ok: "ok", unusual: "unusual", suspicious: "suspicious", invalid: "invalid", unread: "not read" };

export function slotTone(kind: SlotKind | string | undefined): Tone {
  switch (kind) {
    case "vtable": case "module": return "cand";
    case "heap": case "pointer": case "self": return "ptr";
    case "text": return "str";
    case "int": case "float": return "num";
    case "bad-pointer": return "danger";
    default: return "none";
  }
}

/** The tone a segment paints its bytes with. */
export function segTone(seg: Seg): Tone {
  switch (seg.kind) {
    case "field": return checkTone(seg.field?.check);
    case "cand": return "cand";
    case "slot": return slotTone(seg.slot?.kind);
    default: return "none";
  }
}

/** Tint strength for the hex view and the strip: fields and candidates are solid; slots by how telling they are. */
export function segAlpha(seg: Seg): number {
  switch (seg.kind) {
    case "field": return seg.field?.check === "unread" ? 10 : 26;
    case "cand": return 30;
    case "slot": return seg.slot?.kind === "bad-pointer" ? 10 : seg.slot?.kind === "zero" ? 0 : 22;
    default: return 0;
  }
}

export const KIND_LABEL: Record<string, string> = {
  "std::vector": "std::vector", vtable: "vtable", module: "module pointer", heap: "heap pointer", pointer: "pointer",
  self: "self pointer", text: "text", zero: "zero", float: "float pair", int: "integer", "bad-pointer": "not a pointer",
};

export function kindIcon(kind: string | undefined): IconName {
  switch (kind) {
    case "std::vector": return "list";
    case "vtable": return "layers";
    case "module": return "code";
    case "heap": case "pointer": return "arrowUpRight";
    case "self": return "target";
    case "text": return "quote";
    case "float": case "int": return "hash";
    case "bad-pointer": return "warning";
    default: return "dot";
  }
}

/** Human label for a segment, used by the inspector header and the model context. */
export function segLabel(seg: Seg): string {
  switch (seg.kind) {
    case "field": return "mapped field";
    case "cand": return `candidate · ${KIND_LABEL[seg.cand?.kind ?? ""] ?? seg.cand?.kind}`;
    case "gap": return "unmapped";
    case "slot": return KIND_LABEL[seg.slot?.kind ?? ""] ?? seg.slot?.kind ?? "slot";
    case "zeros": return "zero run";
  }
}
