import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore } from "react";
import { Banner, Pinned, Resistances, StatusPill, Toasts, Vitals } from "./components";
import { cleanText, fmt, resists } from "./format";
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

  const byKey = useMemo(() => new Map(stats.map((s) => [s.key, s])), [stats]);
  const pinnedSet = useMemo(() => new Set(view?.pinnedStatKeys ?? []), [view?.pinnedStatKeys]);
  const resistList = useMemo(() => resists(stats), [stats]);

  const select = useCallback((key: string | null) => void store.select(key), [store]);
  const pin = useCallback((key: string, pinned: boolean) => void store.setPinned(key, pinned), [store]);
  const load = useCallback((key: string) => store.getStat(key), [store]);

  useModelContext(host, snap.view?.selectedStatKey, snap.view?.pinnedStatKeys, byKey, game);

  const fullscreen = host.fullscreen?.active;
  const notInGame = snap.inGame === false;

  return (
    <main className={`mx-auto flex max-w-5xl flex-col gap-2.5 p-3 ${fullscreen ? "h-screen" : ""}`}>
      <header className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <h1 className="text-base font-semibold">Player stats</h1>
        <StatusPill game={game} conn={conn} latencyMs={snap.latencyMs} rev={view?.rev} pending={snap.pending}
          title={`tool calls: ${Object.entries(snap.calls).map(([k, v]) => `${k} ${v}`).join(", ")}`} />
        <span className="ml-auto" />
        {host.fullscreen && (
          <button type="button" onClick={host.fullscreen.toggle}
            className="rounded-md border border-line px-2 py-1 text-xs text-fg-2 hover:bg-surface-2 hover:text-fg"
            title={fullscreen ? "Back to the conversation" : "Expand the panel"}>
            {fullscreen ? "Exit full screen" : "Full screen"}
          </button>
        )}
      </header>

      {conn === "offline" && (
        <Banner tone="danger" action={<RetryButton onClick={() => store.kick()} />}>
          <strong>HUD bridge unreachable.</strong> {snap.error ?? ""} Showing the last data received.
          Is the HUD running with “Whats An AI Bridge” enabled?
        </Banner>
      )}
      {conn !== "offline" && notInGame && (
        <Banner tone="warning">Not in game (character select or loading). Values update once you're in an area.</Banner>
      )}

      <div className="grid gap-2.5 sm:grid-cols-2">
        <Vitals vitals={vitals} game={game} />
        <Resistances list={resistList} pinnedKeys={pinnedSet} selectedKey={view?.selectedStatKey} loaded={snap.statsLoaded}
          onPin={pin} onSelect={(k) => select(view?.selectedStatKey === k ? null : k)} />
      </div>

      {view && (
        <Pinned keys={view.pinnedStatKeys} stats={byKey} selectedKey={view.selectedStatKey}
          onUnpin={(k) => pin(k, false)} onSelect={(k) => select(view.selectedStatKey === k ? null : k)} />
      )}

      {view ? (
        <StatTable
          stats={stats}
          loaded={snap.statsLoaded}
          view={view}
          changes={snap.changes}
          maxHeight={fullscreen ? "calc(100vh - 26rem)" : "24rem"}
          onFilter={(t, c) => void store.setFilter(t, c)}
          onSort={(by, desc) => void store.setSort(by, desc)}
          onPin={pin}
          onSelect={select}
          renderDetail={(s) => (
            <StatDetail stat={s} pinned={pinnedSet.has(s.key)} load={load}
              onPin={(p) => pin(s.key, p)}
              onAsk={host.ask && ((st) => host.ask!(askText(st, game)))}
              onCopied={(ok) => store.toast(ok ? "info" : "error", ok ? `Copied ${s.key}` : "Clipboard blocked by the host - select the key text instead")} />
          )}
        />
      ) : conn !== "offline" && (
        <p className="text-xs text-fg-3">Waiting for the HUD…</p>
      )}

      <Toasts toasts={snap.toasts} onDismiss={(id) => store.dismiss(id)} />
    </main>
  );
}

function RetryButton({ onClick }: { onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} className="rounded-md border border-current px-2 py-0.5 font-medium hover:bg-surface">
      Retry
    </button>
  );
}

function askText(s: StatItem, game?: string): string {
  const text = cleanText(s.text);
  return `In my ${game === "poe2" ? "PoE2" : "PoE1"} player stats, explain \`${s.key}\` (currently ${fmt(s.value)}${text ? `, "${text}"` : ""}). ` +
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
        sel ? `Selected stat: ${describe(sel.key)}${cleanText(sel.text) ? ` ("${cleanText(sel.text)}")` : ""}.` : "No stat selected.",
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
