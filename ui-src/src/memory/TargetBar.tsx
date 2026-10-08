import { useEffect, useRef, useState } from "react";
import { IconButton } from "../components";
import { Icon } from "../icons";
import { DEFAULT_EXTEND, READ_SIZE, type MemoryStore, type Snapshot, type View } from "./store";

const EXTENDS = [0, 64, 256, 1024, 2048];
const SIZES = [64, 256, 512, 1024, 4096];

/** Back / forward, the pointer chain as crumbs, the target (path or address) with Go, and the struct/extend options. */
export function TargetBar({ store, snap, view }: { store: MemoryStore; snap: Snapshot; view?: View }) {
  const text = view?.target.path ?? view?.target.address ?? "";
  const [draft, setDraft] = useState(text);
  const [type, setType] = useState(view?.target.type ?? "");
  const [options, setOptions] = useState(!!view?.target.type);
  const input = useRef<HTMLInputElement>(null);
  const crumbs = useRef<HTMLDivElement>(null);
  useEffect(() => { setDraft(text); }, [text, view?.id]);
  useEffect(() => { setType(view?.target.type ?? ""); }, [view?.id, view?.target.type]);
  useEffect(() => { crumbs.current?.scrollTo({ left: crumbs.current.scrollWidth }); }, [snap.index]);

  const dirty = draft.trim() !== text;
  const go = () => {
    const t = draft.trim();
    if (!t) return;
    if (t === text && !view?.loading) { if ((type.trim() || undefined) !== view?.target.type) store.setType(type); else store.reload(); return; }
    store.go(t, type.trim() || undefined, view?.mode === "layout" ? view.target.extend : undefined);
  };
  const chain = snap.views.slice(0, snap.index + 1);

  return (
    <div className="border-b border-line bg-surface-2">
      <div className="flex h-8 items-center gap-1 px-1.5">
        <IconButton icon="arrowLeft" label="Back" size="sm" disabled={snap.index <= 0} onClick={() => store.back()} />
        <IconButton icon="arrowRight" label="Forward" size="sm" disabled={snap.index >= snap.views.length - 1} onClick={() => store.forward()} />
        <div ref={crumbs} role="navigation" aria-label="Pointer chain" className="flex min-w-0 flex-1 items-center overflow-x-auto whitespace-nowrap" style={{ scrollbarWidth: "none" }}>
          {chain.map((v, i) => {
            const last = i === chain.length - 1;
            return (
              <span key={v.id} className="flex items-center">
                {i > 0 && <Icon name={v.via ? "arrowUpRight" : "chevronRight"} className={`mx-0.5 size-3 shrink-0 ${v.via ? "text-m-ptr" : "text-fg-3"}`} />}
                <button type="button" onClick={() => (last ? input.current?.select() : store.goTo(i))} aria-current={last ? "page" : undefined}
                  title={[v.via ? `followed ${v.via}` : null, v.target.path ?? v.target.address, v.mode === "layout" ? (v.target.type ?? (v.data && "struct" in v.data ? v.data.struct : "struct layout")) : `raw read, ${v.target.size ?? READ_SIZE} bytes`].filter(Boolean).join("\n")}
                  className={`inline-flex h-6 items-center gap-1 rounded px-1 font-code text-[11.5px] hover:bg-surface-3 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${last ? "font-semibold text-fg" : "text-fg-2 hover:text-fg"}`}>
                  {v.mode === "read" && <Icon name="grid" className="size-3 text-fg-3" />}
                  {v.label}
                  {v.via && <span className="text-[10px] font-normal text-fg-3">{v.via.replace(/^\+\d+ /, "")}</span>}
                </button>
              </span>
            );
          })}
          {!chain.length && <span className="px-1 font-code text-[11.5px] text-fg-3">Connecting…</span>}
        </div>
        <IconButton icon="sliders" label="Struct type and read size options" size="sm" active={options} onClick={() => setOptions((o) => !o)} />
        <IconButton icon="sync" label="Read again" size="sm" disabled={!view || view.loading} iconClass={view?.loading ? "spin" : ""} onClick={() => store.reload()} />
      </div>

      <form className="flex items-center gap-1.5 px-2 pb-1.5" onSubmit={(e) => { e.preventDefault(); go(); }}>
        <div className="relative min-w-0 flex-1">
          <Icon name={draft.trim().startsWith("0x") || /^\d+$/.test(draft.trim()) ? "hash" : "target"} className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-fg-3" />
          <input
            ref={input}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Escape") { e.preventDefault(); setDraft(text); (e.target as HTMLInputElement).blur(); } }}
            spellCheck={false}
            aria-label="Target: a walker path to a memory object, or an address"
            placeholder="GameController.Player.GetComponent<Life>()  or  0x41137850530"
            className="h-7 w-full rounded-md border border-transparent bg-surface pl-7 pr-2 font-code text-[11.5px] placeholder:text-fg-3 focus:border-ring focus:outline-none"
          />
        </div>
        <button type="submit" disabled={!draft.trim() || !!view?.loading} className={`h-7 shrink-0 rounded-md px-2.5 text-[11px] font-medium disabled:opacity-50 ${dirty ? "bg-fg text-surface" : "border border-line bg-surface text-fg-2 hover:text-fg"}`}>
          {dirty ? "Go" : "Read"}
        </button>
      </form>

      {options && (
        <div className="fade-in flex flex-wrap items-center gap-x-3 gap-y-1.5 border-t border-line/60 px-2 py-1.5 text-[11px]">
          <label className="flex min-w-0 flex-1 basis-[14rem] items-center gap-1.5">
            <span className="shrink-0 text-fg-3">Struct</span>
            <input value={type} onChange={(e) => setType(e.target.value)} onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); store.setType(type); } }}
              placeholder={view?.mode === "layout" && view.data && "struct" in view.data ? `${view.data.struct} (the HUD's own)` : "GameOffsets.LifeComponentOffsets"} spellCheck={false}
              aria-label="Struct type to overlay (hud_type lists them)"
              className="h-6 min-w-0 flex-1 rounded-md border border-transparent bg-surface px-2 font-code text-[11px] placeholder:text-fg-3 focus:border-ring focus:outline-none" />
            {(type.trim() || undefined) !== (view?.target.type ?? undefined) && <button type="button" onClick={() => store.setType(type)} className="h-6 shrink-0 rounded-md bg-fg px-2 text-[11px] font-medium text-surface">Apply</button>}
          </label>
          {view?.mode === "layout" ? (
            <label className="flex items-center gap-1.5">
              <span className="text-fg-3" title="Bytes to read past the struct's declared end, to find members the HUD doesn't map">Extend</span>
              <select value={view.target.extend ?? DEFAULT_EXTEND} onChange={(e) => store.setExtend(Number(e.target.value))} aria-label="Bytes to read past the struct end" className="h-6 rounded-md border border-transparent bg-surface px-1 font-code text-[11px] focus:border-ring focus:outline-none">
                {EXTENDS.map((n) => <option key={n} value={n}>+{n} B</option>)}
              </select>
            </label>
          ) : view ? (
            <label className="flex items-center gap-1.5">
              <span className="text-fg-3">Size</span>
              <select value={view.target.size ?? READ_SIZE} onChange={(e) => store.setReadSize(Number(e.target.value))} aria-label="Bytes to read" className="h-6 rounded-md border border-transparent bg-surface px-1 font-code text-[11px] focus:border-ring focus:outline-none">
                {SIZES.map((n) => <option key={n} value={n}>{n} B</option>)}
              </select>
            </label>
          ) : null}
          {view?.mode === "read" && !view.target.path && <span className="text-fg-3">Give a struct to overlay its fields on these bytes.</span>}
        </div>
      )}
    </div>
  );
}
