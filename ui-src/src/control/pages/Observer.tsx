// Observer: observation on/off with its status, layers as specs (list, add with preflight, pause, remove), the map
// of one layer (every unit that changed, mapped or not), and the live events feed.

import { useEffect, useMemo, useState } from "react";
import { Banner, EmptyState, useNow } from "../../components";
import { Icon } from "../../icons";
import type { ControlStore, Snapshot } from "../store";
import { T } from "../tour/ids";
import { useTours } from "../tour/engine";
import type { LayerStatus, LayerUnit, ObserveEvent } from "../types";
import { Badge, Button, Card, NumberBox, Select, ShowMe, Switch, TextInput, agoShort, fmtNum } from "../ui";

const KIND_TONE: Record<string, "info" | "accent" | "warning" | "success" | "violet" | "neutral"> = { layer: "info", "layer.noisy": "neutral", ui: "accent", area: "success", level: "success", entity: "violet" };
const KINDS = ["layer", "layer.noisy", "ui", "area", "level", "entity"];

/** One line per event, as Tools/ObserveDtos.cs Line() words it. */
export function eventLine(e: ObserveEvent): string {
  switch (e.kind) {
    case "layer": case "server": return `${e.layer ?? "server"} ${e.unit ?? e.off ?? ""}${e.name ? ` ${e.name}` : e.mode === "struct" ? " (unmapped)" : ""} ${e.old ?? "-"} → ${e.new ?? "-"}${e.delta != null ? `  ${e.delta > 0 ? "+" : ""}${e.delta}` : ""}${e.change ? `  ${e.change}` : ""}`;
    case "layer.noisy": case "server.noisy": return `${e.layer ?? "server"} ${e.group ?? e.unit ?? ""} is noisy: counted in its layer map, not logged`;
    case "ui": return `ui [${e.index}] ${e.visible ? "opened" : "closed"} ${e.mapped ?? "UNMAPPED"}${e.firstSeen ? " (first time)" : ""}${e.texts?.length ? ` · ${e.texts.slice(0, 3).join(" | ")}` : ""}`;
    case "area": return `area ${e.from} → ${e.to}`;
    case "level": return `level ${e.from} → ${e.to}${e.area ? ` in ${e.area}` : ""}`;
    case "entity": return `entity ${e.type}${e.entityType ? ` (${e.entityType})` : ""}`;
    default: return e.kind;
  }
}

export function ObserverPage({ store, snap }: { store: ControlStore; snap: Snapshot }) {
  const o = snap.observer;
  const now = useNow(1000);
  const tours = useTours();
  const [adding, setAdding] = useState(false);
  const noHud = store.gamesUp.length === 0 && snap.gamesAt !== undefined;

  useEffect(() => { if (!o.status && !o.busy && snap.game) void store.refreshObserver(); }, [snap.game]); // eslint-disable-line react-hooks/exhaustive-deps

  const layers = o.layers?.layers ?? o.status?.layers ?? [];
  const counts = o.status?.counts ?? {};
  const total = Object.values(counts).reduce((a, b) => a + b, 0);

  return (
    <div className="flex flex-col gap-3">
      {noHud && <Banner tone="warning" icon="offline" title="No HUD is up">The observer runs inside the HUD. Start one with "Whats An AI Bridge" enabled.</Banner>}
      {o.error && <Banner tone="danger" icon="warning" title="The observer did not answer" action={<Button size="sm" onClick={() => void store.refreshObserver()}>Retry</Button>}>{o.error}</Banner>}

      <Card title="Passive observation" icon="eye" right={<><Button size="sm" icon="sync" tone="ghost" onClick={() => void store.refreshObserver()} busy={o.busy} ariaLabel="Refresh" /><ShowMe onClick={() => tours.start("add-layer")} done={tours.done.has("add-layer")}>Show me: add a layer</ShowMe></>}>
        <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
          <div className="flex items-center gap-2">
            <Switch checked={!!o.status?.enabled} onChange={(v) => void store.setObserving(v)} label="Observation" disabled={o.busy || !snap.game} tour={T.observeSwitch} />
            <span className="text-[12.5px] font-medium">{o.status?.enabled ? "Observing" : "Off"}</span>
            {o.status?.enabled && o.status.since && <span className="text-[11px] text-fg-3">since {new Date(o.status.since).toLocaleTimeString()}</span>}
          </div>
          <div className="flex flex-wrap items-center gap-1.5">
            {KINDS.filter((k) => counts[k]).map((k) => <Badge key={k} tone={KIND_TONE[k] ?? "neutral"}>{k} <span className="tnum">{counts[k]}</span></Badge>)}
            {total === 0 && o.status && <span className="text-[11px] text-fg-3">no events yet</span>}
            {o.status?.unmappedPanelsSeen ? <Badge tone="warning" icon="warning" title="UI panels the HUD has no property for">{o.status.unmappedPanelsSeen} unmapped panel{o.status.unmappedPanelsSeen > 1 ? "s" : ""}</Badge> : null}
            {o.status?.entityTypesSeen ? <Badge tone="neutral">{o.status.entityTypesSeen} entity kinds</Badge> : null}
          </div>
        </div>
        <p className="mt-2 text-[11px] leading-snug text-fg-3">While on, the HUD records its layers, top-level panels opening and closing (with the HUD property that maps them, or none), area and level changes, and new entity kinds. Read-only, never input; the state survives HUD restarts. Tell the person playing before turning it on.</p>
      </Card>

      <div className="grid grid-cols-1 gap-3 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)] lg:items-start">
        <div className="flex min-w-0 flex-col gap-3">
          <Card title="Layers" icon="layers" tour={T.layersList} pad={false} right={<><span className="tnum text-[10.5px] text-fg-3">{layers.length}</span><Button size="sm" icon="diff" tone={adding ? "primary" : "default"} onClick={() => setAdding((a) => !a)} tour={T.layerAdd}>Add layer</Button></>}>
            {adding && <AddLayer store={store} modes={o.layers?.modes ?? ["struct", "props", "dict", "list"]} onDone={() => setAdding(false)} />}
            {layers.length === 0 ? (
              <EmptyState icon="layers" title="No layers" className="py-5">A layer is a spec: a walker path and a mode. The defaults are <code className="font-code">server</code> (ServerData, struct) and <code className="font-code">stats</code> (StatDictionary, dict).
                <div className="mt-2 flex justify-center gap-2"><Button size="sm" onClick={() => setAdding(true)}>Add one</Button><ShowMe onClick={() => tours.start("add-layer")} done={tours.done.has("add-layer")} /></div>
              </EmptyState>
            ) : (
              <ul className="divide-y divide-line">
                {layers.map((l, i) => <LayerRow key={l.spec.id} l={l} store={store} first={i === 0} selected={o.map?.layer === l.spec.id} fresh={o.justSet?.id === l.spec.id && now - o.justSet.at < 4000} preflight={o.justSet?.id === l.spec.id ? o.justSet.preflight : undefined} />)}
              </ul>
            )}
          </Card>
          {o.map && <LayerMap store={store} snap={snap} />}
        </div>
        <EventsFeed store={store} snap={snap} />
      </div>
    </div>
  );
}

function LayerRow({ l, store, first, selected, fresh, preflight }: { l: LayerStatus; store: ControlStore; first: boolean; selected: boolean; fresh: boolean; preflight?: string | null }) {
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);
  const s = l.spec;
  return (
    <li className={`flex flex-col gap-1.5 border-l-[3px] px-3 py-2 transition-colors ${l.broken ? "border-l-danger" : l.notNow ? "border-l-fg-3" : !s.enabled ? "border-l-line-2" : "border-l-success"} ${selected ? "bg-surface-2/70" : ""} ${fresh ? "flash-accent" : ""}`}>
      <div className="flex items-center gap-2">
        <Switch size="sm" checked={s.enabled} label={`${s.id} ${s.enabled ? "watching" : "paused"}`} disabled={busy} onChange={async (v) => { setBusy(true); await store.setLayer({ ...s, key: s.key ?? undefined, enabled: v }); setBusy(false); }} />
        <span className="font-code text-[12.5px] font-semibold">{s.id}</span>
        <Badge tone="info">{s.mode}</Badge>
        <span className="tnum whitespace-nowrap text-[11px] text-fg-3">{s.hz} Hz</span>
        {l.broken ? <Badge tone="danger" icon="warning" title={l.broken}>broken</Badge> : l.notNow ? <Badge tone="neutral" icon="clock" title={l.notNow}>not now</Badge> : !s.enabled ? <Badge tone="neutral" icon="pause">paused</Badge> : <Badge tone="success">live</Badge>}
        <span className="ml-auto flex items-center gap-1">
          <Button size="sm" icon="grid" active={selected} onClick={() => (selected ? store.closeLayerMap() : void store.loadLayerMap(s.id))} tour={first ? T.layerMapOpen : undefined} title="Every unit of this layer that changed">Map</Button>
          {confirm ? (
            <><Button size="sm" tone="danger" busy={busy} onClick={async () => { setBusy(true); await store.removeLayer(s.id); setBusy(false); setConfirm(false); }}>Remove</Button><Button size="sm" tone="ghost" onClick={() => setConfirm(false)}>Keep</Button></>
          ) : <Button size="sm" tone="ghost" icon="x" onClick={() => setConfirm(true)} ariaLabel={`Remove layer ${s.id}`} title="Remove this layer" />}
        </span>
      </div>
      <code className="truncate font-code text-[11px] text-fg-2" title={s.path}>{s.path}</code>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-0.5 text-[10.5px] text-fg-3">
        <span className="tnum">{fmtNum(l.events, 0)} events</span>
        <span className="tnum">{fmtNum(l.unitsChanged, 0)} units</span>
        {l.noisyUnits > 0 && <span className="tnum">{l.noisyUnits} noisy</span>}
        <span className="tnum">{fmtNum(l.costMs, 2)} ms/tick</span>
        {l.bytes != null && <span className="tnum">{l.bytes} B{l.namedRanges != null ? `, ${l.namedRanges} named` : ""}</span>}
        {s.key && <span>key {s.key}</span>}
        {l.slowProps?.length ? <span className="text-warning">slow: {l.slowProps.join(", ")}</span> : null}
      </div>
      {(l.broken || preflight) && <p className="rounded-md border border-danger/30 bg-danger/10 px-2 py-1 text-[11px] text-danger" data-tour={T.layerPreflight}><Icon name="warning" className="mr-1 inline size-3 align-[-2px]" />{preflight ?? l.broken}</p>}
      {l.notNow && !l.broken && <p className="text-[11px] text-fg-3">Not now: {l.notNow}</p>}
    </li>
  );
}

function AddLayer({ store, modes, onDone }: { store: ControlStore; modes: string[]; onDone: () => void }) {
  const [id, setId] = useState("");
  const [path, setPath] = useState("GameController.Player.GetComponent<Buffs>().BuffsList");
  const [mode, setMode] = useState(modes.includes("list") ? "list" : modes[0] ?? "props");
  const [hz, setHz] = useState(4);
  const [key, setKey] = useState("");
  const [busy, setBusy] = useState(false);
  const [preflight, setPreflight] = useState<string | null>();
  const [error, setError] = useState<string>();
  const valid = /^[a-z][a-z0-9_-]*$/i.test(id) && path.trim().startsWith("GameController");
  const submit = async () => {
    if (!valid) return;
    setBusy(true); setError(undefined); setPreflight(undefined);
    const r = await store.setLayer({ id: id.trim(), path: path.trim(), mode, hz, enabled: true, key: mode === "list" && key.trim() ? key.trim() : undefined });
    setBusy(false);
    if (!r.ok) { setError(r.error); return; }
    if (r.preflight) { setPreflight(r.preflight); return; }
    onDone();
  };
  return (
    <form className="flex flex-col gap-2 border-b border-line bg-surface-2/50 p-3" onSubmit={(e) => { e.preventDefault(); void submit(); }}>
      <div className="grid gap-2 sm:grid-cols-[8rem_1fr]">
        <label className="flex flex-col gap-1 text-[11px] text-fg-2">id<TextInput value={id} onChange={setId} label="Layer id" placeholder="buffs" mono autoFocus invalid={!!id && !/^[a-z][a-z0-9_-]*$/i.test(id)} /></label>
        <label className="flex flex-col gap-1 text-[11px] text-fg-2">walker path from GameController<TextInput value={path} onChange={setPath} label="Walker path" mono tour={T.layerAddPath} invalid={!!path && !path.trim().startsWith("GameController")} /></label>
      </div>
      <div className="flex flex-wrap items-end gap-2" data-tour={T.layerAddMode}>
        <label className="flex flex-col gap-1 text-[11px] text-fg-2">mode<Select value={mode} options={modes.map((m) => ({ value: m }))} onChange={setMode} label="Mode" className="w-28" /></label>
        <label className="flex flex-col gap-1 text-[11px] text-fg-2">Hz<NumberBox value={hz} min={0.2} max={30} onCommit={setHz} label="Samples per second" /></label>
        {mode === "list" && <label className="flex min-w-0 flex-1 flex-col gap-1 text-[11px] text-fg-2">key property (default Address)<TextInput value={key} onChange={setKey} label="Key property" placeholder="Address" mono /></label>}
        <span className="ml-auto flex items-center gap-1.5">
          <Button tone="ghost" onClick={onDone}>Cancel</Button>
          <Button type="submit" tone="primary" icon="check" busy={busy} disabled={!valid} tour={T.layerAddSubmit}>Preflight and add</Button>
        </span>
      </div>
      <p className="text-[10.5px] text-fg-3">struct diffs the raw bytes of the object's struct; props its scalar properties; dict a dictionary's keys and values; list a collection's items by key.</p>
      {preflight && <p className="rounded-md border border-danger/30 bg-danger/10 px-2 py-1 text-[11px] text-danger" data-tour={T.layerPreflight}><Icon name="warning" className="mr-1 inline size-3 align-[-2px]" />Stored, but the preflight failed at: {preflight}. Fix the path and add it again under the same id.</p>}
      {error && <p className="text-[11px] text-danger">{error}</p>}
    </form>
  );
}

function LayerMap({ store, snap }: { store: ControlStore; snap: Snapshot }) {
  const m = snap.observer.map!;
  const [unmappedOnly, setUnmappedOnly] = useState(false);
  const r = m.result;
  const units = useMemo(() => (r?.units ?? []).filter((u) => !unmappedOnly || !u.name), [r, unmappedOnly]);
  const max = Math.max(1, ...units.map((u) => u.changes));
  const isStruct = r?.layer.spec.mode === "struct";
  return (
    <Card title={`Layer map · ${m.layer}`} icon="grid" tour={T.layerMap} pad={false}
      right={<>{isStruct && <label className="flex items-center gap-1.5 text-[11px] text-fg-2"><Switch size="sm" checked={unmappedOnly} onChange={setUnmappedOnly} label="Unmapped only" />unmapped only</label>}<Button size="sm" tone="ghost" icon="sync" busy={m.loading} onClick={() => void store.loadLayerMap(m.layer)} ariaLabel="Refresh map" /><Button size="sm" tone="ghost" icon="x" onClick={() => store.closeLayerMap()} ariaLabel="Close map" /></>}>
      {m.error && <p className="p-3 text-[11.5px] text-danger">{m.error}</p>}
      {!r && m.loading && <p className="flex items-center gap-2 p-3 text-[11.5px] text-fg-3"><Icon name="sync" className="spin size-3.5" />Reading the map…</p>}
      {r && (
        <>
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-line px-3 py-1.5 text-[10.5px] text-fg-3">
            <span className="tnum">t = {fmtNum(r.t / 1000, 1)} s</span><span className="tnum">{r.units.length} units</span><span className="tnum">{r.layer.unitsChanged} changed, {r.layer.noisyUnits} noisy</span>
            {isStruct && <span className="flex items-center gap-2"><span className="flex items-center gap-1"><span className="size-2 rounded-sm bg-m-field" />HUD maps it</span><span className="flex items-center gap-1"><span className="m-hatch size-2 rounded-sm" />unmapped</span></span>}
          </div>
          {units.length === 0 ? <EmptyState icon="grid" title="Nothing changed yet" className="py-4">{unmappedOnly ? "Every changed unit has a name in the HUD's struct." : "Play a bit, or lower the filter."}</EmptyState> : (
            <ul className="max-h-[26rem] divide-y divide-line overflow-auto scroll-thin">
              {units.map((u, i) => <UnitRow key={u.unit} u={u} max={max} isStruct={!!isStruct} index={i} />)}
            </ul>
          )}
        </>
      )}
    </Card>
  );
}

function UnitRow({ u, max, isStruct, index }: { u: LayerUnit; max: number; isStruct: boolean; index: number }) {
  const pct = (u.changes / max) * 100;
  return (
    <li className="rise relative grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-3 px-3 py-1.5 text-[11.5px]" style={{ "--i": Math.min(index, 12) } as React.CSSProperties}>
      <span className="absolute inset-y-1 left-0 rounded-r-sm opacity-15" style={{ width: `${pct}%`, background: u.name || !isStruct ? "var(--color-m-field)" : "var(--color-m-cand)" }} aria-hidden />
      <span className="relative flex min-w-0 items-center gap-1.5">
        {isStruct && <span className={`size-2 shrink-0 rounded-sm ${u.name ? "bg-m-field" : "m-hatch border border-line-2"}`} />}
        <code className="shrink-0 font-code">{u.unit}</code>
        {u.name ? <span className="truncate text-fg-2">{u.name}</span> : isStruct ? <span className="text-fg-3">unmapped</span> : null}
        {!u.logged && <Badge tone="neutral" title="Its noise group went noisy: counted here, not in the journal">noisy</Badge>}
      </span>
      <span className="relative flex items-center gap-3 text-fg-3">
        <span className="tnum" title="changes">{fmtNum(u.changes, 0)}×</span>
        <span className="tnum hidden xs:inline" title="per minute">{fmtNum(u.perMinute, 1)}/min</span>
        <code className="max-w-[7rem] truncate font-code text-[10.5px]" title={u.last}>{u.last}</code>
      </span>
    </li>
  );
}

export function EventsFeed({ store, snap, compact, limit = 100 }: { store: ControlStore; snap: Snapshot; compact?: boolean; limit?: number }) {
  const o = snap.observer;
  const now = useNow(1000);
  const [kinds, setKinds] = useState<Set<string>>(new Set());
  const [paused, setPaused] = useState(false);
  const [frozen, setFrozen] = useState<ObserveEvent[]>();
  const source = paused && frozen ? frozen : o.events;
  const events = useMemo(() => [...source].reverse().filter((e) => !kinds.size || kinds.has(e.kind)).slice(0, limit), [source, kinds, limit]);
  const toggleKind = (k: string) => setKinds((s) => { const n = new Set(s); if (n.has(k)) n.delete(k); else n.add(k); return n; });
  const live = snap.listen === "live" ? "push" : store.host.listen ? snap.listen : "polling";
  return (
    <Card title="Events" icon="activity" tour={T.eventsFeed} pad={false}
      right={<>
        <span className="flex items-center gap-1 text-[10.5px] text-fg-3" title={o.lastEventAt ? `last event ${agoShort(now - o.lastEventAt)}` : "no events yet"}>
          <span className={`size-1.5 rounded-full ${o.status?.enabled === false ? "bg-fg-3" : live === "push" || live === "polling" ? "bg-success live-dot" : "bg-warning"}`} />{o.status?.enabled === false ? "off" : live}
        </span>
        {!compact && <Button size="sm" tone="ghost" icon={paused ? "play" : "pause"} onClick={() => { if (!paused) setFrozen(o.events); setPaused((p) => !p); }} title={paused ? "Resume the live feed" : "Pause the feed (events keep arriving)"} ariaLabel={paused ? "Resume" : "Pause"} />}
      </>}>
      {!compact && (
        <div className="flex flex-wrap gap-1 border-b border-line px-3 py-1.5" data-tour={T.eventsFilter}>
          {KINDS.map((k) => {
            const n = o.events.filter((e) => e.kind === k).length;
            return (
              <button key={k} type="button" onClick={() => toggleKind(k)} aria-pressed={kinds.has(k)} disabled={!n}
                className={`ds-badge inline-flex h-5 items-center gap-1 rounded-sm border px-1.5 transition-colors disabled:opacity-40 ${kinds.has(k) ? "border-fg bg-fg text-surface" : "border-line text-fg-2 hover:border-line-2"}`}>
                {k}<span className="tnum opacity-70">{n}</span>
              </button>
            );
          })}
        </div>
      )}
      {events.length === 0 ? (
        <EmptyState icon="activity" title={o.status?.enabled === false ? "Observation is off" : "Nothing yet"} className="py-5">
          {o.status?.enabled === false ? "Turn it on above; events appear here as they happen." : "Panels opening, area and level changes, layer diffs: they show up here within a second or two."}
        </EmptyState>
      ) : (
        <ul className={`divide-y divide-line overflow-auto scroll-thin ${compact ? "max-h-56" : "max-h-[34rem]"}`}>
          {events.map((e) => {
            const fresh = o.lastEventAt && now - o.lastEventAt < 2000 && e.seq > o.seq - 20;
            return (
              <li key={e.seq} className={`flex items-start gap-2 px-3 py-1.5 text-[11.5px] ${fresh ? "event-in" : ""}`}>
                <span className="tnum mt-px w-14 shrink-0 text-[10.5px] text-fg-3" title={e.at}>{new Date(e.at).toLocaleTimeString([], { hour12: false })}</span>
                <Badge tone={KIND_TONE[e.kind] ?? "neutral"} className="mt-px">{e.kind.replace(".noisy", "")}</Badge>
                <span className={`min-w-0 flex-1 break-words font-code text-[11px] ${e.kind === "layer.noisy" ? "text-fg-3" : ""}`}>{eventLine(e)}</span>
                {e.kind === "ui" && !e.mapped && <Badge tone="warning" title="The HUD has no property for this panel">unmapped</Badge>}
              </li>
            );
          })}
        </ul>
      )}
    </Card>
  );
}
