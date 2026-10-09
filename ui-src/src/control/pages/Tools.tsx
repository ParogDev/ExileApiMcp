// Tools: browse and search the catalog by family, open a tool, try it with a form generated from its input schema, and
// read the result (from its output schema when typed, text otherwise, raw JSON on demand). Resources and prompts
// are listed under their own tabs.

import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Banner, EmptyState, SectionLabel, Skeleton } from "../../components";
import { Icon, type IconName } from "../../icons";
import { argsLine, fieldsOf, initialValues, propInfo, validate, type Field, type FormValues } from "../schema";
import type { ControlStore, Snapshot, ToolRun } from "../store";
import { T } from "../tour/ids";
import { useTours } from "../tour/engine";
import type { CatalogPrompt, CatalogResource, CatalogTool, Game } from "../types";
import { Badge, Button, CatalogIconImg, Confirm, Copy, JsonView, Select, ShowMe, Spinner, Switch, TextInput, agoShort } from "../ui";

export type Theme = "light" | "dark";

/** Tools whose call is held open by the server until something happens: show a timer and a Stop. */
const HELD = new Set(["observe_wait", "perf_watch", "await_change", "await_motion", "experiment_queue_wait", "watch_object", "watch_memory"]);

const APP_PAGE: Record<string, { page: "perf" | "memory" | "explorer" | "stats"; label: string }> = {
  "ui://exile/hud-performance": { page: "perf", label: "HUD performance" },
  "ui://exile/memory-view": { page: "memory", label: "Memory view" },
  "ui://exile/data-explorer": { page: "explorer", label: "Data explorer" },
  "ui://exile/player-stats": { page: "stats", label: "Player stats" },
};

export function ToolBadges({ t, compact }: { t: CatalogTool; compact?: boolean }) {
  return (
    <span className="flex flex-wrap items-center gap-1">
      {t.readOnly ? <Badge tone="success" icon="eye" title="Never changes anything">{compact ? "ro" : "read-only"}</Badge> : <Badge tone="info" icon="pencil" title="Changes HUD or server state">writes</Badge>}
      {t.destructive && <Badge tone="danger" icon="warning" title="Asks for a confirmation before calling">destructive</Badge>}
      {t.outputSchema && <Badge tone="violet" icon="braces" title="Has an output schema: the result renders from it">typed</Badge>}
      {t.appUri && <Badge tone="accent" icon="arrowUpRight" title={`Opens ${t.appUri}`}>app</Badge>}
      {!compact && t.idempotent && !t.readOnly && <Badge tone="neutral" title="Calling it twice is the same as once">idempotent</Badge>}
    </span>
  );
}

export function ToolsPage({ store, snap, theme }: { store: ControlStore; snap: Snapshot; theme: Theme }) {
  // #/tools/<name> is a tool; #/tools/resources and #/tools/prompts are the other tabs.
  if (snap.route.id && snap.route.id !== "resources" && snap.route.id !== "prompts") return <ToolPage store={store} snap={snap} theme={theme} name={snap.route.id} />;
  return <ToolsList store={store} snap={snap} theme={theme} />;
}

type Tab = "tools" | "resources" | "prompts";

function ToolsList({ store, snap, theme }: { store: ControlStore; snap: Snapshot; theme: Theme }) {
  const c = snap.catalog;
  const [tab, setTab] = useState<Tab>((snap.route.id as Tab) ?? "tools");
  const q = snap.toolQuery.trim().toLowerCase();
  const searchRef = useRef<HTMLInputElement>(null);
  const tours = useTours();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "/" && !(e.target instanceof HTMLInputElement) && !(e.target instanceof HTMLTextAreaElement)) { e.preventDefault(); searchRef.current?.focus(); }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, []);

  const families = useMemo(() => {
    const m = new Map<string, number>();
    for (const t of c?.tools ?? []) m.set(t.family, (m.get(t.family) ?? 0) + 1);
    return [...m.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [c]);

  const matches = (hay: (string | null | undefined)[]) => !q || hay.some((h) => h?.toLowerCase().includes(q));
  const tools = useMemo(() => (c?.tools ?? []).filter((t) => (!snap.toolFamily || t.family === snap.toolFamily) && matches([t.name, t.title, t.description, t.family])), [c, q, snap.toolFamily]); // eslint-disable-line react-hooks/exhaustive-deps
  const resources = useMemo(() => (c?.resources ?? []).filter((r) => matches([r.name, r.title, r.description, r.uri, r.uriTemplate])), [c, q]); // eslint-disable-line react-hooks/exhaustive-deps
  const prompts = useMemo(() => (c?.prompts ?? []).filter((p) => matches([p.name, p.title, p.description])), [c, q]); // eslint-disable-line react-hooks/exhaustive-deps

  if (!c) {
    return (
      <div className="flex flex-col gap-3">
        {snap.catalogError ? (
          <Banner tone="danger" icon="offline" title="The catalog could not be loaded" action={<Button size="sm" onClick={() => void store.loadCatalog()}>Retry</Button>}>{snap.catalogError}</Banner>
        ) : <Skeleton kind="rows" count={8} />}
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative min-w-0 flex-1 basis-56">
          <Icon name="search" className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-fg-3" />
          <input ref={searchRef} type="search" value={snap.toolQuery} onChange={(e) => store.setToolQuery(e.target.value)} placeholder={`Search ${c.tools.length} tools, ${c.resources.length} resources, ${c.prompts.length} prompts  (/)`} aria-label="Search tools" data-tour={T.toolSearch}
            className="h-7 w-full rounded-md border border-line bg-surface pl-7 pr-7 text-[12.5px] placeholder:text-fg-3 hover:border-line-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" />
          {snap.toolQuery && <button type="button" aria-label="Clear search" onClick={() => store.setToolQuery("")} className="absolute right-1.5 top-1/2 grid size-5 -translate-y-1/2 place-items-center rounded text-fg-3 hover:bg-surface-3 hover:text-fg"><Icon name="x" className="size-3" /></button>}
        </div>
        <div role="tablist" className="inline-flex h-7 rounded-md border border-line bg-surface-2 p-0.5">
          {([["tools", c.tools.length], ["resources", c.resources.length], ["prompts", c.prompts.length]] as [Tab, number][]).map(([k, n]) => (
            <button key={k} type="button" role="tab" aria-selected={tab === k} onClick={() => { setTab(k); store.go("tools", k === "tools" ? undefined : k); }}
              className={`inline-flex items-center gap-1 rounded-[5px] px-2 text-[12px] font-medium capitalize transition-colors ${tab === k ? "bg-surface-3 text-fg" : "text-fg-2 hover:bg-surface-2 hover:text-fg"}`}>
              {k} <span className="tnum text-[10.5px] text-fg-3">{n}</span>
            </button>
          ))}
        </div>
        <ShowMe onClick={() => tours.start("try-a-tool")} done={tours.done.has("try-a-tool")} size="md">Show me: try a tool</ShowMe>
      </div>

      {tab === "tools" && (
        <div className="grid grid-cols-1 gap-3 md:grid-cols-[11rem_minmax(0,1fr)] md:items-start">
          <nav aria-label="Families" data-tour={T.toolFamilies} className="flex gap-1 overflow-x-auto pb-1 scroll-thin md:sticky md:top-2 md:flex-col md:overflow-visible md:pb-0">
            <FamilyButton active={!snap.toolFamily} onClick={() => store.setToolFamily(undefined)} count={c.tools.length}>All</FamilyButton>
            {families.map(([f, n]) => <FamilyButton key={f} active={snap.toolFamily === f} onClick={() => store.setToolFamily(snap.toolFamily === f ? undefined : f)} count={n}>{f}</FamilyButton>)}
          </nav>
          <div data-tour={T.toolList} className="min-w-0">
            {tools.length === 0 ? (
              <EmptyState icon="search" title="No tool matches" className="rounded-card border border-dashed border-line">Try another word, or clear the family filter.</EmptyState>
            ) : (
              <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
                {tools.map((t, i) => (
                  <li key={t.name} className="rise min-w-0" style={{ "--i": Math.min(i, 12) } as React.CSSProperties}>
                    <button type="button" onClick={() => store.go("tools", t.name)} className="group flex h-full w-full flex-col gap-1.5 rounded-card border border-line bg-surface p-2.5 text-left transition-colors hover:border-line-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
                      <div className="flex items-center gap-2">
                        <span className="grid size-7 shrink-0 place-items-center rounded-md bg-surface-2"><CatalogIconImg icons={t.icons} theme={theme} fallback={familyIcon(t.family)} className="text-fg-2 size-4" /></span>
                        <span className="min-w-0 flex-1">
                          <span className="block truncate font-code text-[12px] font-semibold">{t.name}</span>
                          <span className="block truncate text-[11px] text-fg-2">{t.title ?? t.family}</span>
                        </span>
                        <Icon name="chevronRight" className="size-3.5 shrink-0 text-fg-3 transition-transform group-hover:translate-x-0.5" />
                      </div>
                      {t.description && <p className="line-clamp-2 text-[11px] leading-snug text-fg-3">{t.description}</p>}
                      <div className="mt-auto flex items-center gap-1.5 pt-0.5"><Badge tone="neutral">{t.family}</Badge><ToolBadges t={t} compact /></div>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      )}

      {tab === "resources" && <ResourcesList resources={resources} store={store} theme={theme} game={snap.game} />}
      {tab === "prompts" && <PromptsList prompts={prompts} store={store} />}
    </div>
  );
}

function familyIcon(f: string): IconName {
  const m: Record<string, IconName> = { Observe: "activity", Memory: "chip", Probe: "chip", Stats: "activity", Explore: "layers", Eval: "code", Recording: "play", Script: "code", Knowledge: "quote", Guide: "target", Experiment: "target", HealthReport: "activity", PipelineTrace: "activity", Perf: "activity", HudDev: "puzzle", HudType: "braces", Control: "sliders", GameState: "gamepad", GameData: "list", Map: "grid", Bridge: "radio" };
  return m[f] ?? "box";
}

function FamilyButton({ children, active, onClick, count }: { children: ReactNode; active: boolean; onClick: () => void; count: number }) {
  return (
    <button type="button" onClick={onClick} aria-pressed={active}
      className={`flex h-7 shrink-0 items-center gap-2 rounded-md px-2 text-[12px] transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${active ? "bg-fg text-surface" : "text-fg-2 hover:bg-surface-3 hover:text-fg"}`}>
      <span className="truncate">{children}</span><span className={`tnum ml-auto text-[10.5px] ${active ? "opacity-70" : "text-fg-3"}`}>{count}</span>
    </button>
  );
}

// ── Resources and prompts ───────────────────────────────────────────

function ResourcesList({ resources, store, theme, game }: { resources: CatalogResource[]; store: ControlStore; theme: Theme; game?: Game }) {
  const [open, setOpen] = useState<string>();
  return (
    <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2">
      {resources.map((r, i) => {
        const uri = r.uri ?? r.uriTemplate ?? "";
        return (
          <li key={uri} className="rise min-w-0 rounded-card border border-line bg-surface p-2.5" style={{ "--i": Math.min(i, 12) } as React.CSSProperties}>
            <div className="flex items-center gap-2">
              <span className="grid size-7 shrink-0 place-items-center rounded-md bg-surface-2"><CatalogIconImg icons={r.icons} theme={theme} fallback="braces" className="size-4 text-fg-2" /></span>
              <span className="min-w-0 flex-1"><span className="block truncate text-[12.5px] font-semibold">{r.title ?? r.name}</span><span className="block truncate font-code text-[11px] text-fg-2" title={uri}>{uri}</span></span>
              <Copy text={uri} label="Copy URI" />
            </div>
            {r.description && <p className="mt-1.5 text-[11px] leading-snug text-fg-3">{r.description}</p>}
            <div className="mt-2 flex flex-wrap items-center gap-1.5">
              {r.subscribable ? <Badge tone="accent" icon="radio" title="subscriptions/listen pushes updates for it">subscribable</Badge> : <Badge tone="neutral">read</Badge>}
              {r.mimeType && <Badge tone="neutral">{r.mimeType.split(";")[0]}</Badge>}
              {uri.startsWith("ui://") && <Badge tone="violet">MCP App</Badge>}
              {store.host.readResource && !uri.startsWith("ui://") && (
                <Button size="sm" className="ml-auto" icon="eye" onClick={() => setOpen(open === uri ? undefined : uri)}>{open === uri ? "Hide" : "Read"}</Button>
              )}
            </div>
            {open === uri && <ResourceReader uri={uri} store={store} game={game} />}
          </li>
        );
      })}
    </ul>
  );
}

function ResourceReader({ uri, store, game }: { uri: string; store: ControlStore; game?: Game }) {
  const params = useMemo(() => [...uri.matchAll(/\{(\w+)\}/g)].map((m) => m[1]), [uri]);
  const [vals, setVals] = useState<Record<string, string>>(() => Object.fromEntries(params.map((p) => [p, p === "game" ? game ?? "poe2" : p === "layer" ? "server" : p === "topic" ? "dev-loop" : ""])));
  const [state, setState] = useState<{ loading: boolean; json?: unknown; text?: string; error?: string }>({ loading: false });
  const resolved = params.reduce((u, p) => u.replace(`{${p}}`, encodeURIComponent(vals[p] ?? "")), uri);
  const read = async () => {
    setState({ loading: true });
    try { const r = await store.host.readResource!(resolved); setState({ loading: false, json: r.json, text: r.text }); }
    catch (e) { setState({ loading: false, error: e instanceof Error ? e.message : String(e) }); }
  };
  return (
    <div className="mt-2 flex flex-col gap-2 border-t border-line pt-2">
      {params.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {params.map((p) => <TextInput key={p} value={vals[p] ?? ""} onChange={(v) => setVals({ ...vals, [p]: v })} label={p} placeholder={p} mono className="basis-28 flex-1" />)}
        </div>
      )}
      <div className="flex items-center gap-2"><span className="min-w-0 flex-1 truncate font-code text-[11px] text-fg-3">{resolved}</span><Button size="sm" tone="primary" busy={state.loading} onClick={() => void read()}>Read</Button></div>
      {state.error && <p className="text-[11.5px] text-danger">{state.error}</p>}
      {state.json !== undefined ? <div className="max-h-72 overflow-auto rounded-md bg-surface-2 p-2 scroll-thin"><JsonView value={state.json} /></div>
        : state.text !== undefined && <pre className="code-wrap max-h-72 overflow-auto rounded-md bg-surface-2 p-2 font-code text-[11px] scroll-thin">{state.text}</pre>}
    </div>
  );
}

function PromptsList({ prompts, store }: { prompts: CatalogPrompt[]; store: ControlStore }) {
  return (
    <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2">
      {prompts.map((p, i) => (
        <li key={p.name} className="rise min-w-0 rounded-card border border-line bg-surface p-2.5" style={{ "--i": Math.min(i, 12) } as React.CSSProperties}>
          <div className="flex items-center gap-2">
            <span className="grid size-7 shrink-0 place-items-center rounded-md bg-surface-2 text-fg-2"><Icon name="quote" className="size-4" /></span>
            <span className="min-w-0 flex-1"><span className="block truncate text-[12.5px] font-semibold">{p.title ?? p.name}</span><span className="block truncate font-code text-[11px] text-fg-2">{p.name}</span></span>
            {store.host.ask && <Button size="sm" icon="sparkle" onClick={() => store.host.ask!(`Use the ${p.name} prompt`)}>Use</Button>}
          </div>
          {p.description && <p className="mt-1.5 text-[11px] leading-snug text-fg-3">{p.description}</p>}
          {!!p.arguments?.length && (
            <div className="mt-2 flex flex-wrap gap-1">{p.arguments.map((a) => <Badge key={a.name} tone={a.required ? "info" : "neutral"} title={a.description ?? undefined}>{a.name}{a.required ? "*" : ""}</Badge>)}</div>
          )}
        </li>
      ))}
    </ul>
  );
}

// ── One tool ───────────────────────────────────────────────────────

function ToolPage({ store, snap, theme, name }: { store: ControlStore; snap: Snapshot; theme: Theme; name: string }) {
  const t = store.tool(name);
  const tours = useTours();
  const fields = useMemo(() => fieldsOf(t?.inputSchema), [t]);
  const [values, setValues] = useState<FormValues>(() => initialValues(fields));
  const [touched, setTouched] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const runs = useMemo(() => snap.runs.filter((r) => r.tool === name), [snap.runs, name]);
  const latest = runs[0];
  const running = latest && !latest.endedAt;

  useEffect(() => { setValues(initialValues(fields)); setTouched(false); }, [fields]);

  if (!t) {
    return (
      <div className="flex flex-col gap-3">
        <Button tone="ghost" icon="arrowLeft" onClick={() => store.go("tools")} className="self-start">All tools</Button>
        <EmptyState icon="search" title={`No tool named ${name}`} className="rounded-card border border-dashed border-line">{snap.catalog ? "It isn't in this server's catalog." : "The catalog hasn't loaded yet."}</EmptyState>
      </div>
    );
  }

  const v = validate(fields, values);
  const gameField = fields.find((f) => f.isGame);
  const args = { ...v.args };
  if (gameField && !args.game && snap.game) args.game = snap.game;
  const hasErrors = Object.keys(v.errors).length > 0;
  const app = t.appUri ? APP_PAGE[t.appUri] : undefined;

  const call = () => {
    setTouched(true);
    if (hasErrors) return;
    if (t.destructive && !confirm) { setConfirm(true); return; }
    setConfirm(false);
    void store.runTool(t.name, args);
  };

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <Button tone="ghost" icon="arrowLeft" onClick={() => store.go("tools")}>Tools</Button>
        <span className="text-fg-3">/</span>
        <button type="button" onClick={() => { store.setToolFamily(t.family); store.go("tools"); }} className="text-[12px] text-fg-2 hover:text-fg hover:underline">{t.family}</button>
      </div>
      <header className="flex items-start gap-3">
        <span className="grid size-10 shrink-0 place-items-center rounded-lg bg-surface-2"><CatalogIconImg icons={t.icons} theme={theme} size={22} fallback={familyIcon(t.family)} className="size-5 text-fg-2" /></span>
        <div className="min-w-0 flex-1">
          <h1 className="text-[13px] font-semibold leading-tight">{t.title ?? t.name}</h1>
          <div className="mt-0.5 flex items-center gap-1"><code className="truncate font-code text-[12px] text-fg-2">{t.name}</code><Copy text={t.name} label="Copy tool name" /></div>
          <div className="mt-1.5" data-tour={T.toolBadges}><ToolBadges t={t} /></div>
        </div>
        {app && (
          store.host.mode === "standalone" ? <Button icon="arrowUpRight" onClick={() => store.go(app.page)}>Open {app.label}</Button>
            : store.host.ask ? <Button icon="sparkle" onClick={() => store.host.ask!(`Open the ${app.label} app (${t.name})`)}>Ask Claude to open it</Button> : null
        )}
      </header>
      {t.description && <p className="max-w-3xl text-[12.5px] leading-relaxed text-fg-2">{t.description}</p>}

      <div className="grid grid-cols-1 gap-3 lg:grid-cols-[minmax(0,22rem)_minmax(0,1fr)] lg:items-start">
        <section className="rounded-card border border-line bg-surface" data-tour={T.toolForm}>
          <header className="flex h-9 items-center gap-2 border-b border-line px-3">
            <Icon name="sliders" className="size-3.5 text-fg-3" /><h2 className="text-[12px] font-semibold">Try it</h2>
            <span className="ml-auto text-[10.5px] text-fg-3">{fields.length ? `${fields.length} argument${fields.length === 1 ? "" : "s"}` : "no arguments"}</span>
          </header>
          <form className="flex flex-col gap-3 p-3" onSubmit={(e) => { e.preventDefault(); call(); }}>
            {fields.map((f) => <FieldRow key={f.name} field={f} value={values[f.name]} error={touched ? v.errors[f.name] : undefined} game={snap.game} gamesUp={store.gamesUp} onChange={(val) => setValues({ ...values, [f.name]: val })} />)}
            {fields.length === 0 && <p className="text-[11.5px] text-fg-3">This tool takes no arguments.</p>}
            <div className="flex items-center gap-2 pt-1">
              {running ? (
                <Button tone="danger" icon="square" onClick={() => store.cancelRun(latest.id)} tour={T.toolCall}>Stop waiting</Button>
              ) : (
                <Button type="submit" tone={t.destructive ? "danger" : "primary"} icon={t.destructive ? "warning" : "play"} tour={T.toolCall} disabled={touched && hasErrors}>
                  {t.destructive ? "Call (destructive)" : "Call"}
                </Button>
              )}
              {touched && hasErrors && <span className="text-[11px] text-danger">Fix the fields marked in red</span>}
              {!running && Object.keys(v.args).length > 0 && <button type="button" onClick={() => { setValues(initialValues(fields)); setTouched(false); }} className="ml-auto text-[11px] text-fg-3 hover:text-fg hover:underline">Reset</button>}
            </div>
            {HELD.has(t.name) && <p className="text-[11px] leading-snug text-fg-3"><Icon name="clock" className="mr-1 inline size-3 align-[-2px]" />This call is held open by the server until something happens or it times out.</p>}
            <details className="text-[11px] text-fg-3">
              <summary className="cursor-pointer select-none hover:text-fg">Arguments as JSON</summary>
              <pre className="code-wrap mt-1 rounded-md bg-surface-2 p-2 font-code text-[11px] text-fg-2">{JSON.stringify(args, null, 2)}</pre>
            </details>
          </form>
        </section>

        <div className="flex min-w-0 flex-col gap-3">
          <section className="rounded-card border border-line bg-surface" data-tour={T.toolResult}>
            <header className="flex h-9 items-center gap-2 border-b border-line px-3">
              <Icon name="braces" className="size-3.5 text-fg-3" /><h2 className="text-[12px] font-semibold">Result</h2>
              {latest && <RunMeta run={latest} />}
            </header>
            <div className="p-3">
              {!latest ? (
                <EmptyState icon="play" title="Nothing called yet" className="py-4">Fill the form and press Call. {t.outputSchema ? "This tool is typed: its result renders in the schema's order." : "The result shows as text, and as JSON when it is JSON."}
                  <div className="mt-2"><ShowMe onClick={() => tours.start("try-a-tool")} done={tours.done.has("try-a-tool")} /></div>
                </EmptyState>
              ) : <RunResult run={latest} tool={t} />}
            </div>
          </section>
          {runs.length > 1 && (
            <section className="rounded-card border border-line bg-surface" data-tour={T.toolHistory}>
              <header className="flex h-9 items-center gap-2 border-b border-line px-3">
                <Icon name="clock" className="size-3.5 text-fg-3" /><h2 className="text-[12px] font-semibold">Earlier calls</h2>
                <span className="tnum text-[10.5px] text-fg-3">{runs.length - 1}</span>
                <button type="button" onClick={() => store.clearRuns(t.name)} className="ml-auto text-[11px] text-fg-3 hover:text-fg hover:underline">Clear</button>
              </header>
              <ul className="divide-y divide-line">
                {runs.slice(1, 8).map((r) => (
                  <li key={r.id} className="flex items-center gap-2 px-3 py-1.5 text-[11.5px]">
                    <span className={`size-1.5 shrink-0 rounded-full ${r.error || r.result?.isError ? "bg-danger" : "bg-success"}`} />
                    <span className="min-w-0 flex-1 truncate font-code text-[11px] text-fg-2" title={argsLine(r.args)}>{argsLine(r.args)}</span>
                    <span className="tnum shrink-0 text-fg-3">{r.result ? `${r.result.ms} ms` : ""}</span>
                    <button type="button" onClick={() => { setValues(initialValues(fields, r.args)); setTouched(false); }} className="shrink-0 text-fg-3 hover:text-fg hover:underline">Load</button>
                    <button type="button" onClick={() => void store.runTool(t.name, r.args)} className="shrink-0 text-fg-3 hover:text-fg hover:underline">Re-run</button>
                  </li>
                ))}
              </ul>
            </section>
          )}
        </div>
      </div>
      {confirm && (
        <Confirm title={`Call ${t.name}?`} confirmLabel="Call it" tone="danger" onCancel={() => setConfirm(false)} onConfirm={call}>
          This tool is marked <b>destructive</b>: it can change or remove state that is not easily restored. Arguments: <code className="font-code text-[11px]">{argsLine(args)}</code>.
        </Confirm>
      )}
    </div>
  );
}

function RunMeta({ run }: { run: ToolRun }) {
  const [, tick] = useState(0);
  useEffect(() => { if (run.endedAt) return; const t = setInterval(() => tick((n) => n + 1), 250); return () => clearInterval(t); }, [run.endedAt]);
  if (!run.endedAt) return <span className="ml-auto flex items-center gap-1.5 text-[11px] text-fg-2"><Spinner />waiting <span className="tnum">{((Date.now() - run.startedAt) / 1000).toFixed(1)} s</span></span>;
  const bad = run.error || run.result?.isError;
  return (
    <span className="ml-auto flex items-center gap-1.5 text-[11px]">
      <span className={`flex items-center gap-1 ${bad ? "text-danger" : "text-success"}`}><Icon name={bad ? "warning" : "check"} className="size-3" />{bad ? "error" : "ok"}</span>
      {run.result && <span className="tnum text-fg-3">{run.result.ms} ms</span>}
      <span className="text-fg-3" title={new Date(run.endedAt).toLocaleTimeString()}>{agoShort(Date.now() - run.endedAt)}</span>
    </span>
  );
}

type ResultTab = "result" | "text" | "json";

export function RunResult({ run, tool }: { run: ToolRun; tool?: CatalogTool }) {
  const r = run.result;
  const info = useMemo(() => propInfo(tool?.outputSchema), [tool]);
  const hasData = r?.data !== undefined && r.data !== null && typeof r.data === "object";
  const [tab, setTab] = useState<ResultTab>("result");
  const [, tick] = useState(0);
  useEffect(() => { if (run.endedAt) return; const t = setInterval(() => tick((n) => n + 1), 250); return () => clearInterval(t); }, [run.endedAt]);
  if (!run.endedAt) {
    return (
      <div className="flex flex-col gap-2 py-2">
        <div className="h-0.5 w-full overflow-hidden rounded bg-surface-3"><div className="shimmer h-full w-full" /></div>
        <p className="flex items-center gap-2 text-[12px] text-fg-2"><Spinner />Calling <code className="font-code">{run.tool}</code> with <span className="truncate font-code text-fg-3">{argsLine(run.args)}</span></p>
      </div>
    );
  }
  if (run.error) {
    return <Banner tone="danger" icon="warning" title="The call failed">{run.error}</Banner>;
  }
  if (!r) return null;
  const ordered = hasData && info ? orderKeys(r.data as Record<string, unknown>, info.order) : r?.data;
  return (
    <div className="flex min-w-0 flex-col gap-2">
      {r.isError && <Banner tone="danger" icon="warning" title="The tool reported an error">{r.text?.split("\n")[0]}</Banner>}
      <div className="flex items-center gap-1" role="tablist" data-tour={T.toolResultTabs}>
        {(["result", "text", "json"] as ResultTab[]).map((k) => (
          <button key={k} type="button" role="tab" aria-selected={tab === k} onClick={() => setTab(k)} disabled={(k === "result" && !hasData) || (k === "text" && !r.text) || (k === "json" && r.data === undefined)}
            className={`h-6 rounded-md px-2 text-[11px] font-medium transition-colors disabled:opacity-40 ${tab === k ? "bg-fg text-surface" : "text-fg-2 hover:bg-surface-3 hover:text-fg"}`}>
            {k === "result" ? (tool?.outputSchema ? "Result (typed)" : "Result") : k === "json" ? "JSON" : "Text"}
          </button>
        ))}
        <span className="ml-auto flex items-center gap-1">
          {tab === "json" && r.data !== undefined && <Copy text={JSON.stringify(r.data, null, 2)} label="Copy JSON" />}
          {tab === "text" && r.text && <Copy text={r.text} label="Copy text" />}
        </span>
      </div>
      <div className="fade-in min-w-0">
        {tab === "result" && (hasData ? <div className="max-h-[32rem] overflow-auto rounded-md bg-surface-2 p-2.5 scroll-thin"><JsonView value={ordered} descriptions={info?.descriptions} /></div>
          : r.text ? <pre className="code-wrap max-h-[32rem] overflow-auto rounded-md bg-surface-2 p-2.5 font-code text-[11.5px] scroll-thin">{r.text}</pre> : <p className="text-[11.5px] text-fg-3">Empty result.</p>)}
        {tab === "text" && <pre className="code-wrap max-h-[32rem] overflow-auto rounded-md bg-surface-2 p-2.5 font-code text-[11.5px] scroll-thin">{r.text}</pre>}
        {tab === "json" && <pre className="code-wrap max-h-[32rem] overflow-auto rounded-md bg-surface-2 p-2.5 font-code text-[11px] scroll-thin">{JSON.stringify(r.data, null, 2)}</pre>}
      </div>
      {info && hasData && tab === "result" && <p className="text-[10.5px] text-fg-3">* has a description in the output schema: hover the name.</p>}
    </div>
  );
}

function orderKeys(o: Record<string, unknown>, order: string[]): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const k of order) if (k in o) out[k] = o[k];
  for (const k of Object.keys(o)) if (!(k in out)) out[k] = o[k];
  return out;
}

// ── Form fields ────────────────────────────────────────────────────

function FieldRow({ field: f, value, error, onChange, game, gamesUp }: { field: Field; value: string | boolean | undefined; error?: string; onChange: (v: string | boolean) => void; game?: Game; gamesUp: Game[] }) {
  const id = `f-${f.name}`;
  const str = typeof value === "string" ? value : "";
  let control: ReactNode;
  if (f.isGame) {
    const opts = [{ value: "", label: game ? `selected HUD (${game})` : "whichever HUD is up" }, ...(["poe1", "poe2"] as const).map((g) => ({ value: g, label: `${g}${gamesUp.includes(g) ? " · up" : ""}` }))];
    control = <Select value={str} options={opts} onChange={onChange} label={f.name} />;
  } else if (f.type === "boolean") {
    control = <div className="flex h-8 items-center gap-2"><Switch checked={!!value} onChange={onChange} label={f.name} /><span className="text-[11.5px] text-fg-3">{value ? "true" : "false"}</span></div>;
  } else if (f.options) {
    const opts = [...(f.required ? [] : [{ value: "", label: f.default === null || f.default === undefined ? "— not set —" : `default (${String(f.default)})` }]), ...f.options.map((o) => ({ value: o }))];
    control = <Select value={f.options.includes(str) ? str : ""} options={opts} onChange={onChange} label={f.name} />;
  } else if (f.type === "integer" || f.type === "number") {
    const range = f.min !== undefined && f.max !== undefined ? [f.min, f.max] : f.hintRange;
    control = (
      <div className="flex items-center gap-2">
        <TextInput value={str} onChange={onChange} label={f.name} placeholder={f.default !== undefined && f.default !== null ? String(f.default) : f.type} invalid={!!error} className="max-w-[9rem]" />
        {range && <span className="tnum text-[11px] text-fg-3">{range[0]} – {range[1]}</span>}
      </div>
    );
  } else if (f.type === "array" || f.type === "object" || f.type === "any" || /code|script|csharp/.test(f.name)) {
    control = (
      <textarea id={id} value={str} onChange={(e) => onChange(e.target.value)} aria-label={f.name} aria-invalid={!!error || undefined} rows={f.type === "array" ? 2 : 4} spellCheck={false}
        placeholder={f.type === "array" ? "one per line" : f.type === "object" ? "{ ... } as JSON" : ""}
        className={`w-full rounded-md border bg-surface px-2 py-1.5 font-code text-[12px] placeholder:text-fg-3 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${error ? "border-danger" : "border-line hover:border-line-2"}`} />
    );
  } else {
    const mono = /path|expression|key|address|uri|name|id$|plugin|file|type|offset|hex/i.test(f.name);
    control = <TextInput value={str} onChange={onChange} label={f.name} mono={mono} invalid={!!error} placeholder={f.default !== undefined && f.default !== null ? String(f.default) : f.required ? "required" : ""} />;
  }
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className="flex items-center gap-1.5 text-[12px]">
        <code className="font-code font-semibold">{f.name}</code>
        {f.required && <span className="text-[10px] text-danger">required</span>}
        <span className="ml-auto text-[10.5px] text-fg-3">{f.type === "array" ? `${f.items ?? "string"}[]` : f.type}{f.nullable && f.type !== "any" ? "?" : ""}</span>
      </label>
      {control}
      {error ? <p className="text-[11px] text-danger">{error}</p> : f.description && <p className="text-[11px] leading-snug text-fg-3">{f.description}</p>}
    </div>
  );
}

export { SectionLabel };
