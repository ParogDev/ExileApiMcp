import { useMemo, useState } from "react";
import { EmptyState, IconButton, SectionLabel } from "../components";
import { SmallButton } from "../explorer/Tree";
import { Icon } from "../icons";
import { hexOff } from "./bytes";
import { mix } from "./paint";
import type { ExpState, MemoryStore, Snapshot } from "./store";
import type { ByteChangeStep, CompareResult, CompareStep, LayoutField } from "./types";
import { Run } from "./Run";

// Experiments: named snapshots (memory_snapshot) compared in order (memory_compare). The timeline shows each step
// (one in-game action) and what flipped; the matrix below has one row per (item, byte) that ever changed and one
// column per step, so a bit that turns on at one step and off at another is visible as a pattern.

export interface ExperimentsHost { send?: (text: string) => void; ask?: (text: string) => void }

/** The Experiments tab: the guided-experiment runner (default) or the snapshot timeline. */
export function Experiments({ store, snap, fullscreen, host, now }: { store: MemoryStore; snap: Snapshot; fullscreen: boolean; host: ExperimentsHost; now: number }) {
  const view = snap.exp.view;
  const running = !!snap.run.waiting || !!snap.run.following;
  const toggle = (
    <div className="flex items-center gap-2">
      <div className="flex overflow-hidden rounded-md border border-line" role="radiogroup" aria-label="Experiments view">
        {([["run", "play", "Guided run", "One in-game action per step, captured and repeated: run a preset or follow Claude's run"], ["snapshots", "layers", "Snapshots", "Named snapshots compared in order: what one change flipped"]] as const).map(([id, icon, label, title]) => (
          <button key={id} type="button" role="radio" aria-checked={view === id} onClick={() => store.setExpView(id)} title={title}
            className={`flex h-6 items-center gap-1 px-2 text-[11px] font-medium ${view === id ? "bg-fg text-surface" : "text-fg-2 hover:bg-surface-3 hover:text-fg"}`}>
            <Icon name={icon} className="size-3" />{label}
            {id === "run" && running && view !== id && <span className="size-1.5 rounded-full bg-ring animate-pulse" title="A step is in progress" />}
          </button>
        ))}
      </div>
      {view === "run" && snap.run.phase !== "pick" && snap.run.experiment && <span className="truncate font-code text-[10.5px] text-fg-3" title="Experiment record">{snap.run.experiment}</span>}
    </div>
  );
  if (view === "run") {
    return (
      <div className={fullscreen ? "flex min-h-0 flex-1 flex-col gap-2.5" : "flex flex-col gap-2.5"}>
        {toggle}
        <Run store={store} snap={snap} fullscreen={fullscreen} host={host} now={now} />
      </div>
    );
  }
  return (
    <div className={fullscreen ? "flex min-h-0 flex-1 flex-col gap-2.5" : "flex flex-col gap-2.5"}>
      {toggle}
      <SnapshotExperiments store={store} snap={snap} fullscreen={fullscreen} host={host} />
    </div>
  );
}

function SnapshotExperiments({ store, snap, fullscreen, host }: { store: MemoryStore; snap: Snapshot; fullscreen: boolean; host: ExperimentsHost }) {
  const exp = snap.exp;
  const compare = exp.compare;
  // Field names for the changed bytes. memory_compare carries no struct, so: collection snapshots ("Name=x [i]"
  // items) take the population's struct when one is loaded; single-object snapshots take the struct view's when
  // its path is the snapshot's item.
  const collection = compare?.steps.some((s) => s.changes.some((c) => /^\w+=.* \[\d+\]$/.test(c.item)));
  const view = snap.views[snap.index];
  const viewFields = view?.data && "fields" in view.data ? (view.data as { fields: LayoutField[] }).fields : undefined;
  const fields: LayoutField[] = collection
    ? (snap.pop.layout?.fields ?? (view?.target.path && /\[\d+\]$/.test(view.target.path) ? viewFields : undefined) ?? [])
    : (compare && viewFields && compare.steps.some((s) => s.changes.some((c) => c.item === view?.target.path)) ? viewFields : []);
  const step = compare?.steps[exp.step];

  return (
    <div className={fullscreen ? "grid min-h-0 flex-1 grid-cols-1 gap-3 sm:grid-cols-[minmax(16rem,20rem)_minmax(0,1fr)]" : "flex flex-col gap-2.5"}>
      <aside className={`flex flex-col gap-2.5 ${fullscreen ? "scroll-thin min-h-0 overflow-y-auto pr-1" : ""}`}>
        <SnapshotList store={store} exp={exp} fullscreen={fullscreen} />
        <TakeSnapshot store={store} snap={snap} fullscreen={fullscreen} />
      </aside>
      <section aria-label="Experiment timeline" className={`flex min-h-0 flex-col overflow-hidden rounded-lg border border-line bg-surface ${fullscreen ? "min-h-0" : ""}`}>
        <div className="flex items-center gap-1.5 border-b border-line px-2 py-1.5">
          <Icon name="diff" className="size-3.5 text-fg-3" />
          <span className="text-[11.5px] font-semibold">Timeline</span>
          {compare && <span className="tnum text-[10.5px] text-fg-3">{compare.snapshots.length} snapshots · {compare.steps.length} steps</span>}
          {exp.comparing && <Icon name="sync" className="spin size-3 text-fg-3" />}
          {compare && (
            <span className="ml-auto flex items-center gap-0.5">
              <IconButton icon="arrowLeft" label="Previous step" size="sm" disabled={exp.step <= 0} onClick={() => store.setStep(exp.step - 1)} />
              <span className="tnum text-[10.5px] text-fg-2">step {exp.step + 1} / {compare.steps.length}</span>
              <IconButton icon="arrowRight" label="Next step" size="sm" disabled={exp.step >= compare.steps.length - 1} onClick={() => store.setStep(exp.step + 1)} />
            </span>
          )}
        </div>
        {exp.compareError && <p className="code-wrap px-3 py-2 text-[11.5px] text-danger">{exp.compareError}</p>}
        {!compare && !exp.comparing && !exp.compareError && (
          <EmptyState icon="diff" title="Compare snapshots in order" className="py-6">
            Pick two or more snapshots on the left, in the order you took them, and compare. Each step is one thing you changed in game; what flipped between two snapshots is where that state lives.
          </EmptyState>
        )}
        {compare && (
          <div className={`scroll-thin flex min-h-0 flex-col overflow-auto ${fullscreen ? "flex-1" : ""}`}>
            <Timeline compare={compare} step={exp.step} onStep={(i) => store.setStep(i)} />
            <Matrix compare={compare} step={exp.step} fields={fields} onStep={(i) => store.setStep(i)} />
            {step && <StepDetail step={step} fields={fields} host={host} compare={compare} index={exp.step} />}
          </div>
        )}
      </section>
    </div>
  );
}

// ── Snapshots ───────────────────────────────────────────────────────

function SnapshotList({ store, exp, fullscreen }: { store: MemoryStore; exp: ExpState; fullscreen: boolean }) {
  const list = exp.list ?? [];
  const order = (name: string) => exp.selected.indexOf(name);
  return (
    <section aria-label="Snapshots" className="overflow-hidden rounded-lg border border-line bg-surface">
      <div className="flex items-center gap-1.5 border-b border-line px-2 py-1.5">
        <Icon name="layers" className="size-3.5 text-fg-3" />
        <span className="text-[11.5px] font-semibold">Snapshots</span>
        <span className="tnum text-[10.5px] text-fg-3">{list.length}</span>
        <span className="ml-auto flex items-center gap-1">
          {exp.selected.length > 0 && <button type="button" className="text-[10.5px] text-fg-3 underline-offset-2 hover:text-fg hover:underline" onClick={() => store.setSelectedSnapshots([])}>clear</button>}
          <IconButton icon="sync" label="Reload the list" size="sm" disabled={exp.listing} iconClass={exp.listing ? "spin" : ""} onClick={() => void store.listSnapshots()} />
        </span>
      </div>
      {exp.error && <p className="code-wrap px-3 py-2 text-[11px] text-danger">{exp.error}</p>}
      {!exp.listing && list.length === 0 && !exp.error && <p className="px-3 py-3 text-[11px] text-fg-3">No snapshots saved yet. Take a baseline below, change one thing in game, take another.</p>}
      <ul className={`scroll-thin overflow-y-auto ${fullscreen ? "max-h-[40vh]" : "max-h-[14rem]"}`}>
        {list.map((s) => {
          const i = order(s.name);
          return (
            <li key={s.name}>
              <button type="button" onClick={() => store.toggleSnapshot(s.name)} aria-pressed={i >= 0}
                className={`flex h-7 w-full items-center gap-2 px-2 text-left text-[11.5px] hover:bg-surface-2 ${i >= 0 ? "bg-ring/5" : ""}`}
                title={`Saved ${new Date(s.savedAt).toLocaleString()}\nClick to ${i >= 0 ? "remove from" : "add to"} the comparison`}>
                <span className={`tnum grid size-4 shrink-0 place-items-center rounded-full text-[9.5px] font-semibold ${i >= 0 ? "bg-fg text-surface" : "border border-line text-fg-3"}`}>{i >= 0 ? i + 1 : ""}</span>
                <span className="truncate font-code">{s.name}</span>
                <span className="tnum ml-auto shrink-0 text-[10px] text-fg-3">{new Date(s.savedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span>
              </button>
            </li>
          );
        })}
      </ul>
      <div className="flex items-center gap-1.5 border-t border-line px-2 py-1.5">
        <SmallButton icon="diff" tone="primary" disabled={exp.selected.length < 2 || exp.comparing} onClick={() => void store.compareSelected()} title="memory_compare in the numbered order">
          {exp.comparing ? "Comparing…" : `Compare ${exp.selected.length || ""}`}
        </SmallButton>
        <span className="text-[10px] text-fg-3">{exp.selected.length < 2 ? "pick 2 or more, in order" : ""}</span>
      </div>
    </section>
  );
}

function TakeSnapshot({ store, snap, fullscreen: _f }: { store: MemoryStore; snap: Snapshot; fullscreen: boolean }) {
  const exp = snap.exp;
  const defaultPath = snap.pop.path || snap.views[snap.index]?.target.path || "";
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [path, setPath] = useState(defaultPath);
  const [labels, setLabels] = useState(snap.pop.labels.join(", ") || "Name");
  const nextName = () => {
    const last = exp.list?.[exp.list.length - 1]?.name;
    const m = last && /^(.*?)-(\d+)(?:-.*)?$/.exec(last);
    return m ? `${m[1]}-${Number(m[2]) + 1}-` : "baseline";
  };
  return (
    <section aria-label="Take a snapshot" className="overflow-hidden rounded-lg border border-line bg-surface">
      <button type="button" onClick={() => { setOpen((o) => !o); if (!open && !name) setName(nextName()); if (!open && !path) setPath(defaultPath); }} aria-expanded={open}
        className="flex w-full items-center gap-1.5 px-2 py-1.5 text-left hover:bg-surface-2">
        <Icon name="target" className="size-3.5 text-fg-3" />
        <span className="text-[11.5px] font-semibold">Take a snapshot</span>
        <Icon name="chevron" className={`ml-auto size-3.5 text-fg-3 transition-transform ${open ? "rotate-180" : ""}`} />
      </button>
      {open && (
        <form className="fade-in flex flex-col gap-1.5 border-t border-line px-2 py-2 text-[11px]" onSubmit={(e) => { e.preventDefault(); void store.takeSnapshot(name, path, labels.split(/[,\s]+/).filter(Boolean)); }}>
          <label className="flex items-center gap-1.5"><span className="w-10 shrink-0 text-fg-3">Name</span>
            <input value={name} onChange={(e) => setName(e.target.value)} spellCheck={false} placeholder="aff-8-delve" pattern="[\w.\-]{1,64}" className="h-6 min-w-0 flex-1 rounded-md border border-line bg-surface px-2 font-code text-[11px] focus:border-ring focus:outline-none" /></label>
          <label className="flex items-center gap-1.5"><span className="w-10 shrink-0 text-fg-3">Path</span>
            <input value={path} onChange={(e) => setPath(e.target.value)} spellCheck={false} placeholder="GameController.IngameState.ServerData.PlayerStashTabs" className="h-6 min-w-0 flex-1 rounded-md border border-line bg-surface px-2 font-code text-[11px] focus:border-ring focus:outline-none" /></label>
          <label className="flex items-center gap-1.5"><span className="w-10 shrink-0 text-fg-3">Labels</span>
            <input value={labels} onChange={(e) => setLabels(e.target.value)} spellCheck={false} placeholder="Name, Affinity" className="h-6 min-w-0 flex-1 rounded-md border border-line bg-surface px-2 font-code text-[11px] focus:border-ring focus:outline-none" /></label>
          <div className="flex items-center gap-2">
            <button type="submit" disabled={exp.saving || !name.trim() || !path.trim()} className="h-6 rounded-md bg-fg px-2.5 text-[11px] font-medium text-surface disabled:opacity-50">{exp.saving ? "Saving…" : "Save snapshot"}</button>
            <span className="text-[10px] text-fg-3">Then change one thing in game and save the next.</span>
          </div>
          {exp.saveError && <p className="code-wrap text-danger">{exp.saveError}</p>}
          {exp.saved && <p className="text-fg-2">Saved <span className="font-code">{exp.saved.saved}</span> · {exp.saved.what}</p>}
        </form>
      )}
    </section>
  );
}

// ── Timeline ────────────────────────────────────────────────────────

function Timeline({ compare, step, onStep }: { compare: CompareResult; step: number; onStep: (i: number) => void }) {
  return (
    <div className="scroll-thin overflow-x-auto px-3 pt-3" role="tablist" aria-label="Steps">
      <div className="flex min-w-max items-stretch">
        {compare.snapshots.map((name, i) => {
          const s = compare.steps[i];
          const active = i === step;
          const nextActive = i === step + 1;
          const bytes = s ? s.changes.reduce((n, c) => n + c.bytes.length, 0) : 0;
          return (
            <div key={name} className="flex items-stretch">
              <div className={`flex w-[7.5rem] flex-col items-center ${active || nextActive ? "" : "opacity-70"}`}>
                <span className={`size-2.5 rounded-full border-2 ${active || nextActive ? "border-fg bg-fg" : "border-fg-3 bg-surface"}`} />
                <span className="mt-1 w-full truncate text-center font-code text-[10.5px]" title={name}>{short(name, compare.snapshots)}</span>
              </div>
              {s && (
                <button type="button" role="tab" aria-selected={active} onClick={() => onStep(i)}
                  className={`-mx-[3.75rem] mt-[4px] flex h-3 w-[7.5rem] shrink-0 flex-col items-center ${active ? "" : "opacity-60 hover:opacity-100"}`}
                  title={`${s.from} → ${s.to}: ${s.changes.length} item${s.changes.length === 1 ? "" : "s"}, ${bytes} byte${bytes === 1 ? "" : "s"} changed`}>
                  <span className="h-0.5 w-full" style={{ background: active ? "var(--color-m-change)" : "var(--color-line-2)" }} />
                  <span className={`tnum mt-6 whitespace-nowrap rounded-sm px-1 text-[9.5px] ${active ? "font-semibold text-m-change" : "text-fg-3"}`} style={active ? { background: mix("change", 12) } : undefined}>{s.changes.length === 0 ? "no change" : `${s.changes.length} item · ${bytes} B`}</span>
                </button>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

/** Drop the common prefix of a series: "aff-3-ultimatum" -> "3-ultimatum". */
function short(name: string, all: string[]): string {
  if (all.length < 2) return name;
  let p = 0;
  while (p < name.length && all.every((n) => n[p] === name[p])) p++;
  const cut = name.lastIndexOf("-", p) + 1;
  return cut > 0 && cut < name.length ? name.slice(cut) : name;
}

// ── Matrix: (item, byte) rows × step columns ─────────────────────────

function Matrix({ compare, step, fields, onStep }: { compare: CompareResult; step: number; fields: LayoutField[]; onStep: (i: number) => void }) {
  const rows = useMemo(() => {
    const map = new Map<string, { item: string; off: number; cells: (ByteChangeStep | undefined)[]; n: number }>();
    compare.steps.forEach((s, si) => {
      for (const c of s.changes) for (const b of c.bytes) {
        const k = `${c.item}|${b.off}`;
        const r = map.get(k) ?? { item: c.item, off: b.off, cells: Array<ByteChangeStep | undefined>(compare.steps.length).fill(undefined), n: 0 };
        r.cells[si] = b; r.n++;
        map.set(k, r);
      }
    });
    const perItem = new Map<string, number>();
    for (const r of map.values()) perItem.set(r.item, (perItem.get(r.item) ?? 0) + r.n);
    // The item that changed most is the one being experimented on: it goes first.
    return [...map.values()].sort((a, b) => (perItem.get(b.item)! - perItem.get(a.item)!) || a.item.localeCompare(b.item) || a.off - b.off);
  }, [compare]);
  if (!rows.length) return <p className="px-3 py-3 text-[11px] text-fg-3">Nothing changed between these snapshots.</p>;
  let lastItem = "";
  return (
    <div className="scroll-thin mt-6 overflow-x-auto px-2">
      <table className="w-full min-w-max border-separate border-spacing-0 text-[10.5px]">
        <thead>
          <tr>
            <th className="sticky left-0 z-10 bg-surface px-1 pb-1 text-left font-semibold text-fg-3">item · byte</th>
            {compare.steps.map((s, i) => (
              <th key={i} className="px-1 pb-1 text-center font-normal">
                <button type="button" onClick={() => onStep(i)} className={`tnum rounded-sm px-1 font-code ${i === step ? "font-semibold text-m-change" : "text-fg-3 hover:text-fg"}`} style={i === step ? { background: mix("change", 12) } : undefined} title={`${s.from} → ${s.to}`}>{i + 1}</button>
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => {
            const first = r.item !== lastItem; lastItem = r.item;
            const f = fields.find((x) => r.off >= x.off && r.off < x.off + x.size);
            return (
              <tr key={`${r.item}|${r.off}`} className={first ? "border-t" : ""}>
                <td className={`sticky left-0 z-10 bg-surface px-1 py-px ${first ? "pt-1.5" : ""}`}>
                  <div className="flex items-center gap-1.5">
                    <span className={`w-[7rem] truncate font-code ${first ? "font-semibold text-fg" : "text-transparent"}`} title={r.item}>{itemName(r.item)}</span>
                    <span className="tnum font-code text-fg-2">+{r.off}</span>
                    <span className="tnum font-code text-[9.5px] text-fg-3">{hexOff(r.off)}</span>
                    {f ? <span className="truncate rounded-sm px-1 font-code text-[9.5px] text-fg" style={{ background: mix("field", 22) }} title={`${f.name} (${f.type}) at +${f.off}`}>{f.name.split(".").pop()}{f.size > 1 ? ` [${r.off - f.off}]` : ""}</span> : fields.length ? <span className="rounded-sm px-1 font-code text-[9.5px] text-m-cand" style={{ background: mix("cand", 14) }}>unmapped</span> : null}
                  </div>
                </td>
                {r.cells.map((b, i) => (
                  <td key={i} className={`px-1 py-px text-center ${i === step ? "bg-ring/5" : ""}`}>
                    {b ? <BitFlip b={b} fieldBase={f ? (r.off - f.off) * 8 : undefined} /> : <span className="text-fg-3/40">·</span>}
                  </td>
                ))}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function itemName(item: string): string {
  const m = /^\w+=(.*?) \[\d+\]$/.exec(item);
  return m ? m[1] : item;
}

function BitFlip({ b, fieldBase }: { b: ByteChangeStep; fieldBase?: number }) {
  const lbl = (bit: number) => fieldBase !== undefined ? String(fieldBase + bit) : String(bit);
  return (
    <span className="inline-flex items-center gap-0.5 whitespace-nowrap font-code" title={`${b.from} → ${b.to}${b.bitsOn.length ? ` · on: ${b.bitsOn.join(",")}` : ""}${b.bitsOff.length ? ` · off: ${b.bitsOff.join(",")}` : ""}${fieldBase !== undefined ? "\nBit numbers are relative to the field" : ""}`}>
      {b.bitsOn.map((bit) => <span key={`on${bit}`} className="tnum rounded-sm px-1 font-semibold text-surface" style={{ background: "var(--color-m-change)" }}>▲{lbl(bit)}</span>)}
      {b.bitsOff.map((bit) => <span key={`off${bit}`} className="tnum rounded-sm border px-1 text-m-change" style={{ borderColor: mix("change", 60) }}>▽{lbl(bit)}</span>)}
      {!b.bitsOn.length && !b.bitsOff.length && <span className="text-fg-2">{b.from}→{b.to}</span>}
    </span>
  );
}

// ── Step detail ─────────────────────────────────────────────────────

function StepDetail({ step, fields, host, compare, index }: { step: CompareStep; fields: LayoutField[]; host: ExperimentsHost; compare: CompareResult; index: number }) {
  const describe = () => `Experiment ${compare.snapshots.join(" → ")}, step ${index + 1} (${step.from} → ${step.to}): ` +
    (step.changes.length ? step.changes.map((c) => `${c.item}: ${c.bytes.map((b) => `+${b.off} ${b.from}->${b.to}${b.bitsOn.length ? ` on ${b.bitsOn.join(",")}` : ""}${b.bitsOff.length ? ` off ${b.bitsOff.join(",")}` : ""}`).join(", ")}${c.labels ? ` | ${Object.entries(c.labels).map(([k, v]) => `${k} ${v}`).join("; ")}` : ""}`).join(". ") : "no change") + ".";
  return (
    <div className="mt-4 border-t border-line px-3 py-2.5">
      <SectionLabel right={<span className="tnum font-code">{step.from} → {step.to}</span>}><Icon name="diff" className="size-3 text-m-change" />Step {index + 1}</SectionLabel>
      {step.changes.length === 0 && <p className="mt-1.5 text-[11px] text-fg-3">Nothing changed. The action may not touch this struct, or the snapshot was taken before the game applied it.</p>}
      <ul className="mt-1.5 space-y-2">
        {step.changes.map((c) => {
          const labelDelta = Object.entries(c.labels ?? {});
          return (
            <li key={c.item} className="rounded-md border border-line bg-surface-2/60 px-2.5 py-2 text-[11px]">
              <div className="flex flex-wrap items-baseline gap-x-2">
                <span className="font-code font-semibold">{itemName(c.item)}</span>
                <span className="font-code text-[10px] text-fg-3">{c.item}</span>
                {labelDelta.map(([k, v]) => <LabelDelta key={k} k={k} v={v} />)}
              </div>
              <ul className="mt-1 space-y-0.5">
                {c.bytes.map((b) => {
                  const f = fields.find((x) => b.off >= x.off && b.off < x.off + x.size);
                  const base = f ? (b.off - f.off) * 8 : 0;
                  const explained = labelDelta.map(([k, v]) => matchesLabel(k, v, b, base)).filter(Boolean) as string[];
                  return (
                    <li key={b.off} className="flex flex-wrap items-center gap-x-2 gap-y-0.5">
                      <span className="tnum font-code text-fg-2">+{b.off} <span className="text-[9.5px] text-fg-3">{hexOff(b.off)}</span></span>
                      {f ? <span className="rounded-sm px-1 font-code text-[9.5px]" style={{ background: mix("field", 22) }}>{f.name}{f.size > 1 ? ` byte ${b.off - f.off}` : ""}</span> : fields.length ? <span className="rounded-sm px-1 font-code text-[9.5px] text-m-cand" style={{ background: mix("cand", 14) }}>unmapped</span> : null}
                      <span className="tnum font-code"><span className="text-fg-3">{b.from}</span> → <span className="font-semibold">{b.to}</span></span>
                      <BitFlip b={b} fieldBase={f ? base : undefined} />
                      {explained.map((e) => <span key={e} className="rounded-sm bg-success/15 px-1 text-[10px] text-success" title="The byte's flipped bit is the same bit that changed in the label's value">{e}</span>)}
                    </li>
                  );
                })}
              </ul>
            </li>
          );
        })}
      </ul>
      {(host.send || host.ask) && (
        <div className="mt-2 flex flex-wrap gap-1.5">
          {host.send && <SmallButton icon="send" onClick={() => host.send!(describe())}>Send to Claude</SmallButton>}
          {host.ask && <SmallButton icon="sparkle" tone="primary" onClick={() => host.ask!(`${describe()} Which bit encodes the thing I changed, and what are the other items' changes (collateral)? Short answer.`)}>Ask Claude</SmallButton>}
        </div>
      )}
    </div>
  );
}

function LabelDelta({ k, v }: { k: string; v: string }) {
  const m = /^(-?\d+) -> (-?\d+)$/.exec(v);
  const bits = m ? bitDelta(Number(m[1]), Number(m[2])) : undefined;
  return (
    <span className="inline-flex items-center gap-1 rounded-sm px-1.5 py-0.5 text-[10.5px]" style={{ background: mix("ptr", 12) }} title={bits ? `${k}: bits on ${bits.on.join(",") || "–"}, off ${bits.off.join(",") || "–"}` : undefined}>
      <span className="font-semibold text-m-ptr">{k}</span>
      <span className="tnum font-code">{v.replace("->", "→")}</span>
      {bits && (bits.on.length || bits.off.length) ? <span className="tnum font-code text-fg-2">{bits.on.map((b) => `▲${b}`).join(" ")} {bits.off.map((b) => `▽${b}`).join(" ")}</span> : null}
    </span>
  );
}

function bitDelta(a: number, b: number): { on: number[]; off: number[] } {
  const on: number[] = [], off: number[] = [];
  for (let i = 0; i < 32; i++) {
    const wa = (a >>> i) & 1, wb = (b >>> i) & 1;
    if (wa !== wb) (wb ? on : off).push(i);
  }
  return { on, off };
}

/** "Affinity bit 5" when the byte's flipped bit (field-relative) equals a bit that changed in the label's value. */
function matchesLabel(k: string, v: string, b: ByteChangeStep, base: number): string | undefined {
  const m = /^(-?\d+) -> (-?\d+)$/.exec(v);
  if (!m) return undefined;
  const d = bitDelta(Number(m[1]), Number(m[2]));
  const on = b.bitsOn.map((x) => base + x).filter((x) => d.on.includes(x));
  const off = b.bitsOff.map((x) => base + x).filter((x) => d.off.includes(x));
  if (!on.length && !off.length) return undefined;
  return `= ${k} bit ${[...on, ...off].join(",")}`;
}
