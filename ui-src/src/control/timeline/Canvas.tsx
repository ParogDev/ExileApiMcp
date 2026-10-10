// The plot: every lane on one time axis, drawn on a canvas (1000 events plus journal reads redraw in well under a
// frame). Events in the same pixel column of a lane collapse into one bucket (taller, with a count), so a burst reads
// as a burst, not as a smear. Spike / reload windows are bands across every lane; events inside them are dimmed when
// asked; after-agent windows are faint bands with a dot under the events that fall in them. Drag pans, the wheel
// zooms around the cursor (shift+wheel pans), a click selects the bucket under the cursor (again: the next event in
// it). Colours come from the CSS tokens at draw time, so both themes work.

import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { ObserveEvent } from "../types";
import { atMs, axisTicks, inWindow, isShown, laneOf, markOf, panBy, xOf, zoomAt, type Filters, type Lane, type Viewport, type Window } from "./model";

export const ROW_H = 26;
export const ROW_HIDDEN_H = 7;
export const AXIS_H = 20;

export interface Bucket { x: number; lane: number; seqs: number[]; top: number; bottom: number }
export interface Hover { bucket: Bucket; clientX: number; clientY: number }

export function laneTops(lanes: readonly Lane[], hidden: ReadonlySet<string>): number[] {
  const tops: number[] = [];
  let y = AXIS_H;
  for (const l of lanes) { tops.push(y); y += hidden.has(l.id) ? ROW_HIDDEN_H : ROW_H; }
  tops.push(y);
  return tops;
}

interface Props {
  events: readonly ObserveEvent[];
  lanes: readonly Lane[];
  vp: Viewport;
  now: number;
  filters: Filters;
  windows: readonly Window[];
  selected?: number;
  onSelect: (seq: number | undefined) => void;
  onViewport: (vp: Viewport, userDriven: boolean) => void;
  onHover: (h: Hover | undefined) => void;
  className?: string;
}

export function LaneCanvas({ events, lanes, vp, now, filters, windows, selected, onSelect, onViewport, onHover, className = "" }: Props) {
  const ref = useRef<HTMLCanvasElement>(null);
  const [width, setWidth] = useState(0);
  const buckets = useRef<Bucket[]>([]);
  const drag = useRef<{ x0: number; vp: Viewport; moved: boolean; lastX: number } | null>(null);
  const lastClick = useRef<{ key: string; index: number }>({ key: "", index: 0 });
  const tops = useMemo(() => laneTops(lanes, filters.hiddenLanes), [lanes, filters.hiddenLanes]);
  const height = tops[tops.length - 1] + 2;

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setWidth(el.getBoundingClientRect().width));
    ro.observe(el);
    setWidth(el.getBoundingClientRect().width);
    return () => ro.disconnect();
  }, []);

  // ── Draw ─────────────────────────────────────────────────────────
  useEffect(() => {
    const el = ref.current;
    if (!el || width <= 0) return;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    el.width = Math.round(width * dpr); el.height = Math.round(height * dpr);
    const ctx = el.getContext("2d");
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);
    const cs = getComputedStyle(el);
    const toneCache = new Map<string, string>();
    const tone = (v: string) => { let c = toneCache.get(v); if (c === undefined) { c = cs.getPropertyValue(v).trim() || "#888"; toneCache.set(v, c); } return c; };
    const line = tone("--color-line"), line2 = tone("--color-line-2"), fg3 = tone("--color-fg-3"), ring = tone("--color-ring"), surface2 = tone("--color-surface-2");
    const laneIndex = new Map(lanes.map((l, i) => [l.id, i]));
    const plotTop = AXIS_H, plotBottom = tops[tops.length - 1];
    const x0 = vp.end - vp.span;

    // Row backgrounds: hidden lanes as a thin hatched strip; the future (past "now") as a shade.
    lanes.forEach((l, i) => {
      if (filters.hiddenLanes.has(l.id)) { ctx.fillStyle = surface2; ctx.fillRect(0, tops[i], width, ROW_HIDDEN_H); }
      else if (i % 2 === 1) { ctx.fillStyle = surface2; ctx.globalAlpha = 0.45; ctx.fillRect(0, tops[i], width, ROW_H); ctx.globalAlpha = 1; }
    });
    const nowX = xOf(now, vp, width);
    if (nowX < width) { ctx.fillStyle = surface2; ctx.globalAlpha = 0.7; ctx.fillRect(Math.max(0, nowX), plotTop, width - Math.max(0, nowX), plotBottom - plotTop); ctx.globalAlpha = 1; }

    // Windows: spikes / reloads as bands across every lane; after-agent as a faint band.
    for (const w of windows) {
      if (w.to < x0 || w.from > vp.end) continue;
      if (w.kind === "agent" && !filters.markAfterAgent) continue;
      const a = Math.max(0, xOf(w.from, vp, width)), b = Math.min(width, xOf(w.to, vp, width));
      ctx.fillStyle = w.kind === "agent" ? tone("--color-fg") : w.kind === "spike" ? tone("--color-p-spike") : tone("--color-p-gc");
      ctx.globalAlpha = w.kind === "agent" ? 0.05 : 0.1;
      ctx.fillRect(a, plotTop, Math.max(1, b - a), plotBottom - plotTop);
      ctx.globalAlpha = 1;
    }

    // Axis: grid lines and labels.
    ctx.font = "9.5px ui-sans-serif, system-ui, sans-serif";
    ctx.textBaseline = "middle";
    for (const tk of axisTicks(vp, width)) {
      const x = Math.round(xOf(tk.t, vp, width)) + 0.5;
      ctx.strokeStyle = tk.major ? line2 : line; ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(x, plotTop); ctx.lineTo(x, plotBottom); ctx.stroke();
      ctx.fillStyle = tk.major ? tone("--color-fg-2") : fg3;
      ctx.fillText(tk.label, x + 3, AXIS_H / 2);
    }
    ctx.strokeStyle = line; ctx.beginPath(); ctx.moveTo(0, plotTop + 0.5); ctx.lineTo(width, plotTop + 0.5); ctx.stroke();
    lanes.forEach((_, i) => { const y = tops[i + 1] + 0.5; ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(width, y); ctx.stroke(); });

    // Buckets: shown events by lane and pixel column.
    const map = new Map<string, Bucket>();
    const list: Bucket[] = [];
    for (const e of events) {
      if (!isShown(e, filters)) continue;
      const t = atMs(e);
      if (t < x0 - vp.span * 0.02 || t > vp.end + vp.span * 0.02) continue;
      const li = laneIndex.get(laneOf(e));
      if (li === undefined) continue;
      const x = Math.round(xOf(t, vp, width));
      const key = `${li}:${x}`;
      let b = map.get(key);
      if (!b) { b = { x, lane: li, seqs: [], top: tops[li], bottom: tops[li + 1] }; map.set(key, b); list.push(b); }
      b.seqs.push(e.seq);
    }
    buckets.current = list;
    const bySeq = new Map(events.map((e) => [e.seq, e]));

    // Rules (area / level) first: they span every lane and sit behind the marks.
    for (const b of list) {
      const e = bySeq.get(b.seqs[0]);
      if (!e || (e.kind !== "area" && e.kind !== "level")) continue;
      const m = markOf(e);
      ctx.strokeStyle = tone(m.tone); ctx.lineWidth = 1; ctx.globalAlpha = 0.55;
      ctx.setLineDash(m.hollow ? [3, 3] : []);
      ctx.beginPath(); ctx.moveTo(b.x + 0.5, plotTop); ctx.lineTo(b.x + 0.5, plotBottom); ctx.stroke();
      ctx.setLineDash([]); ctx.globalAlpha = 1;
    }

    // Marks.
    for (const b of list) {
      const e0 = bySeq.get(b.seqs[0]);
      if (!e0) continue;
      const hiddenLane = filters.hiddenLanes.has(lanes[b.lane].id);
      if (hiddenLane) { ctx.fillStyle = fg3; ctx.globalAlpha = 0.6; ctx.fillRect(b.x, b.top + 2, 1, ROW_HIDDEN_H - 4); ctx.globalAlpha = 1; continue; }
      const n = b.seqs.length;
      const m = markOf(e0);
      const t = atMs(e0);
      const dim = filters.dimInSpikes && (e0.kind === "layer" || e0.kind === "ui" || e0.kind === "entity") && !!inWindow(windows, t, "spike");
      const after = filters.markAfterAgent && e0.kind !== "agent" && e0.kind !== "hud" && !!inWindow(windows, t, "agent");
      const pad = 5, top = b.top + pad, bottom = b.bottom - pad, mid = (top + bottom) / 2, h = bottom - top;
      const color = tone(m.tone);
      ctx.globalAlpha = dim ? 0.3 : 1;
      ctx.fillStyle = color; ctx.strokeStyle = color; ctx.lineWidth = 1;
      const x = b.x + 0.5;
      switch (m.shape) {
        case "tick": {
          const w = n > 1 ? 3 : 2;
          const hh = n > 1 ? h : h * 0.72;
          if (m.hollow) ctx.strokeRect(x - w / 2, mid - hh / 2 + 0.5, w, hh - 1);
          else ctx.fillRect(Math.round(x - w / 2), mid - hh / 2, w, hh);
          break;
        }
        case "add": ctx.beginPath(); ctx.moveTo(x, top + 1); ctx.lineTo(x + 4, bottom - 2); ctx.lineTo(x - 4, bottom - 2); ctx.closePath(); ctx.fill(); break;
        case "remove": ctx.beginPath(); ctx.moveTo(x, bottom - 1); ctx.lineTo(x + 4, top + 2); ctx.lineTo(x - 4, top + 2); ctx.closePath(); ctx.fill(); break;
        case "open": ctx.lineWidth = m.hollow ? 1 : 2; ctx.beginPath(); ctx.moveTo(x, bottom); ctx.lineTo(x, top + 1); ctx.lineTo(x + 5, top + 1); ctx.stroke(); break;
        case "close": ctx.lineWidth = m.hollow ? 1 : 2; ctx.beginPath(); ctx.moveTo(x, top); ctx.lineTo(x, bottom - 1); ctx.lineTo(x + 5, bottom - 1); ctx.stroke(); break;
        case "diamond": ctx.beginPath(); ctx.moveTo(x, mid - 5); ctx.lineTo(x + 5, mid); ctx.lineTo(x, mid + 5); ctx.lineTo(x - 5, mid); ctx.closePath(); ctx.fill(); break;
        case "rule": ctx.lineWidth = 2; ctx.beginPath(); ctx.moveTo(x, top); ctx.lineTo(x, bottom); ctx.stroke(); break;
        case "block": {
          const len = e0.cause === "reload" ? e0.durationMs ?? 0 : e0.intervalMs ?? 0;
          const a = xOf(t - len, vp, width);
          const w = Math.max(3, b.x - a);
          ctx.globalAlpha = dim ? 0.3 : 0.9;
          ctx.fillRect(Math.round(b.x - w), top + 2, Math.round(w), h - 4);
          if (e0.cause !== "reload" && (e0.gcMs ?? 0) > 0) { ctx.fillStyle = tone("--color-p-gc"); ctx.fillRect(Math.round(b.x - w), bottom - 4, Math.round(w), 2); }
          break;
        }
      }
      ctx.globalAlpha = 1;
      if (n > 1 && ROW_H >= 22) {
        ctx.font = "bold 8.5px ui-sans-serif, system-ui, sans-serif"; ctx.fillStyle = tone("--color-fg-2"); ctx.textBaseline = "top";
        ctx.fillText(n > 99 ? "99+" : String(n), b.x + 3, b.top + 2);
        ctx.textBaseline = "middle";
      }
      if (after) { ctx.fillStyle = ring; ctx.beginPath(); ctx.arc(x, b.bottom - 2.5, 1.6, 0, Math.PI * 2); ctx.fill(); }
      if (selected !== undefined && b.seqs.includes(selected)) {
        ctx.strokeStyle = ring; ctx.lineWidth = 1.5;
        ctx.strokeRect(b.x - 5.5, b.top + 1.5, 12, b.bottom - b.top - 3);
        ctx.globalAlpha = 0.5; ctx.lineWidth = 1;
        ctx.beginPath(); ctx.moveTo(x, plotTop); ctx.lineTo(x, plotBottom); ctx.stroke(); ctx.globalAlpha = 1;
      }
    }

    // Now.
    if (nowX >= 0 && nowX <= width) {
      ctx.strokeStyle = tone("--color-success"); ctx.lineWidth = 1; ctx.globalAlpha = 0.6; ctx.setLineDash([2, 3]);
      ctx.beginPath(); ctx.moveTo(Math.round(nowX) + 0.5, plotTop); ctx.lineTo(Math.round(nowX) + 0.5, plotBottom); ctx.stroke();
      ctx.setLineDash([]); ctx.globalAlpha = 1;
    }
  }, [events, lanes, vp, now, filters, windows, selected, width, height, tops]);

  // ── Interaction ──────────────────────────────────────────────────
  const bucketAt = useCallback((cx: number, cy: number): Bucket | undefined => {
    let best: Bucket | undefined, bd = 7;
    for (const b of buckets.current) {
      if (cy < b.top || cy > b.bottom) continue;
      const d = Math.abs(b.x - cx);
      if (d < bd) { bd = d; best = b; }
    }
    return best;
  }, []);

  const local = (e: React.PointerEvent | React.MouseEvent | React.WheelEvent) => {
    const r = ref.current!.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  };

  const onPointerDown = (e: React.PointerEvent) => {
    if (e.button !== 0) return;
    const { x } = local(e);
    drag.current = { x0: x, vp, moved: false, lastX: x };
    ref.current?.setPointerCapture(e.pointerId);
  };
  const onPointerMove = (e: React.PointerEvent) => {
    const { x, y } = local(e);
    const d = drag.current;
    if (d) {
      if (!d.moved && Math.abs(x - d.x0) > 3) d.moved = true;
      if (d.moved) { onViewport(panBy(d.vp, x - d.x0, width), true); onHover(undefined); return; }
    }
    const b = bucketAt(x, y);
    onHover(b ? { bucket: b, clientX: e.clientX, clientY: e.clientY } : undefined);
  };
  const onPointerUp = (e: React.PointerEvent) => {
    const d = drag.current;
    drag.current = null;
    ref.current?.releasePointerCapture(e.pointerId);
    if (!d || d.moved) return;
    const { x, y } = local(e);
    const b = bucketAt(x, y);
    if (!b) { onSelect(undefined); return; }
    // Click a bucket again: the next event in it.
    const key = `${b.lane}:${b.x}`;
    const idx = lastClick.current.key === key ? (lastClick.current.index + 1) % b.seqs.length : 0;
    lastClick.current = { key, index: idx };
    onSelect(b.seqs[idx]);
  };
  // The plot sits in a scrolling page, so a plain wheel scrolls the page. ctrl / cmd + wheel (what a trackpad pinch
  // sends) zooms around the cursor; shift + wheel pans.
  const onWheel = (e: React.WheelEvent) => {
    const { x } = local(e);
    if (e.shiftKey) onViewport(panBy(vp, -(e.deltaY || e.deltaX), width), true);
    else if (e.ctrlKey || e.metaKey) onViewport(zoomAt(vp, Math.exp(e.deltaY * 0.0015), x, width), true);
  };
  // React's onWheel is passive: a non-passive listener is needed so preventDefault keeps the page (and the browser's
  // own zoom on ctrl+wheel) out of it, only when the wheel is ours.
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const h = (ev: WheelEvent) => { if ((ev.ctrlKey || ev.metaKey || ev.shiftKey) && ev.cancelable) ev.preventDefault(); };
    el.addEventListener("wheel", h, { passive: false });
    return () => el.removeEventListener("wheel", h);
  }, []);

  return (
    <canvas ref={ref} role="img" aria-label="Observer timeline: one lane per layer and per event kind, on one time axis" tabIndex={-1}
      className={`block w-full touch-none select-none ${drag.current?.moved ? "cursor-grabbing" : "cursor-crosshair"} ${className}`} style={{ height }}
      onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={onPointerUp} onPointerCancel={() => { drag.current = null; }} onPointerLeave={() => onHover(undefined)} onWheel={onWheel} />
  );
}
