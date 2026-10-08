import type { ReactNode } from "react";
import type { Connection, Toast } from "./sync";
import type { Game, StatItem, Vitals as VitalsDto } from "./types";
import { cleanText, fmt, fmtStat, humanizeKey, type Resist } from "./format";

// ── Small primitives ────────────────────────────────────────────────

export function Card({ title, right, children, className = "" }: { title?: ReactNode; right?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`rounded-card border border-line bg-surface-2 p-3 ${className}`}>
      {(title || right) && (
        <header className="mb-2 flex items-center justify-between gap-2">
          <h2 className="text-xs font-semibold uppercase tracking-wide text-fg-2">{title}</h2>
          {right}
        </header>
      )}
      {children}
    </section>
  );
}

export function StarButton({ pinned, onClick, label }: { pinned: boolean; onClick: () => void; label: string }) {
  return (
    <button
      type="button"
      onClick={(e) => { e.stopPropagation(); onClick(); }}
      aria-pressed={pinned}
      aria-label={`${pinned ? "Unpin" : "Pin"} ${label}`}
      title={pinned ? "Unpin (shared with the HUD)" : "Pin (shared with the HUD)"}
      className={`grid size-6 shrink-0 place-items-center rounded-md transition-colors hover:bg-surface-3 focus-visible:outline-2 focus-visible:outline-ring ${pinned ? "text-warning" : "text-fg-3"}`}
    >
      <svg viewBox="0 0 20 20" className="size-4" fill={pinned ? "currentColor" : "none"} stroke="currentColor" strokeWidth="1.6" aria-hidden>
        <path d="M10 2.5l2.3 4.7 5.2.8-3.8 3.6.9 5.1L10 14.3l-4.6 2.4.9-5.1-3.8-3.6 5.2-.8z" strokeLinejoin="round" />
      </svg>
    </button>
  );
}

// ── Header status ───────────────────────────────────────────────────

const CONN_STYLE: Record<Connection, { dot: string; label: string }> = {
  connecting: { dot: "bg-fg-3 animate-pulse", label: "Connecting" },
  live: { dot: "bg-success", label: "Live" },
  stale: { dot: "bg-warning animate-pulse", label: "Reconnecting" },
  offline: { dot: "bg-danger", label: "Offline" },
};

export function StatusPill({ game, conn, latencyMs, rev, pending, title }: { game?: Game; conn: Connection; latencyMs?: number; rev?: number; pending: number; title?: string }) {
  const s = CONN_STYLE[conn];
  return (
    <div className="flex items-center gap-2 text-xs text-fg-2" title={title}>
      {game && <span className="rounded-md border border-line-2 px-1.5 py-0.5 font-semibold text-fg">{game === "poe2" ? "PoE 2" : "PoE 1"}</span>}
      <span className="flex items-center gap-1.5" role="status" aria-live="polite">
        <span className={`size-2 rounded-full ${s.dot}`} aria-hidden />
        {s.label}
        {conn === "live" && latencyMs !== undefined && <span className="tnum text-fg-3">{latencyMs} ms</span>}
      </span>
      {rev !== undefined && <span className="tnum text-fg-3" title="Shared view revision (bumps on every pin/filter/sort change, from any surface)">rev {rev}{pending > 0 ? " · saving" : ""}</span>}
    </div>
  );
}

// ── Vitals ──────────────────────────────────────────────────────────

function Meter({ label, value, max, color }: { label: string; value: number; max: number; color: string }) {
  const pct = max > 0 ? Math.min(100, (value / max) * 100) : 0;
  return (
    <div className="min-w-0">
      <div className="mb-1 flex items-baseline justify-between gap-2">
        <span className="text-xs text-fg-2">{label}</span>
        <span className="tnum text-sm font-semibold">{fmt(value)}<span className="font-normal text-fg-3"> / {fmt(max)}</span></span>
      </div>
      <div className="h-2.5 overflow-hidden rounded-full bg-surface-3" role="meter" aria-label={label} aria-valuenow={value} aria-valuemin={0} aria-valuemax={max}>
        <div className={`h-full rounded-full ${color} transition-[width] duration-500`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

export function Vitals({ vitals, game }: { vitals?: VitalsDto; game?: Game }) {
  if (!vitals) return <Card title="Vitals"><Skeleton rows={3} /></Card>;
  return (
    <Card
      title="Vitals"
      right={game === "poe2" && vitals.weaponSet !== undefined && (
        <span className="text-xs text-fg-3" title="Active weapon set (stats differ per set)">Weapon set {vitals.weaponSet + 1}</span>
      )}
    >
      <div className="grid gap-2.5">
        <Meter label="Life" value={vitals.hp} max={vitals.maxHp} color="bg-life" />
        {vitals.maxEs > 0 && <Meter label="Energy shield" value={vitals.es} max={vitals.maxEs} color="bg-es" />}
        <Meter label="Mana" value={vitals.mana} max={vitals.maxMana} color="bg-mana" />
      </div>
    </Card>
  );
}

// ── Resistances ─────────────────────────────────────────────────────

const ELEMENT_STYLE = {
  fire: { bar: "bg-fire", text: "text-fire", label: "Fire" },
  cold: { bar: "bg-cold", text: "text-cold", label: "Cold" },
  lightning: { bar: "bg-lightning", text: "text-lightning", label: "Lightning" },
  chaos: { bar: "bg-chaos", text: "text-chaos", label: "Chaos" },
} as const;

function ResistBar({ r, pinned, onPin, onSelect, selected }: { r: Resist; pinned: boolean; onPin: () => void; onSelect: () => void; selected: boolean }) {
  const st = ELEMENT_STYLE[r.element];
  const v = r.value ?? 0;
  const over = Math.max(0, (r.uncapped ?? v) - r.cap);
  const capped = v >= r.cap;
  const fill = Math.max(0, Math.min(v, r.cap));
  const negative = v < 0;
  return (
    <div
      className={`group -mx-1.5 cursor-pointer rounded-md px-1.5 py-1 hover:bg-surface-3/60 ${selected ? "bg-surface-3 ring-1 ring-ring" : ""}`}
      onClick={onSelect}
      title={`${r.key}${r.capAssumed ? " · cap assumed (no max_* stat present)" : ""}`}
    >
      <div className="mb-1 flex items-center gap-2">
        <span className={`w-16 text-xs font-medium ${st.text}`}>{st.label}</span>
        <span className={`tnum text-sm font-semibold ${negative ? "text-danger" : r.value === undefined ? "text-fg-3" : ""}`}
          title={r.value === undefined ? "No resistance stat on the character (0%)" : undefined}>{v}%</span>
        <span className="tnum text-xs text-fg-3">/ {r.cap}%{r.capAssumed ? "*" : ""}</span>
        {capped && <span className="text-[11px] font-medium text-success">capped</span>}
        {over > 0 && <span className="tnum text-[11px] text-fg-3">+{over} over</span>}
        {!capped && <span className="tnum text-[11px] text-warning">{r.cap - v} to cap</span>}
        <span className="ml-auto opacity-60 group-hover:opacity-100"><StarButton pinned={pinned} onClick={onPin} label={r.key} /></span>
      </div>
      <div className="relative h-2 rounded-full bg-surface-3" aria-hidden>
        {negative ? (
          <div className="absolute inset-y-0 left-0 rounded-full bg-danger" style={{ width: `${Math.min(100, -v)}%` }} />
        ) : (
          <>
            <div className={`absolute inset-y-0 left-0 rounded-full ${st.bar} transition-[width] duration-500`} style={{ width: `${fill}%` }} />
            {over > 0 && <div className={`ghost-hatch absolute inset-y-0 rounded-r-full ${st.text}`} style={{ left: `${r.cap}%`, width: `${Math.min(over, 100 - r.cap)}%` }} />}
          </>
        )}
        <div className="absolute -inset-y-0.5 w-0.5 rounded bg-fg-2" style={{ left: `calc(${r.cap}% - 1px)` }} />
      </div>
    </div>
  );
}

export function Resistances({ list, pinnedKeys, selectedKey, onPin, onSelect, loaded }: {
  list: Resist[]; pinnedKeys: Set<string>; selectedKey?: string | null; loaded: boolean;
  onPin: (key: string, pinned: boolean) => void; onSelect: (key: string) => void;
}) {
  const assumed = list.some((r) => r.capAssumed && r.value !== undefined);
  return (
    <Card title="Resistances" right={assumed && <span className="text-[11px] text-fg-3" title="No maximum_*_resistance stat on this character; showing the default 75% cap">* default cap</span>}>
      {!loaded && list.every((r) => r.value === undefined) ? <Skeleton rows={4} /> : (
        <div className="grid gap-1">
          {list.map((r) => (
            <ResistBar key={r.element} r={r} pinned={pinnedKeys.has(r.key)} selected={selectedKey === r.key}
              onPin={() => onPin(r.key, !pinnedKeys.has(r.key))} onSelect={() => onSelect(r.key)} />
          ))}
        </div>
      )}
    </Card>
  );
}

// ── Pinned ──────────────────────────────────────────────────────────

export function Pinned({ keys, stats, selectedKey, onUnpin, onSelect }: {
  keys: string[]; stats: Map<string, StatItem>; selectedKey?: string | null;
  onUnpin: (key: string) => void; onSelect: (key: string) => void;
}) {
  return (
    <Card title={`Pinned · ${keys.length}`}>
      {keys.length === 0 ? (
        <p className="text-xs text-fg-3">Star a stat to pin it here and in the in-game panel. Claude can pin stats too.</p>
      ) : (
        <ul className="grid grid-cols-[repeat(auto-fill,minmax(10.5rem,1fr))] gap-1.5">
          {keys.map((k) => {
            const s = stats.get(k);
            const text = cleanText(s?.text);
            return (
              <li key={k}>
                <div
                  role="button"
                  tabIndex={0}
                  onClick={() => onSelect(k)}
                  onKeyDown={(e) => (e.key === "Enter" || e.key === " ") && onSelect(k)}
                  className={`group flex h-full items-start gap-1 rounded-md border bg-surface px-2 py-1.5 text-left outline-none hover:border-line-2 focus-visible:ring-2 focus-visible:ring-ring ${selectedKey === k ? "border-ring" : "border-line"}`}
                  title={k}
                >
                  <div className="min-w-0 flex-1">
                    <div className={`tnum text-base leading-tight font-semibold ${s ? "" : "text-fg-3"}`}
                      title={s ? undefined : "Not on the character right now (counts as 0)"}>
                      {s ? fmtStat(k, s.value) : "0"}
                    </div>
                    <div className="line-clamp-2 text-[11px] leading-snug text-fg-2">{text ?? humanizeKey(k)}</div>
                  </div>
                  <button type="button" aria-label={`Unpin ${k}`} title="Unpin"
                    onClick={(e) => { e.stopPropagation(); onUnpin(k); }}
                    className="grid size-5 shrink-0 place-items-center rounded text-fg-3 opacity-0 group-hover:opacity-100 hover:bg-surface-3 hover:text-fg focus-visible:opacity-100">
                    ×
                  </button>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </Card>
  );
}

// ── Feedback ────────────────────────────────────────────────────────

export function Skeleton({ rows }: { rows: number }) {
  return (
    <div className="grid gap-2" aria-hidden>
      {Array.from({ length: rows }, (_, i) => <div key={i} className="h-4 animate-pulse rounded bg-surface-3" style={{ width: `${90 - i * 12}%` }} />)}
    </div>
  );
}

export function Toasts({ toasts, onDismiss }: { toasts: Toast[]; onDismiss: (id: number) => void }) {
  if (!toasts.length) return null;
  return (
    <div className="pointer-events-none fixed inset-x-3 bottom-3 z-20 flex flex-col items-end gap-1.5" role="alert">
      {toasts.map((t) => (
        <button key={t.id} type="button" onClick={() => onDismiss(t.id)}
          className={`pointer-events-auto max-w-sm rounded-md border px-3 py-2 text-left text-xs shadow-lg ${t.kind === "error" ? "border-danger/40 bg-danger-bg text-danger" : "border-line bg-surface-2 text-fg"}`}>
          {t.text}
        </button>
      ))}
    </div>
  );
}

export function Banner({ tone, children, action }: { tone: "danger" | "warning" | "info"; children: ReactNode; action?: ReactNode }) {
  const cls = tone === "danger" ? "border-danger/40 bg-danger-bg text-danger" : tone === "warning" ? "border-warning/40 text-warning" : "border-line text-fg-2";
  return (
    <div className={`flex items-center gap-3 rounded-card border px-3 py-2 text-xs ${cls}`}>
      <div className="min-w-0 flex-1">{children}</div>
      {action}
    </div>
  );
}
