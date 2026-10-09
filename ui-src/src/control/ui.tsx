// Control-center primitives on top of the shared components: buttons, switches, sliders, selects, cards, badges, the
// catalog's data-URI icons per theme, a JSON view and the "Show me" entry point. Semantic tokens only.

import { useEffect, useId, useMemo, useRef, useState, type ReactNode } from "react";
import { Icon, type IconName } from "../icons";
import type { CatalogIcon } from "./types";

export function Button({ children, onClick, icon, tone = "default", size = "md", disabled, title, type = "button", className = "", tour, active, busy, ariaLabel, autoFocus }: {
  children?: ReactNode; onClick?: () => void; icon?: IconName; tone?: "default" | "primary" | "danger" | "ghost" | "accent"; size?: "sm" | "md";
  disabled?: boolean; title?: string; type?: "button" | "submit"; className?: string; tour?: string; active?: boolean; busy?: boolean; ariaLabel?: string; autoFocus?: boolean;
}) {
  const tones = {
    default: "border-line bg-surface text-fg-2 hover:border-line-2 hover:text-fg",
    primary: "border-ring/40 bg-ring/10 text-fg hover:bg-ring/20",
    accent: "border-ring/40 bg-ring/10 text-fg hover:bg-ring/20",
    danger: "border-danger/40 bg-danger/10 text-danger hover:bg-danger/20",
    ghost: "border-transparent text-fg-2 hover:bg-surface-3 hover:text-fg",
  };
  return (
    <button type={type} onClick={onClick} disabled={disabled || busy} title={title} data-tour={tour} aria-pressed={active} aria-label={ariaLabel} autoFocus={autoFocus}
      className={`inline-flex shrink-0 items-center justify-center gap-1.5 rounded-md border font-medium transition-[background-color,border-color,color,opacity,transform] active:scale-[0.98] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 disabled:active:scale-100 ${size === "sm" ? "h-6 px-1.5 text-[11px]" : "h-7 px-2.5 text-[12px]"} ${active ? "border-fg bg-fg text-surface" : tones[tone]} ${className}`}>
      {busy ? <Icon name="sync" className="spin size-3.5" /> : icon && <Icon name={icon} className={size === "sm" ? "size-3" : "size-3.5"} />}
      {children}
    </button>
  );
}

export function Switch({ checked, onChange, label, disabled, size = "md", tour, tone = "success" }: {
  checked: boolean; onChange: (v: boolean) => void; label: string; disabled?: boolean; size?: "sm" | "md"; tour?: string; tone?: "success" | "accent";
}) {
  const w = size === "sm" ? "h-4 w-7" : "h-5 w-9";
  const k = size === "sm" ? "size-3" : "size-4";
  const on = tone === "accent" ? "bg-fg" : "bg-fg";
  return (
    <button type="button" role="switch" aria-checked={checked} aria-label={label} title={label} disabled={disabled} data-tour={tour}
      onClick={() => onChange(!checked)}
      className={`relative inline-flex shrink-0 items-center rounded-full border border-transparent p-0.5 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 ${w} ${checked ? on : "bg-line-2"}`}>
      <span className={`knob block rounded-full bg-surface shadow-sm ${k}`} style={{ transform: checked ? `translateX(${size === "sm" ? 12 : 16}px)` : "translateX(0)" }} />
    </button>
  );
}

export function Slider({ value, min, max, step, onChange, onCommit, label, disabled, tour }: {
  value: number; min: number; max: number; step?: number; onChange: (v: number) => void; onCommit?: (v: number) => void; label: string; disabled?: boolean; tour?: string;
}) {
  const pct = max > min ? ((value - min) / (max - min)) * 100 : 0;
  return (
    <div className="flex min-w-0 items-center gap-2" data-tour={tour}>
      <input type="range" min={min} max={max} step={step ?? (Number.isInteger(min) && Number.isInteger(max) && max - min > 1 ? 1 : (max - min) / 100)} value={value} disabled={disabled}
        aria-label={label} onChange={(e) => onChange(Number(e.target.value))} onPointerUp={(e) => onCommit?.(Number((e.target as HTMLInputElement).value))} onKeyUp={(e) => onCommit?.(Number((e.target as HTMLInputElement).value))}
        className="slider h-1.5 min-w-0 flex-1 cursor-pointer appearance-none rounded-full bg-surface-3 accent-[var(--color-ring)] disabled:cursor-default disabled:opacity-50"
        style={{ background: `linear-gradient(90deg, var(--color-ring) ${pct}%, var(--color-surface-3) ${pct}%)` }} />
      <NumberBox value={value} min={min} max={max} onCommit={(v) => { onChange(v); onCommit?.(v); }} label={label} disabled={disabled} />
    </div>
  );
}

export function NumberBox({ value, min, max, onCommit, label, disabled }: { value: number; min?: number; max?: number; onCommit: (v: number) => void; label: string; disabled?: boolean }) {
  const [text, setText] = useState(String(value));
  useEffect(() => setText(String(value)), [value]);
  const commit = () => {
    let n = Number(text);
    if (!Number.isFinite(n)) { setText(String(value)); return; }
    if (min !== undefined) n = Math.max(min, n);
    if (max !== undefined) n = Math.min(max, n);
    setText(String(n));
    if (n !== value) onCommit(n);
  };
  return (
    <input type="text" inputMode="decimal" value={text} disabled={disabled} aria-label={`${label} value`} onChange={(e) => setText(e.target.value)} onBlur={commit}
      onKeyDown={(e) => { if (e.key === "Enter") commit(); if (e.key === "Escape") setText(String(value)); }}
      className="tnum h-7 w-16 shrink-0 rounded-md border border-line bg-surface px-1.5 text-right text-[12px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50" />
  );
}

export function TextInput({ value, onChange, onCommit, placeholder, label, disabled, mono, invalid, tour, className = "", autoFocus, list }: {
  value: string; onChange: (v: string) => void; onCommit?: (v: string) => void; placeholder?: string; label: string; disabled?: boolean; mono?: boolean; invalid?: boolean; tour?: string; className?: string; autoFocus?: boolean; list?: string;
}) {
  return (
    <input type="text" value={value} placeholder={placeholder} aria-label={label} disabled={disabled} aria-invalid={invalid || undefined} data-tour={tour} autoFocus={autoFocus} list={list}
      onChange={(e) => onChange(e.target.value)} onBlur={() => onCommit?.(value)} onKeyDown={(e) => { if (e.key === "Enter") onCommit?.(value); }}
      className={`h-7 w-full min-w-0 rounded-md border bg-surface px-2 text-[12.5px] transition-colors placeholder:text-fg-3 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 ${mono ? "font-code text-[12px]" : ""} ${invalid ? "border-danger" : "border-line hover:border-line-2"} ${className}`} />
  );
}

export function Select({ value, options, onChange, label, disabled, tour, className = "" }: { value: string; options: { value: string; label?: string }[]; onChange: (v: string) => void; label: string; disabled?: boolean; tour?: string; className?: string }) {
  return (
    <div className={`relative ${className}`} data-tour={tour}>
      <select value={value} aria-label={label} disabled={disabled} onChange={(e) => onChange(e.target.value)}
        className="h-7 w-full appearance-none rounded-md border border-line bg-surface py-0 pl-2 pr-7 text-[12.5px] hover:border-line-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50">
        {options.map((o) => <option key={o.value} value={o.value}>{o.label ?? o.value}</option>)}
      </select>
      <Icon name="chevron" className="pointer-events-none absolute right-2 top-1/2 size-3.5 -translate-y-1/2 text-fg-3" />
    </div>
  );
}

/** A row of exclusive options (small). */
export function Segmented<T extends string>({ value, options, onChange, label, tour, size = "md" }: { value: T; options: { value: T; label: ReactNode; title?: string }[]; onChange: (v: T) => void; label: string; tour?: string; size?: "sm" | "md" }) {
  return (
    <div role="radiogroup" aria-label={label} data-tour={tour} className={`inline-flex shrink-0 rounded-md border border-line bg-surface-2 p-0.5 ${size === "sm" ? "h-6" : "h-7"}`}>
      {options.map((o) => (
        <button key={o.value} type="button" role="radio" aria-checked={o.value === value} title={o.title} onClick={() => onChange(o.value)}
          className={`inline-flex items-center gap-1 rounded-[5px] px-2 font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${size === "sm" ? "text-[11px]" : "text-[12px]"} ${o.value === value ? "bg-fg text-surface" : "text-fg-2 hover:text-fg"}`}>
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function Card({ title, icon, right, children, className = "", tour, pad = true, dim, id }: { title?: ReactNode; icon?: IconName; right?: ReactNode; children: ReactNode; className?: string; tour?: string; pad?: boolean; dim?: boolean; id?: string }) {
  return (
    <section id={id} data-tour={tour} className={`min-w-0 rounded-card border border-line bg-surface transition-opacity ${dim ? "opacity-60" : ""} ${className}`}>
      {title !== undefined && (
        <header className="flex min-h-9 flex-wrap items-center gap-x-2 gap-y-1 border-b border-line px-3 py-1">
          {icon && <Icon name={icon} className="size-3.5 shrink-0 text-fg-3" />}
          <h2 className="min-w-[6rem] flex-1 truncate text-[12px] font-semibold">{title}</h2>
          {right && <div className="flex min-w-0 flex-wrap items-center gap-1.5">{right}</div>}
        </header>
      )}
      <div className={pad ? "p-3" : ""}>{children}</div>
    </section>
  );
}

export type BadgeTone = "neutral" | "info" | "success" | "warning" | "danger" | "accent" | "violet";
export function Badge({ children, tone = "neutral", icon, title, className = "" }: { children: ReactNode; tone?: BadgeTone; icon?: IconName; title?: string; className?: string }) {
  const t: Record<BadgeTone, string> = {
    neutral: "border-line bg-surface-2 text-fg-2",
    info: "border-info/30 bg-info/10 text-info",
    success: "border-success/30 bg-success/10 text-success",
    warning: "border-warning/30 bg-warning/10 text-warning",
    danger: "border-danger/30 bg-danger/10 text-danger",
    accent: "border-ring/40 bg-ring/10 text-fg",
    violet: "border-m-cand/30 bg-m-cand/10 text-m-cand",
  };
  return (
    <span title={title} className={`inline-flex h-[18px] shrink-0 items-center gap-1 rounded border px-1.5 text-[10px] font-medium leading-none ${t[tone]} ${className}`}>
      {icon && <Icon name={icon} className="size-2.5" />}{children}
    </span>
  );
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="rounded border border-line bg-surface-2 px-1 font-code text-[10px] text-fg-2">{children}</kbd>;
}

/** A catalog icon (data URI SVG per theme). Falls back to an inline icon when the tool has none. */
export function CatalogIconImg({ icons, theme, size = 16, fallback = "box", className = "" }: { icons?: CatalogIcon[] | null; theme: "light" | "dark"; size?: number; fallback?: IconName; className?: string }) {
  const src = useMemo(() => {
    if (!icons?.length) return undefined;
    return (icons.find((i) => i.theme === theme) ?? icons.find((i) => !i.theme) ?? icons[0]).src;
  }, [icons, theme]);
  if (!src) return <Icon name={fallback} className={className} style={{ width: size, height: size }} />;
  return <img src={src} width={size} height={size} alt="" aria-hidden className={`shrink-0 ${className}`} draggable={false} />;
}

export function Stat({ label, value, unit, sub, tone, trend, tour, flash }: { label: string; value: ReactNode; unit?: string; sub?: ReactNode; tone?: "good" | "warn" | "bad" | "accent"; trend?: number[]; tour?: string; flash?: number }) {
  const color = tone === "good" ? "text-success" : tone === "warn" ? "text-warning" : tone === "bad" ? "text-danger" : tone === "accent" ? "text-p-gc" : "";
  return (
    <div data-tour={tour} className="relative min-w-0 overflow-hidden rounded-lg bg-surface-2 px-2.5 py-2">
      {trend && trend.length > 2 && <MiniTrend values={trend} className="pointer-events-none absolute right-2 top-5 h-5 w-[40%] text-fg-3" />}
      <div className="truncate text-[10.5px] font-medium uppercase tracking-wide text-fg-3">{label}</div>
      <div key={flash} className={`relative mt-0.5 flex items-baseline gap-1 ${flash ? "flash-accent rounded px-0.5 -mx-0.5" : ""}`}>
        <span className={`tnum text-[19px] font-semibold leading-none ${color}`}>{value}</span>
        {unit && <span className="text-[11px] text-fg-3">{unit}</span>}
      </div>
      {sub && <div className="mt-1 truncate text-[10.5px] text-fg-3">{sub}</div>}
    </div>
  );
}

export function MiniTrend({ values, className = "" }: { values: number[]; className?: string }) {
  const w = 48, h = 16, n = values.length;
  const lo = Math.min(...values), hi = Math.max(...values);
  const span = hi - lo || 1;
  const pts = values.map((v, i) => `${((i / (n - 1)) * w).toFixed(1)},${(h - 1 - ((v - lo) / span) * (h - 2)).toFixed(1)}`).join(" ");
  return (
    <svg viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" className={className} aria-hidden>
      <polyline points={pts} fill="none" stroke="currentColor" strokeWidth="1.25" opacity="0.6" vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
    </svg>
  );
}

/** "Show me": the entry point of a tour, next to a complex control or in an empty state. */
export function ShowMe({ onClick, done, children = "Show me", size = "sm", className = "" }: { onClick: () => void; done?: boolean; children?: ReactNode; size?: "sm" | "md"; className?: string }) {
  return (
    <button type="button" onClick={onClick} title={done ? "Seen before. Show it again" : "A short guided tour of this"}
      className={`inline-flex shrink-0 items-center gap-1 rounded-full border border-dashed border-ring/50 bg-ring/5 font-medium text-fg transition-colors hover:border-ring hover:bg-ring/15 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${size === "sm" ? "h-5 px-1.5 text-[10.5px]" : "h-7 px-2.5 text-[11.5px]"} ${className}`}>
      <Icon name={done ? "check" : "sparkle"} className={size === "sm" ? "size-2.5" : "size-3"} />{children}
    </button>
  );
}

export function Copy({ text, label = "Copy", size = "sm" }: { text: string; label?: string; size?: "sm" | "md" }) {
  const [ok, setOk] = useState(false);
  return (
    <button type="button" aria-label={label} title={label} onClick={() => { void navigator.clipboard?.writeText(text).then(() => { setOk(true); setTimeout(() => setOk(false), 1200); }); }}
      className={`grid shrink-0 place-items-center rounded-md text-fg-3 transition-colors hover:bg-surface-3 hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${size === "sm" ? "size-6" : "size-7"}`}>
      <Icon name={ok ? "check" : "copy"} className={`${size === "sm" ? "size-3" : "size-3.5"} ${ok ? "text-success" : ""}`} />
    </button>
  );
}

/** Time ago, short. */
export function agoShort(ms: number): string {
  if (ms < 1500) return "just now";
  if (ms < 60_000) return `${Math.round(ms / 1000)} s ago`;
  if (ms < 3_600_000) return `${Math.round(ms / 60_000)} min ago`;
  return `${Math.round(ms / 3_600_000)} h ago`;
}

export function fmtNum(n: number | undefined | null, digits = 1): string {
  if (n === undefined || n === null || !Number.isFinite(n)) return "–";
  return n.toLocaleString(undefined, { maximumFractionDigits: digits, minimumFractionDigits: 0 });
}

// ── JSON view ─────────────────────────────────────────────────────

function kindClass(v: unknown): string {
  if (typeof v === "number") return "text-k-num";
  if (typeof v === "string") return "text-k-str";
  if (typeof v === "boolean") return "text-k-bool";
  return "text-fg-3";
}

function scalarText(v: unknown): string {
  if (v === null) return "null";
  if (v === undefined) return "undefined";
  if (typeof v === "string") return v;
  return String(v);
}

/** A collapsible tree of any JSON value. Objects open 2 levels by default; arrays of objects render as tables. */
export function JsonView({ value, depth = 0, descriptions, open = 2, name }: { value: unknown; depth?: number; descriptions?: Record<string, string>; open?: number; name?: string }) {
  if (value === null || typeof value !== "object") {
    return <span className={`font-code text-[11.5px] break-words ${kindClass(value)}`}>{typeof value === "string" ? `"${value}"` : scalarText(value)}</span>;
  }
  if (Array.isArray(value)) {
    if (!value.length) return <span className="font-code text-[11.5px] text-fg-3">[]</span>;
    if (value.length <= 12 && value.every((x) => x === null || typeof x !== "object")) {
      return <span className="font-code text-[11.5px] break-words">[{value.map((x, i) => <span key={i}><span className={kindClass(x)}>{typeof x === "string" ? `"${x}"` : scalarText(x)}</span>{i < value.length - 1 ? ", " : ""}</span>)}]</span>;
    }
    if (value.every((x) => x && typeof x === "object" && !Array.isArray(x))) return <JsonTable rows={value as Record<string, unknown>[]} name={name} />;
    return (
      <Collapsible label={`${value.length} items`} open={depth < open}>
        {value.slice(0, 200).map((x, i) => (
          <div key={i} className="grid grid-cols-[2.5rem_1fr] items-start gap-1.5 py-px">
            <span className="tnum text-right text-[11px] text-fg-3">{i}</span>
            <JsonView value={x} depth={depth + 1} open={open} />
          </div>
        ))}
        {value.length > 200 && <div className="text-[11px] text-fg-3">… {value.length - 200} more</div>}
      </Collapsible>
    );
  }
  const entries = Object.entries(value as Record<string, unknown>);
  if (!entries.length) return <span className="font-code text-[11.5px] text-fg-3">{"{}"}</span>;
  const body = (
    <div className="grid grid-cols-[minmax(5rem,max-content)_1fr] items-start gap-x-3 gap-y-0.5">
      {entries.map(([k, v]) => (
        <div key={k} className="contents">
          <span className="truncate pt-px text-[11.5px] text-fg-2" title={descriptions?.[k] ?? k}>{k}{descriptions?.[k] && <span className="text-fg-3">*</span>}</span>
          <div className="min-w-0"><JsonView value={v} depth={depth + 1} open={open} name={k} /></div>
        </div>
      ))}
    </div>
  );
  return depth === 0 ? body : <Collapsible label={`{${entries.length}}`} open={depth < open} inline>{body}</Collapsible>;
}

function Collapsible({ label, open: initial, children, inline }: { label: string; open: boolean; children: ReactNode; inline?: boolean }) {
  const [open, setOpen] = useState(initial);
  return (
    <div className={inline ? "" : ""}>
      <button type="button" onClick={() => setOpen((o) => !o)} aria-expanded={open} className="inline-flex items-center gap-0.5 rounded font-code text-[11px] text-fg-3 hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
        <Icon name="chevronRight" className={`size-3 transition-transform ${open ? "rotate-90" : ""}`} />{label}
      </button>
      {open && <div className="ml-1.5 mt-0.5 border-l border-line pl-2.5">{children}</div>}
    </div>
  );
}

export function JsonTable({ rows: raw, name }: { rows: Record<string, unknown>[]; name?: string }) {
  // One level of nested objects becomes dotted columns (spec.id, spec.path), so a typed row reads as a row.
  const rows = useMemo(() => raw.map((r) => {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(r)) {
      if (v && typeof v === "object" && !Array.isArray(v) && Object.keys(v).length <= 8 && Object.values(v).every((x) => x === null || typeof x !== "object")) {
        for (const [k2, v2] of Object.entries(v as Record<string, unknown>)) out[`${k}.${k2}`] = v2;
      } else out[k] = v;
    }
    return out;
  }), [raw]);
  const cols = useMemo(() => {
    const seen: string[] = [];
    for (const r of rows.slice(0, 50)) for (const k of Object.keys(r)) if (!seen.includes(k)) seen.push(k);
    return seen.slice(0, 16);
  }, [rows]);
  const [all, setAll] = useState(false);
  const shown = all ? rows : rows.slice(0, 25);
  return (
    <div className="min-w-0 overflow-x-auto rounded-md border border-line scroll-thin">
      <table className="w-full border-collapse text-[11px]">
        <thead><tr className="bg-surface-2 text-left text-[10.5px] uppercase tracking-wide text-fg-3">{cols.map((c) => <th key={c} className="whitespace-nowrap px-2 py-1 font-semibold">{c}</th>)}</tr></thead>
        <tbody>
          {shown.map((r, i) => (
            <tr key={i} className="border-t border-line align-top hover:bg-surface-2/60">
              {cols.map((c) => (
                <td key={c} className="max-w-[18rem] px-2 py-1">
                  {r[c] !== null && typeof r[c] === "object" ? <JsonView value={r[c]} depth={2} open={1} /> : <span className={`font-code break-words ${kindClass(r[c])}`}>{r[c] === undefined ? "" : scalarText(r[c])}</span>}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length > 25 && (
        <button type="button" onClick={() => setAll((a) => !a)} className="w-full border-t border-line py-1 text-[11px] text-fg-2 hover:bg-surface-2">{all ? "Show fewer" : `Show all ${rows.length}${name ? ` ${name}` : ""}`}</button>
      )}
    </div>
  );
}

/** Confirm dialog (modal), for destructive calls and permission changes. */
export function Confirm({ title, children, confirmLabel, tone = "primary", onConfirm, onCancel, busy, tour }: { title: string; children: ReactNode; confirmLabel: string; tone?: "primary" | "danger" | "accent"; onConfirm: () => void; onCancel: () => void; busy?: boolean; tour?: string }) {
  const id = useId();
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onCancel(); };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onCancel]);
  return (
    <div className="fade-in fixed inset-0 z-[60] grid place-items-center bg-fg/40 p-4" onMouseDown={(e) => { if (e.target === e.currentTarget) onCancel(); }}>
      <div ref={ref} role="dialog" aria-modal aria-labelledby={id} data-tour={tour} className="tour-card w-full max-w-sm rounded-card border border-line bg-surface p-4 shadow-2xl">
        <h2 id={id} className="text-[13.5px] font-semibold">{title}</h2>
        <div className="mt-2 text-[12px] leading-relaxed text-fg-2">{children}</div>
        <div className="mt-4 flex justify-end gap-2">
          <Button onClick={onCancel} tone="ghost" autoFocus>Cancel</Button>
          <Button onClick={onConfirm} tone={tone} busy={busy}>{confirmLabel}</Button>
        </div>
      </div>
    </div>
  );
}

export function Spinner({ className = "size-3.5" }: { className?: string }) {
  return <Icon name="sync" className={`spin ${className}`} />;
}
