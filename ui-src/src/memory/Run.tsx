import { useMemo, useState, type ReactNode } from "react";
import { EmptyState, IconButton, SectionLabel } from "../components";
import { SmallButton } from "../explorer/Tree";
import { Icon, type IconName } from "../icons";
import { mix } from "./paint";
import { actionsFromRecord, captureHeadline, cardFromGuide, cardFromStep, describeChange, evidenceRows, fmtLeft, fmtWait, GUIDE_LABEL, guideExperiment, guideSubline, parseEvidenceKey, parseWatch, receiptOf, repeatsOf, stepOver, type CardModel, type CardStatus, type EvidenceKey, type EvidenceRow } from "./runModel";
import { plannedSteps, RUN_CHECKS, RUN_TARGET_REPEATS, RUN_TIMEOUTS_MS, type MemoryStore, type RunAttempt, type RunState, type Snapshot } from "./store";
import type { ExperimentChange, ExperimentPreset, RecordStep } from "./types";

// The guided-experiment runner. A developer picks a preset, ticks the setup checklist and runs it step by step: each
// step runs in the server (experiment_step_start) while the app polls experiment_status, so a wait can last minutes; the
// big card mirrors the in-game agent guide card (DO THIS NOW -> CHANGE SEEN -> CAPTURED / TRY AGAIN / CANCELLED), each
// capture is read back in plain language, and the evidence (what changed in every repeat of an action) builds up on the
// side. When the agent is running an experiment instead, the same screens follow its run read-only.

interface Action { label: string; instruction: string }

export interface RunHost { send?: (text: string) => void; ask?: (text: string) => void }

export function Run({ store, snap, fullscreen, host, now }: { store: MemoryStore; snap: Snapshot; fullscreen: boolean; host: RunHost; now: number }) {
  const run = snap.run;
  if (run.following && (run.phase === "run" || run.phase === "done")) return <Follow store={store} run={run} fullscreen={fullscreen} host={host} now={now} />;
  switch (run.phase) {
    case "pick": return <Picker store={store} run={run} now={now} />;
    case "setup": return <Setup store={store} run={run} />;
    case "run": return <Running store={store} run={run} fullscreen={fullscreen} now={now} />;
    case "done": return <Finish store={store} run={run} host={host} />;
  }
}

// ── Pick a preset ────────────────────────────────────────────────────

function Picker({ store, run, now }: { store: MemoryStore; run: RunState; now: number }) {
  const presets = run.presets ?? [];
  const records = run.records ?? [];
  const recorded = (id: string) => records.filter((r) => r.name === id || r.name.startsWith(id + "-"));
  return (
    <div className="flex flex-col gap-2.5">
      <section aria-label="Guided experiments" className="overflow-hidden rounded-lg border border-line bg-surface">
        <Header icon="play" title="Guided experiments" right={<IconButton icon="sync" label="Reload presets and records" size="sm" disabled={run.presetsLoading} iconClass={run.presetsLoading ? "spin" : ""} onClick={() => void store.loadPresets()} />}>
          <span className="text-[10.5px] text-fg-3">{presets.length} preset{presets.length === 1 ? "" : "s"}</span>
        </Header>
        <p className="border-b border-line px-3 py-2 text-[11.5px] leading-snug text-fg-2">
          You do one action in game per step; the tools capture what changed. The instruction shows on the HUD's agent guide card and here. Repeating an action 2-3 times turns one observation into evidence.
        </p>
        {run.presetsError && <p className="code-wrap px-3 py-2 text-[11.5px] text-danger">{run.presetsError}</p>}
        {run.presetsLoading && !run.presets && <div className="space-y-2 p-3" aria-hidden>{Array.from({ length: 3 }, (_, i) => <div key={i} className="shimmer h-10 rounded-md" />)}</div>}
        {run.presets && presets.length === 0 && <EmptyState icon="play" title="No presets for this game" className="py-5">Knowledge/experiments.json holds the presets; ask Claude to run a custom experiment instead.</EmptyState>}
        <ul className="divide-y divide-line">
          {presets.map((p) => {
            const recs = recorded(p.id);
            const n = recs.reduce((s, r) => s + (r.steps ?? 0), 0);
            return (
              <li key={p.id}>
                <button type="button" data-run-preset={p.id} onClick={() => store.pickPreset(p.id)} className="group grid w-full grid-cols-[minmax(0,1fr)_auto] items-start gap-x-3 px-3 py-2.5 text-left hover:bg-surface-2">
                  <div className="min-w-0">
                    <div className="flex items-center gap-1.5">
                      <span className="truncate text-[12.5px] font-semibold">{p.title}</span>
                      {recs.length > 0 && <span className="tnum shrink-0 rounded-sm bg-surface-3 px-1 text-[9.5px] font-semibold text-fg-2" title={`${recs.length} record${recs.length === 1 ? "" : "s"} on disk from earlier runs, ${n} step${n === 1 ? "" : "s"} in all`}>recorded{n ? ` ×${n}` : ""}</span>}
                    </div>
                    <p className="mt-0.5 text-[11px] leading-snug text-fg-2">{p.question}</p>
                    <div className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[10.5px] text-fg-3">
                      <span className="tnum">{p.steps.length} action{p.steps.length === 1 ? "" : "s"} · {p.watch.length} watched</span>
                      <span className="flex gap-1">{p.games.map((g) => <span key={g} className="rounded-sm border border-line px-1 text-[9.5px] font-medium">{g === "poe1" ? "PoE 1" : "PoE 2"}</span>)}</span>
                      <span className="truncate font-code opacity-70">{p.id}</span>
                    </div>
                  </div>
                  <span className="mt-0.5 inline-flex h-6 items-center gap-1 rounded-md border border-ring/40 bg-ring/10 px-2 text-[11px] font-medium text-fg group-hover:bg-ring/20"><Icon name="play" className="size-3" />Run</span>
                </button>
              </li>
            );
          })}
        </ul>
      </section>
      {records.length > 0 && (
        <section aria-label="Experiment records" className="overflow-hidden rounded-lg border border-line bg-surface">
          <Header icon="list" title="Records"><span className="text-[10.5px] text-fg-3">{records.length} on disk · newest first</span></Header>
          <ul className="divide-y divide-line">
            {records.slice(0, 8).map((r) => (
              <li key={r.name}>
                <button type="button" onClick={() => store.openRecord(r.name)} className="flex h-8 w-full items-center gap-2 px-3 text-left hover:bg-surface-2" title={`Read the summary of ${r.name}: what changed every time, what only sometimes`}>
                  <span className="truncate font-code text-[11.5px]">{r.name}</span>
                  {r.steps !== undefined && <span className="tnum shrink-0 rounded-sm bg-surface-3 px-1 text-[9.5px] font-semibold text-fg-2">×{r.steps} step{r.steps === 1 ? "" : "s"}</span>}
                  <span className="tnum ml-auto shrink-0 text-[10px] text-fg-3">{agoText(now - Date.parse(r.updated))}</span>
                  <Icon name="chevronRight" className="size-3.5 shrink-0 text-fg-3" />
                </button>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

// ── Setup: the checklist before step 1 ───────────────────────────────

const CHECK_TEXT: Record<(typeof RUN_CHECKS)[number], (p: ExperimentPreset) => ReactNode> = {
  setup: (p) => <>{p.setup}</>,
  focus: () => <>The game window is focused and the HUD's agent guide card is visible (bridge setting <em>Show Agent Guide</em>).</>,
  "one-action": () => <>I'll do exactly one action per step, then hold still for a second.</>,
};

function Setup({ store, run }: { store: MemoryStore; run: RunState }) {
  const p = run.preset!;
  const existing = run.summary?.experiment === run.experiment ? run.summary.record.steps.length : 0;
  const nameOk = /^[\w.-]{1,64}$/.test(run.experiment);
  const ready = store.runReady;
  const missing = RUN_CHECKS.filter((c) => !run.checks.has(c)).length;
  return (
    <section aria-label="Before you start" className="overflow-hidden rounded-lg border border-line bg-surface">
      <Header icon="play" title={p.title} right={<SmallButton icon="arrowLeft" onClick={() => store.resetRun()}>Presets</SmallButton>} />
      <div className="px-3 py-2.5">
        <p className="text-[11.5px] leading-snug text-fg-2"><span className="font-semibold text-fg">Question. </span>{p.question}</p>
        <p className="mt-1 text-[11px] text-fg-3">Plan: {p.steps.length} action{p.steps.length === 1 ? "" : "s"}, each {RUN_TARGET_REPEATS}-3 times, so about {p.steps.length * RUN_TARGET_REPEATS}-{p.steps.length * 3} steps. Each step waits for you up to {fmtWait(run.timeoutMs)}.</p>

        <SectionLabel className="mt-3">Before you start</SectionLabel>
        <ul className="mt-1.5 space-y-1">
          {RUN_CHECKS.map((c) => {
            const on = run.checks.has(c);
            return (
              <li key={c}>
                <label className={`flex cursor-pointer items-start gap-2 rounded-md border px-2.5 py-1.5 text-[11.5px] leading-snug ${on ? "border-success/30 bg-success/5" : "border-line hover:bg-surface-2"}`}>
                  <input type="checkbox" data-run={`check-${c}`} checked={on} onChange={() => store.toggleCheck(c)} className="mt-0.5 size-3.5 accent-[var(--color-success)]" />
                  <span className={on ? "text-fg-2" : "text-fg"}>{CHECK_TEXT[c](p)}</span>
                </label>
              </li>
            );
          })}
        </ul>

        <SectionLabel className="mt-3">The actions</SectionLabel>
        <ol className="mt-1.5 space-y-1">
          {p.steps.map((s, i) => (
            <li key={s.label} className="flex items-baseline gap-2 text-[11.5px]">
              <span className="tnum w-4 shrink-0 text-right text-fg-3">{i + 1}.</span>
              <span className="min-w-0 flex-1 leading-snug">{s.instruction}</span>
              <span className="shrink-0 font-code text-[10px] text-fg-3">{s.label}</span>
            </li>
          ))}
        </ol>

        <SectionLabel className="mt-3">Watched while you act</SectionLabel>
        <ul className="mt-1.5 flex flex-wrap gap-1">
          {p.watch.map((w) => { const i = parseWatch(w); return <li key={w} className="rounded-sm bg-surface-3 px-1.5 py-0.5 font-code text-[10.5px] text-fg-2" title={w}>{i.kind === "memory" ? <><span className="text-m-field">bytes</span> {i.short}{i.size ? ` ·${i.size}` : ""}</> : i.kind === "collection" ? <><span className="text-m-cand">items</span> {i.short}</> : i.short}</li>; })}
        </ul>

        <div className="mt-3 grid gap-2 sm:grid-cols-[minmax(0,1fr)_auto]">
          <label className="flex min-w-0 items-center gap-1.5 text-[11px]">
            <span className="shrink-0 text-fg-3">Record</span>
            <input value={run.experiment} onChange={(e) => store.setExperimentName(e.target.value)} spellCheck={false} pattern="[\w.\-]{1,64}" aria-invalid={!nameOk}
              className={`h-6 min-w-0 flex-1 rounded-md border bg-surface px-2 font-code text-[11px] focus:border-ring focus:outline-none ${nameOk ? "border-line" : "border-danger"}`} />
          </label>
          <div className="flex items-center gap-1.5 text-[11px]">
            <span className="shrink-0 text-fg-3">Wait up to</span>
            <div className="flex overflow-hidden rounded-md border border-line" role="radiogroup" aria-label="Timeout per step">
              {RUN_TIMEOUTS_MS.map((ms) => <button key={ms} type="button" role="radio" aria-checked={run.timeoutMs === ms} data-run-timeout={ms} onClick={() => store.setRunTimeout(ms)} className={`tnum h-6 px-1.5 text-[11px] ${run.timeoutMs === ms ? "bg-fg text-surface" : "text-fg-2 hover:bg-surface-3 hover:text-fg"}`}>{fmtWait(ms)}</button>)}
            </div>
          </div>
        </div>
        <p className="mt-1 text-[10.5px] leading-snug text-fg-3">
          {existing > 0 ? <>Continues the record <span className="font-code">{run.experiment}</span> ({existing} step{existing === 1 ? "" : "s"} so far); repeats add to its evidence. Change the name for a fresh one.</> : <>Steps are saved to this record; <span className="font-code">experiment_summary</span> reads it back.</>}
          {" "}The wait runs in the server, so take the time an action needs; a step can be cancelled while it waits.
        </p>

        <div className="mt-3 flex flex-wrap items-center gap-2">
          <button type="button" data-run="start" disabled={!ready} onClick={() => store.startRun()} title={ready ? "Show the first action on the in-game card and here" : missing ? `Tick the ${missing} remaining item${missing === 1 ? "" : "s"} first` : "Record name: letters, digits, - _ ."}
            className="inline-flex h-7 items-center gap-1.5 rounded-md bg-fg px-3 text-[11.5px] font-medium text-surface disabled:opacity-40"><Icon name="play" className="size-3.5" />Start</button>
          <span className="text-[10.5px] text-fg-3">{ready ? "The first action appears in game when you press Go." : missing ? `${missing} to tick` : ""}</span>
        </div>
      </div>
    </section>
  );
}

// ── Running: the card, the actions, the results and the evidence ──────

function Running({ store, run, fullscreen, now }: { store: MemoryStore; run: RunState; fullscreen: boolean; now: number }) {
  const p = run.preset!;
  const action = p.steps[run.stepIndex];
  const last = run.attempts[run.attempts.length - 1];
  const card = ownCard(run, action, last, now);
  const record = run.summary?.experiment === run.experiment ? run.summary.record : undefined;
  const reps = repeatsOf(action.label, record, record ? [] : localSteps(run));
  const result = last ? resultOf(last) : undefined;
  const rows = useMemo(() => evidenceRows(run.summary?.experiment === run.experiment ? run.summary : undefined, p), [run.summary, run.experiment, p]);
  const waiting = !!run.waiting;
  const total = record?.steps.length ?? localSteps(run).length;
  const allDone = p.steps.every((s) => repeatsOf(s.label, record, record ? [] : localSteps(run)) >= RUN_TARGET_REPEATS);

  const left = (
    <div className="flex flex-col gap-2.5">
      <StepCard card={card} now={now} title={p.title}>
        <div className="flex flex-wrap items-center gap-1.5">
          {waiting ? (
            <>
              <span className="min-w-0 flex-1 text-[11px] text-fg-2">{card.status === "waiting" ? "Do it in game now, then hold still. The card turns green when the change settled." : "Hold still: the change is settling."}</span>
              <SmallButton icon="x" onClick={() => void store.cancelStep()} disabled={run.waiting?.cancelling} title="experiment_step_cancel: stop waiting; nothing is recorded">{run.waiting?.cancelling ? "Cancelling…" : "Cancel"}</SmallButton>
            </>
          ) : (
            <>
              <button type="button" data-run="go" onClick={() => void store.runStep()} className="inline-flex h-7 items-center gap-1.5 rounded-md bg-fg px-3 text-[11.5px] font-medium text-surface" title={`experiment_step_start label=${action.label}, waits up to ${fmtWait(run.timeoutMs)}`}>
                <Icon name={card.status === "failed" || card.status === "cancelled" ? "sync" : "play"} className="size-3.5" />{card.status === "failed" || card.status === "cancelled" ? "Try again" : card.status === "captured" ? `Repeat · ${ordinal(reps + 1)} time` : reps > 0 ? `Go · ${ordinal(reps + 1)} time` : "Go"}
              </button>
              <SmallButton icon="arrowRight" onClick={() => store.nextStep()} title="Show the next action (no wait starts yet)">{p.steps.length > 1 ? `Next: ${p.steps[(run.stepIndex + 1) % p.steps.length].label}` : "Same action"}</SmallButton>
              <SmallButton icon="check" tone={allDone ? "primary" : "default"} onClick={() => void store.finishRun()} disabled={total === 0} title="Read the evidence, mark the in-game card done and list what to undo">Finish</SmallButton>
            </>
          )}
        </div>
      </StepCard>
      <StepList actions={p.steps} current={run.stepIndex} record={record} local={record ? [] : localSteps(run)} disabled={waiting} onPick={(i) => store.setStepIndex(i)} />
    </div>
  );
  const right = (
    <div className="flex flex-col gap-2.5">
      {result ? <Results r={result} /> : <section className="rounded-lg border border-dashed border-line px-3 py-3 text-[11.5px] leading-snug text-fg-3"><span className="font-medium text-fg-2">No capture yet.</span> Press Go, do the action in game, and the change shows up here: the values, bytes and bits that moved, in plain words first.</section>}
      <Evidence rows={rows} loading={run.summaryLoading} />
    </div>
  );
  return fullscreen ? (
    <div className="grid min-h-0 flex-1 grid-cols-1 gap-3 sm:grid-cols-[minmax(0,1fr)_minmax(18rem,26rem)]">
      <div className="scroll-thin min-h-0 overflow-y-auto pr-1">{left}</div>
      <div className="scroll-thin min-h-0 overflow-y-auto pr-1">{right}</div>
    </div>
  ) : (
    <div className="flex flex-col gap-2.5">{left}{right}</div>
  );
}

/**
 * The card as this app's own run shows it: ready (before Go), the server step's live status while it waits (waiting ->
 * detected, with the countdown against its timeout), then the outcome of the last step for this action.
 */
function ownCard(run: RunState, action: Action, last: RunAttempt | undefined, now: number): CardModel {
  if (run.waiting) {
    const s = run.step;
    const ours = s && s.startedAt === run.waiting.startedAt && !stepOver(s);
    const live = ours ? cardFromStep(s, run.stepSince) : undefined;
    // The in-game card's detail ("That changed back - still waiting") when it is about this very step.
    const g = run.guide;
    const detail = live?.status === "waiting" && g && guideExperiment(g) === run.experiment && g.instruction === action.instruction && g.status === "waiting" ? g.detail ?? undefined : undefined;
    return { status: live?.status ?? "waiting", instruction: action.instruction, step: run.waiting.step, steps: run.waiting.steps, since: run.stepSince ?? Date.parse(run.waiting.startedAt), timeoutMs: run.waiting.timeoutMs, detail };
  }
  if (last && last.label === action.label && now - last.at < 60_000) {
    if ("cancelled" in last.result) return { status: "cancelled", instruction: action.instruction, since: last.at };
    if ("error" in last.result) return { status: "failed", instruction: action.instruction, detail: last.result.error, since: last.at };
    if (last.result.changed) return { status: "captured", instruction: action.instruction, step: last.result.step, steps: plannedSteps(run.preset?.steps.length ?? 1, last.result.step), detail: receiptOf(last.result), since: last.at };
    // The server's long note goes to the results card; the card itself keeps the HUD's one-liner.
    return { status: "failed", instruction: action.instruction, since: last.at };
  }
  return { status: "idle", instruction: action.instruction };
}

function localSteps(run: RunState): RecordStep[] {
  return run.attempts.flatMap((a) => ("changed" in a.result && a.result.changed ? [{ label: a.label, at: new Date(a.at).toISOString(), changedAfterMs: a.result.changedAfterMs, watch: [], changes: a.result.changes }] : []));
}

// ── The card: mirrors the HUD's agent guide card ─────────────────────

const LOOK: Record<CardStatus, { tone: string; icon: IconName; loud: boolean; pulse: boolean }> = {
  idle: { tone: "var(--color-fg-3)", icon: "target", loud: false, pulse: false },
  waiting: { tone: "var(--color-ring)", icon: "radio", loud: true, pulse: true },
  detected: { tone: "var(--color-ring)", icon: "sync", loud: false, pulse: false },
  settling: { tone: "var(--color-ring)", icon: "sync", loud: false, pulse: false },
  captured: { tone: "var(--color-success)", icon: "check", loud: false, pulse: false },
  failed: { tone: "var(--color-danger)", icon: "warning", loud: true, pulse: false },
  cancelled: { tone: "var(--color-fg-2)", icon: "x", loud: false, pulse: false },
  done: { tone: "var(--color-fg-2)", icon: "check", loud: false, pulse: false },
  info: { tone: "var(--color-fg-2)", icon: "info", loud: false, pulse: false },
};

export function StepCard({ card, now, title, children, readOnly }: { card: CardModel; now: number; title?: string; children?: ReactNode; readOnly?: boolean }) {
  const look = LOOK[card.status];
  const label = card.status === "idle" ? "Ready" : GUIDE_LABEL[card.status];
  const sub = guideSubline(card.status, card.detail);
  const elapsed = card.since ? Math.max(0, now - card.since) : undefined;
  const leftMs = card.timeoutMs && elapsed !== undefined ? Math.max(0, card.timeoutMs - elapsed) : undefined;
  const pct = card.timeoutMs && elapsed !== undefined ? Math.min(1, elapsed / card.timeoutMs) : undefined;
  const busy = card.status === "waiting" || card.status === "detected" || card.status === "settling";
  const dashed = card.status === "idle";
  return (
    <section aria-label="Current step" aria-live="polite" data-status={card.status}
      className={`relative overflow-hidden rounded-lg border-2 bg-surface ${dashed ? "border-dashed" : ""} ${look.pulse ? "guide-pulse" : ""}`}
      style={{ borderColor: dashed ? "var(--color-line-2)" : look.tone, "--pulse": look.tone } as React.CSSProperties}>
      <div className="flex items-center gap-2 px-3 pt-2">
        <span className="grid size-5 shrink-0 place-items-center rounded-full" style={{ background: mixVar(look.tone, card.status === "captured" || card.status === "waiting" ? 100 : 15), color: card.status === "captured" || card.status === "waiting" ? "var(--color-surface)" : look.tone }}>
          <Icon name={look.icon} className={`size-3 ${busy && look.icon === "sync" ? "spin" : ""} ${card.status === "waiting" ? "animate-pulse" : ""}`} />
        </span>
        <span className={`shrink-0 whitespace-nowrap text-[10.5px] font-bold uppercase tracking-wider ${look.loud ? "" : "opacity-90"}`} style={{ color: look.tone }}>{label}</span>
        {title && <span className="truncate text-[11px] text-fg-3" title={card.title ?? title}>{card.title?.replace(/^Experiment:\s*/, "") ?? title}</span>}
        <span className="ml-auto flex shrink-0 items-center gap-1.5">
          {readOnly && <span className="rounded-sm border border-line px-1.5 text-[9.5px] font-medium text-fg-3" title="Claude is running this experiment; the app only shows it">following</span>}
          {card.step !== undefined && <span className="tnum rounded-sm border border-line bg-surface-2 px-1.5 text-[10px] text-fg-2">step {card.step}{card.steps ? ` / ${card.steps}` : ""}</span>}
          {(card.status === "waiting" || card.status === "failed" || card.status === "cancelled") && elapsed !== undefined && <span className="tnum text-[10.5px] text-fg-3" title="Since the instruction appeared">{fmtLeft(elapsed)}</span>}
        </span>
      </div>
      <div className="px-3 pb-2.5 pt-1.5">
        {card.instruction && <p className={`leading-snug ${look.loud ? "text-[16px] font-semibold" : "text-[13px] font-medium"} ${card.status === "captured" || card.status === "done" || busy && card.status !== "waiting" ? "text-fg/85" : "text-fg"}`}>{card.instruction}</p>}
        {sub && <p className="mt-1 text-[11.5px] leading-snug" style={{ color: look.tone }}>{sub}</p>}
        {card.detail && card.detail !== sub && <p className="mt-1 text-[11px] leading-snug text-fg-3">{card.detail}</p>}
        {pct !== undefined && busy && (
          <div className="mt-2">
            <div className="h-1.5 overflow-hidden rounded-sm bg-surface-3" role="progressbar" aria-valuenow={Math.round(pct * 100)} aria-valuemin={0} aria-valuemax={100} aria-label="Time left for this step">
              <div className={`h-full transition-[width] duration-500 ease-linear ${card.status === "waiting" ? "m-sampling" : ""}`} style={{ width: `${(1 - pct) * 100}%`, background: look.tone }} />
            </div>
            <p className="tnum mt-1 text-[10.5px] text-fg-3">{leftMs !== undefined ? `${fmtLeft(leftMs)} left` : ""}{card.status === "waiting" ? " · a change that reverts (hover, animation) is ignored" : ""}</p>
          </div>
        )}
        {children && <div className="mt-2.5">{children}</div>}
      </div>
    </section>
  );
}

function mixVar(v: string, pct: number): string {
  return pct >= 100 ? v : `color-mix(in oklab, ${v} ${pct}%, transparent)`;
}

// ── Step list with the repeat counter ────────────────────────────────

function StepList({ actions, current, record, local, disabled, onPick }: { actions: Action[]; current: number; record: { steps: RecordStep[] } | undefined; local: RecordStep[]; disabled: boolean; onPick: (i: number) => void }) {
  const under = actions.filter((s) => repeatsOf(s.label, record, local) < RUN_TARGET_REPEATS).length;
  return (
    <section aria-label="Actions" className="overflow-hidden rounded-lg border border-line bg-surface">
      <Header icon="list" title="Actions"><span className="text-[10.5px] text-fg-3">{under > 0 ? `${under} still need ${RUN_TARGET_REPEATS}+ repeats` : `all done ${RUN_TARGET_REPEATS}+ times`}</span></Header>
      <ol className="divide-y divide-line">
        {actions.map((s, i) => {
          const n = repeatsOf(s.label, record, local);
          const active = i === current;
          return (
            <li key={s.label}>
              <button type="button" role="radio" aria-checked={active} disabled={disabled} onClick={() => onPick(i)} data-run-step={i}
                className={`grid w-full grid-cols-[1.25rem_minmax(0,1fr)_auto] items-center gap-x-2 px-3 py-1.5 text-left disabled:cursor-default ${active ? "bg-ring/10 shadow-[inset_2px_0_0_var(--color-ring)]" : "hover:bg-surface-2"}`}>
                <span className={`tnum text-[11px] ${active ? "font-semibold text-fg" : "text-fg-3"}`}>{i + 1}</span>
                <span className="min-w-0">
                  <span className={`block truncate text-[11.5px] ${active ? "font-medium" : ""}`} title={s.instruction}>{s.instruction}</span>
                  <span className="block truncate font-code text-[10px] text-fg-3">{s.label}</span>
                </span>
                <RepeatBadge n={n} />
              </button>
            </li>
          );
        })}
      </ol>
      <p className="border-t border-line px-3 py-1.5 text-[10.5px] leading-snug text-fg-3">Do each action {RUN_TARGET_REPEATS}-3 times. What changes <em>every</em> time is the evidence; what changes only sometimes is a side effect.</p>
    </section>
  );
}

function RepeatBadge({ n }: { n: number }) {
  const tone = n >= RUN_TARGET_REPEATS ? "var(--color-success)" : n === 1 ? "var(--color-warning)" : "var(--color-fg-3)";
  const text = n === 0 ? "not yet" : n === 1 ? "once · again" : `×${n}`;
  return <span className="tnum shrink-0 rounded-sm px-1.5 py-0.5 text-[10px] font-semibold" style={{ color: tone, background: mixVar(tone, 14) }} title={n === 0 ? "Not captured yet" : n === 1 ? "Captured once: do it again so the repeats can be compared" : `Captured ${n} times`}>{text}</span>;
}

// ── Results: the latest capture in plain words, then the raw changes ─

interface ResultView {
  label: string;
  at: number;
  ok: boolean;
  headline: string;
  changes: ExperimentChange[];
  note?: string;
  error?: string;
  repeats?: number;
  consistent?: { always: EvidenceKey[]; sometimes: EvidenceKey[] };
  transient?: number;
  cancelled?: boolean;
}

function resultOf(a: RunAttempt): ResultView {
  if ("cancelled" in a.result) return { label: a.label, at: a.at, ok: false, headline: "Cancelled before anything was captured; nothing was recorded.", changes: [], cancelled: true };
  if ("error" in a.result) return { label: a.label, at: a.at, ok: false, headline: "The step ended without a result.", changes: [], error: a.result.error };
  const r = a.result;
  if (!r.changed) return { label: a.label, at: a.at, ok: false, headline: captureHeadline(r), changes: [], note: r.note, transient: r.transientChanges };
  return { label: a.label, at: a.at, ok: true, headline: captureHeadline(r), changes: r.changes, repeats: r.repeatsOfThisLabel, transient: r.transientChangesIgnored, consistent: r.consistent ? { always: r.consistent.always.map(parseEvidenceKey), sometimes: r.consistent.sometimes.map(parseEvidenceKey) } : undefined };
}

function resultOfStep(s: RecordStep, repeats: number): ResultView {
  return { label: s.label, at: Date.parse(s.at), ok: true, headline: captureHeadline({ experiment: "", label: s.label, changed: true, step: 0, repeatsOfThisLabel: repeats, changedAfterMs: s.changedAfterMs, changes: s.changes }), changes: s.changes, repeats };
}

function Results({ r }: { r: ResultView }) {
  const [raw, setRaw] = useState(false);
  const views = r.changes.map(describeChange);
  const unmapped = views.filter((v) => v.unmapped).length;
  return (
    <section aria-label="Latest capture" className="overflow-hidden rounded-lg border border-line bg-surface">
      <Header icon={r.ok ? "check" : r.cancelled ? "x" : "warning"} title={r.ok ? "Captured" : r.cancelled ? "Cancelled" : "Nothing captured"} iconClass={r.ok ? "text-success" : r.cancelled ? "text-fg-3" : "text-danger"} right={<span className="font-code text-[10.5px] text-fg-3">{r.label}{r.repeats ? ` · ${ordinal(r.repeats)} time` : ""}</span>} />
      <div className="px-3 py-2">
        <p className="text-[11.5px] leading-snug text-fg">{r.headline}</p>
        {r.error && <p className="code-wrap mt-1 text-[11px] text-danger">{r.error}</p>}
        {r.note && <p className="mt-1 text-[11px] leading-snug text-fg-2">{r.note}</p>}
        {!r.ok && !r.error && !r.cancelled && (
          <ul className="mt-1.5 list-disc space-y-0.5 pl-4 text-[11px] leading-snug text-fg-2">
            <li>Is the right panel open and the game window focused?</li>
            <li>Does the watched value follow this action at all? Try it once by hand and read it again.</li>
            {!!r.transient && r.transient > 2 && <li>{r.transient} brief changes reverted: the watch is wide; hover and animation flicker. A narrower object settles faster.</li>}
          </ul>
        )}
        {r.ok && views.length > 0 && (
          <ul className="mt-2 divide-y divide-line overflow-hidden rounded-md border border-line">
            {views.map((v, i) => (
              <li key={i} className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-2 px-2 py-1.5" title={v.full}>
                <span className="min-w-0">
                  <span className="flex items-center gap-1.5">
                    <span className="truncate font-code text-[11.5px] font-medium">{v.name}</span>
                    {v.unmapped && <span className="shrink-0 rounded-sm px-1 font-code text-[9.5px] font-semibold uppercase text-m-cand" style={{ background: mix("cand", 14) }}>unmapped</span>}
                  </span>
                  {v.note && <span className="block text-[10.5px] text-fg-3">{v.note}</span>}
                </span>
                <span className="tnum shrink-0 font-code text-[11px]"><span className="text-fg-3">{v.from}</span> <span className="text-fg-3">→</span> <span className="font-semibold">{v.to}</span></span>
                {v.bits && v.bits.length > 0 && <span className="col-span-2 flex flex-wrap items-center gap-1 pt-0.5 text-[10px] text-fg-3">bit{v.bits.length === 1 ? "" : "s"} {v.bits.map((b) => <span key={b} className="tnum rounded-sm px-1 font-code font-semibold text-m-change" style={{ background: mix("change", 14) }}>{b}</span>)}</span>}
              </li>
            ))}
          </ul>
        )}
        {r.ok && views.length === 0 && <p className="mt-1.5 text-[11px] text-fg-3">The state changed and settled, but the diff came back empty (an object moved without readable bytes, probably).</p>}
        {unmapped > 0 && <p className="mt-1.5 text-[10.5px] leading-snug text-m-cand">{unmapped} change{unmapped === 1 ? "" : "s"} in bytes the HUD doesn't map: that's a discovery. Open the object in the Struct view to see where.</p>}
        {!!r.transient && r.ok && <p className="mt-1 text-[10.5px] text-fg-3">{r.transient} brief change{r.transient === 1 ? "" : "s"} that reverted {r.transient === 1 ? "was" : "were"} ignored.</p>}
        {r.consistent && r.repeats !== undefined && r.repeats >= 2 && (
          <p className="mt-2 text-[10.5px] text-fg-3">Across {r.repeats} repeats of <span className="font-code">{r.label}</span>: {r.consistent.always.length} change{r.consistent.always.length === 1 ? "" : "s"} every time{r.consistent.sometimes.length ? `, ${r.consistent.sometimes.length} only sometimes` : ""}. The evidence list has them.</p>
        )}
        {r.ok && r.changes.length > 0 && (
          <div className="mt-2">
            <button type="button" onClick={() => setRaw((v) => !v)} aria-expanded={raw} className="flex items-center gap-1 text-[10.5px] text-fg-3 hover:text-fg"><Icon name="chevron" className={`size-3 transition-transform ${raw ? "rotate-180" : ""}`} />Raw changes (the step's result)</button>
            {raw && <pre className="code-wrap mt-1 max-h-48 overflow-auto rounded-sm bg-surface-3/60 px-2 py-1 font-code text-[10px] leading-snug text-fg-2">{JSON.stringify(r.changes, null, 1)}</pre>}
          </div>
        )}
      </div>
    </section>
  );
}

function Key({ k, tone }: { k: EvidenceKey; tone: "success" | "warning" }) {
  return (
    <span className={`mx-0.5 inline-flex max-w-full items-center gap-1 rounded-sm px-1.5 py-px font-code text-[10.5px] ${tone === "success" ? "bg-success/12 text-success" : "bg-warning/12 text-warning"}`} title={k.full}>
      <span className="truncate">{k.name}</span>
      {k.seen !== undefined && <span className="tnum opacity-80">{k.seen}/{k.of}</span>}
      {k.unmapped && <span className="rounded-sm bg-surface px-1 text-[9px] font-semibold uppercase text-m-cand">unmapped</span>}
    </span>
  );
}

// ── Evidence: what changes every time, per action ────────────────────

function Evidence({ rows, loading, full }: { rows: EvidenceRow[]; loading: boolean; full?: boolean }) {
  return (
    <section aria-label="Evidence so far" className="overflow-hidden rounded-lg border border-line bg-surface">
      <Header icon="sparkle" title={full ? "Evidence" : "Evidence so far"} right={loading ? <Icon name="sync" className="spin size-3 text-fg-3" /> : undefined}>
        {rows.length > 0 && <span className="tnum text-[10.5px] text-fg-3">{rows.reduce((n, r) => n + r.repeats, 0)} steps</span>}
      </Header>
      {rows.length === 0 ? (
        <p className="px-3 py-2.5 text-[11px] leading-snug text-fg-3">Nothing recorded yet. After the second repeat of an action, this shows what changed in every repeat (the evidence) and what only sometimes (side effects).</p>
      ) : (
        <ul className="divide-y divide-line">
          {rows.map((r) => (
            <li key={r.label} className="px-3 py-2 text-[11px]">
              <div className="flex items-center gap-2">
                <span className="font-code text-[11.5px] font-semibold">{r.label}</span>
                <RepeatBadge n={r.repeats} />
                {r.instruction && <span className="hidden min-w-0 truncate text-[10.5px] text-fg-3 xs:inline" title={r.instruction}>{r.instruction}</span>}
              </div>
              <div className="mt-1 flex flex-wrap items-baseline gap-x-1 gap-y-1 leading-relaxed">
                <span className={`shrink-0 text-[10.5px] ${r.always.length ? "text-success" : "text-fg-3"}`}>every time</span>
                {r.always.length ? r.always.map((k) => <Key key={k.full} k={k} tone="success" />) : <span className="text-fg-3">{r.repeats < 2 ? "needs a second repeat to compare" : "nothing"}</span>}
              </div>
              {r.sometimes.length > 0 && (
                <div className="mt-0.5 flex flex-wrap items-baseline gap-x-1 gap-y-1 leading-relaxed">
                  <span className="shrink-0 text-[10.5px] text-warning">sometimes</span>
                  {r.sometimes.map((k) => <Key key={k.full} k={k} tone="warning" />)}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

// ── Finish ───────────────────────────────────────────────────────────

function Finish({ store, run, host }: { store: MemoryStore; run: RunState; host: RunHost }) {
  const summary = run.summary?.experiment === run.experiment ? run.summary : undefined;
  const rows = useMemo(() => evidenceRows(summary, run.preset), [summary, run.preset]);
  const undo = store.runUndo();
  const line = store.runResultLine();
  const total = summary?.record.steps.length ?? 0;
  const card: CardModel = { status: "done", title: `Experiment: ${run.experiment}`, instruction: line, detail: run.preset?.question };
  const describe = () => store.describeRun();
  return (
    <div className="flex flex-col gap-2.5">
      <StepCard card={card} now={0} title={run.preset?.title ?? run.experiment}>
        <div className="flex flex-wrap items-center gap-1.5">
          {!run.markedDone && <SmallButton icon="check" tone="primary" onClick={() => void store.markDone()} title="guide status=done with this result, so the in-game card closes the loop">Mark done in game</SmallButton>}
          {run.markedDone && !run.following && <span className="inline-flex items-center gap-1 text-[10.5px] text-fg-3"><Icon name="check" className="size-3 text-success" />in-game card set to done</span>}
          {host.send && <SmallButton icon="send" onClick={() => { const d = describe(); if (d) host.send!(d.text); }}>Send to Claude</SmallButton>}
          {host.ask && <SmallButton icon="sparkle" tone="primary" onClick={() => { const d = describe(); host.ask!(`${d?.text ?? `Guided experiment ${run.experiment}.`} What does the evidence say, which HUD field or memory follows the action, and is anything a mapping bug? Short answer.`); }}>Ask Claude to explain</SmallButton>}
          {run.preset && !run.following && <SmallButton icon="sync" onClick={() => store.runAgain()}>Run again</SmallButton>}
          <SmallButton icon="arrowLeft" onClick={() => store.resetRun()}>Presets</SmallButton>
        </div>
      </StepCard>
      {run.summaryError && <p className="code-wrap rounded-lg border border-danger/30 bg-danger-bg px-3 py-2 text-[11.5px] text-danger">{run.summaryError}</p>}
      {total > 0 && <p className="text-[11px] text-fg-3">{total} step{total === 1 ? "" : "s"} in record <span className="font-code">{run.experiment}</span>{summary?.record.steps[0]?.game ? ` · ${summary.record.steps[0].game === "poe1" ? "PoE 1" : "PoE 2"}` : ""}{summary?.record.steps.length ? ` · ${new Date(summary.record.steps[summary.record.steps.length - 1].at).toLocaleString()}` : ""}</p>}
      <Evidence rows={rows} loading={run.summaryLoading} full />
      <section aria-label="Undo in game" className="overflow-hidden rounded-lg border border-line bg-surface">
        <Header icon="arrowLeft" title="Undo in game" />
        {undo.length === 0 ? (
          <p className="px-3 py-2.5 text-[11px] leading-snug text-fg-2"><span className="font-medium text-fg">Nothing to undo.</span> Every action was done as often as its reverse{run.preset ? "" : ", as far as the record shows"}. If you changed anything else while it ran (tab settings, affinities), put it back now.</p>
        ) : (
          <ul className="divide-y divide-line">
            {undo.map((u, i) => (
              <li key={i} className="flex items-start gap-2 px-3 py-2 text-[11.5px] leading-snug">
                <Icon name="warning" className="mt-0.5 size-3.5 shrink-0 text-warning" />
                <span className="min-w-0 flex-1">{u.text}</span>
                {u.count !== undefined && <span className="tnum shrink-0 rounded-sm bg-warning/12 px-1.5 text-[10px] font-semibold text-warning" title="How many times the action was done more often than its reverse">×{u.count}</span>}
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

// ── Follow: the agent runs it, the app shows it ──────────────────────

function Follow({ store, run, fullscreen, host, now }: { store: MemoryStore; run: RunState; fullscreen: boolean; host: RunHost; now: number }) {
  const g = run.guide;
  const record = run.summary?.experiment === run.experiment ? run.summary.record : undefined;
  const rows = useMemo(() => evidenceRows(run.summary?.experiment === run.experiment ? run.summary : undefined, run.preset), [run.summary, run.experiment, run.preset]);
  const lastStep = record?.steps[record.steps.length - 1];
  const result = lastStep ? resultOfStep(lastStep, record!.steps.filter((s) => s.label === lastStep.label).length) : undefined;
  const card = followCard(run, now);
  // The actions: the preset's, else recovered from the record's steps (and the step running now).
  const actions = useMemo<Action[]>(() => run.preset?.steps ?? actionsFromRecord(record, run.step), [run.preset, record, run.step]);
  const done = run.phase === "done";
  const banner = (
    <div className="flex items-start gap-2 rounded-lg border border-info/30 bg-info/5 px-3 py-2 text-[11.5px] leading-snug text-fg-2" role="status">
      <Icon name="sparkle" className="mt-px size-3.5 shrink-0 text-info" />
      <span className="min-w-0 flex-1">
        <span className="font-semibold text-fg">Claude is running <span className="font-code">{run.following}</span>.</span> Do what the in-game card says; this view follows along and fills in as each step lands. {run.preset ? "" : "No preset matches this record, so the actions are known from its steps only."}
      </span>
      <SmallButton icon="x" onClick={() => store.stopFollowing()} title="Stop following and go back to the presets (the run itself continues)">Stop</SmallButton>
    </div>
  );
  if (done) {
    return (
      <div className="flex flex-col gap-2.5">
        {banner}
        <Finish store={store} run={run} host={host} />
      </div>
    );
  }
  const left = (
    <div className="flex flex-col gap-2.5">
      {banner}
      <StepCard card={card} now={now} title={run.preset?.title ?? run.following} readOnly />
      {actions.length > 0 && <StepList actions={actions} current={currentStep(actions, run.stepRunning ? run.step?.label : undefined, g?.instruction, lastStep?.label)} record={record} local={[]} disabled onPick={() => {}} />}
      {g?.log && g.log.length > 0 && (
        <section aria-label="Agent log" className="overflow-hidden rounded-lg border border-line bg-surface">
          <Header icon="list" title="Agent log"><span className="tnum text-[10.5px] text-fg-3">{g.log.length}</span></Header>
          <ul className="px-3 py-1.5 font-code text-[10.5px] leading-relaxed">
            {g.log.slice(-6).map((l, i) => <li key={i} className="flex gap-2 truncate" title={l.text}><span className="tnum shrink-0 text-fg-3">{l.at}</span><span className={l.kind === "result" ? "text-success" : l.kind === "warn" ? "text-warning" : l.kind === "step" ? "text-info" : "text-fg-2"}>{l.text}</span></li>)}
          </ul>
        </section>
      )}
    </div>
  );
  const right = (
    <div className="flex flex-col gap-2.5">
      {result ? <Results r={result} /> : <section className="rounded-lg border border-dashed border-line px-3 py-3 text-[11.5px] leading-snug text-fg-3"><span className="font-medium text-fg-2">No step recorded yet.</span> The first capture shows up here within a few seconds of the card turning green.</section>}
      <Evidence rows={rows} loading={run.summaryLoading} />
    </div>
  );
  return fullscreen ? (
    <div className="grid min-h-0 flex-1 grid-cols-1 gap-3 sm:grid-cols-[minmax(0,1fr)_minmax(18rem,26rem)]">
      <div className="scroll-thin min-h-0 overflow-y-auto pr-1">{left}</div>
      <div className="scroll-thin min-h-0 overflow-y-auto pr-1">{right}</div>
    </div>
  ) : (
    <div className="flex flex-col gap-2.5">{left}{right}</div>
  );
}

/**
 * The card the follow-along shows: the step the server is running (countdown and all); once it is over, the in-game
 * card (the agent's "done" line, or the receipt), falling back to the step's own outcome for a minute.
 */
function followCard(run: RunState, now: number): CardModel {
  const s = run.step, g = run.guide;
  if (s && run.stepRunning && !stepOver(s)) return cardFromStep(s, run.stepSince);
  const ownGuide = g && (guideExperiment(g) ?? run.following) === run.following;
  if (ownGuide && (g.status === "done" || g.status === "info")) return cardFromGuide(g, run.guideSince);
  if (s && stepOver(s) && s.finishedAt && now - Date.parse(s.finishedAt) < 60_000) return cardFromStep(s, run.stepSince);
  if (g) return cardFromGuide(g, run.guideSince);
  return { status: "info", instruction: "Waiting for the in-game card…" };
}

/** Which action the agent's run is on: the running step's label, else the card's instruction, else the last recorded label. */
function currentStep(actions: Action[], runningLabel: string | undefined, instruction: string | null | undefined, lastLabel: string | undefined): number {
  const byRunning = runningLabel ? actions.findIndex((s) => s.label === runningLabel) : -1;
  if (byRunning >= 0) return byRunning;
  const byText = instruction ? actions.findIndex((s) => s.instruction === instruction) : -1;
  if (byText >= 0) return byText;
  const byLabel = lastLabel ? actions.findIndex((s) => s.label === lastLabel) : -1;
  return byLabel >= 0 ? byLabel : 0;
}

// ── Bits ─────────────────────────────────────────────────────────────

function Header({ icon, title, children, right, iconClass = "text-fg-3" }: { icon: IconName; title: string; children?: ReactNode; right?: ReactNode; iconClass?: string }) {
  return (
    <div className="flex items-center gap-1.5 border-b border-line px-2.5 py-1.5">
      <Icon name={icon} className={`size-3.5 shrink-0 ${iconClass}`} />
      <span className="truncate text-[11.5px] font-semibold">{title}</span>
      {children}
      {right && <span className="ml-auto flex shrink-0 items-center gap-1">{right}</span>}
    </div>
  );
}

function ordinal(n: number): string {
  return n === 1 ? "1st" : n === 2 ? "2nd" : n === 3 ? "3rd" : `${n}th`;
}

function agoText(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return "";
  if (ms < 60_000) return "just now";
  if (ms < 3_600_000) return `${Math.round(ms / 60_000)} min ago`;
  if (ms < 86_400_000) return `${Math.round(ms / 3_600_000)} h ago`;
  return `${Math.round(ms / 86_400_000)} d ago`;
}
