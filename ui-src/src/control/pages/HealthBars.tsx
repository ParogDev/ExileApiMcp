// Health Bars: the PoE2 health bar plugin "Whats A Health Bar" live. A preview canvas renders the bars exactly as the
// plugin draws them in game, from the live settings (every slider or colour change shows at once, drafts included),
// with the plugin's own fake hits and heals and an optional stress grid; a kind selector drives the per-kind controls;
// Look, Animation, Visibility and Bridge get their controls (optimistic, undo like the Settings page); "Preview in game"
// and a "live in game" switch show the result on the HUD through healthbars_preview; the cost strip reads
// healthbars_state at 1 Hz (own timings, counts, why bars are suppressed) next to the HUD's counters for this plugin and
// the stock HealthBars; the recent kills; and "More" for state the page has no view for yet.

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Banner, EmptyState, Skeleton, useNow } from "../../components";
import { Icon } from "../../icons";
import { KINDS, KIND_GROUPS, KIND_LABELS, PREVIEW_POOLS, animFrom, css, drawBar, lookFrom, previewSim, type Look, type PoolSim } from "../healthbars/bars";
import type { ControlStore, Snapshot } from "../store";
import { HEALTHBARS_PLUGIN, type HealthBarsKill, type HealthBarsState, type PluginPerfRow, type RouteError, type SettingNode } from "../types";
import { Badge, Button, Card, ColorControl, JsonView, Segmented, Select, Slider, Stat, Switch, agoShort, fmtNum } from "../ui";
import { UndoBar } from "./Settings";

const UNDO_MS = 8000;
const AUTO_PREVIEW_MS = 700;
const STRESS_IN_GAME = 500;
const LIVE_KEY = "hexile.healthbars.liveInGame";
/** The fields this page renders; everything else in the state goes under "More". */
const KNOWN = new Set<string>(["game", "plugin", "schema", "enabled", "suppressed", "area", "counts", "timings", "preview", "bridge", "poolProblem", "kills"]);
type Resolution = "1080" | "1440" | "2160";

export function HealthBarsPage({ store, snap }: { store: ControlStore; snap: Snapshot }) {
  useEffect(() => { store.setHealthBarsLive(true); return () => store.setHealthBarsLive(false); }, [store]);
  const now = useNow(1000);
  const hb = snap.hb;
  const s = hb.state;
  const st = store.settingsFor(snap.game);
  const hasTool = !snap.catalog || store.hasTool("healthbars_state");
  const noHud = store.gamesUp.length === 0 && snap.gamesAt !== undefined;
  const unreachable = snap.games.find((g) => g.status === "unreachable");
  const stale = hb.at !== undefined && now - hb.at > 5000;

  // The plugin's settings (pulled once with every plugin's, as the Settings page does), by path.
  useEffect(() => { if (!st.result && !st.loading && !st.error && snap.game) void store.pullSettings(); }, [snap.game]); // eslint-disable-line react-hooks/exhaustive-deps
  const plugin = st.result?.plugins.find((p) => p.plugin === HEALTHBARS_PLUGIN);
  const nodes = useMemo(() => new Map((plugin?.settings ?? []).map((n) => [n.path, n])), [plugin]);

  // Drafts: values a slider or colour picker passes through before the commit, so the canvas follows the hand.
  const [drafts, setDrafts] = useState<Record<string, unknown>>({});
  const setDraft = useCallback((path: string, v: unknown) => setDrafts((d) => ({ ...d, [path]: v })), []);
  const clearDraft = useCallback((path: string) => setDrafts((d) => { if (!(path in d)) return d; const n = { ...d }; delete n[path]; return n; }), []);
  const get = useCallback((path: string) => (path in drafts ? drafts[path] : nodes.get(path)?.value), [drafts, nodes]);

  const [focus, setFocus] = useState(0);
  const [stress, setStress] = useState(0);
  const [paused, setPaused] = useState(false);
  const [resolution, setResolution] = useState<Resolution>("1440");
  const [liveInGame, setLiveInGame] = useState<boolean>(() => { try { return localStorage.getItem(LIVE_KEY) === "1"; } catch { return false; } });
  useEffect(() => { try { localStorage.setItem(LIVE_KEY, liveInGame ? "1" : "0"); } catch { /* sandbox */ } }, [liveInGame]);
  const resolutionScale = Number(resolution) / 1440;
  const look = useMemo(() => lookFrom(get, resolutionScale), [get, resolutionScale]);
  const anim = useMemo(() => animFrom(get), [get]);

  // A committed change while "live in game" is on: show it on the HUD after a short pause (one preview per burst of changes).
  const autoTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const afterChange = useCallback(() => {
    if (!liveInGame) return;
    clearTimeout(autoTimer.current);
    autoTimer.current = setTimeout(() => void store.previewHealthBars(8, KINDS[focus]), AUTO_PREVIEW_MS);
  }, [liveInGame, focus, store]);
  useEffect(() => () => clearTimeout(autoTimer.current), []);

  const change = useCallback(async (node: SettingNode, value: unknown) => {
    const ok = await store.changeSetting(HEALTHBARS_PLUGIN, node, value);
    if (ok) afterChange();
    return ok;
  }, [store, afterChange]);

  // Own-cost history for the trend lines (one point per read).
  const history = useRef<{ at: number; tick: number; render: number }[]>([]);
  if (s && hb.at && history.current[history.current.length - 1]?.at !== hb.at) history.current = [...history.current.slice(-39), { at: hb.at, tick: s.timings.tickUs, render: s.timings.renderUs }];

  const extra = useMemo(() => {
    if (!s) return {};
    const o: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(s)) if (!KNOWN.has(k)) o[k] = v;
    return o;
  }, [s]);

  const enableNode = nodes.get("Enable");
  const groups = useMemo(() => {
    const m = new Map<string, SettingNode[]>();
    for (const n of plugin?.settings ?? []) { if (!n.group || n.kind === "button") continue; if (!m.has(n.group)) m.set(n.group, []); m.get(n.group)!.push(n); }
    return m;
  }, [plugin]);
  const otherGroups = [...groups.keys()].filter((g) => !["Look", "Animation", "Visibility", "Bridge"].includes(g) && !KIND_GROUPS.includes(g));
  const previewBusy = hb.previewing;
  const canPreview = !!snap.game && (!snap.catalog || store.hasTool("healthbars_preview"));

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <div className="flex min-w-0 items-center gap-2">
          <Icon name="healthBar" className="size-4 shrink-0 text-fg-3" />
          <h2 className="ds-title truncate">Health Bars</h2>
          {enableNode && <Switch size="sm" checked={!!enableNode.value} onChange={(v) => void change(enableNode, v)} label="Plugin enabled" disabled={st.pending.has(`${HEALTHBARS_PLUGIN}\u0000Enable`)} />}
          {s && !s.enabled && <Badge tone="neutral">disabled</Badge>}
          {s?.area && <Badge tone="neutral" title="The area the plugin sees">{s.area}</Badge>}
          {s?.suppressed ? <Badge tone="warning" icon="eyeOff" title="Why no bars are drawn right now">{s.suppressed}</Badge> : s && <Badge tone="success" icon="eye">drawing</Badge>}
          {s?.preview.active && <Badge tone="accent" icon="play" title={`The in-game preview runs${s.preview.focus ? `, ${s.preview.focus} first` : ""}`}>in game · {fmtNum(s.preview.secondsLeft, 0)} s{s.preview.bars ? ` · ${s.preview.bars} bars` : ""}</Badge>}
        </div>
        <div className="ml-auto flex flex-wrap items-center gap-2 text-[11px] text-fg-3">
          <span className="flex items-center gap-1.5 tnum" title={hb.at ? `Last read ${new Date(hb.at).toLocaleTimeString()} in ${fmtNum(hb.ms, 0)} ms · ${hb.reads} reads this session` : "Nothing read yet"}>
            <span className={`size-1.5 rounded-full ${hb.live && s && !hb.error && !stale ? "bg-success live-dot" : hb.error ? "bg-danger" : "bg-fg-3"}`} />
            {!snap.game ? "no HUD" : hb.live ? (hb.error ? "retrying every 5 s" : "1 Hz while visible") : "paused"}
            {hb.at !== undefined && <span className="hidden sm:inline">· read {agoShort(now - hb.at)}</span>}
          </span>
          <label className="flex items-center gap-1.5 text-fg-2" title="Poll healthbars_state every second while this page is visible">
            <Switch size="sm" checked={hb.live} onChange={(v) => store.setHealthBarsLive(v)} label="Live" disabled={!snap.game || !hasTool} />live
          </label>
          <Button size="sm" icon="sync" busy={hb.loading && !hb.live} disabled={!snap.game || !hasTool} onClick={() => void store.refreshHealthBars(true)} title="Read healthbars_state and hud_plugin_perf once now">Refresh</Button>
        </div>
      </div>

      {!hasTool && <Banner tone="info" icon="info" title="This server has no healthbars_state tool">The Health Bars page needs an ExileApiMcp build with <code className="font-code">healthbars_state</code> and <code className="font-code">healthbars_preview</code> (Tools/HealthBarsTools.cs). Deploy a newer build (mcp_deploy) and reload this page.</Banner>}
      {hasTool && noHud && (unreachable ? (
        <Banner tone="danger" icon="offline" title={`The ${unreachable.game} HUD bridge is unreachable`} action={<Button size="sm" onClick={() => void store.refreshGames()}>Retry</Button>}>
          {unreachable.error ?? "The bridge files exist but the HUD does not answer."} Is the HUD running with "Whats An AI Bridge" enabled? This page checks again every 20 s.
        </Banner>
      ) : (
        <Banner tone="warning" icon="offline" title="No HUD is up">The health bar plugin runs inside the PoE2 HUD. Start it with "Whats An AI Bridge" and "Whats A Health Bar" enabled; this page notices within 20 s.</Banner>
      ))}
      {hasTool && !noHud && hb.error && <HbErrorBanner error={hb.error} onRetry={() => void store.refreshHealthBars(true)} />}
      {hasTool && !noHud && st.result && !plugin && !hb.error && <Banner tone="warning" icon="puzzle" title="hud_settings lists no plugin named Whats A Health Bar">The HUD reports {st.result.plugins.length} plugins ({st.result.plugins.map((p) => p.plugin).slice(0, 6).join(", ")}{st.result.plugins.length > 6 ? ", …" : ""}). The canvas below uses the plugin's defaults; the controls need the plugin loaded.</Banner>}
      {s?.bridge && !s.bridge.linked && <Banner tone="warning" icon="warning" title="The plugin is not linked to the AI Bridge's pub/sub">{s.bridge.why ?? "No reason given."} Previews still work through WhatsAHealthBar.Preview; kills are not published until the link is back.</Banner>}
      {s?.poolProblem && <Banner tone="warning" icon="braces" title="A pool read that couldn't be real (the last 30 s)">{s.poolProblem} The plugin skips such reads; a Life component offset has probably moved with a patch (memory_layout on the entity's Life component shows what the HUD maps).</Banner>}

      <Card pad={false} tour={undefined}
        title={<span className="flex items-center gap-2">Preview {!plugin && <Badge tone="neutral">plugin defaults</Badge>}</span>} icon="healthBar"
        right={<span className="flex flex-wrap items-center gap-2">
          <Segmented size="sm" label="Game window height" value={resolution} onChange={setResolution} options={[{ value: "1080", label: "1080p" }, { value: "1440", label: "1440p" }, { value: "2160", label: "2160p" }]} />
          <Segmented size="sm" label="Stress bars" value={String(stress)} onChange={(v) => setStress(Number(v))} options={[{ value: "0", label: "samples", title: "The plugin's seven preview bars" }, { value: "200", label: "+200", title: "200 more animated bars" }, { value: "500", label: "+500", title: "500 more animated bars: the plugin batches them into one draw call" }]} />
          <Button size="sm" icon={paused ? "play" : "pause"} onClick={() => setPaused((p) => !p)} title={paused ? "Run the animation" : "Freeze the animation"} ariaLabel={paused ? "Run" : "Pause"} />
        </span>}>
        <PreviewCanvas look={look} anim={anim} focus={focus} stress={stress} paused={paused} resolutionScale={resolutionScale} />
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-t border-line px-3 py-2">
          <KindChips look={look} focus={focus} onFocus={setFocus} />
          <div className="ml-auto flex flex-wrap items-center gap-2">
            <PoolLegend look={look} />
            <Button size="sm" icon="play" tone="primary" busy={previewBusy} disabled={!canPreview} onClick={() => void store.previewHealthBars(8, KINDS[focus])} title={`healthbars_preview seconds=8 focus=${KINDS[focus]}: the plugin draws its sample bars mid-screen in game, ${KINDS[focus]} first`}>Preview in game</Button>
            <Button size="sm" icon="activity" busy={previewBusy} disabled={!canPreview} onClick={() => void store.previewHealthBars(10, KINDS[focus], STRESS_IN_GAME)} title={`healthbars_preview stress=${STRESS_IN_GAME}: the plugin also draws ${STRESS_IN_GAME} animated bars on a grid in game for 10 s; the cost strip then shows the per-bar cost over them`}>Stress test in game ({STRESS_IN_GAME})</Button>
            <label className="flex items-center gap-1.5 text-[11px] text-fg-2" title="Every setting change on this page previews in game after a short pause, so you see it on the HUD without a monster nearby">
              <Switch size="sm" checked={liveInGame} onChange={setLiveInGame} label="Live in game" disabled={!canPreview} />live in game
            </label>
          </div>
        </div>
      </Card>

      {s ? <CostStrip s={s} look={look} perf={hb.perf} history={history.current} now={now} /> : !hb.error && hasTool && !noHud && (
        <Card><EmptyState icon="activity" title={hb.loading ? "Reading the health bar plugin…" : snap.game ? "Nothing read yet" : "No HUD selected"} className="py-6">{snap.game ? <>healthbars_state asks the HUD for WhatsAHealthBar.GetState once a second.</> : <>Pick a HUD in the header.</>}</EmptyState></Card>
      )}

      {plugin ? (
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-2 lg:items-start">
          <div className="flex min-w-0 flex-col gap-3">
            <Card pad={false} icon="target" title={<span className="flex items-center gap-2">{KIND_GROUPS[focus]}<KindSwatch look={look} kind={focus} /></span>} right={<span className="text-[10.5px] text-fg-3">pick a kind under the preview</span>}>
              <Rows nodes={groups.get(KIND_GROUPS[focus]) ?? []} pending={st.pending} get={get} setDraft={setDraft} clearDraft={clearDraft} onChange={change} />
            </Card>
            <Card pad={false} icon="sliders" title="Look"><Rows nodes={groups.get("Look") ?? []} pending={st.pending} get={get} setDraft={setDraft} clearDraft={clearDraft} onChange={change} /></Card>
          </div>
          <div className="flex min-w-0 flex-col gap-3">
            <Card pad={false} icon="activity" title="Animation"><Rows nodes={groups.get("Animation") ?? []} pending={st.pending} get={get} setDraft={setDraft} clearDraft={clearDraft} onChange={change} /></Card>
            <Card pad={false} icon="eye" title="Visibility"><Rows nodes={groups.get("Visibility") ?? []} pending={st.pending} get={get} setDraft={setDraft} clearDraft={clearDraft} onChange={change} /></Card>
            <Card pad={false} icon="radio" title="Bridge" right={s?.bridge && <Badge tone={s.bridge.linked ? "success" : "warning"} title={s.bridge.why ?? `subscribes ${s.bridge.subscribes ?? "?"}, publishes ${s.bridge.publishes ?? "?"}`}>{s.bridge.linked ? "linked" : "not linked"}</Badge>}>
              <Rows nodes={groups.get("Bridge") ?? []} pending={st.pending} get={get} setDraft={setDraft} clearDraft={clearDraft} onChange={change} />
              {s?.bridge && <p className="px-3 pb-2 text-[11px] text-fg-3">Subscribes <code className="font-code">{s.bridge.subscribes}</code>, publishes <code className="font-code">{s.bridge.publishes}</code>{hb.lastPreview && hb.previewAt ? <> · last preview {agoShort(now - hb.previewAt)} via {hb.lastPreview.via === "pubsub" ? "pub/sub" : "the plugin's method"}{hb.lastPreview.agentListeners?.length ? ` (also heard by ${hb.lastPreview.agentListeners.join(", ")})` : ""}</> : null}</p>}
            </Card>
            {otherGroups.map((g) => <Card key={g} pad={false} icon="puzzle" title={g}><Rows nodes={groups.get(g) ?? []} pending={st.pending} get={get} setDraft={setDraft} clearDraft={clearDraft} onChange={change} /></Card>)}
          </div>
        </div>
      ) : st.loading && !st.result ? <Card><Skeleton kind="rows" count={5} /></Card> : st.error ? (
        <Banner tone="danger" icon="warning" title="Could not pull the settings" action={<Button size="sm" onClick={() => void store.pullSettings()}>Retry</Button>}>{st.error}</Banner>
      ) : null}

      {s && (
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)] lg:items-start">
          <Kills kills={s.kills} now={now} />
          {Object.keys(extra).length > 0 && (
            <Card title="More" icon="braces" right={<span className="text-[10.5px] text-fg-3">fields this page has no view for yet</span>}>
              <JsonView value={extra} open={1} />
            </Card>
          )}
        </div>
      )}

      {snap.undo && snap.undo.plugin === HEALTHBARS_PLUGIN && now - snap.undo.at < UNDO_MS && <UndoBar store={store} label={snap.undo.label} previous={snap.undo.previous} />}
    </div>
  );
}

// ── The canvas ───────────────────────────────────────────────────────

interface Sim { previews: PoolSim[]; stress: PoolSim[]; last: number; drawMs: number; stressKey: string; focus: number }

function PreviewCanvas({ look, anim, focus, stress, paused, resolutionScale }: { look: Look; anim: ReturnType<typeof animFrom>; focus: number; stress: number; paused: boolean; resolutionScale: number }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const latest = useRef({ look, anim, focus, paused, resolutionScale, stress });
  latest.current = { look, anim, focus, paused, resolutionScale, stress };
  const sim = useRef<Sim>(undefined);
  const [stats, setStats] = useState({ bars: 0, drawMs: 0 });
  const rowStep = 36 * resolutionScale;
  const height = Math.max(200, Math.round(44 + 7 * rowStep + 30));

  useEffect(() => {
    const el = canvas.current;
    if (!el) return;
    const ctx = el.getContext("2d");
    if (!ctx) return;
    if (!sim.current) sim.current = { previews: [], stress: [], last: performance.now() / 1000, drawMs: 0, stressKey: "", focus: -1 };
    const S = sim.current;
    let raf = 0, lastStats = 0;
    const frame = () => {
      raf = requestAnimationFrame(frame);
      const { look, anim, focus, paused, resolutionScale, stress } = latest.current;
      const now = performance.now() / 1000;
      const dt = Math.min(0.1, now - S.last);
      S.last = now;
      const dpr = Math.max(1, Math.min(3, Math.round(window.devicePixelRatio || 1)));
      const cw = el.clientWidth, ch = el.clientHeight;
      if (el.width !== Math.round(cw * dpr) || el.height !== Math.round(ch * dpr)) { el.width = Math.round(cw * dpr); el.height = Math.round(ch * dpr); }
      if (S.focus !== focus) {
        // The plugin's StartPreview: fresh sims, the focused kind first, then the rest in their natural order.
        S.focus = focus;
        S.previews = KINDS.map((_, i) => i).sort((a, b) => (a === focus ? -1 : a) - (b === focus ? -1 : b)).map((k) => previewSim(k, now));
      }
      const stressKey = `${stress}:${cw}:${ch}`;
      if (S.stressKey !== stressKey) {
        // The plugin's stress grid: mostly normals like a real pack, rows that fit, wrapping past one screenful.
        S.stressKey = stressKey;
        S.stress = [];
        for (let i = 0; i < stress; i++) S.stress.push(previewSim(i % 4 === 0 ? Math.floor(i / 4) % 4 : 3, now - Math.random() * 3));
      }
      if (!paused) {
        const top = 44;
        S.previews.forEach((sm, i) => {
          sm.step(now, dt, Math.random);
          sm.bar.x = cw * 0.5;
          sm.bar.y = top + i * 36 * resolutionScale;
          sm.bar.animate(now, dt, anim);
        });
        const cols = Math.max(1, Math.floor((cw - 20) / 90));
        const rows = Math.max(1, Math.floor((ch - 60) / 22));
        S.stress.forEach((sm, i) => {
          sm.step(now, dt, Math.random);
          const slot = i % (cols * rows);
          sm.bar.x = 55 + (slot % cols) * 90 + Math.floor(i / (cols * rows)) * 6;
          sm.bar.y = 60 + Math.floor(slot / cols) * 22;
          sm.bar.animate(now, dt, anim);
        });
      }
      const t0 = performance.now();
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      backdrop(ctx, el.width, el.height);
      for (const sm of S.stress) drawBar(ctx, look, sm.bar, now, dpr);
      for (const sm of S.previews) drawBar(ctx, look, sm.bar, now, dpr, KIND_LABELS[sm.bar.kind]);
      const ms = performance.now() - t0;
      S.drawMs += (ms - S.drawMs) * 0.08;
      if (now - lastStats > 0.5) { lastStats = now; setStats({ bars: S.previews.length + S.stress.length, drawMs: S.drawMs }); }
    };
    raf = requestAnimationFrame(frame);
    return () => cancelAnimationFrame(raf);
  }, []);

  return (
    <div className="relative">
      <canvas ref={canvas} className="block w-full" style={{ height }} aria-label="The bars as the plugin draws them" />
      <div className="pointer-events-none absolute bottom-1.5 right-2 rounded-sm bg-black/40 px-1.5 py-0.5 font-code text-[10px] text-white/70 tnum">{stats.bars} bars · {stats.drawMs.toFixed(2)} ms/frame in this browser</div>
    </div>
  );
}

/** A dark, game-like ground: a warm radial gradient with a vignette, nothing that competes with the bars. */
function backdrop(ctx: CanvasRenderingContext2D, w: number, h: number) {
  const g = ctx.createRadialGradient(w * 0.5, h * 0.45, 0, w * 0.5, h * 0.45, Math.max(w, h) * 0.75);
  g.addColorStop(0, "#2b2520");
  g.addColorStop(0.6, "#18140f");
  g.addColorStop(1, "#0a0908");
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, w, h);
  const band = ctx.createLinearGradient(0, h * 0.55, 0, h);
  band.addColorStop(0, "rgba(60,48,30,0)");
  band.addColorStop(1, "rgba(60,48,30,0.22)");
  ctx.fillStyle = band;
  ctx.fillRect(0, h * 0.55, w, h * 0.45);
}

// ── Kinds, pools ─────────────────────────────────────────────────────

/** A mini bar in the kind's colours: the pools laid out as the plugin lays them out (a sandwich of layers, side by side, or life only), by their sample shares. */
function KindSwatch({ look, kind, w = 28 }: { look: Look; kind: number; w?: number }) {
  const st = look.styles[kind];
  const [life, es, ward] = PREVIEW_POOLS[kind];
  const lifeOnly = look.pools === "Life only";
  const total = lifeOnly ? life : life + es + ward;
  const sandwich = look.pools === "Sandwich";
  const parts = sandwich ? [[ward, look.ward], [es, look.es], [life, st.life]] as const : [[life, st.life], [es, look.es], [ward, look.ward]] as const;
  return (
    <span className={`inline-flex shrink-0 overflow-hidden rounded-[2px] border ${sandwich ? "flex-col" : ""}`} style={{ width: w, height: 8, borderColor: css(look.outline), background: css(look.bg) }} aria-hidden>
      {parts.map(([n, c], i) => (n > 0 && (!lifeOnly || c === st.life)) ? <span key={i} style={sandwich ? { height: `${(n / total) * 100}%`, width: "100%", background: css(c) } : { width: `${(n / total) * 100}%`, background: css(c) }} /> : null)}
    </span>
  );
}

function KindChips({ look, focus, onFocus }: { look: Look; focus: number; onFocus: (i: number) => void }) {
  return (
    <div role="radiogroup" aria-label="Kind" className="flex flex-wrap gap-1">
      {KINDS.map((k, i) => {
        const st = look.styles[i];
        const on = i === focus;
        return (
          <button key={k} type="button" role="radio" aria-checked={on} onClick={() => onFocus(i)} title={`${KIND_GROUPS[i]}: ${st.show ? `${Math.round(st.w)} × ${Math.round(st.h)} px` : "hidden"}; sample ${PREVIEW_POOLS[i].join(" / ")} life / ES / ward`}
            className={`inline-flex h-7 items-center gap-1.5 rounded-md border px-2 text-[11.5px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${on ? "border-fg bg-fg text-surface" : "border-line bg-surface text-fg-2 hover:border-line-2 hover:text-fg"} ${st.show ? "" : "opacity-60"}`}>
            <KindSwatch look={look} kind={i} w={22} />
            {KIND_LABELS[i]}
            {!st.show && <Icon name="eyeOff" className="size-3" />}
          </button>
        );
      })}
    </div>
  );
}

function PoolLegend({ look }: { look: Look }) {
  const chip = (label: string, color: string, title: string) => (
    <span className="flex items-center gap-1" title={title}><span className="h-2 w-3 rounded-[2px]" style={{ background: color }} />{label}</span>
  );
  const mode = look.pools === "Side by side" ? "side by side: each pool its region of the width" : look.pools === "Sandwich" ? "sandwich: layers ward / ES / life, each as thick as its share" : "life only";
  return (
    <span className="flex flex-wrap items-center gap-2.5 text-[10.5px] text-fg-3">
      {chip("life", css(look.styles[0].life), "Life, in the kind's colour; regenerates slowly, flasked when low")}
      {look.pools !== "Life only" && chip("ES", css(look.es), "Energy shield: taken before life, recharges after 1.6 s without a hit")}
      {look.pools !== "Life only" && chip("ward", css(look.ward), "Ward: taken first, restores 2.4 s after it breaks")}
      {look.totalLine !== "Off" && chip("total", css(look.text, 0.9), `The thin line under the bar: life + ES + ward over their normal maxima, spilling past 100% (${look.totalLine.toLowerCase()})`)}
      <span title="Every few seconds the simulation overcaps a pool (a spill past the bar's end, faint for a little, bolder for more), grants a pool the monster has no max for (a translucent veil) or buffs max life for 4 s (the fill eases down quietly: not a hit)">{mode} · spills, veils and buffs every few seconds</span>
    </span>
  );
}

// ── Settings rows ────────────────────────────────────────────────────

function Rows({ nodes, pending, get, setDraft, clearDraft, onChange }: { nodes: SettingNode[]; pending: ReadonlySet<string>; get: (p: string) => unknown; setDraft: (p: string, v: unknown) => void; clearDraft: (p: string) => void; onChange: (n: SettingNode, v: unknown) => Promise<boolean> }) {
  if (!nodes.length) return <p className="px-3 py-2 text-[11px] text-fg-3">Nothing here in this build of the plugin.</p>;
  return (
    <ul className="divide-y divide-line">
      {nodes.map((n) => <Row key={n.path} node={n} pending={pending.has(`${HEALTHBARS_PLUGIN}\u0000${n.path}`)} value={get(n.path)} setDraft={(v) => setDraft(n.path, v)} clearDraft={() => clearDraft(n.path)} onChange={(v) => void onChange(n, v)} />)}
    </ul>
  );
}

function Row({ node: n, pending, value, setDraft, clearDraft, onChange }: { node: SettingNode; pending: boolean; value: unknown; setDraft: (v: unknown) => void; clearDraft: () => void; onChange: (v: unknown) => void }) {
  const [flash, setFlash] = useState(0);
  const [wasPending, setWasPending] = useState(false);
  useEffect(() => { if (pending) setWasPending(true); else if (wasPending) { setWasPending(false); setFlash(Date.now()); } }, [pending]); // eslint-disable-line react-hooks/exhaustive-deps
  const disabled = pending || n.readOnly;
  const committed = n.value;
  let control: ReactNode;
  switch (n.kind) {
    case "toggle":
      control = <Switch size="sm" checked={!!value} onChange={onChange} label={n.label} disabled={disabled} />;
      break;
    case "range": {
      const num = typeof value === "number" ? value : Number(value ?? 0);
      const min = n.min ?? 0, max = n.max ?? Math.max(100, num * 2);
      const fine = !Number.isInteger(min) || !Number.isInteger(max) || /Rounding/.test(n.path);
      control = <div className="w-full min-w-0 sm:w-56"><Slider value={num} min={min} max={max} step={fine ? Math.max(0.01, Math.round(((max - min) / 100) * 100) / 100) : undefined} onChange={setDraft} onCommit={(v) => { clearDraft(); if (v !== committed) onChange(v); }} label={n.label} disabled={disabled} /></div>;
      break;
    }
    case "list":
      control = <Select value={String(value ?? "")} options={(n.options ?? [String(value ?? "")]).map((o) => ({ value: o }))} onChange={onChange} label={n.label} disabled={disabled} className="sm:w-44" />;
      break;
    case "color":
      control = <ColorControl size="sm" value={String(value ?? "#ffffffff")} onPreview={setDraft} onChange={(v) => { clearDraft(); onChange(v); }} label={n.label} disabled={disabled} />;
      break;
    default:
      control = <code className="truncate font-code text-[11px] text-fg-3">{JSON.stringify(value)}</code>;
  }
  return (
    <li className={`flex flex-col gap-1 px-3 py-1.5 sm:flex-row sm:items-center sm:gap-3 ${pending ? "opacity-70" : ""}`}>
      <div className="min-w-0 flex-1" title={n.description ?? n.path}>
        <div className="flex items-center gap-1.5">
          <span className="truncate text-[12px] font-medium">{n.label}</span>
          {pending && <Icon name="sync" className="spin size-3 text-fg-3" />}
          {n.readOnly && <Badge tone="neutral">in game only</Badge>}
        </div>
        {n.description && <p className="mt-0.5 line-clamp-2 text-[10.5px] leading-snug text-fg-3">{n.description}</p>}
      </div>
      <div key={flash} className={`flex shrink-0 items-center rounded-md ${flash ? "flash-accent" : ""}`}>{control}</div>
    </li>
  );
}

// ── The cost strip ───────────────────────────────────────────────────

function CostStrip({ s, look, perf, history, now }: { s: HealthBarsState; look: Look; perf?: { ours?: PluginPerfRow; stock?: PluginPerfRow; at: number; note?: string | null }; history: { tick: number; render: number }[]; now: number }) {
  const t = s.timings, c = s.counts;
  const total = (r?: PluginPerfRow) => (r?.tick?.avgMs ?? 0) + (r?.render?.avgMs ?? 0);
  const ours = total(perf?.ours), stock = total(perf?.stock);
  const ratio = perf?.ours && perf?.stock && perf.stock.enabled && ours > 0 ? stock / ours : undefined;
  const byKind = KINDS.map((k, i) => [k, c.byKind?.[k.toLowerCase()] ?? 0, i] as const).filter(([, n]) => n > 0);
  const previewBars = s.preview.active ? s.preview.bars ?? 0 : 0;
  const barsNow = c.drawn + previewBars;
  const path = typeof t.renderPath === "string" ? t.renderPath : undefined;
  const fallback = path?.startsWith("fallback");
  const hasSplit = typeof t.buildUs === "number" || typeof t.flushUs === "number" || typeof t.quads === "number";
  return (
    <Card title="Live cost" icon="activity" pad={false}
      right={<span className="flex flex-wrap items-center gap-2 text-[10.5px] text-fg-3">
        {s.suppressed ? <Badge tone="warning" icon="eyeOff">not drawing: {s.suppressed}</Badge> : <Badge tone="success" icon="eye">drawing {c.drawn}</Badge>}
        {previewBars >= 100 && <Badge tone="accent" icon="activity" title="The stress test running in game: the plugin's own render time for all its bars this frame">{previewBars} bars ≈ {fmtNum(t.renderUs / 1000, 2)} ms</Badge>}
        {path && <Badge tone={fallback ? "warning" : "success"} icon={fallback ? "warning" : "bolt"} title={fallback ? path : "Vertices written straight into the draw list: one draw command for every bar"}>{fallback ? "fallback" : "batched"}</Badge>}
        <span className="tnum" title="Measured by the plugin itself with a Stopwatch around its Tick and Render">own timings · max over {t.windowSec} s</span>
      </span>}>
      <div className="grid grid-cols-2 gap-2 p-3 sm:grid-cols-3 lg:grid-cols-6">
        <Stat label="Drawn" value={c.drawn} sub={`${c.wanted} wanted · ${c.tracked} tracked · peak ${c.peakDrawn}${previewBars ? ` · +${previewBars} preview` : ""}`} tone={c.drawn > 0 ? "good" : undefined} />
        <Stat label="Tick" value={fmtNum(t.tickUs, 1)} unit="µs" sub={`max ${fmtNum(t.tickMaxUs, 0)} µs · classify ${fmtNum(t.classifyUs, 1)} µs at ${t.classifyHz} Hz`} trend={history.map((h) => h.tick)} tone={t.tickUs > 200 ? "warn" : undefined} />
        <Stat label="Render" value={fmtNum(t.renderUs, 1)} unit="µs" sub={hasSplit ? `max ${fmtNum(t.renderMaxUs, 0)} µs · build ${fmtNum(t.buildUs, 0)} + flush ${fmtNum(t.flushUs, 0)}` : `max ${fmtNum(t.renderMaxUs, 0)} µs`} trend={history.map((h) => h.render)} tone={fallback ? "warn" : t.renderUs > 200 ? "warn" : undefined} />
        <Stat label="Per bar" value={t.perBarNs > 0 ? fmtNum(t.perBarNs, 0) : "–"} unit="ns" sub={barsNow > 0 ? `tick + render over ${barsNow} bars` : "no bar drawn"} tone={previewBars >= 100 ? "accent" : undefined} />
        <Stat label="Quads" value={typeof t.quads === "number" ? fmtNum(t.quads, 0) : "–"} sub={typeof t.quads === "number" ? (barsNow > 0 ? `${fmtNum(t.quads / barsNow, 1)} per bar, one batch` : "written last frame") : "older plugin build"} />
        <Stat label="vs HealthBars" value={ratio !== undefined ? `${fmtNum(ratio, 1)}×` : "–"} unit={ratio !== undefined ? "cheaper" : undefined}
          sub={!perf ? "hud_plugin_perf not read yet" : !perf.ours ? "this plugin has no HUD counter" : !perf.stock ? "stock HealthBars not loaded" : !perf.stock.enabled ? `stock HealthBars disabled (${fmtNum(ours * 1000, 0)} µs ours)` : `${fmtNum(ours * 1000, 0)} µs vs ${fmtNum(stock * 1000, 0)} µs per frame, HUD counters`}
          tone={ratio === undefined ? undefined : ratio >= 1 ? "good" : "bad"} />
      </div>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-t border-line px-3 py-1.5 text-[10.5px] text-fg-3">
        <span>drawn per kind</span>
        {byKind.length === 0 ? <span>none</span> : byKind.map(([k, n, i]) => <span key={k} className="flex items-center gap-1 tnum"><KindSwatch look={look} kind={i} w={16} />{KIND_LABELS[i]} {n}</span>)}
        {fallback && <span className="text-warning" title={path}>{path}</span>}
        {perf?.at && <span className="ml-auto" title={perf.note ?? undefined}>HUD counters {agoShort(now - perf.at)}</span>}
      </div>
    </Card>
  );
}

// ── Kills ────────────────────────────────────────────────────────────

function Kills({ kills, now }: { kills: HealthBarsKill[]; now: number }) {
  return (
    <Card title="Recent kills" icon="skull" pad={false} right={<span className="tnum text-[10.5px] text-fg-3">{kills.length} published</span>}>
      {kills.length === 0 ? <EmptyState icon="skull" title="No kills published yet" className="py-5">A unique (or a rare, by the Publish kills setting) dying with its bar on screen is published as healthbars.kill.</EmptyState> : (
        <table className="w-full border-collapse text-[11px]">
          <thead><tr className="bg-surface-2 ds-label text-left text-fg-3"><th className="px-3 py-1 font-semibold">Name</th><th className="px-2 py-1 font-semibold">Rarity</th><th className="px-2 py-1 text-right font-semibold" title="Life / energy shield / ward">Pools</th><th className="px-2 py-1 text-right font-semibold" title="From the first hit the plugin saw to death">Time to kill</th><th className="hidden px-2 py-1 font-semibold sm:table-cell">Area</th><th className="px-3 py-1 text-right font-semibold">When</th></tr></thead>
          <tbody>
            {kills.map((k, i) => {
              const at = k.at ? Date.parse(k.at) : NaN;
              return (
                <tr key={i} className="border-t border-line hover:bg-surface-2/60">
                  <td className="truncate px-3 py-1 font-medium" title={k.path ?? undefined}>{k.name || "?"}</td>
                  <td className="px-2 py-1"><Badge tone={k.rarity === "Unique" ? "warning" : k.rarity === "Rare" ? "accent" : "neutral"}>{k.rarity || "?"}</Badge></td>
                  <td className="whitespace-nowrap px-2 py-1 text-right tnum text-fg-2">{fmtNum(k.maxLife, 0)}{k.maxEs > 0 ? ` / ${fmtNum(k.maxEs, 0)}` : ""}{(k.maxWard ?? 0) > 0 ? ` / ${fmtNum(k.maxWard, 0)}` : ""}</td>
                  <td className="px-2 py-1 text-right tnum">{k.timeToKillMs != null ? `${fmtNum(k.timeToKillMs / 1000, 1)} s` : <span className="text-fg-3">no hit seen</span>}</td>
                  <td className="hidden truncate px-2 py-1 text-fg-2 sm:table-cell">{k.area ?? ""}</td>
                  <td className="px-3 py-1 text-right tnum text-fg-3" title={k.at ?? undefined}>{Number.isFinite(at) ? agoShort(now - at) : ""}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </Card>
  );
}

// ── Errors ───────────────────────────────────────────────────────────

function HbErrorBanner({ error, onRetry }: { error: RouteError; onRetry: () => void }) {
  const retry = <Button size="sm" onClick={onRetry}>Retry</Button>;
  switch (error.error) {
    case "plugin_not_loaded":
      return <Banner tone="warning" icon="puzzle" title="Whats A Health Bar is not loaded in this HUD" action={retry}>{error.message} Enable the plugin in the HUD menu, or check hud_plugins for a compile error.</Banner>;
    case "unreachable": case "bridge_unavailable":
      return <Banner tone="danger" icon="offline" title="The HUD bridge is unreachable" action={retry}>{error.message}</Banner>;
    case "plugin_threw":
      return <Banner tone="danger" icon="warning" title="The health bar plugin threw while reporting its state" action={retry}>{error.message}</Banner>;
    case "bridge_too_old":
      return <Banner tone="warning" icon="puzzle" title="The HUD's bridge is too old for this page" action={retry}>{error.message}</Banner>;
    case "healthbars_not_json": case "healthbars_shape":
      return <Banner tone="danger" icon="braces" title="The plugin's state does not fit this page" action={retry}>{error.message} The server's HealthBarsStateResult (Tools/HealthBarsDtos.cs) mirrors the plugin's HealthBarStateDto; change both together.</Banner>;
    default:
      return <Banner tone="danger" icon="warning" title="healthbars_state failed" action={retry}>{error.message ?? error.error}</Banner>;
  }
}

