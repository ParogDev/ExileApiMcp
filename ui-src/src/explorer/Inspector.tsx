import { useEffect, useState, type ReactNode } from "react";
import { IconButton, SectionLabel } from "../components";
import { ago } from "../format";
import { Icon } from "../icons";
import { KIND_DOT, KIND_LABEL, Preview } from "./Preview";
import type { Resolved } from "./rows";
import { WATCH_MS, type ExplorerStore, type Snapshot } from "./store";
import { SmallButton } from "./Tree";
import type { WatchChange } from "./types";

export interface InspectorHost {
  send?: (r: Resolved) => void;
  ask?: (r: Resolved) => void;
}

/** Everything about the selected node, each piece one click from the clipboard, plus the actions. */
export function Inspector({ store, snap, now, sel, host, variant }: { store: ExplorerStore; snap: Snapshot; now: number; sel: Resolved; host: InspectorHost; variant: "card" | "panel" }) {
  const n = sel.node;
  const isRoot = sel.path === snap.root;
  const watch = snap.watch?.path === sel.path ? snap.watch : undefined;
  const checked = !!sel.path && snap.checked.has(sel.path);
  const checkable = !!sel.path && !!n.csharp && n.kind !== "blocked" && !sel.child?.error;
  const [full, setFull] = useState<{ path: string; loading: boolean; data?: unknown; error?: string } | undefined>();
  useEffect(() => { setFull(undefined); }, [sel.id]);

  const copy = async (text: string, what: string) => {
    try { await navigator.clipboard.writeText(text); store.toast("info", `Copied ${what}`); }
    catch { store.toast("error", "Clipboard blocked by the host; select the text instead"); }
  };
  const loadFull = async () => {
    if (!sel.path) return;
    setFull({ path: sel.path, loading: true });
    const r = await store.evalPath(sel.path);
    setFull({ path: sel.path, loading: false, ...r });
  };

  const typeLine = n.type ? <>{n.type}{n.declaredType && n.declaredType !== n.type && <span className="text-fg-3"> · declared {n.declaredType}</span>}</> : null;
  const entry = sel.entry;
  // A root that never loaded (bad path, bridge down): nothing is known about it yet.
  const unloaded = !sel.child && !entry?.node;

  return (
    <section aria-label="Selected node" className={`${variant === "panel" ? "rounded-lg border border-line bg-surface-2 p-3" : "rounded-lg border border-line bg-surface p-3"} text-xs`}>
      <div className="flex items-center gap-1.5 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3">
        <span className={`size-1.5 rounded-full ${unloaded ? "bg-fg-3" : KIND_DOT[n.kind]}`} aria-hidden />
        {unloaded ? "not loaded" : sel.component ? "component" : KIND_LABEL[n.kind]}
        {sel.child?.slowMs !== undefined && <span className="inline-flex items-center gap-1 font-normal normal-case tracking-normal text-warning"><Icon name="clock" className="size-3" />slow getter {sel.child.slowMs} ms</span>}
        {isRoot && <span className="font-normal normal-case tracking-normal">· root</span>}
        <span className="ml-auto flex items-center gap-0.5">
          {entry?.loadedAt && <span className="tnum font-normal normal-case tracking-normal" title={entry.elapsedMs !== undefined ? `Bridge read took ${entry.elapsedMs} ms` : undefined}>{ago(now - entry.loadedAt)}{entry.elapsedMs !== undefined ? ` · ${entry.elapsedMs} ms` : ""}</span>}
        </span>
      </div>

      <div className="mt-1 flex items-baseline gap-2">
        <h2 className="code-wrap min-w-0 font-code text-[13px] font-semibold leading-tight">{sel.component ? `GetComponent<${sel.name}>()` : sel.name}</h2>
      </div>
      {(typeLine || n.namespace) && (
        <p className="code-wrap mt-0.5 font-code text-[11px] text-fg-2">
          {typeLine}{n.namespace && <span className="text-fg-3">{typeLine ? " — " : ""}{n.namespace}</span>}
        </p>
      )}

      {/* Value */}
      <div className="mt-2 rounded-md bg-surface-3/60 px-2.5 py-2">
        {unloaded ? (
          <p className="text-fg-3">{entry?.error ? <><Icon name="warning" className="mr-1 inline size-3.5 align-[-2px] text-danger" />{entry.error}</> : "Loading…"}</p>
        ) : sel.child?.error ? (
          <p className="code-wrap text-danger"><Icon name="warning" className="mr-1 inline size-3.5 align-[-2px]" />{sel.child.error}</p>
        ) : n.kind === "object" || n.kind === "component" ? (
          <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <Preview node={n} full className="text-[12px]" />
            {!n.preview || n.preview === n.type ? <span className="text-fg-3">{n.kind === "component" && !sel.path ? n.note ?? "No HUD wrapper type: not readable through the API." : "Object — expand it for its members."}</span> : null}
          </div>
        ) : (
          <Preview node={n} full className="text-[12.5px]" />
        )}
        {n.kind === "blocked" && <p className="mt-1 text-fg-3">The walker cannot read this member (unsupported or unsafe type). The C# accessor still works in a plugin.</p>}
        {n.note && n.kind !== "component" && <p className="mt-1 text-fg-3">{n.note}</p>}
      </div>

      <dl className="mt-2 grid grid-cols-[2.25rem_minmax(0,1fr)_auto] items-start gap-x-2 gap-y-1">
        <dt className="pt-0.5 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3">Path</dt>
        <dd className="code-wrap min-w-0 font-code text-[11px] leading-snug">{sel.path ?? <span className="italic text-fg-3">not addressable by the walker</span>}</dd>
        <dd>{sel.path && <IconButton icon="copy" label="Copy path" size="sm" onClick={() => copy(sel.path!, "path")} className="-my-0.5" />}</dd>
        <dt className="pt-0.5 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3">C#</dt>
        <dd className="code-wrap min-w-0 font-code text-[11px] leading-snug">{n.csharp ?? <span className="italic text-fg-3">—</span>}</dd>
        <dd>{n.csharp && <IconButton icon="copy" label="Copy C#" size="sm" onClick={() => copy(n.csharp!, "C#")} className="-my-0.5" />}</dd>
      </dl>

      <div className="mt-2.5 flex flex-wrap gap-1.5">
        {sel.path && !isRoot && <SmallButton icon="target" onClick={() => void store.navigate(sel.path!)} title="Make this node the top of the tree">Re-root</SmallButton>}
        {sel.path && <SmallButton icon="sync" onClick={() => void store.refresh(sel.path!)} disabled={entry?.loading} title="Read this node again">Refresh</SmallButton>}
        {sel.path && <SmallButton icon="activity" onClick={() => void store.watch(sel.path!)} disabled={snap.watch?.status === "running"} title={`watch_object for ${WATCH_MS / 1000} s: which fields below this change`}>Watch {WATCH_MS / 1000} s</SmallButton>}
        {sel.path && (n.kind === "object" || n.kind === "struct" || n.kind === "list" || n.kind === "dictionary" || n.kind === "string" || n.kind === "component") && (
          <SmallButton icon="braces" onClick={loadFull} disabled={full?.loading} title="eval_path: the full JSON value">Full value</SmallButton>
        )}
        {checkable && (
          <SmallButton icon={checked ? "checkSquare" : "square"} active={checked}
            onClick={() => store.setChecked({ name: sel.component ? `GetComponent<${sel.name}>()` : sel.name, path: sel.path!, csharp: n.csharp!, type: n.type, kind: n.kind, preview: n.preview }, !checked)}
            title="Include this value in the generated C# snippet">
            {checked ? "In snippet" : "Add to snippet"}
          </SmallButton>
        )}
      </div>
      {(host.send || host.ask) && (
        <div className="mt-1.5 flex flex-wrap gap-1.5">
          {host.send && <SmallButton icon="send" onClick={() => host.send!(sel)} title="Put this node's path, C#, type and value into Claude's context">Send to Claude</SmallButton>}
          {host.ask && <SmallButton icon="sparkle" tone="primary" onClick={() => host.ask!(sel)} title="Ask in the conversation what this is and how a plugin uses it">Ask Claude about this</SmallButton>}
        </div>
      )}

      {watch && <WatchPanel watch={watch} now={now} onClear={() => store.clearWatch()} />}

      {full && full.path === sel.path && (
        <div className="mt-3">
          <SectionLabel right={<IconButton icon="x" label="Hide full value" size="sm" onClick={() => setFull(undefined)} className="-my-1" />}>Full value · eval_path</SectionLabel>
          {full.loading ? <div className="mt-1.5 space-y-1.5"><div className="shimmer h-2.5 w-3/4 rounded" /><div className="shimmer h-2.5 w-1/2 rounded" /></div>
            : full.error ? <p className="mt-1.5 text-danger">{full.error}</p>
            : <pre className="scroll-thin code-wrap mt-1.5 max-h-56 overflow-auto rounded-md bg-surface-3/60 p-2 font-code text-[10.5px] leading-snug">{typeof full.data === "string" ? full.data : JSON.stringify(full.data, null, 2)}</pre>}
        </div>
      )}
    </section>
  );
}

function WatchPanel({ watch, now, onClear }: { watch: NonNullable<Snapshot["watch"]>; now: number; onClear: () => void }) {
  const pct = Math.min(1, (now - watch.startedAt) / watch.durationMs);
  const changes = watch.result?.changes ?? [];
  return (
    <div className="mt-3" aria-live="polite">
      <SectionLabel right={watch.status !== "running" && <IconButton icon="x" label="Clear watch results" size="sm" onClick={onClear} className="-my-1" />}>
        <Icon name="activity" className="size-3" />Watch
        {watch.status === "running" && <span className="font-normal normal-case tracking-normal">· sampling {Math.ceil((watch.durationMs - (now - watch.startedAt)) / 1000)} s</span>}
        {watch.status === "done" && <span className="tnum font-normal normal-case tracking-normal">· {watch.result?.samples ?? "?"} samples, {changes.length} changed</span>}
      </SectionLabel>
      {watch.status === "running" && (
        <div className="mt-1.5 h-1 overflow-hidden rounded bg-surface-3" role="progressbar" aria-valuenow={Math.round(pct * 100)} aria-valuemin={0} aria-valuemax={100}>
          <div className="h-full bg-warning transition-[width] duration-500 ease-linear" style={{ width: `${pct * 100}%` }} />
        </div>
      )}
      {watch.status === "error" && <p className="mt-1.5 code-wrap text-danger">{watch.error}</p>}
      {watch.status === "done" && changes.length === 0 && (
        <p className="mt-1.5 text-fg-3">Nothing below this changed in {watch.durationMs / 1000} s.{watch.result?.note ? ` ${watch.result.note}` : " Do something in game and watch again."}</p>
      )}
      {changes.length > 0 && (
        <ul className="mt-1.5 divide-y divide-line rounded-md border border-line">
          {changes.map((c) => <WatchRow key={c.path} c={c} />)}
        </ul>
      )}
      {watch.status === "done" && watch.result?.note && changes.length > 0 && <p className="mt-1 text-fg-3">{watch.result.note}</p>}
    </div>
  );
}

function WatchRow({ c }: { c: WatchChange }) {
  return (
    <li className="flex items-center gap-2 px-2 py-1">
      <span className="min-w-0 flex-1 truncate font-code text-[11px]" title={c.path}>{c.path || "(value)"}</span>
      <span className="tnum shrink-0 font-code text-[11px]">
        <span className="text-fg-3">{fmtVal(c.first)}</span> <span className="text-fg-3">→</span> <span className="text-fg">{fmtVal(c.last)}</span>
      </span>
      <span className="tnum shrink-0 rounded bg-surface-3 px-1 text-[10px] text-fg-2" title={`Changed ${c.changes} times`}>×{c.changes}</span>
      {c.noisy && <span className="shrink-0 rounded bg-warning/15 px-1 text-[10px] text-warning" title="Changed on almost every sample">noisy</span>}
    </li>
  );
}

function fmtVal(v: unknown): ReactNode {
  if (v === undefined) return "—";
  if (typeof v === "string") return v.length > 24 ? `"${v.slice(0, 23)}…"` : `"${v}"`;
  if (typeof v === "number") return v.toLocaleString("en-US", { maximumFractionDigits: 3 });
  const s = JSON.stringify(v) ?? String(v);
  return s.length > 24 ? s.slice(0, 23) + "…" : s;
}
