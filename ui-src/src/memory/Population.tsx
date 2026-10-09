import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { EmptyState, IconButton, SectionLabel } from "../components";
import { SmallButton } from "../explorer/Tree";
import { Icon } from "../icons";
import { fmtValue, hexByte, hexOff } from "./bytes";
import { mix } from "./paint";
import type { MemoryStore, PopState, Snapshot } from "./store";
import type { ExplainedBit, LayoutField } from "./types";

// Population view: every item of a collection as a row, every byte as a column, drawn on a canvas so 200 × 1024
// cells stay cheap. Above the grid: the struct's fields over the columns and a heatmap of the bits a label
// explains (memory_correlate). Pick a byte and a bit; the rows regroup by a label and show who has it set.

const LABEL_W = 148;
const BIT_ROWS = 8;

export interface PopulationHost {
  send?: (text: string) => void;
  ask?: (text: string) => void;
}

export function Population({ store, snap, fullscreen, host }: { store: MemoryStore; snap: Snapshot; fullscreen: boolean; host: PopulationHost }) {
  const pop = snap.pop;
  const [path, setPath] = useState(pop.path);
  const [labels, setLabels] = useState(pop.labels.join(", "));
  useEffect(() => { setPath(pop.path); setLabels(pop.labels.join(", ")); }, [pop.path, pop.labels]);
  const parseLabels = () => labels.split(/[,\s]+/).map((s) => s.trim()).filter(Boolean);
  const dirty = path.trim() !== pop.path || parseLabels().join(",") !== pop.labels.join(",");

  const toolbar = (
    <form className="flex flex-wrap items-center gap-1.5 border-b border-line bg-surface-2 px-2 py-1.5" onSubmit={(e) => { e.preventDefault(); void store.loadPopulation(path, parseLabels()); }}>
      <div className="relative min-w-0 flex-1 basis-[16rem]">
        <Icon name="list" className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-fg-3" />
        <input value={path} onChange={(e) => setPath(e.target.value)} spellCheck={false} aria-label="Collection path" placeholder="GameController.IngameState.ServerData.PlayerStashTabs"
          className="h-7 w-full rounded-md border border-transparent bg-surface pl-7 pr-2 font-code text-[11.5px] placeholder:text-fg-3 focus:border-ring focus:outline-none" />
      </div>
      <div className="relative min-w-0 flex-1 basis-[10rem]">
        <Icon name="hash" className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-fg-3" />
        <input value={labels} onChange={(e) => setLabels(e.target.value)} spellCheck={false} aria-label="Known per-item properties to test against (comma separated)" placeholder="Name, Affinity, TabType"
          className="h-7 w-full rounded-md border border-transparent bg-surface pl-7 pr-2 font-code text-[11.5px] placeholder:text-fg-3 focus:border-ring focus:outline-none" />
      </div>
      <button type="submit" disabled={pop.loading || !path.trim()} className={`h-7 shrink-0 rounded-md px-2.5 text-[11px] font-medium disabled:opacity-50 ${dirty ? "bg-fg text-surface" : "border border-line bg-surface text-fg-2 hover:text-fg"}`}>
        {pop.loading ? "Reading…" : dirty ? "Read population" : "Read again"}
      </button>
    </form>
  );

  if (!pop.data && !pop.loading && !pop.error) {
    return (
      <section className="overflow-hidden rounded-lg border border-line bg-surface">{toolbar}
        <EmptyState icon="grid" title="Read a population" className="py-6">Every item of a collection, bytes across, items down. Give the properties you already know per item (labels) and the view finds which bits they explain.</EmptyState>
      </section>
    );
  }

  return (
    <div className={fullscreen ? "grid min-h-0 flex-1 grid-cols-1 gap-3 sm:grid-cols-[minmax(0,1fr)_minmax(19rem,24rem)]" : "flex flex-col gap-2.5"}>
      <section aria-label="Population grid" className={`flex min-h-0 flex-col overflow-hidden rounded-lg border border-line bg-surface ${fullscreen ? "min-h-0" : ""}`}>
        {toolbar}
        {pop.error && !pop.loading && (
          <EmptyState icon="warning" title="Could not read the population" className="py-5">
            <span className="code-wrap block text-danger">{pop.error}</span>
            <span className="mt-1 block">The path must be a collection of memory objects (a list of entities, stash tabs, UI children). Each item needs an Address.</span>
            <div className="mt-2"><SmallButton icon="sync" onClick={() => void store.loadPopulation(pop.path, pop.labels)}>Retry</SmallButton></div>
          </EmptyState>
        )}
        {pop.loading && !pop.data && <div className="space-y-1.5 p-3" aria-hidden>{Array.from({ length: 8 }, (_, i) => <div key={i} className="shimmer h-2.5 rounded-sm" style={{ width: `${95 - (i % 3) * 10}%` }} />)}</div>}
        {pop.data && pop.bytes && <Grid store={store} pop={pop} fullscreen={fullscreen} />}
      </section>
      <aside className={fullscreen ? "scroll-thin flex min-h-0 flex-col gap-3 overflow-y-auto pr-1" : "flex flex-col gap-2.5"}>
        {pop.data && pop.bytes && <BitPanel store={store} pop={pop} host={host} variant={fullscreen ? "panel" : "card"} />}
      </aside>
    </div>
  );
}

// ── The grid ────────────────────────────────────────────────────────

interface GroupRow { t: "group"; key: string; label: string; count: number; setCount: number }
interface ItemRow { t: "item"; index: number; i: number }
type Row = GroupRow | ItemRow;

function labelText(v: unknown): string {
  if (v === undefined || v === null) return "—";
  return typeof v === "string" ? v : fmtValue(v);
}

/** Rows grouped by the chosen label (groups by set-count of the selected bit, then label value), else by index. */
function buildRows(pop: PopState): Row[] {
  const data = pop.data!, bytes = pop.bytes!;
  const bit = pop.bit;
  const isSet = (i: number) => !!bit && ((bytes[i][bit.byte] >> bit.bit) & 1) === 1;
  if (!pop.groupBy) return data.items.map((it, i) => ({ t: "item", index: it.index, i }));
  const groups = new Map<string, number[]>();
  data.items.forEach((it, i) => { const k = labelText(it.labels?.[pop.groupBy!]); (groups.get(k) ?? groups.set(k, []).get(k)!).push(i); });
  const list = [...groups.entries()].map(([k, idx]) => ({ k, idx, set: idx.filter(isSet).length }));
  // Groups where the bit is set (fully, then partly) first, then by numeric value, then name.
  list.sort((a, b) => {
    if (bit) { const ra = a.set === a.idx.length ? 0 : a.set > 0 ? 1 : 2, rb = b.set === b.idx.length ? 0 : b.set > 0 ? 1 : 2; if (ra !== rb) return ra - rb; }
    const na = Number(a.k), nb = Number(b.k);
    if (!Number.isNaN(na) && !Number.isNaN(nb)) return na - nb;
    return a.k.localeCompare(b.k);
  });
  const rows: Row[] = [];
  for (const g of list) {
    rows.push({ t: "group", key: g.k, label: `${pop.groupBy} = ${g.k}`, count: g.idx.length, setCount: g.set });
    for (const i of g.idx) rows.push({ t: "item", index: data.items[i].index, i });
  }
  return rows;
}

/**
 * A theme token as a colour the canvas can parse. The tokens are light-dark(...) expressions, which
 * getComputedStyle returns verbatim for custom properties, so resolve them through an element's `color`.
 */
function cssVar(el: HTMLElement, name: string): string {
  let probe = el.querySelector<HTMLElement>(":scope > [data-color-probe]");
  if (!probe) {
    probe = document.createElement("span");
    probe.dataset.colorProbe = "";
    probe.style.cssText = "position:absolute;width:0;height:0;overflow:hidden;pointer-events:none";
    el.appendChild(probe);
  }
  probe.style.color = `var(${name})`;
  return getComputedStyle(probe).color || "#888";
}

function Grid({ store, pop, fullscreen }: { store: MemoryStore; pop: PopState; fullscreen: boolean }) {
  const data = pop.data!, bytes = pop.bytes!;
  const size = data.size;
  const wrap = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const heat = useRef<HTMLCanvasElement>(null);
  const [cw, setCw] = useState(10);
  const [themeTick, setThemeTick] = useState(0);
  const rows = useMemo(() => buildRows(pop), [pop]);
  const rowH = fullscreen ? 13 : 12;
  const explained = useMemo(() => explainedMap(pop), [pop]);
  const fields = pop.layout?.fields ?? [];

  // Column width from the available width: whole grid visible when it fits, else at least 6 px per byte.
  useLayoutEffect(() => {
    const el = wrap.current;
    if (!el) return;
    const measure = () => setCw(Math.max(6, Math.min(16, Math.floor((el.clientWidth - LABEL_W - 8) / size))));
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [size]);

  // Redraw on theme changes (the host flips color-scheme; the canvas does not).
  useEffect(() => {
    const mo = new MutationObserver(() => setThemeTick((t) => t + 1));
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "style", "class"] });
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    const onMq = () => setThemeTick((t) => t + 1);
    mq.addEventListener("change", onMq);
    return () => { mo.disconnect(); mq.removeEventListener("change", onMq); };
  }, []);

  // Items canvas.
  useEffect(() => {
    const c = canvas.current, el = wrap.current;
    if (!c || !el) return;
    const dpr = window.devicePixelRatio || 1;
    const w = size * cw, h = rows.length * rowH;
    c.width = Math.round(w * dpr); c.height = Math.round(h * dpr);
    c.style.width = `${w}px`; c.style.height = `${h}px`;
    const ctx = c.getContext("2d")!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    const fg = cssVar(el, "--color-fg"), change = cssVar(el, "--color-m-change"), surface3 = cssVar(el, "--color-surface-3"), ring = cssVar(el, "--color-ring"), field = cssVar(el, "--color-m-field");
    const bit = pop.bit;
    rows.forEach((r, ri) => {
      const y = ri * rowH;
      if (r.t === "group") { ctx.fillStyle = surface3; ctx.globalAlpha = 0.5; ctx.fillRect(0, y, w, rowH); ctx.globalAlpha = 1; return; }
      const b = bytes[r.i];
      for (let x = 0; x < size; x++) {
        const v = b[x];
        if (v === 0) continue;
        ctx.fillStyle = fg;
        ctx.globalAlpha = bit ? 0.06 + (v / 255) * 0.22 : 0.1 + (v / 255) * 0.6;
        ctx.fillRect(x * cw, y, cw - 1, rowH - 1);
      }
      ctx.globalAlpha = 1;
      if (bit) {
        const set = ((b[bit.byte] >> bit.bit) & 1) === 1;
        ctx.fillStyle = set ? change : surface3;
        ctx.fillRect(bit.byte * cw, y, cw - 1, rowH - 1);
        if (set) { ctx.fillStyle = fg; ctx.globalAlpha = 0.9; ctx.fillRect(bit.byte * cw + Math.max(1, cw / 2 - 1), y + Math.max(1, rowH / 2 - 1), 2, 2); ctx.globalAlpha = 1; }
      }
      if (pop.hoverItem === r.index) { ctx.strokeStyle = ring; ctx.globalAlpha = 0.6; ctx.strokeRect(0.5, y + 0.5, w - 1, rowH - 2); ctx.globalAlpha = 1; }
    });
    // Mapped-field column bands: a faint blue under mapped bytes so "the HUD reads this" is visible per column.
    for (const f of fields) { ctx.fillStyle = field; ctx.globalAlpha = 0.07; ctx.fillRect(f.off * cw, 0, f.size * cw - 1, h); }
    ctx.globalAlpha = 1;
    if (bit) { ctx.strokeStyle = ring; ctx.lineWidth = 1.5; ctx.strokeRect(bit.byte * cw - 0.75, 0.75, cw + 0.5, h - 1.5); }
  }, [rows, bytes, size, cw, rowH, pop.bit, pop.hoverItem, fields, themeTick]);

  // Heatmap canvas: 8 rows (bits) × size columns; explained bits coloured by label, near misses hatched.
  useEffect(() => {
    const c = heat.current, el = wrap.current;
    if (!c || !el) return;
    const dpr = window.devicePixelRatio || 1;
    const bh = 5;
    const w = size * cw, h = BIT_ROWS * bh;
    c.width = Math.round(w * dpr); c.height = Math.round(h * dpr);
    c.style.width = `${w}px`; c.style.height = `${h}px`;
    const ctx = c.getContext("2d")!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    const surface3 = cssVar(el, "--color-surface-3"), ring = cssVar(el, "--color-ring"), fg = cssVar(el, "--color-fg");
    ctx.fillStyle = surface3; ctx.globalAlpha = 0.6; ctx.fillRect(0, 0, w, h); ctx.globalAlpha = 1;
    // Constant bits (all 0 or all 1) are drawn empty; varying bits get a faint mark so structure shows even before correlating.
    for (let x = 0; x < size; x++) for (let bit = 0; bit < 8; bit++) {
      let ones = 0;
      for (const b of bytes) ones += (b[x] >> bit) & 1;
      if (ones === 0 || ones === bytes.length) continue;
      ctx.fillStyle = fg; ctx.globalAlpha = 0.14; ctx.fillRect(x * cw, (7 - bit) * bh, cw - 1, bh - 1); ctx.globalAlpha = 1;
    }
    for (const [key, e] of explained) {
      const [bx, bb] = key.split(":").map(Number);
      ctx.fillStyle = e.color(el);
      ctx.globalAlpha = e.near ? 0.45 : 1;
      ctx.fillRect(bx * cw, (7 - bb) * bh, cw - 1, bh - 1);
      ctx.globalAlpha = 1;
    }
    if (pop.bit) { ctx.strokeStyle = ring; ctx.lineWidth = 1.5; ctx.strokeRect(pop.bit.byte * cw - 0.75, (7 - pop.bit.bit) * bh - 0.75, cw + 0.5, bh + 0.5); }
  }, [bytes, size, cw, explained, pop.bit, themeTick]);

  const cellAt = (e: React.MouseEvent, el: HTMLElement) => {
    const r = el.getBoundingClientRect();
    return { x: Math.floor((e.clientX - r.left) / cw), y: e.clientY - r.top };
  };
  const [tip, setTip] = useState<{ x: number; y: number; text: string } | undefined>();

  const onGridMove = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const { x, y } = cellAt(e, e.currentTarget);
    const ri = Math.floor(y / rowH);
    const row = rows[ri];
    if (!row || row.t !== "item" || x < 0 || x >= size) { store.hoverItem(undefined); setTip(undefined); return; }
    store.hoverItem(row.index);
    const v = bytes[row.i][x];
    const f = fields.find((f) => x >= f.off && x < f.off + f.size);
    setTip({ x: e.clientX, y: e.clientY, text: `[${row.index}] ${labelText(data.items[row.i].labels?.Name ?? data.items[row.i].labels?.[pop.labels[0] ?? ""])} · +${hexOff(x)} (${x}) = ${hexByte(v)}${v ? ` · bits ${bitsOf(v).join(",")}` : ""}${f ? ` · ${f.name}` : " · unmapped"}` });
  };
  const onGridClick = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const { x, y } = cellAt(e, e.currentTarget);
    const row = rows[Math.floor(y / rowH)];
    if (x < 0 || x >= size) return;
    if (row?.t === "item" && e.detail === 2) { store.openItem(row.index); return; }
    // Click a byte: keep the bit if one is picked on this byte, else the lowest varying bit, else bit 0.
    const cur = pop.bit;
    if (cur?.byte === x) return;
    let bit = 0;
    for (let b = 0; b < 8; b++) { let ones = 0; for (const bb of bytes) ones += (bb[x] >> b) & 1; if (ones > 0 && ones < bytes.length) { bit = b; break; } }
    store.selectBit({ byte: x, bit });
  };
  const onHeatClick = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const { x, y } = cellAt(e, e.currentTarget);
    const bit = 7 - Math.floor(y / 5);
    if (x >= 0 && x < size && bit >= 0 && bit < 8) store.selectBit({ byte: x, bit });
  };

  const total = data.items.length;
  return (
    <div ref={wrap} className={`relative flex min-h-0 flex-col ${fullscreen ? "flex-1" : ""}`}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-2 py-1.5 text-[10.5px] text-fg-3">
        <span className="tnum"><span className="font-medium text-fg-2">{total}</span> items × <span className="font-medium text-fg-2">{size}</span> bytes{data.struct && <span className="font-code"> · {data.struct.split(".").pop()}</span>}{data.truncated && <span className="text-warning"> · {data.truncated}</span>}</span>
        <label className="flex items-center gap-1">Group by
          <select value={pop.groupBy ?? ""} onChange={(e) => store.setGroupBy(e.target.value || undefined)} className="h-5 rounded-sm border border-line bg-surface px-1 text-[10.5px] text-fg">
            <option value="">index</option>
            {pop.labels.map((l) => <option key={l} value={l}>{l}</option>)}
          </select>
        </label>
        <span className="ml-auto flex items-center gap-2">
          {pop.correlating ? <span className="inline-flex items-center gap-1"><Icon name="sync" className="spin size-3" />correlating…</span>
            : pop.correlate ? <span>{explained.size} explained bit{explained.size === 1 ? "" : "s"}</span>
            : pop.correlateError ? <span className="text-danger" title={pop.correlateError}>correlate failed</span> : null}
          <SmallButton icon="sync" onClick={() => void store.correlate()} disabled={pop.correlating} title="memory_correlate: which bits each label explains">Correlate</SmallButton>
        </span>
      </div>
      <div className={`scroll-thin relative min-h-0 overflow-auto ${fullscreen ? "flex-1" : "max-h-[26rem]"}`} onMouseLeave={() => { store.hoverItem(undefined); setTip(undefined); }}>
        <div className="relative" style={{ width: LABEL_W + size * cw, minWidth: "100%" }}>
          {/* Field ribbon: the HUD's struct over the columns. */}
          <div className="sticky top-0 z-20 flex bg-surface">
            <div className="sticky left-0 z-30 flex shrink-0 items-end bg-surface px-2 pb-0.5 text-[9.5px] text-fg-3" style={{ width: LABEL_W }}>{fields.length ? "HUD struct" : ""}</div>
            <div className="relative h-5" style={{ width: size * cw }}>
              {fields.map((f) => (
                <span key={f.off} title={`${f.name} (${f.type}) at +${hexOff(f.off)}, ${f.size} B`}
                  className="absolute bottom-0.5 h-3.5 overflow-hidden truncate rounded-xs px-0.5 font-code text-[9px] leading-[0.9rem] text-fg"
                  style={{ left: f.off * cw, width: Math.max(2, f.size * cw - 1), background: mix("field", 30) }}>
                  {f.size * cw >= 22 ? f.name.split(".").pop() : ""}
                </span>
              ))}
              {!fields.length && <span className="absolute bottom-0.5 text-[9.5px] text-fg-3">no struct for these items</span>}
            </div>
          </div>
          {/* Heatmap of explained bits. */}
          <div className="sticky top-5 z-20 flex bg-surface">
            <div className="sticky left-0 z-30 shrink-0 bg-surface px-2 text-[9.5px] leading-[10px] text-fg-3" style={{ width: LABEL_W }}>
              <div>bit 7</div><div className="mt-[20px]">bit 0</div>
            </div>
            <canvas ref={heat} onClick={onHeatClick} className="cursor-crosshair" aria-label="Explained bits heatmap" />
          </div>
          <div className="flex">
            {/* Row labels, sticky on the left. */}
            <div className="sticky left-0 z-10 shrink-0 bg-surface" style={{ width: LABEL_W }}>
              {rows.map((r) => r.t === "group" ? (
                <div key={`g${r.key}`} className="flex items-center gap-1 truncate bg-surface-3/50 px-2 text-[10px] font-semibold text-fg-2" style={{ height: rowH }} title={`${r.label}: ${r.count} items${pop.bit ? `, bit set in ${r.setCount}` : ""}`}>
                  <span className="truncate">{r.label}</span>
                  <span className="tnum ml-auto shrink-0 font-normal text-fg-3">{pop.bit ? <><span className={r.setCount === r.count ? "text-m-change" : r.setCount ? "text-warning" : ""}>{r.setCount}</span>/{r.count}</> : r.count}</span>
                </div>
              ) : (
                <button key={`i${r.index}`} type="button" onClick={() => store.openItem(r.index)} onMouseEnter={() => store.hoverItem(r.index)}
                  className={`flex w-full items-center gap-1 truncate px-2 text-left font-code text-[10px] hover:bg-surface-2 ${pop.hoverItem === r.index ? "bg-surface-2 text-fg" : "text-fg-2"}`} style={{ height: rowH, lineHeight: `${rowH}px` }}
                  title={`[${r.index}] ${Object.entries(data.items[r.i].labels ?? {}).map(([k, v]) => `${k}=${labelText(v)}`).join(" · ")}\nClick: open in the struct view`}>
                  <span className="tnum w-5 shrink-0 text-fg-3">{r.index}</span>
                  <span className="truncate">{labelText(data.items[r.i].labels?.Name ?? data.items[r.i].labels?.[pop.labels[0] ?? ""] ?? data.items[r.i].address)}</span>
                  {pop.bit && ((bytes[r.i][pop.bit.byte] >> pop.bit.bit) & 1) === 1 && <span className="ml-auto size-1.5 shrink-0 rounded-full" style={{ background: "var(--color-m-change)" }} />}
                </button>
              ))}
            </div>
            <canvas ref={canvas} onMouseMove={onGridMove} onClick={onGridClick} className="cursor-crosshair" aria-label="Items by bytes" />
          </div>
        </div>
        {tip && <div className="pointer-events-none fixed z-50 max-w-xs rounded-md border border-line bg-surface px-2 py-1 font-code text-[10.5px] shadow-lg" style={{ left: tip.x + 12, top: tip.y + 14 }}>{tip.text}</div>}
      </div>
      <div className="flex items-center gap-2 border-t border-line px-2 py-1 text-[10px] text-fg-3">
        <span>click a column: pick that byte · heatmap: pick a bit · double-click a row: open the item</span>
      </div>
    </div>
  );
}

function bitsOf(v: number): number[] {
  const out: number[] = [];
  for (let b = 0; b < 8; b++) if (v & (1 << b)) out.push(b);
  return out;
}

interface Explained { label: string; e: ExplainedBit; near: boolean; color: (el: HTMLElement) => string }

const LABEL_COLORS = ["--color-m-cand", "--color-m-ptr", "--color-k-str", "--color-k-enum", "--color-k-bool"];

/** "byte:bit" -> the best explanation (perfect over near miss). */
function explainedMap(pop: PopState): Map<string, Explained> {
  const m = new Map<string, Explained>();
  const c = pop.correlate;
  if (!c) return m;
  c.findings.forEach((f, li) => {
    const colorVar = LABEL_COLORS[li % LABEL_COLORS.length];
    const color = (el: HTMLElement) => cssVar(el, colorVar);
    for (const e of f.bitsExplained) { const k = `${e.byte - c.offset}:${e.bit}`; if (!m.has(k) || m.get(k)!.near) m.set(k, { label: f.label, e, near: false, color }); }
    for (const e of f.nearMisses ?? []) { const k = `${e.byte - c.offset}:${e.bit}`; if (!m.has(k)) m.set(k, { label: f.label, e, near: true, color }); }
  });
  return m;
}

export function labelColor(pop: PopState, label: string): string {
  const found = pop.correlate?.findings.findIndex((f) => f.label === label) ?? -1;
  const idx = (found < 0 ? 0 : found) % LABEL_COLORS.length;
  return "var(" + LABEL_COLORS[idx] + ")";
}

// ── The side panel: the selected bit, who has it, what explains it ──

function BitPanel({ store, pop, host, variant }: { store: MemoryStore; pop: PopState; host: PopulationHost; variant: "card" | "panel" }) {
  const data = pop.data!, bytes = pop.bytes!;
  const bit = pop.bit;
  const panel = variant === "panel" ? "rounded-lg border border-line bg-surface-2 p-3" : "rounded-lg border border-line bg-surface p-3";
  const explained = useMemo(() => explainedMap(pop), [pop]);
  const field = bit ? pop.layout?.fields.find((f) => bit.byte >= f.off && bit.byte < f.off + f.size) : undefined;
  const fieldBit = field && bit ? (bit.byte - field.off) * 8 + bit.bit : undefined;
  const setIdx = bit ? data.items.map((_, i) => i).filter((i) => ((bytes[i][bit.byte] >> bit.bit) & 1) === 1) : [];
  const explainedHere = bit ? explained.get(`${bit.byte}:${bit.bit}`) : undefined;
  const allHere = bit && pop.correlate ? pop.correlate.findings.flatMap((f) => [...f.bitsExplained.map((e) => ({ f, e, near: false })), ...(f.nearMisses ?? []).map((e) => ({ f, e, near: true }))]).filter((x) => x.e.byte - pop.correlate!.offset === bit.byte && x.e.bit === bit.bit) : [];
  const perByte = bit ? Array.from({ length: 8 }, (_, b) => bytes.filter((x) => ((x[bit.byte] >> b) & 1) === 1).length) : [];

  // Breakdown of who has the bit by each label's values.
  const breakdown = bit ? pop.labels.map((label) => {
    const groups = new Map<string, { n: number; set: number }>();
    data.items.forEach((it, i) => { const k = labelText(it.labels?.[label]); const g = groups.get(k) ?? { n: 0, set: 0 }; g.n++; if (setIdx.includes(i)) g.set++; groups.set(k, g); });
    const rows = [...groups.entries()].sort((a, b) => (b[1].set / b[1].n) - (a[1].set / a[1].n) || b[1].n - a[1].n);
    // A label whose values are mostly unique (names) says nothing as a breakdown.
    const singletons = rows.filter(([, g]) => g.n === 1).length;
    return { label, rows: rows.slice(0, 12), more: rows.length - 12, useful: rows.length > 1 && singletons * 2 < rows.length };
  }).filter((b) => b.useful) : [];

  const describe = () => {
    if (!bit) return "";
    const where = `+${bit.byte} (${hexOff(bit.byte)}) bit ${bit.bit}${field ? ` = ${field.name} bit ${fieldBit}` : " (unmapped by the HUD)"}`;
    const who = `set in ${setIdx.length} of ${data.items.length} items (${setIdx.slice(0, 8).map((i) => labelText(data.items[i].labels?.Name ?? data.items[i].index)).join(", ")}${setIdx.length > 8 ? ", …" : ""})`;
    const expl = allHere.length ? `; memory_correlate: ${allHere.map((x) => `${x.near ? "~" : "="} ${x.e.equals} (${x.e.evidence ?? `set for ${x.e.setFor}`}, ${x.e.counterexamples} counterexamples)`).join("; ")}` : "";
    return `Population ${data.path} (${data.items.length} items × ${data.size} bytes${data.struct ? `, ${data.struct}` : ""}). Looking at ${where}: ${who}${expl}.`;
  };

  return (
    <section aria-label="Selected bit" className={`${panel} text-xs`}>
      {!bit ? (
        <EmptyState icon="binary" title="Pick a byte and a bit" className="py-4">Click a column in the grid or a cell in the heatmap. Coloured heatmap cells are bits a label fully explains; faint ones vary between items.</EmptyState>
      ) : (
        <>
          <div className="flex items-center gap-1.5 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3">
            <span className="size-1.5 rounded-full" style={{ background: field ? "var(--color-m-field)" : "var(--color-m-cand)" }} />
            {field ? "mapped field" : "unmapped by the HUD"}
            <span className="tnum ml-auto font-code font-normal normal-case tracking-normal">+{hexOff(bit.byte)} ({bit.byte}) · bit {bit.bit}</span>
          </div>
          <h2 className="mt-1 font-code text-[13px] font-semibold">{field ? <>{field.name} <span className="text-fg-2">bit {fieldBit}</span></> : `byte +${bit.byte} bit ${bit.bit}`}</h2>
          {field && <p className="font-code text-[11px] text-fg-2">{field.type} · value {fmtValue(field.value)} in item [{data.items[0]?.index ?? 0}]</p>}

          <div className="mt-2 rounded-md bg-surface-3/60 px-2.5 py-2">
            <div className="flex items-baseline gap-2">
              <span className="tnum text-[15px] font-semibold text-m-change">{setIdx.length}</span>
              <span className="text-fg-2">of {data.items.length} items have it set</span>
            </div>
            <div className="mt-1.5 h-1.5 overflow-hidden rounded-sm bg-surface-3"><div className="h-full" style={{ width: `${(setIdx.length / data.items.length) * 100}%`, background: "var(--color-m-change)" }} /></div>
            {setIdx.length > 0 && setIdx.length <= 16 && <p className="code-wrap mt-1.5 font-code text-[10.5px] text-fg-2">{setIdx.map((i) => labelText(data.items[i].labels?.Name ?? `[${data.items[i].index}]`)).join(", ")}</p>}
          </div>

          {/* The 8 bits of this byte: pick another one. */}
          <div className="mt-2.5">
            <SectionLabel>Byte +{bit.byte} · bits</SectionLabel>
            <div className="mt-1 grid grid-cols-8 gap-1">
              {perByte.map((n, b) => {
                const ex = explained.get(`${bit.byte}:${b}`);
                const varies = n > 0 && n < data.items.length;
                return (
                  <button key={b} type="button" onClick={() => store.selectBit({ byte: bit.byte, bit: b })} aria-pressed={b === bit.bit}
                    title={`bit ${b}: set in ${n} of ${data.items.length}${ex ? ` · ${ex.near ? "~" : "="} ${ex.e.equals}` : ""}`}
                    className={`flex h-8 flex-col items-center justify-center rounded-md border text-[10px] ${b === bit.bit ? "border-ring bg-ring/10" : "border-line hover:bg-surface-3"} ${varies ? "text-fg" : "text-fg-3"}`}
                    style={ex ? { boxShadow: `inset 0 -3px 0 ${labelColor(pop, ex.label)}` } : undefined}>
                    <span className="font-semibold">{b}</span>
                    <span className="tnum text-[9px]">{n}</span>
                  </button>
                );
              })}
            </div>
          </div>

          {/* Explanations. */}
          <div className="mt-2.5">
            <SectionLabel right={pop.correlating && <Icon name="sync" className="spin size-3" />}>Explained by</SectionLabel>
            {allHere.length === 0 ? (
              <p className="mt-1 text-[11px] text-fg-3">{pop.correlate ? "No label explains this bit with enough evidence. Group the rows by a label and look for a pattern, or add a label." : pop.correlating ? "Correlating…" : "Run Correlate."}</p>
            ) : (
              <ul className="mt-1 space-y-1">
                {allHere.map((x, i) => (
                  <li key={i} className="rounded-md border px-2 py-1.5" style={{ borderColor: labelColor(pop, x.f.label), background: `color-mix(in oklab, ${labelColor(pop, x.f.label)} 8%, transparent)` }}>
                    <div className="flex items-center gap-1.5">
                      <span className="font-code text-[11.5px] font-semibold">{x.near ? "≈" : "="} {x.e.equals}</span>
                      {x.near ? <span className="rounded-sm bg-warning/15 px-1 text-[10px] text-warning">{x.e.counterexamples} counterexample{x.e.counterexamples === 1 ? "" : "s"}</span> : <span className="rounded-sm bg-success/15 px-1 text-[10px] text-success">0 counterexamples</span>}
                    </div>
                    {x.e.evidence && <p className="tnum mt-0.5 text-[10.5px] text-fg-2">{x.e.evidence}</p>}
                    {x.e.setFor && <p className="code-wrap mt-0.5 font-code text-[10.5px] text-fg-2">set for {x.e.setFor}</p>}
                    {x.e.counterexampleItems?.length ? <p className="code-wrap mt-0.5 font-code text-[10px] text-fg-3">but: {x.e.counterexampleItems.map((c) => `[${c.index}] ${Object.entries(c.labels ?? {}).map(([k, v]) => `${k}=${labelText(v)}`).join(" ")}`).join("; ")}</p> : null}
                  </li>
                ))}
              </ul>
            )}
            {explainedHere === undefined && pop.correlate?.findings.some((f) => f.tooLittleEvidence) && <p className="mt-1 text-[10px] text-fg-3">{pop.correlate.findings.find((f) => f.tooLittleEvidence)!.tooLittleEvidence}</p>}
          </div>

          {/* Who has it, by label value. */}
          {breakdown.map((b) => (
            <div key={b.label} className="mt-2.5">
              <SectionLabel right={<button type="button" className="underline-offset-2 hover:underline" onClick={() => store.setGroupBy(b.label)}>group rows</button>}>By {b.label}</SectionLabel>
              <ul className="mt-1 space-y-0.5">
                {b.rows.map(([k, g]) => (
                  <li key={k} className="grid grid-cols-[minmax(0,1fr)_3.5rem_4rem] items-center gap-2 text-[10.5px]">
                    <span className="truncate font-code" title={k}>{k}</span>
                    <span className="h-1.5 overflow-hidden rounded-sm bg-surface-3"><span className="block h-full" style={{ width: `${(g.set / g.n) * 100}%`, background: "var(--color-m-change)" }} /></span>
                    <span className={`tnum text-right ${g.set === g.n ? "text-m-change" : g.set ? "text-warning" : "text-fg-3"}`}>{g.set}/{g.n}</span>
                  </li>
                ))}
                {b.more > 0 && <li className="text-[10px] text-fg-3">+{b.more} more values</li>}
              </ul>
            </div>
          ))}

          <div className="mt-3 flex flex-wrap gap-1.5">
            {pop.hoverItem !== undefined && <SmallButton icon="layers" onClick={() => store.openItem(pop.hoverItem!)}>Open [{pop.hoverItem}] in struct</SmallButton>}
            {host.send && <SmallButton icon="send" onClick={() => host.send!(describe())}>Send to Claude</SmallButton>}
            {host.ask && <SmallButton icon="sparkle" tone="primary" onClick={() => host.ask!(`${describe()} What does this bit mean, and how would a plugin read it? Short answer.`)}>Ask Claude</SmallButton>}
          </div>
        </>
      )}

      {/* Every explained bit, as a list to click through. */}
      {pop.correlate && (
        <div className="mt-3 border-t border-line pt-2">
          <SectionLabel>All explained bits</SectionLabel>
          <ExplainedList pop={pop} onPick={(b) => store.selectBit(b)} />
        </div>
      )}
    </section>
  );
}

function ExplainedList({ pop, onPick }: { pop: PopState; onPick: (b: { byte: number; bit: number }) => void }) {
  const c = pop.correlate!;
  const items: ReactNode[] = [];
  for (const f of c.findings) {
    const color = labelColor(pop, f.label);
    const perfect = f.bitsExplained.filter((e) => !e.equals.startsWith("NOT "));
    const near = (f.nearMisses ?? []).filter((e) => !e.equals.startsWith("NOT "));
    items.push(
      <li key={f.label} className="mt-1.5">
        <div className="flex items-center gap-1.5 text-[10.5px]">
          <span className="size-2 rounded-xs" style={{ background: color }} />
          <span className="font-semibold">{f.label}</span>
          <span className="tnum text-fg-3">{f.distinctValues} distinct values</span>
          {f.storedAt.length > 0 && <span className="tnum ml-auto font-code text-fg-2" title="Where the label's own value is stored">stored at {f.storedAt.map((s) => `+${s.offset - c.offset} (${s.type})`).join(", ")}</span>}
        </div>
        {perfect.length === 0 && near.length === 0 && <p className="ml-3.5 text-[10px] text-fg-3">explains no bit in range{f.tooLittleEvidence ? ` (${f.tooLittleEvidence})` : ""}</p>}
        <ul className="ml-3.5 mt-0.5 flex flex-wrap gap-1">
          {perfect.map((e, i) => <Chip key={`p${i}`} e={e} off={c.offset} color={color} active={pop.bit?.byte === e.byte - c.offset && pop.bit?.bit === e.bit} onPick={onPick} />)}
          {near.slice(0, 6).map((e, i) => <Chip key={`n${i}`} e={e} off={c.offset} color={color} near active={pop.bit?.byte === e.byte - c.offset && pop.bit?.bit === e.bit} onPick={onPick} />)}
          {near.length > 6 && <li className="text-[10px] text-fg-3">+{near.length - 6} near misses</li>}
        </ul>
      </li>,
    );
  }
  return <ul>{items}</ul>;
}

function Chip({ e, off, color, near, active, onPick }: { e: ExplainedBit; off: number; color: string; near?: boolean; active: boolean; onPick: (b: { byte: number; bit: number }) => void }) {
  return (
    <li>
      <button type="button" onClick={() => onPick({ byte: e.byte - off, bit: e.bit })} aria-pressed={active}
        title={`${e.equals}${e.evidence ? ` · ${e.evidence}` : ""}${e.setFor ? ` · set for ${e.setFor}` : ""} · ${e.counterexamples} counterexamples`}
        className={`tnum rounded-sm border px-1.5 py-0.5 font-code text-[10px] ${active ? "border-ring bg-ring/10 text-fg" : "border-transparent text-fg-2 hover:text-fg"} ${near ? "opacity-70" : ""}`}
        style={{ background: active ? undefined : `color-mix(in oklab, ${color} ${near ? 8 : 14}%, transparent)` }}>
        +{e.byte - off}.{e.bit} {near ? "≈" : "="} {e.equals.replace(/^a function of /, "f(") + (e.equals.startsWith("a function of") ? ")" : "")}
      </button>
    </li>
  );
}

export { IconButton, type LayoutField };
