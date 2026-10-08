// Shapes of the explore_object / show_data_explorer structuredContent (mirrors Tools/ExploreTools.cs
// and the bridge's object.explore). Optional fields are omitted when empty, never faked.

export type Game = "poe1" | "poe2";

export type NodeKind =
  | "null" | "bool" | "number" | "string" | "enum" | "struct" | "object"
  | "list" | "dictionary" | "sequence" | "blocked" | "component";

/** One addressable value in the HUD object model. */
export interface ExploreNode {
  /** Walker path, e.g. "GameController.Player.GetComponent<Life>()". Absent on children the walker cannot address. */
  path?: string;
  /** Null-safe C# for a plugin, e.g. "GameController?.Player?.GetComponent<Life>()". */
  csharp?: string;
  /** Friendly runtime type, e.g. "Dictionary<GameStat, Int32>". */
  type?: string;
  /** Declared member type when it differs from the runtime type (e.g. "Element" for a NpcDialog). */
  declaredType?: string;
  /** For the `using` a plugin needs. Only on nodes returned as the root of a call. */
  namespace?: string;
  kind: NodeKind;
  /** One line: numbers, "\"text\"", "X=1 Y=2" for structs, "Entity RenderName=\"Mercenary\"" / "Element hidden", "Count = 271". */
  preview?: string;
  /** Lists and dictionaries. */
  count?: number;
  expandable?: boolean;
  /** Object members (sorted by name), list items "[i]", dictionary entries "[Key]". */
  children?: ExploreChild[];
  /** Entities: GetComponent<T>() paths. No path = no HUD wrapper type for that component. */
  components?: ExploreComponent[];
  /** Collections: the window of items in `children`. */
  page?: { offset: number; limit: number; total?: number };
  /** Members not read because the bridge's time budget ran out. Explore each member's path to load it. */
  skipped?: { reason: string; members: string[] };
  note?: string;
  /** Set by explore_object with depth > 1 when its lookup budget ran out. */
  truncated?: string;
  elapsedMs?: number;
}

export interface ExploreChild extends ExploreNode {
  name: string;
  /** Getter took this long (>= 5 ms): expanding it costs the game thread. */
  slowMs?: number;
  /** The getter threw. */
  error?: string;
  /** depth > 1 only: why a nested expansion was skipped or failed. */
  notExpanded?: string;
  expandError?: string;
}

export interface ExploreComponent {
  name: string;
  kind: "component";
  path?: string;
  csharp?: string;
  expandable?: boolean;
  note?: string;
}

/** Error result of explore_object (isError): {error, message?}. */
export interface ExploreError {
  error: string;
  message?: string;
}

/** watch_object result. Leaf paths are relative to the watched object ("Health.Current", "[3].Name"). */
export interface WatchResult {
  samples?: number;
  changes?: WatchChange[];
  note?: string;
  error?: string;
  message?: string;
}

export interface WatchChange {
  path: string;
  changes: number;
  first?: unknown;
  last?: unknown;
  firstChangeAtMs?: number;
  lastChangeAtMs?: number;
  noisy?: boolean;
}
