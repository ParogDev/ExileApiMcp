import { useMemo } from "react";
import { EmptyState, IconButton, SectionLabel } from "../components";
import { SmallButton } from "../explorer/Tree";
import { Icon } from "../icons";
import { isToCheck, parseBitTable, parseWhere, statusOn, subjectLabel } from "./findingsModel";
import { mix } from "./paint";
import type { FndState, MemoryStore, Snapshot } from "./store";
import type { Finding, FindingGame, FindingStatus, Game, VerifyResult } from "./types";

// Findings: facts about game data with a status per game. The matrix shows PoE1 and PoE2 side by side, so
// "verified there, unverified here" stands out and can be verified in one click (verify_finding).

export interface FindingsHost { ask?: (text: string) => void }

const GAMES: Game[] = ["poe1", "poe2"];

export function Findings({ store, snap, fullscreen, host }: { store: MemoryStore; snap: Snapshot; fullscreen: boolean; host: FindingsHost }) {
  const fnd = snap.fnd;
  const game = snap.game;
  const all = fnd.data?.findings ?? [];
  const q = fnd.filter.trim().toLowerCase();
  const rows = useMemo(() => all.filter((f) => {
    if (q && !`${f.id} ${f.title} ${f.subject}`.toLowerCase().includes(q)) return false;
    if (fnd.status === "all") return true;
    if (fnd.status === "toCheck") return !!game && isToCheck(f, game);
    return !!game && statusOn(f, game) === fnd.status;
  }), [all, q, fnd.status, game]);
  const toCheck = game ? all.filter((f) => isToCheck(f, game)) : [];
  const counts = (g: Game) => ({ verified: all.filter((f) => statusOn(f, g) === "verified").length, differs: all.filter((f) => statusOn(f, g) === "differs").length, unverified: all.filter((f) => statusOn(f, g) === "unverified").length });

  return (
    <section aria-label="Findings" className={`flex min-h-0 flex-col overflow-hidden rounded-lg border border-line bg-surface ${fullscreen ? "flex-1" : ""}`}>
      <div className="flex flex-wrap items-center gap-1.5 border-b border-line bg-surface-2 px-2 py-1.5">
        <div className="relative min-w-0 flex-1 basis-[10rem]">
          <Icon name="search" className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-fg-3" />
          <input type="search" value={fnd.filter} onChange={(e) => store.setFindingsFilter(e.target.value)} placeholder="Filter findings" aria-label="Filter findings by id, title or subject"
            className="h-7 w-full rounded-md border border-transparent bg-surface pl-7 pr-2 text-[12px] placeholder:text-fg-3 focus:border-ring focus:outline-none [&::-webkit-search-cancel-button]:hidden" />
        </div>
        <div className="flex overflow-hidden rounded-md border border-line" role="radiogroup" aria-label="Status filter">
          {([["all", "All"], ["toCheck", `To check${toCheck.length ? ` ${toCheck.length}` : ""}`], ["verified", "Verified"], ["unverified", "Unverified"], ["differs", "Differs"]] as [FndState["status"], string][]).map(([s, label]) => (
            <button key={s} type="button" role="radio" aria-checked={fnd.status === s} disabled={s !== "all" && !game} onClick={() => store.setFindingsStatus(s)}
              className={`h-7 px-2 text-[11px] font-medium disabled:opacity-40 ${fnd.status === s ? "bg-fg text-surface" : "text-fg-2 hover:bg-surface-3 hover:text-fg"} ${s === "toCheck" && toCheck.length && fnd.status !== s ? "text-info" : ""}`}>{label}</button>
          ))}
        </div>
        <IconButton icon="sync" label="Reload findings" size="sm" disabled={fnd.loading} iconClass={fnd.loading ? "spin" : ""} onClick={() => void store.loadFindings()} />
      </div>

      {game && toCheck.length > 0 && fnd.status !== "toCheck" && (
        <div className="flex items-center gap-2 border-b border-line bg-info/5 px-3 py-1.5 text-[11px] text-fg-2">
          <Icon name="info" className="size-3.5 shrink-0 text-info" />
          <span><span className="font-semibold text-fg">{toCheck.length}</span> finding{toCheck.length === 1 ? "" : "s"} verified on the other game but not on {gameName(game)}: a hypothesis on {gameName(game)} until checked.</span>
          <button type="button" onClick={() => store.setFindingsStatus("toCheck")} className="ml-auto shrink-0 text-[11px] font-medium text-info underline-offset-2 hover:underline">Show them</button>
        </div>
      )}

      <div className="hidden items-center gap-x-4 border-b border-line px-3 py-1 text-[10px] text-fg-3 sm:flex">
        {GAMES.map((g) => { const c = counts(g); return <span key={g} className="tnum"><span className="font-semibold text-fg-2">{gameName(g)}</span> · {c.verified} verified · {c.differs} differs · {c.unverified} unverified</span>; })}
      </div>

      {fnd.error && <p className="code-wrap px-3 py-2 text-[11.5px] text-danger">{fnd.error}</p>}
      {fnd.loading && !fnd.data && <div className="space-y-1.5 p-3" aria-hidden>{Array.from({ length: 6 }, (_, i) => <div key={i} className="shimmer h-2.5 rounded" style={{ width: `${90 - (i % 3) * 12}%` }} />)}</div>}
      {fnd.data && rows.length === 0 && <EmptyState icon="search" title="No finding matches" className="py-6">{q ? `Nothing matches “${fnd.filter}”.` : "Nothing with this status."}</EmptyState>}

      <div className={`scroll-thin min-h-0 overflow-y-auto ${fullscreen ? "flex-1" : "max-h-[32rem]"}`}>
        <div className="sticky top-0 z-10 grid grid-cols-[minmax(0,1fr)_4.5rem_4.5rem_1.5rem] items-center gap-x-2 border-b border-line bg-surface px-3 py-1 text-[10px] font-semibold uppercase tracking-wide text-fg-3 sm:grid-cols-[minmax(0,1fr)_5.5rem_5.5rem_5rem_1.5rem]">
          <span>Finding</span><span className="text-center">PoE 1</span><span className="text-center">PoE 2</span><span className="hidden text-center sm:block">Check</span><span />
        </div>
        <ul className="divide-y divide-line">
          {rows.map((f) => <Row key={f.id} f={f} store={store} fnd={fnd} game={game} host={host} struct={structOf(snap)} />)}
        </ul>
      </div>
      {fnd.data?.about && <p className="border-t border-line px-3 py-1.5 text-[10px] leading-snug text-fg-3">{fnd.data.about}</p>}
    </section>
  );
}

function structOf(snap: Snapshot): string | undefined {
  const v = snap.views[snap.index];
  return v?.data && "struct" in v.data ? v.data.struct : undefined;
}

export function gameName(g: Game): string { return g === "poe1" ? "PoE 1" : "PoE 2"; }

function Row({ f, store, fnd, game, host, struct }: { f: Finding; store: MemoryStore; fnd: FndState; game?: Game; host: FindingsHost; struct?: string }) {
  const open = fnd.open.has(f.id);
  const toCheck = !!game && isToCheck(f, game);
  const verifying = fnd.verifying.has(f.id);
  const verified = fnd.verified.get(f.id);
  const onStruct = !!struct && f.subject.toLowerCase().includes(struct.split(".").pop()!.toLowerCase().replace(/offsets$/, ""));
  const canVerify = !!game && f.check.kind !== "manual";
  return (
    <li className={toCheck ? "bg-info/5" : ""}>
      <div className="grid grid-cols-[minmax(0,1fr)_4.5rem_4.5rem_1.5rem] items-center gap-x-2 px-3 py-1.5 sm:grid-cols-[minmax(0,1fr)_5.5rem_5.5rem_5rem_1.5rem]">
        <button type="button" onClick={() => store.toggleFinding(f.id)} aria-expanded={open} className="min-w-0 text-left">
          <div className="flex items-center gap-1.5">
            {toCheck && <span className="size-1.5 shrink-0 rounded-full bg-info" title="Verified on the other game, not on this one" />}
            <span className="truncate text-[12px] font-medium">{f.title}</span>
          </div>
          <div className="flex items-center gap-1.5 truncate text-[10.5px] text-fg-3">
            <span className="truncate font-code">{subjectLabel(f.subject)}</span>
            <span className="truncate font-code opacity-70">{f.id}</span>
            {onStruct && <span className="shrink-0 rounded px-1 text-[9.5px] font-semibold text-m-cand" style={{ background: mix("cand", 14) }} title={`About ${struct}, the struct open in the Struct view (not the game screen). Expand for "show in struct".`}>this struct</span>}
          </div>
        </button>
        {GAMES.map((g) => <StatusCell key={g} f={f} g={g} current={game} />)}
        <span className="hidden justify-center sm:flex"><span className="rounded bg-surface-3 px-1.5 py-0.5 font-code text-[10px] text-fg-2" title={f.check.kind === "manual" ? "An experiment to run with the user" : `Re-runs automatically on the live game (${f.check.kind})`}>{f.check.kind}</span></span>
        <IconButton icon="chevron" label={open ? "Collapse" : "Expand"} size="sm" onClick={() => store.toggleFinding(f.id)} iconClass={`transition-transform ${open ? "rotate-180" : ""}`} />
      </div>
      {open && (
        <div className="fade-in border-t border-line/60 px-3 py-2 text-[11px]">
          <div className="grid gap-3 sm:grid-cols-2">
            {GAMES.map((g) => <GameDetail key={g} g={g} d={f.games[g]} current={game} onShow={onStruct ? (off) => store.showInStruct(off) : undefined} />)}
          </div>
          <div className="mt-2 flex flex-wrap items-center gap-1.5">
            {canVerify && <SmallButton icon={verifying ? "sync" : "check"} tone={toCheck ? "primary" : "default"} disabled={verifying} onClick={() => void store.verify(f.id)} title={`verify_finding ${f.id} on ${gameName(game!)}`}>{verifying ? "Verifying…" : `Verify on ${gameName(game!)}`}</SmallButton>}
            {!canVerify && game && <SmallButton icon="info" onClick={() => void store.verify(f.id)} disabled={verifying}>How to verify</SmallButton>}
            {host.ask && <SmallButton icon="sparkle" tone={canVerify ? "default" : "primary"} onClick={() => host.ask!(askText(f, game))}>{f.check.kind === "manual" ? "Ask Claude to run the experiment" : "Ask Claude about this"}</SmallButton>}
            <span className="ml-auto font-code text-[10px] text-fg-3">{f.check.path ?? f.check.expression ?? ""}{f.check.label ? ` · ${f.check.label}` : ""}{f.check.expect ? ` · expect ${f.check.expect}` : ""}</span>
          </div>
          {f.check.kind === "manual" && f.check.how && <p className="mt-1.5 rounded-md bg-surface-3/60 px-2.5 py-1.5 leading-snug text-fg-2"><span className="font-semibold text-fg">Experiment: </span>{f.check.how}</p>}
          {verified && <Verdict v={verified} store={store} />}
        </div>
      )}
    </li>
  );
}

const STATUS_STYLE: Record<FindingStatus, { cls: string; label: string }> = {
  verified: { cls: "border-success/30 bg-success/10 text-success", label: "verified" },
  differs: { cls: "border-warning/30 bg-warning/10 text-warning", label: "differs" },
  unverified: { cls: "border-line text-fg-3", label: "unverified" },
  "n/a": { cls: "border-transparent text-fg-3/60", label: "n/a" },
};

function StatusCell({ f, g, current }: { f: Finding; g: Game; current?: Game }) {
  const s = statusOn(f, g);
  const st = STATUS_STYLE[s];
  const d = f.games[g];
  const hypothesis = s === "unverified" && isToCheck(f, g);
  return (
    <span className="flex justify-center" title={[`${gameName(g)}: ${s}`, d?.date, d?.where && `@ ${d.where}`, d?.note].filter(Boolean).join("\n")}>
      <span className={`tnum inline-flex h-5 items-center gap-1 rounded-full border px-1.5 text-[10px] font-medium ${st.cls} ${g === current ? "" : "opacity-80"} ${hypothesis ? "border-dashed" : ""}`}>
        {s === "verified" && <Icon name="check" className="size-2.5" />}
        {s === "differs" && <Icon name="warning" className="size-2.5" />}
        <span className="hidden sm:inline">{hypothesis ? "hypothesis" : st.label}</span>
        <span className="sm:hidden">{s === "verified" ? "ok" : s === "differs" ? "≠" : hypothesis ? "?" : "–"}</span>
      </span>
    </span>
  );
}

function GameDetail({ g, d, current, onShow }: { g: Game; d?: FindingGame; current?: Game; onShow?: (off: number) => void }) {
  const s = d?.status ?? "unverified";
  const table = d?.where ? parseBitTable(d.where) : undefined;
  const offs = d?.where && !table ? parseWhere(d.where) : [];
  return (
    <div className={`rounded-md border px-2.5 py-2 ${g === current ? "border-line-2 bg-surface" : "border-line bg-surface-2/50"}`}>
      <div className="flex items-center gap-1.5">
        <span className="text-[10.5px] font-semibold uppercase tracking-wide text-fg-3">{gameName(g)}</span>
        <span className={`tnum rounded-full border px-1.5 text-[10px] font-medium ${STATUS_STYLE[s].cls}`}>{STATUS_STYLE[s].label}</span>
        {d?.date && <span className="tnum ml-auto text-[10px] text-fg-3">{d.date}</span>}
      </div>
      {d?.where && !table && (
        <p className="code-wrap mt-1 font-code text-[11px]">
          <span className="text-fg-3">where </span>{d.where}
          {onShow && offs.length > 0 && <button type="button" className="ml-1.5 text-[10px] text-info underline-offset-2 hover:underline" onClick={() => onShow(offs[0].off)}>show in struct</button>}
        </p>
      )}
      {table && (
        <div className="mt-1">
          <span className="text-[10px] text-fg-3">bit table</span>
          <ul className="mt-0.5 flex flex-wrap gap-1">
            {[...table.entries()].sort((a, b) => a[0] - b[0]).map(([bit, name]) => <li key={bit} className="tnum rounded px-1 font-code text-[10px]" style={{ background: mix("cand", 14) }}><span className="text-fg-3">{bit} </span>{name}</li>)}
          </ul>
        </div>
      )}
      {d?.evidence && <p className="mt-1 leading-snug text-fg-2"><span className="text-fg-3">evidence </span>{d.evidence}</p>}
      {d?.note && <p className="mt-1 leading-snug text-fg-2"><span className="text-fg-3">note </span>{d.note}</p>}
      {!d?.where && !d?.evidence && !d?.note && <p className="mt-1 text-fg-3">Nothing recorded for this game.</p>}
    </div>
  );
}

const VERDICT: Record<NonNullable<VerifyResult["verdict"]>, { cls: string; icon: "check" | "warning" | "x" | "arrowUpRight"; label: string }> = {
  pass: { cls: "text-success", icon: "check", label: "pass" },
  moved: { cls: "text-warning", icon: "arrowUpRight", label: "moved" },
  differs: { cls: "text-warning", icon: "warning", label: "differs" },
  fail: { cls: "text-danger", icon: "x", label: "fail" },
};

function Verdict({ v, store }: { v: VerifyResult | { error: string }; store: MemoryStore }) {
  if ("error" in v) return <p className="code-wrap mt-2 text-danger"><Icon name="warning" className="mr-1 inline size-3.5 align-[-2px]" />{v.error}</p>;
  const copy = async (text: string) => { try { await navigator.clipboard.writeText(text); store.toast("info", "Copied the record for findings.json"); } catch { store.toast("error", "Clipboard blocked by the host"); } };
  if (v.howToVerify) {
    return (
      <div className="mt-2 rounded-md border border-line bg-surface-2/60 px-2.5 py-2">
        <SectionLabel>How to verify on {gameName(v.game)}</SectionLabel>
        <p className="mt-1 leading-snug text-fg-2">{v.howToVerify}</p>
        {v.next && <p className="mt-1 text-[10.5px] text-fg-3">{v.next}</p>}
      </div>
    );
  }
  const vd = v.verdict ? VERDICT[v.verdict] : undefined;
  return (
    <div className="mt-2 rounded-md border px-2.5 py-2" style={{ borderColor: v.verdict === "pass" ? "color-mix(in oklab, var(--color-success) 40%, transparent)" : v.verdict === "fail" ? "color-mix(in oklab, var(--color-danger) 40%, transparent)" : "color-mix(in oklab, var(--color-warning) 40%, transparent)" }}>
      <div className="flex items-center gap-1.5">
        {vd && <span className={`inline-flex items-center gap-1 text-[11.5px] font-semibold ${vd.cls}`}><Icon name={vd.icon} className="size-3.5" />{vd.label}</span>}
        <span className="text-[10.5px] text-fg-3">on {gameName(v.game)} · {v.kind}</span>
        {v.where && <span className="tnum ml-auto font-code text-[11px]">{v.where}</span>}
      </div>
      {v.evidence && <p className="mt-1 leading-snug text-fg-2">{v.evidence}</p>}
      {v.next && <p className="mt-1 leading-snug text-fg">{v.next}</p>}
      {v.record && (
        <div className="mt-1.5 flex items-start gap-1.5">
          <pre className="code-wrap min-w-0 flex-1 rounded bg-surface-3/60 px-2 py-1 font-code text-[10px] leading-snug text-fg-2">{JSON.stringify({ [v.game]: v.record })}</pre>
          <IconButton icon="copy" label="Copy the record for Knowledge/findings.json" size="sm" onClick={() => copy(JSON.stringify({ [v.game]: v.record }, null, 2))} />
        </div>
      )}
    </div>
  );
}

function askText(f: Finding, game?: Game): string {
  const g = game ? gameName(game) : "this game";
  const d = game ? f.games[game] : undefined;
  if (f.check.kind === "manual") return `Run the experiment for finding "${f.id}" (${f.title}) on ${g} with me: ${f.check.how ?? "see the findings tool"}. Tell me what to do in game at each step and record the result.`;
  return `Finding "${f.id}": ${f.title}. On ${g} it is ${d?.status ?? "unverified"}${d?.where ? ` at ${d.where}` : ""}. ${Object.entries(f.games).filter(([k]) => k !== game).map(([k, v]) => `On ${gameName(k as Game)}: ${v?.status}${v?.where ? ` at ${v.where}` : ""}.`).join(" ")} Verify it here (verify_finding ${f.id}) and explain what it means for a plugin. Short answer.`;
}
