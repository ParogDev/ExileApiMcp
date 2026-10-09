import type { IconName } from "./icons";
import type { Category, CategoryFilter, StatItem } from "./types";

export const CATEGORY_LABEL: Record<CategoryFilter, string> = {
  all: "All", vitals: "Vitals", resistances: "Resistances", defense: "Defense",
  offense: "Offense", charges: "Charges", movement: "Movement", other: "Other",
};

/** PoE2 stat text uses link markup "[Target|Label]" or "[Label]"; show just the label. */
export function cleanText(text: string | undefined): string | undefined {
  if (!text) return undefined;
  if (text.startsWith("<unknown ")) return undefined; // undescribed stat: the key says more
  return text.replace(/\[([^\]|]+)\|([^\]]+)\]/g, "$2").replace(/\[([^\]]+)\]/g, "$1");
}

/** Readable label for stats without in-game text: "base_fire_damage_resistance_%" -> "Base fire damage resistance". */
export function humanizeKey(key: string): string {
  // The trailing "%" is dropped: fmtStat puts the unit on the value instead.
  const s = key.replace(/_%$/, "").replace(/_/g, " ").replace(/\s+/g, " ").trim();
  return s.charAt(0).toUpperCase() + s.slice(1);
}

/** What a row is called: the in-game text when there is one, otherwise the humanised key. */
export function statLabel(s: { key: string; text?: string }): string {
  return cleanText(s.text) ?? humanizeKey(s.key);
}

export function isPercentKey(key: string): boolean {
  return key.endsWith("%") || key.includes("_%_");
}

/** Value with its unit when the key says it is a percentage. */
export function fmtStat(key: string, value: number): string {
  return isPercentKey(key) ? `${fmt(value)}%` : fmt(value);
}

export function fmt(n: number): string {
  return n.toLocaleString("en-US");
}

export function signed(n: number): string {
  return n > 0 ? `+${fmt(n)}` : fmt(n);
}

/** "just now", "12 s ago", "3 min ago". */
export function ago(ms: number): string {
  if (ms < 2_000) return "just now";
  if (ms < 60_000) return `${Math.round(ms / 1000)} s ago`;
  if (ms < 3_600_000) return `${Math.round(ms / 60_000)} min ago`;
  return `${Math.round(ms / 3_600_000)} h ago`;
}

export const ELEMENTS = ["fire", "cold", "lightning", "chaos"] as const;
export type Element = (typeof ELEMENTS)[number];

export const ELEMENT_META: Record<Element, { label: string; icon: IconName; text: string; bg: string }> = {
  fire: { label: "Fire", icon: "flame", text: "text-fire", bg: "bg-fire" },
  cold: { label: "Cold", icon: "snowflake", text: "text-cold", bg: "bg-cold" },
  lightning: { label: "Lightning", icon: "bolt", text: "text-lightning", bg: "bg-lightning" },
  chaos: { label: "Chaos", icon: "skull", text: "text-chaos", bg: "bg-chaos" },
};

export interface Resist {
  element: Element;
  value?: number;
  /** Before the cap; equals value when the game doesn't expose an uncapped stat. */
  uncapped?: number;
  cap: number;
  /** True when no max_*_resistance stat was present and the default cap is assumed. */
  capAssumed: boolean;
  key: string;
}

const DEFAULT_CAP = 75;

/** Resistances against their caps, from whatever resistance stats the player has. */
export function resists(stats: StatItem[]): Resist[] {
  const by = new Map(stats.map((s) => [s.key, s.value]));
  return ELEMENTS.map((element) => {
    const key = `${element}_damage_resistance_%`;
    const max = by.get(`maximum_${element}_damage_resistance_%`) ?? by.get(`max_${element}_damage_resistance_%`);
    const value = by.get(key) ?? by.get(`base_${key}`);
    const uncapped = by.get(`uncapped_${key}`) ?? value;
    return { element, key, value, uncapped, cap: max ?? DEFAULT_CAP, capAssumed: max === undefined };
  });
}

/** The element a resistance stat key belongs to, if it is one of the layered resistance stats. */
export function resistElementOf(key: string): Element | undefined {
  const m = /^(?:base_|uncapped_|maximum_|max_)?(fire|cold|lightning|chaos)_damage_resistance_%$/.exec(key);
  return m ? (m[1] as Element) : undefined;
}

export interface ResistLayer {
  label: string;
  key: string;
  value?: number;
  /** For the cap row: true when no stat backs it and 75% is assumed. */
  assumed?: boolean;
}

/** PoE2 exposes resistances in layers (base, total, uncapped, cap). All of them, present or not. */
export function resistLayers(stats: StatItem[], element: Element): ResistLayer[] {
  const by = new Map(stats.map((s) => [s.key, s.value]));
  const k = `${element}_damage_resistance_%`;
  const max = by.get(`maximum_${k}`) ?? by.get(`max_${k}`);
  return [
    { label: "Base", key: `base_${k}`, value: by.get(`base_${k}`) },
    { label: "Total", key: k, value: by.get(k) },
    { label: "Uncapped", key: `uncapped_${k}`, value: by.get(`uncapped_${k}`) },
    { label: "Cap", key: `maximum_${k}`, value: max ?? DEFAULT_CAP, assumed: max === undefined },
  ];
}

export const CATEGORY_DOT: Record<Category, string> = {
  vitals: "bg-life", resistances: "bg-fire", defense: "bg-es", offense: "bg-lightning",
  charges: "bg-chaos", movement: "bg-cold", other: "bg-fg-3",
};

/**
 * A toast message in caps, the way the brand book says: words become capitals, identifiers keep their case so
 * they stay recognisable (a word with an inner "_", ".", "/", ":", "[", "<", "{" or "#", a CamelCase bump, or a
 * 0x prefix: fire_damage_resistance_%, findings.json, GameController.Player, ReAgent, 0x3D). Trailing
 * punctuation does not count. The in-game toasts use the same rule (GuideCaps in the bridge).
 */
export function capsLine(text: string): string {
  return text.split(" ").map((w) => (isIdentifier(w.replace(/[.,;:!?)]+$/, "")) ? w : w.toUpperCase())).join(" ");
}

function isIdentifier(w: string): boolean {
  if (/^[+-]?0x/.test(w)) return true;
  if (/^.+[_./:\[<{#].+$/.test(w)) return true;
  return /[a-z][A-Z]/.test(w);
}
