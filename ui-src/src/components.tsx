import { useEffect, useRef, useState, type ReactNode } from "react";
import { Icon, type IconName } from "./icons";
import type { Connection, RemoteChange, StatChange, Toast, ViewField, VitalSample } from "./sync";
import type { Game, StatItem, Vitals as VitalsDto, ViewState } from "./types";
import { ELEMENT_META, ago, fmt, fmtStat, signed, statLabel, type Resist } from "./format";

/** How long the delta column and chip deltas stay visible after a change. */
export const DELTA_VISIBLE_MS = 30_000;
/** How long a changed value flashes. */
export const FLASH_MS = 1_600;

/** A clock that re-renders the caller every `everyMs` (for "12 s ago" and fading deltas). */
export function useNow(everyMs = 1000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), everyMs);
    return () => clearInterval(t);
  }, [everyMs]);
  return now;
}

// ── Primitives ──────────────────────────────────────────────────────

export function IconButton({ icon, label, onClick, active, className = "", size = "md", disabled, iconClass = "" }: {
  icon: IconName; label: string; onClick?: (e: React.MouseEvent) => void; active?: boolean; className?: string;
  size?: "sm" | "md"; disabled?: boolean; iconClass?: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={label}
      title={label}
      aria-pressed={active}
      className={`grid shrink-0 place-items-center rounded-md transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-40 ${size === "sm" ? "size-6" : "size-7"} ${active ? "bg-surface-3 text-fg" : "text-fg-2 hover:bg-surface-3 hover:text-fg"} ${className}`}
    >
      <Icon name={icon} className={`${size === "sm" ? "size-3.5" : "size-4"} ${iconClass}`} />
    </button>
  );
}

export function PinButton({ pinned, onToggle, label, className = "", size = "md" }: {
  pinned: boolean; onToggle: () => void; label: string; className?: string; size?: "sm" | "md";
}) {
  return (
    <button
      type="button"
      onClick={(e) => { e.stopPropagation(); onToggle(); }}
      aria-pressed={pinned}
      aria-label={`${pinned ? "Unpin" : "Pin"} ${label}`}
      title={pinned ? "Unpin · also unpins in the in-game panel" : "Pin · also pins in the in-game panel"}
      data-pinned={pinned || undefined}
      className={`grid shrink-0 place-items-center rounded-md transition-colors hover:bg-surface-3 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${size === "sm" ? "size-5" : "size-6"} ${pinned ? "text-warning" : "text-fg-3 hover:text-fg"} ${className}`}
    >
      <Icon name="pin" className={size === "sm" ? "size-3" : "size-3.5"} fill={pinned ? "currentColor" : "none"} />
    </button>
  );
}

export function SectionLabel({ children, right, className = "" }: { children: ReactNode; right?: ReactNode; className?: string }) {
  return (
    <div className={`flex items-center gap-2 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3 ${className}`}>
      <span className="flex items-center gap-1.5">{children}</span>
      {right && <span className="ml-auto font-normal normal-case tracking-normal">{right}</span>}
    </div>
  );
}

// ── Sync status (header pill + popover) ─────────────────────────────

const FIELD_LABEL: Record<ViewField, string> = { selection: "selection", pins: "pins", filter: "filter", sort: "sort" };

export interface SyncPillProps {
  conn: Connection;
  pending: number;
  latencyMs?: number;
  lastOkAt?: number;
  remote?: RemoteChange;
  view?: ViewState;
  game?: Game;
  calls: Record<string, number>;
  error?: string;
  now: number;
}

export function SyncPill({ conn, pending, latencyMs, lastOkAt, remote, view, game, calls, error, now }: SyncPillProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", onDoc);
    document.addEventListener("keydown", onKey);
    return () => { document.removeEventListener("mousedown", onDoc); document.removeEventListener("keydown", onKey); };
  }, [open]);

  const fresh = remote && now - remote.at < 3000 ? remote : undefined;
  let tone: string, label: string, icon: ReactNode;
  if (conn === "offline") {
    tone = "border-danger/30 bg-danger/10 text-danger"; label = "Offline"; icon = <Icon name="offline" className="size-3.5" />;
  } else if (conn === "stale") {
    tone = "border-warning/30 bg-warning/10 text-warning"; label = "Reconnecting"; icon = <Icon name="sync" className="spin size-3.5" />;
  } else if (conn === "connecting") {
    tone = "border-line text-fg-3"; label = "Connecting"; icon = <Icon name="sync" className="spin size-3.5" />;
  } else if (pending > 0) {
    tone = "border-line text-fg-2"; label = "Saving"; icon = <Icon name="sync" className="spin size-3.5" />;
  } else if (fresh) {
    tone = "border-info/30 bg-info/10 text-info"; icon = <Icon name="sync" className="size-3.5" />;
    label = fresh.fields.length === 1 ? `Synced ${FIELD_LABEL[fresh.fields[0]]}` : `Synced ${fresh.fields.length} changes`;
  } else {
    tone = "border-success/30 bg-success/10 text-success"; label = "Live";
    icon = <span className="grid size-3.5 place-items-center"><span className="size-1.5 rounded-full bg-success shadow-[0_0_0_3px_color-mix(in_oklab,var(--color-success)_25%,transparent)]" /></span>;
  }

  const totalCalls = Object.values(calls).reduce((a, b) => a + b, 0);
  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        aria-haspopup="dialog"
        title={fresh ? `Changed elsewhere (HUD panel or Claude): ${fresh.fields.map((f) => FIELD_LABEL[f]).join(", ")}` : "Shared view: how this panel stays in sync"}
        className={`flex h-7 max-w-[9.5rem] items-center gap-1.5 rounded-full border px-2 text-[11px] font-medium transition-colors hover:bg-surface-3 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${tone}`}
      >
        {icon}
        <span className="truncate" role="status" aria-live="polite">{label}</span>
      </button>
      {open && (
        <div role="dialog" aria-label="Shared view" className="fade-in absolute right-0 top-full z-30 mt-1.5 w-72 rounded-lg border border-line bg-surface p-3 text-xs shadow-xl">
          <p className="mb-2 leading-relaxed text-fg-2">
            Pins, filter, sort and selection are <strong className="font-semibold text-fg">one shared view</strong>: the in-game panel, this app and Claude all edit the same state and see each other's changes within a second.
          </p>
          <dl className="grid grid-cols-[6.5rem_1fr] gap-y-1">
            <dt className="text-fg-3">Game</dt><dd>{game === "poe2" ? "Path of Exile 2" : game === "poe1" ? "Path of Exile" : "—"}</dd>
            <dt className="text-fg-3">Connection</dt>
            <dd className="min-w-0 break-words">
              {conn === "live" ? <>Live{latencyMs !== undefined && <span className="tnum text-fg-3"> · {latencyMs} ms</span>}</>
                : conn === "connecting" ? "Connecting…"
                : <span className="text-danger">{conn === "offline" ? "Bridge unreachable" : "Reconnecting"}{error ? ` — ${error}` : ""}</span>}
            </dd>
            <dt className="text-fg-3">Last update</dt><dd className="tnum">{lastOkAt ? ago(now - lastOkAt) : "never"}</dd>
            <dt className="text-fg-3">View revision</dt><dd className="tnum">{view ? `rev ${view.rev}` : "—"}{pending > 0 && <span className="text-fg-3"> · saving {pending}</span>}</dd>
            <dt className="text-fg-3">In-game panel</dt><dd>{view ? (view.panelOpen ? "open" : "closed") : "—"}</dd>
            <dt className="text-fg-3">Tool calls</dt>
            <dd className="tnum" title={Object.entries(calls).map(([k, v]) => `${k}: ${v}`).join("\n")}>{totalCalls}</dd>
          </dl>
        </div>
      )}
    </div>
  );
}

// ── Vitals ──────────────────────────────────────────────────────────

type VitalKind = "life" | "es" | "mana";
const VITAL_STYLE: Record<VitalKind, { text: string; bg: string; icon: IconName; label: string; name: string }> = {
  life: { text: "text-life", bg: "bg-life", icon: "heart", label: "Life", name: "Life" },
  es: { text: "text-es", bg: "bg-es", icon: "shield", label: "ES", name: "Energy shield" },
  mana: { text: "text-mana", bg: "bg-mana", icon: "drop", label: "Mana", name: "Mana" },
};

export function Sparkline({ values, max, className = "" }: { values: number[]; max: number; className?: string }) {
  if (values.length < 2) return <span className={className} aria-hidden />;
  const w = 48, h = 16, n = values.length;
  const top = Math.max(max, ...values, 1);
  const pts = values.map((v, i) => [(i / (n - 1)) * w, h - 1 - (v / top) * (h - 2)] as const);
  const line = pts.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(" ");
  return (
    <svg viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" className={className} aria-hidden>
      <polygon points={`0,${h} ${line} ${w},${h}`} fill="currentColor" opacity="0.12" />
      <polyline points={line} fill="none" stroke="currentColor" strokeWidth="1.25" opacity="0.6" vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
    </svg>
  );
}

function VitalTile({ kind, value, max, history, dim }: { kind: VitalKind; value: number; max: number; history: number[]; dim?: boolean }) {
  const st = VITAL_STYLE[kind];
  const pct = max > 0 ? Math.max(0, Math.min(1, value / max)) : 0;
  const low = kind === "life" && max > 0 && pct < 0.35;
  // A trace only once the value has actually moved; a flat line is noise.
  const trend = history.length > 2 && Math.min(...history) !== Math.max(...history);
  return (
    <div className={`relative min-w-0 overflow-hidden rounded-lg bg-surface-2 px-2.5 py-2 ${dim ? "opacity-60" : ""}`}
      title={`${st.name}: ${fmt(value)} of ${fmt(max)}${trend ? " · recent trend shown behind the value" : ""}`}>
      {trend && <Sparkline values={history} max={max} className={`pointer-events-none absolute right-2 top-6 h-5 w-[42%] ${st.text}`} />}
      <div className="relative flex items-center gap-1 text-[11px] font-medium text-fg-2">
        <Icon name={st.icon} className={`size-3 ${st.text}`} />
        {/* Full name only where the tile is wide enough: stacked layouts between xs and sm. */}
        <span className="truncate" aria-label={st.name}>
          {st.label === st.name ? st.label : <><span className="xs:hidden sm:inline">{st.label}</span><span className="hidden xs:inline sm:hidden">{st.name}</span></>}
        </span>
        <span className={`tnum ml-auto ${low ? "font-semibold text-danger" : "text-fg-3"}`}>{Math.round(pct * 100)}%</span>
      </div>
      <div className="relative mt-1 flex items-end gap-1.5">
        <span className="tnum text-[17px] font-semibold leading-none">{fmt(value)}</span>
        <span className="tnum pb-px text-[11px] leading-none text-fg-3">/ {fmt(max)}</span>
      </div>
      <div className="relative mt-1.5 h-1.5 overflow-hidden rounded-sm bg-surface-3" role="meter" aria-label={st.name} aria-valuenow={value} aria-valuemin={0} aria-valuemax={max}>
        <div className={`absolute inset-y-0 left-0 ${st.bg} transition-[width] duration-500 ${low ? "breathe" : ""}`} style={{ width: `${pct * 100}%` }} />
        <div className="seg-gaps absolute inset-0" aria-hidden />
      </div>
    </div>
  );
}

export function VitalsRow({ vitals, history, dim }: { vitals?: VitalsDto; history: VitalSample[]; dim?: boolean }) {
  if (!vitals) return <Skeleton kind="tiles" count={3} />;
  const tiles: { kind: VitalKind; value: number; max: number; history: number[] }[] = [
    { kind: "life", value: vitals.hp, max: vitals.maxHp, history: history.map((h) => h.hp) },
    ...(vitals.maxEs > 0 ? [{ kind: "es" as const, value: vitals.es, max: vitals.maxEs, history: history.map((h) => h.es) }] : []),
    { kind: "mana", value: vitals.mana, max: vitals.maxMana, history: history.map((h) => h.mana) },
  ];
  return (
    <div className={`grid gap-1.5 ${tiles.length === 3 ? "grid-cols-3" : "grid-cols-2"}`}>
      {tiles.map((t) => <VitalTile key={t.kind} {...t} dim={dim} />)}
    </div>
  );
}

// ── Resistances ─────────────────────────────────────────────────────

function ResistTile({ r, pinned, selected, onPin, onSelect }: { r: Resist; pinned: boolean; selected: boolean; onPin: () => void; onSelect: () => void }) {
  const m = ELEMENT_META[r.element];
  const has = r.value !== undefined;
  const v = r.value ?? 0;
  const over = Math.max(0, (r.uncapped ?? v) - r.cap);
  const negative = v < 0;
  const capped = has && v >= r.cap;
  const status = !has ? { text: "no stat", tone: "text-fg-3" }
    : negative ? { text: "below zero", tone: "text-danger" }
    : capped ? { text: over > 0 ? `capped +${over}` : "capped", tone: "text-success" }
    : { text: `${r.cap - v} to cap`, tone: "text-warning" };
  const title = `${m.label} resistance: ${has ? `${v}%` : "no stat on the character (0%)"} · cap ${r.cap}%${r.capAssumed ? " (assumed)" : ""}${over > 0 ? ` · ${over}% over cap` : ""}\n${r.key}`;
  return (
    <div
      role="button"
      tabIndex={0}
      aria-pressed={selected}
      aria-label={`${m.label} resistance ${v}%, ${status.text}`}
      title={title}
      onClick={onSelect}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onSelect(); } }}
      className={`group relative min-w-0 cursor-pointer rounded-lg bg-surface-2 px-1.5 py-1.5 text-left outline-none transition-[background-color,box-shadow] hover:bg-surface-3/70 focus-visible:ring-2 focus-visible:ring-ring ${selected ? "ring-2 ring-ring" : ""}`}
    >
      <div className={`flex items-center gap-1 text-[10.5px] font-medium ${m.text}`}>
        <Icon name={m.icon} className="size-3 shrink-0" />
        <span className="truncate">{m.label}</span>
      </div>
      <div className={`tnum mt-1 text-[17px] font-semibold leading-none ${!has ? "text-fg-3" : negative ? "text-danger" : ""}`}>{v}%</div>
      <div className="mt-1 flex h-4 items-center">
        <span className={`truncate text-[10px] leading-none ${status.tone}`}>{status.text}</span>
        <PinButton pinned={pinned} onToggle={onPin} label={`${m.label} resistance`} size="sm"
          className={`-mr-1 ml-auto ${pinned ? "" : "opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 focus-visible:opacity-100"}`} />
      </div>
      <div className="relative mt-1 h-1.5 rounded-sm bg-surface-3" aria-hidden>
        {negative ? (
          <div className="absolute inset-y-0 left-0 rounded-sm bg-danger" style={{ width: `${Math.min(100, -v)}%` }} />
        ) : (
          <>
            <div className={`absolute inset-y-0 left-0 rounded-sm ${m.bg} transition-[width] duration-500`} style={{ width: `${Math.min(v, r.cap)}%` }} />
            {over > 0 && <div className={`ghost-hatch absolute inset-y-0 rounded-r-sm ${m.text}`} style={{ left: `${r.cap}%`, width: `${Math.min(over, 100 - r.cap)}%` }} />}
          </>
        )}
        <div className="absolute -inset-y-0.5 w-px bg-fg-2" style={{ left: `${r.cap}%` }} />
      </div>
    </div>
  );
}

export function ResistGrid({ list, pinnedKeys, selectedKey, loaded, onPin, onSelect }: {
  list: Resist[]; pinnedKeys: Set<string>; selectedKey?: string | null; loaded: boolean;
  onPin: (key: string, pinned: boolean) => void; onSelect: (key: string) => void;
}) {
  if (!loaded && list.every((r) => r.value === undefined)) return <Skeleton kind="tiles" count={4} />;
  const assumed = list.some((r) => r.capAssumed && r.value !== undefined);
  return (
    <div>
      <div className="grid grid-cols-4 gap-1.5">
        {list.map((r) => (
          <ResistTile key={r.element} r={r} pinned={pinnedKeys.has(r.key)} selected={selectedKey === r.key}
            onPin={() => onPin(r.key, !pinnedKeys.has(r.key))} onSelect={() => onSelect(r.key)} />
        ))}
      </div>
      {assumed && (
        <p className="mt-1 truncate text-right text-[10px] text-fg-3" title="The game exposes no maximum_*_resistance stat for this character, so the default 75% cap is shown.">
          75% cap assumed · no maximum_*_resistance stat
        </p>
      )}
    </div>
  );
}

// ── Pinned ──────────────────────────────────────────────────────────

export function PinnedChips({ keys, byKey, selectedKey, changes, now, onUnpin, onSelect }: {
  keys: string[]; byKey: Map<string, StatItem>; selectedKey?: string | null; changes: Record<string, StatChange>; now: number;
  onUnpin: (key: string) => void; onSelect: (key: string) => void;
}) {
  return (
    <section aria-label="Pinned stats">
      <SectionLabel right={keys.length > 0 && <span className="flex items-center gap-1"><Icon name="sync" className="size-3" />shared with the HUD</span>}>
        <Icon name="pin" className="size-3" />Pinned <span className="tnum font-normal">{keys.length}</span>
      </SectionLabel>
      {keys.length === 0 ? (
        <p className="mt-1.5 rounded-lg border border-dashed border-line px-2.5 py-1.5 text-[11px] leading-snug text-fg-3">
          Nothing pinned. Hover a stat and click <Icon name="pin" className="inline size-3 align-[-2px]" /> to keep it up here and in the in-game panel. Claude can pin stats for you too.
        </p>
      ) : (
        <ul className="mt-1.5 flex flex-wrap gap-1.5">
          {keys.map((k) => {
            const s = byKey.get(k);
            const ch = changes[k];
            const recent = ch && now - ch.at < DELTA_VISIBLE_MS;
            const flash = ch && now - ch.at < FLASH_MS ? (ch.delta > 0 ? "flash-up" : "flash-down") : "";
            const selected = selectedKey === k;
            return (
              <li key={k} className="max-w-full">
                <div
                  role="button"
                  tabIndex={0}
                  aria-pressed={selected}
                  onClick={() => onSelect(k)}
                  onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onSelect(k); } }}
                  title={`${k}${s ? "" : " · not on the character right now (0)"}`}
                  className={`group flex h-7 max-w-full cursor-pointer items-center gap-1.5 rounded-full border bg-surface pl-2.5 pr-1 text-[12px] outline-none transition-colors hover:border-line-2 focus-visible:ring-2 focus-visible:ring-ring ${selected ? "border-ring" : "border-line"}`}
                >
                  <span key={ch?.at} className={`tnum rounded px-0.5 font-semibold ${s ? "" : "text-fg-3"} ${flash}`}>{s ? fmtStat(k, s.value) : "–"}</span>
                  <span className="max-w-[11rem] truncate text-fg-2">{statLabel(s ?? { key: k })}</span>
                  {recent && <span className={`tnum text-[10px] ${ch.delta > 0 ? "text-success" : "text-danger"}`}>{signed(ch.delta)}</span>}
                  <button
                    type="button"
                    aria-label={`Unpin ${k}`}
                    title="Unpin"
                    onClick={(e) => { e.stopPropagation(); onUnpin(k); }}
                    className="grid size-5 shrink-0 place-items-center rounded-full text-fg-3 opacity-50 transition-opacity hover:bg-surface-3 hover:text-fg focus-visible:opacity-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring group-hover:opacity-100"
                  >
                    <Icon name="x" className="size-3" />
                  </button>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

// ── Feedback ────────────────────────────────────────────────────────

export function Skeleton({ kind, count }: { kind: "tiles" | "rows"; count: number }) {
  if (kind === "tiles") {
    return (
      <div className={`grid gap-1.5 ${count === 4 ? "grid-cols-4" : "grid-cols-3"}`} aria-hidden>
        {Array.from({ length: count }, (_, i) => (
          <div key={i} className="rounded-lg bg-surface-2 px-2.5 py-2">
            <div className="shimmer h-2.5 w-2/3 rounded" />
            <div className="shimmer mt-2 h-4 w-1/2 rounded" />
            <div className="shimmer mt-2.5 h-1.5 rounded" />
          </div>
        ))}
      </div>
    );
  }
  return (
    <div aria-hidden>
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="grid h-7 grid-cols-[1.75rem_minmax(0,1fr)_4rem] items-center gap-2 px-2">
          <span />
          <div className="shimmer h-2.5 rounded" style={{ width: `${85 - (i % 4) * 14}%` }} />
          <div className="shimmer ml-auto h-2.5 w-8 rounded" />
        </div>
      ))}
    </div>
  );
}

export function EmptyState({ icon, title, children, className = "" }: { icon: IconName; title: string; children?: ReactNode; className?: string }) {
  return (
    <div className={`flex flex-col items-center px-4 py-6 text-center ${className}`}>
      <span className="grid size-9 place-items-center rounded-full bg-surface-2 text-fg-3"><Icon name={icon} className="size-4" /></span>
      <p className="mt-2 text-[12.5px] font-medium">{title}</p>
      {children && <div className="mt-1 max-w-xs text-[11.5px] leading-snug text-fg-3">{children}</div>}
    </div>
  );
}

export function Banner({ tone, icon, title, children, action }: { tone: "danger" | "warning" | "info"; icon: IconName; title: ReactNode; children?: ReactNode; action?: ReactNode }) {
  const cls = tone === "danger" ? "border-danger/30 bg-danger-bg text-danger"
    : tone === "warning" ? "border-warning/30 bg-warning/10 text-warning"
    : "border-line bg-surface-2 text-fg-2";
  return (
    <div role={tone === "danger" ? "alert" : "status"} className={`flex items-start gap-2.5 rounded-lg border px-3 py-2 text-xs ${cls}`}>
      <Icon name={icon} className="mt-px size-4 shrink-0" />
      <div className="min-w-0 flex-1 leading-snug">
        <div className="font-semibold">{title}</div>
        {children && <div className="mt-0.5 text-fg-2">{children}</div>}
      </div>
      {action}
    </div>
  );
}

export function Toasts({ toasts, onDismiss }: { toasts: Toast[]; onDismiss: (id: number) => void }) {
  if (!toasts.length) return null;
  return (
    <div className="pointer-events-none fixed inset-x-3 bottom-3 z-40 flex flex-col items-end gap-1.5">
      {toasts.map((t) => (
        <div key={t.id} role="alert"
          className={`slide-up pointer-events-auto flex max-w-sm items-start gap-2 rounded-lg border px-3 py-2 text-xs shadow-lg ${t.kind === "error" ? "border-danger/40 bg-danger-bg text-danger" : "border-line bg-surface text-fg"}`}>
          <Icon name={t.kind === "error" ? "warning" : "check"} className="mt-px size-3.5 shrink-0" />
          <span className="min-w-0 flex-1 leading-snug">{t.text}</span>
          <IconButton icon="x" label="Dismiss" size="sm" onClick={() => onDismiss(t.id)} className="-my-1 -mr-1.5 text-current hover:text-current" />
        </div>
      ))}
    </div>
  );
}
