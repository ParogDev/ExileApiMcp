import { memo, useEffect, useRef } from "react";
import { hexByte, hexOff, type Region } from "./bytes";
import { mix, segAlpha, segTone, type Tone } from "./paint";
import { LIVE_FLASH_MS, type ByteChange, type MemoryStore, type Snapshot } from "./store";

const PER_ROW = 16;

/** Classic 16-bytes-per-row dump: bytes tinted by what covers them, selection and hover shared with the map. */
export function HexView({ store, snap, region, now, fill, maxHeight = "14rem" }: {
  store: MemoryStore; snap: Snapshot; region: Region; now: number; fill?: boolean; maxHeight?: string;
}) {
  const body = useRef<HTMLDivElement>(null);
  const sel = snap.selection;
  const rows = Math.ceil(region.size / PER_ROW);

  useEffect(() => {
    if (!sel) return;
    body.current?.querySelector<HTMLElement>(`[data-row="${Math.floor(sel.off / PER_ROW)}"]`)?.scrollIntoView({ block: "nearest" });
  }, [sel?.off, sel?.size, sel]);

  const onClick = (e: React.MouseEvent) => {
    const t = (e.target as HTMLElement).closest<HTMLElement>("[data-off]");
    if (!t) return;
    const off = Number(t.dataset.off);
    store.selectByte(off, e.shiftKey && sel ? sel : undefined);
  };
  const onMove = (e: React.MouseEvent) => {
    const t = (e.target as HTMLElement).closest<HTMLElement>("[data-off]");
    if (!t) { store.hover(undefined); return; }
    const off = Number(t.dataset.off);
    const k = region.cover[off];
    const seg = k >= 0 ? region.segs[k] : undefined;
    store.hover(seg && seg.kind !== "gap" && seg.kind !== "zeros" ? { off: seg.off, size: seg.size } : { off, size: 1 });
  };

  return (
    <div
      ref={body}
      className={`scroll-thin overflow-auto font-code text-[11px] leading-[1.35rem] ${fill ? "min-h-0 flex-1" : ""}`}
      style={{ maxHeight: fill ? undefined : maxHeight }}
      onClick={onClick}
      onMouseMove={onMove}
      onMouseLeave={() => store.hover(undefined)}
      role="grid"
      aria-label="Hex dump"
    >
      <div className="min-w-max px-2 pb-1">
        <div className="tnum sticky top-0 z-10 flex bg-surface text-[9.5px] text-fg-3" aria-hidden>
          <span className="w-12 shrink-0" />
          {Array.from({ length: PER_ROW }, (_, i) => <span key={i} className={`w-[1.45rem] text-center ${i === 8 ? "ml-2" : ""}`}>{i.toString(16).toUpperCase().padStart(2, "0")}</span>)}
          <span className="ml-3 w-[6.5rem] text-left">ASCII</span>
        </div>
        {Array.from({ length: rows }, (_, r) => (
          <HexRow key={r} row={r} region={region} sel={sel} hover={snap.hover} changes={snap.changes} flash={snap.liveFlash} now={now} />
        ))}
      </div>
    </div>
  );
}

const HexRow = memo(function HexRow({ row, region, sel, hover, changes, flash, now }: {
  row: number; region: Region; sel?: { off: number; size: number }; hover?: { off: number; size: number };
  changes: ReadonlyMap<number, ByteChange>; flash: ReadonlyMap<number, number>; now: number;
}) {
  const start = row * PER_ROW;
  const end = Math.min(region.size, start + PER_ROW);
  const past = region.structSize !== undefined && start >= region.structSize;
  const cells = [];
  const ascii = [];
  for (let i = start; i < start + PER_ROW; i++) {
    if (i >= end) { cells.push(<span key={i} className={`inline-block w-[1.45rem] ${i === start + 8 ? "ml-2" : ""}`} />); ascii.push(<span key={i} className="inline-block w-[0.4rem]" />); continue; }
    const known = region.known[i] === 1;
    const k = region.cover[i];
    const seg = k >= 0 ? region.segs[k] : undefined;
    const tone: Tone = seg ? segTone(seg) : "none";
    const alpha = seg ? segAlpha(seg) : 0;
    const selected = !!sel && i >= sel.off && i < sel.off + sel.size;
    const hovered = !selected && !!hover && i >= hover.off && i < hover.off + hover.size;
    const ch = changes.get(i);
    const fl = flash.get(i);
    const flashing = fl !== undefined && now - fl < LIVE_FLASH_MS;
    const b = region.bytes[i];
    const zero = b === 0;
    const gapish = !seg || seg.kind === "gap" || seg.kind === "zeros";
    const style: React.CSSProperties = {};
    if (ch) style.background = `color-mix(in oklab, var(--color-m-change) ${Math.round(28 + ch.recency * 42)}%, transparent)`;
    else if (alpha) style.background = mix(tone, alpha);
    const cls = `inline-block w-[1.45rem] cursor-default rounded-[2px] text-center ${i === start + 8 ? "ml-2" : ""} ` +
      (!known ? "text-fg-3/50" : ch ? "font-semibold text-fg" : zero && gapish ? "text-fg-3/70" : "text-fg") +
      (selected ? " ring-2 ring-inset ring-ring" : hovered ? " ring-1 ring-inset ring-fg/40" : "") +
      (gapish && seg?.kind === "gap" && !ch && !selected ? " m-hatch" : "") +
      (flashing ? " m-flash" : "");
    cells.push(<span key={flashing ? `${i}-${fl}` : i} data-off={i} className={cls} style={style} aria-label={`+${hexOff(i)}`}>{known ? hexByte(b) : "··"}</span>);
    const printable = b >= 0x20 && b < 0x7f;
    ascii.push(
      <span key={i} data-off={i} className={`inline-block w-[0.4rem] cursor-default text-center ${selected ? "bg-ring/25 text-fg" : hovered ? "bg-fg/10" : ""} ${ch ? "font-semibold text-m-change" : printable ? "text-fg-2" : "text-fg-3/50"}`}>
        {known && printable ? String.fromCharCode(b) : "·"}
      </span>,
    );
  }
  return (
    <div data-row={row} role="row" className={`tnum flex items-center ${past ? "opacity-80" : ""}`}>
      <span className={`w-12 shrink-0 select-none text-[10px] ${past ? "text-fg-3/70" : "text-fg-3"}`} title={`+${start}`}>{hexOff(start).slice(2).padStart(4, "0")}</span>
      {cells}
      <span className="ml-3 flex w-[6.5rem]">{ascii}</span>
    </div>
  );
});
