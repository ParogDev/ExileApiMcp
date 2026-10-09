import { useEffect, useState, type ReactNode } from "react";
import { DELTA_VISIBLE_MS, IconButton } from "./components";
import { CATEGORY_DOT, CATEGORY_LABEL, ago, cleanText, fmtStat, humanizeKey, resistElementOf, resistLayers, signed, ELEMENT_META } from "./format";
import { Icon, type IconName } from "./icons";
import type { StatChange } from "./sync";
import type { GetStatResult, StatItem } from "./types";

export interface StatDetailProps {
  statKey: string;
  /** Undefined when the key is pinned/selected but not on the character right now. */
  stat?: StatItem;
  stats: StatItem[];
  pinned: boolean;
  change?: StatChange;
  now: number;
  /** The selection came from the in-game panel or an agent, not from this app. */
  remote: boolean;
  load: (key: string) => Promise<GetStatResult | undefined>;
  onPin: (pinned: boolean) => void;
  onAsk?: () => void;
  onCopied: (ok: boolean) => void;
  onClose: () => void;
  /** "sheet" sits inside the table (inline); "panel" is a card in the fullscreen sidebar. */
  variant: "sheet" | "panel";
}

/** Everything about one stat: value, key, Stats.dat record details, resistance layers, actions. */
export function StatDetail({ statKey, stat, stats, pinned, change, now, remote, load, onPin, onAsk, onCopied, onClose, variant }: StatDetailProps) {
  const [info, setInfo] = useState<GetStatResult | undefined>();
  useEffect(() => {
    let live = true;
    setInfo(undefined);
    load(statKey).then((r) => live && setInfo(r), () => {});
    return () => { live = false; };
  }, [statKey, load]);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(statKey);
      onCopied(true);
    } catch {
      onCopied(false); // sandboxed iframes may not grant clipboard-write
    }
  };

  const text = cleanText(stat?.text);
  const category = stat?.category ?? info?.stat.category;
  const element = resistElementOf(statKey);
  const recent = change && now - change.at < DELTA_VISIBLE_MS ? change : undefined;
  const meta = [
    info?.recordType && `${info.recordType} record`,
    info && (info.isWeaponLocal ? "weapon-local" : "global"),
    stat && `id ${stat.id}`,
  ].filter(Boolean) as string[];
  const innerBg = variant === "panel" ? "bg-surface" : "bg-surface-2";

  return (
    <div className={variant === "panel" ? "rounded-lg border border-line bg-surface-2 p-3" : "px-3 pb-3 pt-2.5"} onClick={(e) => e.stopPropagation()}>
      <div className="flex items-center gap-1.5 ds-label text-fg-3">
        {category && <><span className={`size-1.5 rounded-full ${CATEGORY_DOT[category]}`} aria-hidden />{CATEGORY_LABEL[category]}</>}
        {meta.length > 0 && <span className="truncate font-normal normal-case tracking-normal" title="Stats.dat record details; keys are stable across patches, ids are not">· {meta.join(" · ")}</span>}
        {remote && (
          <span className="ml-1 flex shrink-0 items-center gap-1 whitespace-nowrap rounded-sm bg-info/10 px-1.5 py-px font-medium normal-case tracking-normal text-info" title="This stat was selected in the in-game panel or by Claude; the selection is shared.">
            <Icon name="sync" className="size-3" />selected elsewhere
          </span>
        )}
        <IconButton icon="x" label="Close details" size="sm" onClick={onClose} className="-my-1 -mr-1.5 ml-auto" />
      </div>

      <h3 className="mt-1 text-[13px] font-semibold leading-snug">{text ?? humanizeKey(statKey)}</h3>

      <div className="mt-1.5 flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <span className={`tnum text-xl font-semibold leading-none ${stat && stat.value < 0 ? "text-danger" : stat ? "" : "text-fg-3"}`}>
          {stat ? fmtStat(statKey, stat.value) : "—"}
        </span>
        {recent && (
          <span className={`tnum text-xs ${recent.delta > 0 ? "text-success" : "text-danger"}`}>
            {signed(recent.delta)} <span className="text-fg-3">{ago(now - recent.at)}</span>
          </span>
        )}
        <span className="flex min-w-0 max-w-full items-center gap-0.5 font-code text-[11px] text-fg-2">
          <span className="truncate" title={statKey}>{statKey}</span>
          <IconButton icon="copy" label="Copy key" size="sm" onClick={copy} className="-my-1" />
        </span>
      </div>

      {!stat && <p className="mt-1.5 text-[11.5px] text-fg-3">Not on the character right now; it counts as 0 until something grants it.</p>}
      {stat?.text && stat.text !== text && <p className="mt-1 break-words font-code text-[10.5px] text-fg-3" title="Raw in-game text">{stat.text}</p>}

      {element && (
        <div className="mt-2">
          <div className={`ds-label ${ELEMENT_META[element].text}`}>{ELEMENT_META[element].label} resistance layers</div>
          <div className="mt-1 grid grid-cols-4 gap-1">
            {resistLayers(stats, element).map((l) => (
              <div key={l.key} className={`rounded-md ${innerBg} px-1.5 py-1 ${l.key === statKey ? "ring-1 ring-ring" : ""}`}
                title={l.assumed ? "No maximum_*_resistance stat exposed; the default 75% cap is assumed" : l.value === undefined ? `${l.key} · not on the character` : l.key}>
                <div className="text-[10px] text-fg-3">{l.label}{l.assumed ? " (assumed)" : ""}</div>
                <div className={`tnum text-[12.5px] font-semibold ${l.value === undefined || l.assumed ? "text-fg-3" : ""}`}>{l.value === undefined ? "—" : `${l.value}%`}</div>
              </div>
            ))}
          </div>
        </div>
      )}

      <div className="mt-2.5 flex flex-wrap gap-1.5">
        <Action icon="pin" onClick={() => onPin(!pinned)} active={pinned}>{pinned ? "Pinned" : "Pin"}</Action>
        {onAsk && <Action icon="sparkle" onClick={onAsk} primary>Ask Claude</Action>}
      </div>
    </div>
  );
}

function Action({ children, icon, onClick, primary, active }: { children: ReactNode; icon: IconName; onClick: () => void; primary?: boolean; active?: boolean }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      className={`flex h-7 items-center gap-1.5 rounded-md border px-2.5 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${
        primary ? "border-fg bg-fg text-surface hover:opacity-90"
        : active ? "border-warning/40 bg-warning/10 text-warning hover:bg-warning/15"
        : "border-line-2 bg-surface text-fg hover:bg-surface-3"}`}
    >
      <Icon name={icon} className="size-3.5" fill={active && icon === "pin" ? "currentColor" : "none"} />
      {children}
    </button>
  );
}
