import { useEffect, useRef, useState } from "react";
import { IconButton } from "../components";
import { Icon } from "../icons";
import { joinPath, segmentLabel, splitPath } from "./paths";
import type { ExplorerStore, Snapshot } from "./store";

/** Back / forward, breadcrumbs that re-root on click, and an editable path with Go. */
export function PathBar({ store, snap }: { store: ExplorerStore; snap: Snapshot }) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(snap.root ?? "");
  const input = useRef<HTMLInputElement>(null);
  const crumbs = useRef<HTMLDivElement>(null);
  const root = snap.root ?? "";
  const segs = splitPath(root);

  useEffect(() => { if (!editing) setDraft(root); }, [root, editing]);
  useEffect(() => { if (editing) { input.current?.focus(); input.current?.select(); } }, [editing]);
  useEffect(() => { crumbs.current?.scrollTo({ left: crumbs.current.scrollWidth }); }, [root]);

  const go = () => {
    const p = draft.trim();
    setEditing(false);
    if (p && p !== root) void store.navigate(p);
  };
  const loading = !!snap.root && !!snap.entries.get(snap.root)?.loading;

  return (
    <div className="flex h-8 items-center gap-1 border-b border-line bg-surface-2 px-1.5">
      <IconButton icon="arrowLeft" label="Back" size="sm" disabled={snap.historyIndex <= 0} onClick={() => store.back()} />
      <IconButton icon="arrowRight" label="Forward" size="sm" disabled={snap.historyIndex >= snap.history.length - 1} onClick={() => store.forward()} />
      {editing ? (
        <form className="flex min-w-0 flex-1 items-center gap-1" onSubmit={(e) => { e.preventDefault(); go(); }}>
          <input
            ref={input}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Escape") { e.preventDefault(); setEditing(false); } }}
            onBlur={() => setTimeout(() => setEditing(false), 150)}
            spellCheck={false}
            aria-label="Walker path"
            placeholder="GameController.Player.GetComponent<Life>()"
            className="h-6 min-w-0 flex-1 rounded-md border border-ring bg-surface px-2 font-code text-[11.5px] outline-none"
          />
          <button type="submit" className="h-6 shrink-0 rounded-md bg-fg px-2 text-[11px] font-medium text-surface">Go</button>
        </form>
      ) : (
        <>
          <div ref={crumbs} className="scroll-thin flex min-w-0 flex-1 items-center overflow-x-auto whitespace-nowrap" role="navigation" aria-label="Path"
            onDoubleClick={() => setEditing(true)} style={{ scrollbarWidth: "none" }}>
            {segs.map((seg, i) => {
              const path = joinPath(segs.slice(0, i + 1));
              const last = i === segs.length - 1;
              const gc = seg.startsWith("GetComponent<");
              return (
                <span key={path} className="flex items-center">
                  {i > 0 && !seg.startsWith("[") && <Icon name="chevronRight" className="mx-0.5 size-3 shrink-0 text-fg-3" />}
                  <button
                    type="button"
                    onClick={() => (last ? setEditing(true) : void store.navigate(path))}
                    title={last ? `${path}\nClick to edit the path` : `Re-root at ${path}`}
                    aria-current={last ? "page" : undefined}
                    className={`inline-flex h-6 items-center gap-1 rounded-sm px-1 font-code text-[11.5px] hover:bg-surface-3 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${last ? "font-semibold text-fg" : "text-fg-2 hover:text-fg"}`}
                  >
                    {gc && <Icon name="puzzle" className="size-3 text-info" />}
                    {gc ? segmentLabel(seg) : seg}
                  </button>
                </span>
              );
            })}
            {!root && <span className="px-1 font-code text-[11.5px] text-fg-3">Connecting…</span>}
          </div>
          <IconButton icon="pencil" label="Edit path" size="sm" onClick={() => setEditing(true)} />
          <IconButton icon="sync" label="Reload this tree" size="sm" disabled={!root || loading} iconClass={loading ? "spin" : ""} onClick={() => root && void store.refresh(root)} />
        </>
      )}
    </div>
  );
}
