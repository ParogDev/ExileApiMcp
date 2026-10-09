import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { Banner, EmptyState, IconButton, SectionLabel, Sparkline, Toasts, useNow } from "../components";
import { SmallButton } from "../explorer/Tree";
import { Icon } from "../icons";
import { analyseSpikes, fmtElapsed, fmtInt, fmtKB, fmtMs, fmtNum, fmtPct, framesOf, POOL_ENV, poolCheck, pluginsByAlloc, pluginsByTime, type SpikeAnalysis } from "./model";
import { FrameSplit, PluginDetail, PluginTable, type PluginSort } from "./Plugins";
import { RunCard, RunsSection } from "./Results";
import { Timeline, TimelineLegend } from "./Timeline";
import { AUTO_REFRESH_MS, EXPECTED_REFRESH_MS, PerfStore, type Snapshot } from "./store";
import type { Action, Gc, Report, Trace } from "./types";

/** What the panel needs from its host (the MCP Apps host, or the dev harness). */
export interface HostApi {
  updateModelContext?: (text: string, structured: Record<string, unknown>) => void;
  ask?: (text: string) => void;
  fullscreen?: { active: boolean; toggle: () => void };
}

export function Perf({ store, host }: { store: PerfStore; host: HostApi }) {
  const snap = useSyncExternalStore(store.subscribe, store.getSnapshot);
  const now = useNow(250);
  const fullscreen = host.fullscreen?.active ?? false;
  const [sort, setSort] = useState<PluginSort>("time");
  const report = snap.report;
  const trace = report?.trace;
  const game = snap.game;
  const gameLabel = game === "poe2" ? "Path of Exile 2" : game === "poe1" ? "Path of Exile" : "Game not known yet";
  const frames = useMemo(() => framesOf(trace?.series), [trace]);
  const analysis = useMemo(() => (trace ? analyseSpikes(trace, frames) : undefined), [trace, frames]);
  const pool = useMemo(() => poolCheck(trace?.gc), [trace]);
  const loadingFor = snap.loading && snap.loadStartedAt ? now - snap.loadStartedAt : 0;
  const offline = snap.conn === "offline";
  const noInstr = snap.conn === "no-instrumentation";
  const running = useCallback((a: Action) => snap.runs.get(PerfStore.runKey(a))?.status === "running", [snap.runs]);
  const run = useCallback((a: Action) => void store.run(a), [store]);

  // Model context: the report in one paragraph, when it changes, debounced and deduplicated.
  const lastCtx = useRef("");
  useEffect(() => {
    if (!host.updateModelContext || !report || !trace || !analysis) return;
    const t = setTimeout(() => {
      const key = `${snap.reportAt}|${snap.runOrder.map((k) => `${k}:${snap.runs.get(k)?.status}`).join(",")}`;
      if (key === lastCtx.current) return;
      lastCtx.current = key;
      const d = describe(report, trace, analysis, snap, game);
      host.updateModelContext!(d.text, d.structured);
    }, 800);
    return () => clearTimeout(t);
  }, [host, report, trace, analysis, snap, game]);

  const ask = host.ask && report && trace && analysis ? () => host.ask!(askText(report, trace, analysis)) : undefined;

  const header = (
    <header className="flex h-11 items-center gap-2 px-3">
      <span className="grid h-6 shrink-0 place-items-center rounded-md bg-fg px-1.5 text-[11px] font-bold tracking-tight text-surface" title={gameLabel}>
        {game === "poe2" ? "PoE 2" : game === "poe1" ? "PoE 1" : "PoE"}
      </span>
      <h1 className="truncate text-[13px] font-semibold">HUD performance</h1>
      {snap.reportAt && !snap.loading && <span className="hidden truncate text-[11px] text-fg-3 xs:inline" title={new Date(snap.reportAt).toLocaleTimeString()}>traced {agoShort(now - snap.reportAt)}</span>}
      <div className="ml-auto flex shrink-0 items-center gap-1">
        <span className={`hidden items-center gap-1.5 rounded-full border px-2 text-[11px] font-medium xs:flex ${offline ? "border-danger/30 bg-danger/10 text-danger" : noInstr ? "border-warning/30 bg-warning/10 text-warning" : snap.conn === "live" ? "border-success/30 bg-success/10 text-success" : "border-line text-fg-3"}`} role="status">
          {offline ? <Icon name="offline" className="size-3" /> : noInstr ? <Icon name="warning" className="size-3" /> : <span className={`size-1.5 rounded-full ${snap.conn === "live" ? "bg-success" : "bg-fg-3"}`} />}
          {offline ? "Offline" : noInstr ? "No instrumentation" : snap.conn === "live" ? "Live" : "Connecting"}
        </span>
        <button
          type="button"
          onClick={() => store.setAutoRefresh(!snap.autoRefresh)}
          aria-pressed={snap.autoRefresh}
          title={`Trace again every ${AUTO_REFRESH_MS / 1000} s (each trace patches the HUD for 3 s, so they are spaced out)`}
          className={`flex h-7 items-center gap-1 rounded-md border px-1.5 text-[11px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${snap.autoRefresh ? "border-fg bg-fg text-surface" : "border-transparent text-fg-2 hover:bg-surface-3 hover:text-fg"}`}
        >
          <Icon name={snap.autoRefresh ? "pause" : "play"} className="size-3.5" /><span className="hidden sm:inline">Auto</span>
        </button>
        <button
          type="button"
          onClick={() => void store.refresh()}
          disabled={snap.loading}
          title="Trace the HUD for 3 s again (show_hud_performance, about 3.5 s)"
          className="flex h-7 items-center gap-1 rounded-md border border-line bg-surface px-2 text-[11px] font-medium text-fg-2 transition-colors hover:border-line-2 hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-60"
        >
          <Icon name="sync" className={`size-3.5 ${snap.loading ? "spin" : ""}`} />
          <span className={snap.loading ? "tnum" : ""}>{snap.loading ? `Tracing ${fmtElapsed(loadingFor)}` : "Refresh"}</span>
        </button>
        {host.fullscreen && <IconButton icon={fullscreen ? "minimize" : "maximize"} label={fullscreen ? "Back to the conversation" : "Expand"} onClick={host.fullscreen.toggle} />}
      </div>
    </header>
  );

  const progress = snap.loading && (
    <div className="h-0.5 w-full bg-surface-3" role="progressbar" aria-label="Tracing the HUD" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(Math.min(1, loadingFor / EXPECTED_REFRESH_MS) * 100)}>
      <div className={`h-full bg-ring transition-[width] duration-300 ${loadingFor > EXPECTED_REFRESH_MS ? "shimmer" : ""}`} style={{ width: `${Math.max(2, Math.min(1, loadingFor / EXPECTED_REFRESH_MS) * 100)}%` }} />
    </div>
  );

  const banners = (
    <>
      {offline && (
        <Banner tone="danger" icon="offline" title="HUD bridge unreachable"
          action={<button type="button" onClick={() => void store.refresh()} disabled={snap.loading} className="rounded-md border border-current px-2 py-0.5 text-[11px] font-medium hover:bg-surface disabled:opacity-50">Retry</button>}>
          Is the HUD running with "Whats An AI Bridge" enabled?{snap.lastError && <span className="block truncate opacity-80" title={snap.lastError}>{snap.lastError}</span>}
        </Banner>
      )}
      {noInstr && (
        <Banner tone="warning" icon="warning" title="HUD instrumentation is off"
          action={<button type="button" onClick={() => void store.refresh()} disabled={snap.loading} className="rounded-md border border-current px-2 py-0.5 text-[11px] font-medium hover:bg-surface disabled:opacity-50">Retry</button>}>
          Tracing patches the HUD's own render loop for 3 s (never the game). Tick <strong className="font-semibold">Allow HUD Instrumentation</strong> in the bridge settings (HUD menu, Whats An AI Bridge, Dev Loop), then retry.
        </Banner>
      )}
      {report?.desktop && (report.desktop.displayLikelyOff || report.desktop.gameForeground === false) && (
        <Banner tone="info" icon="eyeOff" title={report.desktop.displayLikelyOff ? "The display is probably off" : "The game is not in front"}>
          {report.desktop.displayLikelyOff
            ? <>Idle for {fmtDuration(report.desktop.idleSeconds)}, past the power plan's {fmtDuration(report.desktop.displayTimeoutSeconds)} timeout: the overlay isn't composited, so nothing shows on screen. The HUD still runs every frame, so these measurements are valid.</>
            : <>Another window is in front of the game, so the overlay isn't visible. The HUD still runs every frame, so these measurements are valid.</>}
        </Banner>
      )}
    </>
  );

  if (!trace) {
    return (
      <div className={fullscreen ? "flex h-screen flex-col overflow-hidden" : "flex flex-col"}>
        {header}{progress}
        <div className="flex flex-col gap-2 px-3 pb-3">
          {banners}
          {snap.loading ? (
            <LoadingCard elapsed={loadingFor} />
          ) : !offline && !noInstr ? (
            <EmptyState icon="activity" title="No trace yet" className="rounded-lg border border-dashed border-line py-6">
              Trace the running HUD for 3 s: frame pacing, GC pauses, and where each frame's time goes.
              <div className="mt-2"><SmallButton icon="play" tone="primary" onClick={() => void store.refresh()}>Trace now</SmallButton></div>
            </EmptyState>
          ) : null}
        </div>
        <Toasts toasts={snap.toasts} onDismiss={(id) => store.dismissToast(id)} />
      </div>
    );
  }

  const dim = snap.loading;
  const headline = (
    <div className={`grid grid-cols-3 gap-1.5 ${dim ? "opacity-60" : ""}`}>
      <Tile label="Frame rate" value={fmtNum(trace.hudFps, 1)} unit="fps" sub={`${fmtMs(trace.frameIntervalMs.avg)} avg, p95 ${fmtMs(trace.frameIntervalMs.p95, false)}`}
        trend={snap.history.length > 2 ? snap.history.map((h) => h.fps) : undefined} />
      <Tile label="Spikes" value={String(analysis!.spikes)} unit={analysis!.spikes === 1 ? "frame" : "frames"} sub={`over ${fmtMs(analysis!.thresholdMs)}, max ${fmtMs(analysis!.maxMs, false)}`}
        tone={analysis!.spikes === 0 ? "good" : analysis!.spikesWithGc === analysis!.spikes ? "gc" : "warn"} />
      <Tile label="GC pauses" value={fmtNum(trace.gc?.pauseMsTotal ?? analysis!.gcPauseTotalMs, 0)} unit="ms" sub={`in ${(trace.durationMs / 1000).toFixed(0)} s, ${fmtPct(trace.gc?.pauseMsTotal ?? analysis!.gcPauseTotalMs, trace.durationMs)} of the time`}
        tone={(trace.gc?.pauseMsTotal ?? 0) > 60 ? "gc" : undefined} />
    </div>
  );

  const timelineCard = (
    <Card title="Frame timeline" icon="activity" dim={dim} right={<span className="tnum text-[10.5px] text-fg-3">{frames.length ? "hover a frame · click to pin" : ""}</span>}>
      {frames.length ? (
        <>
          <Timeline frames={frames} durationMs={trace.durationMs} analysis={analysis!} height={fullscreen ? 170 : 112} />
          <TimelineLegend analysis={analysis!} frames={frames.length} durationMs={trace.durationMs} />
          <Verdict a={analysis!} />
        </>
      ) : (
        <p className="text-[11.5px] text-fg-3">This report has no per-frame series (an older server, or series=false). Interval {fmtMs(trace.frameIntervalMs.avg)} avg, p95 {fmtMs(trace.frameIntervalMs.p95)}, max {fmtMs(trace.frameIntervalMs.max)}.</p>
      )}
    </Card>
  );

  const pluginsCard = (
    <Card title="Where the frame time goes" icon="layers" dim={dim}>
      <FrameSplit trace={trace} />
      <div className="mt-3">
        <PluginTable report={report!} selected={snap.selectedPlugin} onSelect={(n) => store.selectPlugin(n)} sort={sort} onSort={setSort} maxRows={fullscreen ? 14 : 8} />
      </div>
      {snap.selectedPlugin && (
        <div className="mt-2">
          <PluginDetail name={snap.selectedPlugin} trace={trace} onRun={run} running={running} onClose={() => store.selectPlugin(undefined)} />
        </div>
      )}
    </Card>
  );

  const gcCard = trace.gc && (
    <Card title="GC health" icon="box" dim={dim}>
      <GcPanel gc={trace.gc} trace={trace} report={report!} pool={pool} onCopied={() => store.toast("info", "Copied the environment variable")} />
    </Card>
  );

  const findingsCard = (
    <Card title="Findings and next steps" icon="sparkle" dim={dim} right={ask && <SmallButton icon="sparkle" onClick={ask} title="Ask Claude, with this report as context">Ask Claude</SmallButton>}>
      {report!.findings?.length ? (
        <ol className="flex flex-col gap-1 text-[11.5px] leading-snug">
          {report!.findings.map((f, i) => (
            <li key={i} className="flex gap-2"><span className="mt-[5px] size-1.5 shrink-0 rounded-full bg-fg-3" aria-hidden /><span className="min-w-0 break-words">{f}</span></li>
          ))}
        </ol>
      ) : (
        <p className="flex items-center gap-1.5 text-[11.5px] text-success"><Icon name="check" className="size-3.5" />Nothing stands out: steady frames, little garbage, no plugin over 0.3 ms per frame.</p>
      )}
      {!!report!.lint?.length && (
        <ul className="mt-2 flex flex-col gap-1 text-[11px]">
          {report!.lint.map((l, i) => (
            <li key={i} className="rounded-md bg-surface-2 px-2 py-1">
              <div className="flex flex-wrap items-baseline gap-x-1.5"><span className="font-medium">{l.plugin}</span><span className="truncate font-code text-[10.5px] text-fg-2" title={l.method}>{l.method}</span><span className="text-fg-3">calls</span><span className="font-code text-[10.5px]">{l.call}</span>{l.count > 1 && <span className="tnum text-fg-3">x{l.count}</span>}<span className="rounded border border-warning/40 bg-warning/10 px-1 text-[10px] text-warning">in a loop</span></div>
              <div className="text-fg-2">{l.advice}</div>
            </li>
          ))}
        </ul>
      )}
      {!!report!.actions?.length && (
        <div className="mt-2.5 flex flex-wrap gap-1.5" aria-label="Next steps">
          {report!.actions.map((a) => {
            const r = snap.runs.get(PerfStore.runKey(a));
            const busy = r?.status === "running";
            return (
              <SmallButton key={PerfStore.runKey(a)} icon={busy ? "sync" : a.tool === "profile_plugin" ? "activity" : a.tool === "hud_plugin_lint" ? "code" : "target"} tone={a.tool === "profile_plugin" ? "primary" : "default"}
                onClick={() => run(a)} disabled={busy} title={actionTitle(a)}>
                <span className={busy ? "tnum" : ""}>{busy ? `${a.label} · ${fmtElapsed(now - r!.startedAt)}` : a.label}</span>
              </SmallButton>
            );
          })}
        </div>
      )}
    </Card>
  );

  const runs = (
    <RunsSection count={snap.runOrder.length}>
      {snap.runOrder.map((k) => {
        const r = snap.runs.get(k)!;
        return <RunCard key={k} run={r} now={now} onDismiss={() => store.dismissRun(k)} onRetry={() => run(r.action)} onAsk={host.ask} />;
      })}
    </RunsSection>
  );

  return (
    <div className={fullscreen ? "flex h-screen flex-col overflow-hidden" : "flex flex-col"}>
      {header}{progress}
      {fullscreen ? (
        <div className="min-h-0 flex-1 overflow-auto scroll-thin px-3 pb-3">
          <div className="flex flex-col gap-2">{banners}</div>
          <div className="mt-2 grid gap-2 sm:grid-cols-[minmax(0,3fr)_minmax(20rem,2fr)]">
            <div className="flex min-w-0 flex-col gap-2">{headline}{timelineCard}{pluginsCard}</div>
            <div className="flex min-w-0 flex-col gap-2">{gcCard}{findingsCard}{runs}</div>
          </div>
        </div>
      ) : (
        <div className="flex flex-col gap-2 px-3 pb-3">
          {banners}{headline}{timelineCard}{pluginsCard}{gcCard}{findingsCard}{runs}
        </div>
      )}
      <Toasts toasts={snap.toasts} onDismiss={(id) => store.dismissToast(id)} />
    </div>
  );
}

// ── Pieces ──────────────────────────────────────────────────────────

function Card({ title, icon, right, dim, children }: { title: string; icon: Parameters<typeof Icon>[0]["name"]; right?: React.ReactNode; dim?: boolean; children: React.ReactNode }) {
  return (
    <section aria-label={title} className={`rounded-lg border border-line bg-surface p-2.5 transition-opacity ${dim ? "opacity-60" : ""}`}>
      <SectionLabel right={right} className="mb-2"><Icon name={icon} className="size-3" />{title}</SectionLabel>
      {children}
    </section>
  );
}

function Tile({ label, value, unit, sub, tone, trend }: { label: string; value: string; unit: string; sub: string; tone?: "good" | "gc" | "warn"; trend?: number[] }) {
  const toneCls = tone === "good" ? "text-success" : tone === "gc" ? "text-p-gc" : tone === "warn" ? "text-p-spike" : "";
  return (
    <div className="relative min-w-0 overflow-hidden rounded-lg bg-surface-2 px-2.5 py-2" title={`${label}: ${value} ${unit}, ${sub}`}>
      {trend && <Sparkline values={trend} max={Math.max(...trend)} className="pointer-events-none absolute right-2 top-5 h-5 w-[40%] text-fg-3" />}
      <div className="truncate text-[11px] font-medium text-fg-2">{label}</div>
      <div className="relative mt-1 flex items-baseline gap-1">
        <span className={`text-[19px] font-semibold leading-none ${toneCls}`}>{value}</span>
        <span className="text-[11px] leading-none text-fg-3">{unit}</span>
      </div>
      <div className="tnum mt-1 text-[10.5px] leading-tight text-fg-3">{sub}</div>
    </div>
  );
}

function Verdict({ a }: { a: SpikeAnalysis }) {
  const cls = a.tone === "good" ? "border-success/40 text-fg" : a.tone === "gc" ? "border-p-gc text-fg" : a.tone === "work" ? "border-p-spike text-fg" : "border-warning/60 text-fg";
  const icon = a.tone === "good" ? "check" : "info";
  return (
    <p className={`mt-2 flex items-start gap-2 border-l-2 pl-2.5 text-[11.5px] leading-snug ${cls}`} role="status">
      <Icon name={icon} className={`mt-px size-3.5 shrink-0 ${a.tone === "good" ? "text-success" : a.tone === "gc" ? "text-p-gc" : a.tone === "work" ? "text-p-spike" : "text-warning"}`} />
      <span>{a.verdict}</span>
    </p>
  );
}

function LoadingCard({ elapsed }: { elapsed: number }) {
  const frac = Math.min(1, elapsed / EXPECTED_REFRESH_MS);
  return (
    <div className="rounded-lg border border-line bg-surface p-3 text-[12px]" role="status" aria-busy>
      <div className="flex items-center gap-2"><Icon name="sync" className="spin size-4 text-fg-3" /><span className="font-medium">Tracing the HUD for 3 s</span><span className="tnum ml-auto text-fg-3">{fmtElapsed(elapsed)}</span></div>
      <p className="mt-1 text-[11.5px] text-fg-2">{frac < 0.15 ? "Patching the HUD's frame loop and the plugins' Tick / Render..." : frac < 0.9 ? "Recording every frame: interval, work, GC pauses..." : elapsed > EXPECTED_REFRESH_MS * 1.5 ? "Taking longer than usual; the bridge answers on the game thread." : "Collecting the result..."}</p>
      <div className="mt-2 h-1 overflow-hidden rounded-sm bg-surface-3"><div className={`h-full rounded-sm bg-ring transition-[width] duration-300 ${elapsed > EXPECTED_REFRESH_MS ? "shimmer" : ""}`} style={{ width: `${Math.max(3, frac * 100)}%` }} /></div>
    </div>
  );
}

function GcPanel({ gc, trace, report, pool, onCopied }: { gc: Gc; trace: Trace; report: Report; pool: ReturnType<typeof poolCheck>; onCopied: () => void }) {
  const pluginKB = (report.plugins ?? []).reduce((a, p) => a + p.allocKBPerFrame, 0);
  const pluginMBs = (pluginKB * trace.hudFps) / 1024;
  // Fetched pages are allocations only while the pool drops them (churn); with the pool keeping up they are reused.
  const fetched = pool.state === "churn" ? Math.min(gc.fetchedMBPerSecond, gc.allocMBPerSecond) : 0;
  const other = Math.max(0, gc.allocMBPerSecond - fetched - pluginMBs);
  const pct = (v: number) => `${gc.allocMBPerSecond > 0 ? Math.max(0, Math.min(100, (v / gc.allocMBPerSecond) * 100)) : 0}%`;
  const collections = gc.gen0 + gc.gen1 + gc.gen2;
  return (
    <div className="text-[11.5px]">
      <div className="grid grid-cols-2 gap-x-3 gap-y-1.5">
        <Stat label="Allocated" value={fmtNum(gc.allocMBPerSecond, 0)} unit="MB/s" />
        <Stat label="Fetched from the game" value={fmtNum(gc.fetchedMBPerSecond, 0)} unit="MB/s" title="Bytes the HUD's page cache read from game memory: every fetched page is a fresh allocation unless the ArrayPool keeps it" />
        <Stat label="Collections" value={`${gc.gen0} / ${gc.gen1} / ${gc.gen2}`} unit="gen 0 / 1 / 2" title={`${collections} collections in ${(trace.durationMs / 1000).toFixed(0)} s`} />
        <Stat label="Paused" value={fmtNum(gc.pauseMsTotal, 0)} unit={`ms / ${(trace.durationMs / 1000).toFixed(0)} s`} tone={gc.pauseMsTotal > 60 ? "gc" : undefined} />
      </div>
      <div className="mt-2.5">
        <div className="flex h-2 overflow-hidden rounded-sm bg-line-2/50" role="img" aria-label={`Of ${fmtNum(gc.allocMBPerSecond, 0)} MB/s allocated: ${fmtNum(fetched, 0)} game pages, ${fmtNum(pluginMBs, 0)} plugins, ${fmtNum(other, 0)} other`}>
          <div className="bg-p-core" style={{ width: pct(fetched) }} />
          <div className="ml-px bg-p-plugins" style={{ width: pct(pluginMBs) }} />
        </div>
        <div className="mt-1 flex flex-wrap gap-x-3 gap-y-0.5 text-[10.5px] text-fg-3">
          {fetched > 0 && <span className="inline-flex items-center gap-1"><span className="size-2 rounded-[2px] bg-p-core" aria-hidden />game pages <span className="tnum text-fg-2">{fmtPct(fetched, gc.allocMBPerSecond)}</span></span>}
          <span className="inline-flex items-center gap-1"><span className="size-2 rounded-[2px] bg-p-plugins" aria-hidden />plugins <span className="tnum text-fg-2">{fmtPct(pluginMBs, gc.allocMBPerSecond)}</span> <span>({fmtKB(pluginKB)} / frame)</span></span>
          {other > 1 && <span className="inline-flex items-center gap-1"><span className="size-2 rounded-[2px] bg-line-2" aria-hidden />other <span className="tnum text-fg-2">{fmtPct(other, gc.allocMBPerSecond)}</span></span>}
        </div>
      </div>
      {pool.state === "churn" && (
        <div className="mt-2.5 rounded-md border border-warning/30 bg-warning/10 px-2.5 py-2 leading-snug" role="status">
          <div className="flex items-start gap-2">
            <Icon name="warning" className="mt-px size-3.5 shrink-0 text-warning" />
            <div className="min-w-0 flex-1">
              <div className="font-semibold">The page cache churns through the shared ArrayPool</div>
              <p className="mt-0.5 text-fg-2">The HUD cycles about <span className="tnum font-medium text-fg">{fmtInt(pool.cycles)}</span> memory pages per frame, but the shared ArrayPool keeps only <span className="tnum font-medium text-fg">{fmtInt(pool.keeps)}</span> per size. The rest are dropped and reallocated every frame: that is most of the {fmtNum(gc.allocMBPerSecond, 0)} MB/s, and what the collector keeps pausing for.</p>
              <p className="mt-1 text-fg-2">Mitigation, not a cure: this environment variable (decimal) lets the pool keep the pages. Measured: about 4x less allocation and half the total pause, but each pause gets longer, so spikes stay. The cure is a pool of long-lived pages in the HUD itself (research/hud-gc.md).</p>
              <div className="mt-1 flex items-center gap-1">
                <code className="code-wrap min-w-0 flex-1 rounded bg-surface px-1.5 py-1 font-code text-[10.5px] text-fg">{POOL_ENV}</code>
                <CopyButton text={POOL_ENV} onCopied={onCopied} />
              </div>
            </div>
          </div>
        </div>
      )}
      {pool.state === "tuned" && <p className="mt-2 flex items-center gap-1.5 text-fg-2"><Icon name="check" className="size-3.5 text-success" />ArrayPool limit raised to {fmtInt(pool.env ?? undefined)} per partition ({fmtInt(pool.keeps)} per size) by the environment; the page cache cycles ~{fmtInt(pool.cycles)} per frame.</p>}
      {pool.state === "ok" && <p className="mt-2 text-fg-3">The shared ArrayPool keeps {fmtInt(pool.keeps)} pages per size and the page cache cycles ~{fmtInt(pool.cycles)} per frame: pages are reused.</p>}
      {pool.broken && <p className="mt-2 text-fg-3">Cannot check the ArrayPool limits: {pool.broken}</p>}
    </div>
  );
}

function Stat({ label, value, unit, tone, title }: { label: string; value: string; unit: string; tone?: "gc"; title?: string }) {
  return (
    <div className="min-w-0" title={title}>
      <div className="truncate text-[10.5px] text-fg-3">{label}</div>
      <div className="flex items-baseline gap-1"><span className={`tnum text-[14px] font-semibold leading-tight ${tone === "gc" ? "text-p-gc" : ""}`}>{value}</span><span className="truncate text-[10px] text-fg-3">{unit}</span></div>
    </div>
  );
}

function CopyButton({ text, onCopied }: { text: string; onCopied: () => void }) {
  const [done, setDone] = useState(false);
  return (
    <IconButton icon={done ? "check" : "copy"} label="Copy" size="sm" onClick={() => {
      void navigator.clipboard?.writeText(text).then(() => { setDone(true); onCopied(); setTimeout(() => setDone(false), 1500); }).catch(() => {});
    }} />
  );
}

function actionTitle(a: Action): string {
  switch (a.tool) {
    case "profile_plugin": return `profile_plugin ${JSON.stringify(a.args)}: patch every method of the plugin and time it for 4 s; self time, calls and allocation per method`;
    case "hud_plugin_lint": return `hud_plugin_lint ${JSON.stringify(a.args)}: expensive HUD API calls in the plugin's per-frame code, from its DLL (offline)`;
    case "overlay_accuracy": return "overlay_accuracy: how far HUD drawings are from the game, in pixels, over 5 s (ask the user to move the camera)";
    default: return `${a.tool} ${JSON.stringify(a.args)}`;
  }
}

function agoShort(ms: number): string {
  if (ms < 5000) return "just now";
  if (ms < 60_000) return `${Math.round(ms / 1000)} s ago`;
  return `${Math.round(ms / 60_000)} min ago`;
}

function fmtDuration(s: number | undefined): string {
  if (s === undefined) return "a while";
  if (s < 90) return `${Math.round(s)} s`;
  if (s < 5400) return `${Math.round(s / 60)} min`;
  return `${(s / 3600).toFixed(1)} h`;
}

// ── Model context and questions ─────────────────────────────────────

function describe(report: Report, t: Trace, a: SpikeAnalysis, snap: Snapshot, game?: string): { text: string; structured: Record<string, unknown> } {
  const top = pluginsByTime(report).slice(0, 3).map((p) => `${p.name} ${(p.tickMs + p.renderMs).toFixed(2)} ms`).join(", ");
  const alloc = pluginsByAlloc(report).slice(0, 2).map((p) => `${p.name} ${p.allocKBPerFrame.toFixed(0)} KB`).join(", ");
  const pool = poolCheck(t.gc);
  const parts = [
    `HUD performance panel (${game ?? "?"}) shows a ${(t.durationMs / 1000).toFixed(0)} s trace: ${t.hudFps.toFixed(1)} fps, interval avg ${fmtMs(t.frameIntervalMs.avg)} p95 ${fmtMs(t.frameIntervalMs.p95)} max ${fmtMs(t.frameIntervalMs.max)}; work ${fmtMs(t.updateMs.avg)} = plugins ${fmtMs(t.pluginsMs.avg)} + core ${fmtMs(t.coreMs.avg)}.`,
    a.verdict,
    t.gc ? `GC: ${t.gc.allocMBPerSecond.toFixed(0)} MB/s allocated (${t.gc.fetchedMBPerSecond.toFixed(0)} MB/s fetched from the game), ${t.gc.gen0}/${t.gc.gen1}/${t.gc.gen2} collections, ${t.gc.pauseMsTotal.toFixed(0)} ms paused.` : "",
    pool.state === "churn" ? `ArrayPool churn: cycles ~${pool.cycles} pages per frame, keeps ${pool.keeps}; mitigation  (halves total GC pause; pauses get longer).` : "",
    top ? `Costliest plugins: ${top}; by allocation ${alloc}.` : "",
    report.findings?.length ? `Findings: ${report.findings.join(" | ")}` : "Findings: nothing stands out.",
  ];
  const runs = snap.runOrder.map((k) => snap.runs.get(k)!).filter((r) => r.status === "done").slice(0, 3);
  for (const r of runs) {
    const d = r.data as Record<string, unknown>;
    if (r.action.tool === "profile_plugin" && Array.isArray(d?.top)) {
      const p = d as unknown as { plugin: string; selfTotalMsPerSecond: number; top: { method: string; selfMsPerSecond: number; calls: number; allocSelfKBPerSecond: number }[] };
      parts.push(`Profile ${p.plugin}: ${p.selfTotalMsPerSecond.toFixed(1)} ms/s; top ${p.top.slice(0, 4).map((m) => `${m.method} ${m.selfMsPerSecond.toFixed(1)} ms/s x${m.calls} ${m.allocSelfKBPerSecond.toFixed(0)} KB/s`).join(", ")}.`);
    } else if (r.action.tool === "hud_plugin_lint" && Array.isArray(d?.plugins)) {
      const l = d as unknown as { plugins: { plugin: string; findings: { method: string; call: string; inLoop: boolean }[] }[] };
      const fs = l.plugins.flatMap((p) => p.findings);
      parts.push(`Lint ${l.plugins.map((p) => p.plugin).join(", ")}: ${fs.length} expensive calls, ${fs.filter((f) => f.inLoop).length} in loops${fs.length ? `; e.g. ${fs.slice(0, 3).map((f) => `${f.method} -> ${f.call}`).join(", ")}` : ""}.`);
    }
  }
  const structured: Record<string, unknown> = {
    game, fps: t.hudFps, frameIntervalMs: t.frameIntervalMs, updateMs: t.updateMs, pluginsMs: t.pluginsMs, coreMs: t.coreMs,
    spikes: { threshold: a.thresholdMs, count: a.spikes, withGc: a.spikesWithGc, maxWithoutGcMs: a.maxWithoutGcMs, verdict: a.verdict },
    gc: t.gc, arrayPool: pool, plugins: report.plugins?.slice(0, 8), findings: report.findings, desktop: report.desktop,
    results: runs.map((r) => ({ tool: r.action.tool, args: r.action.args, data: r.data })),
  };
  return { text: parts.filter(Boolean).join(" "), structured };
}

function askText(report: Report, t: Trace, a: SpikeAnalysis): string {
  const pool = poolCheck(t.gc);
  const top = pluginsByTime(report).slice(0, 3).map((p) => `${p.name} ${(p.tickMs + p.renderMs).toFixed(2)} ms/frame, ${p.allocKBPerFrame.toFixed(0)} KB/frame`).join("; ");
  return `My HUD runs at ${t.hudFps.toFixed(0)} fps with frames up to ${fmtMs(t.frameIntervalMs.max)} (p95 ${fmtMs(t.frameIntervalMs.p95)}). ${a.verdict}${t.gc ? ` GC: ${t.gc.allocMBPerSecond.toFixed(0)} MB/s allocated, ${t.gc.pauseMsTotal.toFixed(0)} ms paused per ${(t.durationMs / 1000).toFixed(0)} s.` : ""}${pool.state === "churn" ? ` The page cache cycles ~${pool.cycles} pages per frame but the ArrayPool keeps ${pool.keeps}.` : ""} Costliest plugins: ${top}. What should I fix first, and how?`;
}
