import { useEffect, useState } from "react";
import { cleanText, fmt } from "./format";
import type { GetStatResult, StatItem } from "./types";

export interface StatDetailProps {
  stat: StatItem;
  pinned: boolean;
  load: (key: string) => Promise<GetStatResult | undefined>;
  onPin: (pinned: boolean) => void;
  onAsk?: (stat: StatItem) => void;
  onCopied: (ok: boolean) => void;
}

/** Inline expansion under the selected row: Stats.dat record details and actions. */
export function StatDetail({ stat, pinned, load, onPin, onAsk, onCopied }: StatDetailProps) {
  const [info, setInfo] = useState<GetStatResult | undefined>();
  useEffect(() => {
    let live = true;
    setInfo(undefined);
    load(stat.key).then((r) => live && setInfo(r), () => {});
    return () => { live = false; };
  }, [stat.key, load]);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(stat.key);
      onCopied(true);
    } catch {
      onCopied(false); // sandboxed iframes may not grant clipboard-write
    }
  };

  const text = cleanText(stat.text);
  const rows: [string, string][] = [
    ["Key", stat.key],
    ["Stat id", `${stat.id} (this build; keys are stable, ids shift per patch)`],
    ["Value", fmt(stat.value)],
    ["Category", stat.category],
  ];
  if (info?.recordType) rows.push(["Record type", info.recordType]);
  if (info) rows.push(["Weapon-local", info.isWeaponLocal ? "yes" : "no"]);
  if (stat.text && stat.text !== text) rows.push(["Raw text", stat.text]);

  return (
    <div className="border-b border-line bg-surface-2 px-3 py-2.5 text-xs" onClick={(e) => e.stopPropagation()}>
      {text && <p className="mb-2 text-sm">{text}</p>}
      <dl className="grid grid-cols-[6.5rem_1fr] gap-x-3 gap-y-1">
        {rows.map(([k, v]) => (
          <div key={k} className="contents">
            <dt className="text-fg-3">{k}</dt>
            <dd className={`min-w-0 break-words ${k === "Key" ? "font-code" : ""}`}>{v}</dd>
          </div>
        ))}
      </dl>
      <div className="mt-2.5 flex flex-wrap gap-1.5">
        <Action onClick={() => onPin(!pinned)}>{pinned ? "Unpin" : "Pin"}</Action>
        <Action onClick={copy}>Copy key</Action>
        {onAsk && <Action onClick={() => onAsk(stat)} primary>Ask Claude about this stat</Action>}
      </div>
    </div>
  );
}

function Action({ children, onClick, primary }: { children: React.ReactNode; onClick: () => void; primary?: boolean }) {
  return (
    <button type="button" onClick={onClick}
      className={`rounded-md border px-2.5 py-1 text-xs transition-colors focus-visible:outline-2 focus-visible:outline-ring ${primary ? "border-fg bg-fg text-surface hover:opacity-90" : "border-line-2 bg-surface hover:bg-surface-3"}`}>
      {children}
    </button>
  );
}
