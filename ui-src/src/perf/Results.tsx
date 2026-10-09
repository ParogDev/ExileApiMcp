import { useState, type ReactNode } from "react";
import { EmptyState, SectionLabel } from "../components";
import { SmallButton } from "../explorer/Tree";
import { Icon } from "../icons";
import { fmtBytes, fmtElapsed, fmtInt, fmtKB, fmtMs, fmtNs, fmtUs, methodLabel } from "./model";
import { EXPECTED_ACTION_MS, type ActionRun } from "./store";
import type { LintResult, ProfileResult } from "./types";

// The result of a one-click next step, in-panel: a profile as a compact method table (by time or by allocation),
// a lint as the list of expensive calls with advice, anything else (overlay_accuracy) as its numbers. Each run
// is a card with its loading state (elapsed against what the tool usually takes), its error, or its empty state.

export function RunCard({ run, now, onDismiss, onRetry, onAsk }: { run: ActionRun; now: number; onDismiss: () => void; onRetry: () => void; onAsk?: (text: string) => void }) {
  const [open, setOpen] = useState(true);
  const elapsed = (run.endedAt ?? now) - run.startedAt;
  const expected = EXPECTED_ACTION_MS[run.action.tool] ?? 3000;
  const icon = run.action.tool === "profile_plugin" ? "activity" : run.action.tool === "hud_plugin_lint" ? "code" : "target";
  return (
    <section className="rounded-lg border border-line bg-surface" aria-label={run.action.label} aria-busy={run.status === "running"}>
      <header className="flex h-8 items-center gap-2 px-2.5">
        <button type="button" onClick={() => setOpen((o) => !o)} aria-expanded={open} className="flex min-w-0 flex-1 items-center gap-1.5 text-left text-[12px] font-medium">
          <Icon name="chevron" className={`size-3 shrink-0 text-fg-3 transition-transform ${open ? "" : "-rotate-90"}`} />
          <Icon name={icon} className="size-3.5 shrink-0 text-fg-3" />
          <span className="truncate">{run.action.label}</span>
          <span className="truncate font-code text-[10.5px] font-normal text-fg-3">{run.action.tool}</span>
        </button>
        {run.status === "running" ? (
          <span className="tnum flex items-center gap-1 text-[10.5px] text-fg-3"><Icon name="sync" className="spin size-3" />{fmtElapsed(elapsed)}</span>
        ) : (
          <span className="tnum text-[10.5px] text-fg-3" title="How long the call took">{fmtElapsed(elapsed)}</span>
        )}
        <button type="button" onClick={onDismiss} aria-label="Dismiss result" className="grid size-5 place-items-center rounded text-fg-3 hover:bg-surface-3 hover:text-fg"><Icon name="x" className="size-3" /></button>
      </header>
      {open && (
        <div className="border-t border-line px-2.5 py-2">
          {run.status === "running" && <Running elapsed={elapsed} expected={expected} tool={run.action.tool} />}
          {run.status === "error" && (
            <div role="alert" className="rounded-md border border-danger/30 bg-danger-bg px-2.5 py-2 text-[11.5px] text-danger">
              <div className="flex items-start gap-2">
                <Icon name="warning" className="mt-px size-3.5 shrink-0" />
                <div className="min-w-0 flex-1">
                  <div className="font-semibold">{errorTitle(run.error)}</div>
                  <div className="mt-0.5 break-words text-fg-2">{run.error}</div>
                </div>
              </div>
              <div className="mt-2"><SmallButton icon="sync" onClick={onRetry}>Try again</SmallButton></div>
            </div>
          )}
          {run.status === "done" && <Result run={run} onAsk={onAsk} />}
        </div>
      )}
    </section>
  );
}

function errorTitle(msg: string | undefined): string {
  if (!msg) return "The tool failed";
  if (/instrumentation/i.test(msg)) return "HUD instrumentation is off";
  if (/No loaded plugin/i.test(msg)) return "No such plugin";
  if (/unreachable|not reachable|refused/i.test(msg)) return "HUD bridge unreachable";
  if (/busy/i.test(msg)) return "The HUD is busy with another run";
  return "The tool failed";
}

function Running({ elapsed, expected, tool }: { elapsed: number; expected: number; tool: string }) {
  const frac = Math.min(1, elapsed / expected);
  const what = tool === "profile_plugin" ? "Patching the plugin's methods and timing them for 4 s" : tool === "hud_plugin_lint" ? "Reading the plugin's DLL for expensive HUD calls" : tool === "overlay_accuracy" ? "Projecting the nearest players twice per frame for 5 s" : "Running";
  return (
    <div className="text-[11.5px] text-fg-2" role="status">
      <div className="flex items-center justify-between"><span>{what}...</span>{elapsed > expected && <span className="text-fg-3">taking longer than usual</span>}</div>
      <div className="mt-1.5 h-1 overflow-hidden rounded-sm bg-surface-3">
        <div className={`h-full rounded-sm bg-ring transition-[width] duration-300 ${elapsed > expected ? "shimmer" : ""}`} style={{ width: `${Math.max(3, frac * 100)}%` }} />
      </div>
    </div>
  );
}

function Result({ run, onAsk }: { run: ActionRun; onAsk?: (text: string) => void }) {
  const d = run.data as Record<string, unknown> | undefined;
  if (run.action.tool === "profile_plugin" && d && Array.isArray(d.top)) return <Profile r={d as unknown as ProfileResult} onAsk={onAsk} />;
  if (run.action.tool === "hud_plugin_lint" && d && Array.isArray(d.plugins)) return <Lint r={d as unknown as LintResult} onAsk={onAsk} />;
  return <Generic data={run.data} text={run.text} />;
}

// ── profile_plugin ───────────────────────────────────────────────────

function Profile({ r, onAsk }: { r: ProfileResult; onAsk?: (text: string) => void }) {
  const [by, setBy] = useState<"time" | "alloc">("time");
  const top = by === "time" ? r.top : (r.topAlloc ?? []);
  const maxSelf = Math.max(0.001, ...r.top.map((m) => m.selfMsPerSecond));
  const maxAlloc = Math.max(0.001, ...(r.topAlloc ?? []).map((m) => m.allocSelfKBPerSecond));
  if (!r.top.length) {
    return <EmptyState icon="activity" title="Nothing ran" className="py-3">The plugin's methods were patched but none was called in {Math.round(r.durationMs / 1000)} s: it may be idle outside a map, or disabled.</EmptyState>;
  }
  return (
    <div className="text-[11.5px]">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-0.5">
        <span><span className="tnum font-semibold">{fmtMs(r.selfTotalMsPerSecond)}</span> <span className="text-fg-3">per second in {r.plugin}</span></span>
        {r.allocTotalKBPerSecond !== undefined && <span><span className="tnum font-semibold">{fmtKB(r.allocTotalKBPerSecond)}</span> <span className="text-fg-3">/ s allocated</span></span>}
        <span className="tnum text-fg-3">{fmtInt(r.calls)} calls · {r.methodsPatched} methods{r.methodsCapped ? " (capped)" : ""} · {Math.round(r.durationMs / 1000)} s</span>
      </div>
      <div className="mt-2 flex items-center gap-1">
        <SmallButton active={by === "time"} onClick={() => setBy("time")}>By self time</SmallButton>
        <SmallButton active={by === "alloc"} onClick={() => setBy("alloc")} disabled={!r.topAlloc?.length}>By allocation</SmallButton>
        <span className="ml-auto text-[10.5px] text-fg-3" title={r.note}>self = excluding profiled callees</span>
      </div>
      <div className="mt-1.5 overflow-hidden rounded-md border border-line">
        <div className={`grid gap-x-2 bg-surface-2 px-2 py-1 text-[10.5px] text-fg-3 ${by === "time" ? "grid-cols-[minmax(0,1fr)_4.2rem_3.6rem_4rem]" : "grid-cols-[minmax(0,1fr)_4.6rem_3.6rem_4.2rem]"}`} role="row">
          <span>method</span>
          {by === "time" ? <><span className="text-right">self ms/s</span><span className="text-right">calls</span><span className="text-right">us/call</span></>
            : <><span className="text-right">KB/s</span><span className="text-right">calls</span><span className="text-right">per call</span></>}
        </div>
        <ul className="max-h-64 overflow-auto scroll-thin">
          {top.slice(0, 15).map((m, i) => {
            const frac = by === "time" ? (m as { selfMsPerSecond: number }).selfMsPerSecond / maxSelf : m.allocSelfKBPerSecond / maxAlloc;
            return (
              <li key={`${m.method}-${i}`} className={`relative grid h-6 items-center gap-x-2 px-2 text-[11px] ${by === "time" ? "grid-cols-[minmax(0,1fr)_4.2rem_3.6rem_4rem]" : "grid-cols-[minmax(0,1fr)_4.6rem_3.6rem_4.2rem]"}`} title={m.method}>
                <span className={`pointer-events-none absolute inset-y-1 left-0 rounded-r-[2px] ${by === "time" ? "bg-p-plugins" : "bg-p-gc"} opacity-10`} style={{ width: `${Math.max(1, frac * 100)}%` }} aria-hidden />
                <span className="relative truncate font-code">{methodLabel(m.method)}</span>
                {by === "time" ? (
                  <>
                    <span className="tnum relative text-right font-medium">{fmtMs((m as { selfMsPerSecond: number }).selfMsPerSecond, false)}</span>
                    <span className="tnum relative text-right text-fg-2">{fmtInt(m.calls)}</span>
                    <span className="tnum relative text-right text-fg-2">{fmtUs((m as { selfUsPerCall: number }).selfUsPerCall).replace(" us", "")}</span>
                  </>
                ) : (
                  <>
                    <span className="tnum relative text-right font-medium">{fmtKB(m.allocSelfKBPerSecond)}</span>
                    <span className="tnum relative text-right text-fg-2">{fmtInt(m.calls)}</span>
                    <span className="tnum relative text-right text-fg-2">{fmtBytes((m as { bytesPerCall: number }).bytesPerCall)}</span>
                  </>
                )}
              </li>
            );
          })}
        </ul>
      </div>
      {r.hookOverhead && <p className="mt-1 text-[10.5px] text-fg-3">Hook overhead of {fmtUs(r.hookOverhead.usPerCall)} and {fmtBytes(r.hookOverhead.bytesPerCall)} per call already subtracted.{r.refused?.length ? ` ${r.refused.length} methods refused patching.` : ""}</p>}
      {onAsk && (
        <div className="mt-2">
          <SmallButton icon="sparkle" onClick={() => onAsk(profileQuestion(r))}>Ask Claude how to cut this</SmallButton>
        </div>
      )}
    </div>
  );
}

function profileQuestion(r: ProfileResult): string {
  const top = r.top.slice(0, 5).map((m) => `${m.method} ${m.selfMsPerSecond.toFixed(1)} ms/s, ${m.calls} calls, ${m.allocSelfKBPerSecond.toFixed(0)} KB/s`).join("; ");
  return `profile_plugin for ${r.plugin}: ${r.selfTotalMsPerSecond.toFixed(1)} ms/s self time${r.allocTotalKBPerSecond !== undefined ? `, ${r.allocTotalKBPerSecond.toFixed(0)} KB/s allocated` : ""}. Top methods: ${top}. How do I cut the biggest one? Use the optimize_plugin prompt's approach.`;
}

// ── hud_plugin_lint ──────────────────────────────────────────────────

function Lint({ r, onAsk }: { r: LintResult; onAsk?: (text: string) => void }) {
  const entries = r.plugins.flatMap((p) => p.findings.map((f) => ({ plugin: p.plugin, f })));
  if (!entries.length) {
    const names = r.plugins.map((p) => p.plugin).join(", ") || "the plugin";
    return <EmptyState icon="check" title="Nothing expensive found" className="py-3">No costly HUD API call in {names}'s per-frame code. If it is still slow, profile it: the cost is in its own logic.</EmptyState>;
  }
  const sorted = [...entries].sort((a, b) => (b.f.inLoop ? 1 : 0) * 1e9 + b.f.costNs * b.f.count - ((a.f.inLoop ? 1 : 0) * 1e9 + a.f.costNs * a.f.count));
  const loops = sorted.filter((e) => e.f.inLoop).length;
  return (
    <div className="text-[11.5px]">
      <p className="text-fg-2"><span className="tnum font-semibold text-fg">{entries.length}</span> expensive call{entries.length === 1 ? "" : "s"}{loops ? <>, <span className="tnum font-semibold text-fg">{loops}</span> inside a loop (per item per frame)</> : null}. Costs are per call, cold.</p>
      <ul className="mt-1.5 divide-y divide-line rounded-md border border-line">
        {sorted.slice(0, 20).map(({ plugin, f }, i) => (
          <li key={i} className="px-2 py-1.5">
            <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
              <span className="truncate font-code text-[11px]" title={`${plugin}: ${f.method}`}>{f.method}</span>
              <span className="text-fg-3">calls</span>
              <span className="truncate font-code text-[11px] font-medium" title={f.call}>{f.call}</span>
              {f.count > 1 && <span className="tnum text-fg-3">x{f.count}</span>}
              {f.inLoop && <span className="rounded border border-warning/40 bg-warning/10 px-1 text-[10px] font-medium text-warning">in a loop</span>}
              {f.costNs > 0 && <span className="tnum ml-auto text-fg-3">~{fmtNs(f.costNs)}</span>}
            </div>
            <p className="mt-0.5 text-fg-2">{f.advice}</p>
          </li>
        ))}
      </ul>
      {sorted.length > 20 && <p className="mt-1 text-[10.5px] text-fg-3">{sorted.length - 20} more in the tool's text result.</p>}
      {onAsk && (
        <div className="mt-2">
          <SmallButton icon="sparkle" onClick={() => onAsk(`hud_plugin_lint found ${entries.length} expensive HUD calls in ${r.plugins.map((p) => p.plugin).join(", ")} (${loops} in loops): ${sorted.slice(0, 5).map((e) => `${e.f.method} -> ${e.f.call}${e.f.inLoop ? " in a loop" : ""}`).join("; ")}. Which should I fix first, and how?`)}>Ask Claude which to fix first</SmallButton>
        </div>
      )}
    </div>
  );
}

// ── Anything else (overlay_accuracy and future tools) ────────────────

function humanKey(k: string): string {
  return k.replace(/./g, " ").replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/Px/g, "px").toLowerCase();
}

function Generic({ data, text }: { data: unknown; text?: string }) {
  const rows: { k: string; v: ReactNode }[] = [];
  const walk = (o: unknown, prefix: string) => {
    if (!o || typeof o !== "object" || Array.isArray(o)) return;
    for (const [k, v] of Object.entries(o as Record<string, unknown>)) {
      const key = prefix ? `${prefix}.${k}` : k;
      if (typeof v === "number") rows.push({ k: key, v: <span className="tnum">{Number.isInteger(v) ? fmtInt(v) : v.toFixed(Math.abs(v) < 10 ? 2 : 1)}</span> });
      else if (typeof v === "string" || typeof v === "boolean") rows.push({ k: key, v: String(v) });
      else if (v && typeof v === "object" && !Array.isArray(v) && rows.length < 40) walk(v, key);
    }
  };
  walk(data, "");
  if (!rows.length) return <pre className="code-wrap max-h-48 overflow-auto font-code text-[10.5px] text-fg-2">{text ?? JSON.stringify(data, null, 1)}</pre>;
  const note = (data as { note?: string } | undefined)?.note;
  return (
    <div className="text-[11.5px]">
      {note && <p className="mb-1.5 text-fg-2">{note}</p>}
      <dl className="grid grid-cols-[minmax(0,1fr)_auto] gap-x-3 gap-y-0.5">
        {rows.filter((r) => r.k !== "note").slice(0, 40).map((r) => (
          <div key={r.k} className="contents"><dt className="truncate text-fg-3" title={r.k}>{humanKey(r.k)}</dt><dd className="text-right">{r.v}</dd></div>
        ))}
      </dl>
    </div>
  );
}

export function RunsSection({ children, count }: { children: ReactNode; count: number }) {
  if (!count) return null;
  return (
    <section aria-label="Results" className="flex flex-col gap-2">
      <SectionLabel>Results <span className="tnum font-normal">{count}</span></SectionLabel>
      {children}
    </section>
  );
}
