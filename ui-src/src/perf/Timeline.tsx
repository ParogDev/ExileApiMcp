import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { fmtMs, type Frame, type SpikeAnalysis } from "./model";

// The frame timeline: one bar per frame (its interval to the next frame) over the trace's ~3 s, the expected
// frame time and the spike threshold as reference lines, and GC pauses drawn as a magenta base segment of the
// frame they landed in (the pause is part of that frame's time), so a spike with a pause reads as one mark.
// Below it, on the same time axis, the frame's own work split into plugins and HUD core. Pixel coordinates from
// a ResizeObserver (not a stretched viewBox) so hairlines and labels stay crisp at 380 px and fullscreen alike.

const GUTTER_L = 30;
const GUTTER_R = 6;
const GC_LANE = 7;
const AXIS_H = 16;
const WORK_H = 42;
const GAP_V = 10;

export interface TimelineProps {
  frames: Frame[];
  durationMs: number;
  analysis: SpikeAnalysis;
  /** Main track height in px. */
  height?: number;
  dim?: boolean;
}

interface Layout {
  w: number;
  plotW: number;
  mainH: number;
  mainTop: number;
  workTop: number;
  totalH: number;
  yMax: number;
  workMax: number;
  x: (t: number) => number;
  slot: number;
}

function niceCeil(v: number): number {
  if (v <= 0) return 1;
  const p = 10 ** Math.floor(Math.log10(v));
  for (const m of [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10]) if (m * p >= v) return m * p;
  return 10 * p;
}

export function Timeline({ frames, durationMs, analysis, height = 112, dim }: TimelineProps) {
  const ref = useRef<HTMLDivElement>(null);
  const [w, setW] = useState(0);
  const [hover, setHover] = useState<number | undefined>();
  const [pinned, setPinned] = useState<number | undefined>();

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => setW(el.clientWidth);
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const L = useMemo<Layout>(() => {
    const plotW = Math.max(0, w - GUTTER_L - GUTTER_R);
    const maxIv = Math.max(analysis.thresholdMs * 1.15, ...frames.map((f) => f.intervalMs ?? 0));
    const yMax = niceCeil(maxIv);
    const workMax = niceCeil(Math.max(1, ...frames.map((f) => f.workMs)));
    const mainTop = GC_LANE;
    const workTop = mainTop + height + GAP_V;
    return {
      w, plotW, mainH: height, mainTop, workTop, totalH: workTop + WORK_H + AXIS_H, yMax, workMax,
      x: (t) => GUTTER_L + (t / durationMs) * plotW, slot: frames.length ? plotW / frames.length : 0,
    };
  }, [w, frames, durationMs, analysis.thresholdMs, height]);

  const bars = useMemo(() => {
    const gap = L.slot >= 3 ? 1 : 0;
    return frames.map((f, i) => {
      const next = i + 1 < frames.length ? frames[i + 1].tMs : f.tMs + (f.intervalMs ?? 0);
      const x0 = L.x(f.tMs);
      const x1 = L.x(next);
      const bw = Math.max(0.75, x1 - x0 - gap);
      return { f, x: x0, w: bw };
    });
  }, [frames, L]);

  const frameAt = (clientX: number): number | undefined => {
    const el = ref.current;
    if (!el || !frames.length) return undefined;
    const px = clientX - el.getBoundingClientRect().left;
    const t = ((px - GUTTER_L) / L.plotW) * durationMs;
    // Nearest frame start at or before t (tMs is sorted).
    let lo = 0, hi = frames.length - 1;
    while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (frames[mid].tMs <= t) lo = mid; else hi = mid - 1; }
    return Math.max(0, Math.min(frames.length - 1, lo));
  };

  const onMove = (e: PointerEvent) => { if (pinned === undefined) setHover(frameAt(e.clientX)); };
  const onLeave = () => { if (pinned === undefined) setHover(undefined); };
  const onClick = (e: PointerEvent) => {
    const i = frameAt(e.clientX);
    if (pinned !== undefined && pinned === i) { setPinned(undefined); setHover(undefined); } else { setPinned(i); setHover(i); }
  };
  const onKey = (e: KeyboardEvent) => {
    if (!frames.length) return;
    const cur = hover ?? pinned ?? -1;
    let next: number | undefined;
    if (e.key === "ArrowRight") next = Math.min(frames.length - 1, cur + 1);
    else if (e.key === "ArrowLeft") next = Math.max(0, cur < 0 ? 0 : cur - 1);
    else if (e.key === "Home") next = 0;
    else if (e.key === "End") next = frames.length - 1;
    else if (e.key === "Escape") { setPinned(undefined); setHover(undefined); return; }
    else return;
    e.preventDefault();
    setHover(next); setPinned(next);
  };

  const active = hover ?? pinned;
  const af = active !== undefined ? frames[active] : undefined;
  const ab = active !== undefined ? bars[active] : undefined;
  const yMain = (ms: number) => L.mainTop + L.mainH - (ms / L.yMax) * L.mainH;
  const yWork = (ms: number) => L.workTop + WORK_H - (ms / L.workMax) * WORK_H;
  const secs = Math.max(1, Math.floor(durationMs / 1000));
  const ticks = Array.from({ length: secs + 1 }, (_, i) => i * 1000).filter((t) => t <= durationMs);
  const yTicks = [0, L.yMax / 2, L.yMax];

  // Tooltip placement: right of the frame unless that would overflow, then left.
  const tipLeft = ab ? (ab.x + 12 + 176 > L.w ? Math.max(0, ab.x - 176 - 8) : ab.x + 12) : 0;

  return (
    <div ref={ref} className={`relative select-none ${dim ? "opacity-60" : ""}`} aria-label="Frame timeline">
      {w > 0 && (
        <svg width={L.w} height={L.totalH} className="block overflow-visible font-ui text-[10px] text-fg-3" role="img"
          aria-label={`${frames.length} frames over ${(durationMs / 1000).toFixed(1)} s; ${analysis.spikes} over ${fmtMs(analysis.thresholdMs)}, ${analysis.spikesWithGc} of them with a GC pause`}
          tabIndex={0} onKeyDown={onKey}
          onPointerMove={onMove} onPointerLeave={onLeave} onPointerDown={onClick}
          style={{ cursor: "crosshair", outline: "none" }}>
          {/* Main track: hairline grid, reference lines, bars */}
          {yTicks.map((v) => (
            <g key={v}>
              <line x1={GUTTER_L} x2={L.w - GUTTER_R} y1={yMain(v)} y2={yMain(v)} className="stroke-line" strokeWidth={1} shapeRendering="crispEdges" />
              <text x={GUTTER_L - 4} y={yMain(v) + 3.5} textAnchor="end" fill="currentColor" className="tnum">{v}</text>
            </g>
          ))}
          {bars.map(({ f, x, w: bw }, i) => {
            if (f.intervalMs === undefined) return null;
            const spike = f.intervalMs > analysis.thresholdMs;
            const top = yMain(f.intervalMs);
            const gcTop = yMain(Math.min(f.gcPauseMs, f.intervalMs));
            const isActive = active === i;
            return (
              <g key={i} opacity={active !== undefined && !isActive ? 0.55 : 1}>
                <rect x={x} y={top} width={bw} height={L.mainTop + L.mainH - top} className={spike ? "fill-p-spike" : "fill-p-frame"} shapeRendering="crispEdges" />
                {f.gcPauseMs > 0 && (
                  <>
                    <rect x={x} y={gcTop} width={bw} height={L.mainTop + L.mainH - gcTop} className="fill-p-gc" shapeRendering="crispEdges" />
                    <rect x={x} y={0} width={Math.max(bw, 1.5)} height={GC_LANE - 3} className="fill-p-gc" shapeRendering="crispEdges" />
                  </>
                )}
              </g>
            );
          })}
          <line x1={GUTTER_L} x2={L.w - GUTTER_R} y1={yMain(analysis.expectedMs)} y2={yMain(analysis.expectedMs)} className="stroke-fg-2" strokeWidth={1} shapeRendering="crispEdges" opacity={0.8} />
          <line x1={GUTTER_L} x2={L.w - GUTTER_R} y1={yMain(analysis.thresholdMs)} y2={yMain(analysis.thresholdMs)} className="stroke-p-spike" strokeWidth={1} shapeRendering="crispEdges" opacity={0.7} />
          <text x={L.w - GUTTER_R} y={yMain(analysis.expectedMs) - 3} textAnchor="end" fill="currentColor" className="tnum">expected {fmtMs(analysis.expectedMs)}</text>
          <text x={L.w - GUTTER_R} y={yMain(analysis.thresholdMs) - 3} textAnchor="end" fill="currentColor" className="tnum">spike &gt; {fmtMs(analysis.thresholdMs)}</text>

          {/* Work track: plugins + HUD core stacked, same x */}
          <line x1={GUTTER_L} x2={L.w - GUTTER_R} y1={yWork(0)} y2={yWork(0)} className="stroke-line" strokeWidth={1} shapeRendering="crispEdges" />
          <line x1={GUTTER_L} x2={L.w - GUTTER_R} y1={yWork(L.workMax)} y2={yWork(L.workMax)} className="stroke-line" strokeWidth={1} shapeRendering="crispEdges" />
          <text x={GUTTER_L - 4} y={yWork(L.workMax) + 3.5} textAnchor="end" fill="currentColor" className="tnum">{L.workMax}</text>
          <text x={GUTTER_L - 4} y={yWork(0) + 3.5} textAnchor="end" fill="currentColor" className="tnum">0</text>
          <text x={L.w - GUTTER_R} y={L.workTop - 3} textAnchor="end" fill="currentColor">work per frame, ms</text>
          {bars.map(({ f, x, w: bw }, i) => {
            const pTop = yWork(f.pluginsMs);
            const cTop = yWork(f.workMs);
            const isActive = active === i;
            return (
              <g key={i} opacity={active !== undefined && !isActive ? 0.55 : 1}>
                <rect x={x} y={pTop} width={bw} height={L.workTop + WORK_H - pTop} className="fill-p-plugins" shapeRendering="crispEdges" />
                <rect x={x} y={cTop} width={bw} height={Math.max(0, pTop - cTop)} className="fill-p-core" shapeRendering="crispEdges" />
              </g>
            );
          })}

          {/* Time axis */}
          {ticks.map((t) => (
            <g key={t}>
              <line x1={L.x(t)} x2={L.x(t)} y1={L.workTop + WORK_H} y2={L.workTop + WORK_H + 3} className="stroke-line-2" strokeWidth={1} shapeRendering="crispEdges" />
              <text x={L.x(t)} y={L.totalH - 2} textAnchor={t === 0 ? "start" : t === ticks[ticks.length - 1] && L.x(t) > L.w - 24 ? "end" : "middle"} fill="currentColor" className="tnum">{t / 1000} s</text>
            </g>
          ))}

          {/* Crosshair */}
          {ab && (
            <line x1={ab.x + ab.w / 2} x2={ab.x + ab.w / 2} y1={0} y2={L.workTop + WORK_H} className="stroke-fg" strokeWidth={1} shapeRendering="crispEdges" opacity={0.5} />
          )}
        </svg>
      )}
      {af && ab && (
        <div className="pointer-events-none absolute top-1 z-10 w-44 rounded-md border border-line bg-surface px-2 py-1.5 text-[11px] shadow-lg" style={{ left: tipLeft }} role="status">
          <div className="flex items-baseline justify-between text-fg-3">
            <span>frame {af.i}</span><span className="tnum">t {(af.tMs / 1000).toFixed(2)} s</span>
          </div>
          <Row swatch={af.intervalMs !== undefined && af.intervalMs > analysis.thresholdMs ? "bg-p-spike" : "bg-p-frame"} label={af.intervalMs !== undefined && af.intervalMs > analysis.thresholdMs ? "interval (spike)" : "interval"} value={af.intervalMs === undefined ? "last frame" : fmtMs(af.intervalMs)} strong />
          <Row swatch="bg-p-gc" label="GC pause" value={af.gcPauseMs > 0 ? fmtMs(af.gcPauseMs) : "none"} muted={af.gcPauseMs === 0} />
          <Row swatch="bg-p-plugins" label="plugins" value={fmtMs(af.pluginsMs)} />
          <Row swatch="bg-p-core" label="HUD core" value={fmtMs(af.coreMs)} />
          {pinned !== undefined && <div className="mt-0.5 text-[10px] text-fg-3">pinned · click again or Esc</div>}
        </div>
      )}
    </div>
  );
}

function Row({ swatch, label, value, strong, muted }: { swatch: string; label: string; value: string; strong?: boolean; muted?: boolean }) {
  return (
    <div className={`flex items-center gap-1.5 ${muted ? "text-fg-3" : ""}`}>
      <span className={`size-2 shrink-0 rounded-xs ${swatch}`} aria-hidden />
      <span className="min-w-0 flex-1 truncate">{label}</span>
      <span className={`tnum ${strong ? "font-semibold" : ""}`}>{value}</span>
    </div>
  );
}

export function TimelineLegend({ analysis, frames, durationMs }: { analysis: SpikeAnalysis; frames: number; durationMs: number }) {
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[10.5px] text-fg-3" aria-label="Legend">
      <Key swatch="bg-p-frame">frame interval</Key>
      <Key swatch="bg-p-spike">spike (&gt; {fmtMs(analysis.thresholdMs, false)} ms)</Key>
      <Key swatch="bg-p-gc">GC pause</Key>
      <Key swatch="bg-p-plugins">plugins</Key>
      <Key swatch="bg-p-core">HUD core</Key>
      <span className="tnum ml-auto">{frames} frames · {(durationMs / 1000).toFixed(1)} s</span>
    </div>
  );
}

function Key({ swatch, children }: { swatch: string; children: React.ReactNode }) {
  return <span className="inline-flex items-center gap-1"><span className={`size-2 rounded-xs ${swatch}`} aria-hidden />{children}</span>;
}
