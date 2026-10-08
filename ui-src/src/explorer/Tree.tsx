import { memo, useCallback, useEffect, useMemo, useRef, type ReactNode } from "react";
import { EmptyState, IconButton } from "../components";
import { Icon } from "../icons";
import { hasDetail, Preview } from "./Preview";
import { FOCUSABLE, flatten, type Row } from "./rows";
import { FLASH_MS, WATCH_HIT_MS, type ExplorerStore, type Snapshot } from "./store";
import type { SnippetItem } from "./codegen";

const INDENT = 14;

export interface TreeProps {
  store: ExplorerStore;
  snap: Snapshot;
  now: number;
  /** Fill the parent (fullscreen) instead of capping the height. */
  fill?: boolean;
  maxHeight?: string;
  filterRef: React.RefObject<HTMLInputElement | null>;
}

/** The outline: lazy rows over the cached entries, with filter, keyboard navigation and paging rows. */
export function Tree({ store, snap, now, fill, maxHeight = "22rem", filterRef }: TreeProps) {
  const rows = useMemo(() => flatten(snap), [snap]);
  const body = useRef<HTMLDivElement>(null);
  const rootEntry = snap.root ? snap.entries.get(snap.root) : undefined;
  const q = snap.filter.trim();
  const nodeCount = rows.reduce((n, r) => n + (r.t === "node" ? 1 : 0), 0);

  const selectedIndex = rows.findIndex((r) => r.id === snap.selected);
  useEffect(() => {
    if (selectedIndex < 0 || !snap.selected) return;
    body.current?.querySelector<HTMLElement>(`[data-id="${CSS.escape(snap.selected)}"]`)?.scrollIntoView({ block: "nearest" });
  }, [snap.selected, selectedIndex]);

  const activate = useCallback((row: Row) => {
    switch (row.t) {
      case "node":
        if (row.expandable) store.toggle(row.path!);
        else if (row.path && row.child.csharp) toggleCheck(store, snap, row);
        break;
      case "more": void store.loadMore(row.parent); break;
      case "skipped": void store.loadSkipped(row.parent); break;
      case "error": void store.load(row.parent); break;
    }
  }, [store, snap]);

  const onKey = (e: React.KeyboardEvent) => {
    const focusable = rows.map((r, i) => [r, i] as const).filter(([r]) => FOCUSABLE.has(r.t));
    if (!focusable.length) return;
    const pos = focusable.findIndex(([, i]) => i === selectedIndex);
    const selectAt = (p: number) => store.select(focusable[Math.max(0, Math.min(focusable.length - 1, p))][0].id);
    const cur = selectedIndex >= 0 ? rows[selectedIndex] : undefined;
    switch (e.key) {
      case "ArrowDown": e.preventDefault(); selectAt(pos < 0 ? 0 : pos + 1); break;
      case "ArrowUp": e.preventDefault(); selectAt(pos < 0 ? focusable.length - 1 : pos - 1); break;
      case "Home": e.preventDefault(); selectAt(0); break;
      case "End": e.preventDefault(); selectAt(focusable.length - 1); break;
      case "ArrowRight":
        e.preventDefault();
        if (cur?.t === "node" && cur.expandable && !cur.expanded) store.expand(cur.path!);
        else if (cur?.t === "node" && cur.expanded) selectAt(pos + 1);
        break;
      case "ArrowLeft":
        e.preventDefault();
        if (cur?.t === "node" && cur.expanded) store.collapse(cur.path!);
        else if (cur && cur.t !== "loading" && cur.t !== "group" && cur.t !== "empty" && cur.parent !== snap.root) {
          const parentRow = rows.find((r) => r.t === "node" && r.path === cur.parent);
          if (parentRow) store.select(parentRow.id); else store.select(snap.root);
        } else store.select(snap.root);
        break;
      case "Enter": if (cur) { e.preventDefault(); activate(cur); } break;
      case " ": if (cur?.t === "node" && cur.path && cur.child.csharp) { e.preventDefault(); toggleCheck(store, snap, cur); } break;
      case "Escape": if (q) { e.preventDefault(); store.setFilter(""); } break;
      default:
        if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey && e.key !== "/") {
          e.preventDefault();
          store.setFilter(snap.filter + e.key);
          filterRef.current?.focus();
        }
    }
  };

  const onToggle = useCallback((path: string) => store.toggle(path), [store]);
  const onSelect = useCallback((id: string) => store.select(id), [store]);
  const onCheck = useCallback((item: SnippetItem, on: boolean) => store.setChecked(item, on), [store]);

  return (
    <div
      ref={body}
      role="tree"
      aria-label="Object model"
      aria-activedescendant={snap.selected && selectedIndex >= 0 ? elId(snap.selected) : undefined}
      tabIndex={0}
      onKeyDown={onKey}
      className={`scroll-thin overflow-y-auto overflow-x-hidden py-1 outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring ${fill ? "min-h-0 flex-1" : ""}`}
      style={{ maxHeight: fill ? undefined : maxHeight, minHeight: fill ? undefined : "8rem", scrollPaddingBlock: "1.5rem" }}
    >
      {!snap.root && <LoadingRows depth={0} count={8} />}
      {rootEntry?.error && !rootEntry.children && !rootEntry.loading && (
        <EmptyState icon={snap.conn === "offline" ? "offline" : "warning"} title={snap.conn === "offline" ? "Nothing loaded" : "Could not read this path"} className="py-5">
          {snap.conn === "offline"
            ? <span className="block">The bridge is unreachable. Retry once the HUD is running.</span>
            : <span className="block break-words text-danger">{rootEntry.error}</span>}
          <div className="mt-2 flex justify-center gap-1.5">
            <SmallButton onClick={() => void store.load(snap.root!)}>Retry</SmallButton>
            {snap.root !== "GameController" && <SmallButton onClick={() => void store.navigate("GameController")}>Go to GameController</SmallButton>}
          </div>
        </EmptyState>
      )}
      {rootEntry?.children && q && nodeCount === 0 && (
        <EmptyState icon="search" title="No loaded member matches" className="py-5">
          “{q}” — the filter searches names, values and types of what is loaded. Expand more nodes or clear it.
          <div className="mt-1.5"><SmallButton onClick={() => store.setFilter("")}>Clear filter</SmallButton></div>
        </EmptyState>
      )}
      {rows.map((row) => {
        switch (row.t) {
          case "node": {
            const flashAt = snap.changed.get(row.id);
            const hitAt = snap.watchHits.get(row.id);
            const watched = [...snap.watchHits.keys()].some((p) => p !== row.id && p.startsWith(row.id) && (p[row.id.length] === "." || p[row.id.length] === "["));
            return (
              <NodeRow key={row.id} row={row} selected={snap.selected === row.id} checked={!!row.path && snap.checked.has(row.path)}
                flash={flashAt !== undefined && now - flashAt < FLASH_MS ? flashAt : 0}
                hit={hitAt !== undefined && now - hitAt < WATCH_HIT_MS} hitBelow={watched && !hitAt}
                onToggle={onToggle} onSelect={onSelect} onCheck={onCheck} />
            );
          }
          case "group":
            return (
              <div key={row.id} className="flex h-6 items-center gap-1.5 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3" style={{ paddingLeft: row.depth * INDENT + 22 }}>
                <Icon name="puzzle" className="size-3" />{row.label}
                <span className="tnum font-normal">{row.count}</span>
                <span className="font-normal normal-case tracking-normal">· GetComponent&lt;T&gt;()</span>
              </div>
            );
          case "more":
            return (
              <ActionRow key={row.id} id={row.id} depth={row.depth} selected={snap.selected === row.id} onSelect={onSelect}>
                <button type="button" disabled={row.loading} onClick={() => void store.loadMore(row.parent)}
                  className="inline-flex items-center gap-1 rounded-md border border-line px-1.5 py-0.5 text-[11px] font-medium text-fg-2 hover:bg-surface-3 hover:text-fg disabled:opacity-60">
                  {row.loading ? <Icon name="sync" className="spin size-3" /> : <Icon name="down" className="size-3" />}
                  Load {Math.min(50, row.remaining)} more
                </button>
                <span className="tnum text-[11px] text-fg-3">{row.remaining.toLocaleString("en-US")} not loaded</span>
                {row.remaining > 50 && row.remaining <= 200 && (
                  <button type="button" disabled={row.loading} onClick={() => void store.loadMore(row.parent, true)} className="text-[11px] text-fg-3 underline-offset-2 hover:text-fg hover:underline">all</button>
                )}
              </ActionRow>
            );
          case "skipped":
            return (
              <ActionRow key={row.id} id={row.id} depth={row.depth} selected={snap.selected === row.id} onSelect={onSelect} tone="warning">
                <Icon name="clock" className="size-3.5 shrink-0 text-warning" />
                <span className="min-w-0 truncate text-[11px] text-fg-2" title={`${row.reason}: ${row.members.join(", ")}`}>
                  <span className="tnum font-medium text-fg">{row.members.length}</span> not read ({row.reason}) <span className="font-code text-fg-3">{row.members.join(", ")}</span>
                </span>
                <button type="button" disabled={row.loading} onClick={() => void store.loadSkipped(row.parent)}
                  className="ml-auto inline-flex shrink-0 items-center gap-1 rounded-md border border-line px-1.5 py-0.5 text-[11px] font-medium text-fg-2 hover:bg-surface-3 hover:text-fg disabled:opacity-60">
                  {row.loading && <Icon name="sync" className="spin size-3" />}{row.loading ? "Loading" : "Load them"}
                </button>
              </ActionRow>
            );
          case "error":
            return (
              <ActionRow key={row.id} id={row.id} depth={row.depth} selected={snap.selected === row.id} onSelect={onSelect} tone="danger">
                <Icon name="warning" className="size-3.5 shrink-0 text-danger" />
                <span className="min-w-0 truncate text-[11px] text-danger" title={row.message}>{row.message}</span>
                <button type="button" onClick={() => void store.load(row.parent)} className="ml-auto shrink-0 rounded-md border border-current px-1.5 py-0.5 text-[11px] font-medium text-danger hover:bg-danger/10">Retry</button>
              </ActionRow>
            );
          case "empty":
            return <div key={row.id} className="flex h-6 items-center text-[11px] italic text-fg-3" style={{ paddingLeft: row.depth * INDENT + 22 }}>no members</div>;
          case "loading":
            return <LoadingRows key={row.id} depth={row.depth} count={3} />;
        }
      })}
    </div>
  );
}

function toggleCheck(store: ExplorerStore, snap: Snapshot, row: Extract<Row, { t: "node" }>) {
  const c = row.child;
  store.setChecked({ name: c.name, path: row.path!, csharp: c.csharp!, type: c.type, kind: c.kind, preview: c.preview }, !snap.checked.has(row.path!));
}

export function elId(id: string): string {
  return `row-${id.replace(/[^A-Za-z0-9_-]/g, (ch) => `_${ch.charCodeAt(0).toString(16)}`)}`;
}

const NodeRow = memo(function NodeRow({ row, selected, checked, flash, hit, hitBelow, onToggle, onSelect, onCheck }: {
  row: Extract<Row, { t: "node" }>; selected: boolean; checked: boolean; flash: number; hit: boolean; hitBelow: boolean;
  onToggle: (path: string) => void; onSelect: (id: string) => void; onCheck: (item: SnippetItem, on: boolean) => void;
}) {
  const c = row.child;
  const checkable = !!row.path && !!c.csharp && c.kind !== "blocked" && !c.error && !(c.kind === "component" && !row.path);
  const dim = c.kind === "blocked" || (c.kind === "component" && !row.path);
  const title = [c.name, c.type && `: ${c.type}`, c.declaredType && c.declaredType !== c.type && ` (declared ${c.declaredType})`, c.slowMs !== undefined && ` · getter took ${c.slowMs} ms`, !row.path && " · not addressable by the walker (C# still shown)"].filter(Boolean).join("");
  return (
    <div
      id={elId(row.id)}
      data-id={row.id}
      role="treeitem"
      aria-level={row.depth + 1}
      aria-expanded={row.expandable ? row.expanded : undefined}
      aria-selected={selected}
      onClick={() => onSelect(row.id)}
      onDoubleClick={() => { if (row.expandable) onToggle(row.path!); }}
      title={title}
      className={`group flex h-6 cursor-default items-center gap-1 pr-1 text-[12px] ${selected ? "bg-ring/10 shadow-[inset_2px_0_0_var(--color-ring)]" : hit ? "" : "hover:bg-surface-2"} ${hit ? "watch-hit" : ""}`}
      style={{ paddingLeft: row.depth * INDENT + 4 }}
    >
      <button
        type="button"
        tabIndex={-1}
        aria-hidden={!row.expandable}
        onClick={(e) => { e.stopPropagation(); if (row.expandable) onToggle(row.path!); }}
        className={`grid size-4 shrink-0 place-items-center rounded text-fg-3 ${row.expandable ? "hover:bg-surface-3 hover:text-fg" : "pointer-events-none"}`}
      >
        {row.loading ? <Icon name="sync" className="spin size-3" />
          : row.expandable ? <Icon name="chevronRight" className={`size-3 transition-transform ${row.expanded ? "rotate-90" : ""}`} />
          : hitBelow ? <span className="size-1.5 rounded-full bg-warning" title="Something below this changed during the watch" /> : null}
      </button>
      <span className={`shrink-0 font-code ${row.component ? "text-info" : dim ? "text-fg-3" : "text-fg"} ${row.component ? "" : "font-medium"}`}>
        {row.component && <Icon name="puzzle" className="mr-1 inline size-3 align-[-2px]" />}
        {c.name}
      </span>
      {c.kind !== "component" && <span className="shrink-0 text-fg-3">:</span>}
      <span key={flash} className={`flex min-w-0 flex-1 items-center gap-1.5 ${flash ? "flash-up rounded" : ""}`}>
        <Preview node={c} className="text-[12px]" />
        {c.slowMs !== undefined && <Icon name="clock" className="size-3 shrink-0 text-warning/80" />}
        {!row.path && c.kind !== "component" && <Icon name="lock" className="size-3 shrink-0 text-fg-3" />}
      </span>
      {c.type && c.kind !== "component" && hasDetail(c) && (
        <span className="hidden max-w-[11rem] shrink-0 truncate text-right font-code text-[10.5px] text-fg-3 xs:block">
          {c.type}{c.declaredType && c.declaredType !== c.type && <span className="opacity-70"> ⟵ {c.declaredType}</span>}
        </span>
      )}
      <span className="grid size-5 shrink-0 place-items-center">
        {checkable && (
          <button
            type="button"
            tabIndex={-1}
            aria-pressed={checked}
            aria-label={checked ? "Remove from snippet" : "Add to snippet"}
            title={checked ? "Remove from the C# snippet" : "Add to the C# snippet (Space)"}
            onClick={(e) => { e.stopPropagation(); onCheck({ name: c.name, path: row.path!, csharp: c.csharp!, type: c.type, kind: c.kind, preview: c.preview }, !checked); }}
            className={`grid size-5 place-items-center rounded text-fg-3 hover:bg-surface-3 hover:text-fg ${checked ? "text-fg" : "opacity-0 group-hover:opacity-100 focus-visible:opacity-100"}`}
          >
            <Icon name={checked ? "checkSquare" : "square"} className="size-3.5" />
          </button>
        )}
      </span>
    </div>
  );
});

function ActionRow({ id, depth, selected, onSelect, tone, children }: { id: string; depth: number; selected: boolean; onSelect: (id: string) => void; tone?: "warning" | "danger"; children: ReactNode }) {
  return (
    <div id={elId(id)} data-id={id} role="treeitem" aria-selected={selected} onClick={() => onSelect(id)}
      className={`flex h-7 items-center gap-2 pr-2 ${selected ? "bg-ring/10 shadow-[inset_2px_0_0_var(--color-ring)]" : tone === "danger" ? "bg-danger/5" : tone === "warning" ? "bg-warning/5" : ""}`}
      style={{ paddingLeft: depth * INDENT + 24 }}>
      {children}
    </div>
  );
}

function LoadingRows({ depth, count }: { depth: number; count: number }) {
  return (
    <div aria-hidden>
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="flex h-6 items-center gap-2 pr-2" style={{ paddingLeft: depth * INDENT + 24 }}>
          <div className="shimmer h-2.5 rounded" style={{ width: `${22 + (i % 3) * 8}%` }} />
          <div className="shimmer h-2.5 rounded" style={{ width: `${30 - (i % 4) * 5}%` }} />
        </div>
      ))}
    </div>
  );
}

export function SmallButton({ children, onClick, icon, active, disabled, title, tone = "default" }: { children?: ReactNode; onClick?: () => void; icon?: Parameters<typeof Icon>[0]["name"]; active?: boolean; disabled?: boolean; title?: string; tone?: "default" | "primary" }) {
  return (
    <button type="button" onClick={onClick} disabled={disabled} aria-pressed={active} title={title}
      className={`inline-flex h-6 items-center gap-1 rounded-md border px-1.5 text-[11px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 ${active ? "border-fg bg-fg text-surface" : tone === "primary" ? "border-ring/40 bg-ring/10 text-fg hover:bg-ring/20" : "border-line bg-surface text-fg-2 hover:border-line-2 hover:text-fg"}`}>
      {icon && <Icon name={icon} className="size-3" />}{children}
    </button>
  );
}

export { IconButton };
