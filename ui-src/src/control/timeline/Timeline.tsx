// The Timeline view of the observer: every lane (one per layer, then ui, area · level, entity, hud, agent) on one time
// axis. Follow slides the newest events in; any pan or zoom pauses it. Lanes collapse on click, kinds filter with chips,
// HUD spikes dim what they cover and agent prompts mark what follows them. Clicking a mark opens the detail column.

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Banner, EmptyState } from "../../components";
import { Icon } from "../../icons";
import type { ControlStore, Snapshot } from "../store";
import { T } from "../tour/ids";
import { useTours } from "../tour/engine";
import type { ObserveEvent } from "../types";
import { Badge, Button, Card, Segmented, ShowMe, Switch } from "../ui";
import { AXIS_H, LaneCanvas, ROW_H, ROW_HIDDEN_H, type Hover } from "./Canvas";
import { EventDetail } from "./Detail";
import { DEFAULT_FILTERS, KINDS, KIND_TONE, SPAN_PRESETS, atMs, buildLanes, eventShort, fitSpan, fmtClock, fmtMs, isShown, mergeEvents, windowsOf, zoomAt, type Filters, type Lane, type Viewport } from "./model";

const FOLLOW_LEAD = 0.04;

export function TimelineView({ store, snap, onOpenLayerMap }: { store: ControlStore; snap: Snapshot; onOpenLayerMap: (layer: string) => void }) {
  const o = snap.observer;
  const tl = snap.timeline;
  const tours = useTours();
  const noHud = store.gamesUp.length === 0 && snap.gamesAt !== undefined;

  const events = useMemo(() => mergeEvents(o.events, tl.journal), [o.events, tl.journal]);
  const lanes = useMemo(() => buildLanes(events, o.layers?.layers ?? o.status?.layers), [events, o.layers, o.status?.layers]);
  const windows = useMemo(() => windowsOf(events), [events]);
  const [filters, setFilters] = useState<Filters>(DEFAULT_FILTERS);
  const [follow, setFollow] = useState(true);
  const [span, setSpan] = useState(60_000);
  const [vp, setVp] = useState<Viewport>(() => ({ end: Date.now() + 60_000 * FOLLOW_LEAD, span: 60_000 }));
  const [now, setNow] = useState(() => Date.now());
  const [hover, setHover] = useState<Hover>();
  const [legend, setLegend] = useState(false);
  const canvasWidth = useRef(600);
  const wrap = useRef<HTMLDivElement>(null);

  // Follow: the window's end tracks the clock (4 ticks a second is smooth enough and cheap).
  useEffect(() => {
    if (!follow) return;
    const tick = () => { if (document.hidden) return; const n = Date.now(); setNow(n); setVp((v) => ({ span: v.span, end: n + v.span * FOLLOW_LEAD })); };
    tick();
    const t = setInterval(tick, 250);
    return () => clearInterval(t);
  }, [follow]);
  useEffect(() => { if (!follow) { const t = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(t); } }, [follow]);

  const setViewport = useCallback((v: Viewport, user: boolean) => { setVp(v); setSpan(v.span); if (user) setFollow(false); }, []);
  const pickSpan = (s: number) => { setSpan(s); setVp((v) => ({ span: s, end: follow ? Date.now() + s * FOLLOW_LEAD : v.end - v.span / 2 + s / 2 })); };
  const fit = () => { setFollow(false); const v = fitSpan(events, Date.now()); setVp(v); setSpan(v.span); };
  const resume = () => { setFollow(true); setVp((v) => ({ span: v.span, end: Date.now() + v.span * FOLLOW_LEAD })); };
  const zoom = (f: number) => setViewport(zoomAt(vp, f, canvasWidth.current / 2, canvasWidth.current), !follow);

  const shown = useMemo(() => events.filter((e) => isShown(e, filters)), [events, filters]);
  const select = useCallback((seq: number | undefined) => store.selectTimelineEvent(seq, tl.around?.windowMs ?? 1000), [store, tl.around?.windowMs]);
  const selected = tl.selected !== undefined ? store.eventBySeq(tl.selected) : undefined;

  // Selecting an event off screen brings it into view (paused).
  const reveal = useCallback((e: ObserveEvent) => {
    const t = atMs(e);
    setVp((v) => (t >= v.end - v.span && t <= v.end ? v : { span: v.span, end: t + v.span / 2 }));
    if (t < vp.end - vp.span || t > vp.end) setFollow(false);
  }, [vp]);

  const onKey = (e: React.KeyboardEvent) => {
    if ((e.target as HTMLElement).tagName === "INPUT") return;
    const idx = selected ? shown.findIndex((x) => x.seq === selected.seq) : -1;
    const go = (i: number) => { const x = shown[Math.max(0, Math.min(shown.length - 1, i))]; if (x) { select(x.seq); reveal(x); } };
    if (e.key === "ArrowLeft") { e.preventDefault(); go(idx < 0 ? shown.length - 1 : idx - 1); }
    else if (e.key === "ArrowRight") { e.preventDefault(); go(idx < 0 ? shown.length - 1 : idx + 1); }
    else if (e.key === "Home") { e.preventDefault(); go(0); }
    else if (e.key === "End") { e.preventDefault(); go(shown.length - 1); }
    else if (e.key === "Escape") { select(undefined); }
    else if (e.key === "+" || e.key === "=") { e.preventDefault(); zoom(0.5); }
    else if (e.key === "-") { e.preventDefault(); zoom(2); }
    else if (e.key === " ") { e.preventDefault(); if (follow) setFollow(false); else resume(); }
  };

  const toggleLane = (id: string) => setFilters((f) => { const n = new Set(f.hiddenLanes); if (n.has(id)) n.delete(id); else n.add(id); return { ...f, hiddenLanes: n }; });
  const toggleKind = (k: string) => setFilters((f) => { const n = new Set(f.hiddenKinds); if (n.has(k)) n.delete(k); else n.add(k); return { ...f, hiddenKinds: n }; });
  const counts = useMemo(() => { const c: Record<string, number> = {}; for (const e of events) c[e.kind] = (c[e.kind] ?? 0) + 1; return c; }, [events]);
  const spikes = counts.hud ?? 0, agents = counts.agent ?? 0;
  const off = o.status?.enabled === false;
  const empty = events.length === 0;

  return (
    <div className="flex flex-col gap-3" onKeyDown={onKey}>
      {noHud && <Banner tone="warning" icon="offline" title="No HUD is up">The observer runs inside the HUD. Start one with "Whats An AI Bridge" enabled; the timeline fills as events arrive.</Banner>}
      {o.error && <Banner tone="danger" icon="warning" title="The observer did not answer" action={<Button size="sm" onClick={() => void store.refreshObserver()}>Retry</Button>}>{o.error}</Banner>}

      <div className="grid grid-cols-1 gap-3 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)] lg:items-start">
        <Card title="Timeline" icon="activity" pad={false} tour={T.tlView}
          right={<>
            <span className="flex items-center gap-1 text-[10.5px] text-fg-3" title={o.lastEventAt ? `last event ${fmtClock(o.lastEventAt, false)}` : "no events yet"}>
              <span className={`size-1.5 rounded-full ${off ? "bg-fg-3" : follow ? "bg-success live-dot" : "bg-warning"}`} />{off ? "off" : follow ? "live" : "paused"}
            </span>
            <span className="flex items-center gap-1" data-tour={T.tlControls}>
              <Button size="sm" icon={follow ? "pause" : "play"} tone={follow ? "default" : "primary"} onClick={() => (follow ? setFollow(false) : resume())} title={follow ? "Pause: the axis stops sliding (events keep arriving). Space" : "Follow live: the newest events slide in. Space"}>{follow ? "Pause" : "Follow"}</Button>
              <Button size="sm" onClick={() => zoom(2)} title="Zoom out (−, or ctrl+wheel)" ariaLabel="Zoom out">−</Button>
              <Button size="sm" onClick={() => zoom(0.5)} title="Zoom in (+, or ctrl+wheel)" ariaLabel="Zoom in">+</Button>
              <Segmented size="sm" label="Window" value={String(SPAN_PRESETS.find((p) => p.span === span)?.span ?? "custom")} onChange={(v) => { if (v !== "custom") pickSpan(Number(v)); }}
                options={[...SPAN_PRESETS.map((p) => ({ value: String(p.span), label: p.label })), ...(SPAN_PRESETS.some((p) => p.span === span) ? [] : [{ value: "custom", label: fmtMs(span) }])]} />
              <Button size="sm" onClick={fit} disabled={empty} title="Fit every event in memory">Fit</Button>
            </span>
            <ShowMe onClick={() => tours.start("timeline")} done={tours.done.has("timeline")} />
          </>}>
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-line px-3 py-1.5" data-tour={T.tlFilters}>
            <div className="flex flex-wrap gap-1">
              {KINDS.map((k) => {
                const n = counts[k] ?? 0;
                const on = !filters.hiddenKinds.has(k);
                return (
                  <button key={k} type="button" onClick={() => toggleKind(k)} aria-pressed={on} disabled={!n} title={on ? `Hide ${k} events` : `Show ${k} events`}
                    className={`ds-badge inline-flex h-5 items-center gap-1 rounded-sm border px-1.5 transition-colors disabled:opacity-40 ${on ? "border-line text-fg-2 hover:border-line-2" : "border-dashed border-line text-fg-3 line-through"}`}>
                    {k.replace(".noisy", " noisy")}<span className="tnum opacity-70">{n}</span>
                  </button>
                );
              })}
            </div>
            <label className="flex items-center gap-1.5 text-[11px] text-fg-2" title="Layer, ui and entity events read inside a HUD frame that ran long were measured late: dim them">
              <Switch size="sm" checked={filters.dimInSpikes} onChange={(v) => setFilters((f) => ({ ...f, dimInSpikes: v }))} label="Dim events inside HUD spikes" />dim in spikes{spikes ? <span className="tnum text-fg-3">{spikes}</span> : null}
            </label>
            <label className="flex items-center gap-1.5 text-[11px] text-fg-2" title="After an agent asks the user to do something (guide, highlight, experiment), mark the 10 s that follow: that is the user's action">
              <Switch size="sm" checked={filters.markAfterAgent} onChange={(v) => setFilters((f) => ({ ...f, markAfterAgent: v }))} label="Mark events after an agent prompt" />after agent{agents ? <span className="tnum text-fg-3">{agents}</span> : null}
            </label>
            <button type="button" onClick={() => setLegend((l) => !l)} aria-expanded={legend} className="ml-auto text-[11px] text-fg-3 hover:text-fg hover:underline">{legend ? "Hide legend" : "Legend"}</button>
          </div>

          {empty ? (
            off ? (
              <EmptyState icon="eyeOff" title="Observation is off" className="py-8">Nothing is recorded. Turn it on and the lanes fill as you play (read-only, never input).
                <div className="mt-2 flex justify-center"><Button size="sm" tone="primary" icon="eye" busy={o.busy} disabled={!snap.game} onClick={() => void store.setObserving(true)}>Turn observation on</Button></div>
              </EmptyState>
            ) : (
              <EmptyState icon={o.error || noHud ? "offline" : "activity"} title={o.status ? "Nothing yet" : o.error || noHud ? "No events to show" : "Waiting for the observer"} className="py-8">
                {o.status ? <>Open a panel, change area or let a layer tick: marks appear on their lane within a second or two.<span className="mt-1 flex items-center justify-center gap-1.5"><span className="size-1.5 rounded-full bg-success live-dot" />listening</span></>
                  : o.error || noHud ? "The observer lives in the HUD and is not answering (see above). The lanes fill as soon as it does; the journal on disk is kept."
                  : "Reading the observer's status…"}
              </EmptyState>
            )
          ) : (
            <div ref={wrap} tabIndex={0} aria-label="Timeline. Arrows step through events, Home and End jump, + and − zoom, Space pauses, Esc clears" className="relative grid grid-cols-[5rem_minmax(0,1fr)] outline-none focus-visible:ring-1 focus-visible:ring-inset focus-visible:ring-ring/50 xs:grid-cols-[7rem_minmax(0,1fr)]">
              <LaneLabels lanes={lanes} hidden={filters.hiddenLanes} onToggle={toggleLane} />
              <div className="min-w-0" ref={(el) => { if (el) canvasWidth.current = el.getBoundingClientRect().width || canvasWidth.current; }}>
                <LaneCanvas events={events} lanes={lanes} vp={vp} now={now} filters={filters} windows={windows} selected={tl.selected}
                  onSelect={select} onViewport={setViewport} onHover={setHover} />
              </div>
              {hover && <Tooltip hover={hover} events={events} anchor={wrap.current} />}
            </div>
          )}
          {legend && <Legend />}
          {!empty && (
            <div className="flex flex-wrap items-center gap-x-3 gap-y-0.5 border-t border-line px-3 py-1 text-[10.5px] text-fg-3">
              <span className="tnum">{shown.length} of {events.length} events shown</span>
              <span className="tnum hidden xs:inline">{fmtClock(vp.end - vp.span, false)} – {fmtClock(vp.end, false)}</span>
              {Object.keys(tl.journal).length > 0 && <span className="tnum">+{Object.keys(tl.journal).length} from the journal</span>}
              <span className="ml-auto hidden sm:inline">drag pans · ctrl+wheel zooms · shift+wheel pans · click selects</span>
            </div>
          )}
        </Card>

        <Card title={selected ? eventShort(selected) : "Event"} icon="target" pad={false} className="lg:sticky lg:top-0">
          <EventDetail store={store} snap={snap} event={selected} windows={windows} onSelect={(seq) => { select(seq); const e = store.eventBySeq(seq); if (e) reveal(e); wrap.current?.focus({ preventScroll: true }); }} onOpenLayerMap={onOpenLayerMap} />
        </Card>
      </div>
    </div>
  );
}

function LaneLabels({ lanes, hidden, onToggle }: { lanes: Lane[]; hidden: ReadonlySet<string>; onToggle: (id: string) => void }) {
  return (
    <div className="flex flex-col border-r border-line" data-tour={T.tlLanes} style={{ paddingTop: AXIS_H }}>
      {lanes.map((l) => {
        const off = hidden.has(l.id);
        const tone = l.kind === "layer" ? "bg-m-field" : l.kind === "ui" ? "bg-ring" : l.kind === "world" ? "bg-success" : l.kind === "entity" ? "bg-m-cand" : l.kind === "hud" ? "bg-p-spike" : "bg-fg";
        return (
          <button key={l.id} type="button" onClick={() => onToggle(l.id)} aria-pressed={!off}
            title={off ? `Show the ${l.label} lane` : `${l.label}${l.mode ? ` (${l.mode})` : ""}: ${l.count} events${l.noisy ? `, ${l.noisy} noisy` : ""}${l.unmapped ? `, ${l.unmapped} unmapped` : ""}. Click to collapse`}
            className={`group flex items-center gap-1.5 overflow-hidden border-b border-line px-1.5 text-left transition-colors hover:bg-surface-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring ${off ? "opacity-60" : ""}`} style={{ height: off ? ROW_HIDDEN_H : ROW_H }}>
            {off ? <span className={`h-px w-full ${tone} opacity-50`} /> : (
              <>
                <span className={`h-3 w-[3px] shrink-0 rounded-xs ${tone} ${l.state === "paused" || l.state === "gone" ? "opacity-30" : l.state === "broken" ? "bg-danger" : ""}`} aria-hidden />
                <span className={`min-w-0 flex-1 truncate ${l.kind === "layer" ? "font-code text-[11px] font-semibold" : "ds-badge text-fg-2"}`}>{l.label}</span>
                {l.kind === "layer" && l.mode && <span className="hidden text-[9.5px] text-fg-3 xs:inline">{l.mode}</span>}
                {l.state === "broken" && <Icon name="warning" className="size-3 shrink-0 text-danger" />}
                {(l.state === "paused" || l.state === "gone") && <Icon name="pause" className="size-2.5 shrink-0 text-fg-3" />}
                <span className="tnum hidden text-[9.5px] text-fg-3 xs:inline">{l.count}</span>
              </>
            )}
          </button>
        );
      })}
    </div>
  );
}

function Tooltip({ hover, events, anchor }: { hover: Hover; events: readonly ObserveEvent[]; anchor: HTMLElement | null }) {
  const r = anchor?.getBoundingClientRect();
  const left = hover.clientX - (r?.left ?? 0), top = hover.clientY - (r?.top ?? 0);
  const list = hover.bucket.seqs.slice(0, 5).map((s) => events.find((e) => e.seq === s)).filter((e): e is ObserveEvent => !!e);
  const flip = r ? left > r.width * 0.6 : false;
  return (
    <div role="tooltip" className="pointer-events-none absolute z-20 max-w-[18rem] rounded-sm border border-line bg-surface p-1.5 text-[10.5px] shadow-lg" style={{ left: flip ? undefined : left + 12, right: flip ? (r ? r.width - left + 12 : undefined) : undefined, top: Math.max(0, top - 10) }}>
      {list.map((e) => (
        <div key={e.seq} className="flex items-baseline gap-1.5 whitespace-nowrap">
          <span className="tnum text-fg-3">{fmtClock(atMs(e))}</span>
          <Badge tone={KIND_TONE[e.kind] ?? "neutral"}>{e.kind.replace(".noisy", "")}</Badge>
          <span className="truncate font-code">{eventShort(e)}</span>
        </div>
      ))}
      {hover.bucket.seqs.length > 5 && <div className="text-fg-3">… {hover.bucket.seqs.length - 5} more here: zoom in, or click again for the next</div>}
      {hover.bucket.seqs.length > 1 && hover.bucket.seqs.length <= 5 && <div className="text-fg-3">click again for the next one here</div>}
    </div>
  );
}

function Legend() {
  const Sw = ({ children, label }: { children: React.ReactNode; label: string }) => <span className="flex items-center gap-1.5 whitespace-nowrap"><span className="grid h-4 w-4 place-items-center">{children}</span>{label}</span>;
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-t border-line px-3 py-1.5 text-[10.5px] text-fg-2" data-tour={T.tlLegend}>
      <Sw label="mapped unit"><span className="h-3 w-[2px] bg-m-field" /></Sw>
      <Sw label="unmapped unit"><span className="h-3 w-[3px] border border-fg-3" /></Sw>
      <Sw label="added"><span className="h-0 w-0 border-x-[4px] border-b-[7px] border-x-transparent border-b-success" /></Sw>
      <Sw label="removed"><span className="h-0 w-0 border-x-[4px] border-t-[7px] border-x-transparent border-t-danger" /></Sw>
      <Sw label="panel opened / closed"><span className="h-3 w-2 border-l-2 border-t-2 border-ring" /></Sw>
      <Sw label="unmapped panel"><span className="h-3 w-2 border-l border-t border-warning" /></Sw>
      <Sw label="area (level dashed)"><span className="h-4 w-px bg-success" /></Sw>
      <Sw label="entity kind"><span className="h-3 w-[2px] bg-m-cand" /></Sw>
      <Sw label="HUD spike (GC base)"><span className="relative h-3 w-3 bg-p-spike"><span className="absolute inset-x-0 bottom-0 h-[2px] bg-p-gc" /></span></Sw>
      <Sw label="plugin reload"><span className="h-3 w-3 bg-p-gc" /></Sw>
      <Sw label="agent call"><span className="size-2 rotate-45 bg-fg" /></Sw>
      <Sw label="after an agent prompt"><span className="size-1.5 rounded-full bg-ring" /></Sw>
      <Sw label="now"><span className="h-4 w-px border-l border-dashed border-success" /></Sw>
    </div>
  );
}
