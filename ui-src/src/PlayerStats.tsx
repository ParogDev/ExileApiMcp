import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore, type ReactNode } from "react";
import { Banner, EmptyState, IconButton, PinnedChips, ResistGrid, SectionLabel, SyncPill, Toasts, VitalsRow, useNow } from "./components";
import { ago, cleanText, fmt, resists } from "./format";
import { Icon } from "./icons";
import { StatDetail } from "./StatDetail";
import { StatTable } from "./StatTable";
import type { StatsStore } from "./sync";
import type { StatItem } from "./types";

/** What the panel needs from its host (the MCP Apps host, or the dev harness). */
export interface HostApi {
  /** Tell the model what the user is looking at (ui/update-model-context). */
  updateModelContext?: (text: string, structured: Record<string, unknown>) => void;
  /** Post a user message into the conversation (ui/message). */
  ask?: (text: string) => void;
  fullscreen?: { active: boolean; toggle: () => void };
}

export function PlayerStats({ store, host }: { store: StatsStore; host: HostApi }) {
  const snap = useSyncExternalStore(store.subscribe, store.getSnapshot);
  const { view, stats, vitals, game, conn } = snap;
  const now = useNow(1000);

  const byKey = useMemo(() => new Map(stats.map((s) => [s.key, s])), [stats]);
  const pinnedSet = useMemo(() => new Set(view?.pinnedStatKeys ?? []), [view?.pinnedStatKeys]);
  const resistList = useMemo(() => resists(stats), [stats]);
  const selectedKey = view?.selectedStatKey ?? null;
  const selectedStat = selectedKey ? byKey.get(selectedKey) : undefined;
  const level = byKey.get("level")?.value;

  const select = useCallback((key: string | null) => void store.select(key), [store]);
  const toggleSelect = useCallback((key: string) => select(selectedKey === key ? null : key), [select, selectedKey]);
  const pin = useCallback((key: string, pinned: boolean) => void store.setPinned(key, pinned), [store]);
  const load = useCallback((key: string) => store.getStat(key), [store]);

  useModelContext(host, selectedKey, view?.pinnedStatKeys, byKey, game);

  const fullscreen = host.fullscreen?.active ?? false;
  const notInGame = snap.inGame === false;
  const offline = conn === "offline";

  const detail = view && selectedKey ? (
    <StatDetail
      key={selectedKey}
      statKey={selectedKey}
      stat={selectedStat}
      stats={stats}
      pinned={pinnedSet.has(selectedKey)}
      change={snap.changes[selectedKey]}
      now={now}
      remote={snap.selectionSource === "remote"}
      load={load}
      onPin={(p) => pin(selectedKey, p)}
      onAsk={host.ask && (() => host.ask!(askText(selectedStat ?? { key: selectedKey }, game)))}
      onCopied={(ok) => store.toast(ok ? "info" : "error", ok ? `Copied ${selectedKey}` : "Clipboard blocked by the host; select the key text instead")}
      onClose={() => select(null)}
      variant={fullscreen ? "panel" : "sheet"}
    />
  ) : null;

  const banners = (
    <>
      {offline && (
        <Banner tone="danger" icon="offline" title="HUD bridge unreachable"
          action={<button type="button" onClick={() => store.kick()} className="rounded-md border border-current px-2 py-0.5 text-[11px] font-medium hover:bg-surface">Retry</button>}>
          {snap.lastOkAt ? `Showing data from ${ago(now - snap.lastOkAt)}. ` : ""}
          Is the HUD running with “Whats An AI Bridge” enabled?{snap.error && <span className="block truncate opacity-80" title={snap.error}>{snap.error}</span>}
        </Banner>
      )}
      {!offline && notInGame && (
        <Banner tone="warning" icon="gamepad" title="Not in game">Character select or loading screen. Values refresh once you are in an area.</Banner>
      )}
    </>
  );

  const vitalsBlock = <VitalsRow vitals={vitals} history={snap.vitalsHistory} dim={notInGame || offline} />;
  const resistBlock = (
    <ResistGrid list={resistList} pinnedKeys={pinnedSet} selectedKey={selectedKey} loaded={snap.statsLoaded} onPin={pin} onSelect={toggleSelect} />
  );
  const pinnedBlock = view && (
    <PinnedChips keys={view.pinnedStatKeys} byKey={byKey} selectedKey={selectedKey} changes={snap.changes} now={now}
      onUnpin={(k) => pin(k, false)} onSelect={toggleSelect} />
  );
  const table = (key: "inline" | "full") => view ? (
    <StatTable
      key={key}
      stats={stats}
      loaded={snap.statsLoaded}
      view={view}
      changes={snap.changes}
      now={now}
      remote={snap.remote}
      selectionSource={snap.selectionSource}
      fill={fullscreen}
      maxHeight="17rem"
      onFilter={(t, c) => void store.setFilter(t, c)}
      onSort={(by, desc) => void store.setSort(by, desc)}
      onPin={pin}
      onSelect={select}
      sheet={fullscreen ? undefined : detail}
    />
  ) : (
    <div className="rounded-lg border border-line px-3 py-6 text-center text-xs text-fg-3">
      {offline ? "No view state received yet." : "Waiting for the HUD…"}
    </div>
  );

  return (
    <div className={fullscreen ? "flex h-screen flex-col overflow-hidden" : "flex flex-col"}>
      <header className="flex h-11 items-center gap-2 px-3">
        <span className="grid h-6 shrink-0 place-items-center rounded-md bg-fg px-1.5 text-[11px] font-bold tracking-tight text-surface" title={game === "poe2" ? "Path of Exile 2" : game === "poe1" ? "Path of Exile" : "Game not known yet"}>
          {game === "poe2" ? "PoE 2" : game === "poe1" ? "PoE 1" : "PoE"}
        </span>
        <h1 className="truncate text-[13px] font-semibold">Player stats</h1>
        {level !== undefined && <span className="tnum shrink-0 text-[11px] text-fg-3" title="Character level (stat key: level)">Lv {level}</span>}
        {game === "poe2" && vitals?.weaponSet !== undefined && (
          <span className="flex shrink-0 items-center gap-1 rounded-md border border-line px-1.5 py-0.5 text-[10.5px] text-fg-2" title="Active weapon set; stats differ per set">
            <Icon name="sword" className="size-3" />Set {vitals.weaponSet === 0 ? "I" : "II"}
          </span>
        )}
        <div className="ml-auto flex shrink-0 items-center gap-1">
          <SyncPill conn={conn} pending={snap.pending} latencyMs={snap.latencyMs} lastOkAt={snap.lastOkAt} remote={snap.remote}
            view={view} game={game} calls={snap.calls} error={snap.error} now={now} />
          {host.fullscreen && (
            <IconButton icon={fullscreen ? "minimize" : "maximize"} label={fullscreen ? "Back to the conversation" : "Expand"} onClick={host.fullscreen.toggle} />
          )}
        </div>
      </header>

      {fullscreen ? (
        <>
          <div className="flex flex-col gap-2 px-4 empty:hidden">{banners}</div>
          <div className="grid min-h-0 flex-1 grid-cols-[minmax(18rem,24rem)_minmax(0,1fr)] gap-4 p-4 pt-3">
            <aside className="scroll-thin flex min-h-0 flex-col gap-4 overflow-y-auto pr-1">
              <Section label="Vitals">{vitalsBlock}</Section>
              <Section label="Resistances">{resistBlock}</Section>
              {pinnedBlock}
              {detail ?? (
                <EmptyState icon="info" title="Select a stat" className="rounded-lg border border-dashed border-line py-5">
                  Click a row, a resistance or a pin to see its key, record details and actions here.
                </EmptyState>
              )}
            </aside>
            {table("full")}
          </div>
        </>
      ) : (
        <div className="flex flex-col gap-2.5 px-3 pb-3">
          {banners}
          <div className="grid gap-2.5 sm:grid-cols-[minmax(0,5fr)_minmax(0,6fr)]">
            {vitalsBlock}
            {resistBlock}
          </div>
          {pinnedBlock}
          {table("inline")}
        </div>
      )}

      <Toasts toasts={snap.toasts} onDismiss={(id) => store.dismiss(id)} />
    </div>
  );
}

function Section({ label, children }: { label: string; children: ReactNode }) {
  return (
    <section aria-label={label}>
      <SectionLabel className="mb-1.5">{label}</SectionLabel>
      {children}
    </section>
  );
}

function askText(s: { key: string; value?: number; text?: string }, game?: string): string {
  const text = cleanText(s.text);
  const value = s.value === undefined ? "not present on the character" : `currently ${fmt(s.value)}`;
  return `In my ${game === "poe2" ? "PoE2" : "PoE1"} player stats, explain \`${s.key}\` (${value}${text ? `, "${text}"` : ""}). ` +
    "Where does it come from, what affects it, and is it capped?";
}

/**
 * Keep the model informed of what the user is looking at, so "this stat" or "my pins" in the
 * next prompt resolves without another tool call. Debounced and deduplicated: only sent when the
 * selection or pins actually change.
 */
function useModelContext(host: HostApi, selected: string | null | undefined, pins: string[] | undefined,
  byKey: Map<string, StatItem>, game?: string) {
  const last = useRef("");
  useEffect(() => {
    if (!host.updateModelContext || !pins) return;
    const t = setTimeout(() => {
      const sel = selected ? byKey.get(selected) : undefined;
      const describe = (k: string) => {
        const s = byKey.get(k);
        return s ? `${k}=${s.value}` : k;
      };
      const text = [
        `Player stats panel (${game ?? "?"}) is open.`,
        selected ? `Selected stat: ${describe(selected)}${sel && cleanText(sel.text) ? ` ("${cleanText(sel.text)}")` : ""}.` : "No stat selected.",
        pins.length ? `Pinned: ${pins.map(describe).join(", ")}.` : "Nothing pinned.",
      ].join(" ");
      const keyOnly = `${selected}|${pins.join(",")}`;
      if (keyOnly === last.current) return;
      last.current = keyOnly;
      host.updateModelContext!(text, { game, selectedStatKey: selected ?? null, pinnedStatKeys: pins });
    }, 800);
    return () => clearTimeout(t);
  }, [host, selected, pins, byKey, game]);
}
