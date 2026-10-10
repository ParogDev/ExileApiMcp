// The selected event in depth: its fields, the windows it sits in (a HUD spike, the seconds after an agent prompt),
// what happened around it from the journal (observe_timeline around=seq, with a window picker), and for a layer event
// the unit's companions (observe_timeline layer+unit) and its series (observe_series, when the server offers it).

import { useMemo, type ReactNode } from "react";
import { EmptyState } from "../../components";
import { Icon } from "../../icons";
import type { ControlStore, Snapshot } from "../store";
import { T } from "../tour/ids";
import type { ObserveEvent, SeriesResult, TimelineCompanion } from "../types";
import { Badge, Button, Copy, Segmented, Spinner, fmtNum } from "../ui";
import { KIND_TONE, atMs, eventFields, eventLine, eventShort, fmtClock, fmtDt, fmtMs, inWindow, type Window } from "./model";

const AROUND_WINDOWS = [250, 1000, 5000, 30_000];

export function EventDetail({ store, snap, event, windows, onSelect, onOpenLayerMap }: {
  store: ControlStore; snap: Snapshot; event?: ObserveEvent; windows: readonly Window[]; onSelect: (seq: number) => void; onOpenLayerMap: (layer: string) => void;
}) {
  const tl = snap.timeline;
  const latest = snap.observer.events[snap.observer.events.length - 1];
  if (!event) {
    return (
      <EmptyState icon="target" title="Nothing selected" className="py-8">
        Click a mark for its details, what happened within a second of it, and (for a layer unit) what usually happens at the same moments.
        <span className="mt-1 block">Arrows step through events; <kbd className="rounded-sm border border-line bg-surface-2 px-1 font-code text-[10px] text-fg-2">Esc</kbd> clears.</span>
        {latest && <div className="mt-2 flex justify-center"><Button size="sm" onClick={() => onSelect(latest.seq)} tour={T.tlSelectLatest}>Select the latest event</Button></div>}
      </EmptyState>
    );
  }
  const t = atMs(event);
  const spike = inWindow(windows, t, "spike") ?? inWindow(windows, t, "reload");
  const agent = event.kind !== "agent" ? inWindow(windows, t, "agent") : undefined;
  const fields = eventFields(event);
  const json = JSON.stringify(event, null, 2);
  const isLayer = event.kind === "layer" && !!event.layer && !!event.unit;

  return (
    <div className="flex flex-col divide-y divide-line" data-tour={T.tlDetail}>
      <div className="flex flex-col gap-1.5 p-3">
        <div className="flex flex-wrap items-center gap-1.5">
          <Badge tone={KIND_TONE[event.kind] ?? "neutral"}>{event.kind}</Badge>
          {event.kind === "hud" && <Badge tone={event.cause === "reload" ? "accent" : "warning"}>{event.cause}</Badge>}
          {event.kind === "layer" && event.mode === "struct" && <Badge tone={event.name ? "info" : "neutral"} className={event.name ? "" : "m-hatch"}>{event.name ? "mapped" : "unmapped"}</Badge>}
          {event.kind === "ui" && !event.mapped && <Badge tone="warning" title="The HUD has no property for this panel">unmapped</Badge>}
          <span className="tnum text-[11px] text-fg-3" title={event.at}>{fmtClock(t)}</span>
          <span className="tnum text-[10.5px] text-fg-3">#{event.seq}{event.frame != null ? ` · frame ${event.frame}` : ""}{event.t != null ? ` · t ${fmtNum(event.t / 1000, 2)} s` : ""}</span>
          <span className="ml-auto flex items-center gap-0.5">
            <Copy text={json} label="Copy the event as JSON" />
            {isLayer && <Button size="sm" tone="ghost" icon="grid" title={`Open the ${event.layer} layer map`} onClick={() => onOpenLayerMap(event.layer!)} ariaLabel="Open the layer map" />}
            {store.host.ask && <Button size="sm" tone="ghost" icon="sparkle" title="Ask Claude about this event" onClick={() => store.host.ask!(askText(event, tl.around?.result?.events, tl.companions?.result?.companions))} ariaLabel="Ask Claude" />}
          </span>
        </div>
        <p className="break-words font-code text-[11.5px] leading-snug">{eventLine(event)}</p>
        {(spike || agent) && (
          <div className="flex flex-wrap gap-1">
            {spike && (
              <button type="button" onClick={() => onSelect(spike.seq)} className="inline-flex h-5 items-center gap-1 rounded-sm border border-p-spike/40 bg-p-spike/10 px-1.5 ds-badge text-p-spike hover:bg-p-spike/20" title="This event was read inside a HUD frame that ran long: its timing is the HUD's, not the game's. Click for the spike.">
                <Icon name="warning" className="size-2.5" />inside a HUD {spike.kind} · {spike.label}
              </button>
            )}
            {agent && (
              <button type="button" onClick={() => onSelect(agent.seq)} className="inline-flex h-5 items-center gap-1 rounded-sm border border-ring/40 bg-ring/10 px-1.5 ds-badge text-fg hover:bg-ring/20" title="An agent asked the user to do something shortly before: this is likely the user's action. Click for the agent's call.">
                <Icon name="sparkle" className="size-2.5" />{fmtDt(t - agent.from)} {agent.label}
              </button>
            )}
          </div>
        )}
        {fields.length > 0 && (
          <dl className="grid grid-cols-[minmax(4rem,max-content)_1fr] gap-x-3 gap-y-0.5 text-[11.5px]">
            {fields.map(([k, v]) => (
              <div key={k} className="contents">
                <dt className="text-fg-3">{k}</dt>
                <dd className="min-w-0 break-words font-code text-[11px]">{renderValue(v)}</dd>
              </div>
            ))}
          </dl>
        )}
      </div>

      <Around store={store} snap={snap} event={event} onSelect={onSelect} />
      {isLayer && <Companions store={store} snap={snap} event={event} />}
      {isLayer && <Series store={store} snap={snap} event={event} />}
    </div>
  );
}

function renderValue(v: unknown): ReactNode {
  if (v === null || v === undefined) return <span className="text-fg-3">–</span>;
  if (typeof v === "boolean") return <span className="text-k-bool">{String(v)}</span>;
  if (typeof v === "number") return <span className="tnum text-k-num">{String(v)}</span>;
  if (typeof v === "string") return v;
  if (Array.isArray(v)) return v.map((x, i) => <span key={i}>{i > 0 && <span className="text-fg-3"> | </span>}{String(x)}</span>);
  return <span className="code-wrap">{JSON.stringify(v)}</span>;
}

function Section({ title, right, children, tour }: { title: ReactNode; right?: ReactNode; children: ReactNode; tour?: string }) {
  return (
    <section className="flex flex-col gap-1.5 p-3" data-tour={tour}>
      <header className="flex flex-wrap items-center gap-x-2 gap-y-1"><h3 className="ds-label flex-1 text-fg-3">{title}</h3>{right}</header>
      {children}
    </section>
  );
}

function Around({ store, snap, event, onSelect }: { store: ControlStore; snap: Snapshot; event: ObserveEvent; onSelect: (seq: number) => void }) {
  const a = snap.timeline.around;
  const win = a?.windowMs ?? 1000;
  const t0 = atMs(event);
  const rows = useMemo(() => (a?.result?.events ?? []).map((e) => ({ e, dt: atMs(e) - t0 })).sort((x, y) => x.dt - y.dt || x.e.seq - y.e.seq), [a?.result, t0]);
  const others = rows.filter((r) => r.e.seq !== event.seq);
  return (
    <Section title="Around this moment" tour={T.tlAround}
      right={<Segmented size="sm" label="Window" value={String(win)} options={AROUND_WINDOWS.map((w) => ({ value: String(w), label: `±${fmtMs(w)}` }))} onChange={(v) => void store.loadAround(event.seq, Number(v))} />}>
      {a?.loading && !a.result && <p className="flex items-center gap-2 text-[11.5px] text-fg-3"><Spinner />Reading the journal…</p>}
      {a?.error && (
        <p className="flex items-start gap-2 text-[11.5px] text-danger"><Icon name="warning" className="mt-px size-3.5 shrink-0" /><span className="min-w-0 flex-1">{a.error}</span><Button size="sm" onClick={() => void store.loadAround(event.seq, win)}>Retry</Button></p>
      )}
      {a?.result && (
        <>
          <p className="text-[10.5px] text-fg-3"><span className="tnum">{others.length}</span> other event{others.length === 1 ? "" : "s"} within ±{fmtMs(win)} · the journal holds <span className="tnum">{fmtNum(a.result.journalEvents, 0)}</span> events across sessions{a.loading ? " · refreshing…" : ""}</p>
          {others.length === 0 ? <p className="text-[11.5px] text-fg-3">Nothing else in this window: widen it, or this one happened on its own.</p> : (
            <ul className="max-h-56 divide-y divide-line overflow-auto rounded-sm border border-line scroll-thin">
              {others.slice(0, 120).map(({ e, dt }) => (
                <li key={e.seq}>
                  <button type="button" onClick={() => onSelect(e.seq)} className="grid w-full grid-cols-[3.6rem_auto_minmax(0,1fr)] items-center gap-x-2 px-2 py-1 text-left text-[11px] hover:bg-surface-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" title={eventLine(e)}>
                    <span className={`tnum text-right ${dt < 0 ? "text-fg-3" : "text-fg-2"}`}>{fmtDt(dt)}</span>
                    <Badge tone={KIND_TONE[e.kind] ?? "neutral"}>{e.kind.replace(".noisy", "")}</Badge>
                    <span className="truncate font-code">{eventShort(e)}</span>
                  </button>
                </li>
              ))}
              {others.length > 120 && <li className="px-2 py-1 text-[10.5px] text-fg-3">… {others.length - 120} more: narrow the window</li>}
            </ul>
          )}
        </>
      )}
    </Section>
  );
}

function Companions({ store, snap, event }: { store: ControlStore; snap: Snapshot; event: ObserveEvent }) {
  const c = snap.timeline.companions;
  const r = c?.result;
  const changes = r?.changes ?? 0;
  const rows: TimelineCompanion[] = r?.companions ?? [];
  const max = Math.max(1, changes, ...rows.map((x) => x.count));
  return (
    <Section title={<>Companions of <code className="font-code normal-case tracking-normal">{event.name ?? event.unit}</code></>} tour={T.tlCompanions}
      right={c && !c.loading && <Button size="sm" tone="ghost" icon="sync" onClick={() => void store.loadCompanions(event.layer!, event.unit!, c.windowMs)} ariaLabel="Re-read companions" />}>
      {c?.loading && <p className="flex items-center gap-2 text-[11.5px] text-fg-3"><Spinner />Cross-referencing the journal…</p>}
      {c?.error && <p className="flex items-start gap-2 text-[11.5px] text-danger"><Icon name="warning" className="mt-px size-3.5 shrink-0" /><span className="min-w-0 flex-1">{c.error}</span><Button size="sm" onClick={() => void store.loadCompanions(event.layer!, event.unit!, c.windowMs)}>Retry</Button></p>}
      {r && !c?.loading && (
        <>
          <p className="text-[10.5px] text-fg-3">Changed <span className="tnum">{fmtNum(changes, 0)}</span>× in the journal. What else happened within ±{fmtMs(r.windowMs)} of those changes, and how consistently:</p>
          {rows.length === 0 ? <p className="text-[11.5px] text-fg-3">Nothing else: it changes on its own (server-pushed, or a value the client updates by itself).</p> : (
            <ul className="flex flex-col gap-0.5">
              {rows.slice(0, 12).map((x) => {
                const pct = changes ? (x.count / changes) * 100 : (x.count / max) * 100;
                const strong = changes > 1 && x.count / changes >= 0.7;
                return (
                  <li key={x.event} className="relative grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-2 px-1.5 py-1 text-[11px]">
                    <span className="absolute inset-y-0.5 left-0 rounded-r-xs opacity-15" style={{ width: `${pct}%`, background: strong ? "var(--color-ring)" : "var(--color-fg-3)" }} aria-hidden />
                    <code className="relative truncate font-code" title={x.event}>{x.event}</code>
                    <span className="relative flex items-center gap-2 text-fg-3">
                      <span className={`tnum ${strong ? "font-semibold text-fg" : ""}`} title="in how many of the changes' windows">{x.count}/{changes}</span>
                      <span className="tnum w-14 text-right" title="average offset from the change">{fmtDt(x.avgDtMs)}</span>
                    </span>
                  </li>
                );
              })}
            </ul>
          )}
        </>
      )}
    </Section>
  );
}

function Series({ store, snap, event }: { store: ControlStore; snap: Snapshot; event: ObserveEvent }) {
  const s = snap.timeline.series;
  const has = store.hasTool("observe_series");
  return (
    <Section title={<>Series of <code className="font-code normal-case tracking-normal">{event.name ?? event.unit}</code></>} tour={T.tlSeries}
      right={has && s && !s.loading && <Button size="sm" tone="ghost" icon="sync" onClick={() => void store.loadSeries(event.layer!, event.unit!)} ariaLabel="Re-read the series" />}>
      {!has && <p className="text-[11px] text-fg-3">This server has no <code className="font-code">observe_series</code> yet: the unit's values over time, its shape and its relations appear here once it does.</p>}
      {has && !s && <Button size="sm" onClick={() => void store.loadSeries(event.layer!, event.unit!)}>Read the series</Button>}
      {s?.loading && <p className="flex items-center gap-2 text-[11.5px] text-fg-3"><Spinner />Reading the series…</p>}
      {s?.error && <p className="flex items-start gap-2 text-[11.5px] text-danger"><Icon name="warning" className="mt-px size-3.5 shrink-0" /><span className="min-w-0 flex-1">{s.error}</span><Button size="sm" onClick={() => void store.loadSeries(event.layer!, event.unit!)}>Retry</Button></p>}
      {s?.result && !s.loading && <SeriesBody r={s.result} selectedAt={atMs(event)} />}
    </Section>
  );
}

function SeriesBody({ r, selectedAt }: { r: SeriesResult; selectedAt: number }) {
  const pts = r.points ?? [];
  const spark = useMemo(() => {
    if (pts.length < 2) return undefined;
    const ts = pts.map((p) => Date.parse(p.at));
    const t0 = Math.min(...ts), t1 = Math.max(...ts) || t0 + 1;
    const nums = pts.map((p) => (typeof p.number === "number" ? p.number : Number(p.value)));
    const numeric = nums.every((n) => Number.isFinite(n));
    // Non-numeric values (states, text) get a level per distinct value, in first-seen order.
    const levels = new Map<string, number>();
    const ys = numeric ? nums : pts.map((p) => { const k = String(p.value ?? ""); if (!levels.has(k)) levels.set(k, levels.size); return levels.get(k)!; });
    const lo = Math.min(...ys), hi = Math.max(...ys);
    const W = 260, H = 44, span = hi - lo || 1;
    const X = (t: number) => ((t - t0) / (t1 - t0)) * (W - 2) + 1;
    const Y = (y: number) => H - 3 - ((y - lo) / span) * (H - 6);
    // A step line: a value holds until the next point.
    let d = `M${X(ts[0]).toFixed(1)},${Y(ys[0]).toFixed(1)}`;
    for (let i = 1; i < pts.length; i++) d += ` H${X(ts[i]).toFixed(1)} V${Y(ys[i]).toFixed(1)}`;
    d += ` H${W - 1}`;
    const sel = selectedAt >= t0 && selectedAt <= t1 ? X(selectedAt) : undefined;
    return { d, W, H, sel, numeric, lo, hi, levels: [...levels.keys()] };
  }, [pts, selectedAt]);
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex flex-wrap items-center gap-1.5 text-[10.5px] text-fg-3">
        {r.shape && <Badge tone="info" title={SHAPE_HELP[r.shape] ?? r.shape}>{r.shape}</Badge>}
        <span className="tnum" title="changes in the journal / points returned">{r.changes != null ? `${fmtNum(r.changes, 0)} changes` : `${pts.length} points`}</span>
        {r.distinct != null && <span className="tnum">{r.distinct} distinct</span>}
        {r.min != null && r.max != null && <span className="tnum">{fmtNum(r.min, 2)} … {fmtNum(r.max, 2)}</span>}
        {r.stepTypical != null && <span className="tnum">step {fmtNum(r.stepTypical, 2)}</span>}
        {r.intervalMedianMs != null && <span className="tnum" title={r.intervalRegular ? "the interval is regular: a timer or a tick" : "irregular intervals: driven by something"}>every ~{fmtMs(r.intervalMedianMs)}{r.intervalRegular ? " (regular)" : ""}</span>}
      </div>
      {spark ? (
        <svg viewBox={`0 0 ${spark.W} ${spark.H}`} preserveAspectRatio="none" className="h-11 w-full rounded-sm bg-surface-2" role="img" aria-label={`${pts.length} values over time`}>
          <path d={spark.d} fill="none" stroke="var(--color-m-field)" strokeWidth="1.5" vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
          {spark.sel !== undefined && <line x1={spark.sel} x2={spark.sel} y1={0} y2={spark.H} stroke="var(--color-ring)" strokeWidth="1" vectorEffect="non-scaling-stroke" strokeDasharray="2 2" />}
        </svg>
      ) : <p className="text-[11px] text-fg-3">Not enough points for a line yet.</p>}
      {!spark?.numeric && spark?.levels.length ? <p className="truncate text-[10.5px] text-fg-3">levels: {spark.levels.slice(0, 6).join(" · ")}{spark.levels.length > 6 ? " …" : ""}</p> : null}
      {!!r.topValues?.length && (
        <div className="flex flex-wrap gap-1">{r.topValues.slice(0, 6).map((v) => <Badge key={v.value} tone="neutral" title={`${v.count}×`}><code className="font-code normal-case tracking-normal">{v.value}</code> <span className="tnum opacity-70">{v.count}</span></Badge>)}</div>
      )}
      {!!r.relations?.length && (
        <ul className="flex flex-col gap-0.5">
          <li className="ds-label px-1.5 text-fg-3">relations</li>
          {r.relations.slice(0, 8).map((x, i) => {
            const base = x.numeric ?? x.together ?? 0;
            const pct = x.holds != null && base ? (x.holds / base) * 100 : (x.strength ?? 0) * 100;
            const strong = pct >= 70;
            return (
              <li key={i} className="relative grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-2 px-1.5 py-1 text-[11px]" title={`${x.event}: together ${x.together ?? "?"}× (${x.numeric ?? "?"} numeric); ${x.relation ?? "no relation"} held ${x.holds ?? 0}×`}>
                <span className="absolute inset-y-0.5 left-0 rounded-r-xs opacity-15" style={{ width: `${Math.min(100, pct)}%`, background: strong ? "var(--color-ring)" : "var(--color-fg-3)" }} aria-hidden />
                <span className="relative min-w-0 truncate"><code className="font-code">{x.name ? `${x.event} ${x.name}` : x.event}</code>{x.relation && <span className={`ml-1.5 ${strong ? "font-semibold text-fg" : "text-fg-3"}`}>{x.relation}</span>}</span>
                <span className="relative tnum text-fg-3">{x.holds != null ? `${x.holds}/${base || (x.together ?? "?")}` : x.together != null ? `${x.together}×` : ""}</span>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

const SHAPE_HELP: Record<string, string> = {
  toggle: "two values, back and forth: a flag",
  states: "a few distinct values: an enum or a mode",
  counter: "goes up by a typical step: a count",
  timer: "changes at a regular interval: a tick or a countdown",
  continuous: "many numeric values: a measurement",
  text: "non-numeric or mixed values",
};

function askText(e: ObserveEvent, around?: ObserveEvent[] | null, companions?: TimelineCompanion[] | null): string {
  const lines = [`On the observer timeline, event #${e.seq} at ${e.at}: ${eventLine(e)}.`];
  if (e.kind === "layer" && e.mode === "struct" && !e.name) lines.push(`The HUD does not map ${e.layer} ${e.unit}. What is it?`);
  else if (e.kind === "ui" && !e.mapped) lines.push(`The HUD has no property for panel [${e.index}]. Which IngameUi member should map it?`);
  else lines.push("What does it mean, and what caused it?");
  const t0 = atMs(e);
  const near = (around ?? []).filter((x) => x.seq !== e.seq).slice(0, 12);
  if (near.length) lines.push("Around it: " + near.map((x) => `${fmtDt(atMs(x) - t0)} ${eventLine(x)}`).join("; ") + ".");
  if (companions?.length) lines.push("Its usual companions: " + companions.slice(0, 6).map((c) => `${c.event} (${c.count}×, avg ${fmtDt(c.avgDtMs)})`).join("; ") + ".");
  return lines.join(" ");
}
