import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { Banner, EmptyState, IconButton, Toasts, useNow } from "../components";
import { SmallButton } from "../explorer/Tree";
import { Icon } from "../icons";
import { addrPlus, fmtBytes, fmtValue, hexOf, hexOff, typeLabel, type Region, type Seg } from "./bytes";
import { HexView } from "./HexView";
import { Inspector, NoSelection, type InspectorHost } from "./Inspector";
import { KIND_LABEL, segLabel } from "./paint";
import { Legend, MapRows, Strip, filterSegs, type MapFilter } from "./StructMap";
import { TargetBar } from "./TargetBar";
import { LIVE_MS, type MemoryStore, type Mode, type Selection, type View } from "./store";
import { WatchPanel } from "./WatchPanel";
import type { LayoutResult, ReadResult } from "./types";
import { findingsFor, isToCheck } from "./findingsModel";
import { codeKey, codeMarks, gatesOf, rankFunctions } from "./codeModel";
import { CodePanel, type CodeHost } from "./CodePanel";
import { Population } from "./Population";
import { Experiments } from "./Experiments";
import { Findings } from "./Findings";

const MODES: { id: Mode; label: string; icon: "layers" | "grid" | "diff" | "sparkle"; title: string }[] = [
  { id: "struct", label: "Struct", icon: "layers", title: "One object: the HUD's struct over live bytes" },
  { id: "population", label: "Population", icon: "grid", title: "Every item of a collection: which bits a known property explains" },
  { id: "experiments", label: "Experiments", icon: "diff", title: "Guided experiments run with the user step by step, and snapshots compared in order" },
  { id: "findings", label: "Findings", icon: "sparkle", title: "What is known, per game, and whether it still holds" },
];

/** What the panel needs from its host (the MCP Apps host, or the dev harness). */
export interface HostApi {
  updateModelContext?: (text: string, structured: Record<string, unknown>) => void;
  ask?: (text: string) => void;
  fullscreen?: { active: boolean; toggle: () => void };
}

export function MemoryView({ store, host }: { store: MemoryStore; host: HostApi }) {
  const snap = useSyncExternalStore(store.subscribe, store.getSnapshot);
  const now = useNow(500);
  const fullscreen = host.fullscreen?.active ?? false;
  const view = snap.views[snap.index] as View | undefined;
  const region = view?.region;
  const layout = view?.data && "fields" in view.data ? (view.data as LayoutResult) : undefined;
  const read = view?.data && !layout ? (view.data as ReadResult) : undefined;
  const game = snap.game;
  const gameLabel = game === "poe2" ? "Path of Exile 2" : game === "poe1" ? "Path of Exile" : "Game not known yet";
  const [filter, setFilter] = useState<MapFilter>("all");
  const [hexOpen, setHexOpen] = useState(true);
  const sel = snap.selection;
  const selSeg = sel?.segId && region ? region.segs.find((s) => s.id === sel.segId) : undefined;
  const segs = useMemo(() => (region ? filterSegs(region, filter, snap.changes) : []), [region, filter, snap.changes]);
  const watchForView = snap.watch?.viewId === view?.id ? snap.watch : undefined;
  const sf = useMemo(() => snap.fnd.data && (layout || view?.target.path) ? findingsFor(snap.fnd.data.findings, { struct: layout?.struct, object: layout?.object, path: view?.target.path }, game) : undefined, [snap.fnd.data, layout, view?.target.path, game]);
  const toCheck = game && snap.fnd.data ? snap.fnd.data.findings.filter((f) => isToCheck(f, game)).length : 0;
  const mode = snap.mode;
  // Offsets the game's code uses, from this session's find_field_access lookups on this struct.
  const structKey = store.structKeyOf(view);
  const cm = useMemo(() => codeMarks(snap.code.values(), structKey), [snap.code, structKey]);
  const codeQ = sel ? snap.code.get(codeKey(structKey, store.codeTarget(sel, snap.bitSel).offset, store.codeTarget(sel, snap.bitSel).bit)) : undefined;

  useEffect(() => { setFilter("all"); }, [view?.id]);
  // A watch that found changes switches the map to them once, so the discovery is in view.
  const lastWatch = useRef<number | undefined>(undefined);
  useEffect(() => {
    if (watchForView?.status === "done" && watchForView.startedAt !== lastWatch.current) {
      lastWatch.current = watchForView.startedAt;
      if ((watchForView.result?.changedRanges.length ?? 0) > 0) setFilter("changed");
    }
  }, [watchForView]);

  const describe = useCallback((): { text: string; structured: Record<string, unknown> } | undefined => {
    if (!view || !region) return undefined;
    const where = layout
      ? `${layout.struct} @ ${layout.address} (${layout.structSize} bytes${region.size > layout.structSize ? ` + ${region.size - layout.structSize} past the end` : ""}; ${layout.fields.length} fields map ${mappedBytes(region)} bytes, ${layout.candidates.length} candidates in ${layout.gaps.length} unmapped ranges)`
      : `raw read of ${region.size} bytes @ ${region.address}${read?.origin ? ` (${read.origin})` : ""}`;
    const parts = [`Memory view (${game ?? "?"}) is open at ${where}.`];
    const structured: Record<string, unknown> = { game, mode: view.mode, address: region.address, struct: layout?.struct, path: view.target.path, size: region.size, chain: snap.views.slice(0, snap.index + 1).map((v) => v.via ?? v.label) };
    if (sel) {
      const s = selSeg;
      const addr = addrPlus(region.address, sel.off);
      const bytes = hexOf(region.bytes, sel.off, sel.size);
      const name = s?.field?.name ?? (s?.cand ? `${KIND_LABEL[s.cand.kind] ?? s.cand.kind} candidate` : s?.slot ? KIND_LABEL[s.slot.kind] ?? s.slot.kind : s?.kind === "gap" ? "unmapped bytes" : "bytes");
      const value = s?.field?.value ?? s?.cand?.value ?? s?.cand?.first ?? s?.slot?.value ?? s?.slot?.hex;
      const ghidra = s?.field?.ghidra ?? s?.cand?.ghidra ?? s?.slot?.ghidra;
      parts.push(`Selected: ${name} at +${sel.off} (${hexOff(sel.off)}), ${sel.size} bytes, address ${addr}${s?.field ? ` : ${s.field.type}` : ""}${value !== undefined ? ` = ${fmtValue(value)}` : ""}; bytes ${bytes}${s?.field?.check && s.field.check !== "ok" ? `; check ${s.field.check}${s.field.why ? ` (${s.field.why})` : ""}` : ""}${s?.cand?.detail ? `; ${s.cand.detail}` : ""}${ghidra ? `; Ghidra ${ghidra}` : ""}${s?.field?.bits?.length ? `; set bits ${s.field.bits.join(", ")}` : ""}.`);
      structured.selected = { off: sel.off, size: sel.size, address: addr, kind: s ? segLabel(s) : "bytes", name: s?.field?.name, type: s?.field?.type, value, bytes, check: s?.field?.check, why: s?.field?.why, ghidra, bits: s?.field?.bits ?? s?.slot?.bits, detail: s?.cand?.detail };
    }
    if (watchForView?.status === "done" && watchForView.result) {
      const r = watchForView.result;
      parts.push(r.changedRanges.length ? `Watch (${Math.round(r.durationMs / 1000)} s, ${r.samples} samples): ${r.changedRanges.map((c) => `+${c.off} ${c.field ?? ""} ${c.first} -> ${c.last} x${c.changes}${c.bitsFlipped?.length ? ` bits ${c.bitsFlipped.join(",")}${c.bitsRelativeTo ? ` of ${c.bitsRelativeTo}` : ""}` : ""}${c.noisy ? " (noisy)" : ""}`).join("; ")}.` : "Watch: nothing changed.");
      structured.watch = { durationMs: r.durationMs, samples: r.samples, changedRanges: r.changedRanges };
    }
    if (codeQ?.status === "done" && codeQ.result) {
      const r = codeQ.result;
      const fns = rankFunctions(r, r.target.bit ?? undefined).slice(0, 5);
      const gates = gatesOf(r);
      parts.push(`Code (find_field_access, static Ghidra) for +${r.target.offset}${r.target.bit !== undefined && r.target.bit !== null ? ` bit ${r.target.bit}` : ""}: ${r.functions.length} functions of the struct; top ${fns.map((f) => `${f.fn.function}${f.role ? ` (${f.role})` : ""} ${f.confidence}`).join(", ")}${gates.length ? `; gates: ${gates.map((g) => `bit ${g.bits.join("+")} ${g.when} -> +${hexOff(g.off)}${g.width ? ` (${g.width} B)` : ""}`).join(", ")}` : ""}.`);
      structured.code = { target: r.target, functions: fns.map((f) => ({ function: f.fn.function, role: f.role, confidence: f.confidence, kinds: f.kinds, also: f.also })), gates: gates.map((g) => ({ bits: g.bits, when: g.when, off: g.off, width: g.width, kind: g.kind })), decompiled: r.decompiled.slice(0, 2) };
    }
    return { text: parts.join(" "), structured };
  }, [view, region, layout, read, game, sel, selSeg, watchForView, snap.views, snap.index, codeQ]);

  // Debounced, deduplicated model context on selection / view / watch changes.
  const lastCtx = useRef("");
  useEffect(() => {
    if (!host.updateModelContext) return;
    const t = setTimeout(() => {
      const d = describe();
      if (!d) return;
      const key = `${view?.id}|${sel?.off}:${sel?.size}|${watchForView?.startedAt}:${watchForView?.status}|${codeQ?.key}:${codeQ?.status}`;
      if (key === lastCtx.current) return;
      lastCtx.current = key;
      host.updateModelContext!(d.text, d.structured);
    }, 800);
    return () => clearTimeout(t);
  }, [host, describe, view?.id, sel?.off, sel?.size, watchForView?.startedAt, watchForView?.status, codeQ?.key, codeQ?.status]);

  const codeHost = useMemo<CodeHost>(() => ({
    send: host.updateModelContext ? (text, structured) => { host.updateModelContext!(text, { game, ...structured }); store.toast("info", "Sent to Claude's context"); } : undefined,
    ask: host.ask,
  }), [host, game, store]);

  const inspectorHost = useMemo<InspectorHost>(() => ({
    send: host.updateModelContext ? () => { const d = describe(); if (d) { host.updateModelContext!(d.text, d.structured); store.toast("info", "Sent to Claude's context"); } } : undefined,
    ask: host.ask && view && region && sel ? () => host.ask!(askText(view, region, sel, selSeg, game, watchForView?.result?.changedRanges ?? [])) : undefined,
  }), [host, describe, store, view, region, sel, selSeg, game, watchForView]);

  const offline = snap.conn === "offline";
  const banners = offline && (
    <Banner tone="danger" icon="offline" title="HUD bridge unreachable"
      action={<button type="button" onClick={() => store.reload()} className="rounded-md border border-current px-2 py-0.5 text-[11px] font-medium hover:bg-surface">Retry</button>}>
      Is the HUD running with “Whats An AI Bridge” enabled?{snap.lastError && <span className="block truncate opacity-80" title={snap.lastError}>{snap.lastError}</span>}
    </Banner>
  );

  const summary = region && (
    <div className="flex flex-wrap items-center gap-1.5 text-[10.5px]" aria-label="Coverage summary">
      {layout ? (
        <>
          <Stat tone="field" label={`${layout.fields.length} fields`} detail={`${mappedBytes(region)} B mapped · ${Math.round((mappedBytes(region) / layout.structSize) * 100)}%`} />
          <Stat tone="gap" label={`${layout.gaps.length} unmapped`} detail={`${unmappedBytes(region)} B`} />
          <Stat tone="cand" label={`${layout.candidates.length} candidates`} detail={layout.candidates.length ? "structure the HUD skips" : "none found"} strong={layout.candidates.length > 0} />
          {(() => { const bad = layout.fields.filter((f) => f.check === "suspicious" || f.check === "invalid").length; const odd = layout.fields.filter((f) => f.check === "unusual").length; return bad || odd ? <Stat tone={bad ? "danger" : "warning"} label={bad ? `${bad} suspicious` : `${odd} unusual`} detail={bad && odd ? `+${odd} unusual` : "re-check the mapping"} strong /> : <Stat tone="ok" label="checks pass" />; })()}
          {sf && sf.all.length > 0 && <Stat tone="cand" label={`${sf.all.length} finding${sf.all.length === 1 ? "" : "s"}`} detail={`${sf.all.filter((f) => game && f.games[game]?.status === "verified").length} verified on ${game === "poe2" ? "PoE 2" : "PoE 1"}`} strong onClick={() => store.setMode("findings")} />}
          {region.size > layout.structSize && <Stat tone="gap" label={`+${region.size - layout.structSize} B past end`} />}
        </>
      ) : read ? (
        <>
          <Stat tone="field" label={`${read.size} bytes`} detail={read.region} />
          {read.origin && read.origin !== "address" && <Stat tone="gap" label={read.origin} />}
          <Stat tone="cand" label={`${read.slots.filter((s) => s.kind === "vtable" || s.kind === "module").length} module ptrs`} />
          <Stat tone="ptr" label={`${read.slots.filter((s) => s.kind === "heap").length} heap ptrs`} />
        </>
      ) : null}
      {view?.loadedAt && <span className="tnum ml-auto text-fg-3" title={snap.live ? `Live: re-read every ${LIVE_MS / 1000} s` : "When these bytes were read"}>{snap.live ? <span className="inline-flex items-center gap-1 text-m-change"><span className="size-1.5 animate-pulse rounded-full bg-m-change" />live</span> : ago(now - view.loadedAt)}</span>}
    </div>
  );

  const errorBody = view?.error && !view.loading && <ErrorState store={store} view={view} offline={offline} />;

  const mapCard = (
    <section aria-label="Struct map" className={`flex flex-col overflow-hidden rounded-lg border border-line bg-surface ${fullscreen ? "min-h-0 flex-1" : ""}`}>
      <div className="flex items-center gap-1.5 border-b border-line px-2 py-1.5">
        <Icon name="layers" className="size-3.5 text-fg-3" />
        <span className="text-[11.5px] font-semibold">{layout ? "Struct map" : "Slots"}</span>
        {region && (
          <div className="ml-auto flex overflow-hidden rounded-md border border-line" role="radiogroup" aria-label="Show">
            {(["all", "fields", "cands", "changed"] as MapFilter[]).map((f) => {
              const label = f === "all" ? "All" : f === "fields" ? (layout ? "Mapped" : "Values") : f === "cands" ? (layout ? "Candidates" : "Pointers") : "Changed";
              const disabled = f === "changed" && snap.changes.size === 0;
              return (
                <button key={f} type="button" role="radio" aria-checked={filter === f} disabled={disabled} onClick={() => setFilter(f)}
                  className={`h-5 px-1.5 text-[10.5px] font-medium disabled:opacity-40 ${filter === f ? "bg-fg text-surface" : "text-fg-2 hover:bg-surface-3 hover:text-fg"}`}>{label}</button>
              );
            })}
          </div>
        )}
      </div>
      {region ? (
        <>
          <div className="px-2 pt-1">
            <Strip store={store} snap={snap} region={region} segs={region.segs} sf={sf} cm={cm} />
            <div className="mt-0.5 pb-1.5"><Legend struct={!!layout} code={cm.size > 0} /></div>
          </div>
          <MapRows store={store} snap={snap} view={view!} region={region} segs={segs} now={now} fill={fullscreen} maxHeight="20rem" sf={sf} cm={cm} />
        </>
      ) : errorBody ? errorBody : <LoadingMap />}
      <div className="flex items-center gap-2 border-t border-line px-2 py-1 text-[10.5px] text-fg-3">
        <span className="hidden sm:inline">↑↓ move · Enter follow pointer · Esc clear · shift-click in hex widens</span>
        <span className="sm:hidden">↑↓ Enter Esc</span>
        <span className="ml-auto tnum" title="memory_layout / memory_read / watch_memory / memory_where / find_field_access calls this session">{snap.calls} calls</span>
      </div>
    </section>
  );

  const hexCard = region && (
    <section aria-label="Hex view" className={`flex flex-col overflow-hidden rounded-lg border border-line bg-surface ${fullscreen ? "min-h-0 flex-1" : ""}`}>
      <button type="button" onClick={() => setHexOpen((o) => !o)} aria-expanded={hexOpen} className="flex w-full items-center gap-1.5 border-b border-line px-2 py-1.5 text-left hover:bg-surface-2">
        <Icon name="grid" className="size-3.5 text-fg-3" />
        <span className="text-[11.5px] font-semibold">Hex</span>
        <span className="tnum font-code text-[10.5px] text-fg-3">{region.address} · {fmtBytes(region.size)}</span>
        {!fullscreen && <Icon name="chevron" className={`ml-auto size-3.5 text-fg-3 transition-transform ${hexOpen ? "rotate-180" : ""}`} />}
      </button>
      {(hexOpen || fullscreen) && <HexView store={store} snap={snap} region={region} now={now} fill={fullscreen} maxHeight="13rem" />}
    </section>
  );

  const inspector = region && view ? (sel ? <Inspector key={`${view.id}:${sel.off}:${sel.size}`} store={store} snap={snap} view={view} region={region} sel={sel} host={inspectorHost} variant={fullscreen ? "panel" : "card"} sf={sf} cm={cm} /> : <NoSelection struct={!!layout} />) : null;
  const codePanel = region && view && sel && <CodePanel key={`code:${view.id}:${sel.off}:${sel.size}`} store={store} snap={snap} view={view} region={region} sel={sel} cm={cm} host={codeHost} variant={fullscreen ? "panel" : "card"} now={now} />;
  const watchPanel = region && view && <WatchPanel store={store} snap={snap} view={view} region={region} now={now} variant={fullscreen ? "panel" : "card"} />;

  const textHost = useMemo(() => ({
    send: host.updateModelContext ? (text: string) => { host.updateModelContext!(text, { game, mode: snap.mode, text }); store.toast("info", "Sent to Claude's context"); } : undefined,
    ask: host.ask,
  }), [host, game, snap.mode, store]);

  const tabs = (
    <div role="tablist" aria-label="View" className="flex items-center gap-0.5 border-b border-line px-2 py-1">
      {MODES.map((m) => {
        const active = mode === m.id;
        const badge = m.id === "findings" && toCheck > 0 ? toCheck : m.id === "struct" && sf && sf.all.length ? undefined : undefined;
        return (
          <button key={m.id} type="button" role="tab" aria-selected={active} onClick={() => store.setMode(m.id)} title={m.title}
            className={`flex h-7 items-center gap-1.5 rounded-md px-2 text-[11.5px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${active ? "bg-surface-3 text-fg" : "text-fg-2 hover:bg-surface-2 hover:text-fg"}`}>
            <Icon name={m.icon} className={`size-3.5 ${active ? "" : "text-fg-3"}`} />
            <span className={m.id === "experiments" || m.id === "population" ? "hidden xs:inline" : ""}>{m.label}</span>
            {badge !== undefined && <span className="tnum rounded-full bg-info px-1.5 text-[9.5px] font-semibold text-surface" title={`${badge} finding${badge === 1 ? "" : "s"} to check on this game`}>{badge}</span>}
          </button>
        );
      })}
      {mode === "struct" && layout && <span className="ml-auto hidden truncate font-code text-[10.5px] text-fg-3 sm:inline" title={layout.source}>{layout.source}</span>}
    </div>
  );

  const other = mode === "population" ? <Population store={store} snap={snap} fullscreen={fullscreen} host={textHost} />
    : mode === "experiments" ? <Experiments store={store} snap={snap} fullscreen={fullscreen} host={textHost} now={now} />
    : mode === "findings" ? <Findings store={store} snap={snap} fullscreen={fullscreen} host={textHost} /> : null;

  return (
    <div className={fullscreen ? "flex h-screen flex-col overflow-hidden" : "flex flex-col"}>
      <header className="flex h-11 items-center gap-2 px-3">
        <span className="grid h-6 shrink-0 place-items-center rounded-md bg-fg px-1.5 text-[11px] font-bold tracking-tight text-surface" title={gameLabel}>
          {game === "poe2" ? "PoE 2" : game === "poe1" ? "PoE 1" : "PoE"}
        </span>
        <h1 className="truncate text-[13px] font-semibold">Memory view</h1>
        {layout && <span className="hidden truncate font-code text-[11px] text-fg-3 xs:inline" title={`${layout.struct}${layout.source ? ` — ${layout.source}` : ""}${layout.object ? `\n${layout.object}` : ""}`}>{typeLabel(layout.struct)}</span>}
        {read && !layout && <span className="hidden truncate font-code text-[11px] text-fg-3 xs:inline" title={read.region}>raw · {read.module?.name}</span>}
        <div className="ml-auto flex shrink-0 items-center gap-1">
          <span className={`hidden items-center gap-1.5 rounded-full border px-2 text-[11px] font-medium xs:flex ${offline ? "border-danger/30 bg-danger/10 text-danger" : snap.conn === "live" ? "border-success/30 bg-success/10 text-success" : "border-line text-fg-3"}`} role="status"
            title={offline ? "The HUD bridge is unreachable" : snap.conn === "live" ? "The HUD bridge answers; this is the connection, not a live re-read (that's the Live toggle)" : "Waiting for the first answer from the HUD bridge"}>
            {offline ? <Icon name="offline" className="size-3" /> : <span className={`size-1.5 rounded-full ${snap.conn === "live" ? "bg-success" : "bg-fg-3"}`} />}
            {offline ? "Offline" : snap.conn === "live" ? "Connected" : "Connecting"}
          </span>
          <button
            type="button"
            onClick={() => store.setLive(!snap.live)}
            aria-pressed={snap.live}
            disabled={!region}
            title={`Re-read these bytes every ${LIVE_MS / 1000} s and flash what changed (one memory_read per tick; the bridge runs on the game thread)`}
            className={`flex h-7 items-center gap-1 rounded-md border px-1.5 text-[11px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-40 ${snap.live ? "border-fg bg-fg text-surface" : "border-transparent text-fg-2 hover:bg-surface-3 hover:text-fg"}`}
          >
            <Icon name="radio" className={`size-3.5 ${snap.live ? "animate-pulse" : ""}`} /><span className="hidden sm:inline">Live</span>
          </button>
          {host.fullscreen && (
            <IconButton icon={fullscreen ? "minimize" : "maximize"} label={fullscreen ? "Back to the conversation" : "Expand"} onClick={host.fullscreen.toggle} />
          )}
        </div>
      </header>

      {tabs}
      {mode === "struct" && <TargetBar store={store} snap={snap} view={view} />}

      {mode !== "struct" ? (
        <div className={fullscreen ? "flex min-h-0 flex-1 flex-col gap-2 p-3 sm:p-4 sm:pt-3" : "flex flex-col gap-2.5 px-3 pb-3 pt-2.5"}>
          {banners}
          {other}
        </div>
      ) : fullscreen ? (
        <>
          <div className="flex flex-col gap-2 px-4 pt-3 empty:hidden">{banners}{summary}</div>
          <div className="grid min-h-0 flex-1 grid-cols-1 grid-rows-[minmax(0,3fr)_minmax(0,2fr)] gap-3 p-3 pt-2 sm:grid-cols-[minmax(0,1fr)_minmax(19rem,24rem)] sm:grid-rows-1 sm:gap-4 sm:p-4 sm:pt-3 lg:grid-cols-[minmax(0,1fr)_auto_minmax(20rem,25rem)]">
            {mapCard}
            {hexCard && <div className="hidden min-h-0 lg:flex lg:flex-col">{hexCard}</div>}
            <aside className="scroll-thin flex min-h-0 flex-col gap-3 overflow-y-auto pr-1">
              {inspector}
              {codePanel}
              {watchPanel}
              {hexCard && <div className="flex max-h-[22rem] flex-col lg:hidden">{hexCard}</div>}
            </aside>
          </div>
        </>
      ) : (
        <div className="flex flex-col gap-2.5 px-3 pb-3 pt-2.5">
          {banners}
          {summary}
          {mapCard}
          {inspector}
          {codePanel}
          {hexCard}
          {watchPanel}
        </div>
      )}

      <Toasts toasts={snap.toasts} onDismiss={(id) => store.dismiss(id)} />
    </div>
  );
}

function mappedBytes(region: Region): number {
  return region.segs.filter((s) => s.kind === "field").reduce((n, s) => n + s.size, 0);
}
function unmappedBytes(region: Region): number {
  const end = region.structSize ?? region.size;
  return region.segs.filter((s) => s.kind !== "field" && s.off < end).reduce((n, s) => n + Math.min(s.size, end - s.off), 0);
}

function ago(ms: number): string {
  if (ms < 2_000) return "read just now";
  if (ms < 60_000) return `read ${Math.round(ms / 1000)} s ago`;
  return `read ${Math.round(ms / 60_000)} min ago`;
}

function Stat({ tone, label, detail, strong, onClick }: { tone: "field" | "gap" | "cand" | "ptr" | "warning" | "danger" | "ok"; label: string; detail?: string; strong?: boolean; onClick?: () => void }) {
  const dot = tone === "field" ? { background: "var(--color-m-field)" } : tone === "cand" ? { background: "var(--color-m-cand)" } : tone === "ptr" ? { background: "var(--color-m-ptr)" }
    : tone === "warning" ? { background: "var(--color-warning)" } : tone === "danger" ? { background: "var(--color-danger)" } : tone === "ok" ? { background: "var(--color-success)" } : undefined;
  const text = tone === "cand" && strong ? "text-m-cand" : tone === "warning" ? "text-warning" : tone === "danger" ? "text-danger" : "text-fg-2";
  const Tag = onClick ? "button" : "span";
  return (
    <Tag type={onClick ? "button" : undefined} onClick={onClick} className={`inline-flex h-5 items-center gap-1.5 rounded-md border border-line bg-surface px-1.5 ${text} ${onClick ? "hover:border-line-2" : ""}`} title={detail}>
      <span className={`size-1.5 shrink-0 rounded-[2px] ${tone === "gap" ? "m-hatch bg-surface-3" : ""}`} style={dot} aria-hidden />
      <span className="tnum font-medium">{label}</span>
      {detail && <span className="tnum hidden text-fg-3 sm:inline">{detail}</span>}
    </Tag>
  );
}

function LoadingMap() {
  return (
    <div className="px-2 py-2" aria-hidden>
      <div className="shimmer h-4 rounded-sm" />
      <div className="mt-3 space-y-1.5">
        {Array.from({ length: 7 }, (_, i) => (
          <div key={i} className="flex items-center gap-2">
            <div className="shimmer h-2.5 w-12 rounded" />
            <div className="shimmer h-2.5 rounded" style={{ width: `${30 + (i % 3) * 15}%` }} />
            <div className="shimmer ml-auto h-2.5 w-16 rounded" />
          </div>
        ))}
      </div>
    </div>
  );
}

function ErrorState({ store, view, offline }: { store: MemoryStore; view: View; offline: boolean }) {
  const e = view.error!;
  const target = view.target.path ?? view.target.address ?? "";
  const code = offline || e.error === "offline" ? "offline" : e.error;
  const title = code === "offline" ? "Nothing loaded" : code === "no_address" ? "Not a memory object" : code === "no_struct" ? "No struct to overlay" : code === "unreadable" ? "Not readable memory" : "Could not read this target";
  const hint = code === "offline" ? "The bridge is unreachable. Retry once the HUD is running."
    : code === "no_address" ? "Only objects with an Address can be viewed: entities, components, UI elements, server data records. Pick one of those, or enter an address."
    : code === "no_struct" ? "The HUD caches no offsets struct for this object. Enter a struct type in the options (hud_type lists GameOffsets structs), or read the bytes raw."
    : code === "unreadable" ? "The object may have been freed since, or the address is off. Go back to where the pointer came from and read again."
    : undefined;
  return (
    <EmptyState icon={code === "offline" ? "offline" : "warning"} title={title} className="py-5">
      <span className="code-wrap block break-words text-danger">{e.message ?? e.error}</span>
      {hint && <span className="mt-1 block">{hint}</span>}
      <div className="mt-2 flex flex-wrap justify-center gap-1.5">
        <SmallButton icon="sync" onClick={() => store.reload()}>Retry</SmallButton>
        {code === "no_struct" && view.target.address && <SmallButton icon="grid" onClick={() => void store.open({ address: view.target.address }, { mode: "read" })}>Read raw bytes</SmallButton>}
        {code === "no_struct" && view.target.path && <SmallButton icon="grid" onClick={() => void store.open({ path: view.target.path, size: 256 }, { mode: "read" })}>Read raw bytes</SmallButton>}
        {store.getSnapshot().index > 0 && <SmallButton icon="arrowLeft" onClick={() => store.back()}>Back</SmallButton>}
        {target !== "GameController.Player.GetComponent<Life>()" && <SmallButton onClick={() => void store.open({ path: "GameController.Player.GetComponent<Life>()" })}>Player's Life</SmallButton>}
      </div>
    </EmptyState>
  );
}

function askText(view: View, region: Region, sel: Selection, seg: Seg | undefined, game: string | undefined, ranges: { off: number; size: number; first: string; last: string; bitsFlipped?: number[]; bitsRelativeTo?: string; changes: number }[]): string {
  const g = game === "poe2" ? "PoE2" : game === "poe1" ? "PoE1" : "PoE";
  const layout = view.data && "fields" in view.data ? (view.data as LayoutResult) : undefined;
  const where = layout ? `${typeLabel(layout.struct)} (${view.label}, ${g})` : `the ${region.size}-byte object at ${region.address} (${view.label}, ${g})`;
  const bytes = hexOf(region.bytes, sel.off, sel.size);
  const changed = ranges.filter((c) => c.off < sel.off + sel.size && c.off + c.size > sel.off);
  const diff = changed.length ? ` During a watch these bytes changed ${changed.map((c) => `${c.first} -> ${c.last}${c.bitsFlipped?.length ? ` (bits ${c.bitsFlipped.join(", ")}${c.bitsRelativeTo ? ` of ${c.bitsRelativeTo}` : ""} flipped)` : ""} ×${c.changes}`).join("; ")} while I did one thing in game: what state does that encode?` : "";
  if (seg?.cand) {
    const c = seg.cand;
    return `What is the unmapped ${KIND_LABEL[c.kind] ?? c.kind} at +${sel.off} (${hexOff(sel.off)}) in ${where}?${c.ghidra ? ` Check it in Ghidra at ${c.ghidra}${c.firstMethod ? ` (first method ${c.firstMethod})` : ""}.` : ""}${c.detail ? ` ${c.detail}.` : c.value ? ` Value ${c.value}.` : ""} Bytes: ${bytes}.${diff} Short answer, with how the HUD struct would map it.`;
  }
  if (seg?.field) {
    const f = seg.field;
    return `Is ${f.name} (${f.type}) at +${sel.off} (${hexOff(sel.off)}) in ${where} still mapped correctly? Live value ${fmtValue(f.value)}, bytes ${bytes}${f.check !== "ok" ? `, check: ${f.check}${f.why ? ` (${f.why})` : ""}` : ""}${f.bits?.length ? `, set bits ${f.bits.join(", ")}` : ""}.${diff} Short answer.`;
  }
  if (seg?.slot) {
    const s = seg.slot;
    return `In ${where}, the 8 bytes at +${sel.off} (${hexOff(sel.off)}) read as ${KIND_LABEL[s.kind] ?? s.kind} ${s.hex}${s.points ? ` pointing at ${s.points}` : ""}${s.ghidra ? ` (Ghidra ${s.ghidra})` : ""}. What is it likely to be?${diff} Short answer.`;
  }
  return `What is in the unmapped bytes +${sel.off}..+${sel.off + sel.size - 1} (${hexOff(sel.off)}, ${sel.size} bytes) of ${where}? Bytes: ${bytes}.${diff} Short answer.`;
}
