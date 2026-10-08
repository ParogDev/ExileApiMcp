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

/** Readable label for stats without in-game text: "base_fire_damage_resistance_%" -> "Base fire damage resistance %". */
export function humanizeKey(key: string): string {
  // The trailing "%" is dropped: fmtStat puts the unit on the value instead.
  const s = key.replace(/_%$/, "").replace(/_/g, " ").replace(/\s+/g, " ").trim();
  return s.charAt(0).toUpperCase() + s.slice(1);
}

/** Value with its unit when the key says it is a percentage. */
export function fmtStat(key: string, value: number): string {
  return key.endsWith("%") || key.includes("_%_") ? `${fmt(value)}%` : fmt(value);
}

export function fmt(n: number): string {
  return n.toLocaleString("en-US");
}

export function signed(n: number): string {
  return n > 0 ? `+${fmt(n)}` : fmt(n);
}

export const ELEMENTS = ["fire", "cold", "lightning", "chaos"] as const;
export type Element = (typeof ELEMENTS)[number];

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

export const CATEGORY_DOT: Record<Category, string> = {
  vitals: "bg-life", resistances: "bg-fire", defense: "bg-es", offense: "bg-lightning",
  charges: "bg-chaos", movement: "bg-cold", other: "bg-fg-3",
};
