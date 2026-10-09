// The shell: a 44 px header (game badge, title, status chips), the page navigation (a sidebar from md up, a strip of
// tabs below), the page, toasts and the tour layer. Everything talks to one ControlStore.

import { useEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { Toasts, useNow } from "../components";
import { Icon, type IconName } from "../icons";
import { EmbeddedPage } from "./pages/Embedded";
import { ObserverPage } from "./pages/Observer";
import { OverviewPage } from "./pages/Overview";
import { SettingsPage } from "./pages/Settings";
import { ToolsPage } from "./pages/Tools";
import { ControlStore, PAGES, type Page } from "./store";
import { TourProvider, findTarget, useTours } from "./tour/engine";
import { T } from "./tour/ids";
import { TOURS } from "./tour/tours";
import type { Game } from "./types";
import { Badge, Button, agoShort } from "./ui";

const PAGE_ICON: Record<Page, IconName> = { overview: "grid", tools: "sliders", settings: "puzzle", observer: "eye", perf: "activity", memory: "chip", explorer: "layers", stats: "heart" };

function useMedia(q: string): boolean {
  const [m, setM] = useState(() => window.matchMedia(q).matches);
  useEffect(() => {
    const mq = window.matchMedia(q);
    const on = () => setM(mq.matches);
    mq.addEventListener("change", on);
    return () => mq.removeEventListener("change", on);
  }, [q]);
  return m;
}

export function ControlCenter({ store }: { store: ControlStore }) {
  const snap = useSyncExternalStore(store.subscribe, store.getSnapshot);
  const host = store.host;
  const tourApi = useMemo(() => ({
    go: (page: string, id?: string, sub?: string) => store.go(page as Page, id, sub),
    setToolQuery: (q: string) => store.setToolQuery(q),
    mode: host.mode,
    wait: (ms: number) => new Promise<void>((r) => setTimeout(r, ms)),
    click: (target: string) => findTarget(target)?.click(),
  }), [store, host.mode]);
  const tours = useMemo(() => TOURS.filter((t) => !t.standaloneOnly || host.mode === "standalone"), [host.mode]);
  return (
    <TourProvider tours={tours} api={tourApi}>
      <Shell store={store} snap={snap} />
    </TourProvider>
  );
}

function Shell({ store, snap }: { store: ControlStore; snap: ReturnType<ControlStore["getSnapshot"]> }) {
  const host = store.host;
  const wide = useMedia("(min-width: 48rem)");
  const standalone = host.mode === "standalone";
  const pages = PAGES.filter((p) => !p.standaloneOnly || standalone);
  const route = snap.route;
  const main = useRef<HTMLElement>(null);
  useEffect(() => { main.current?.scrollTo({ top: 0 }); }, [route.page, route.id]);

  const nav = (
    <nav aria-label="Pages" data-tour={T.nav} className={wide ? "flex flex-col gap-0.5 px-2" : "flex gap-1 overflow-x-auto px-3 py-1.5 scroll-thin"}>
      {pages.map((p) => {
        const active = route.page === p.page;
        return (
          <button key={p.page} type="button" onClick={() => store.go(p.page)} aria-current={active ? "page" : undefined} data-tour={`nav-${p.page}`}
            className={`flex h-7 shrink-0 items-center gap-2 rounded-md px-2 text-[12px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${active ? "bg-surface-3 text-fg" : "text-fg-2 hover:bg-surface-2 hover:text-fg"} ${wide ? "" : "px-2.5"}`}>
            <Icon name={PAGE_ICON[p.page]} className={`size-3.5 shrink-0 ${active ? "text-fg" : "text-fg-3"}`} />
            <span className="whitespace-nowrap">{p.label}</span>
            {wide && active && <span className="ml-auto size-1.5 rounded-full bg-ring" aria-hidden />}
          </button>
        );
      })}
    </nav>
  );

  const header = (
    <header className="flex h-11 shrink-0 items-center gap-2 border-b border-line px-3">
      <GameBadge game={snap.game} />
      <h1 className="truncate text-[13px] font-semibold">Control center</h1>
      {snap.catalog && <span className="hidden truncate text-[11px] text-fg-3 sm:inline">{snap.catalog.server.title ?? snap.catalog.server.name} {snap.catalog.server.version}</span>}
      <div className="ml-auto flex shrink-0 items-center gap-1.5">
        <HudChips store={store} games={snap.games} game={snap.game} gamesAt={snap.gamesAt} />
        <LivePill store={store} listen={snap.listen} watching={snap.health.watching} lastEventAt={snap.observer.lastEventAt} healthAt={snap.health.at} />
        <ShowMeMenu />
        {host.setTheme && <Button size="sm" tone="ghost" icon={host.theme === "dark" ? "eye" : "eyeOff"} onClick={() => host.setTheme!(host.theme === "dark" ? "light" : "dark")} ariaLabel={`Switch to the ${host.theme === "dark" ? "light" : "dark"} theme`} title={`Theme: ${host.theme}. Click to switch`} />}
        {host.fullscreen && <Button size="sm" tone="ghost" icon={host.fullscreen.active ? "minimize" : "maximize"} onClick={host.fullscreen.toggle} ariaLabel={host.fullscreen.active ? "Back to the conversation" : "Expand"} />}
      </div>
    </header>
  );

  const page = (
    <div key={`${route.page}`} className="page-in">
      {route.page === "overview" && <OverviewPage store={store} snap={snap} />}
      {route.page === "tools" && <ToolsPage store={store} snap={snap} theme={host.theme} />}
      {route.page === "settings" && <SettingsPage store={store} snap={snap} />}
      {route.page === "observer" && <ObserverPage store={store} snap={snap} />}
      {(route.page === "perf" || route.page === "memory" || route.page === "explorer" || route.page === "stats") && (standalone ? <EmbeddedPage app={route.page} host={host} game={snap.game} /> : <StandaloneOnly page={route.page} store={store} />)}
    </div>
  );

  const fill = standalone || host.fullscreen?.active;
  return (
    <div className={`flex flex-col bg-surface text-fg ${fill ? "h-screen overflow-hidden" : "min-h-[20rem]"}`}>
      {header}
      {wide ? (
        <div className={`grid min-h-0 flex-1 grid-cols-[12.5rem_minmax(0,1fr)] ${fill ? "overflow-hidden" : ""}`}>
          <aside className={`flex flex-col gap-3 border-r border-line py-3 ${fill ? "overflow-y-auto" : ""}`}>
            {nav}
            <div className="mt-auto px-4 text-[10.5px] leading-relaxed text-fg-3">
              {standalone ? <>Standalone · HTTP MCP with subscriptions</> : <>MCP App · held calls and polling</>}
              <div className="tnum">{snap.calls} calls this session</div>
            </div>
          </aside>
          <main ref={main} className={`min-w-0 p-4 ${fill ? "overflow-y-auto scroll-thin" : ""}`}>{page}</main>
        </div>
      ) : (
        <>
          <div className="border-b border-line">{nav}</div>
          <main ref={main} className={`min-w-0 flex-1 p-3 ${fill ? "overflow-y-auto scroll-thin" : ""}`}>{page}</main>
        </>
      )}
      <Toasts toasts={snap.toasts} onDismiss={(id) => store.dismissToast(id)} />
    </div>
  );
}

function GameBadge({ game }: { game?: Game }) {
  const label = game === "poe2" ? "Path of Exile 2" : game === "poe1" ? "Path of Exile" : "No HUD selected";
  return <span className="grid h-6 shrink-0 place-items-center rounded-md bg-fg px-1.5 text-[11px] font-bold tracking-tight text-surface" title={label}>{game === "poe2" ? "PoE 2" : game === "poe1" ? "PoE 1" : "PoE"}</span>;
}

function HudChips({ store, games, game, gamesAt }: { store: ControlStore; games: { game: string; status: string; error?: string; port?: number }[]; game?: Game; gamesAt?: number }) {
  const list = games.length ? games : [{ game: "poe1", status: "checking" }, { game: "poe2", status: "checking" }];
  return (
    <div className="flex items-center gap-1" data-tour={T.huds} role="radiogroup" aria-label="HUDs">
      {list.map((g) => {
        const up = g.status === "connected";
        const selected = g.game === game;
        const tone = up ? "text-success" : g.status === "unreachable" ? "text-danger" : "text-fg-3";
        return (
          <button key={g.game} type="button" role="radio" aria-checked={selected} onClick={() => store.setGame(g.game as Game)}
            title={`${g.game}: ${g.status}${g.port ? ` on :${g.port}` : ""}${g.error ? ` — ${g.error}` : ""}${gamesAt ? ` · checked ${agoShort(Date.now() - gamesAt)}` : ""}. Click to act on this HUD.`}
            className={`flex h-6 items-center gap-1.5 rounded-full border px-2 text-[11px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${selected ? "border-ring bg-surface-2 text-fg" : "border-line text-fg-2 hover:border-line-2"}`}>
            <span className={`size-1.5 rounded-full ${up ? "bg-success" : g.status === "unreachable" ? "bg-danger" : "bg-fg-3"}`} />
            <span>{g.game === "poe2" ? "PoE 2" : "PoE 1"}</span>
            <span className={`hidden xs:inline ${tone}`}>{up ? "up" : g.status === "unreachable" ? "unreachable" : g.status === "checking" ? "…" : "down"}</span>
          </button>
        );
      })}
    </div>
  );
}

function LivePill({ store, listen, watching, lastEventAt, healthAt }: { store: ControlStore; listen: string; watching: boolean; lastEventAt?: number; healthAt?: number }) {
  const now = useNow(1000);
  const push = !!store.host.listen;
  const last = Math.max(lastEventAt ?? 0, healthAt ?? 0);
  let tone: string, label: string, icon: ReactNode;
  if (push && listen === "live") { tone = "border-success/30 bg-success/10 text-success"; label = "Push"; icon = <span className="size-1.5 rounded-full bg-success live-dot" />; }
  else if (push && (listen === "connecting" || listen === "reconnecting")) { tone = listen === "reconnecting" ? "border-warning/30 bg-warning/10 text-warning" : "border-line text-fg-3"; label = listen === "reconnecting" ? "Reconnecting" : "Connecting"; icon = <Icon name="sync" className="spin size-3" />; }
  else if (push) { tone = "border-line text-fg-3"; label = "No subscription"; icon = <Icon name="radio" className="size-3" />; }
  else { tone = "border-line text-fg-2"; label = watching ? "Held calls" : "Polling"; icon = <Icon name="clock" className="size-3" />; }
  const title = push
    ? `Standalone: the server pushes resource updates over subscriptions/listen (observer events and layers${watching ? ", the perf report" : ""}); the app re-reads on each. ${last ? `Last update ${agoShort(now - last)}.` : ""}`
    : `Inside the host: observer events are polled every 2.5 s${watching ? " and perf_watch is held open for the next report" : ""}. ${last ? `Last update ${agoShort(now - last)}.` : ""}`;
  return (
    <span data-tour={T.livePill} title={title} role="status" className={`hidden h-6 items-center gap-1.5 rounded-full border px-2 text-[11px] font-medium xs:flex ${tone}`}>
      {icon}{label}
      {last > 0 && <span className="tnum hidden text-[10.5px] opacity-70 sm:inline">{agoShort(now - last)}</span>}
    </span>
  );
}

function ShowMeMenu() {
  const tours = useTours();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", onDoc); document.addEventListener("keydown", onKey);
    return () => { document.removeEventListener("mousedown", onDoc); document.removeEventListener("keydown", onKey); };
  }, [open]);
  return (
    <div ref={ref} className="relative">
      <Button size="sm" tone={open ? "ghost" : "ghost"} active={open} icon="sparkle" onClick={() => setOpen((o) => !o)} tour={T.showMe} title="Short guided tours of the key tasks"><span className="hidden sm:inline">Show me</span></Button>
      {open && (
        <div role="menu" className="fade-in absolute right-0 top-full z-30 mt-1.5 w-72 rounded-lg border border-line bg-surface p-1 shadow-xl">
          <p className="px-2 py-1.5 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3">Show me how to</p>
          {tours.tours.map((t) => (
            <button key={t.id} type="button" role="menuitem" onClick={() => { setOpen(false); tours.start(t.id); }}
              className="flex w-full items-start gap-2 rounded-md px-2 py-1.5 text-left hover:bg-surface-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
              <Icon name={tours.done.has(t.id) ? "check" : "play"} className={`mt-0.5 size-3.5 shrink-0 ${tours.done.has(t.id) ? "text-success" : "text-fg-3"}`} />
              <span className="min-w-0"><span className="block text-[12px] font-medium">{t.title}</span><span className="block text-[11px] leading-snug text-fg-3">{t.summary} · {t.steps.length} steps</span></span>
            </button>
          ))}
          <p className="px-2 py-1.5 text-[10.5px] text-fg-3">Arrow keys move, Esc leaves. Seen tours get a check.</p>
        </div>
      )}
    </div>
  );
}

function StandaloneOnly({ page, store }: { page: Page; store: ControlStore }) {
  const label = PAGES.find((p) => p.page === page)?.label ?? page;
  return (
    <div className="rounded-card border border-dashed border-line px-4 py-6 text-center">
      <p className="text-[12.5px] font-medium">{label} is a page of the standalone control center</p>
      <p className="mt-1 text-[11.5px] text-fg-3">Inside Claude, ask for the app instead; it opens as its own panel.</p>
      <div className="mt-2 flex justify-center gap-2">
        {store.host.ask && <Button size="sm" tone="primary" icon="sparkle" onClick={() => store.host.ask!(`Open the ${label} app`)}>Ask Claude to open it</Button>}
        <Button size="sm" onClick={() => store.go("overview")}>Back to the overview</Button>
      </div>
      <Badge tone="neutral" className="mt-3">standalone only</Badge>
    </div>
  );
}
