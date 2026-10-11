// F8 sessions: every F8 press of Whats A Route (PoE2) and the Claude session the dispatcher started for it
// (feedback_sessions). The dispatcher's state first (listening, idle, broken with its fix, or not running), then the
// presses newest first with a detail pane: the player's note, the answer, status and timings, cost and usage, the
// links (a Remote Control URL when the dispatcher recorded one, the transcript, the worktree, the handler's folder),
// what was said on the route card, the brief collapsed, and every field this page has no view for. Standalone the
// page follows exile://feedback/sessions (the server watches the dispatcher's files); inside a host it polls every 3 s.

import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Banner, EmptyState, useNow } from "../../components";
import { Icon, type IconName } from "../../icons";
import type { ControlStore, Snapshot } from "../store";
import type { FeedbackDispatcherSummary, FeedbackReply, FeedbackSession } from "../types";
import { Badge, type BadgeTone, Button, Card, Copy, JsonView, Spinner, Stat, Switch, agoShort, fmtNum } from "../ui";

/** The fields of a session this page renders; the rest goes under "More". */
const KNOWN = new Set<string>(["seq", "status", "name", "url", "mode", "verdict", "note", "area", "areaName", "label", "target", "pressedAt", "startedAt", "endedAt", "durationSec", "result", "error", "replied", "pid", "sessionId", "transcriptPath", "transcriptExists", "worktree", "worktreeExists", "dir", "costUsd", "usage", "turns", "model", "apiMs", "replies", "brief", "stderr", "processStart"]);

export function F8Page({ store, snap }: { store: ControlStore; snap: Snapshot }) {
  // Following lives with the page: on while it is mounted (and Live is on), off when the user leaves.
  useEffect(() => { store.setF8Live(true); return () => store.setF8Live(false); }, [store]);
  const now = useNow(1000);
  const f8 = snap.f8;
  const r = f8.result;
  const hasTool = !snap.catalog || store.hasTool("feedback_sessions");
  const sessions = r?.sessions ?? [];
  // The press in the detail pane: the one in the route (#/f8/21), else the newest.
  const selectedSeq = snap.route.id !== undefined ? Number(snap.route.id) : sessions[0]?.seq;
  const selected = selectedSeq !== undefined ? sessions.find((s) => s.seq === selectedSeq) : undefined;
  // A deep link or a fresh list: make sure the detail pane has its press's brief.
  useEffect(() => {
    if (selectedSeq !== undefined && Number.isFinite(selectedSeq) && f8.detail?.seq !== selectedSeq) void store.loadF8Detail(selectedSeq);
  }, [store, selectedSeq, f8.detail?.seq]);
  const stale = f8.at !== undefined && now - f8.at > (f8.via === "push" ? 60_000 : 15_000);
  const running = sessions.filter((s) => s.status === "running" || s.status === "starting").length;
  const queued = sessions.filter((s) => s.status === "queued").length;

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <div className="flex min-w-0 items-center gap-2">
          <Icon name="keyboard" className="size-4 shrink-0 text-fg-3" />
          <h2 className="ds-title truncate">F8 sessions</h2>
          {r && <DispatcherBadge d={r.dispatcher} />}
          {running > 0 && <Badge tone="accent" icon="play" title="Handlers running now">{running} running</Badge>}
          {queued > 0 && <Badge tone="warning" icon="clock" title="Presses waiting for a handler">{queued} queued</Badge>}
        </div>
        <div className="ml-auto flex flex-wrap items-center gap-2 text-[11px] text-fg-3">
          <span className="flex items-center gap-1.5 tnum" title={f8.at ? `Last read ${new Date(f8.at).toLocaleTimeString()}${f8.ms !== undefined ? ` in ${fmtNum(f8.ms, 0)} ms` : ""}${r?.bridge ? ` · ${r.bridge}` : ""}` : "Nothing read yet"}>
            <span className={`size-1.5 rounded-full ${f8.live && r && !f8.error && !stale ? "bg-success live-dot" : f8.error ? "bg-danger" : "bg-fg-3"}`} />
            {!f8.live ? "paused" : f8.error ? "retrying every 10 s" : f8.via === "push" ? "push" : "3 s while visible"}
            {f8.at !== undefined && <span className="hidden sm:inline">· read {agoShort(now - f8.at)}</span>}
          </span>
          <label className="flex items-center gap-1.5 text-fg-2" title={f8.via === "push" || store.host.listen ? "Follow the dispatcher's files as the server pushes changes" : "Poll feedback_sessions every 3 s while this page is visible"}>
            <Switch size="sm" checked={f8.live} onChange={(v) => store.setF8Live(v)} label="Live" disabled={!hasTool} />live
          </label>
          <Button size="sm" icon="sync" busy={f8.loading && !f8.live} disabled={!hasTool} onClick={() => void store.refreshF8()} title="Read feedback_sessions once now">Refresh</Button>
        </div>
      </div>

      {!hasTool && <Banner tone="info" icon="info" title="This server has no feedback_sessions tool">The F8 page needs an ExileApiMcp build with <code className="font-code">feedback_sessions</code> (Tools/FeedbackSessionsTools.cs). Deploy a newer build (mcp_deploy) and reload this page.</Banner>}
      {hasTool && f8.error && <Banner tone="danger" icon="warning" title="feedback_sessions failed" action={<Button size="sm" onClick={() => void store.refreshF8()}>Retry</Button>}>{f8.error}</Banner>}
      {r && <DispatcherCard d={r.dispatcher} bridge={r.bridge ?? undefined} handlersDir={r.handlersDir ?? undefined} now={now} />}

      {!r ? (
        !f8.error && hasTool && (
          <Card>
            <EmptyState icon="keyboard" title={f8.loading ? "Reading the dispatcher's files…" : "Nothing read yet"} className="py-8">
              feedback_sessions reads <code className="font-code">%LOCALAPPDATA%\Hexile\WhatsARoute\handlers\</code> and merges the bridge's press events when a HUD is up.
            </EmptyState>
          </Card>
        )
      ) : sessions.length === 0 ? (
        <Card>
          <EmptyState icon="keyboard" title="No F8 press yet" className="py-8">
            Press F8 in game with Whats A Route on: the dispatcher starts a Claude session for it and this list fills in.
          </EmptyState>
        </Card>
      ) : (
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-[minmax(0,2fr)_minmax(0,3fr)] lg:items-start">
          <Card title="Presses" icon="list" pad={false} right={<span className="tnum text-[10.5px] text-fg-3">{sessions.length}{r.total > sessions.length ? ` of ${r.total}` : ""} · newest first</span>}>
            <ol className="divide-y divide-line">
              {sessions.map((s) => <SessionRow key={s.seq} s={s} selected={s.seq === selectedSeq} now={now} onSelect={() => store.selectF8(s.seq)} />)}
            </ol>
          </Card>
          <div className="min-w-0 lg:sticky lg:top-0">
            {selected ? (
              <Detail s={selected} detail={f8.detail?.seq === selected.seq ? f8.detail : undefined} now={now} onReloadDetail={() => void store.loadF8Detail(selected.seq)} />
            ) : (
              <Card>
                <EmptyState icon="quote" title={selectedSeq !== undefined ? `No press #${selectedSeq} in the list` : "Pick a press"} className="py-8">
                  {selectedSeq !== undefined ? "It may be older than the list shows, or its folder is gone." : "The note, the answer, the timings, the cost and the links show here."}
                </EmptyState>
              </Card>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

// ── The dispatcher ────────────────────────────────────────────────────

function dispatcherLook(d: FeedbackDispatcherSummary): { tone: BadgeTone; icon: IconName; label: string } {
  if (!d.alive) return { tone: "danger", icon: "offline", label: d.status === "never-ran" ? "never ran" : "not running" };
  switch (d.status) {
    case "listening": return { tone: "success", icon: "radio", label: "listening" };
    case "idle": return { tone: "neutral", icon: "clock", label: "idle · no HUD" };
    case "broken": return { tone: "danger", icon: "warning", label: "broken" };
    case "starting": return { tone: "info", icon: "sync", label: "starting" };
    default: return { tone: "warning", icon: "info", label: d.status };
  }
}

function DispatcherBadge({ d }: { d: FeedbackDispatcherSummary }) {
  const look = dispatcherLook(d);
  return <Badge tone={look.tone} icon={look.icon} title={`The F8 dispatcher: ${d.status}${d.why ? ` — ${d.why}` : ""}`}>dispatcher {look.label}</Badge>;
}

/** The dispatcher's state: a banner with the fix when something is broken, a quiet fact strip when it listens. */
function DispatcherCard({ d, bridge, handlersDir, now }: { d: FeedbackDispatcherSummary; bridge?: string; handlersDir?: string; now: number }) {
  const look = dispatcherLook(d);
  const broken = !d.alive || d.status === "broken";
  const facts = (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px] text-fg-3">
      {d.claudeExe ? (
        <span className="flex min-w-0 items-center gap-1.5" title={d.claudeExe}>
          <Icon name="code" className="size-3 shrink-0" />
          <span className="truncate">CLI {cliVersion(d.claudeExe)}</span>
          {d.loggedIn === true && <Badge tone="success" icon="check">signed in</Badge>}
          {d.loggedIn === false && <Badge tone="danger" icon="lock">not signed in</Badge>}
        </span>
      ) : <span className="flex items-center gap-1.5"><Icon name="code" className="size-3" />CLI not found yet</span>}
      {d.waiting.length > 0 && <span className="flex items-center gap-1.5"><Icon name="clock" className="size-3" />waiting {d.waiting.map((w) => `#${w}`).join(", ")}</span>}
      {d.running != null && <span className="flex items-center gap-1.5"><Icon name="play" className="size-3" />handling #{d.running}</span>}
      {d.version && <span className="tnum" title={`The dispatcher runs on the ${d.host ?? "shared"} server, pid ${d.pid ?? "?"}`}>v{d.version}{d.pid ? ` · pid ${d.pid}` : ""}</span>}
      {d.updatedAt && <span className="tnum" title="The dispatcher rewrites its file every second">updated {agoShort(now - Date.parse(d.updatedAt))}</span>}
      {bridge && <span className="truncate" title="Whether the HUD bridge's press and reply events were merged in">{bridge}</span>}
      {handlersDir && <span className="flex min-w-0 items-center gap-1" title={handlersDir}><Icon name="folder" className="size-3 shrink-0" /><span className="truncate font-code">{shortPath(handlersDir)}</span><Copy text={handlersDir} label="Copy the handlers folder" /></span>}
    </div>
  );
  if (!broken) return <Card pad={false}><div className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-3 py-2"><Badge tone={look.tone} icon={look.icon}>{look.label}</Badge>{facts}</div></Card>;
  const title = !d.alive ? (d.status === "never-ran" ? "No F8 dispatcher has run on this machine yet" : "The F8 dispatcher is not running") : "The F8 dispatcher is broken: presses wait";
  return (
    <Banner tone="danger" icon={look.icon} title={title}>
      <div className="flex flex-col gap-2">
        {d.why && <p>{d.why}</p>}
        {d.fix && !(d.loggedIn === false && d.loginCommand && d.fix.includes(d.loginCommand)) && <p><b className="font-semibold">Fix:</b> {d.fix}</p>}
        {d.loggedIn === false && d.loginCommand && (
          <div className="flex flex-col gap-1">
            <span><b className="font-semibold">Sign in once</b>, in your own PowerShell (the dispatcher can't do it for you):</span>
            <div className="flex items-center gap-1 rounded-md border border-line bg-surface px-2 py-1"><code className="min-w-0 flex-1 break-all font-code text-[11px] text-fg">{d.loginCommand}</code><Copy text={d.loginCommand} label="Copy the sign-in command" /></div>
          </div>
        )}
        {facts}
      </div>
    </Banner>
  );
}

function cliVersion(exe: string): string {
  const m = /claude-code[\\/]([0-9][^\\/]*)/.exec(exe);
  return m ? m[1] : "";
}

function shortPath(p: string): string {
  const parts = p.split(/[\\/]/).filter(Boolean);
  return parts.length > 3 ? "…\\" + parts.slice(-3).join("\\") : p;
}

// ── The list ──────────────────────────────────────────────────────────

function statusLook(s: FeedbackSession): { tone: BadgeTone; icon: IconName; label: string; live?: boolean } {
  switch (s.status) {
    case "queued": return { tone: "warning", icon: "clock", label: "queued" };
    case "starting": return { tone: "accent", icon: "sync", label: "starting", live: true };
    case "running": return { tone: "accent", icon: "play", label: "running", live: true };
    case "done": return { tone: "success", icon: "check", label: "done" };
    case "failed": return { tone: "danger", icon: "circleX", label: "failed" };
    case "works": return { tone: "info", icon: "circleCheck", label: "it works" };
    default: return { tone: "neutral", icon: "dot", label: s.status };
  }
}

function SessionRow({ s, selected, now, onSelect }: { s: FeedbackSession; selected: boolean; now: number; onSelect: () => void }) {
  const look = statusLook(s);
  const at = s.pressedAt ?? s.startedAt;
  const title = s.name ?? (s.areaName ? `F8 #${s.seq} · ${s.areaName}` : `F8 #${s.seq}`);
  return (
    <li>
      <button type="button" onClick={onSelect} aria-current={selected ? "true" : undefined}
        className={`relative flex w-full flex-col gap-1 px-3 py-2 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring ${selected ? "bg-ring/10" : "hover:bg-surface-2"}`}>
        {selected && <span className="absolute inset-y-0 left-0 w-0.5 bg-ring" aria-hidden />}
        <div className="flex min-w-0 items-center gap-2">
          <span className={`grid size-4 shrink-0 place-items-center rounded-full ${look.tone === "success" ? "bg-success/15 text-success" : look.tone === "danger" ? "bg-danger/15 text-danger" : look.tone === "warning" ? "bg-warning/15 text-warning" : look.tone === "info" ? "bg-info/15 text-info" : "bg-ring/15 text-fg"} ${look.live ? "live-dot" : ""}`}>
            {look.live ? <Spinner className="size-2.5" /> : <Icon name={look.icon} className="size-2.5" />}
          </span>
          <span className="min-w-0 flex-1 truncate text-[12.5px] font-medium">{title}</span>
          {s.url && <Icon name="externalLink" className="size-3 shrink-0 text-fg-3" aria-label="Has a session link" />}
          {at && <span className="tnum shrink-0 text-[10.5px] text-fg-3" title={new Date(at).toLocaleString()}>{agoShort(now - Date.parse(at))}</span>}
        </div>
        <p className={`line-clamp-2 pl-6 text-[11.5px] leading-snug ${s.note ? "text-fg-2" : "text-fg-3 italic"}`}>{s.note ?? (s.status === "works" ? "The player said it works." : "No note: the player pressed F8 without typing.")}</p>
        <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 pl-6 text-[10.5px] text-fg-3 tnum">
          <Badge tone={look.tone} className="normal-case">{look.label}</Badge>
          {s.durationSec != null && <span>{fmtDuration(s.durationSec)}</span>}
          {s.costUsd != null && <span>${s.costUsd.toFixed(2)}</span>}
          {s.mode && <span className="capitalize">{s.mode.replace(/-/g, " ")}</span>}
          {s.label && <span className="truncate">step {s.label}</span>}
        </div>
      </button>
    </li>
  );
}

// ── The detail pane ───────────────────────────────────────────────────

function Detail({ s, detail, now, onReloadDetail }: { s: FeedbackSession; detail?: Snapshot["f8"]["detail"]; now: number; onReloadDetail: () => void }) {
  const look = statusLook(s);
  const extra = useMemo(() => {
    const o: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(s)) if (!KNOWN.has(k) && v !== null && v !== undefined) o[k] = v;
    return o;
  }, [s]);
  const title = s.name ?? `F8 #${s.seq}`;
  const u = s.usage;
  // A running handler's duration counts up here: the server's figure is as of the last read.
  const durationSec = look.live && s.startedAt ? (now - Date.parse(s.startedAt)) / 1000 : s.durationSec;
  const tokensIn = (u?.inputTokens ?? 0) + (u?.cacheReadTokens ?? 0) + (u?.cacheCreationTokens ?? 0);
  return (
    <Card title={<span className="flex min-w-0 items-center gap-2"><span className="truncate">{title}</span>{s.name && <span className="font-code text-[11px] font-normal normal-case tracking-normal text-fg-3">#{s.seq}</span>}</span>} icon="keyboard"
      right={<>
        {s.mode && <Badge tone="violet" title="How the handler runs">{s.mode}</Badge>}
        <Badge tone={look.tone} icon={look.live ? undefined : look.icon}>{look.live && <Spinner className="size-2.5" />}{look.label}</Badge>
        {s.url && <a href={s.url} target="_blank" rel="noreferrer noopener" className="inline-flex h-6 items-center gap-1 rounded-md border border-ring/40 bg-ring/10 px-1.5 text-[11px] font-medium text-fg hover:bg-ring/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"><Icon name="externalLink" className="size-3" />Open session</a>}
      </>}>
      <div className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px] text-fg-2">
          {s.areaName && <span className="flex items-center gap-1"><Icon name="mapPin" className="size-3 text-fg-3" />{s.areaName}{s.area && <span className="font-code text-[10.5px] text-fg-3"> {s.area}</span>}</span>}
          {s.label && <span className="flex min-w-0 items-center gap-1" title={s.target ?? undefined}><Icon name="target" className="size-3 shrink-0 text-fg-3" /><span className="truncate">{s.label}</span></span>}
          {s.verdict && s.verdict !== "note" && <Badge tone="neutral">{s.verdict}</Badge>}
          {s.pressedAt && <span className="tnum" title={new Date(s.pressedAt).toLocaleString()}>pressed {new Date(s.pressedAt).toLocaleTimeString()} · {agoShort(now - Date.parse(s.pressedAt))}</span>}
        </div>

        <Section icon="quote" label="The player wrote">
          {s.note ? <blockquote className="border-l-2 border-ring/60 pl-3 text-[13px] leading-relaxed text-fg">{s.note}</blockquote> : <p className="text-[11.5px] italic text-fg-3">{s.status === "works" ? "Nothing: F8 with no note says the route works here." : "Nothing: F8 with no note."}</p>}
        </Section>

        {(s.result || s.error || look.live || s.status === "queued") && (
          <Section icon={s.error ? "warning" : "sparkle"} label={s.error ? "What went wrong" : look.live ? "Working" : s.status === "queued" ? "Waiting" : "Claude answered"}>
            {s.error ? <p className="rounded-md border border-danger/30 bg-danger/10 px-3 py-2 text-[12px] leading-relaxed text-danger">{s.error}</p>
              : s.result ? <p className="rounded-md bg-surface-2 px-3 py-2 text-[13px] leading-relaxed">{s.result}</p>
              : look.live ? <p className="text-[11.5px] text-fg-3">A fresh session is on it{s.startedAt ? ` since ${new Date(s.startedAt).toLocaleTimeString()} (${fmtDuration(durationSec ?? 0)})` : ""}. Its answer lands here and above the route card.</p>
              : <p className="text-[11.5px] text-fg-3">The dispatcher takes it once the running handler finishes (one at a time: they share route.live.json).</p>}
            {s.replies.filter((x) => x.source && x.source !== "route-dispatcher" && x.status === "working").slice(-1).map((x) => (
              <p key={x.seq} className="mt-1.5 text-[11.5px] text-fg-2"><span className="text-fg-3">while working: </span>{x.text}</p>
            ))}
          </Section>
        )}

        <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
          <Stat label="Duration" value={durationSec != null ? fmtDuration(durationSec) : "–"} sub={s.apiMs != null ? `${fmtDuration(s.apiMs / 1000)} in the API` : s.startedAt ? `started ${new Date(s.startedAt).toLocaleTimeString()}` : undefined} />
          <Stat label="Cost" value={s.costUsd != null ? `$${s.costUsd.toFixed(2)}` : "–"} sub={s.model ?? undefined} tone={s.costUsd != null && s.costUsd > 2 ? "warn" : undefined} />
          <Stat label="Turns" value={s.turns ?? "–"} sub={u?.outputTokens != null ? `${fmtTokens(u.outputTokens)} out${u.thinkingTokens ? `, ${fmtTokens(u.thinkingTokens)} thinking` : ""}` : undefined} />
          <Stat label="Context" value={tokensIn ? fmtTokens(tokensIn) : "–"} sub={u ? `${fmtTokens(u.cacheReadTokens ?? 0)} cached · ${fmtTokens(u.cacheCreationTokens ?? 0)} new` : undefined} />
        </div>

        <Timeline s={s} now={now} />

        <Section icon="braces" label="Links">
          <dl className="grid grid-cols-[6rem_minmax(0,1fr)] items-center gap-x-2 gap-y-1 text-[11.5px]">
            {s.url && <LinkRow label="Session" value={s.url} href={s.url} />}
            {s.sessionId && <LinkRow label="Session id" value={s.sessionId} />}
            {s.transcriptPath && <LinkRow label="Transcript" value={s.transcriptPath} missing={s.transcriptExists === false ? "not on disk" : undefined} />}
            {s.worktree && <LinkRow label="Worktree" value={s.worktree} missing={s.worktreeExists === false ? "removed (held nothing)" : undefined} />}
            {s.dir && <LinkRow label="Handler" value={s.dir} />}
            {s.pid != null && <><dt className="text-fg-3">Process</dt><dd className="tnum">pid {s.pid}{s.replied ? " · the dispatcher told the card" : s.status === "done" ? " · the session told the card itself" : ""}</dd></>}
          </dl>
        </Section>

        {s.replies.length > 0 && (
          <Section icon="send" label={`On the route card (${s.replies.length})`}>
            <ol className="flex flex-col gap-1">
              {s.replies.map((x) => <ReplyRow key={x.seq} x={x} />)}
            </ol>
          </Section>
        )}

        <Disclosure icon="file" label="Brief" summary={detail?.loading ? "reading…" : detail?.brief ? `${fmtNum(detail.brief.length, 0)} chars` : detail?.error ? "unreadable" : "the press, the route state and the trace"}>
          {detail?.loading ? <p className="flex items-center gap-2 text-[11.5px] text-fg-3"><Spinner />Reading brief.md…</p>
            : detail?.error ? <p className="text-[11.5px] text-danger">{detail.error} <Button size="sm" onClick={onReloadDetail}>Retry</Button></p>
            : detail?.brief ? <pre className="max-h-96 overflow-auto whitespace-pre-wrap break-words rounded-md bg-surface-2 p-3 font-code text-[11px] leading-relaxed scroll-thin">{detail.brief}</pre>
            : <p className="text-[11.5px] text-fg-3">{s.status === "queued" || s.status === "works" ? "No brief: no handler was started for this press." : "No brief.md in the handler's folder."}</p>}
        </Disclosure>
        {detail?.stderr && (
          <Disclosure icon="warning" label="stderr" summary={`${fmtNum(detail.stderr.length, 0)} chars`}>
            <pre className="max-h-60 overflow-auto whitespace-pre-wrap break-words rounded-md bg-surface-2 p-3 font-code text-[11px] leading-relaxed text-danger scroll-thin">{detail.stderr}</pre>
          </Disclosure>
        )}
        {Object.keys(extra).length > 0 && (
          <Disclosure icon="braces" label="More" summary="fields this page has no view for yet">
            <JsonView value={extra} open={1} />
          </Disclosure>
        )}
      </div>
    </Card>
  );
}

function Section({ icon, label, children }: { icon: IconName; label: string; children: ReactNode }) {
  return (
    <section className="min-w-0">
      <h3 className="mb-1 flex items-center gap-1.5 ds-label text-fg-3"><Icon name={icon} className="size-3" />{label}</h3>
      {children}
    </section>
  );
}

function Disclosure({ icon, label, summary, children }: { icon: IconName; label: string; summary?: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <section className="min-w-0 rounded-md border border-line">
      <button type="button" onClick={() => setOpen((o) => !o)} aria-expanded={open} className="flex w-full items-center gap-1.5 px-2.5 py-1.5 text-left hover:bg-surface-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring">
        <Icon name="chevronRight" className={`size-3 shrink-0 text-fg-3 transition-transform ${open ? "rotate-90" : ""}`} />
        <Icon name={icon} className="size-3 shrink-0 text-fg-3" />
        <span className="ds-label text-fg-2">{label}</span>
        {summary && <span className="min-w-0 truncate text-[10.5px] text-fg-3">{summary}</span>}
      </button>
      {open && <div className="border-t border-line p-2.5">{children}</div>}
    </section>
  );
}

function LinkRow({ label, value, href, missing }: { label: string; value: string; href?: string; missing?: string }) {
  return (
    <>
      <dt className="text-fg-3">{label}</dt>
      <dd className="flex min-w-0 items-center gap-1">
        {href ? <a href={href} target="_blank" rel="noreferrer noopener" className="min-w-0 truncate font-code text-[11px] text-fg underline decoration-line underline-offset-2 hover:decoration-fg">{value}</a>
          : <span className={`min-w-0 truncate font-code text-[11px] ${missing ? "text-fg-3 line-through" : "text-fg-2"}`} title={value}>{value}</span>}
        {missing && <Badge tone="neutral">{missing}</Badge>}
        <Copy text={value} label={`Copy the ${label.toLowerCase()}`} />
      </dd>
    </>
  );
}

function ReplyRow({ x }: { x: FeedbackReply }) {
  const tone: BadgeTone = x.status === "done" ? "success" : x.status === "failed" ? "danger" : x.status === "working" ? "accent" : x.status === "queued" ? "warning" : "neutral";
  const who = x.source === "route-dispatcher" ? "dispatcher" : x.source ?? "?";
  return (
    <li className="flex items-start gap-2 text-[11.5px]">
      <span className="tnum w-[4.5rem] shrink-0 pt-0.5 text-[10.5px] text-fg-3">{x.at ? new Date(x.at).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" }) : ""}</span>
      <Badge tone={tone} className="mt-px">{x.status ?? "?"}</Badge>
      <span className="min-w-0 flex-1 leading-snug text-fg-2"><span className="text-fg-3">{who}: </span>{x.text}</span>
    </li>
  );
}

/** Pressed → taken → answered, with the waits between them: where the time went. */
function Timeline({ s, now }: { s: FeedbackSession; now: number }) {
  const pressed = s.pressedAt ? Date.parse(s.pressedAt) : undefined;
  const started = s.startedAt ? Date.parse(s.startedAt) : undefined;
  const ended = s.endedAt ? Date.parse(s.endedAt) : (s.status === "running" || s.status === "starting") ? now : undefined;
  if (pressed === undefined && started === undefined) return null;
  const marks: { label: string; at?: number; tone: string }[] = [
    { label: "pressed", at: pressed, tone: "bg-fg-3" },
    { label: "taken", at: started, tone: "bg-ring" },
    { label: s.status === "failed" ? "failed" : ended !== undefined && s.endedAt ? "answered" : "now", at: ended, tone: s.status === "failed" ? "bg-danger" : s.endedAt ? "bg-success" : "bg-ring" },
  ];
  const t0 = pressed ?? started!;
  const t1 = ended ?? started ?? t0;
  const span = Math.max(1, t1 - t0);
  const wait = pressed !== undefined && started !== undefined ? started - pressed : undefined;
  return (
    <Section icon="clock" label="Where the time went">
      <div className="relative mx-1 mt-1 h-1.5 rounded-full bg-surface-3">
        {wait !== undefined && started !== undefined && <div className="absolute inset-y-0 rounded-full bg-warning/50" style={{ left: 0, width: `${((started - t0) / span) * 100}%` }} title={`Waited ${fmtDuration(wait / 1000)} for a handler`} />}
        {started !== undefined && ended !== undefined && <div className={`absolute inset-y-0 rounded-full ${s.status === "failed" ? "bg-danger/60" : "bg-ring/70"}`} style={{ left: `${((started - t0) / span) * 100}%`, width: `${((ended - started) / span) * 100}%` }} title={`Handled in ${fmtDuration((ended - started) / 1000)}`} />}
        {marks.filter((m) => m.at !== undefined).map((m) => (
          <span key={m.label} className={`absolute top-1/2 size-2.5 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-surface ${m.tone}`} style={{ left: `${((m.at! - t0) / span) * 100}%` }} title={`${m.label} ${new Date(m.at!).toLocaleTimeString()}`} />
        ))}
      </div>
      <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-0.5 text-[10.5px] text-fg-3 tnum">
        {pressed !== undefined && <span>pressed {new Date(pressed).toLocaleTimeString()}</span>}
        {wait !== undefined && <span className={wait > 60_000 ? "text-warning" : ""}>waited {fmtDuration(wait / 1000)}</span>}
        {started !== undefined && ended !== undefined && <span>handled in {fmtDuration((ended - started) / 1000)}{s.endedAt ? "" : " so far"}</span>}
        {s.endedAt && <span>answered {new Date(s.endedAt).toLocaleTimeString()}</span>}
      </div>
    </Section>
  );
}

export function fmtDuration(sec: number): string {
  if (!Number.isFinite(sec) || sec < 0) return "–";
  if (sec < 60) return `${Math.round(sec)} s`;
  const m = Math.floor(sec / 60), s = Math.round(sec % 60);
  if (m < 60) return s ? `${m} min ${s} s` : `${m} min`;
  const h = Math.floor(m / 60);
  return `${h} h ${m % 60} min`;
}

export function fmtTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(n >= 10_000_000 ? 0 : 1)}M`;
  if (n >= 1000) return `${(n / 1000).toFixed(n >= 100_000 ? 0 : 1)}k`;
  return String(n);
}
