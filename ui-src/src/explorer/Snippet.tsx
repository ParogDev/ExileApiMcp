import { useState } from "react";
import { IconButton, SectionLabel } from "../components";
import { Icon } from "../icons";
import type { ExplorerStore, Snapshot } from "./store";
import { SmallButton } from "./Tree";
import { shortPath } from "./paths";

/** The ticked values as ready-to-paste C# (hoisted parent, null-safe, with usings) or as a path list. */
export function SnippetPanel({ store, snap, variant }: { store: ExplorerStore; snap: Snapshot; variant: "card" | "panel" }) {
  const [tab, setTab] = useState<"csharp" | "paths">("csharp");
  const items = [...snap.checked.values()];
  if (items.length === 0) return null;
  const snippet = store.snippet();
  const text = tab === "csharp" ? snippet.csharp : snippet.paths;

  const copy = async () => {
    try { await navigator.clipboard.writeText(text); store.toast("success", tab === "csharp" ? "The C# snippet" : "The paths", "Copied"); }
    catch { store.toast("error", "Select the code instead", "Clipboard blocked"); }
  };

  return (
    <section aria-label="Snippet" className={`${variant === "panel" ? "bg-surface-2" : "bg-surface"} rounded-lg border border-line p-3 text-xs`}>
      <SectionLabel right={
        <span className="flex items-center gap-1">
          <SmallButton onClick={copy} icon="copy">Copy</SmallButton>
          <IconButton icon="x" label="Clear selection" size="sm" onClick={() => store.clearChecked()} className="-my-1" />
        </span>
      }>
        <Icon name="code" className="size-3" />Snippet <span className="tnum font-normal">{items.length}</span>
      </SectionLabel>

      <div className="mt-2 flex flex-wrap gap-1">
        {items.sort((a, b) => a.path.localeCompare(b.path)).map((i) => (
          <span key={i.path} className="inline-flex h-5 max-w-full items-center gap-1 rounded-sm border border-line bg-surface pl-2 pr-0.5 font-code text-[10.5px]" title={i.path}>
            <span className="truncate">{i.name.startsWith("[") ? shortPath(i.path, 2) : i.name}</span>
            <button type="button" aria-label={`Remove ${i.name}`} onClick={() => store.setChecked(i, false)} className="grid size-4 place-items-center rounded-sm text-fg-3 hover:bg-surface-3 hover:text-fg"><Icon name="x" className="size-2.5" /></button>
          </span>
        ))}
      </div>

      <div className="mt-2 flex items-center gap-1 border-b border-line text-[11px]" role="tablist">
        {(["csharp", "paths"] as const).map((t) => (
          <button key={t} type="button" onClick={() => setTab(t)} aria-selected={tab === t} role="tab"
            className={`-mb-px whitespace-nowrap border-b-2 px-2 py-1 font-medium ${tab === t ? "border-fg text-fg" : "border-transparent text-fg-3 hover:text-fg"}`}>
            {t === "csharp" ? "C# for a plugin" : "Paths"}
          </button>
        ))}
      </div>
      <pre className="scroll-thin mt-2 max-h-64 overflow-auto whitespace-pre rounded-md bg-surface-3/60 p-2 font-code text-[11px] leading-relaxed" tabIndex={0}>{text}</pre>
      <p className="mt-1.5 truncate text-[10.5px] text-fg-3">
        {tab === "csharp"
          ? (snippet.hoisted ? <>Null-safe; <code className="font-code">{snippet.hoisted.variable}</code> holds {shortPath(snippet.hoisted.path, 2)} so each value is one read.</> : "Null-safe, one local per value.")
          : "One path per line, for eval_path or watch_object."}
      </p>
    </section>
  );
}
