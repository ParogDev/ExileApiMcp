import { useMemo, useState, type ReactNode } from "react";
import { IconButton, SectionLabel } from "../components";
import { Icon } from "../icons";
import { SmallButton } from "../explorer/Tree";
import { fmtBytes, hexOff, leafOf, offPair, segAt, type Region } from "./bytes";
import { ACCESS_TONE, TONE_TEXT, mix, type Tone } from "./paint";
import type { MemoryStore, Selection, Snapshot, View } from "./store";
import type { Access, Confidence, FieldAccessResult, LayoutResult } from "./types";
import { KIND_LABEL, codeKey, fnAddress, gatesOf, ghidraCommand, marksIn, markSize, parseExcerpt, rankFunctions, roleLabel, scanPhase, widthsAtTarget, type CodeMark, type CodeMarks, type CodeQuery, type ExcerptModel, type FnView, type Gate, type Token } from "./codeModel";

export interface CodeHost {
  send?: (text: string, structured: Record<string, unknown>) => void;
  ask?: (text: string) => void;
}

const CONF_TONE: Record<Confidence, Tone> = { high: "success", medium: "warning", low: "none" };
const SHOW_FNS = 6;

/**
 * "What code uses this field?" for the selected bytes: a find_field_access lookup (static, Ghidra) with its
 * long-running state, then the functions ranked, their instructions kind-coloured, the decompiled excerpts with
 * struct offsets named and unmapped ones flagged, and the field-gating the code reveals.
 */
export function CodePanel({ store, snap, view, region, sel, cm, host, variant, now }: {
  store: MemoryStore; snap: Snapshot; view: View; region: Region; sel: Selection; cm: CodeMarks; host: CodeHost; variant: "card" | "panel"; now: number;
}) {
  const layout = view.data && "fields" in view.data ? (view.data as LayoutResult) : undefined;
  const target = store.codeTarget(sel, snap.bitSel);
  const structKey = store.structKeyOf(view);
  const q = snap.code.get(codeKey(structKey, target.offset, target.bit));
  const seg = segAt(region, target.offset);
  const fieldName = seg?.field ? leafOf(seg.field.name) : undefined;
  const canQuery = !!(view.target.path || layout);
  const [minKnown, setMinKnown] = useState(2);
  const [decompile, setDecompile] = useState(3);
  const [showOpts, setShowOpts] = useState(false);
  // What other lookups of this struct already said about these bytes (the discovery hook, before any lookup here).
  const seen = marksIn(cm, sel.off, sel.size).filter((m) => !m.isTarget || m.off !== target.offset);
  const panel = variant === "panel" ? "rounded-lg border border-line bg-surface-2 p-3" : "rounded-lg border border-line bg-surface p-3";
  const run = (o: { minKnown?: number; decompile?: number } = {}) => void store.findCode({ minKnown: o.minKnown ?? minKnown, decompile: o.decompile ?? decompile });

  const copy = async (text: string, what: string) => {
    try { await navigator.clipboard.writeText(text); store.toast("success", what, "Copied"); }
    catch { store.toast("error", "Select the text instead", "Clipboard blocked"); }
  };

  // Names for offsets referenced by the code.
  const nameAt = (off: number): OffName => {
    const s = segAt(region, off);
    if (!s) return region.structSize !== undefined && off >= region.structSize ? { kind: "past" } : { kind: "outside" };
    if (s.field) return { kind: "field", name: leafOf(s.field.name), full: s.field.name, type: s.field.type, size: s.field.size, start: s.off, inside: s.off !== off };
    if (s.cand) return { kind: "cand", name: s.cand.kind, size: s.size, start: s.off };
    if (region.structSize !== undefined && off >= region.structSize) return { kind: "past" };
    return { kind: "gap" };
  };

  const title = (
    <span className="code-wrap font-code font-normal normal-case tracking-normal">
      {fieldName && <span className="font-semibold text-fg">{fieldName} </span>}
      <span className="text-fg-2">{offPair(target.offset)}</span>
      {target.bit !== undefined && <span className="rounded-sm px-1 font-semibold text-warning" style={{ background: mix("warning", 14) }}> bit {target.bit}{seg?.field && seg.field.size > 1 && target.offset !== sel.off ? <span className="font-normal text-fg-3"> ({fieldName} bit {snap.bitSel})</span> : null}</span>}
    </span>
  );

  return (
    <section aria-label="Code that uses this field" className={`${panel} text-xs`} aria-live="polite">
      <SectionLabel right={q && q.status !== "running" && <IconButton icon="x" label="Forget this lookup (the server keeps its cache)" size="sm" onClick={() => store.forgetCode(q.key)} className="-my-1" />}>
        <Icon name="code" className="size-3" />Code
        <span className="font-normal normal-case tracking-normal text-fg-3">· static, Ghidra</span>
      </SectionLabel>
      <p className="mt-1 text-[11.5px] leading-snug">{title}</p>

      {seen.length > 0 && <SeenHere marks={seen} sel={sel} nameAt={nameAt} store={store} />}

      {!q && (
        <>
          {canQuery ? (
            <p className="mt-1.5 text-[11.5px] leading-snug text-fg-2">
              Finds the game functions that read, write or bit-test this offset through a base that also touches the struct's known fields, and decompiles the best ones. It reads the Ghidra copy of the exe, never the running game.
            </p>
          ) : (
            <p className="mt-1.5 text-[11.5px] leading-snug text-fg-2">Needs the struct's known fields as a fingerprint: open this object through a walker path, or overlay a struct type in the target options, then look up code.</p>
          )}
          <div className="mt-2 flex flex-wrap items-center gap-1.5">
            <SmallButton icon="search" tone="primary" disabled={!canQuery || target.offset === 0} onClick={() => run()} title={target.offset === 0 ? "Offset 0 has no displacement to search ([reg] alone); pick a field at +1 or above" : "find_field_access"}>Find code that uses this</SmallButton>
            <SmallButton icon="sliders" active={showOpts} onClick={() => setShowOpts(!showOpts)} title="minKnown and how many functions to decompile">Options</SmallButton>
            <span className="ml-auto flex items-center gap-1 text-[10.5px] text-fg-3" title="A program-wide instruction search per offset (the target plus up to 6 anchor fields), cached on disk afterwards"><Icon name="clock" className="size-3" />{cachedHint(snap, structKey, target.offset) ? "cached · instant" : "first run ≈ 30 s per offset"}</span>
          </div>
          {target.offset === 0 && <p className="mt-1.5 text-[11px] text-fg-3">Offset 0 can't be searched by displacement. Look up a neighbouring field instead: its code often reads +0 too (see "seen in code" above when it does).</p>}
          {showOpts && <Options minKnown={minKnown} decompile={decompile} setMinKnown={setMinKnown} setDecompile={setDecompile} anchors={layout?.fields.filter((f) => f.off >= 8).length} />}
        </>
      )}

      {q?.status === "running" && <Running q={q} now={now} anchors={Math.min(6, layout?.fields.filter((f) => f.off >= 8 && f.off !== q.offset).length ?? 6)} onCancel={() => store.cancelCode(q.key)} />}

      {q?.status === "cancelled" && (
        <div className="mt-2 rounded-md border border-dashed border-line px-2.5 py-2 text-[11.5px] text-fg-2">
          <span className="font-medium text-fg">Stopped waiting.</span> The scan keeps running in Ghidra and is cached when it finishes; this panel will say so. <button type="button" className="underline-offset-2 hover:underline" onClick={() => { store.forgetCode(q.key); run(); }}>Look again</button>
        </div>
      )}

      {q?.status === "error" && q.error && <CodeError q={q} onRetry={() => { store.forgetCode(q.key); run(); }} onRetryLoose={() => { store.forgetCode(q.key); setMinKnown(1); run({ minKnown: 1 }); }} copy={copy} />}

      {q?.status === "done" && q.result && (
        q.result.functions.length === 0
          ? <NoAnchored q={q} minKnown={minKnown} onRetry={(mk) => { store.forgetCode(q.key); setMinKnown(mk); run({ minKnown: mk }); }} />
          : <Results q={q} r={q.result} sel={sel} region={region} bitSel={snap.bitSel} store={store} nameAt={nameAt} copy={copy} host={host} fieldName={fieldName} layout={layout} game={snap.game} onRerun={() => { store.forgetCode(q.key); setShowOpts(true); }} />
      )}
    </section>
  );
}

type OffName =
  | { kind: "field"; name: string; full: string; type: string; size: number; start: number; inside: boolean }
  | { kind: "cand"; name: string; size: number; start: number }
  | { kind: "gap" } | { kind: "past" } | { kind: "outside" };

function cachedHint(snap: Snapshot, structKey: string, offset: number): boolean {
  for (const q of snap.code.values()) if (q.structKey === structKey && q.status === "done" && (q.offset === offset || q.result?.anchors.some((a) => a.offset === offset))) return true;
  return false;
}

// ── States ───────────────────────────────────────────────────────────

function Options({ minKnown, decompile, setMinKnown, setDecompile, anchors }: { minKnown: number; decompile: number; setMinKnown: (n: number) => void; setDecompile: (n: number) => void; anchors?: number }) {
  return (
    <div className="mt-2 grid grid-cols-[auto_minmax(0,1fr)] items-center gap-x-2 gap-y-1.5 rounded-md bg-surface-3/60 px-2.5 py-2 text-[11px]">
      <label className="text-fg-2" htmlFor="code-minknown">minKnown</label>
      <span className="flex items-center gap-2">
        <select id="code-minknown" value={minKnown} onChange={(e) => setMinKnown(Number(e.target.value))} className="h-6 rounded-sm border border-line bg-surface px-1 text-[11px]">
          {[1, 2, 3, 4].map((n) => <option key={n} value={n}>{n}</option>)}
        </select>
        <span className="text-fg-3">known fields the same base register must also touch{anchors !== undefined ? ` (the struct has ${anchors} usable)` : ""}. Lower finds more, with more false positives.</span>
      </span>
      <label className="text-fg-2" htmlFor="code-decompile">decompile</label>
      <span className="flex items-center gap-2">
        <select id="code-decompile" value={decompile} onChange={(e) => setDecompile(Number(e.target.value))} className="h-6 rounded-sm border border-line bg-surface px-1 text-[11px]">
          {[0, 1, 2, 3, 4, 5, 6].map((n) => <option key={n} value={n}>{n}</option>)}
        </select>
        <span className="text-fg-3">functions to decompile around the offset (the bit-testing ones first).</span>
      </span>
    </div>
  );
}

function Running({ q, now, anchors, onCancel }: { q: CodeQuery; now: number; anchors: number; onCancel: () => void }) {
  const elapsed = Math.max(0, now - q.startedAt);
  const phase = scanPhase(elapsed, anchors, q.expectCached);
  const slow = elapsed > 4000 && !q.expectCached;
  return (
    <div className="mt-2">
      <div className="flex items-center gap-2 text-[11.5px]">
        <Icon name="sync" className="spin size-3.5 text-fg-2" />
        <span className="font-medium text-fg">{q.expectCached ? "Reading the cached scan…" : "Scanning the program…"}</span>
        <span className="tnum font-code text-fg-3">{fmtElapsed(elapsed)}</span>
        <button type="button" onClick={onCancel} className="ml-auto rounded-md border border-line px-1.5 py-0.5 text-[11px] text-fg-2 hover:border-line-2 hover:text-fg">Stop waiting</button>
      </div>
      <div className="mt-2 h-1.5 overflow-hidden rounded-sm bg-surface-3" role="progressbar" aria-valuenow={Math.round(phase.pct * 100)} aria-valuemin={0} aria-valuemax={100} aria-label="Estimated progress">
        <div className="m-sampling h-full transition-[width] duration-700 ease-linear" style={{ width: `${Math.max(3, phase.pct * 100)}%`, background: "var(--color-m-cand)" }} />
      </div>
      <p className="mt-1.5 text-[11px] text-fg-2">{phase.label}<span className="text-fg-3"> · estimate; Ghidra reports no progress</span></p>
      {slow && (
        <p className="mt-1 text-[11px] leading-snug text-fg-3">
          Each searched offset is one pass over the whole program (~30 s): the target, then up to {anchors} anchor fields. Nothing to do in game, this reads the Ghidra snapshot. Every later lookup on this struct reuses these passes and answers in a second.
        </p>
      )}
    </div>
  );
}

function fmtElapsed(ms: number): string {
  const s = Math.floor(ms / 1000);
  return s < 60 ? `${s} s` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

function CodeError({ q, onRetry, onRetryLoose, copy }: { q: CodeQuery; onRetry: () => void; onRetryLoose: () => void; copy: (t: string, w: string) => void }) {
  const e = q.error!;
  if (e.kind === "ghidra") {
    const cmd = ghidraCommand(e.message);
    return (
      <div className="mt-2 rounded-md border px-2.5 py-2 text-[11.5px]" style={{ borderColor: mix("warning", 40), background: mix("warning", 6) }}>
        <p className="flex items-center gap-1.5 font-medium text-fg"><Icon name="offline" className="size-3.5 text-warning" />Ghidra isn't running</p>
        <p className="mt-1 leading-snug text-fg-2">Start the headless server from the scaffolding repo, in the background, and wait for “running on port 8089”:</p>
        <div className="mt-1.5 flex items-start gap-1.5 rounded-sm bg-surface-3/70 px-2 py-1.5">
          <code className="code-wrap min-w-0 flex-1 font-code text-[10.5px] leading-snug text-fg">{cmd}</code>
          <IconButton icon="copy" label="Copy command" size="sm" onClick={() => copy(cmd, "command")} className="-my-1 -mr-1" />
        </div>
        <div className="mt-2 flex gap-1.5"><SmallButton icon="sync" onClick={onRetry}>Retry</SmallButton></div>
      </div>
    );
  }
  const title = e.kind === "offline" ? "Server unreachable" : e.kind === "offset0" ? "Offset 0 can't be searched" : e.kind === "anchors" ? "Not enough known fields" : e.kind === "snapshot" ? "No Ghidra snapshot for this exe" : "Lookup failed";
  const hint = e.kind === "offline" ? "The MCP server didn't answer. Retry once it is back."
    : e.kind === "offset0" ? "Displacement 0 is [reg] alone, which every instruction has. Pick a neighbouring field; its code often reads +0 too."
    : e.kind === "anchors" ? "The fingerprint needs known offsets of the same struct (at +8 or above). Open the object through a walker path, or lower minKnown to 1."
    : e.kind === "snapshot" ? "The game was patched since the last analysis. Re-import the exe into Ghidra (tools/ghidra-analyze.ps1), then retry."
    : undefined;
  return (
    <div className="mt-2 rounded-md border border-danger/30 bg-danger-bg px-2.5 py-2 text-[11.5px]">
      <p className="flex items-center gap-1.5 font-medium text-danger"><Icon name="warning" className="size-3.5" />{title}</p>
      <p className="code-wrap mt-1 break-words font-code text-[10.5px] text-fg-2">{e.message}</p>
      {hint && <p className="mt-1 leading-snug text-fg-2">{hint}</p>}
      <div className="mt-2 flex flex-wrap gap-1.5">
        <SmallButton icon="sync" onClick={onRetry}>Retry</SmallButton>
        {e.kind === "anchors" && <SmallButton onClick={onRetryLoose}>Retry with minKnown 1</SmallButton>}
      </div>
    </div>
  );
}

function NoAnchored({ q, minKnown, onRetry }: { q: CodeQuery; minKnown: number; onRetry: (minKnown: number) => void }) {
  const r = q.result!;
  return (
    <div className="mt-2 rounded-md border border-dashed border-line px-2.5 py-2 text-[11.5px]">
      <p className="font-medium text-fg">No function of this struct reaches {r.target.hex}</p>
      <p className="mt-1 leading-snug text-fg-2">
        {r.programWideAccesses} instruction{r.programWideAccesses === 1 ? "" : "s"} program-wide use the displacement, but none through a base register that also touches {r.minKnown} of the anchor fields ({r.anchors.filter((a) => !a.skipped).map((a) => `+${a.offset} ${a.field}`).join(", ")}).
      </p>
      <ul className="mt-1.5 list-disc space-y-0.5 pl-4 text-[11px] text-fg-2">
        <li>Lower <span className="font-code">minKnown</span> to 1: more hits, more false positives.</li>
        <li>The code may address the struct through a different base (an inlined copy, a vector element by stride): try a field the game surely touches often, such as the nearest pointer or count.</li>
        <li>Pass <span className="font-code">knownOffsets</span> yourself when the HUD maps too few neighbours.</li>
      </ul>
      <div className="mt-2 flex flex-wrap gap-1.5">
        {minKnown > 1 && <SmallButton icon="search" tone="primary" onClick={() => onRetry(1)}>Retry with minKnown 1</SmallButton>}
        <SmallButton icon="sync" onClick={() => onRetry(minKnown)}>Retry</SmallButton>
      </div>
    </div>
  );
}

// ── Results ──────────────────────────────────────────────────────────

function Results({ q, r, sel, region, bitSel, store, nameAt, copy, host, fieldName, layout, game, onRerun }: {
  q: CodeQuery; r: FieldAccessResult; sel: Selection; region: Region; bitSel?: number; store: MemoryStore; nameAt: (off: number) => OffName;
  copy: (t: string, w: string) => void; host: CodeHost; fieldName?: string; layout?: LayoutResult; game?: string; onRerun: () => void;
}) {
  const tbit = r.target.bit ?? undefined;
  const fns = useMemo(() => rankFunctions(r, tbit), [r, tbit]);
  const gates = useMemo(() => gatesOf(r), [r]);
  const [showAll, setShowAll] = useState(false);
  const [open, setOpen] = useState<ReadonlySet<string>>(() => new Set(fns.filter((f) => f.dec).slice(0, 2).map((f) => f.fn.function)));
  const shown = showAll ? fns : fns.slice(0, SHOW_FNS);
  const widths = widthsAtTarget(r.accesses.filter((a) => a.confidence !== "low"));
  const hudSize = segAt(region, r.target.offset)?.field?.size;
  const wider = hudSize !== undefined ? widths.filter((w) => w.width > hudSize) : [];
  const byteDelta = r.target.offset - sel.off;
  const toggle = (fn: string) => setOpen((s) => { const n = new Set(s); if (n.has(fn)) n.delete(fn); else n.add(fn); return n; });
  const bitTone = (bit: number): Tone => (tbit === bit ? "warning" : "none");
  const onBit = (bit: number) => store.pickBit(byteDelta * 8 + bit);
  const onHover = (bits?: number[]) => store.hoverBits(bits?.map((b) => byteDelta * 8 + b));

  const ask = host.ask ? () => host.ask!(askText(r, fns, gates, fieldName, layout, game, nameAt)) : undefined;
  const send = host.send ? () => host.send!(...contextOf(r, fns, gates, fieldName, nameAt)) : undefined;

  return (
    <div className="mt-2">
      {/* Summary */}
      <div className="flex flex-wrap items-center gap-1.5 text-[10.5px]">
        <Chip tone="plain"><span className="font-semibold text-fg">{r.functions.length}</span> function{r.functions.length === 1 ? "" : "s"} of {r.struct ? leafType(r.struct) : "the struct"}</Chip>
        {tbit !== undefined && fns.some((f) => f.fn.bitMatch) && <Chip tone="warning">{fns.filter((f) => f.fn.bitMatch).length} test bit {tbit}</Chip>}
        <Chip tone="plain" title="Instructions anywhere in the program that use this displacement, on any base">{r.programWideAccesses} program-wide</Chip>
        {gates.length > 0 && <Chip tone="cand">{gates.length} gated field{gates.length === 1 ? "" : "s"}</Chip>}
        <span className="ml-auto tnum text-fg-3" title={`${r.program} · ${q.finishedAt && q.startedAt ? `${((q.finishedAt - q.startedAt) / 1000).toFixed(1)} s` : ""}`}>{q.finishedAt && q.startedAt ? (q.finishedAt - q.startedAt < 3000 ? "from cache" : `${Math.round((q.finishedAt - q.startedAt) / 1000)} s scan`) : ""}</span>
      </div>
      {widths.length > 0 && (
        <p className="mt-1.5 text-[11px] leading-snug text-fg-2">
          Accessed as {widths.map((w, i) => <span key={w.width}>{i > 0 && ", "}<span className="font-code text-fg">{w.width} B</span> ×{w.count}</span>)}
          {hudSize !== undefined && <span className="text-fg-3"> · the HUD maps {fmtBytes(hudSize)}</span>}
          {wider.length > 0 && (
            <span className="block text-m-cand">The code also uses {wider.map((w) => `${w.width} B`).join(" and ")} here: the field may be wider than the HUD's, covering {Array.from({ length: wider[0].width - hudSize! }, (_, i) => hexOff(r.target.offset + hudSize! + i)).join(", ")}.</span>
          )}
        </p>
      )}

      {/* Explained by the code */}
      {gates.length > 0 && <Gates gates={gates} r={r} nameAt={nameAt} store={store} bitTone={bitTone} onBit={onBit} onHover={onHover} />}

      {/* Functions */}
      <SectionLabel className="mt-3" right={<span>{tbit !== undefined ? `bit ${tbit} first · ` : ""}decompiled first · by known fields touched</span>}><Icon name="list" className="size-3" />Functions</SectionLabel>
      <ul className="mt-1.5 divide-y divide-line overflow-hidden rounded-md border border-line">
        {shown.map((f) => (
          <FnRow key={f.fn.function} f={f} r={r} open={open.has(f.fn.function)} onToggle={() => toggle(f.fn.function)} copy={copy} nameAt={nameAt} store={store} bitSel={bitSel} byteDelta={byteDelta} bitTone={bitTone} onBit={onBit} onHover={onHover} />
        ))}
      </ul>
      {fns.length > SHOW_FNS && (
        <button type="button" onClick={() => setShowAll(!showAll)} className="mt-1.5 text-[11px] text-fg-2 underline-offset-2 hover:text-fg hover:underline">
          {showAll ? `Show the top ${SHOW_FNS}` : `Show all ${fns.length} functions`}
        </button>
      )}
      {r.unanchored && <p className="mt-1.5 text-[10.5px] leading-snug text-fg-3">{r.unanchored}.</p>}

      {/* Actions */}
      <div className="mt-2.5 flex flex-wrap gap-1.5">
        {ask && <SmallButton icon="sparkle" tone="primary" onClick={ask} title="Ask in the conversation, with the top function's pseudocode and the gates found">Ask Claude to explain this field</SmallButton>}
        {send && <SmallButton icon="send" onClick={send} title="Put the functions, instructions and excerpts into Claude's context">Send to Claude</SmallButton>}
        <SmallButton icon="sliders" onClick={onRerun} title="Forget this result and look again with other options (the server answers from its cache)">Look again…</SmallButton>
      </div>
    </div>
  );
}

function leafType(t: string): string { const i = t.lastIndexOf("."); return i >= 0 ? t.slice(i + 1) : t; }

function Chip({ tone, children, title }: { tone: "plain" | "cand" | "warning" | "change"; children: ReactNode; title?: string }) {
  const cls = tone === "plain" ? "bg-surface-3 text-fg-2" : tone === "cand" ? "text-m-cand" : tone === "warning" ? "text-warning" : "text-m-change";
  return <span className={`tnum rounded-sm px-1.5 py-0.5 font-medium ${cls}`} style={{ background: tone === "plain" ? undefined : mix(tone, 14) }} title={title}>{children}</span>;
}

// ── Seen in code (cross-references from other lookups) ───────────────

function SeenHere({ marks, sel, nameAt, store }: { marks: CodeMark[]; sel: Selection; nameAt: (off: number) => OffName; store: MemoryStore }) {
  return (
    <div className="mt-2 rounded-md border px-2.5 py-2" style={{ borderColor: mix("cand", 40), background: mix("cand", 6) }}>
      <SectionLabel><Icon name="code" className="size-3 text-m-cand" />Seen in code</SectionLabel>
      <ul className="mt-1 space-y-1 text-[11px]">
        {marks.map((m) => {
          const n = nameAt(m.off);
          return (
            <li key={m.off} className="leading-snug">
              <span className="tnum font-code text-fg-2">{m.off === sel.off && sel.size === markSize(m) ? "" : `+${hexOff(m.off)} `}</span>
              {m.fns.length ? <span className="font-code text-fg">{m.fns.slice(0, 2).join(", ")}{m.fns.length > 2 ? ` +${m.fns.length - 2}` : ""}</span> : <span className="text-fg">code</span>}
              <span className="text-fg-2"> {m.widths.length ? `${m.fns.length === 1 ? "handles" : "handle"} ${m.widths.map((w) => `${w} B`).join(" / ")} here` : m.fns.length === 1 ? "touches these bytes" : "touch these bytes"}</span>
              {m.gatedBy.map((g) => (
                <span key={`${g.fromOff}:${g.bits.join()}`} className="text-fg-2"> when <button type="button" className="font-code text-warning underline-offset-2 hover:underline" onClick={() => { store.selectBytes(g.fromOff, 1); store.pickBit(g.bits[0]); }} title="Select that bit">{nameAt(g.fromOff).kind === "field" ? (nameAt(g.fromOff) as { name: string }).name : `+${hexOff(g.fromOff)}`} bit {g.bits.join("+")}</button> is {g.when}</span>
              ))}
              {n.kind === "gap" && <span className="ml-1 rounded-sm px-1 text-[9.5px] font-semibold uppercase text-m-cand" style={{ background: mix("cand", 14) }}>unmapped</span>}
              <span className="text-fg-3"> · from the +{hexOff(m.fromTargets[0])} lookup</span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

// ── Gates: "bit 6 set → +0x3F Affinity (4 B)" ────────────────────────

function Gates({ gates, r, nameAt, store, bitTone, onBit, onHover }: { gates: (Gate & { fns: string[] })[]; r: FieldAccessResult; nameAt: (off: number) => OffName; store: MemoryStore; bitTone: (b: number) => Tone; onBit: (b: number) => void; onHover: (bits?: number[]) => void }) {
  return (
    <div className="mt-2.5 rounded-md border px-2.5 py-2" style={{ borderColor: mix("cand", 40), background: mix("cand", 6) }}>
      <SectionLabel right={<span title="Pattern-matched in the decompiled excerpts: an if on this field's bits, and the struct offsets accessed inside the block">heuristic</span>}><Icon name="sparkle" className="size-3 text-m-cand" />Explained by the code</SectionLabel>
      <p className="mt-0.5 text-[10.5px] text-fg-3">Bits of {r.target.hex} that gate other fields in the functions above: a flag that says "this field is present".</p>
      <ul className="mt-1.5 space-y-1">
        {gates.map((g) => {
          const n = nameAt(g.off);
          const size = g.width ?? (n.kind === "field" ? n.size : 1);
          return (
            <li key={`${g.bits.join()}:${g.off}`} className="flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[11px]">
              <span className="flex items-center gap-0.5">
                {g.bits.map((b) => <BitChip key={b} bit={b} tone={bitTone(b)} onClick={() => onBit(b)} onHover={(h) => onHover(h ? [b] : undefined)} />)}
                <span className="text-fg-3">{g.when === "set" ? "set" : "clear"}</span>
              </span>
              <span className="text-fg-3">→</span>
              <OffsetChip off={g.off} width={size} n={n} onSelect={() => store.selectBytes(g.off, size)} />
              <span className="text-fg-2">{g.kind === "zeroed" ? "zeroed when clear" : /serial/.test(roleText(r, g.fns)) ? "sent / received" : g.kind === "io" ? "handed to a helper" : g.kind === "write" ? "written" : "read"}{g.width ? ` (${g.width} B)` : ""}</span>
              <span className="ml-auto font-code text-[10px] text-fg-3" title={g.fns.join(", ")}>{g.fns.length === 1 ? g.fns[0] : `${g.fns.length} functions`}</span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

function roleText(r: FieldAccessResult, fns: string[]): string {
  return fns.map((f) => r.decompiled.find((d) => d.function === f)?.excerpt ?? "").some((e) => /\b(htons|ntohs)\(/.test(e)) ? "serializer" : "";
}

function BitChip({ bit, tone, onClick, onHover, matches }: { bit: number; tone: Tone; onClick?: () => void; onHover?: (h: boolean) => void; matches?: boolean }) {
  return (
    <button type="button" onClick={onClick} onMouseEnter={() => onHover?.(true)} onMouseLeave={() => onHover?.(false)} onFocus={() => onHover?.(true)} onBlur={() => onHover?.(false)}
      className={`tnum inline-flex h-4 items-center rounded-sm px-1 font-code text-[10px] font-semibold ${matches || tone === "warning" ? "text-warning" : "text-fg-2"} ${onClick ? "hover:brightness-110" : ""}`}
      style={{ background: matches || tone === "warning" ? mix("warning", 16) : "var(--color-surface-3)" }}
      title={`bit ${bit} = 0x${(1 << bit).toString(16).toUpperCase()} · click to pick it in the bit grid`}>
      b{bit}
    </button>
  );
}

/** An offset the code touches, named when the HUD maps it, flagged when it doesn't. */
function OffsetChip({ off, width, n, onSelect, inline }: { off: number; width?: number; n: OffName; onSelect: () => void; inline?: boolean }) {
  const unmapped = n.kind === "gap" || n.kind === "past";
  const label = n.kind === "field" ? `${n.name}${n.inside ? `+${off - n.start}` : ""}` : n.kind === "cand" ? `${n.name} candidate` : n.kind === "past" ? "past the struct end" : n.kind === "outside" ? "outside the read" : "unmapped";
  const title = n.kind === "field" ? `${n.full} (${n.type}, ${fmtBytes(n.size)}) at +${hexOff(n.start)} · click to select` : unmapped ? `+${hexOff(off)}: the HUD's struct has no field here, but this code uses it · click to select the bytes` : `+${hexOff(off)} · click to select`;
  return (
    <button type="button" onClick={onSelect} title={title}
      className={`inline-flex max-w-full items-center gap-1 rounded-sm px-1 font-code ${inline ? "text-[10px] align-[1px]" : "text-[10.5px]"} ${unmapped ? "border border-dashed text-m-cand" : n.kind === "field" ? "text-m-field" : "text-fg-2"} hover:brightness-110`}
      style={{ background: unmapped ? mix("cand", 8) : n.kind === "field" ? mix("field", 12) : "var(--color-surface-3)", borderColor: unmapped ? mix("cand", 55) : undefined }}>
      {!inline && <span className="tnum">+{hexOff(off)}</span>}
      <span className={`truncate ${unmapped ? "font-semibold" : ""}`}>{unmapped && !inline ? `${label}: the code uses it` : label}</span>
      {width !== undefined && n.kind !== "field" && !inline && <span className="tnum opacity-70">{width} B</span>}
    </button>
  );
}

// ── One function: header, instructions, excerpt ──────────────────────

function FnRow({ f, r, open, onToggle, copy, nameAt, store, bitSel, byteDelta, bitTone, onBit, onHover }: {
  f: FnView; r: FieldAccessResult; open: boolean; onToggle: () => void; copy: (t: string, w: string) => void; nameAt: (off: number) => OffName; store: MemoryStore;
  bitSel?: number; byteDelta: number; bitTone: (b: number) => Tone; onBit: (b: number) => void; onHover: (bits?: number[]) => void;
}) {
  const addr = fnAddress(f.fn.function);
  const [showCode, setShowCode] = useState(true);
  const model = useMemo(() => (f.dec?.excerpt ? parseExcerpt(f.dec, r.target.offset) : undefined), [f.dec, r.target.offset]);
  const matchesSel = bitSel !== undefined && f.accesses.some((a) => a.bits?.includes(bitSel - byteDelta * 8));
  return (
    <li className={matchesSel ? "bg-warning/5" : ""}>
      <div className="flex items-center gap-1.5 px-2 py-1.5">
        <button type="button" onClick={onToggle} aria-expanded={open} className="flex min-w-0 flex-1 items-center gap-1.5 text-left">
          <Icon name="chevronRight" className={`size-3 shrink-0 text-fg-3 transition-transform ${open ? "rotate-90" : ""}`} />
          <span className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-0.5">
            <span className={`font-code text-[11.5px] font-semibold ${f.fn.function === "?" ? "text-fg-3" : "text-fg"}`}>{f.fn.function === "?" ? "no function here" : f.fn.function}</span>
            {f.role && <span className="text-[10.5px] text-fg-2" title="A guess from the pseudocode and the instruction mix">{roleLabel(f.role).prefix}<span className="font-medium text-fg">{f.role}</span></span>}
            <span className={`tnum rounded-sm px-1 text-[9.5px] font-semibold uppercase ${TONE_TEXT[CONF_TONE[f.confidence]]}`} style={{ background: mix(CONF_TONE[f.confidence], 14) || "var(--color-surface-3)" }} title={`${f.fn.knownFields} of the anchor fields touched through the same base register`}>{f.confidence}</span>
            {f.fn.bitMatch && r.target.bit !== undefined && r.target.bit !== null && <span className="rounded-sm px-1 text-[9.5px] font-semibold text-warning" style={{ background: mix("warning", 16) }}>tests bit {r.target.bit}</span>}
          </span>
        </button>
        {addr && <IconButton icon="copy" label={`Copy Ghidra address ${addr}`} size="sm" onClick={() => copy(addr, "Ghidra address")} className="-my-1" />}
      </div>
      {!open && (
        <div className="flex flex-wrap items-center gap-1 px-2 pb-1.5 pl-6 text-[10px]">
          {f.kinds.map((k) => <KindChip key={k} kind={k} />)}
          <span className="tnum text-fg-3">· {f.fn.accesses} instruction{f.fn.accesses === 1 ? "" : "s"}</span>
          {f.also.length > 0 && <span className="truncate text-fg-3" title={f.also.join(", ")}>· also {f.also.map((k) => k.replace(/^\+\d+\s*/, "")).filter(Boolean).join(", ")}</span>}
        </div>
      )}
      {open && (
        <div className="px-2 pb-2 pl-6">
          {f.also.length > 0 && <p className="text-[10.5px] text-fg-3">Same base register also touches {f.also.map((k, i) => <span key={k}>{i > 0 && ", "}<span className="font-code text-fg-2">{k}</span></span>)}</p>}
          <ul className="mt-1.5 space-y-0.5">
            {f.accesses.map((a) => <AccessRow key={a.address} a={a} r={r} copy={copy} bitTone={bitTone} onBit={onBit} onHover={onHover} nameAt={nameAt} />)}
          </ul>
          {f.dec && (
            <div className="mt-2">
              <button type="button" onClick={() => setShowCode(!showCode)} aria-expanded={showCode} className="flex items-center gap-1 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3 hover:text-fg">
                <Icon name="chevron" className={`size-3 transition-transform ${showCode ? "rotate-180" : ""}`} />Decompiled
                {f.dec.lineCount !== undefined && <span className="font-normal normal-case tracking-normal">· {model?.lines.filter((l) => !l.gap).length ?? 0} of {f.dec.lineCount} lines, around {r.target.hex}</span>}
              </button>
              {showCode && (f.dec.error ? <p className="mt-1 text-[11px] text-danger">{f.dec.error}</p> : model && <Excerpt model={model} dec={f.dec} nameAt={nameAt} store={store} onBit={onBit} onHover={onHover} />)}
            </div>
          )}
          {!f.dec && <p className="mt-1.5 text-[10.5px] text-fg-3">Not decompiled (raise <span className="font-code">decompile</span> in the options, or open {addr ?? "it"} in Ghidra).</p>}
        </div>
      )}
    </li>
  );
}

function KindChip({ kind }: { kind: string }) {
  const tone = ACCESS_TONE[kind] ?? "none";
  return <span className={`rounded-sm px-1 text-[9.5px] font-semibold ${tone === "none" ? "text-fg-2" : TONE_TEXT[tone]}`} style={{ background: tone === "none" ? "var(--color-surface-3)" : mix(tone, 14) }}>{KIND_LABEL[kind as keyof typeof KIND_LABEL] ?? kind}</span>;
}

function AccessRow({ a, r, copy, bitTone, onBit, onHover, nameAt }: { a: Access; r: FieldAccessResult; copy: (t: string, w: string) => void; bitTone: (b: number) => Tone; onBit: (b: number) => void; onHover: (bits?: number[]) => void; nameAt: (off: number) => OffName }) {
  const tone = ACCESS_TONE[a.kind] ?? "none";
  const addr = "0x" + a.address;
  // Highlight the target displacement in the instruction text.
  const re = new RegExp(`(\\+ 0x${r.target.offset.toString(16)})\\b`, "i");
  const parts = a.instruction.split(re);
  const hudSize = nameAt(r.target.offset);
  return (
    <li className={`grid grid-cols-[3px_minmax(0,1fr)_auto] items-center gap-x-2 rounded-sm px-1 py-0.5 ${a.matchesBit ? "bg-warning/8" : ""}`} onMouseEnter={() => a.bits && onHover(a.bits)} onMouseLeave={() => onHover(undefined)}>
      <span className="h-[70%] rounded-xs" style={{ background: tone === "none" ? "var(--color-fg-3)" : mix(tone, 85) }} aria-hidden />
      <span className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-0.5">
        <code className="code-wrap font-code text-[10.5px] text-fg">{parts.map((p, i) => re.test(p) ? <mark key={i} className="rounded-xs bg-transparent px-px font-semibold text-warning ring-1 ring-inset" style={{ ["--tw-ring-color" as string]: mix("warning", 55) }}>{p}</mark> : p)}</code>
        <KindChip kind={a.kind} />
        {a.width > 0 && <span className={`tnum rounded-sm px-1 font-code text-[9.5px] ${hudSize.kind === "field" && a.width > hudSize.size ? "text-m-cand" : "text-fg-3"}`} style={{ background: hudSize.kind === "field" && a.width > hudSize.size ? mix("cand", 12) : "var(--color-surface-3)" }} title={hudSize.kind === "field" && a.width > hudSize.size ? `Wider than the HUD's ${fmtBytes(hudSize.size)} field` : "Access width"}>{a.width} B</span>}
        {a.bits && a.bits.length > 0 && <span className="flex items-center gap-0.5">{a.bits.map((b) => <BitChip key={b} bit={b} tone={bitTone(b)} matches={!!a.matchesBit && b === r.target.bit} onClick={() => onBit(b)} />)}</span>}
      </span>
      <span className="flex items-center gap-0.5">
        <span className="tnum hidden font-code text-[9.5px] text-fg-3 sm:inline" title="Instruction address (Ghidra)">{a.address}</span>
        <IconButton icon="copy" label={`Copy ${addr}`} size="sm" onClick={() => copy(addr, "address")} className="-my-1" />
      </span>
    </li>
  );
}

// ── The excerpt, annotated ───────────────────────────────────────────

function Excerpt({ model, dec, nameAt, store, onBit, onHover }: { model: ExcerptModel; dec: { signature?: string }; nameAt: (off: number) => OffName; store: MemoryStore; onBit: (b: number) => void; onHover: (bits?: number[]) => void }) {
  const unmapped = [...new Set(model.refs.filter((r) => { const n = nameAt(r.off); return n.kind === "gap" || n.kind === "past"; }).map((r) => r.off))].sort((a, b) => a - b);
  return (
    <div className="mt-1 overflow-hidden rounded-md border border-line bg-surface-3/40">
      {dec.signature && <div className="code-wrap border-b border-line px-2 py-1 font-code text-[10px] text-fg-3">{dec.signature}</div>}
      <pre className="scroll-thin max-h-[22rem] overflow-auto px-1 py-1 font-code text-[10.5px] leading-[1.5]">
        {model.lines.map((l, i) => (
          <div key={i} className={`flex min-w-max ${l.gap ? "text-fg-3/70" : ""} ${l.hasTarget ? "bg-warning/6 shadow-[inset_2px_0_0_color-mix(in_oklab,var(--color-warning)_60%,transparent)]" : ""}`}>
            <span className="w-1 shrink-0" />
            <span className="whitespace-pre">{l.tokens.map((t, j) => <Tok key={j} t={t} nameAt={nameAt} store={store} onBit={onBit} onHover={onHover} />)}</span>
          </div>
        ))}
      </pre>
      <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 border-t border-line px-2 py-1 text-[10px] text-fg-3">
        <span>base {model.bases.map((b) => <span key={b} className="font-code text-fg-2">{b}</span>).reduce<ReactNode[]>((acc, x, i) => (i ? [...acc, ", ", x] : [x]), [])}</span>
        <span>· <span className="text-warning">target</span> · <span className="text-m-field">HUD field</span> · <span className="text-m-cand">unmapped</span> · <span className="text-fg-3/70">other objects dimmed</span></span>
        {unmapped.length > 0 && <span className="ml-auto text-m-cand">{unmapped.length} unmapped offset{unmapped.length === 1 ? "" : "s"} used here</span>}
      </div>
    </div>
  );
}

function Tok({ t, nameAt, store, onBit, onHover }: { t: Token; nameAt: (off: number) => OffName; store: MemoryStore; onBit: (b: number) => void; onHover: (bits?: number[]) => void }) {
  switch (t.t) {
    case "text": return <>{t.s}</>;
    case "other": return <span className="text-fg-3/70" title="Through a variable that isn't this struct">{t.s}</span>;
    case "fn": return <span className="text-fg-2">{t.s}</span>;
    case "mask": return <button type="button" onClick={() => onBit(t.bits[0])} onMouseEnter={() => onHover(t.bits)} onMouseLeave={() => onHover(undefined)} className="rounded-xs px-px font-semibold text-warning hover:brightness-110" style={{ background: mix("warning", 14) }} title={`mask ${t.s} = bit${t.bits.length === 1 ? "" : "s"} ${t.bits.join(", ")} · click to pick in the bit grid`}>{t.s}</button>;
    case "off": {
      const n = nameAt(t.off);
      if (t.target) return <mark className="rounded-xs bg-transparent px-px font-semibold text-warning ring-1 ring-inset" style={{ ["--tw-ring-color" as string]: mix("warning", 55) }} title={`the target +${hexOff(t.off)}`}>{t.s}</mark>;
      const unmapped = n.kind === "gap" || n.kind === "past";
      const width = t.width ?? (n.kind === "field" ? n.size : 1);
      return (
        <span className="inline-flex items-baseline gap-0.5">
          <span className={unmapped ? "text-m-cand" : n.kind === "field" ? "text-m-field" : ""}>{t.s}</span>
          <OffsetChip off={t.off} width={t.width} n={n} inline onSelect={() => store.selectBytes(n.kind === "field" ? n.start : t.off, n.kind === "field" ? n.size : width)} />
        </span>
      );
    }
  }
}

// ── Claude ───────────────────────────────────────────────────────────

function gateLine(g: Gate & { fns: string[] }, nameAt: (off: number) => OffName): string {
  const n = nameAt(g.off);
  const name = n.kind === "field" ? `${n.name}${n.inside ? `+${g.off - n.start}` : ""}` : n.kind === "cand" ? `${n.name} candidate` : "unmapped";
  return `bit ${g.bits.join("+")} ${g.when} -> +${hexOff(g.off)} ${name}${g.width ? ` (${g.width} B)` : ""} ${g.kind === "io" ? "handed to a helper" : g.kind}`;
}

function askText(r: FieldAccessResult, fns: FnView[], gates: (Gate & { fns: string[] })[], fieldName: string | undefined, layout: LayoutResult | undefined, game: string | undefined, nameAt: (off: number) => OffName): string {
  const g = game === "poe2" ? "PoE2" : game === "poe1" ? "PoE1" : "PoE";
  const struct = r.struct ? leafType(r.struct) : layout ? leafType(layout.struct) : "this struct";
  const top = fns.filter((f) => f.dec?.excerpt).slice(0, 2);
  const what = fieldName ? `field ${fieldName} (+${r.target.offset}, ${r.target.hex}${r.target.bit !== undefined ? `, bit ${r.target.bit}` : ""})` : `the unmapped bytes at +${r.target.offset} (${r.target.hex}${r.target.bit !== undefined ? `, bit ${r.target.bit}` : ""})`;
  const lines = [`Explain ${what} of ${struct} (${g}) from the code that uses it (find_field_access, static Ghidra analysis of ${r.program}).`];
  lines.push(`${r.functions.length} functions of the struct access it; top: ${fns.slice(0, 4).map((f) => `${f.fn.function}${f.role ? ` (${roleLabel(f.role).prefix}${f.role})` : ""} [${f.kinds.join(", ")}; also ${f.also.join(", ") || "-"}]`).join("; ")}.`);
  if (gates.length) lines.push(`Gating found by pattern in the excerpts: ${gates.map((x) => gateLine(x, nameAt)).join("; ")}.`);
  for (const f of top) lines.push(`\n${f.fn.function}${f.role ? ` (looks like a ${f.role})` : ""}: ${f.dec!.signature ?? ""}\n\`\`\`c\n${clip(f.dec!.excerpt!, 1800)}\n\`\`\``);
  const unm = [...new Set(gates.filter((x) => { const n = nameAt(x.off); return n.kind === "gap" || n.kind === "past"; }).map((x) => `+${hexOff(x.off)}`))];
  lines.push(`\nWhat does ${fieldName ? "each bit of " + fieldName : "this field"} mean${unm.length ? `, and what are the unmapped ${unm.join(", ")} the code reads` : ""}? Name the field(s) as the HUD struct should map them. Short answer.`);
  return lines.join("\n");
}

function contextOf(r: FieldAccessResult, fns: FnView[], gates: (Gate & { fns: string[] })[], fieldName: string | undefined, nameAt: (off: number) => OffName): [string, Record<string, unknown>] {
  const text = `Code accessing ${r.struct ?? "struct"} +${r.target.offset} (${r.target.hex}${r.target.bit !== undefined ? ` bit ${r.target.bit}` : ""})${fieldName ? ` = ${fieldName}` : ""} in ${r.program}: ${r.functions.length} functions (${r.programWideAccesses} program-wide). `
    + `Top: ${fns.slice(0, 5).map((f) => `${f.fn.function}${f.role ? ` (${f.role})` : ""} ${f.confidence}, ${f.accesses.map((a) => `${a.kind}${a.bits ? ` bits ${a.bits.join(",")}` : ""} @${a.address}`).join(", ")}`).join("; ")}. `
    + (gates.length ? `Gates: ${gates.map((x) => gateLine(x, nameAt)).join("; ")}. ` : "")
    + fns.filter((f) => f.dec?.excerpt).slice(0, 3).map((f) => `\n${f.fn.function}: ${f.dec!.signature ?? ""}\n${clip(f.dec!.excerpt!, 1500)}`).join("\n");
  return [text, { tool: "find_field_access", target: r.target, struct: r.struct, program: r.program, functions: fns.slice(0, 8).map((f) => ({ function: f.fn.function, role: f.role, confidence: f.confidence, kinds: f.kinds, also: f.also, accesses: f.accesses })), gates: gates.map((x) => ({ bits: x.bits, when: x.when, off: x.off, width: x.width, kind: x.kind, fns: x.fns })), decompiled: r.decompiled.slice(0, 3) }];
}

function clip(s: string, max: number): string {
  const t = s.replace(/\r/g, "");
  return t.length <= max ? t : t.slice(0, max) + "\n  ...";
}
