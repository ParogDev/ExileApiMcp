import { useEffect, useMemo, useRef, useState } from "react";
import { Card, StarButton } from "./components";
import { CATEGORY_DOT, CATEGORY_LABEL, cleanText, fmt, signed } from "./format";
import { countByCategory, filterStats, sortStats, type StatChange } from "./sync";
import type { CategoryFilter, SortBy, StatItem, ViewState } from "./types";

const FILTERS: CategoryFilter[] = ["all", "vitals", "resistances", "defense", "offense", "charges", "movement", "other"];
const DELTA_VISIBLE_MS = 30_000;
const FLASH_MS = 1_500;

export interface StatTableProps {
  stats: StatItem[];
  loaded: boolean;
  view: ViewState;
  changes: Record<string, StatChange>;
  maxHeight?: string;
  onFilter: (text: string | undefined, category?: CategoryFilter) => void;
  onSort: (sortBy: SortBy, desc: boolean) => void;
  onPin: (key: string, pinned: boolean) => void;
  onSelect: (key: string | null) => void;
  /** Rendered under the selected row. */
  renderDetail?: (stat: StatItem) => React.ReactNode;
}

export function StatTable({ stats, loaded, view, changes, maxHeight, onFilter, onSort, onPin, onSelect, renderDetail }: StatTableProps) {
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

  const pinned = useMemo(() => new Set(view.pinnedStatKeys), [view.pinnedStatKeys]);
  const counts = useMemo(() => countByCategory(filterStats(stats, draft, "all")), [stats, draft]);
  const rows = useMemo(
    () => sortStats(filterStats(stats, draft, view.category), view.sortBy, view.sortDesc),
    [stats, draft, view.category, view.sortBy, view.sortDesc],
  );

  const search = useRef<HTMLInputElement>(null);
  useEffect(() => {
    // "/" focuses search (same convention as GitHub et al.); Escape clears it.
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "/" && document.activeElement?.tagName !== "INPUT") {
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
    body.current?.querySelector<HTMLElement>(`[data-key="${CSS.escape(rows[selectedIndex].key)}"]`)?.scrollIntoView({ block: "nearest" });
  }, [view.selectedStatKey, selectedIndex, rows]);

  const onTableKey = (e: React.KeyboardEvent) => {
    if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
    e.preventDefault();
    const next = Math.max(0, Math.min(rows.length - 1, selectedIndex + (e.key === "ArrowDown" ? 1 : -1)));
    if (rows[next]) onSelect(rows[next].key);
  };

  const sortHeader = (label: string, by: SortBy, className: string) => {
    const active = view.sortBy === by;
    return (
      <button
        type="button"
        onClick={() => onSort(by, active ? !view.sortDesc : by === "value")}
        className={`flex items-center gap-1 hover:text-fg ${active ? "text-fg" : ""} ${className}`}
        aria-sort={active ? (view.sortDesc ? "descending" : "ascending") : "none"}
        title={`Sort by ${label.toLowerCase()} (shared with the HUD)`}
      >
        {label}
        <span className="text-[10px]" aria-hidden>{active ? (view.sortDesc ? "▼" : "▲") : ""}</span>
      </button>
    );
  };

  const now = Date.now();
  let lastCategory: string | undefined;

  return (
    <Card
      title={`Stats · ${loaded ? rows.length : "…"}${loaded && rows.length !== stats.length ? ` of ${stats.length}` : ""}`}
      right={view.sortBy !== "category" && (
        <button type="button" className="text-xs text-fg-3 hover:text-fg" onClick={() => onSort("category", false)}>Group by category</button>
      )}
    >
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <div className="relative min-w-40 flex-1">
          <input
            ref={search}
            type="search"
            value={draft}
            onChange={(e) => changeDraft(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Escape" && draft) { e.preventDefault(); changeDraft(""); } }}
            placeholder="Search key or text  ( / )"
            aria-label="Search stats"
            className="w-full rounded-md border border-line bg-surface px-2.5 py-1.5 text-sm placeholder:text-fg-3 focus:border-ring focus:outline-none"
          />
        </div>
      </div>
      <div className="mb-2 flex flex-wrap gap-1" role="tablist" aria-label="Category">
        {FILTERS.map((c) => {
          const active = view.category === c;
          return (
            <button key={c} type="button" role="tab" aria-selected={active}
              onClick={() => onFilter(undefined, c)}
              className={`rounded-full border px-2.5 py-0.5 text-xs transition-colors ${active ? "border-fg bg-fg text-surface" : "border-line text-fg-2 hover:border-line-2 hover:text-fg"}`}>
              {CATEGORY_LABEL[c]} <span className={`tnum ${active ? "opacity-70" : "text-fg-3"}`}>{counts[c] ?? 0}</span>
            </button>
          );
        })}
      </div>

      <div className="overflow-hidden rounded-md border border-line bg-surface">
        <div className="grid grid-cols-[1.75rem_minmax(0,1fr)_minmax(3rem,auto)_2.75rem] items-center gap-2 border-b border-line bg-surface-2 px-2 py-1.5 text-xs font-medium text-fg-2">
          <span />
          {sortHeader("Stat", "key", "")}
          {sortHeader("Value", "value", "justify-end")}
          <span className="text-right" title="Change since the panel opened (last 30 s)">Δ</span>
        </div>
        <div ref={body} className="overflow-y-auto outline-none" style={{ maxHeight }} tabIndex={0} onKeyDown={onTableKey} aria-label="Stats table" role="grid">
          {!loaded && rows.length === 0 && <p className="p-3 text-xs text-fg-3">Loading stats…</p>}
          {loaded && rows.length === 0 && (
            <p className="p-3 text-xs text-fg-3">
              No stats match{draft ? <> “{draft}”</> : null}{view.category !== "all" ? <> in {CATEGORY_LABEL[view.category]}</> : null}.{" "}
              <button type="button" className="underline hover:text-fg" onClick={() => { changeDraft(""); onFilter("", "all"); }}>Clear filters</button>
            </p>
          )}
          {rows.map((s) => {
            const header = view.sortBy === "category" && s.category !== lastCategory ? s.category : undefined;
            lastCategory = s.category;
            const ch = changes[s.key];
            const recent = ch && now - ch.at < DELTA_VISIBLE_MS;
            const flash = ch && now - ch.at < FLASH_MS ? (ch.delta > 0 ? "flash-up" : "flash-down") : "";
            const text = cleanText(s.text);
            const selected = view.selectedStatKey === s.key;
            return (
              <div key={s.key}>
                {header && (
                  <div className="sticky top-0 z-[1] flex items-center gap-1.5 border-b border-line bg-surface/95 px-2 py-1 text-[11px] font-semibold uppercase tracking-wide text-fg-3 backdrop-blur">
                    <span className={`size-1.5 rounded-full ${CATEGORY_DOT[s.category]}`} aria-hidden />{CATEGORY_LABEL[s.category]}
                  </div>
                )}
                <div
                  data-key={s.key}
                  role="row"
                  aria-selected={selected}
                  onClick={() => onSelect(selected ? null : s.key)}
                  className={`grid cursor-pointer grid-cols-[1.75rem_minmax(0,1fr)_minmax(3rem,auto)_2.75rem] items-center gap-2 border-b border-line/60 px-2 py-1 last:border-b-0 ${selected ? "bg-surface-3 shadow-[inset_2px_0_0_var(--color-ring)]" : "hover:bg-surface-2"}`}
                >
                  <StarButton pinned={pinned.has(s.key)} onClick={() => onPin(s.key, !pinned.has(s.key))} label={s.key} />
                  <div className="min-w-0" title={`${s.key} · id ${s.id}`}>
                    {text && <div className="truncate text-[13px] leading-snug">{text}</div>}
                    <div className={`truncate font-code ${text ? "text-[11px] text-fg-3" : "text-[12px] text-fg"}`}>{s.key}</div>
                  </div>
                  <span key={ch?.at} className={`tnum rounded px-1 text-right text-[13px] font-semibold ${s.value < 0 ? "text-danger" : ""} ${flash}`}>{fmt(s.value)}</span>
                  <span className={`tnum text-right text-[11px] ${recent ? (ch.delta > 0 ? "text-success" : "text-danger") : "text-transparent"}`}>
                    {recent ? signed(ch.delta) : "·"}
                  </span>
                </div>
                {selected && renderDetail?.(s)}
              </div>
            );
          })}
        </div>
      </div>
    </Card>
  );
}
