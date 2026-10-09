// "Show me" tours: a data-driven, reusable tour engine. A tour is a list of steps; each step targets an element by its
// stable data-tour id, says one thing, and may run an action first (go to a page, type a search). The overlay dims
// everything but the target (a spotlight that morphs between targets), shows a card with the message, progress and
// next / back / skip, and an animated pointer where it helps. Keyboard: Right / Enter next, Left back, Escape skip.
// Completed tours are remembered (localStorage) so entry points can show a check.

import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Icon } from "../../icons";
import { Button } from "../ui";

export interface TourStep {
  /** data-tour id of the target; none = a centred card (an intro or outro). */
  target?: string;
  title: string;
  body: ReactNode;
  /** Runs before the step shows (navigate, open, type). May be async. */
  before?: (ctx: TourContextApi) => void | Promise<void>;
  /** Where the card goes relative to the target (auto picks below / above). */
  placement?: "auto" | "below" | "above";
  /** Show the bobbing pointer (default true when there is a target). */
  pointer?: boolean;
  /** Let the person use the target while this step shows (clicks pass through the overlay inside the spotlight). */
  interactive?: boolean;
  /** Spotlight padding in px (default 6). */
  pad?: number;
}

export interface Tour {
  id: string;
  title: string;
  /** One line for the "Show me" menu. */
  summary: string;
  /** Standalone only (needs a page the MCP App host doesn't show). */
  standaloneOnly?: boolean;
  steps: TourStep[];
}

/** What steps can do: the app injects it (navigate, set a search, open a tool...). */
export interface TourContextApi {
  go: (page: string, id?: string, sub?: string) => void;
  setToolQuery: (q: string) => void;
  mode: "mcp-app" | "standalone";
  wait: (ms: number) => Promise<void>;
  /** Click an element by data-tour id, if it exists. */
  click: (target: string) => void;
}

interface TourState { tour: Tour; index: number; busy: boolean }

interface TourContextValue {
  start: (id: string) => void;
  stop: () => void;
  active?: TourState;
  tours: Tour[];
  done: ReadonlySet<string>;
}

const Ctx = createContext<TourContextValue>({ start: () => {}, stop: () => {}, tours: [], done: new Set() });
export const useTours = () => useContext(Ctx);

const DONE_KEY = "hexile.cc.tours";
function loadDone(): Set<string> {
  try { return new Set(JSON.parse(localStorage.getItem(DONE_KEY) ?? "[]") as string[]); } catch { return new Set(); }
}
function saveDone(s: Set<string>) {
  try { localStorage.setItem(DONE_KEY, JSON.stringify([...s])); } catch { /* storage blocked */ }
}

export function findTarget(id: string): HTMLElement | null {
  return document.querySelector<HTMLElement>(`[data-tour="${CSS.escape(id)}"]`);
}

/** Wait until the target exists (after a navigation), up to ~2 s. */
async function waitForTarget(id: string): Promise<HTMLElement | null> {
  for (let i = 0; i < 40; i++) {
    const el = findTarget(id);
    if (el) return el;
    await new Promise((r) => setTimeout(r, 50));
  }
  return null;
}

export function TourProvider({ tours, api, children }: { tours: Tour[]; api: TourContextApi; children: ReactNode }) {
  const [active, setActive] = useState<TourState>();
  const [done, setDone] = useState<Set<string>>(loadDone);
  const apiRef = useRef(api);
  apiRef.current = api;

  const runStep = useCallback(async (tour: Tour, index: number) => {
    setActive({ tour, index, busy: true });
    const step = tour.steps[index];
    try { await step.before?.(apiRef.current); } catch (e) { console.warn("[tour] step action failed", e); }
    if (step.target) {
      const el = await waitForTarget(step.target);
      el?.scrollIntoView({ block: "center", inline: "nearest", behavior: "auto" });
      await new Promise((r) => requestAnimationFrame(() => r(undefined)));
    }
    setActive((a) => (a && a.tour.id === tour.id ? { tour, index, busy: false } : a));
  }, []);

  const start = useCallback((id: string) => {
    const tour = tours.find((t) => t.id === id);
    if (!tour) return;
    void runStep(tour, 0);
  }, [tours, runStep]);

  const finish = useCallback((completed: boolean) => {
    setActive((a) => {
      if (a && completed) setDone((d) => { const n = new Set(d); n.add(a.tour.id); saveDone(n); return n; });
      return undefined;
    });
  }, []);

  const next = useCallback(() => {
    setActive((a) => {
      if (!a || a.busy) return a;
      if (a.index + 1 >= a.tour.steps.length) { setTimeout(() => finish(true), 0); return a; }
      void runStep(a.tour, a.index + 1);
      return { ...a, busy: true };
    });
  }, [runStep, finish]);

  const back = useCallback(() => {
    setActive((a) => {
      if (!a || a.busy || a.index === 0) return a;
      void runStep(a.tour, a.index - 1);
      return { ...a, busy: true };
    });
  }, [runStep]);

  const value = useMemo<TourContextValue>(() => ({ start, stop: () => finish(false), active, tours, done }), [start, finish, active, tours, done]);
  return (
    <Ctx.Provider value={value}>
      {children}
      {active && <TourOverlay state={active} onNext={next} onBack={back} onSkip={() => finish(false)} />}
    </Ctx.Provider>
  );
}

interface Rect { top: number; left: number; width: number; height: number }

function TourOverlay({ state, onNext, onBack, onSkip }: { state: TourState; onNext: () => void; onBack: () => void; onSkip: () => void }) {
  const step = state.tour.steps[state.index];
  const [rect, setRect] = useState<Rect | null>(null);
  const [missing, setMissing] = useState(false);
  const cardRef = useRef<HTMLDivElement>(null);
  const last = state.index === state.tour.steps.length - 1;

  // Measure the target now and whenever the page moves.
  useLayoutEffect(() => {
    if (state.busy) return;
    let raf = 0;
    const measure = () => {
      if (!step.target) { setRect(null); setMissing(false); return; }
      const el = findTarget(step.target);
      if (!el) { setRect(null); setMissing(true); return; }
      const r = el.getBoundingClientRect();
      const pad = step.pad ?? 6;
      setMissing(false);
      setRect({ top: r.top - pad, left: r.left - pad, width: r.width + pad * 2, height: r.height + pad * 2 });
    };
    measure();
    const onMove = () => { cancelAnimationFrame(raf); raf = requestAnimationFrame(measure); };
    window.addEventListener("resize", onMove);
    window.addEventListener("scroll", onMove, true);
    const ro = typeof ResizeObserver !== "undefined" ? new ResizeObserver(onMove) : undefined;
    ro?.observe(document.body);
    const t = setInterval(measure, 400);
    return () => { window.removeEventListener("resize", onMove); window.removeEventListener("scroll", onMove, true); ro?.disconnect(); clearInterval(t); cancelAnimationFrame(raf); };
  }, [step, state.busy, state.index]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") { e.preventDefault(); onSkip(); }
      else if (e.key === "ArrowRight" || e.key === "Enter") { if ((e.target as HTMLElement)?.tagName !== "INPUT" && (e.target as HTMLElement)?.tagName !== "TEXTAREA") { e.preventDefault(); onNext(); } }
      else if (e.key === "ArrowLeft") { e.preventDefault(); onBack(); }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onNext, onBack, onSkip]);


  // Card placement: below the target when there is room, else above; a bottom sheet on narrow viewports.
  const vw = window.innerWidth, vh = window.innerHeight;
  const narrow = vw < 560;
  const cardW = Math.min(340, vw - 24);
  let cardStyle: React.CSSProperties;
  let placement: "below" | "above" | "centre" | "sheet" = "centre";
  if (narrow) { placement = "sheet"; cardStyle = { left: 12, right: 12, bottom: 12, maxHeight: "45vh" }; }
  else if (rect) {
    const spaceBelow = vh - (rect.top + rect.height);
    const wantBelow = step.placement === "below" || (step.placement !== "above" && spaceBelow > 190) || rect.top < 190;
    const left = Math.max(12, Math.min(vw - cardW - 12, rect.left + rect.width / 2 - cardW / 2));
    if (wantBelow) { placement = "below"; cardStyle = { top: Math.min(vh - 160, rect.top + rect.height + 14), left, width: cardW }; }
    else { placement = "above"; cardStyle = { bottom: Math.max(12, vh - rect.top + 14), left, width: cardW }; }
  } else cardStyle = { top: "50%", left: "50%", transform: "translate(-50%, -50%)", width: cardW };

  const showPointer = !!rect && step.pointer !== false && !narrow;
  const pointerAbove = placement === "below";
  const blocking = !step.interactive;

  return createPortal(
    <div className="fixed inset-0 z-[70]" role="dialog" aria-modal aria-label={`Tour: ${state.tour.title}`} style={{ pointerEvents: blocking ? "auto" : "none" }}>
      {/* The dimming: a hole over the target. Clicks outside end nothing (the card is the control). */}
      {rect ? (
        <div className="tour-spot tour-ring absolute rounded-lg outline outline-2 outline-ring" aria-hidden
          style={{ top: rect.top, left: rect.left, width: rect.width, height: rect.height, boxShadow: "0 0 0 9999px color-mix(in oklab, var(--color-fg) 42%, transparent)", pointerEvents: "none" }} />
      ) : (
        <div className="absolute inset-0 fade-in" style={{ background: "color-mix(in oklab, var(--color-fg) 42%, transparent)", pointerEvents: blocking ? "auto" : "none" }} aria-hidden />
      )}
      {showPointer && rect && (
        <div aria-hidden className={`absolute text-ring ${pointerAbove ? "tour-bob-down" : "tour-bob-up"}`}
          style={pointerAbove
            ? { top: rect.top + rect.height + 2, left: rect.left + rect.width / 2 - 10, pointerEvents: "none" }
            : { top: rect.top - 22, left: rect.left + rect.width / 2 - 10, pointerEvents: "none" }}>
          <Icon name={pointerAbove ? "up" : "down"} className="size-5 drop-shadow" />
        </div>
      )}
      <div ref={cardRef} key={state.index} className={`tour-card absolute flex flex-col rounded-card border border-line bg-surface shadow-2xl ${placement === "sheet" ? "rounded-b-card" : ""}`} style={{ ...cardStyle, pointerEvents: "auto" }}>
        <div className="flex items-center gap-2 border-b border-line px-3 py-2">
          <Icon name="sparkle" className="size-3.5 text-ring" />
          <span className="min-w-0 flex-1 truncate text-[11px] font-medium text-fg-2">{state.tour.title}</span>
          <span className="tnum text-[10.5px] text-fg-3">{state.index + 1} / {state.tour.steps.length}</span>
          <button type="button" onClick={onSkip} aria-label="End the tour" title="End the tour (Esc)" className="grid size-6 place-items-center rounded-md text-fg-3 hover:bg-surface-3 hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"><Icon name="x" className="size-3.5" /></button>
        </div>
        <div className="min-h-0 overflow-auto px-3 py-2.5 scroll-thin">
          <h3 className="ds-card-title">{step.title}</h3>
          <div className="mt-1 text-[12px] leading-relaxed text-fg-2">{state.busy ? <span className="text-fg-3">…</span> : step.body}</div>
          {missing && !state.busy && <p className="mt-1.5 text-[11px] text-warning">That part isn't on screen right now (it may need a HUD or a different page). You can still continue.</p>}
        </div>
        <div className="flex items-center gap-1 border-t border-line px-3 py-2">
          <div className="flex items-center gap-1" aria-hidden>
            {state.tour.steps.map((_, i) => <span key={i} className={`h-1 rounded-full transition-all ${i === state.index ? "w-4 bg-ring" : i < state.index ? "w-1.5 bg-ring/50" : "w-1.5 bg-line-2"}`} />)}
          </div>
          <div className="ml-auto flex items-center gap-1.5">
            {state.index > 0 && <Button size="sm" tone="ghost" onClick={onBack} disabled={state.busy}>Back</Button>}
            <Button size="sm" tone="primary" onClick={onNext} disabled={state.busy} autoFocus>{last ? "Done" : "Next"}</Button>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}
