import { useState, type ReactNode } from "react";
import { EmptyState, IconButton, SectionLabel } from "../components";
import { Icon } from "../icons";
import { SmallButton } from "../explorer/Tree";
import { addrPlus, decodeDotNet, flippedBitsFor, fmtBytes, fmtValue, hexOf, hexOff, interpret, isIntegerType, offPair, setBits, uintAt, type Region } from "./bytes";
import { CHECK_LABEL, KIND_LABEL, TONE_TEXT, checkTone, kindIcon, mix, segLabel, segTone, type Tone } from "./paint";
import { followTarget, vectorSummary } from "./StructMap";
import type { MemoryStore, Selection, Snapshot, View } from "./store";
import type { ChangedRange } from "./types";
import { marksIn, type StructFindings } from "./findingsModel";
import { MarkChip } from "./StructMap";

export interface InspectorHost {
  send?: () => void;
  ask?: () => void;
}

/** The selected bytes in depth: identity, address, value, every reading of the same bytes, bits, and the actions. */
export function Inspector({ store, snap, view, region, sel, host, variant, sf }: {
  store: MemoryStore; snap: Snapshot; view: View; region: Region; sel: Selection; host: InspectorHost; variant: "card" | "panel"; sf?: StructFindings;
}) {
  const seg = sel.segId ? region.segs.find((s) => s.id === sel.segId) : undefined;
  const marks = marksIn(sf, sel.off, sel.size);
  const fieldLeaf = seg?.field?.name.split(".").pop();
  const bitNames = fieldLeaf ? sf?.bitNames.get(fieldLeaf) : undefined;
  const tablesFor = fieldLeaf ? (sf?.tables.filter((t) => t.field === fieldLeaf) ?? []) : [];
  // Finding bits, numbered relative to the selection's first byte.
  const foundBits = new Map<number, string>();
  for (const m of marks) if (m.bit !== undefined) foundBits.set((m.off - sel.off) * 8 + m.bit, m.finding.title.includes(" = ") ? m.finding.title.slice(m.finding.title.indexOf(" = ") + 3) : m.finding.title);
  if (bitNames) for (const [b, n] of bitNames) foundBits.set(b, n);
  const tone: Tone = seg ? segTone(seg) : "none";
  const label = seg ? segLabel(seg) : "bytes";
  const address = addrPlus(region.address, sel.off);
  const cls = seg?.field ?? seg?.cand ?? seg?.slot;
  const ghidra = cls?.ghidra;
  const target = seg ? followTarget(seg) : undefined;
  const ranges = snap.watch?.viewId === view.id ? (snap.watch.result?.changedRanges ?? []) : [];
  const overlapping = ranges.filter((c) => c.off < sel.off + sel.size && c.off + c.size > sel.off);
  const flipped = flippedBitsFor(sel, overlapping);
  const [showBits, setShowBits] = useState<boolean | undefined>();

  const copy = async (text: string, what: string) => {
    try { await navigator.clipboard.writeText(text); store.toast("info", `Copied ${what}`); }
    catch { store.toast("error", "Clipboard blocked by the host; select the text instead"); }
  };

  // Title and type line.
  let title: ReactNode, sub: ReactNode = null;
  if (seg?.field) {
    const f = seg.field;
    title = <>{f.name.includes(".") && <span className="font-normal text-fg-3">{f.name.slice(0, f.name.lastIndexOf(".") + 1)}</span>}{f.name.slice(f.name.lastIndexOf(".") + 1)}</>;
    sub = <>{f.type}{f.kind && f.kind !== "int" && <span className="text-fg-3"> · {KIND_LABEL[f.kind] ?? f.kind}</span>}</>;
  } else if (seg?.cand) {
    const c = seg.cand;
    title = <span className="flex items-center gap-1.5"><Icon name={kindIcon(c.kind)} className="size-3.5 text-m-cand" />{KIND_LABEL[c.kind] ?? c.kind}</span>;
    sub = c.rtti ? <span title="RTTI class name">{c.rtti}</span> : c.kind === "std::vector" ? vectorSummary(c.detail) : c.points ?? c.detail ?? (c.section ? `${c.module ?? "module"} ${c.section}` : null);
  } else if (seg?.slot) {
    const s = seg.slot;
    title = <span className="flex items-center gap-1.5"><Icon name={kindIcon(s.kind)} className={`size-3.5 ${TONE_TEXT[tone]}`} />{KIND_LABEL[s.kind] ?? s.kind}</span>;
    sub = s.rtti ?? s.points ?? (s.section ? `${s.module ?? "module"} ${s.section}` : null);
  } else if (seg?.kind === "zeros") {
    title = `${seg.count} zero slots`;
  } else {
    title = region.structSize !== undefined && sel.off >= region.structSize ? "Past the struct end" : seg?.kind === "gap" ? "Unmapped bytes" : "Bytes";
    sub = seg?.kind === "gap" ? "The HUD's struct has no field here. Shift-click in the hex view to widen the range." : null;
  }

  // Value.
  const live = seg?.field ? decodeDotNet(seg.field.type, region.bytes, seg.field.off, seg.field.size) : undefined;
  const value = seg?.field ? (live !== undefined ? live : seg.field.value) : seg?.cand ? (seg.cand.kind === "std::vector" ? seg.cand.first : seg.cand.value) : seg?.slot ? (seg.slot.kind === "int" || seg.slot.kind === "float" ? seg.slot.value : seg.slot.hex) : undefined;
  const text = seg?.field?.text ?? seg?.cand?.text ?? seg?.slot?.text;
  const check = seg?.field?.check;
  const raw = hexOf(region.bytes, sel.off, sel.size);
  const interps = sel.size <= 64 ? interpret(region.bytes, sel.off, sel.size) : [];

  // Bits: integers up to 8 bytes; shown by default for flag-like values, changed bits, or small ad-hoc ranges.
  const bitsPossible = sel.size <= 8 && (seg?.field ? isIntegerType(seg.field.type) : seg?.slot ? seg.slot.kind === "int" || seg.slot.kind === "zero" : true);
  const serverBits = seg?.field?.bits ?? seg?.slot?.bits;
  const bitsDefault = bitsPossible && (!!serverBits?.length || flipped.length > 0 || foundBits.size > 0 || !seg || seg.kind === "gap" || (seg.field !== undefined && seg.field.size <= 4 && seg.field.type !== "Single"));
  const bitsOpen = showBits ?? bitsDefault;
  const set = bitsPossible ? setBits(region.bytes, sel.off, sel.size) : [];

  const panel = variant === "panel" ? "rounded-lg border border-line bg-surface-2 p-3" : "rounded-lg border border-line bg-surface p-3";

  return (
    <section aria-label="Selected bytes" className={`${panel} text-xs`}>
      <div className="flex items-center gap-1.5 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3">
        <span className={`size-1.5 rounded-full ${seg?.kind === "gap" ? "m-hatch bg-surface-3" : ""}`} style={seg?.kind === "gap" ? undefined : { background: tone === "none" ? "var(--color-fg-3)" : mix(tone, 90) }} aria-hidden />
        {label}
        {seg?.kind === "cand" && <span className="rounded px-1 font-semibold normal-case tracking-normal text-m-cand" style={{ background: mix("cand", 14) }}>not mapped by the HUD</span>}
        {check && check !== "ok" && (
          <span className={`rounded px-1 font-semibold normal-case tracking-normal ${TONE_TEXT[checkTone(check)]}`} style={{ background: mix(checkTone(check), 14) }} title={seg?.field?.why}>{CHECK_LABEL[check]}</span>
        )}
        <span className="tnum ml-auto font-code font-normal normal-case tracking-normal">{offPair(sel.off)} · {fmtBytes(sel.size)}</span>
      </div>

      <h2 className="code-wrap mt-1 font-code text-[13px] font-semibold leading-tight">{title}</h2>
      {sub && <p className="code-wrap mt-0.5 font-code text-[11px] text-fg-2">{sub}</p>}

      {/* Value */}
      <div className="mt-2 rounded-md bg-surface-3/60 px-2.5 py-2">
        {value !== undefined && (
          <div className="flex flex-wrap items-baseline gap-x-2">
            <span className={`tnum code-wrap font-code text-[13px] font-semibold ${seg?.field && seg.field.kind && seg.field.kind !== "int" ? "text-m-ptr" : ""}`}>{fmtValue(value)}</span>
            {text && <span className="code-wrap font-code text-[12px] text-k-str">"{text}"</span>}
            {seg?.field?.why && <span className={`text-[11px] ${TONE_TEXT[checkTone(check)]}`}>{seg.field.why}</span>}
          </div>
        )}
        {seg?.cand?.detail && seg.cand.kind !== "self" && <p className="code-wrap mt-0.5 font-code text-[10.5px] text-fg-2">{seg.cand.detail}</p>}
        {seg?.cand?.kind === "self" && <p className="mt-0.5 text-[11px] text-fg-2">{seg.cand.detail}. Embedded sub-objects often start with a vtable and a back-pointer like this.</p>}
        <p className={`code-wrap font-code text-[10.5px] text-fg-3 ${value !== undefined || seg?.cand?.detail ? "mt-1" : ""}`} title="Raw bytes, little-endian">{raw}</p>
      </div>

      {/* Addresses */}
      <dl className="mt-2 grid grid-cols-[3.25rem_minmax(0,1fr)_auto] items-start gap-x-2 gap-y-1">
        <dt className="pt-0.5 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3">Address</dt>
        <dd className="tnum code-wrap min-w-0 font-code text-[11px] leading-snug" title={`${region.address} + ${sel.off}`}>{address}</dd>
        <dd><IconButton icon="copy" label="Copy address" size="sm" onClick={() => copy(address, "address")} className="-my-0.5" /></dd>
        {ghidra && (
          <>
            <dt className="pt-0.5 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3">Ghidra</dt>
            <dd className="tnum code-wrap min-w-0 font-code text-[11px] leading-snug">
              {ghidra}
              {(cls?.section || cls?.rva) && <span className="text-fg-3"> · {cls.section} {cls.rva}</span>}
              {cls?.firstMethod && <span className="block text-fg-3">first method {cls.firstMethod}</span>}
            </dd>
            <dd><IconButton icon="copy" label="Copy Ghidra address" size="sm" onClick={() => copy(ghidra, "Ghidra address")} className="-my-0.5" /></dd>
          </>
        )}
      </dl>

      {/* Actions */}
      <div className="mt-2.5 flex flex-wrap gap-1.5">
        {target && <SmallButton icon="arrowUpRight" onClick={() => store.follow(target.address, target.via)} title={`memory_read at ${target.address}`}>Follow pointer</SmallButton>}
        {seg?.cand?.kind === "self" && seg.cand.detail && (() => { const m = /\+(\d+)/.exec(seg.cand!.detail!); return m ? <SmallButton icon="target" onClick={() => store.selectByte(Number(m[1]))}>Go to +{m[1]}</SmallButton> : null; })()}
        {interps.some((i) => i.label === "pointer" && i.address) && !target && (
          <SmallButton icon="arrowUpRight" onClick={() => { const i = interps.find((x) => x.label === "pointer")!; store.follow(i.address!, `+${sel.off} pointer`); }} title="Read the memory this 8-byte value points at">Follow as pointer</SmallButton>
        )}
        {typeof value === "string" && /^0x[0-9a-f]+$/i.test(value) && (
          <SmallButton icon="search" onClick={() => void store.where(value)} disabled={snap.where?.loading} title="memory_where: module section and Ghidra address, or heap, and what it points at">Where is it?</SmallButton>
        )}
        {bitsPossible && <SmallButton icon="binary" active={bitsOpen} onClick={() => setShowBits(!bitsOpen)} title="Show the value's bits">Bits</SmallButton>}
      </div>
      {(host.send || host.ask) && (
        <div className="mt-1.5 flex flex-wrap gap-1.5">
          {host.send && <SmallButton icon="send" onClick={host.send} title="Put this selection (address, offset, value) into Claude's context">Send to Claude</SmallButton>}
          {host.ask && <SmallButton icon="sparkle" tone="primary" onClick={host.ask} title="Ask in the conversation what this is">Ask Claude about this</SmallButton>}
        </div>
      )}

      {snap.where && <WherePanel where={snap.where} onClose={() => store.clearWhere()} />}

      {overlapping.length > 0 && <ChangedHere ranges={overlapping} sel={sel} />}

      {marks.some((m) => m.finding.check?.kind === "stored") && (
        <p className="mt-2 flex flex-wrap items-center gap-1.5 text-[10.5px] text-fg-2">
          {marks.filter((m) => m.finding.check?.kind === "stored").map((m) => <MarkChip key={m.finding.id} m={m} />)}
          <span>the value the HUD reads here is confirmed across the population{tablesFor.length ? "; bit names below come from a finding" : ""}.</span>
        </p>
      )}
      {marks.some((m) => m.finding.check?.kind !== "stored") && (
        <div className="mt-3 rounded-md border px-2.5 py-2" style={{ borderColor: mix("cand", 40), background: mix("cand", 6) }}>
          <SectionLabel right={<button type="button" className="underline-offset-2 hover:underline" onClick={() => store.setMode("findings")}>all findings</button>}><Icon name="sparkle" className="size-3 text-m-cand" />Found here, not in the HUD's struct</SectionLabel>
          <ul className="mt-1 space-y-1">
            {marks.filter((m) => m.finding.check?.kind !== "stored").map((m, i) => (
              <li key={`${m.finding.id}:${i}`} className="text-[11px]">
                <div className="flex flex-wrap items-center gap-1.5">
                  <MarkChip m={m} />
                  <span className={`tnum rounded-full border px-1.5 text-[10px] ${m.status === "verified" ? "border-success/30 text-success" : m.status === "differs" ? "border-warning/30 text-warning" : "border-line text-fg-3"}`}>{m.status === "unverified" ? "hypothesis here" : m.status}</span>
                </div>
                <p className="mt-0.5 leading-snug text-fg-2">{m.finding.title}</p>
              </li>
            ))}
          </ul>
        </div>
      )}

      {bitsPossible && bitsOpen && <BitGrid bytes={region.bytes} off={sel.off} size={sel.size} set={set} flipped={flipped} tone={tone === "none" ? "field" : tone} names={foundBits}
        namesFrom={tablesFor.length ? `${tablesFor.map((t) => `${t.finding.id} (${t.status === "verified" ? "verified" : `${t.status} here`})`).join(", ")}` : undefined} />}

      {interps.length > 0 && (
        <div className="mt-3">
          <SectionLabel>Same bytes read as</SectionLabel>
          <dl className="mt-1.5 grid grid-cols-[4rem_minmax(0,1fr)] gap-x-2 gap-y-0.5 text-[11px]">
            {interps.map((i) => (
              <div key={i.label} className="contents">
                <dt className="text-fg-3">{i.label}</dt>
                <dd className={`tnum code-wrap min-w-0 ${i.mono ? "font-code" : ""} ${i.dim ? "text-fg-3" : "text-fg"}`}>
                  {i.value}
                  {i.address && !target && <button type="button" className="ml-1.5 text-[10px] text-fg-3 underline-offset-2 hover:text-fg hover:underline" onClick={() => store.follow(i.address!, `+${sel.off} pointer`)}>follow</button>}
                </dd>
              </div>
            ))}
          </dl>
        </div>
      )}
    </section>
  );
}

function ChangedHere({ ranges, sel }: { ranges: ChangedRange[]; sel: Selection }) {
  return (
    <div className="mt-3 rounded-md border px-2.5 py-2" style={{ borderColor: mix("change", 40), background: mix("change", 7) }}>
      <SectionLabel><Icon name="diff" className="size-3 text-m-change" />Changed during the watch</SectionLabel>
      <ul className="mt-1 space-y-1">
        {ranges.map((c) => (
          <li key={c.off} className="text-[11px]">
            <span className="tnum font-code text-fg-2">+{hexOff(c.off)}</span>
            {c.off !== sel.off || c.size !== sel.size ? <span className="text-fg-3"> ({c.size} of the bytes)</span> : null}
            <span className="tnum ml-2 font-code"><span className="text-fg-3">{c.first}</span> <span className="text-fg-3">→</span> <span className="font-semibold">{c.last}</span></span>
            <span className="tnum ml-2 rounded bg-surface-3 px-1 text-[10px] text-fg-2">×{c.changes}</span>
            {c.noisy && <span className="ml-1 rounded bg-warning/15 px-1 text-[10px] text-warning">noisy</span>}
            {c.bitsFlipped && c.bitsFlipped.length > 0 && (
              <span className="ml-2 text-fg-2">bits <span className="font-code text-m-change">{c.bitsFlipped.join(", ")}</span>{c.bitsRelativeTo && <span className="text-fg-3"> of {c.bitsRelativeTo.replace(/^field /, "")}</span>}</span>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

/** One cell per bit, LSB first in each byte row of 8… laid out as 32 per row so bit indices read left to right from 0. */
export function BitGrid({ bytes, off, size, set, flipped, tone, names, namesFrom }: { bytes: Uint8Array; off: number; size: number; set: number[]; flipped: number[]; tone: Tone; names?: Map<number, string>; namesFrom?: string }) {
  const nbits = size * 8;
  const setS = new Set(set), flipS = new Set(flipped);
  const nameOf = (bit: number) => names?.get(bit);
  const perRow = nbits <= 16 ? nbits : 32;
  const rows = Math.ceil(nbits / perRow);
  const v = uintAt(bytes, off, size);
  return (
    <div className="mt-3">
      <SectionLabel right={<span className="tnum font-code">0x{v.toString(16).toUpperCase()}{set.length ? ` · set ${set.join(", ")}` : " · no bits set"}</span>}>
        <Icon name="binary" className="size-3" />Bits <span className="tnum font-normal">{nbits}</span>
      </SectionLabel>
      <div className="mt-1.5 space-y-1.5">
        {Array.from({ length: rows }, (_, r) => {
          const start = r * perRow;
          const n = Math.min(perRow, nbits - start);
          return (
            <div key={r}>
              <div className="grid gap-px" style={{ gridTemplateColumns: `repeat(${n}, minmax(0, 1fr))` }} role="img" aria-label={`Bits ${start} to ${start + n - 1}`}>
                {Array.from({ length: n }, (_, i) => {
                  const bit = start + i;
                  const on = setS.has(bit), fl = flipS.has(bit), nm = nameOf(bit);
                  return (
                    <span key={bit} title={`bit ${bit} = 0x${(1n << BigInt(bit)).toString(16).toUpperCase()}${nm ? ` · ${nm} (finding)` : ""}${on ? " · set" : ""}${fl ? " · flipped during the watch" : ""}`}
                      className={`h-4 rounded-[2px] ${bit % 8 === 7 && i !== n - 1 ? "mr-1" : ""} ${fl || nm ? "ring-2 ring-inset" : on ? "" : "bg-surface-3"}`}
                      style={{ background: on ? mix(tone, fl ? 95 : 80) : nm ? mix("cand", 10) : undefined, ...(fl ? { ["--tw-ring-color" as string]: "var(--color-m-change)" } : nm ? { ["--tw-ring-color" as string]: mix("cand", on ? 100 : 55) } : {}) }} />
                  );
                })}
              </div>
              <div className="tnum mt-0.5 grid font-code text-[9px] text-fg-3" style={{ gridTemplateColumns: `repeat(${n}, minmax(0, 1fr))` }} aria-hidden>
                {Array.from({ length: n }, (_, i) => {
                  const bit = start + i;
                  const show = bit % 8 === 0 || setS.has(bit) || flipS.has(bit) || bit === nbits - 1;
                  return <span key={bit} className={`text-center ${bit % 8 === 7 && i !== n - 1 ? "mr-1" : ""} ${flipS.has(bit) ? "font-semibold text-m-change" : setS.has(bit) ? "text-fg-2" : ""}`}>{show ? bit : ""}</span>;
                })}
              </div>
            </div>
          );
        })}
      </div>
      {names && names.size > 0 && namesFrom && <p className="mt-1.5 text-[10px] text-fg-3">Bit names from finding <span className="font-code text-fg-2">{namesFrom}</span>; the HUD's struct doesn't name them.</p>}
      {names && names.size > 0 && (
        <ul className="mt-1.5 flex flex-wrap gap-1">
          {[...names.entries()].sort((a, b) => a[0] - b[0]).map(([bit, nm]) => (
            <li key={bit} className={`tnum inline-flex items-center gap-1 rounded px-1 font-code text-[10px] ${setS.has(bit) ? "text-fg" : "text-fg-3"}`} style={{ background: mix("cand", setS.has(bit) ? 22 : 8) }} title={`bit ${bit}: ${nm}${setS.has(bit) ? " (set)" : ""}`}>
              <span className="opacity-70">{bit}</span>{nm.length > 28 ? nm.slice(0, 27) + "…" : nm}
            </li>
          ))}
        </ul>
      )}
      {flipped.length > 0 && <p className="mt-1 text-[10.5px] text-fg-2">Magenta outline: bits that flipped during the watch (<span className="font-code text-m-change">{flipped.join(", ")}</span>). A bit that toggles with one in-game action is a flag.</p>}
    </div>
  );
}

function WherePanel({ where, onClose }: { where: NonNullable<Snapshot["where"]>; onClose: () => void }) {
  const d = where.data;
  return (
    <div className="mt-3">
      <SectionLabel right={<IconButton icon="x" label="Hide" size="sm" onClick={onClose} className="-my-1" />}><Icon name="search" className="size-3" />Where is <span className="tnum font-code font-normal normal-case tracking-normal">{where.address}</span></SectionLabel>
      {where.loading ? <div className="mt-1.5 space-y-1.5"><div className="shimmer h-2.5 w-3/4 rounded" /><div className="shimmer h-2.5 w-1/2 rounded" /></div>
        : where.error ? <p className="mt-1.5 text-danger">{where.error}</p>
        : d ? (
          <dl className="mt-1.5 grid grid-cols-[4rem_minmax(0,1fr)] gap-x-2 gap-y-0.5 text-[11px]">
            <dt className="text-fg-3">kind</dt><dd className="font-medium">{KIND_LABEL[d.kind ?? ""] ?? d.kind ?? "?"}{d.rtti && <span className="font-code text-fg-2"> {d.rtti}</span>}</dd>
            {d.module && <><dt className="text-fg-3">module</dt><dd className="font-code">{d.module}{d.section && ` ${d.section}`}{d.rva && <span className="text-fg-3"> rva {d.rva}</span>}</dd></>}
            {d.ghidra && <><dt className="text-fg-3">Ghidra</dt><dd className="tnum font-code">{d.ghidra}{d.firstMethod && <span className="text-fg-3"> · first method {d.firstMethod}</span>}</dd></>}
            {d.points && <><dt className="text-fg-3">points at</dt><dd>{d.points}{d.text && <span className="font-code text-k-str"> "{d.text}"</span>}</dd></>}
            {d.region && <><dt className="text-fg-3">region</dt><dd className="text-fg-2">{d.region}</dd></>}
          </dl>
        ) : null}
    </div>
  );
}

export function NoSelection({ struct }: { struct: boolean }) {
  return (
    <EmptyState icon="target" title="Select bytes" className="rounded-lg border border-dashed border-line py-5">
      Click a row in the map, a byte in the hex view or a slice of the strip. {struct ? "Candidates (violet) are structure the HUD doesn't map yet." : "Pointers can be followed; the inspector reads the same bytes every way."}
    </EmptyState>
  );
}
