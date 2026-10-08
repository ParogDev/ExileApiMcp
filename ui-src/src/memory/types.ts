// Shapes of the memory_layout / memory_read / memory_where / watch_memory structuredContent (mirrors
// Tools/MemoryTools.cs and the bridge's MemoryInspector). Optional fields are omitted when empty, never faked.
// Addresses are hex strings ("0x..."); `ghidra` is the address to give a Ghidra user (imageBase + rva).

export type Game = "poe1" | "poe2";

export type Check = "ok" | "unusual" | "suspicious" | "invalid" | "unread";

/** What an 8-byte value is, from the bridge's classifier. */
export type SlotKind = "zero" | "module" | "vtable" | "heap" | "float" | "int" | "text" | "bad-pointer";

/** Classification details shared by pointer-like fields, candidates and read slots. */
export interface Classified {
  kind?: SlotKind | string;
  /** Module pointers: section and relative address, and the Ghidra address (imageBase + rva). */
  module?: string;
  section?: string;
  rva?: string;
  ghidra?: string;
  /** vtable with RTTI: the class name. */
  rtti?: string;
  /** First virtual method of a vtable (Ghidra address). */
  firstMethod?: string;
  /** Heap pointers: what the target looks like, "object (vtable 0x...)" or "text". */
  points?: string;
  text?: string;
}

export interface LayoutField extends Classified {
  off: number;
  size: number;
  /** Dotted for nested structs: "Health.Max". */
  name: string;
  /** .NET type name: Int32, Single, Int64, Byte, UInt16... */
  type: string;
  /** "AA BB CC DD" */
  bytes: string;
  value?: unknown;
  check: Check;
  why?: string;
  /** Set bits for flag-like integer fields. */
  bits?: number[];
}

export interface Gap { off: number; size: number }

export type CandidateKind = "std::vector" | "vtable" | "module" | "heap" | "pointer" | "self" | "text";

/** Structure-looking slot inside an unmapped range. */
export interface Candidate extends Classified {
  off: number;
  size: number;
  kind: CandidateKind | string;
  value?: string;
  detail?: string;
  /** std::vector: the First pointer. */
  first?: string;
}

export interface HexRow { off: number; bytes: string; ascii: string }

export interface LayoutResult {
  address: string;
  struct: string;
  structSize: number;
  source?: string;
  object?: string;
  game?: Game;
  summary?: string;
  hex: HexRow[];
  fields: LayoutField[];
  gaps: Gap[];
  candidates: Candidate[];
}

export interface ReadSlot extends Classified {
  off: number;
  hex: string;
  kind: SlotKind;
  /** int / float / bad-pointer: the decoded value(s) as text. */
  value?: string;
  bits?: number[];
}

export interface ModuleInfo { name: string; base: string; size: string; imageBase: string; note?: string }

export interface ReadResult {
  address: string;
  size: number;
  origin?: string;
  region?: string;
  module?: ModuleInfo;
  slots: ReadSlot[];
  hex: HexRow[];
  game?: Game;
}

export interface WhereResult extends Classified {
  hex?: string;
  address: string;
  region?: string;
  moduleInfo?: ModuleInfo;
}

export interface ChangedRange {
  off: number;
  size: number;
  /** Overlapping struct field names, "(unmapped)" when a struct is known, absent for raw reads. */
  field?: string;
  changes: number;
  first: string;
  last: string;
  /**
   * Bits that flipped (absent when > 16). Numbered relative to the first byte of the field that covers the range
   * when there is exactly one ("field Affinity (+63)": bit 11 means 1 << 11 in Affinity), else relative to the range start.
   */
  bitsFlipped?: number[];
  /** "field Affinity (+63)" or "range start (+300)": what bitsFlipped counts from. */
  bitsRelativeTo?: string;
  firstChangeAtMs: number;
  lastChangeAtMs: number;
  noisy?: boolean;
}

export interface WatchResult {
  address: string;
  size: number;
  samples: number;
  durationMs: number;
  struct?: string;
  changedRanges: ChangedRange[];
  note?: string;
}

/** Error result (isError): bridge codes are unreadable | no_address | no_struct; walker errors come as messages. */
export interface MemoryError {
  error: string;
  message?: string;
}

// ── Probing: populations, correlations, snapshots ───────────────────

/** memory_population: the same byte range from every item of a collection. */
export interface PopulationResult {
  path: string;
  offset: number;
  size: number;
  struct?: string;
  count: number;
  items: PopulationItem[];
  truncated?: string;
}

export interface PopulationItem {
  index: number;
  address: string;
  labels?: Record<string, unknown>;
  /** "AA BB ..." */
  hex: string;
}

/** memory_correlate: which bits a known property explains, with counts and counterexamples. */
export interface CorrelateResult {
  path: string;
  offset: number;
  size: number;
  items: number;
  struct?: string;
  findings: LabelFinding[];
}

export interface LabelFinding {
  label: string;
  distinctValues: number;
  /** Where the label's own value is stored. */
  storedAt: { offset: number; type: string }[];
  /** Bits equal to a feature of the label with 0 counterexamples. */
  bitsExplained: ExplainedBit[];
  /** <= 2 counterexamples. */
  nearMisses?: ExplainedBit[];
  tooLittleEvidence?: string;
}

export interface ExplainedBit {
  /** Absolute offset of the byte (offset + i). */
  byte: number;
  bit: number;
  /** "Affinity != 0", "NOT TabType == 0", "Affinity bit 5", "a function of TabType". */
  equals: string;
  evidence?: string;
  /** Categorical: the label values the bit is set for. */
  setFor?: string;
  counterexamples: number;
  counterexampleItems?: { index: number; labels?: Record<string, unknown> }[];
}

/** memory_compare with no names. */
export interface SnapshotList { snapshots: { name: string; savedAt: string }[] }

/** memory_compare {names}. */
export interface CompareResult {
  snapshots: string[];
  steps: CompareStep[];
}

export interface CompareStep {
  from: string;
  to: string;
  changes: ItemChange[];
}

export interface ItemChange {
  /** "Name=19 [21]" for collections, the path for single objects. */
  item: string;
  bytes: ByteChangeStep[];
  /** Label -> "a -> b". */
  labels?: Record<string, string>;
}

export interface ByteChangeStep {
  off: number;
  from: string;
  to: string;
  /** Bits within the byte (0 = lowest) that went 0 -> 1 / 1 -> 0. */
  bitsOn: number[];
  bitsOff: number[];
}

/** memory_snapshot result. */
export interface SnapshotSaved { saved: string; kind: "collection" | "object"; what: string; path: string; takenAt: string }

// ── Findings ─────────────────────────────────────────────────────────

export type FindingStatus = "verified" | "differs" | "unverified" | "n/a";

export interface FindingGame {
  status: FindingStatus;
  date?: string;
  /** "+61 bit 6", "+63 (32-bit)", "+0x178 / +0x1C8 / +0x210", or a bit table "3 Currency, 4 Unique, ..." */
  where?: string;
  evidence?: string;
  note?: string;
}

export interface Finding {
  id: string;
  title: string;
  /** "ServerStashTab (ServerStashTabOffsets)", "ServerStashTab.Affinity", "Life (LifeComponentOffsets)". */
  subject: string;
  games: Partial<Record<Game, FindingGame>>;
  check: { kind: "correlate" | "stored" | "eval" | "manual" | string; path?: string; label?: string; expect?: string; how?: string; expression?: string; expressionByGame?: Partial<Record<Game, string>> };
}

export interface FindingsResult {
  about?: string;
  findings: Finding[];
  /** Verified on another game, unverified on the requested one. */
  toCheck?: string[];
}

export interface VerifyResult {
  id: string;
  game: Game;
  kind: string;
  recorded?: FindingGame;
  verdict?: "pass" | "moved" | "differs" | "fail";
  where?: string;
  evidence?: string;
  record?: FindingGame;
  next?: string;
  /** Manual checks: the experiment to run. */
  howToVerify?: string;
  correlate?: LabelFinding;
}

// ── Guided experiments (mirrors Tools/ExperimentTools.cs and Tools/GuideTools.cs) ───

/** One ready-made experiment from Knowledge/experiments.json. */
export interface ExperimentPreset {
  id: string;
  title: string;
  games: Game[];
  question: string;
  setup: string;
  steps: { label: string; instruction: string }[];
  watch: string[];
}

/** experiment_presets {game?}. */
export interface PresetsResult {
  presets: ExperimentPreset[];
  /** Experiment records on disk, newest first, with how many steps each holds. */
  records: ExperimentRecordInfo[];
}

export interface ExperimentRecordInfo { name: string; updated: string; steps?: number }

/** One change await_change saw between the baseline and the settled state. */
export interface ExperimentChange {
  /** The watch spec it belongs to ("value:GameController...."). */
  watch: string;
  kind: "value" | "bytes" | "label" | "moved" | string;
  /** "spec key" for values, "spec item X +off Field" for bytes. */
  key: string;
  /** Collection item ("Name#0") for label / bytes changes of a collection. */
  item?: string | null;
  off?: number;
  size?: number;
  /** HUD field name covering the bytes, "(unmapped)" when none. */
  field?: string;
  from: string;
  to: string;
  /** Flipped bits within the byte range (absent when > 16). */
  bitsFlipped?: number[] | null;
}

/** Change keys seen in every repeat of a label (evidence) vs only some ("key (1/2)"). */
export interface Consistent { repeats: number; always: string[]; sometimes: string[] }

/** await_change: changed:true with the diff, or changed:false after the timeout. */
export type AwaitResult =
  | { experiment: string; label: string; changed: true; step: number; repeatsOfThisLabel: number; changedAfterMs: number; changes: ExperimentChange[]; consistent?: Consistent; transientChangesIgnored?: number }
  | { experiment: string; label: string; changed: false; transientChanges: number; note: string };

/** One step of an experiment record on disk. */
export interface RecordStep {
  label: string;
  /** What the user was told to do (absent on records from before the field existed). */
  instruction?: string | null;
  at: string;
  game?: Game;
  changedAfterMs: number;
  watch: string[];
  changes: ExperimentChange[];
}

export interface ExperimentRecord { experiment: string; steps: RecordStep[] }

/** experiment_summary {experiment}. */
export interface SummaryResult {
  experiment: string;
  labels: { label: string; repeats: number; consistent: Consistent }[];
  record: ExperimentRecord;
}

// ── Non-blocking steps (experiment_step_start / experiment_status / experiment_step_cancel) ──

/** experiment_step_start: the step runs in the server; poll experiment_status. */
export interface StepStarted { started: true; experiment: string; label: string; startedAt: string; timeoutMs: number; next?: string }

/** starting -> waiting -> detected -> captured | failed | cancelled | error; stale when the server process died mid-step. */
export type StepStatus = "starting" | "waiting" | "detected" | "captured" | "failed" | "cancelled" | "error" | "stale";

/** The current or last step of an experiment, as experiment_status reports it (the server's .inflight.json). */
export interface StepState {
  experiment: string;
  label: string;
  instruction?: string | null;
  step?: number | null;
  steps?: number | null;
  /** ISO, server clock; `elapsedMs` (while running) is the server's own measure, so the countdown needn't trust clocks. */
  startedAt: string;
  timeoutMs: number;
  status: StepStatus | string;
  watch?: string[];
  updatedAt?: string;
  elapsedMs?: number;
  finishedAt?: string;
  /** The full await_change result once captured or failed. */
  result?: AwaitResult;
  error?: string;
}

/** experiment_status {experiment}. */
export interface StatusResult {
  experiment: string;
  running: boolean;
  recordedSteps: number;
  step?: StepState;
}

/** experiment_step_cancel {experiment}. */
export interface StepCancelled { experiment: string; cancelled: boolean; note?: string }

/** The in-game agent guide card (guide_state, or guide with no arguments, returns it). */
export type GuideStatus = "idle" | "waiting" | "detected" | "settling" | "captured" | "failed" | "info" | "done";

export interface GuideState {
  ok?: boolean;
  rev: number;
  title?: string | null;
  instruction?: string | null;
  step?: number | null;
  steps?: number | null;
  status: GuideStatus | string;
  detail?: string | null;
  log?: { at: string; kind: "step" | "result" | "warn" | "agent" | string; text: string }[];
}

// ── Code access (find_field_access: static analysis of the Ghidra copy of the exe) ──

export type AccessKind = "read" | "write" | "bit-test" | "set-bits" | "clear-bits" | "address-of";
export type Confidence = "high" | "medium" | "low";

/** One function of the struct that touches the target offset. */
export interface AccessFunction {
  /** "FUN_141d4d020", or "?" when Ghidra has no function at the address. */
  function: string;
  accesses: number;
  /** Comma-joined kinds: "read,write". */
  kinds: string;
  /** How many of the anchor fields the same base register also touches. */
  knownFields: number;
  bitMatch: boolean;
}

/** One instruction touching the target offset. */
export interface Access {
  function: string;
  /** Hex without 0x: "141d4d1bf". */
  address: string;
  /** "TEST byte ptr [RDI + 0x3d], 0x40" */
  instruction: string;
  kind: AccessKind | string;
  /** Bytes; 0 = unknown (address-of). */
  width: number;
  base: string;
  /** Bits a mask / BT touches (for clear-bits: the bits cleared). */
  bits?: number[];
  matchesBit?: boolean;
  /** "+63 Affinity" */
  knownFieldsAlsoAccessed: string[];
  confidence: Confidence;
}

export interface Decompiled {
  function: string;
  signature?: string;
  /** C-like pseudocode lines around the target offset, "  ..." between runs. */
  excerpt?: string;
  lineCount?: number;
  error?: string;
}

/** find_field_access structuredContent (mirrors Tools/CodeAccessTools.cs). */
export interface FieldAccessResult {
  game?: Game;
  program: string;
  struct?: string | null;
  path?: string | null;
  target: { offset: number; hex: string; bit?: number | null };
  /** Known fields used to fingerprint the struct's code; `skipped` ones were too common (50000+ uses) to identify anything. */
  anchors: { offset: number; field: string; accesses?: number; skipped?: string }[];
  minKnown: number;
  programWideAccesses: number;
  functions: AccessFunction[];
  accesses: Access[];
  decompiled: Decompiled[];
  unanchored?: string;
}
