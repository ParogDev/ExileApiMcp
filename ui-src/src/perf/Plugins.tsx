import { useState } from "react";
import { SectionLabel } from "../components";
import { SmallButton } from "../explorer/Tree";
import { Icon } from "../icons";
import { fmtKB, fmtMs, fmtPct, fmtStats, pluginsByAlloc, pluginsByTime, splitOf } from "./model";
import type { Action, PluginCost, Report, Trace } from "./types";

// Where the frame time goes: the average frame as one bar (plugins | HUD core | the rest: waiting on the game,
// present, vsync), then every traced plugin ranked by time or by allocation per frame, with a proportional bar
// behind the number so the ranking reads without reading the numbers. Selecting a plugin opens its Tick / Render
// distribution and the two actions that go deeper (profile_plugin, hud_plugin_lint).

export function FrameSplit({ trace }: { trace: Trace }) {
  const s = splitOf(trace);
  const pct = (v: number) => `${Math.max(0, Math.min(100, (v / s.intervalMs) * 100))}%`;
  return (
    <div>
      <div className="flex h-3 overflow-hidden rounded-sm bg-surface-3" role="img" aria-label={`Average frame ${fmtMs(s.intervalMs)}: plugins ${fmtMs(s.pluginsMs)}, HUD core ${fmtMs(s.coreMs)}, waiting ${fmtMs(s.restMs)}`}>
        <div className="bg-p-plugins" style={{ width: pct(s.pluginsMs) }} />
        <div className="ml-px bg-p-core" style={{ width: pct(s.coreMs) }} />
      </div>
      <div className="mt-1.5 flex flex-wrap gap-x-3 gap-y-0.5 text-[11px]">
        <Part swatch="bg-p-plugins" label="plugins" ms={s.pluginsMs} of={s.intervalMs} />
        <Part swatch="bg-p-core" label="HUD core" ms={s.coreMs} of={s.intervalMs} />
        <Part swatch="bg-surface-3" label="waiting on the game / present" ms={s.restMs} of={s.intervalMs} />
      </div>
    </div>
  );
}

function Part({ swatch, label, ms, of }: { swatch: string; label: string; ms: number; of: number }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className={`size-2 rounded-xs ${swatch}`} aria-hidden />
      <span className="text-fg-2">{label}</span>
      <span className="tnum font-medium">{fmtMs(ms)}</span>
      <span className="tnum text-fg-3">{fmtPct(ms, of)}</span>
    </span>
  );
}

export type PluginSort = "time" | "alloc";

export function PluginTable({ report, selected, onSelect, sort, onSort, maxRows = 8 }: {
  report: Report; selected?: string; onSelect: (name: string) => void; sort: PluginSort; onSort: (s: PluginSort) => void; maxRows?: number;
}) {
  const [all, setAll] = useState(false);
  const list = sort === "time" ? pluginsByTime(report) : pluginsByAlloc(report);
  const maxTime = Math.max(0.001, ...list.map((p) => p.tickMs + p.renderMs));
  const maxAlloc = Math.max(0.001, ...list.map((p) => p.allocKBPerFrame));
  const shown = all ? list : list.slice(0, maxRows);
  if (!list.length) return <p className="text-[11.5px] text-fg-3">No plugin was traced: none has a Tick or Render the HUD called during the trace.</p>;
  return (
    <div>
      <div className="grid grid-cols-[minmax(0,1fr)_5.5rem_5.5rem] items-center gap-x-2 px-1 text-[10.5px] text-fg-3" role="row">
        <span>plugin</span>
        <SortHead active={sort === "time"} onClick={() => onSort("time")} title="Tick + Render per frame, averaged over the trace">ms / frame</SortHead>
        <SortHead active={sort === "alloc"} onClick={() => onSort("alloc")} title="Bytes allocated per frame in Tick + Render: what feeds the garbage collector">KB / frame</SortHead>
      </div>
      <ul role="listbox" aria-label="Plugins by cost" className="mt-0.5">
        {shown.map((p) => <PluginRow key={p.name} p={p} maxTime={maxTime} maxAlloc={maxAlloc} selected={selected === p.name} onSelect={() => onSelect(p.name)} sort={sort} />)}
      </ul>
      {list.length > maxRows && (
        <button type="button" onClick={() => setAll((a) => !a)} className="mt-1 px-1 text-[11px] text-fg-3 underline-offset-2 hover:text-fg hover:underline">
          {all ? "Show the costliest only" : `Show all ${list.length} plugins`}
        </button>
      )}
    </div>
  );
}

function SortHead({ active, onClick, title, children }: { active: boolean; onClick: () => void; title: string; children: React.ReactNode }) {
  return (
    <button type="button" onClick={onClick} title={title} aria-pressed={active}
      className={`flex items-center justify-end gap-0.5 rounded-sm px-1 text-right hover:text-fg ${active ? "font-semibold text-fg-2" : ""}`}>
      {children}{active && <Icon name="down" className="size-2.5" />}
    </button>
  );
}

function PluginRow({ p, maxTime, maxAlloc, selected, onSelect, sort }: { p: PluginCost; maxTime: number; maxAlloc: number; selected: boolean; onSelect: () => void; sort: PluginSort }) {
  const t = p.tickMs + p.renderMs;
  return (
    <li role="option" aria-selected={selected} tabIndex={0} onClick={onSelect}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onSelect(); } }}
      className={`grid h-7 cursor-pointer grid-cols-[minmax(0,1fr)_5.5rem_5.5rem] items-center gap-x-2 rounded-md px-1 text-[12px] outline-none transition-colors hover:bg-surface-2 focus-visible:ring-2 focus-visible:ring-ring ${selected ? "bg-surface-2 ring-1 ring-ring" : ""}`}>
      <span className="truncate" title={p.name}>{p.name}</span>
      <Cell value={fmtMs(t, false)} frac={t / maxTime} swatch="bg-p-plugins" lead={sort === "time"} />
      <Cell value={fmtKB(p.allocKBPerFrame).replace(" KB", "")} frac={p.allocKBPerFrame / maxAlloc} swatch="bg-p-gc" lead={sort === "alloc"} unit={p.allocKBPerFrame >= 1024 ? "MB" : undefined} />
    </li>
  );
}

function Cell({ value, frac, swatch, lead, unit }: { value: string; frac: number; swatch: string; lead: boolean; unit?: string }) {
  return (
    <span className="relative flex h-5 items-center justify-end">
      <span className={`absolute inset-y-1 right-0 rounded-xs ${swatch} ${lead ? "opacity-30" : "opacity-15"}`} style={{ width: `${Math.max(2, frac * 100)}%` }} aria-hidden />
      <span className={`tnum relative pr-1 ${lead ? "font-semibold" : "text-fg-2"}`}>{value}{unit && <span className="text-[10px] text-fg-3"> {unit}</span>}</span>
    </span>
  );
}

export function PluginDetail({ name, trace, onRun, running, onClose }: { name: string; trace: Trace; onRun: (a: Action) => void; running: (a: Action) => boolean; onClose: () => void }) {
  const tick = trace.pluginTickMs?.[name];
  const render = trace.pluginRenderMs?.[name];
  const allocR = trace.pluginAllocKBPerFrame?.render?.[name];
  const allocT = trace.pluginAllocKBPerFrame?.tick?.[name];
  const profile: Action = { label: `Profile ${name}`, tool: "profile_plugin", args: { name } };
  const lint: Action = { label: `Lint ${name}`, tool: "hud_plugin_lint", args: { plugin: name } };
  return (
    <div className="fade-in rounded-lg border border-line bg-surface-2 p-2.5 text-[11.5px]">
      <div className="flex items-center gap-2">
        <SectionLabel className="min-w-0 flex-1"><span className="truncate normal-case tracking-normal text-fg">{name}</span></SectionLabel>
        <button type="button" onClick={onClose} aria-label="Close" className="grid size-5 place-items-center rounded-sm text-fg-3 hover:bg-surface-3 hover:text-fg"><Icon name="x" className="size-3" /></button>
      </div>
      <dl className="mt-1.5 grid grid-cols-[3.5rem_1fr] gap-y-1">
        <dt className="text-fg-3">Tick</dt><dd className="tnum min-w-0 break-words">{tick?.n ? fmtStats(tick) : "not called"}{allocT ? <span className="text-fg-3"> · {fmtKB(allocT)} / frame</span> : null}</dd>
        <dt className="text-fg-3">Render</dt><dd className="tnum min-w-0 break-words">{render?.n ? fmtStats(render) : "not called"}{allocR ? <span className="text-fg-3"> · {fmtKB(allocR)} / frame</span> : null}</dd>
      </dl>
      {(render?.max ?? 0) > 4 * Math.max(0.05, render?.p50 ?? 0) && render?.max !== undefined && render.max > 2 && (
        <p className="mt-1.5 text-fg-2">Render's worst frame ({fmtMs(render.max)}) is far above its median: a GC pause landed inside it, or one frame did much more (an area change, a scan). The timeline shows which.</p>
      )}
      <div className="mt-2 flex flex-wrap gap-1.5">
        <SmallButton icon="activity" tone="primary" onClick={() => onRun(profile)} disabled={running(profile)} title="profile_plugin: where this plugin's time and allocation go, by method (4 s)">Profile</SmallButton>
        <SmallButton icon="code" onClick={() => onRun(lint)} disabled={running(lint)} title="hud_plugin_lint: expensive HUD API calls in its per-frame code (offline, from the DLL)">Lint</SmallButton>
      </div>
    </div>
  );
}
