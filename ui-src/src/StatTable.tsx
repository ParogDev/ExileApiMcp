import { memo, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { DELTA_VISIBLE_MS, EmptyState, FLASH_MS, IconButton, PinButton, Skeleton } from "./components";
import { CATEGORY_DOT, CATEGORY_LABEL, cleanText, fmtStat, signed, statLabel } from "./format";
import { Icon } from "./icons";
import { countByCategory, filterStats, sortStats, type RemoteChange, type StatChange } from "./sync";
import type { Category, CategoryFilter, SortBy, StatItem, ViewState } from "./types";

const FILTERS: CategoryFilter[] = ["all", "vitals", "resistances", "defense", "offense", "charges", "movement", "other"];
const COLS = "grid-cols-[1.75rem_minmax(0,1fr)_auto_2.25rem]";

export interface StatTableProps {
  stats: StatItem[];
  loaded: boolean;
  view: ViewState;
  changes: Record<string, StatChange>;
  now: number;
  remote?: RemoteChange;
  selectionSource: "local" | "remote";
  /** Fill the parent (fullscreen) instead of capping the list height. */
  fill?: boolean;
  maxHeight?: string;
  onFilter: (text: string | undefined, category?: CategoryFilter) => void;
  onSort: (sortBy: SortBy, desc: boolean) => void;
  onPin: (key: string, pinned: boolean) => void;
  onSelect: (key: string | null) => void;
  /** Detail for the selected stat, shown as a sheet over the bottom of the list (inline mode). */
  sheet?: ReactNode;
}

export function StatTable({ stats, loaded, view, changes, now, remote, selectionSource, fill, maxHeight = "17rem", onFilter, onSort, onPin, onSelect, sheet }: StatTableProps) {
  // The search box filters locally on every keystroke and syncs to the shared view after a pause,
  // so typing stays instant while the HUD panel (and agents) still see what the user searched.
  const [draft, setDraft] = useState(view.filter);
  const typing = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => {
    if (!typing.current) setDraft(view.filter);
  }, [view.filter]);
  const changeDraft = (text: string) => {
    setDraft(text);
    clearTimeout(typing.current);
    typing.current = setTimeout(() => {
      typing.current = undefined;
      onFilter(text);
    }, 350);
  };

  // Local-only view preferences (not part of the shared state).
  const [keysMode, setKeysMode] = useState(() => readPref("keys") === "1");
  const [onlyChanged, setOnlyChanged] = useState(false);
  const toggleKeys = () => setKeysMode((k) => { writePref("keys", k ? "0" : "1"); return !k; });

  const pinned = useMemo(() => new Set(view.pinnedStatKeys), [view.pinnedStatKeys]);
  const counts = useMemo(() => countByCategory(filterStats(stats, draft, "all")), [stats, draft]);
  const changedCount = useMemo(() => stats.reduce((n, s) => n + (changes[s.key] ? 1 : 0), 0), [stats, changes]);
  const rows = useMemo(() => {
    const base = filterStats(stats, draft, view.category);
    return sortStats(onlyChanged ? base.filter((s) => changes[s.key]) : base, view.sortBy, view.sortDesc);
  }, [stats, draft, view.category, view.sortBy, view.sortDesc, onlyChanged, changes]);
  const groupCounts = useMemo(() => {
    const c: Partial<Record<Category, number>> = {};
    if (view.sortBy === "category") for (const r of rows) c[r.category] = (c[r.category] ?? 0) + 1;
    return c;
  }, [rows, view.sortBy]);
  useEffect(() => { if (onlyChanged && changedCount === 0) setOnlyChanged(false); }, [onlyChanged, changedCount]);

  // Stable handlers so memoised rows don't re-render on every poll.
  const pinnedRef = useRef(pinned); pinnedRef.current = pinned;
  const selectedRef = useRef(view.selectedStatKey); selectedRef.current = view.selectedStatKey;
  const handlePin = useCallback((key: string) => onPin(key, !pinnedRef.current.has(key)), [onPin]);
  const handleSelect = useCallback((key: string) => onSelect(selectedRef.current === key ? null : key), [onSelect]);

  const search = useRef<HTMLInputElement>(null);
  useEffect(() => {
    // "/" focuses search (same convention as GitHub et al.).
    const onKey = (e: KeyboardEvent) => {
      const tag = (document.activeElement as HTMLElement | null)?.tagName;
      if (e.key === "/" && tag !== "INPUT" && tag !== "SELECT" && tag !== "TEXTAREA") {
        e.preventDefault();
        search.current?.focus();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const body = useRef<HTMLDivElement>(null);
  const selectedIndex = rows.findIndex((r) => r.key === view.selectedStatKey);
  useEffect(() => {
    // Keep the selection in view, including when the HUD or an agent selected it.
    if (selectedIndex < 0) return;
    body.current?.querySelector<HTMLElement>(`[data-key="${CSS.escape(rows[selectedIndex].key)}"]`)
      ?.scrollIntoView({ block: selectionSource === "remote" ? "center" : "nearest" });
  }, [view.selectedStatKey, selectedIndex, rows, selectionSource]);

  const onListKey = (e: React.KeyboardEvent) => {
    if (e.key === "ArrowDown" || e.key === "ArrowUp" || e.key === "Home" || e.key === "End") {
      e.preventDefault();
      if (!rows.length) return;
      const next = e.key === "Home" ? 0 : e.key === "End" ? rows.length - 1
        : Math.max(0, Math.min(rows.length - 1, (selectedIndex < 0 ? (e.key === "ArrowDown" ? -1 : rows.length) : selectedIndex) + (e.key === "ArrowDown" ? 1 : -1)));
      onSelect(rows[next].key);
    } else if (e.key === "Enter" && view.selectedStatKey) {
      e.preventDefault();
      handlePin(view.selectedStatKey);
    } else if (e.key === "Escape") {
      if (view.selectedStatKey) { e.preventDefault(); onSelect(null); }
    } else if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey && e.key !== "/" && e.key !== " ") {
      // Typing while browsing the list starts a search.
      e.preventDefault();
      changeDraft(draft + e.key);
      search.current?.focus();
    }
  };

  // A plain helper, not a component: a component defined inside render would remount (and lose
  // focus) on every poll-driven re-render.
  const sortHeader = (label: string, by: SortBy, right?: boolean, children?: ReactNode) => {
    const active = view.sortBy === by;
    return (
      <button
        type="button"
        onClick={() => onSort(by, active ? !view.sortDesc : by === "value")}
        aria-sort={active ? (view.sortDesc ? "descending" : "ascending") : "none"}
        title={`Sort by ${label.toLowerCase()} (shared with the HUD)`}
        className={`flex min-w-0 items-center gap-1 rounded hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${right ? "justify-end" : ""} ${active ? "text-fg" : ""}`}
      >
        {label}
        {active && <Icon name={view.sortDesc ? "down" : "up"} className="size-3" />}
        {children}
      </button>
    );
  };

  const pulseKey = selectionSource === "remote" && remote && now - remote.at < 2500 && remote.fields.includes("selection") ? view.selectedStatKey : undefined;
  let lastCategory: Category | undefined;

  return (
    <section
      aria-label="All stats"
      className={`flex flex-col overflow-hidden rounded-lg border border-line bg-surface ${fill ? "min-h-0 flex-1" : ""}`}
    >
      <div className="flex items-center gap-1.5 border-b border-line bg-surface-2 px-2 py-1.5">
        <div className="relative min-w-0 flex-1">
          <Icon name="search" className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-fg-3" />
          <input
            ref={search}
            type="search"
            value={draft}
            onChange={(e) => changeDraft(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Escape" && draft) { e.preventDefault(); changeDraft(""); } }}
            placeholder={stats.length ? `Search ${stats.length} stats` : "Search stats"}
            aria-label="Search stats by key or in-game text"
            className="h-7 w-full rounded-md border border-transparent bg-surface pl-7 pr-7 text-[12px] placeholder:text-fg-3 focus:border-ring focus:outline-none [&::-webkit-search-cancel-button]:hidden"
          />
          {draft && <IconButton icon="x" label="Clear search" size="sm" onClick={() => changeDraft("")} className="absolute right-0.5 top-1/2 -translate-y-1/2" />}
          <kbd className={`pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 rounded border border-line px-1 font-code text-[10px] text-fg-3 ${draft ? "hidden" : "hidden xs:block"}`}>/</kbd>
        </div>
        <div className="relative shrink-0">
          <select
            value={view.category}
            onChange={(e) => onFilter(undefined, e.target.value as CategoryFilter)}
            aria-label="Category"
            title="Category (shared with the HUD)"
            className={`h-7 appearance-none rounded-md border border-transparent bg-surface pl-2 pr-6 text-[12px] hover:border-line-2 focus:border-ring focus:outline-none ${view.category === "all" ? "text-fg-2" : "text-fg"}`}
          >
            {FILTERS.map((c) => <option key={c} value={c}>{CATEGORY_LABEL[c]} · {counts[c] ?? 0}</option>)}
          </select>
          <Icon name="chevron" className="pointer-events-none absolute right-1.5 top-1/2 size-3.5 -translate-y-1/2 text-fg-3" />
        </div>
        {changedCount > 0 && (
          <button
            type="button"
            onClick={() => setOnlyChanged((v) => !v)}
            aria-pressed={onlyChanged}
            title="Only stats whose value changed since the panel opened"
            className={`flex h-7 shrink-0 items-center gap-1 rounded-md border px-1.5 text-[11px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${onlyChanged ? "border-fg bg-fg text-surface" : "border-transparent bg-surface text-fg-2 hover:border-line-2 hover:text-fg"}`}
          >
            <Icon name="activity" className="size-3.5" /><span className="tnum">{changedCount}</span>
          </button>
        )}
        <IconButton icon="code" label={keysMode ? "Show in-game text" : "Show raw stat keys"} active={keysMode} onClick={toggleKeys} className={keysMode ? "" : "bg-surface"} />
      </div>

      <div className={`grid ${COLS} items-center gap-2 border-b border-line px-1 py-1 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3`}>
        <span className="grid place-items-center">
          {view.sortBy !== "category" && <IconButton icon="layers" label="Group by category" size="sm" onClick={() => onSort("category", false)} />}
        </span>
        {sortHeader("Stat", "key", false, (
          <span className="tnum ml-1 font-normal normal-case tracking-normal text-fg-3">
            {loaded ? rows.length : "…"}{loaded && rows.length !== stats.length ? ` of ${stats.length}` : ""}
          </span>
        ))}
        {sortHeader("Value", "value", true)}
        <span className="text-right" title="Change in the last 30 s">Δ</span>
      </div>

      {/* While a detail sheet is open the list grows a little so rows stay visible above it. */}
      <div className={`relative flex flex-col ${fill ? "min-h-0 flex-1" : ""}`} style={fill ? undefined : { height: sheet ? `calc(${maxHeight} + 3.5rem)` : undefined }}>
        <div
          ref={body}
          role="listbox"
          aria-label="Stats"
          aria-activedescendant={view.selectedStatKey && selectedIndex >= 0 ? rowId(view.selectedStatKey) : undefined}
          tabIndex={0}
          onKeyDown={onListKey}
          className={`scroll-thin overflow-y-auto outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring ${fill ? "min-h-0 flex-1" : ""}`}
          style={{ maxHeight: fill ? undefined : sheet ? `calc(${maxHeight} + 3.5rem)` : maxHeight, scrollPaddingBottom: sheet ? "65%" : undefined, scrollPaddingTop: "1.5rem" }}
        >
          {!loaded && rows.length === 0 && <Skeleton kind="rows" count={7} />}
          {loaded && stats.length === 0 && (
            <EmptyState icon="gamepad" title="No stats reported yet">
              The character has no stats right now. They appear once you are in an area with a loaded character.
            </EmptyState>
          )}
          {loaded && stats.length > 0 && rows.length === 0 && (
            <EmptyState icon="search" title={onlyChanged ? "No changed stats match" : "No stats match"}>
              {draft && <>“{draft}”</>}{view.category !== "all" && <> in {CATEGORY_LABEL[view.category]}</>}
              <div className="mt-1.5">
                <button type="button" className="rounded-md border border-line-2 px-2 py-0.5 text-[11px] font-medium text-fg hover:bg-surface-3"
                  onClick={() => { changeDraft(""); setOnlyChanged(false); onFilter("", "all"); }}>
                  Clear filters
                </button>
              </div>
            </EmptyState>
          )}
          {rows.map((s) => {
            const header = view.sortBy === "category" && s.category !== lastCategory ? s.category : undefined;
            lastCategory = s.category;
            const ch = changes[s.key];
            const age = ch ? now - ch.at : Infinity;
            return (
              <div key={s.key}>
                {header && (
                  <div className="sticky top-0 z-[1] flex items-center gap-1.5 border-b border-line bg-surface/95 px-2 py-1 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3 backdrop-blur">
                    <span className={`size-1.5 rounded-full ${CATEGORY_DOT[s.category]}`} aria-hidden />
                    {CATEGORY_LABEL[s.category]}
                    <span className="tnum font-normal">{groupCounts[s.category]}</span>
                  </div>
                )}
                <Row
                  s={s}
                  selected={view.selectedStatKey === s.key}
                  pinned={pinned.has(s.key)}
                  keysMode={keysMode}
                  flash={age < FLASH_MS ? (ch.delta > 0 ? "flash-up" : "flash-down") : ""}
                  flashAt={age < FLASH_MS ? ch.at : 0}
                  delta={age < DELTA_VISIBLE_MS ? ch.delta : undefined}
                  pulse={pulseKey === s.key}
                  onPin={handlePin}
                  onSelect={handleSelect}
                />
              </div>
            );
          })}
        </div>
        {sheet && (
          <div className="slide-up scroll-thin absolute inset-x-0 bottom-0 z-[2] max-h-[78%] overflow-y-auto border-t border-line bg-surface shadow-[0_-10px_24px_-14px_rgb(0_0_0/0.45)]">
            {sheet}
          </div>
        )}
      </div>
    </section>
  );
}

const Row = memo(function Row({ s, selected, pinned, keysMode, flash, flashAt, delta, pulse, onPin, onSelect }: {
  s: StatItem; selected: boolean; pinned: boolean; keysMode: boolean; flash: string; flashAt: number; delta?: number; pulse: boolean;
  onPin: (key: string) => void; onSelect: (key: string) => void;
}) {
  const text = cleanText(s.text);
  const label = statLabel(s);
  return (
    <div
      id={rowId(s.key)}
      data-key={s.key}
      role="option"
      aria-selected={selected}
      onClick={() => onSelect(s.key)}
      title={keysMode ? label : s.key}
      className={`group grid h-7 cursor-pointer ${COLS} items-center gap-2 px-1 text-[12.5px] ${selected ? "bg-ring/10 shadow-[inset_2px_0_0_var(--color-ring)]" : "hover:bg-surface-2"} ${pulse ? "pulse-ring" : ""}`}
    >
      <PinButton pinned={pinned} onToggle={() => onPin(s.key)} label={s.key} size="sm"
        className={`justify-self-center ${pinned ? "" : "opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 focus-visible:opacity-100"}`} />
      <div className="flex min-w-0 items-baseline gap-2">
        {keysMode ? (
          <>
            <span className="min-w-0 shrink truncate font-code text-[11.5px]">{s.key}</span>
            {text && <span className="hidden min-w-0 flex-1 truncate text-[11px] text-fg-3 sm:inline">{text}</span>}
          </>
        ) : (
          <>
            <span className="min-w-0 shrink truncate">{label}</span>
            {text && <span className="hidden min-w-0 flex-1 truncate font-code text-[10.5px] text-fg-3 sm:inline">{s.key}</span>}
          </>
        )}
      </div>
      <span key={flashAt} className={`tnum rounded px-1 text-right font-semibold ${s.value < 0 ? "text-danger" : ""} ${flash}`}>{fmtStat(s.key, s.value)}</span>
      <span className={`tnum text-right text-[10.5px] ${delta === undefined ? "invisible" : delta > 0 ? "text-success" : "text-danger"}`}>
        {delta === undefined ? "·" : signed(delta)}
      </span>
    </div>
  );
});

function rowId(key: string): string {
  return `stat-${key}`;
}

function readPref(name: string): string | null {
  try { return localStorage.getItem(`player-stats.${name}`); } catch { return null; }
}
function writePref(name: string, value: string) {
  try { localStorage.setItem(`player-stats.${name}`, value); } catch { /* storage blocked in this sandbox */ }
}
