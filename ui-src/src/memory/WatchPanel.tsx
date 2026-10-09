import { useState } from "react";
import { IconButton, SectionLabel } from "../components";
import { Icon } from "../icons";
import { SmallButton } from "../explorer/Tree";
import { hexOff, type Region } from "./bytes";
import { mix } from "./paint";
import { WATCH_MS, type MemoryStore, type Snapshot, type View } from "./store";
import type { ChangedRange } from "./types";

const DURATIONS = [5, 15, 60];

/** Start a watch, see it sample, then the changed ranges: unmapped changes and bit flips first (the discovery moment). */
export function WatchPanel({ store, snap, view, region, now, variant }: { store: MemoryStore; snap: Snapshot; view: View; region: Region; now: number; variant: "card" | "panel" }) {
  const [secs, setSecs] = useState(WATCH_MS / 1000);
  const [hideNoisy, setHideNoisy] = useState(false);
  const watch = snap.watch?.viewId === view.id ? snap.watch : undefined;
  const running = snap.watch?.status === "running";
  const ranges = watch?.result?.changedRanges ?? [];
  const shown = sortRanges(ranges).filter((c) => !hideNoisy || !c.noisy);
  const noisyCount = ranges.filter((c) => c.noisy).length;
  const unmappedCount = ranges.filter((c) => c.field === "(unmapped)").length;
  const bitCount = ranges.filter((c) => c.bitsFlipped?.length).length;
  const panel = variant === "panel" ? "rounded-lg border border-line bg-surface-2 p-3" : "rounded-lg border border-line bg-surface p-3";
  const pct = watch?.status === "running" ? Math.min(1, (now - watch.startedAt) / watch.durationMs) : 0;

  return (
    <section aria-label="Watch for changes" className={`${panel} text-xs`} aria-live="polite">
      <SectionLabel right={watch && watch.status !== "running" && <IconButton icon="x" label="Clear watch results" size="sm" onClick={() => store.clearWatch()} className="-my-1" />}>
        <Icon name="diff" className="size-3" />Watch
        {watch?.status === "running" && <span className="font-normal normal-case tracking-normal">· sampling, {Math.max(0, Math.ceil((watch.durationMs - (now - watch.startedAt)) / 1000))} s left</span>}
        {watch?.status === "done" && watch.result && <span className="tnum font-normal normal-case tracking-normal">· {watch.result.samples} samples in {Math.round(watch.result.durationMs / 1000)} s</span>}
      </SectionLabel>

      {!watch && (
        <p className="mt-1 text-[11.5px] leading-snug text-fg-2">
          Samples these {region.size} bytes every 100 ms and reports what moved, byte by byte, with the bits that flipped. Start it, then do <em>one</em> thing in game (toggle a stash tab's affinity, swap weapons, take a hit).
        </p>
      )}

      <div className="mt-2 flex flex-wrap items-center gap-1.5">
        <SmallButton icon={running ? "sync" : "play"} tone="primary" disabled={running || view.loading} onClick={() => void store.watch(secs * 1000)} title={`watch_memory for ${secs} s at 100 ms`}>
          {running ? "Watching…" : watch ? `Watch again · ${secs} s` : `Watch ${secs} s`}
        </SmallButton>
        <div className="flex overflow-hidden rounded-md border border-line" role="radiogroup" aria-label="Watch duration">
          {DURATIONS.map((d) => (
            <button key={d} type="button" role="radio" aria-checked={secs === d} disabled={running} onClick={() => setSecs(d)}
              className={`tnum h-6 px-1.5 text-[11px] ${secs === d ? "bg-fg text-surface" : "text-fg-2 hover:bg-surface-3 hover:text-fg"} disabled:opacity-50`}>{d} s</button>
          ))}
        </div>
        {noisyCount > 0 && (
          <label className="ml-auto flex items-center gap-1 text-[11px] text-fg-2"><input type="checkbox" checked={hideNoisy} onChange={(e) => setHideNoisy(e.target.checked)} className="size-3" />hide noisy ({noisyCount})</label>
        )}
      </div>

      {watch?.status === "running" && (
        <div className="mt-2">
          <div className="h-1.5 overflow-hidden rounded-sm bg-surface-3" role="progressbar" aria-valuenow={Math.round(pct * 100)} aria-valuemin={0} aria-valuemax={100}>
            <div className="m-sampling h-full transition-[width] duration-500 ease-linear" style={{ width: `${pct * 100}%`, background: "var(--color-m-change)" }} />
          </div>
          <p className="mt-1.5 text-[11.5px] text-fg-2">Now do the thing in game. Changed bytes will light up in the map and the hex view when the watch ends.</p>
        </div>
      )}

      {watch?.status === "error" && <p className="code-wrap mt-2 text-danger"><Icon name="warning" className="mr-1 inline size-3.5 align-[-2px]" />{watch.error}</p>}

      {watch?.status === "done" && ranges.length === 0 && (
        <div className="mt-2 rounded-md border border-dashed border-line px-2.5 py-2 text-[11.5px] text-fg-2">
          <span className="font-medium text-fg">Nothing changed</span> in {Math.round(watch.result!.durationMs / 1000)} s. {watch.result?.note ?? "Do something in game while it runs, or watch for longer."}
          {region.structSize !== undefined && <span className="block text-fg-3">The state you are after may live past the struct end: raise <em>extend</em> in the target options.</span>}
        </div>
      )}

      {watch?.status === "done" && ranges.length > 0 && (
        <>
          <div className="mt-2 flex flex-wrap gap-1.5 text-[10.5px]">
            <Chip tone="plain">{ranges.length} range{ranges.length === 1 ? "" : "s"} changed</Chip>
            {unmappedCount > 0 && <Chip tone="cand">{unmappedCount} unmapped</Chip>}
            {bitCount > 0 && <Chip tone="change">{bitCount} with bit flips</Chip>}
            {noisyCount > 0 && <Chip tone="warning">{noisyCount} noisy</Chip>}
          </div>
          <ul className="mt-2 divide-y divide-line overflow-hidden rounded-md border border-line">
            {shown.map((c) => <RangeRow key={c.off} c={c} region={region} selected={!!snap.selection && snap.selection.off <= c.off && snap.selection.off + snap.selection.size >= c.off + c.size} onSelect={() => store.selectRange(c)} />)}
          </ul>
          {shown.length === 0 && <p className="mt-1.5 text-[11px] text-fg-3">Only noisy ranges changed (timers, positions).</p>}
          {watch.result?.note && <p className="mt-1.5 text-[11px] text-fg-3">{watch.result.note}</p>}
        </>
      )}
    </section>
  );
}

/** Discoveries first: unmapped non-noisy ranges, then mapped ranges with bit flips, then the rest; noisy last. */
function sortRanges(ranges: ChangedRange[]): ChangedRange[] {
  const rank = (c: ChangedRange) => (c.noisy ? 3 : c.field === "(unmapped)" ? 0 : c.bitsFlipped?.length ? 1 : 2);
  return [...ranges].sort((a, b) => rank(a) - rank(b) || a.off - b.off);
}

function RangeRow({ c, region, selected, onSelect }: { c: ChangedRange; region: Region; selected: boolean; onSelect: () => void }) {
  const unmapped = c.field === "(unmapped)";
  const past = region.structSize !== undefined && c.off >= region.structSize;
  return (
    <li>
      <button type="button" onClick={onSelect} aria-pressed={selected}
        className={`grid w-full grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-x-2 gap-y-0.5 px-2 py-1.5 text-left hover:bg-surface-3/60 ${selected ? "bg-ring/10 shadow-[inset_2px_0_0_var(--color-ring)]" : ""}`}
        title={`+${hexOff(c.off)} (${c.off}), ${c.size} byte${c.size === 1 ? "" : "s"} · first change at ${(c.firstChangeAtMs / 1000).toFixed(1)} s, last at ${(c.lastChangeAtMs / 1000).toFixed(1)} s`}>
        <span className="tnum flex items-baseline gap-1 font-code text-[11px]"><span className="text-fg-2">+{hexOff(c.off)}</span><span className="text-[9.5px] text-fg-3">{c.off}</span></span>
        <span className="flex min-w-0 items-center gap-1.5">
          {unmapped ? (
            <span className="shrink-0 rounded-sm px-1 font-code text-[10px] font-semibold uppercase text-m-cand" style={{ background: mix("cand", 14) }}>{past ? "past end" : "unmapped"}</span>
          ) : c.field ? (
            <span className="truncate font-code text-[11.5px] font-medium text-fg" title={c.field}>{c.field}</span>
          ) : (
            <span className="font-code text-[11px] text-fg-3">{c.size} B</span>
          )}
          {c.noisy && <span className="shrink-0 rounded-sm bg-warning/15 px-1 text-[10px] text-warning" title="Changed on most samples: a timer or position, probably">noisy</span>}
        </span>
        <span className="tnum shrink-0 rounded-sm px-1 font-code text-[10px] font-semibold text-surface" style={{ background: mix("change", c.noisy ? 55 : 85) }} title={`Changed ${c.changes} time${c.changes === 1 ? "" : "s"}`}>×{c.changes}</span>
        <span className="tnum col-span-3 flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 font-code text-[10.5px]">
          <span className="truncate"><span className="text-fg-3">{c.first}</span> <span className="text-fg-3">→</span> <span className="font-semibold text-fg">{c.last}</span></span>
          {c.bitsFlipped && c.bitsFlipped.length > 0 && (
            <span className="flex flex-wrap items-center gap-1">
              <span className="text-fg-3">bit{c.bitsFlipped.length === 1 ? "" : "s"}</span>
              {c.bitsFlipped.map((b) => <span key={b} className="rounded-sm px-1 font-semibold text-m-change" style={{ background: mix("change", 14) }} title={`1 << ${b} = 0x${(1n << BigInt(b)).toString(16).toUpperCase()}${c.bitsRelativeTo ? ` of ${c.bitsRelativeTo}` : ""}`}>{b}</span>)}
            </span>
          )}
        </span>
      </button>
    </li>
  );
}

function Chip({ tone, children }: { tone: "plain" | "cand" | "change" | "warning"; children: React.ReactNode }) {
  const cls = tone === "plain" ? "bg-surface-3 text-fg-2" : tone === "cand" ? "text-m-cand" : tone === "change" ? "text-m-change" : "text-warning";
  const bg = tone === "plain" ? undefined : mix(tone, 14);
  return <span className={`tnum rounded-sm px-1.5 py-0.5 font-medium ${cls}`} style={{ background: bg }}>{children}</span>;
}
