import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore } from "react";
import { Banner, EmptyState, IconButton, Toasts, useNow } from "../components";
import { Icon } from "../icons";
import { Inspector, type InspectorHost } from "./Inspector";
import { PathBar } from "./PathBar";
import { resolveRow, type Resolved } from "./rows";
import { SnippetPanel } from "./Snippet";
import { AUTO_REFRESH_MS, type ExplorerStore } from "./store";
import { Tree } from "./Tree";
import { shortPath } from "./paths";

/** What the panel needs from its host (the MCP Apps host, or the dev harness). */
export interface HostApi {
  updateModelContext?: (text: string, structured: Record<string, unknown>) => void;
  ask?: (text: string) => void;
  fullscreen?: { active: boolean; toggle: () => void };
}

export function Explorer({ store, host }: { store: ExplorerStore; host: HostApi }) {
  const snap = useSyncExternalStore(store.subscribe, store.getSnapshot);
  const now = useNow(1000);
  const filterRef = useRef<HTMLInputElement>(null);
  const fullscreen = host.fullscreen?.active ?? false;
  const sel = useMemo(() => resolveRow(snap, snap.selected), [snap]);
  const rootEntry = snap.root ? snap.entries.get(snap.root) : undefined;
  const game = snap.game;
  const gameLabel = game === "poe2" ? "Path of Exile 2" : game === "poe1" ? "Path of Exile" : "Game not known yet";

  // "/" focuses the filter from anywhere in the panel.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const tag = (document.activeElement as HTMLElement | null)?.tagName;
      if (e.key === "/" && tag !== "INPUT" && tag !== "TEXTAREA") { e.preventDefault(); filterRef.current?.focus(); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const describe = useCallback((r: Resolved) => {
    const n = r.node;
    const text = [
      `Data explorer (${game ?? "?"}) is open at ${snap.root}.`,
      `Selected: ${r.path ?? `${r.parent}.${r.name} (not addressable)`}` + (n.type ? ` : ${n.type}` : "") + (n.preview && n.preview !== n.type ? ` = ${n.preview}` : "") + ".",
      n.csharp ? `C#: ${n.csharp}` : "",
      n.namespace ? `Namespace: ${n.namespace}.` : "",
      snap.checked.size ? `Ticked for a snippet: ${[...snap.checked.keys()].join(", ")}.` : "",
    ].filter(Boolean).join(" ");
    const structured = {
      game, root: snap.root,
      selected: { path: r.path, csharp: n.csharp, type: n.type, kind: n.kind, preview: n.preview, namespace: n.namespace },
      checked: [...snap.checked.keys()],
    };
    return { text, structured };
  }, [game, snap.root, snap.checked]);

  // Debounced, deduplicated model context on selection / tick changes.
  const lastCtx = useRef("");
  useEffect(() => {
    if (!host.updateModelContext || !sel) return;
    const t = setTimeout(() => {
      const key = `${sel.id}|${[...snap.checked.keys()].join(",")}`;
      if (key === lastCtx.current) return;
      lastCtx.current = key;
      const { text, structured } = describe(sel);
      host.updateModelContext!(text, structured);
    }, 800);
    return () => clearTimeout(t);
  }, [host, sel, snap.checked, describe]);

  const inspectorHost = useMemo<InspectorHost>(() => ({
    send: host.updateModelContext ? (r) => { const { text, structured } = describe(r); host.updateModelContext!(text, structured); lastCtx.current = `${r.id}|${[...snap.checked.keys()].join(",")}`; store.toast("info", "Sent to Claude's context"); } : undefined,
    ask: host.ask ? (r) => host.ask!(askText(r, game)) : undefined,
  }), [host, describe, store, game, snap.checked]);

  const offline = snap.conn === "offline";
  const banners = offline && (
    <Banner tone="danger" icon="offline" title="HUD bridge unreachable"
      action={<button type="button" onClick={() => snap.root && void store.load(snap.root)} className="rounded-md border border-current px-2 py-0.5 text-[11px] font-medium hover:bg-surface">Retry</button>}>
      Is the HUD running with “Whats An AI Bridge” enabled?{snap.lastError && <span className="block truncate opacity-80" title={snap.lastError}>{snap.lastError}</span>}
    </Banner>
  );

  const toolbar = (
    <div className="flex items-center gap-1.5 border-b border-line px-2 py-1.5">
      <div className="relative min-w-0 flex-1">
        <Icon name="search" className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-fg-3" />
        <input
          ref={filterRef}
          type="search"
          value={snap.filter}
          onChange={(e) => store.setFilter(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Escape") { e.preventDefault(); store.setFilter(""); (e.target as HTMLInputElement).blur(); } if (e.key === "ArrowDown") { e.preventDefault(); (filterRef.current?.closest("section")?.querySelector('[role="tree"]') as HTMLElement | null)?.focus(); } }}
          placeholder="Filter loaded members"
          aria-label="Filter loaded members by name, value or type"
          className="h-7 w-full rounded-md border border-transparent bg-surface-2 pl-7 pr-7 text-[12px] placeholder:text-fg-3 focus:border-ring focus:outline-none [&::-webkit-search-cancel-button]:hidden"
        />
        {snap.filter ? <IconButton icon="x" label="Clear filter" size="sm" onClick={() => store.setFilter("")} className="absolute right-0.5 top-1/2 -translate-y-1/2" />
          : <kbd className="pointer-events-none absolute right-2 top-1/2 hidden -translate-y-1/2 rounded border border-line px-1 font-code text-[10px] text-fg-3 xs:block">/</kbd>}
      </div>
      {rootEntry?.children && (
        <span className="tnum shrink-0 text-[11px] text-fg-3" title="Members of the root node (loaded)">
          {rootEntry.children.length}{rootEntry.page?.total && rootEntry.page.total > rootEntry.children.length ? ` of ${rootEntry.page.total}` : ""}
          {rootEntry.components?.length ? ` + ${rootEntry.components.length} comp.` : ""}
        </span>
      )}
      {snap.expanded.size > 0 && (
        <IconButton icon="minimize" label="Collapse all" size="sm" onClick={() => { for (const p of snap.expanded) store.collapse(p); }} />
      )}
    </div>
  );

  const tree = (
    <section aria-label="Object tree" className={`flex flex-col overflow-hidden rounded-lg border border-line bg-surface ${fullscreen ? "min-h-0 flex-1" : ""}`}>
      {toolbar}
      <Tree store={store} snap={snap} now={now} fill={fullscreen} maxHeight="22rem" filterRef={filterRef} />
      <div className="flex items-center gap-2 border-t border-line px-2 py-1 text-[10.5px] text-fg-3">
        <span className="hidden sm:inline">↑↓ move · → ← expand/collapse · Enter open · Space tick · / filter</span>
        <span className="sm:hidden">↑↓ → ← Enter Space · / filter</span>
        <span className="ml-auto tnum" title="explore_object / watch_object / eval_path calls this session">{snap.calls} calls</span>
      </div>
    </section>
  );

  const inspector = sel ? (
    <Inspector key={sel.id} store={store} snap={snap} now={now} sel={sel} host={inspectorHost} variant={fullscreen ? "panel" : "card"} />
  ) : (
    <EmptyState icon="info" title="Select a node" className="rounded-lg border border-dashed border-line py-5">
      Click a row to see its type, path and C# accessor, watch it for changes, or add it to a snippet.
    </EmptyState>
  );
  const snippet = <SnippetPanel store={store} snap={snap} variant={fullscreen ? "panel" : "card"} />;

  return (
    <div className={fullscreen ? "flex h-screen flex-col overflow-hidden" : "flex flex-col"}>
      <header className="flex h-11 items-center gap-2 px-3">
        <span className="grid h-6 shrink-0 place-items-center rounded-md bg-fg px-1.5 text-[11px] font-bold tracking-tight text-surface" title={gameLabel}>
          {game === "poe2" ? "PoE 2" : game === "poe1" ? "PoE 1" : "PoE"}
        </span>
        <h1 className="truncate text-[13px] font-semibold">Data explorer</h1>
        {rootEntry?.node?.type && <span className="hidden truncate font-code text-[11px] text-fg-3 xs:inline" title={snap.root}>{rootEntry.node.type}</span>}
        <div className="ml-auto flex shrink-0 items-center gap-1">
          <span className={`hidden items-center gap-1.5 rounded-full border px-2 text-[11px] font-medium xs:flex ${offline ? "border-danger/30 bg-danger/10 text-danger" : snap.conn === "live" ? "border-success/30 bg-success/10 text-success" : "border-line text-fg-3"}`} role="status">
            {offline ? <Icon name="offline" className="size-3" /> : <span className={`size-1.5 rounded-full ${snap.conn === "live" ? "bg-success" : "bg-fg-3"}`} />}
            {offline ? "Offline" : snap.conn === "live" ? "Live" : "Connecting"}
          </span>
          <button
            type="button"
            onClick={() => store.setAutoRefresh(!snap.autoRefresh)}
            aria-pressed={snap.autoRefresh}
            title={`Auto-refresh the selected node every ${AUTO_REFRESH_MS / 1000} s (only that node, to spare the game thread)`}
            className={`flex h-7 items-center gap-1 rounded-md border px-1.5 text-[11px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${snap.autoRefresh ? "border-fg bg-fg text-surface" : "border-transparent text-fg-2 hover:bg-surface-3 hover:text-fg"}`}
          >
            <Icon name={snap.autoRefresh ? "pause" : "play"} className="size-3.5" /><span className="hidden sm:inline">Auto</span>
          </button>
          {host.fullscreen && (
            <IconButton icon={fullscreen ? "minimize" : "maximize"} label={fullscreen ? "Back to the conversation" : "Expand"} onClick={host.fullscreen.toggle} />
          )}
        </div>
      </header>

      <PathBar store={store} snap={snap} />

      {fullscreen ? (
        <>
          <div className="flex flex-col gap-2 px-4 pt-3 empty:hidden">{banners}</div>
          <div className="grid min-h-0 flex-1 grid-cols-1 grid-rows-[minmax(0,3fr)_minmax(0,2fr)] gap-3 p-3 pt-2 sm:grid-cols-[minmax(0,1fr)_minmax(20rem,26rem)] sm:grid-rows-1 sm:gap-4 sm:p-4 sm:pt-3">
            {tree}
            <aside className="scroll-thin flex min-h-0 flex-col gap-3 overflow-y-auto pr-1">
              {inspector}
              {snippet}
            </aside>
          </div>
        </>
      ) : (
        <div className="flex flex-col gap-2.5 px-3 pb-3 pt-2.5">
          {banners}
          {tree}
          {snippet}
          {inspector}
        </div>
      )}

      <Toasts toasts={snap.toasts} onDismiss={(id) => store.dismiss(id)} />
    </div>
  );
}

function askText(r: Resolved, game?: string): string {
  const n = r.node;
  const where = r.path ?? `${r.parent}.${r.name}`;
  const value = n.preview && n.preview !== n.type ? `, currently ${n.preview}` : "";
  return `In the ${game === "poe2" ? "PoE2" : game === "poe1" ? "PoE1" : "PoE"} HUD object model, \`${where}\` is a ${n.type ?? n.kind}${n.namespace ? ` (${n.namespace})` : ""}${value}. ` +
    `What does it hold, when is it valid, and how should a plugin read it${n.csharp ? ` (\`${n.csharp}\`)` : ""}? Short answer, with the C#.` +
    (r.path && r.path !== where ? "" : "") + ` Context: I am looking at ${shortPath(where)}.`;
}
