// Guided experiments read for a human, all pure: watch specs and change keys shortened to what matters, one change
// described in plain language, the evidence per action (what changed every time vs sometimes) and what the user has
// left to undo in game when an action's undo was done fewer times than the action.

import type { AwaitResult, Consistent, ExperimentChange, ExperimentPreset, ExperimentRecord, GuideState, GuideStatus, RecordStep, SummaryResult } from "./types";

export interface WatchInfo {
  kind: "value" | "memory" | "collection" | string;
  /** The walker path without GameController. */
  path: string;
  /** The last two or three segments: "VisibleStash.ItemCount". */
  short: string;
  /** memory: bytes to read; collection: labels. */
  size?: number;
  labels?: string[];
}

/** "value:GameController.IngameState.IngameUi.StashElement.VisibleStash.ItemCount" -> kind, path and a short name. */
export function parseWatch(spec: string): WatchInfo {
  const i = spec.indexOf(":");
  const kind = i > 0 ? spec.slice(0, i) : "value";
  let rest = i > 0 ? spec.slice(i + 1) : spec;
  let size: number | undefined, labels: string[] | undefined;
  const j = rest.lastIndexOf(":");
  if (j > 0 && (kind === "memory" || kind === "collection")) {
    const tail = rest.slice(j + 1);
    if (kind === "memory" && /^\d+$/.test(tail)) { size = Number(tail); rest = rest.slice(0, j); }
    else if (kind === "collection") { labels = tail.split(",").map((s) => s.trim()).filter(Boolean); rest = rest.slice(0, j); }
  }
  const path = rest.replace(/^GameController\./, "");
  return { kind, path, short: shortPath(path), size, labels };
}

/** The last two segments of a path, three when the last one is an indexer or a one-word getter. */
export function shortPath(path: string): string {
  const parts = path.replace(/^GameController\./, "").split(".");
  if (parts.length <= 2) return parts.join(".");
  const tail = parts.slice(-2);
  const third = parts[parts.length - 3];
  return /\[\d+\]$/.test(tail[0]) || /\(\)$/.test(tail[0]) || /\[\d+\]$/.test(third) ? parts.slice(-3).join(".") : tail.join(".");
}

/** A change key as the server builds it ("spec key", "spec item X +off Field") -> the part after the spec. */
export function changeName(c: ExperimentChange): string {
  const w = parseWatch(c.watch);
  if (c.kind === "bytes") return `${w.short} +${c.off ?? "?"}${c.field && c.field !== "(unmapped)" ? ` ${c.field}` : ""}${c.item ? ` · ${itemLabel(c.item)}` : ""}`;
  if (c.kind === "moved") return `${w.short} re-created`;
  const key = c.key.startsWith(c.watch) ? c.key.slice(c.watch.length).trim() : c.key;
  // "value" is the walker's name for a scalar leaf: the path already says what it is.
  const leaf = key === "value" ? "" : key.replace(/\s+value$/, "");
  if (c.kind === "label") return `${w.short}${c.item ? ` · ${itemLabel(c.item)}` : ""}${leaf ? ` ${leaf}` : ""}`;
  return leaf ? `${w.short} ${leaf}` : w.short;
}

/** "Name#0" -> "Name". */
export function itemLabel(item: string): string {
  return item.replace(/#\d+$/, "");
}

export interface ChangeView {
  name: string;
  /** The full path for a tooltip. */
  full: string;
  kind: ExperimentChange["kind"];
  from: string;
  to: string;
  /** Plain-language reading: "went up by 1", "bits 0, 3 flipped", "unmapped bytes". */
  note?: string;
  unmapped?: boolean;
  bits?: number[];
  /** Numeric delta when both sides are numbers. */
  delta?: number;
}

/** One change in plain language. */
export function describeChange(c: ExperimentChange): ChangeView {
  const w = parseWatch(c.watch);
  const name = changeName(c);
  const full = `${w.kind}:${w.path}${c.off !== undefined ? ` +${c.off}` : ""}${c.item ? ` item ${c.item}` : ""}`;
  if (c.kind === "bytes") {
    const bits = c.bitsFlipped ?? undefined;
    const unmapped = c.field === "(unmapped)";
    const note = bits?.length ? `bit${bits.length === 1 ? "" : "s"} ${bits.join(", ")} flipped${unmapped ? " in unmapped bytes" : ""}` : unmapped ? `${c.size ?? 1} unmapped byte${c.size === 1 ? "" : "s"}` : `${c.size ?? 1} byte${c.size === 1 ? "" : "s"}`;
    return { name, full, kind: c.kind, from: c.from, to: c.to, note, unmapped, bits };
  }
  if (c.kind === "moved") return { name, full, kind: c.kind, from: c.from, to: c.to, note: "the object moved: a new one was created, so its bytes aren't comparable" };
  const a = Number(c.from), b = Number(c.to);
  if (c.from !== "(absent)" && c.to !== "(absent)" && Number.isFinite(a) && Number.isFinite(b) && /^-?\d+(\.\d+)?$/.test(c.from) && /^-?\d+(\.\d+)?$/.test(c.to)) {
    const d = b - a;
    const note = d === 0 ? undefined : b === 0 ? "dropped to 0" : a === 0 ? "was 0" : Math.abs(d) === 1 ? (d > 0 ? "up by 1" : "down by 1") : `${d > 0 ? "up" : "down"} by ${Math.abs(d)}`;
    return { name, full, kind: c.kind, from: c.from, to: c.to, note, delta: d };
  }
  if (c.from === "(absent)") return { name, full, kind: c.kind, from: c.from, to: c.to, note: "appeared" };
  if (c.to === "(absent)") return { name, full, kind: c.kind, from: c.from, to: c.to, note: "disappeared" };
  return { name, full, kind: c.kind, from: c.from, to: c.to };
}

/** The one-line reading of a capture: "Changed 14.7 s after the card appeared: 2 values." */
export function captureHeadline(r: AwaitResult): string {
  if (!r.changed) return `Nothing lasting changed${r.transientChanges ? ` (${r.transientChanges} brief change${r.transientChanges === 1 ? "" : "s"} reverted)` : ""}.`;
  const kinds = countKinds(r.changes);
  const parts: string[] = [];
  if (kinds.value) parts.push(`${kinds.value} value${kinds.value === 1 ? "" : "s"}`);
  if (kinds.bytes) parts.push(`${kinds.bytes} byte range${kinds.bytes === 1 ? "" : "s"}`);
  if (kinds.label) parts.push(`${kinds.label} item label${kinds.label === 1 ? "" : "s"}`);
  if (kinds.moved) parts.push(`${kinds.moved} object${kinds.moved === 1 ? "" : "s"} re-created`);
  return `Changed ${fmtSecs(r.changedAfterMs)} after the card appeared: ${parts.join(", ") || "nothing readable"}.`;
}

export function countKinds(changes: ExperimentChange[]): Record<string, number> {
  const n: Record<string, number> = {};
  for (const c of changes) n[c.kind] = (n[c.kind] ?? 0) + 1;
  return n;
}

export function fmtSecs(ms: number): string {
  return ms < 10_000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms / 1000)} s`;
}

/** Plain name for the in-game card's status, as the HUD shows it. */
export const GUIDE_LABEL: Record<GuideStatus, string> = {
  idle: "", waiting: "Do this now", detected: "Change seen", settling: "Holding still", captured: "Captured", failed: "Try again", info: "Note", done: "Done",
};

/** The one-line meaning under the instruction (same wording as the HUD's card). */
export function guideSubline(status: GuideStatus | string, detail?: string | null): string | undefined {
  switch (status) {
    case "detected": return "Change seen… hold still while it settles";
    case "settling": return "Still changing… keep holding still";
    case "captured": return detail ?? "Recorded.";
    case "failed": return detail ?? "Nothing lasting changed. Do it once more.";
    default: return undefined;
  }
}

/** "Experiment: stash-ctrl-click" -> "stash-ctrl-click". */
export function guideExperiment(g: GuideState | undefined): string | undefined {
  const m = g?.title && /^Experiment:\s*(.+)$/.exec(g.title);
  return m ? m[1].trim() : undefined;
}

// ── Evidence ─────────────────────────────────────────────────────────

export interface EvidenceKey {
  name: string;
  full: string;
  /** For "sometimes": seen / repeats. */
  seen?: number;
  of?: number;
  unmapped?: boolean;
}

export interface EvidenceRow {
  label: string;
  instruction?: string;
  repeats: number;
  always: EvidenceKey[];
  sometimes: EvidenceKey[];
  /** The step order from the preset (unknown labels go last). */
  order: number;
}

/** "value:GameController.X.Y value (1/2)" -> name + counts. */
export function parseEvidenceKey(key: string): EvidenceKey {
  const m = /^(.*?)(?: \((\d+)\/(\d+)\))?$/.exec(key)!;
  const raw = m[1];
  // Rebuild a change from the key: the spec is everything up to the first space.
  const sp = raw.indexOf(" ");
  const watch = sp > 0 ? raw.slice(0, sp) : raw;
  const rest = sp > 0 ? raw.slice(sp + 1) : "";
  const w = parseWatch(watch);
  let name: string;
  let unmapped = false;
  const bytes = /^(?:item (\S+) )?\+(\d+) (.+)$/.exec(rest);
  if (bytes) { unmapped = bytes[3] === "(unmapped)"; name = `${w.short} +${bytes[2]}${unmapped ? "" : ` ${bytes[3]}`}${bytes[1] ? ` · ${itemLabel(bytes[1])}` : ""}`; }
  else if (rest === "object moved (re-created)") name = `${w.short} re-created`;
  else name = rest && rest !== "value" ? `${w.short} ${rest.replace(/\s+value$/, "")}` : w.short;
  return { name, full: raw, seen: m[2] ? Number(m[2]) : undefined, of: m[3] ? Number(m[3]) : undefined, unmapped };
}

export function evidenceRows(summary: SummaryResult | undefined, preset: ExperimentPreset | undefined): EvidenceRow[] {
  if (!summary) return [];
  const order = new Map((preset?.steps ?? []).map((s, i) => [s.label, i]));
  return summary.labels.map((l) => ({
    label: l.label,
    instruction: preset?.steps.find((s) => s.label === l.label)?.instruction,
    repeats: l.repeats,
    always: l.consistent.always.map(parseEvidenceKey),
    sometimes: l.consistent.sometimes.map(parseEvidenceKey),
    order: order.get(l.label) ?? 99,
  })).sort((a, b) => a.order - b.order || a.label.localeCompare(b.label));
}

/** Consistent keys from a capture (repeats >= 2) as evidence keys. */
export function consistentKeys(c: Consistent | undefined): { always: EvidenceKey[]; sometimes: EvidenceKey[] } {
  return { always: (c?.always ?? []).map(parseEvidenceKey), sometimes: (c?.sometimes ?? []).map(parseEvidenceKey) };
}

export function labelCounts(record: ExperimentRecord | undefined): Map<string, number> {
  const m = new Map<string, number>();
  for (const s of record?.steps ?? []) m.set(s.label, (m.get(s.label) ?? 0) + 1);
  return m;
}

/** The repeat counter the step list shows: how many times each preset step was captured. */
export function repeatsOf(label: string, record: { steps: RecordStep[] } | undefined, local: RecordStep[] = []): number {
  return (record?.steps ?? []).filter((s) => s.label === label).length + local.filter((s) => s.label === label).length;
}

// ── Undo ─────────────────────────────────────────────────────────────

export interface UndoItem { text: string; count?: number }

/**
 * What the user has left to undo: preset steps come in action/undo pairs (to-inventory / to-stash, next-tab /
 * prev-tab); when one of a pair was captured more often than the other, the difference is still applied in game.
 * Unknown shapes get a generic reminder.
 */
export function undoList(preset: ExperimentPreset | undefined, counts: Map<string, number>): UndoItem[] {
  const out: UndoItem[] = [];
  const steps = preset?.steps ?? [];
  for (let i = 0; i + 1 < steps.length; i += 2) {
    const a = steps[i], b = steps[i + 1];
    const na = counts.get(a.label) ?? 0, nb = counts.get(b.label) ?? 0;
    if (na > nb) out.push({ text: b.instruction, count: na - nb });
    else if (nb > na) out.push({ text: a.instruction, count: nb - na });
  }
  if (steps.length % 2 === 1) {
    const last = steps[steps.length - 1];
    if ((counts.get(last.label) ?? 0) > 0) out.push({ text: `Undo "${last.label}" if it changed something lasting.` });
  }
  return out;
}

/** The in-game card as the runner mirrors it, from a guide state or from the app's own run. */
export interface CardModel {
  status: GuideStatus;
  title?: string;
  instruction?: string;
  step?: number;
  steps?: number;
  detail?: string;
  /** When the current instruction appeared (for the elapsed / countdown). */
  since?: number;
  /** The timeout the countdown runs against, when the app started the step. */
  timeoutMs?: number;
}

export function cardFromGuide(g: GuideState, since?: number): CardModel {
  const status = (["idle", "waiting", "detected", "settling", "captured", "failed", "info", "done"] as GuideStatus[]).includes(g.status as GuideStatus) ? (g.status as GuideStatus) : "info";
  return { status, title: g.title ?? undefined, instruction: g.instruction ?? undefined, step: g.step ?? undefined, steps: g.steps ?? undefined, detail: g.detail ?? undefined, since };
}

/** True when the guide's card belongs to an experiment this app isn't running itself. */
export function isAgentRun(g: GuideState | undefined, ownExperiment: string | undefined, ownBusy: boolean): boolean {
  if (!g || ownBusy) return false;
  const exp = guideExperiment(g);
  if (!exp) return false;
  return g.status === "waiting" || g.status === "detected" || g.status === "settling" || (exp !== ownExperiment && (g.status === "captured" || g.status === "failed"));
}
