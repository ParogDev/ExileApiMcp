// Standalone pages that are the existing apps (perf, memory view, data explorer, player stats), reused as they are:
// their stores get the control center's tool caller; their HostApi has no fullscreen or Claude (nothing to ask).
// A store is created the first time its page opens and kept, so switching pages keeps the app's state.

import { useEffect, useMemo, useRef } from "react";
import { Explorer } from "../../explorer/Explorer";
import { ExplorerStore } from "../../explorer/store";
import { MemoryView } from "../../memory/MemoryView";
import { MemoryStore } from "../../memory/store";
import { Perf } from "../../perf/Perf";
import { PerfStore } from "../../perf/store";
import { PlayerStats } from "../../PlayerStats";
import { StatsStore } from "../../sync";
import type { Host } from "../host";
import type { Game } from "../types";
import { T } from "../tour/ids";

type AppName = "perf" | "memory" | "explorer" | "stats";
interface Stores { perf?: PerfStore; memory?: MemoryStore; explorer?: ExplorerStore; stats?: StatsStore }

export function EmbeddedPage({ app, host, game }: { app: AppName; host: Host; game?: Game }) {
  const stores = useRef<Stores>({});
  const call = useMemo(() => (name: string, args: Record<string, unknown>) => host.callTool(name, args), [host]);
  const hostApi = useMemo(() => ({}), []);

  const store = useMemo(() => {
    const s = stores.current;
    switch (app) {
      case "perf": return (s.perf ??= new PerfStore(call));
      case "memory": return (s.memory ??= new MemoryStore(call));
      case "explorer": return (s.explorer ??= new ExplorerStore(call));
      case "stats": return (s.stats ??= new StatsStore(call));
    }
  }, [app, call]);

  useEffect(() => {
    if (store instanceof StatsStore) store.setGameArg(game);
    else store.setToolInput(game ? { game } : undefined);
    store.start();
    return () => store.stop();
  }, [store, game]);

  return (
    <div className="-m-3 overflow-hidden rounded-card border border-line bg-surface md:-m-0" data-tour={app === "perf" ? T.perfPage : undefined}>
      {app === "perf" && <Perf store={store as PerfStore} host={hostApi} />}
      {app === "memory" && <MemoryView store={store as MemoryStore} host={hostApi} />}
      {app === "explorer" && <Explorer store={store as ExplorerStore} host={hostApi} />}
      {app === "stats" && <PlayerStats store={store as StatsStore} host={hostApi} />}
    </div>
  );
}
