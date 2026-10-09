// Overview: which HUDs are up, live HUD health (push), observer status with the latest events, quick actions and
// the server at a glance.

import { useMemo } from "react";
import { Banner, useNow } from "../../components";
import { Icon } from "../../icons";
import type { ControlStore, Snapshot } from "../store";
import { T } from "../tour/ids";
import { useTours } from "../tour/engine";
import { Badge, Button, Card, ShowMe, Stat, Switch, agoShort, fmtNum } from "../ui";
import { EventsFeed } from "./Observer";

export function OverviewPage({ store, snap }: { store: ControlStore; snap: Snapshot }) {
  const now = useNow(1000);
  const tours = useTours();
  const h = snap.health;
  const report = h.snapshot?.report;
  const trace = report?.trace;
  const spikes = useMemo(() => {
    const s = trace?.series;
    if (!s?.intervalMs || !trace?.hudFps) return undefined;
    const expected = 1000 / trace.hudFps, thr = expected * 1.25;
    let n = 0, withGc = 0;
    s.intervalMs.forEach((v, i) => { if (v != null && v > thr) { n++; if ((s.gcPauseMs?.[i] ?? 0) > 0) withGc++; } });
    return { n, withGc, thr };
  }, [trace]);
  const up = store.gamesUp;
  const noHud = up.length === 0 && snap.gamesAt !== undefined;
  const unreachable = snap.games.find((g) => g.status === "unreachable");
  const o = snap.observer;
  const fpsTone = trace?.hudFps ? (trace.hudFps >= 55 ? "good" : trace.hudFps >= 30 ? "warn" : "bad") : undefined;

  return (
    <div className="flex flex-col gap-3">
      {noHud && (unreachable ? (
        <Banner tone="danger" icon="offline" title={`The ${unreachable.game} HUD bridge is unreachable`} action={<Button size="sm" onClick={() => void store.refreshGames()}>Retry</Button>}>
          {unreachable.error ?? "The bridge files exist but the HUD does not answer."} Is the HUD running with "Whats An AI Bridge" enabled? This page checks again every 20 s.
        </Banner>
      ) : (
        <Banner tone="warning" icon="offline" title="No HUD is up" action={<Button size="sm" onClick={() => void store.refreshGames()}>Check again</Button>}>
          The server runs, but no HUD bridge is running. Start a HUD with "Whats An AI Bridge" enabled; this page notices within 20 s. Offline tools (catalog, HUD types, logs, knowledge) still work.
        </Banner>
      ))}
      {snap.games.map((g) => g.hudApiChanged && <Banner key={g.game} tone="info" icon="info" title={`${g.game}: the HUD API changed`}>{g.hudApiChanged}</Banner>)}

      <div className="grid grid-cols-1 gap-3 md:grid-cols-2 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <Card title="HUD health" icon="activity" tour={T.healthCard} className="md:col-span-2 lg:col-span-1"
          right={<>
            <label className="flex items-center gap-1.5 text-[11px] text-fg-2" title="Each report traces the HUD for 3 s. Pushed by the server standalone; held perf_watch calls inside Claude.">
              <Switch size="sm" checked={h.watching} onChange={(v) => store.setWatchHealth(v)} label="Watch health" disabled={!snap.game} tour={T.healthWatch} />watch
            </label>
            <Button size="sm" icon="sync" busy={h.loading} disabled={!snap.game} onClick={() => void store.pullHealth(0)} title="One report now (perf_watch since=0)">Trace now</Button>
            <ShowMe onClick={() => tours.start("read-timeline")} done={tours.done.has("read-timeline")} />
          </>}>
          {h.error && <p className="mb-2 flex items-center gap-1.5 text-[11.5px] text-danger"><Icon name="warning" className="size-3.5" />{h.error}</p>}
          {!trace ? (
            <div className="flex flex-col items-start gap-2 py-1 text-[12px] text-fg-2">
              {h.loading ? <span className="flex items-center gap-2"><Icon name="sync" className="spin size-3.5" />Tracing the HUD for 3 s…</span>
                : report?.trace?.message || h.snapshot?.text ? <span>{report?.trace?.message ?? h.snapshot?.text}</span>
                : <span>No health report yet. Trace once, or watch to keep them coming.</span>}
              {report?.desktop?.warning && <span className="text-[11px] text-fg-3">{report.desktop.warning}</span>}
            </div>
          ) : (
            <div className="flex flex-col gap-2">
              <div className="grid grid-cols-3 gap-1.5">
                <Stat label="Frame rate" value={fmtNum(trace.hudFps, 0)} unit="fps" tone={fpsTone} sub={`${fmtNum(trace.frameIntervalMs?.avg, 1)} ms avg · p95 ${fmtNum(trace.frameIntervalMs?.p95, 1)}`} trend={h.history.map((p) => p.fps)} flash={h.at} />
                <Stat label="Spikes" value={spikes ? String(spikes.n) : "–"} unit={spikes?.n === 1 ? "frame" : "frames"} tone={spikes ? (spikes.n === 0 ? "good" : spikes.withGc === spikes.n ? "accent" : "warn") : undefined} sub={spikes ? (spikes.n ? `${spikes.withGc} with a GC pause` : `none over ${fmtNum(spikes.thr, 1)} ms`) : "no series"} flash={h.at} />
                <Stat label="GC pauses" value={fmtNum(trace.gc?.pauseMsTotal, 0)} unit="ms" tone={(trace.gc?.pauseMsTotal ?? 0) > 60 ? "accent" : undefined} sub={trace.gc ? `gen0 ${trace.gc.gen0} · gen1 ${trace.gc.gen1} · gen2 ${trace.gc.gen2}` : ""} flash={h.at} />
              </div>
              <FrameStrip intervals={trace.series?.intervalMs} gc={trace.series?.gcPauseMs} fps={trace.hudFps} />
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[10.5px] text-fg-3">
                <span>traced {h.at ? agoShort(now - h.at) : "–"}{h.snapshot ? ` · #${h.snapshot.seq}` : ""}</span>
                <span className="flex items-center gap-1"><span className={`size-1.5 rounded-full ${h.watching ? "bg-success live-dot" : "bg-fg-3"}`} />{h.watching ? (h.via === "push" ? `pushed every ${h.snapshot?.intervalSec ?? 15} s` : `held perf_watch, every ${h.snapshot?.intervalSec ?? 15} s`) : "not watching"}</span>
                {report?.desktop?.displayLikelyOff && <Badge tone="neutral" icon="eyeOff">display off</Badge>}
                {report?.desktop?.gameForeground === false && <Badge tone="neutral" icon="eyeOff">game not in front</Badge>}
              </div>
              {!!report?.findings?.length && (
                <ul className="flex flex-col gap-0.5 text-[11.5px] leading-snug text-fg-2">
                  {report.findings.slice(0, 3).map((f, i) => <li key={i} className="flex gap-1.5"><span className="mt-[6px] size-1 shrink-0 rounded-full bg-fg-3" />{f}</li>)}
                  {report.findings.length > 3 && <li className="text-fg-3">+{report.findings.length - 3} more in the performance app</li>}
                </ul>
              )}
              {!!report?.plugins?.length && (
                <div className="flex flex-wrap gap-1">
                  {[...report.plugins].sort((a, b) => b.tickMs + b.renderMs - a.tickMs - a.renderMs).slice(0, 4).map((p) => <Badge key={p.name} tone="neutral" title={`tick ${p.tickMs} ms · render ${p.renderMs} ms · ${p.allocKBPerFrame} KB/frame`}>{p.name} <span className="tnum">{fmtNum(p.tickMs + p.renderMs, 2)} ms</span></Badge>)}
                </div>
              )}
              <div className="flex gap-2">
                {store.host.mode === "standalone" ? <Button size="sm" icon="arrowUpRight" onClick={() => store.go("perf")}>Open the performance app</Button>
                  : store.host.ask && <Button size="sm" icon="sparkle" onClick={() => store.host.ask!("Open the HUD performance app (show_hud_performance)")}>Ask Claude to open the performance app</Button>}
              </div>
            </div>
          )}
        </Card>

        <Card title="Quick actions" icon="bolt" tour={T.quickActions}>
          <div className="grid grid-cols-1 gap-1.5 xs:grid-cols-2">
            <Action icon="activity" label="Trace the HUD" sub="3 s health report" onClick={() => void store.pullHealth(0)} disabled={!snap.game} busy={h.loading} />
            <Action icon={o.status?.enabled ? "pause" : "eye"} label={o.status?.enabled ? "Stop observing" : "Start observing"} sub={o.status?.enabled ? `${o.events.length} events this session` : "record what happens in game"} onClick={() => void store.setObserving(!o.status?.enabled)} disabled={!snap.game} busy={o.busy} />
            <Action icon="sliders" label="Pull settings" sub="every plugin" onClick={() => { store.go("settings"); void store.pullSettings(); }} disabled={!snap.game} />
            <Action icon="search" label="Browse tools" sub={snap.catalog ? `${snap.catalog.tools.length} tools` : "the catalog"} onClick={() => store.go("tools")} />
            <Action icon="layers" label="Observer layers" sub="add or pause specs" onClick={() => store.go("observer")} />
            <Action icon="sparkle" label="Show me" sub="guided tours" onClick={() => tours.start("welcome")} />
          </div>
        </Card>

        <div className="md:col-span-2 lg:col-span-1" data-tour={T.observerCard}>
          <EventsFeed store={store} snap={snap} compact />
          <div className="mt-1.5 flex items-center justify-between px-1 text-[11px] text-fg-3">
            <span>{o.status ? (o.status.enabled ? "observing" : "observation off") : "observer status unknown"}{o.status?.layers?.length ? ` · ${o.status.layers.length} layers` : ""}</span>
            <button type="button" onClick={() => store.go("observer")} className="hover:text-fg hover:underline">Open the observer →</button>
          </div>
        </div>

        <Card title="Server" icon="radio">
          {snap.catalog ? (
            <dl className="grid grid-cols-[6rem_1fr] gap-y-1 text-[12px]">
              <dt className="text-fg-3">Name</dt><dd className="truncate">{snap.catalog.server.title ?? snap.catalog.server.name} <span className="tnum text-fg-3">{snap.catalog.server.version}</span></dd>
              <dt className="text-fg-3">Offers</dt><dd className="tnum">{snap.catalog.tools.length} tools in {new Set(snap.catalog.tools.map((t) => t.family)).size} families, {snap.catalog.resources.length} resources ({snap.catalog.resources.filter((r) => r.subscribable).length} subscribable), {snap.catalog.prompts.length} prompts</dd>
              <dt className="text-fg-3">Mode</dt><dd>{store.host.mode === "standalone" ? "standalone (HTTP, subscriptions)" : "MCP App inside the host (held calls, polling)"}</dd>
              <dt className="text-fg-3">Bridges</dt>
              <dd className="flex flex-col gap-0.5">
                {snap.games.length === 0 && <span className="text-fg-3">checking…</span>}
                {snap.games.map((g) => (
                  <span key={g.game} className="flex items-center gap-1.5">
                    <span className={`size-1.5 rounded-full ${g.status === "connected" ? "bg-success" : g.status === "unreachable" ? "bg-danger" : "bg-fg-3"}`} />
                    <b className="font-semibold">{g.game}</b><span className="text-fg-2">{g.status}{g.port ? ` · :${g.port}` : ""}</span>
                    {typeof g.hello?.hudBuild === "string" && <span className="truncate text-fg-3">build {g.hello.hudBuild}</span>}
                  </span>
                ))}
                {snap.games.some((g) => g.findingsToCheck) && <span className="text-[11px] text-warning">{snap.games.find((g) => g.findingsToCheck)!.findingsToCheck}</span>}
              </dd>
              <dt className="text-fg-3">Calls</dt><dd className="tnum">{snap.calls} this session</dd>
            </dl>
          ) : snap.catalogError ? <p className="text-[12px] text-danger">{snap.catalogError}</p> : <p className="flex items-center gap-2 text-[12px] text-fg-3"><Icon name="sync" className="spin size-3.5" />Loading the catalog…</p>}
        </Card>
      </div>
    </div>
  );
}

function Action({ icon, label, sub, onClick, disabled, busy }: { icon: Parameters<typeof Icon>[0]["name"]; label: string; sub: string; onClick: () => void; disabled?: boolean; busy?: boolean }) {
  return (
    <button type="button" onClick={onClick} disabled={disabled || busy}
      className="group flex items-center gap-2 rounded-lg border border-line bg-surface px-2 py-1.5 text-left transition-colors hover:border-line-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50">
      <span className="grid size-7 shrink-0 place-items-center rounded-md bg-surface-2 text-fg-2 transition-colors group-hover:bg-ring/10 group-hover:text-fg"><Icon name={busy ? "sync" : icon} className={`size-4 ${busy ? "spin" : ""}`} /></span>
      <span className="min-w-0"><span className="block truncate text-[12px] font-medium">{label}</span><span className="block truncate text-[10.5px] text-fg-3">{sub}</span></span>
    </button>
  );
}

/** The trace's frames as a thin strip: one column per frame, spikes amber, GC pauses magenta. */
function FrameStrip({ intervals, gc, fps }: { intervals?: (number | null)[]; gc?: number[]; fps?: number }) {
  if (!intervals?.length || !fps) return null;
  const expected = 1000 / fps, thr = expected * 1.25;
  const max = Math.max(thr * 1.5, ...intervals.map((v) => v ?? 0));
  const w = 400, h = 28;
  const n = intervals.length, bw = w / n;
  return (
    <svg viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" className="h-7 w-full rounded-sm bg-surface-2" aria-label="Frame intervals of the latest trace" role="img">
      <line x1={0} x2={w} y1={h - (thr / max) * h} y2={h - (thr / max) * h} stroke="var(--color-p-spike)" strokeDasharray="2 3" strokeWidth={0.75} opacity={0.6} />
      {intervals.map((v, i) => {
        if (v == null) return null;
        const bh = (v / max) * h, g = gc?.[i] ?? 0;
        return (
          <g key={i}>
            <rect x={i * bw} y={h - bh} width={Math.max(0.5, bw - 0.3)} height={bh} fill={v > thr ? "var(--color-p-spike)" : "var(--color-p-frame)"} />
            {g > 0 && <rect x={i * bw} y={h - Math.max(1.5, (g / max) * h)} width={Math.max(0.5, bw - 0.3)} height={Math.max(1.5, (g / max) * h)} fill="var(--color-p-gc)" />}
          </g>
        );
      })}
    </svg>
  );
}
