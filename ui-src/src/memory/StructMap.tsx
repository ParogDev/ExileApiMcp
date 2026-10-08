import { memo, useEffect, useMemo, useRef, type ReactNode } from "react";
import { Icon } from "../icons";
import { decodeDotNet, fmtBytes, fmtValue, hexOff, leafOf, type Region, type Seg } from "./bytes";
import { CHECK_LABEL, KIND_LABEL, TONE_TEXT, checkTone, kindIcon, mix, segAlpha, segTone } from "./paint";
import { LIVE_FLASH_MS, type ByteChange, type MemoryStore, type Snapshot, type View } from "./store";
import { marksIn, type FindingMark, type StructFindings } from "./findingsModel";

export type MapFilter = "all" | "fields" | "cands" | "changed";

// ── Strip: the whole region as one bar, byte-accurate ────────────────

/** Minimap of the region: every segment at its true width, change ticks above, struct end marked. */
export function Strip({ store, snap, region, segs, sf }: { store: MemoryStore; snap: Snapshot; region: Region; segs: Seg[]; sf?: StructFindings }) {
  const pct = (n: number) => `${(n / region.size) * 100}%`;
  const sel = snap.selection;
  const hov = snap.hover;
  const changeRuns = useMemo(() => runs(snap.changes, region.size), [snap.changes, region.size]);
  const struct = region.structSize;
  return (
    <div className="px-0.5 pt-1">
      {/* Change ticks: where the last watch saw bytes move. */}
      <div className="relative h-1.5" aria-hidden>
        {changeRuns.map((r) => (
          <span key={r.off} className="absolute inset-y-0 rounded-sm" title={`${r.size} byte${r.size === 1 ? "" : "s"} changed at +${hexOff(r.off)}`}
            style={{ left: pct(r.off), width: `max(2px, ${pct(r.size)})`, background: `color-mix(in oklab, var(--color-m-change) ${Math.round(55 + r.recency * 45)}%, transparent)` }} />
        ))}
      </div>
      <div className="relative h-4 overflow-hidden rounded-sm bg-surface-3/60" role="img" aria-label={`Struct map, ${region.size} bytes`}>
        {segs.map((s) => {
          const found = s.kind === "gap" && marksIn(sf, s.off, s.size).length > 0;
          const tone = found ? "cand" : segTone(s);
          const alpha = found ? 30 : segAlpha(s);
          const strong = s.kind === "field" || s.kind === "cand" || found;
          return (
            <button
              key={s.id}
              type="button"
              tabIndex={-1}
              aria-label={`${segTitle(s)} at +${hexOff(s.off)}`}
              title={`${segTitle(s)} · +${hexOff(s.off)} (${s.off}) · ${fmtBytes(s.size)}`}
              onClick={() => store.select({ off: s.off, size: s.size, segId: s.id })}
              onMouseEnter={() => store.hover({ off: s.off, size: s.size })}
              onMouseLeave={() => store.hover(undefined)}
              className={`absolute inset-y-0 ${s.kind === "gap" && !found ? "m-hatch" : ""} ${strong ? "hover:brightness-110" : "hover:bg-fg/10"}`}
              style={{ left: pct(s.off), width: `max(${strong ? 2 : 1}px, ${pct(s.size)})`, background: alpha ? mix(tone, strong ? 75 : 45) : undefined }}
            />
          );
        })}
        {struct !== undefined && struct < region.size && (
          <span className="pointer-events-none absolute inset-y-0 w-px bg-fg/70" style={{ left: pct(struct) }} title={`Struct end: ${struct} bytes`} />
        )}
        {hov && <span className="pointer-events-none absolute inset-y-0 bg-fg/15" style={{ left: pct(hov.off), width: `max(2px, ${pct(hov.size)})` }} />}
        {sel && <span className="pointer-events-none absolute -inset-y-px rounded-[2px] ring-2 ring-ring" style={{ left: pct(sel.off), width: `max(3px, ${pct(sel.size)})` }} />}
      </div>
      <div className="tnum relative mt-0.5 h-3.5 font-code text-[9.5px] text-fg-3" aria-hidden>
        <span className="absolute left-0">0</span>
        {struct !== undefined && struct < region.size && struct / region.size > 0.12 && struct / region.size < 0.9 && (
          <span className="absolute -translate-x-1/2 whitespace-nowrap" style={{ left: pct(struct) }}>{hexOff(struct)} · struct end</span>
        )}
        <span className="absolute right-0">{hexOff(region.size)} · {region.size}</span>
      </div>
    </div>
  );
}

function runs(changes: ReadonlyMap<number, ByteChange>, size: number): { off: number; size: number; recency: number }[] {
  const out: { off: number; size: number; recency: number }[] = [];
  let i = 0;
  while (i < size) {
    const c = changes.get(i);
    if (!c) { i++; continue; }
    let j = i, rec = c.recency;
    while (changes.has(j + 1)) { j++; rec = Math.max(rec, changes.get(j)!.recency); }
    out.push({ off: i, size: j - i + 1, recency: rec });
    i = j + 1;
  }
  return out;
}

export function Legend({ struct }: { struct: boolean }) {
  const item = (swatch: ReactNode, label: string, title?: string) => <span className="inline-flex items-center gap-1" title={title}>{swatch}{label}</span>;
  const box = (cls: string, style?: React.CSSProperties) => <span className={`inline-block size-2 rounded-[2px] ${cls}`} style={style} />;
  return (
    <div className="flex flex-wrap items-center gap-x-2.5 gap-y-0.5 px-0.5 text-[10px] text-fg-3" aria-label="Legend">
      {struct ? (
        <>
          {item(box("", { background: mix("field", 75) }), "HUD maps this", "A field of the struct the HUD reads")}
          {item(box("", { background: mix("cand", 75) }), "found, not mapped", "Structure the HUD's struct doesn't name: candidates (vtables, vectors, pointers) and recorded findings (bits, offsets)")}
          {item(box("m-hatch bg-surface-3"), "unmapped", "Nothing known here")}
          {item(box("", { background: mix("warning", 80) }), "suspicious")}
          {item(box("", { background: mix("danger", 80) }), "invalid")}
        </>
      ) : (
        <>
          {item(box("", { background: mix("cand", 75) }), "module / vtable")}
          {item(box("", { background: mix("ptr", 75) }), "heap pointer")}
          {item(box("", { background: mix("num", 75) }), "number")}
          {item(box("", { background: mix("str", 75) }), "text")}
          {item(box("bg-surface-3"), "zero")}
        </>
      )}
      {item(<span className="inline-block h-1 w-2.5 rounded-sm" style={{ background: "var(--color-m-change)" }} />, "changed")}
    </div>
  );
}

// ── Rows: offset-ordered, grouped by nested struct ──────────────────

type RowItem = { t: "group"; key: string; name: string; off: number; end: number; count: number } | { t: "seg"; seg: Seg };

export function filterSegs(region: Region, filter: MapFilter, changes: ReadonlyMap<number, ByteChange>): Seg[] {
  const changed = (s: Seg) => { for (let i = s.off; i < s.off + s.size; i++) if (changes.has(i)) return true; return false; };
  switch (filter) {
    case "fields": return region.segs.filter((s) => s.kind === "field" || (s.kind === "slot" && s.slot?.kind !== "zero"));
    case "cands": return region.segs.filter((s) => s.kind === "cand" || (s.kind === "slot" && (s.slot?.kind === "vtable" || s.slot?.kind === "module" || s.slot?.kind === "heap" || s.slot?.kind === "text")));
    case "changed": return region.segs.filter(changed);
    default: return region.segs;
  }
}

function toRows(segs: Seg[]): RowItem[] {
  const rows: RowItem[] = [];
  let open: string | undefined;
  for (let i = 0; i < segs.length; i++) {
    const s = segs[i];
    const g = s.group;
    if (g && g !== open) {
      let end = s.off + s.size, count = 0;
      for (let j = i; j < segs.length && segs[j].group === g; j++) { end = segs[j].off + segs[j].size; if (segs[j].kind === "field") count++; }
      rows.push({ t: "group", key: `grp:${g}:${s.off}`, name: g, off: s.off, end, count });
    }
    open = g;
    rows.push({ t: "seg", seg: s });
  }
  return rows;
}

export function MapRows({ store, snap, view, region, segs, now, fill, maxHeight = "20rem", sf }: {
  store: MemoryStore; snap: Snapshot; view: View; region: Region; segs: Seg[]; now: number; fill?: boolean; maxHeight?: string; sf?: StructFindings;
}) {
  const rows = useMemo(() => toRows(segs), [segs]);
  const body = useRef<HTMLDivElement>(null);
  const sel = snap.selection;

  // A selection made elsewhere (strip, hex, watch list) scrolls its row into view.
  useEffect(() => {
    if (!sel?.segId) return;
    body.current?.querySelector<HTMLElement>(`[data-seg="${CSS.escape(sel.segId)}"]`)?.scrollIntoView({ block: "nearest" });
  }, [sel?.segId]);

  const segRows = rows.map((r, i) => [r, i] as const).filter(([r]) => r.t === "seg");
  const onKey = (e: React.KeyboardEvent) => {
    if (!segRows.length) return;
    const pos = segRows.findIndex(([r]) => r.t === "seg" && r.seg.id === sel?.segId);
    const pick = (p: number) => {
      const r = segRows[Math.max(0, Math.min(segRows.length - 1, p))][0];
      if (r.t === "seg") store.select({ off: r.seg.off, size: r.seg.size, segId: r.seg.id });
    };
    switch (e.key) {
      case "ArrowDown": e.preventDefault(); pick(pos < 0 ? 0 : pos + 1); break;
      case "ArrowUp": e.preventDefault(); pick(pos < 0 ? segRows.length - 1 : pos - 1); break;
      case "Home": e.preventDefault(); pick(0); break;
      case "End": e.preventDefault(); pick(segRows.length - 1); break;
      case "Enter": {
        const s = sel?.segId ? region.segs.find((x) => x.id === sel.segId) : undefined;
        const target = s && followTarget(s);
        if (target) { e.preventDefault(); store.follow(target.address, target.via); }
        break;
      }
      case "Escape": e.preventDefault(); store.select(undefined); break;
    }
  };

  return (
    <div
      ref={body}
      role="listbox"
      aria-label="Struct map rows"
      tabIndex={0}
      onKeyDown={onKey}
      className={`scroll-thin overflow-y-auto overflow-x-hidden outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring ${fill ? "min-h-0 flex-1" : ""}`}
      style={{ maxHeight: fill ? undefined : maxHeight, minHeight: fill ? undefined : "6rem", scrollPaddingBlock: "1.5rem" }}
    >
      {rows.length === 0 && <p className="px-3 py-4 text-center text-[11.5px] text-fg-3">Nothing matches this filter.</p>}
      {rows.map((r) => r.t === "group" ? (
        <div key={r.key} className="sticky top-0 z-10 flex h-6 items-center gap-2 border-b border-line/60 bg-surface px-2 text-[10.5px] font-semibold uppercase tracking-wide text-fg-2">
          <span className="inline-block size-1.5 rounded-sm" style={{ background: mix("field", 80) }} />
          {r.name}
          <span className="tnum font-normal normal-case tracking-normal text-fg-3">{hexOff(r.off)}–{hexOff(r.end)} · {r.count} field{r.count === 1 ? "" : "s"}</span>
        </div>
      ) : (
        <SegRow key={r.seg.id} seg={r.seg} region={region} view={view} selected={sel?.segId === r.seg.id || (!sel?.segId && !!sel && sel.off >= r.seg.off && sel.off < r.seg.off + r.seg.size)}
          hovered={!!snap.hover && snap.hover.off < r.seg.off + r.seg.size && snap.hover.off + snap.hover.size > r.seg.off && snap.hover.off !== sel?.off}
          change={changeOf(snap.changes, r.seg)} flashAt={flashOf(snap.liveFlash, r.seg, now)} store={store}
          marks={marksIn(sf, r.seg.off, r.seg.size)} bitNames={r.seg.field ? sf?.bitNames.get(leafOf(r.seg.field.name)) : undefined} />
      ))}
    </div>
  );
}

function changeOf(changes: ReadonlyMap<number, ByteChange>, s: Seg): ByteChange | undefined {
  let best: ByteChange | undefined;
  for (let i = s.off; i < s.off + s.size; i++) { const c = changes.get(i); if (c && (!best || c.recency > best.recency)) best = c; }
  return best;
}
function flashOf(flash: ReadonlyMap<number, number>, s: Seg, now: number): number {
  let at = 0;
  for (let i = s.off; i < s.off + s.size; i++) { const t = flash.get(i); if (t && t > at) at = t; }
  return at && now - at < LIVE_FLASH_MS ? at : 0;
}

/** Where a segment's value points, if it can be followed. */
export function followTarget(s: Seg): { address: string; via: string } | undefined {
  if (s.kind === "cand" && s.cand) {
    const c = s.cand;
    if (c.kind === "std::vector" && c.first) return { address: c.first, via: `+${c.off} vector.First` };
    if (c.kind === "self") return undefined;
    if (c.value && /^0x[0-9a-f]+$/i.test(c.value) && c.kind !== "text") return { address: c.value, via: `+${c.off} ${KIND_LABEL[c.kind] ?? c.kind}` };
  }
  if (s.kind === "field" && s.field) {
    const f = s.field;
    if (f.size === 8 && typeof f.value === "string" && /^0x[0-9a-f]+$/i.test(f.value) && f.kind && f.kind !== "int" && f.kind !== "zero" && f.kind !== "bad-pointer" && f.kind !== "text") return { address: f.value, via: `+${f.off} ${f.name}` };
  }
  if (s.kind === "slot" && s.slot) {
    const k = s.slot.kind;
    if (k === "heap" || k === "vtable" || k === "module") return { address: s.slot.hex, via: `+${s.off} ${KIND_LABEL[k] ?? k}` };
  }
  return undefined;
}

export function segTitle(s: Seg): string {
  switch (s.kind) {
    case "field": return s.field!.name;
    case "cand": return `candidate ${KIND_LABEL[s.cand!.kind] ?? s.cand!.kind}`;
    case "gap": return "unmapped";
    case "slot": return KIND_LABEL[s.slot!.kind] ?? s.slot!.kind;
    case "zeros": return `${s.count} zero slots`;
  }
}

/** Short text for a finding chip: the part after "=" of the title when there is one. */
export function markLabel(m: FindingMark): string {
  const t = m.finding.title;
  const eq = t.indexOf(" = ");
  const s = eq > 0 ? t.slice(eq + 3) : t;
  return s.length > 34 ? s.slice(0, 33) + "…" : s;
}

export function MarkChip({ m, onClick }: { m: FindingMark; onClick?: () => void }) {
  const verified = m.status === "verified";
  // A "stored" finding on a mapped field confirms the HUD's mapping rather than adding to it: a quiet green check.
  const confirms = m.finding.check?.kind === "stored";
  return (
    <span
      role={onClick ? "button" : undefined}
      onClick={onClick ? (e) => { e.stopPropagation(); onClick(); } : undefined}
      title={`${m.finding.title}\n${m.finding.id} · ${m.status}${m.bit !== undefined ? ` · bit ${m.bit}` : ""}${verified ? "" : "\nNot verified on this game yet: a hypothesis"}`}
      className={`inline-flex max-w-[16rem] shrink items-center gap-1 truncate rounded px-1 text-[9.5px] font-medium ${confirms ? "text-success" : "text-m-cand"} ${verified ? "" : "border border-dashed"} ${onClick ? "cursor-pointer hover:brightness-110" : ""}`}
      style={{ background: confirms ? "color-mix(in oklab, var(--color-success) 12%, transparent)" : mix("cand", verified ? 16 : 8), borderColor: verified ? undefined : confirms ? "color-mix(in oklab, var(--color-success) 50%, transparent)" : mix("cand", 50) }}
    >
      {confirms ? <Icon name="check" className="size-2.5 shrink-0" /> : m.bit !== undefined && <span className="tnum shrink-0 font-code opacity-80">b{m.bit}</span>}
      <span className={`truncate ${m.bit !== undefined || confirms ? "hidden xs:inline" : ""}`}>{confirms ? (verified ? "mapping verified by population" : "mapping to verify") : markLabel(m)}</span>
    </span>
  );
}

const SegRow = memo(function SegRow({ seg: s, region, view, selected, hovered, change, flashAt, store, marks, bitNames }: {
  seg: Seg; region: Region; view: View; selected: boolean; hovered: boolean; change?: ByteChange; flashAt: number; store: MemoryStore;
  marks: FindingMark[]; bitNames?: Map<number, string>;
}) {
  const found = marks.length > 0;
  const tone = s.kind === "gap" && found ? "cand" : segTone(s);
  const dim = (s.kind === "gap" && !found) || s.kind === "zeros" || (s.kind === "slot" && s.slot?.kind === "zero");
  const past = region.structSize !== undefined && s.off >= region.structSize;
  const target = followTarget(s);
  const select = () => store.select({ off: s.off, size: s.size, segId: s.id });

  let name: ReactNode, detail: ReactNode = null, value: ReactNode = null, badge: ReactNode = null;
  if (s.kind === "field" && s.field) {
    const f = s.field;
    const live = decodeDotNet(f.type, region.bytes, f.off, f.size);
    const v = live !== undefined && view.loadedAt ? live : f.value;
    name = <span className="max-w-[55%] shrink-0 truncate font-code text-[12px] font-medium text-fg">{f.name.includes(".") && <span className="font-normal text-fg-3">{f.name.slice(0, f.name.lastIndexOf(".") + 1)}</span>}{leafOf(f.name)}</span>;
    detail = <span className="hidden truncate font-code text-[10.5px] text-fg-3 xs:inline">{f.type}</span>;
    value = (
      <span key={flashAt} className={`tnum flex min-w-0 items-center justify-end gap-1.5 rounded px-0.5 font-code text-[11.5px] ${flashAt ? "m-flash" : ""}`}>
        {f.kind === "text" && f.text ? <span className="truncate text-k-str" title={f.text}>"{f.text}"</span> : null}
        <span className={`truncate ${f.kind && f.kind !== "int" && f.kind !== "zero" ? "text-m-ptr" : "text-fg"}`}>{fmtValue(v)}</span>
        {(() => {
          // Only flag-like fields (the server gave `bits`) or fields with a bit table; live bits from the bytes.
          if (!f.bits && !bitNames) return null;
          const bits = typeof v === "number" && f.size <= 4 ? bitsOfValue(v, f.size * 8) : f.bits;
          if (!bits?.length) return null;
          const named = bitNames ? bits.map((b) => bitNames.get(b)).filter((n): n is string => !!n) : [];
          if (named.length) return <span className="hidden shrink-0 items-center gap-0.5 sm:inline-flex" title={`Set bits: ${bits.map((b) => `${b}${bitNames?.get(b) ? ` ${bitNames.get(b)}` : ""}`).join(", ")} (bit table from findings)`}>{named.slice(0, 3).map((n) => <span key={n} className="rounded px-1 text-[9.5px] font-medium text-m-cand" style={{ background: mix("cand", 14) }}>{n}</span>)}{named.length > 3 && <span className="text-[9.5px] text-fg-3">+{named.length - 3}</span>}</span>;
          return <span className="hidden shrink-0 rounded bg-surface-3 px-1 text-[9.5px] text-fg-2 sm:inline" title={`Set bits: ${bits.join(", ")}`}>bits {bits.join(",")}</span>;
        })()}
      </span>
    );
    if (f.check !== "ok") {
      const ct = checkTone(f.check);
      badge = <span className={`shrink-0 rounded px-1 text-[9.5px] font-semibold uppercase ${f.check === "unread" ? "bg-surface-3 text-fg-3" : `${TONE_TEXT[ct]}`}`} style={f.check === "unread" ? undefined : { background: mix(ct, 16) }} title={f.why ?? CHECK_LABEL[f.check]}>{CHECK_LABEL[f.check]}</span>;
    }
  } else if (s.kind === "cand" && s.cand) {
    const c = s.cand;
    name = (
      <span className="flex min-w-0 items-center gap-1.5 text-[12px]">
        <Icon name={kindIcon(c.kind)} className="size-3 shrink-0 text-m-cand" />
        <span className="shrink-0 font-medium text-m-cand">{KIND_LABEL[c.kind] ?? c.kind}</span>
        {c.rtti && <span className="truncate font-code text-[10.5px] text-fg-2" title={c.rtti}>{c.rtti}</span>}
      </span>
    );
    detail = <span className="hidden truncate font-code text-[10.5px] text-fg-3 xs:inline" title={c.detail}>{c.kind === "std::vector" ? vectorSummary(c.detail) : c.points ?? c.section ?? c.detail ?? ""}</span>;
    value = <span className="tnum truncate font-code text-[11.5px] text-fg-2" title={c.value ?? c.first}>{c.kind === "text" && c.text ? <span className="text-k-str">"{c.text}"</span> : c.kind === "std::vector" ? c.first : c.value}</span>;
  } else if (s.kind === "slot" && s.slot) {
    const sl = s.slot;
    const zero = sl.kind === "zero";
    name = (
      <span className={`flex min-w-0 items-center gap-1.5 text-[12px] ${zero ? "text-fg-3" : TONE_TEXT[tone]}`}>
        {!zero && <Icon name={kindIcon(sl.kind)} className="size-3 shrink-0" />}
        <span className="truncate font-medium">{KIND_LABEL[sl.kind] ?? sl.kind}</span>
        {sl.rtti && <span className="truncate font-code text-[10.5px] font-normal text-fg-2" title={sl.rtti}>{sl.rtti}</span>}
      </span>
    );
    detail = <span className="hidden truncate font-code text-[10.5px] text-fg-3 xs:inline">{sl.points ?? (sl.section ? `${sl.section} ${sl.rva ?? ""}` : "")}</span>;
    value = <span key={flashAt} className={`tnum truncate rounded px-0.5 font-code text-[11.5px] ${zero ? "text-fg-3" : "text-fg"} ${flashAt ? "m-flash" : ""}`} title={sl.hex}>
      {sl.kind === "text" && sl.text ? <span className="text-k-str">"{sl.text}"</span> : sl.kind === "int" || sl.kind === "float" ? sl.value : sl.hex}
      {sl.bits && sl.bits.length > 0 && <span className="ml-1.5 rounded bg-surface-3 px-1 text-[9.5px] text-fg-2" title={`Set bits: ${sl.bits.join(", ")}`}>bits {sl.bits.join(",")}</span>}
    </span>;
  } else if (s.kind === "zeros") {
    name = <span className="text-[11.5px] italic text-fg-3">{s.count} zero slots</span>;
    value = <span className="tnum font-code text-[11px] text-fg-3">{fmtBytes(s.size)}</span>;
  } else if (found) {
    name = <span className="flex min-w-0 items-center gap-1.5 text-[12px]"><Icon name="sparkle" className="size-3 shrink-0 text-m-cand" /><span className="truncate font-medium text-m-cand">finding</span></span>;
    value = <span className="tnum font-code text-[11px] text-fg-3">{fmtBytes(s.size)}</span>;
  } else {
    name = <span className="text-[11.5px] italic text-fg-3">{past ? "past the struct end" : "unmapped"}</span>;
    value = <span className="tnum font-code text-[11px] text-fg-3">{fmtBytes(s.size)}</span>;
  }
  const markChips = marks.length > 0 && (
    <span className="flex min-w-0 shrink items-center gap-1 overflow-hidden">
      {marks.slice(0, 2).map((m, i) => <MarkChip key={`${m.finding.id}:${m.bit ?? i}`} m={m} />)}
      {marks.length > 2 && <span className="shrink-0 text-[9.5px] text-m-cand">+{marks.length - 2}</span>}
    </span>
  );

  return (
    <div
      data-seg={s.id}
      role="option"
      aria-selected={selected}
      onClick={select}
      onDoubleClick={() => { if (target) store.follow(target.address, target.via); }}
      onMouseEnter={() => store.hover({ off: s.off, size: s.size })}
      onMouseLeave={() => store.hover(undefined)}
      title={`${segTitle(s)} · +${hexOff(s.off)} (${s.off}) · ${fmtBytes(s.size)}${target ? "\nDouble-click or Enter: follow the pointer" : ""}${change ? `\nChanged ×${change.count} during the watch` : ""}`}
      className={`group relative grid cursor-default grid-cols-[3.4rem_3px_minmax(0,1.4fr)_minmax(0,1fr)_auto] items-center gap-x-2 pr-2 text-[12px] xs:grid-cols-[5.25rem_2.5rem_3px_minmax(0,1.3fr)_minmax(0,1fr)_auto] ${dim ? "h-5" : "h-6"} ${selected ? "bg-ring/10 shadow-[inset_2px_0_0_var(--color-ring)]" : hovered ? "bg-surface-2" : "hover:bg-surface-2"}`}
    >
      <span className="tnum flex items-baseline gap-1 pl-2 font-code text-[11px]">
        <span className={dim ? "text-fg-3" : "text-fg-2"}>{hexOff(s.off)}</span>
        <span className="hidden text-[9.5px] text-fg-3 xs:inline">{s.off}</span>
      </span>
      <span className="tnum hidden font-code text-[10px] text-fg-3 xs:block">{s.size}B</span>
      <span className={`h-[70%] rounded-[1px] ${s.kind === "gap" && !found ? "m-hatch bg-surface-3" : s.kind === "zeros" ? "bg-surface-3" : ""}`} style={(s.kind === "gap" && !found) || s.kind === "zeros" ? undefined : { background: mix(tone, 85) }} aria-hidden />
      <span className="flex min-w-0 items-center gap-2">{name}{markChips || detail}</span>
      <span className="flex min-w-0 items-center justify-end">{value}</span>
      <span className="flex items-center gap-1">
        {badge}
        {change && (
          <span className="tnum shrink-0 rounded px-1 font-code text-[9.5px] font-semibold text-surface" title={`${change.unmapped ? "Unmapped bytes changed" : "Changed"} ×${change.count}${change.noisy ? " · noisy" : ""}`}
            style={{ background: `color-mix(in oklab, var(--color-m-change) ${Math.round(55 + change.recency * 45)}%, transparent)` }}>
            Δ{change.count > 1 ? change.count : ""}
          </span>
        )}
        {target && <Icon name="arrowUpRight" className={`size-3 shrink-0 text-fg-3 ${selected ? "" : "opacity-0 group-hover:opacity-100"}`} />}
        {!target && !badge && !change && <span className="size-3" />}
      </span>
      {past && <span className="pointer-events-none absolute inset-y-0 left-0 w-0.5 bg-fg-3/30" aria-hidden />}
    </div>
  );
});

function bitsOfValue(v: number, nbits: number): number[] {
  const out: number[] = [];
  const u = v >>> 0;
  for (let b = 0; b < Math.min(32, nbits); b++) if ((u >>> b) & 1) out.push(b);
  return out;
}

/** "First=0x.. Last=0x.. End=0x.. (32 bytes used, 128 capacity)" -> "32 B used / 128 cap". */
export function vectorSummary(detail?: string): string {
  const m = /\((\d+) bytes used, (\d+) capacity\)/.exec(detail ?? "");
  return m ? `${m[1]} B used / ${m[2]} cap` : detail ?? "";
}

