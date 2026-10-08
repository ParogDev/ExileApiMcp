// Shapes of the ExileApiMcp stats tools' structuredContent (mirrors the bridge's stats.* DTOs).
// Optional fields are omitted by the bridge when empty, never faked.

export type Game = "poe1" | "poe2";

export const CATEGORIES = ["vitals", "resistances", "defense", "offense", "charges", "movement", "other"] as const;
export type Category = (typeof CATEGORIES)[number];
export type CategoryFilter = Category | "all";

export type SortBy = "category" | "key" | "value";

/** The shared view state owned by the HUD plugin. Every change bumps rev. */
export interface ViewState {
  rev: number;
  pinnedStatKeys: string[];
  filter: string;
  category: CategoryFilter;
  sortBy: SortBy;
  sortDesc: boolean;
  panelOpen: boolean;
  selectedStatKey?: string | null;
}

export interface Vitals {
  hp: number;
  maxHp: number;
  es: number;
  maxEs: number;
  mana: number;
  maxMana: number;
  /** PoE2 only: active weapon set index (0/1). */
  weaponSet?: number;
}

/** stats_ui_state: full state, or {unchanged:true} when sinceRev is current. */
export interface UiStateResult {
  game?: Game;
  rev: number;
  unchanged?: boolean;
  state?: ViewState;
  vitals?: Vitals;
  inGame?: boolean;
}

export interface StatItem {
  id: number;
  key: string;
  value: number;
  /** Translated in-game text; absent for stats without a description. */
  text?: string;
  category: Category;
  pinned?: boolean;
}

export interface StatsPageResult {
  game?: Game;
  rev: number;
  total: number;
  page: number;
  pageSize: number;
  items: StatItem[];
  categories: Partial<Record<Category, number>>;
  pinned: StatItem[];
}

export interface GetStatResult {
  game?: Game;
  stat: StatItem;
  recordType?: string;
  isWeaponLocal?: boolean;
  present: boolean;
}

/** Every mutator returns the new state (ok:false + error code when nothing was applied). */
export interface MutationResult {
  ok: boolean;
  rev: number;
  state: ViewState;
  error?: string;
  message?: string;
}

/** show_player_stats structuredContent: what the app gets first, before any polling. */
export interface ShowResult {
  game?: Game;
  rev?: number;
  inGame?: boolean;
  vitals?: Vitals;
  state?: ViewState;
  pinned?: StatItem[];
  resistances?: StatItem[];
  categories?: Partial<Record<Category, number>>;
}
