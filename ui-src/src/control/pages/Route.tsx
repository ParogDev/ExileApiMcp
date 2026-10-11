// Route: the live state of the PoE2 campaign navigation plugin "Whats A Route" (route_state, polled at 1 Hz while
// this page is open and visible). The map (the area's grid as the frame, the route, the player, the goal), the
// objective and why there is no path, the steps with done / current / optional, the active quests, the travel map
// while it is open, and the plugin's timings next to the load they were measured under. Fields the plugin adds
// later show under "More", so a growing DTO never hides anything.

import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Banner, EmptyState, useNow } from "../../components";
import { Icon } from "../../icons";
import type { ControlStore, Snapshot } from "../store";
import type { RouteError, RouteState, RouteStep } from "../types";
import { Badge, Button, Card, JsonView, Segmented, Switch, agoShort, fmtNum } from "../ui";

/** The fields this page renders; everything else in the state goes under "More". */
const KNOWN = new Set<string>(["game", "area", "areaName", "mode", "stepIndex", "steps", "target", "label", "goals", "why", "playerGrid", "areaSize", "pathCells", "pathWorldLength", "edgePieces", "timings", "mapPath", "notes", "activeQuests", "questObjective", "travelTo", "travelMapOpen", "travelMapStops", "playerTile", "sightings"]);

export function RoutePage({ store, snap }: { store: ControlStore; snap: Snapshot }) {
  // Polling lives with the page: on while it is mounted (and the Live switch is on), off when the user leaves.
  useEffect(() => { store.setRouteLive(true); return () => store.setRouteLive(false); }, [store]);
  const now = useNow(1000);
  const nav = snap.nav;
  const s = nav.state;
  const hasTool = !snap.catalog || store.hasTool("route_state");
  const noHud = store.gamesUp.length === 0 && snap.gamesAt !== undefined;
  const unreachable = snap.games.find((g) => g.status === "unreachable");
  const step = s && s.stepIndex >= 0 && s.stepIndex < s.steps.length ? s.steps[s.stepIndex] : undefined;
  const extra = useMemo(() => {
    if (!s) return {};
    const o: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(s)) if (!KNOWN.has(k)) o[k] = v;
    return o;
  }, [s]);
  const stale = nav.at !== undefined && now - nav.at > 5000;
  const [mapView, setMapView] = useState<MapView>("area");

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <div className="flex min-w-0 items-center gap-2">
          <Icon name="route" className="size-4 shrink-0 text-fg-3" />
          <h2 className="ds-title truncate">{s?.areaName ?? s?.area ?? "Route"}</h2>
          {s?.area && s.areaName && <Badge tone="neutral" className="font-code normal-case tracking-normal" title="Area id">{s.area}</Badge>}
          {s?.mode && <Badge tone="info" title="The plugin's route mode">{s.mode}</Badge>}
          {s?.game === "poe1" && <Badge tone="neutral">PoE 1</Badge>}
        </div>
        <div className="ml-auto flex flex-wrap items-center gap-2 text-[11px] text-fg-3">
          <span className="flex items-center gap-1.5 tnum" title={nav.at ? `Last read ${new Date(nav.at).toLocaleTimeString()} in ${fmtNum(nav.ms, 0)} ms · ${nav.reads} reads this session` : "Nothing read yet"}>
            <span className={`size-1.5 rounded-full ${nav.live && s && !nav.error && !stale ? "bg-success live-dot" : nav.error ? "bg-danger" : "bg-fg-3"}`} />
            {!snap.game ? "no HUD" : nav.live ? (nav.error ? "retrying every 5 s" : "1 Hz while visible") : "paused"}
            {nav.at !== undefined && <span className="hidden sm:inline">· read {agoShort(now - nav.at)}{nav.ms !== undefined ? ` in ${fmtNum(nav.ms, 0)} ms` : ""}</span>}
          </span>
          <label className="flex items-center gap-1.5 text-fg-2" title="Poll route_state every second while this page is visible">
            <Switch size="sm" checked={nav.live} onChange={(v) => store.setRouteLive(v)} label="Live" disabled={!snap.game || !hasTool} />live
          </label>
          <Button size="sm" icon="sync" busy={nav.loading && !nav.live} disabled={!snap.game || !hasTool} onClick={() => void store.refreshRoute()} title="Read route_state once now">Refresh</Button>
        </div>
      </div>

      {!hasTool && <Banner tone="info" icon="info" title="This server has no route_state tool">The Route page needs an ExileApiMcp build with <code className="font-code">route_state</code> (Tools/RouteTools.cs). Deploy a newer build (mcp_deploy) and reload this page.</Banner>}
      {hasTool && noHud && (unreachable ? (
        <Banner tone="danger" icon="offline" title={`The ${unreachable.game} HUD bridge is unreachable`} action={<Button size="sm" onClick={() => void store.refreshGames()}>Retry</Button>}>
          {unreachable.error ?? "The bridge files exist but the HUD does not answer."} Is the HUD running with "Whats An AI Bridge" enabled? This page checks again every 20 s.
        </Banner>
      ) : (
        <Banner tone="warning" icon="offline" title="No HUD is up">The route plugin runs inside the PoE2 HUD. Start it with "Whats An AI Bridge" and "Whats A Route" enabled; this page notices within 20 s.</Banner>
      ))}
      {hasTool && !noHud && nav.error && <RouteErrorBanner error={nav.error} onRetry={() => void store.refreshRoute()} />}

      {!s ? (
        !nav.error && hasTool && !noHud && (
          <Card>
            <EmptyState icon="route" title={nav.loading ? "Reading the route plugin…" : snap.game ? "Nothing read yet" : "No HUD selected"} className="py-8">
              {snap.game ? <>route_state asks the HUD for WhatsARoute.GetState once a second.</> : <>Pick a HUD in the header.</>}
            </EmptyState>
          </Card>
        )
      ) : (
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)] lg:items-start">
          <div className="flex min-w-0 flex-col gap-3">
            <Objective s={s} step={step} />
            <Card title="Map" icon="mapPin" pad={false}
              right={<span className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[10.5px] text-fg-3">
                {s.areaSize && <span className="tnum" title="The area's pathfinding grid">{s.areaSize[0]} × {s.areaSize[1]} cells</span>}
                {s.pathCells > 0 && <span className="tnum" title={`${s.mapPath.length / 2} map points (every few cells of the path)`}>path {fmtNum(s.pathCells, 0)} cells · {fmtNum(s.pathWorldLength, 0)} units</span>}
                <span className="flex items-center gap-1"><span className="size-2 rounded-full bg-success" />player</span>
                <span className="flex items-center gap-1"><span className="h-0.5 w-3 rounded-full bg-ring" />route</span>
                <span className="flex items-center gap-1"><span className="size-2 rotate-45 bg-p-spike" />goal</span>
                <Segmented size="sm" label="Map view" value={mapView} onChange={setMapView} options={[{ value: "area", label: "Area", title: "The whole pathfinding grid" }, { value: "route", label: "Route", title: "Zoomed to the route and the player" }]} />
              </span>}>
              {s.areaSize && s.areaSize.length === 2 && s.areaSize[0] > 0 && s.areaSize[1] > 0 ? (
                <RouteMap size={s.areaSize as [number, number]} view={mapView} path={s.mapPath} player={s.playerGrid ?? undefined} goalLabel={s.label ?? step?.label ?? undefined} why={s.why ?? undefined} />
              ) : (
                <EmptyState icon="mapPin" title="No terrain grid yet" className="py-6">The plugin builds the grid when an area loads{s.why ? `: ${s.why}` : "."}</EmptyState>
              )}
            </Card>
            <Timings s={s} />
          </div>

          <div className="flex min-w-0 flex-col gap-3">
            <Card title="Steps" icon="list" pad={false} right={<span className="tnum text-[10.5px] text-fg-3">{s.steps.filter((x) => x.done).length} of {s.steps.length} done</span>}>
              {s.steps.length === 0 ? <EmptyState icon="list" title="No steps for this area" className="py-5">The route book has no entry for {s.area ?? "this area"}.</EmptyState> : (
                <ol className="divide-y divide-line">
                  {s.steps.map((x, i) => <StepRow key={i} i={i} step={x} current={i === s.stepIndex} />)}
                </ol>
              )}
            </Card>
            <Card title="Active quests" icon="flag" right={<span className="tnum text-[10.5px] text-fg-3">{s.activeQuests.length}</span>}>
              {s.activeQuests.length === 0 ? <p className="text-[11.5px] text-fg-3">No open quests (IngameUi.UncompletedQuests is empty).</p> : (
                <div className="flex flex-wrap gap-1">
                  {s.activeQuests.map((q) => {
                    const i = q.lastIndexOf(":");
                    const id = i > 0 ? q.slice(0, i) : q, st = i > 0 ? q.slice(i + 1) : "";
                    const mine = !!s.questObjective && s.questObjective.startsWith(id + " ");
                    return <Badge key={q} tone={mine ? "accent" : "neutral"} title={mine ? `${q}: the quest the current objective comes from` : `${q}: QuestId:StateId`} className="font-code normal-case tracking-normal">{id}{st && <span className="text-fg-3">:{st}</span>}</Badge>;
                  })}
                </div>
              )}
              {s.questObjective && <p className="mt-2 text-[11.5px] text-fg-2"><span className="text-fg-3">objective </span>{s.questObjective}</p>}
            </Card>
            {(s.travelMapOpen || s.travelTo || s.travelMapStops.length > 0) && (
              <Card title="Travel map" icon="compass" pad={false} right={s.travelMapOpen ? <Badge tone="success" icon="eye">open</Badge> : <Badge tone="neutral" icon="eyeOff">closed</Badge>}>
                <div className="px-3 py-2 text-[11.5px] text-fg-2">
                  {s.travelTo ? <>Travel to <b className="font-code font-semibold text-fg">{s.travelTo}</b></> : "No travel target"}
                  {s.travelMapOpen && s.travelMapStops.length > 0 && <span className="text-fg-3"> · {s.travelMapStops.length} stops{s.travelMapStops.some((x) => x.chosen) ? ", one chosen" : ", none chosen"}</span>}
                </div>
                {s.travelMapStops.length > 0 && (
                  <table className="w-full border-collapse border-t border-line text-[11px]">
                    <thead><tr className="bg-surface-2 ds-label text-left text-fg-3"><th className="px-3 py-1 font-semibold">#</th><th className="px-2 py-1 font-semibold">Destination</th><th className="px-2 py-1 font-semibold">State</th><th className="px-3 py-1 text-right font-semibold" title="Where the stop is drawn, in screen pixels">Screen</th></tr></thead>
                    <tbody>
                      {s.travelMapStops.map((x) => (
                        <tr key={x.index} className={`border-t border-line ${x.chosen ? "bg-ring/10" : ""}`}>
                          <td className="tnum px-3 py-1 align-top text-fg-3">{x.index}</td>
                          <td className="min-w-0 px-2 py-1"><span className="block font-medium">{x.destName || "–"}</span><span className="block font-code text-[10.5px] text-fg-3">{x.destId}</span></td>
                          <td className="px-2 py-1 align-top"><span className="flex flex-wrap gap-1">{x.chosen && <Badge tone="accent" icon="check">chosen</Badge>}{x.unlocked ? <Badge tone="success">unlocked</Badge> : <Badge tone="neutral" icon="lock">locked</Badge>}</span></td>
                          <td className="tnum whitespace-nowrap px-3 py-1 text-right align-top text-fg-3">{x.x}, {x.y}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </Card>
            )}
            {s.notes.length > 0 && (
              <Card title="Notes" icon="info">
                <ul className="flex flex-col gap-1 text-[11.5px] leading-snug text-fg-2">
                  {s.notes.map((n, i) => <li key={i} className="flex gap-1.5"><span className="mt-[6px] size-1 shrink-0 rounded-full bg-fg-3" />{n}</li>)}
                </ul>
              </Card>
            )}
            {Object.keys(extra).length > 0 && (
              <Card title="More" icon="braces" right={<span className="text-[10.5px] text-fg-3">fields this page has no view for yet</span>}>
                <JsonView value={extra} open={1} />
              </Card>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

/** The route_state error, by code: the plugin not loaded, the bridge down, the method throwing, bad JSON. */
function RouteErrorBanner({ error, onRetry }: { error: RouteError; onRetry: () => void }) {
  const retry = <Button size="sm" onClick={onRetry}>Retry</Button>;
  switch (error.error) {
    case "plugin_not_loaded":
      return <Banner tone="warning" icon="puzzle" title="Whats A Route is not loaded in this HUD" action={retry}>{error.message} Enable the plugin in the HUD menu, or check hud_plugins for a compile error.</Banner>;
    case "unreachable": case "bridge_unavailable":
      return <Banner tone="danger" icon="offline" title="The HUD bridge is unreachable" action={retry}>{error.message}</Banner>;
    case "plugin_threw":
      return <Banner tone="danger" icon="warning" title="The route plugin threw while reporting its state" action={retry}>{error.message}</Banner>;
    case "bridge_too_old":
      return <Banner tone="warning" icon="puzzle" title="The HUD's bridge is too old for this page" action={retry}>{error.message}</Banner>;
    case "route_not_json": case "route_shape":
      return <Banner tone="danger" icon="braces" title="The route plugin's state does not fit this page" action={retry}>{error.message} The server's RouteStateResult (Tools/RouteDtos.cs) mirrors the plugin's RouteStateDto; change both together.</Banner>;
    default:
      return <Banner tone="danger" icon="warning" title="route_state failed" action={retry}>{error.message ?? error.error}</Banner>;
  }
}

function Objective({ s, step }: { s: RouteState; step?: RouteStep }) {
  const tileName = s.playerTile ? s.playerTile.split("/").pop() : undefined;
  return (
    <Card title="Objective" icon="target" right={s.steps.length > 0 && <Badge tone={step ? "accent" : "neutral"}>{step ? `step ${s.stepIndex + 1} of ${s.steps.length}` : "no current step"}</Badge>}>
      <div className="flex flex-col gap-2">
        <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
          <span className={`ds-title ${s.label || step?.label ? "" : "text-fg-3"}`}>{s.label ?? step?.label ?? (s.target ? s.target : "No objective")}</span>
          {step?.optional && <Badge tone="neutral">optional</Badge>}
          {s.target && <code className="font-code text-[11.5px] text-fg-2">{s.target}</code>}
        </div>
        {s.why && (
          <Banner tone="warning" icon="warning" title="No path">{s.why}</Banner>
        )}
        <dl className="grid grid-cols-[5.5rem_minmax(0,1fr)] gap-x-2 gap-y-1 text-[12px]">
          <dt className="text-fg-3">Goals</dt><dd className="tnum">{s.goals} position{s.goals === 1 ? "" : "s"}{s.pathCells > 0 && <span className="text-fg-3"> · path {fmtNum(s.pathCells, 0)} cells, {fmtNum(s.pathWorldLength, 0)} world units</span>}</dd>
          {s.questObjective && <><dt className="text-fg-3">Quest</dt><dd className="min-w-0 break-words">{s.questObjective}</dd></>}
          {s.travelTo && <><dt className="text-fg-3">Travel to</dt><dd className="font-code">{s.travelTo}{s.travelMapOpen ? <span className="font-ui text-fg-3"> · map open</span> : null}</dd></>}
          <dt className="text-fg-3">Player</dt>
          <dd className="min-w-0">
            {s.playerGrid && s.playerGrid.length === 2 ? <span className="tnum">cell {s.playerGrid[0]}, {s.playerGrid[1]}</span> : <span className="text-fg-3">no position</span>}
            {s.areaSize && s.areaSize.length === 2 && <span className="tnum text-fg-3"> of {s.areaSize[0]} × {s.areaSize[1]}</span>}
            {tileName && <span className="block truncate font-code text-[11px] text-fg-3" title={s.playerTile ?? undefined}>{tileName}</span>}
          </dd>
          <dt className="text-fg-3">Sightings</dt><dd className="tnum">{s.sightings} place{s.sightings === 1 ? "" : "s"} recorded in this instance</dd>
        </dl>
      </div>
    </Card>
  );
}

function StepRow({ i, step, current }: { i: number; step: RouteStep; current: boolean }) {
  const state = step.done ? "done" : current ? "now" : "next";
  return (
    <li className={`flex items-start gap-2.5 border-l-[3px] px-3 py-2 ${current ? "border-l-ring bg-surface-2/70" : step.done ? "border-l-success/60" : "border-l-transparent"}`}>
      <span className={`mt-px grid size-5 shrink-0 place-items-center rounded-full text-[10.5px] font-bold tnum ${step.done ? "bg-success/15 text-success" : current ? "bg-ring text-surface" : "border border-line-2 text-fg-3"}`} title={state}>
        {step.done ? <Icon name="check" className="size-3" /> : i + 1}
      </span>
      <span className="min-w-0 flex-1">
        <span className="flex flex-wrap items-center gap-x-2 gap-y-0.5">
          <span className={`text-[12.5px] font-medium ${step.done ? "text-fg-2 line-through decoration-line-2" : ""}`}>{step.label ?? step.target}</span>
          {current && <Badge tone="accent">now</Badge>}
          {step.optional && <Badge tone="neutral">optional</Badge>}
        </span>
        <span className="block truncate font-code text-[11px] text-fg-3" title={step.target}>{step.target}{step.doneFlag ? <span className="font-ui"> · done when {step.doneFlag}</span> : null}</span>
      </span>
    </li>
  );
}

const KNOWN_TIMINGS = new Set<string>(["gridBuildMs", "fieldBuildMs", "fieldCellsReached", "edgeBuildMs", "edgeContours", "tickUs", "renderUs", "lastReplanUs", "resolveUs", "sightingsScanUs", "entities", "places", "goals"]);

/** A timing the plugin added later: its unit read from the name (…Us, …Ms), the label from the camel case. */
function extraTiming(k: string, v: unknown): { label: string; value: string; unit?: string } {
  const m = /^(.*?)(Us|Ms|Count)?$/.exec(k) ?? [k, k];
  const label = (m[1] || k).replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
  const unit = m[2] === "Us" ? "µs" : m[2] === "Ms" ? "ms" : undefined;
  return { label, value: typeof v === "number" ? fmtNum(v, unit === "ms" ? 1 : 0) : String(v), unit };
}

interface TimingRow { label: string; value: number; unit: "µs" | "ms"; digits: number; when: string; under: ReactNode; tone?: "warn" | "bad" }

/** Every timing next to the load it was measured under: what ran, how often, and the counts that scale it (entities,
 *  places, goals, cells, pieces). Per-frame costs in µs, area builds in ms. */
function Timings({ s }: { s: RouteState }) {
  const t = s.timings;
  const extras = Object.entries(t).filter(([k]) => !KNOWN_TIMINGS.has(k) && (typeof t[k] === "number" || typeof t[k] === "string"));
  const cells = s.areaSize && s.areaSize.length === 2 ? s.areaSize[0] * s.areaSize[1] : undefined;
  const reached = cells ? Math.round((t.fieldCellsReached / cells) * 100) : undefined;
  const frameUs = t.tickUs + t.renderUs;
  const frameTone = frameUs > 2000 ? "bad" : frameUs > 500 ? "warn" : undefined;
  const n = (v: number, one: string, many = `${one}s`) => `${fmtNum(v, 0)} ${v === 1 ? one : many}`;
  const entities = n(t.entities, "entity", "entities"), places = n(t.places, "place"), goals = n(t.goals, "goal");
  const perFrame: TimingRow[] = [
    { label: "Tick", value: t.tickUs, unit: "µs", digits: 0, when: "every frame", under: entities, tone: frameTone },
    { label: "Render", value: t.renderUs, unit: "µs", digits: 0, when: "every frame", under: `${n(s.edgePieces, "edge piece")} drawn${s.pathCells > 0 ? `, ${n(s.mapPath.length / 2, "path point")}` : ""}`, tone: frameTone },
    { label: "Last replan", value: t.lastReplanUs, unit: "µs", digits: 0, when: "when the goal or the player's cell moves", under: s.pathCells > 0 ? `${n(s.pathCells, "cell")} of path, ${goals}` : "no path" },
    { label: "Sightings scan", value: t.sightingsScanUs, unit: "µs", digits: 0, when: "2 Hz, smoothed", under: `${entities}, ${places}` },
    { label: "Resolve", value: t.resolveUs, unit: "µs", digits: 0, when: "2 Hz, smoothed", under: `${entities}, ${places}, ${goals}` },
  ];
  const builds: TimingRow[] = [
    { label: "Grid", value: t.gridBuildMs, unit: "ms", digits: 1, when: "once per area", under: s.areaSize && s.areaSize.length === 2 ? `${s.areaSize[0]} × ${s.areaSize[1]} = ${n(cells ?? 0, "cell")}` : "no grid" },
    { label: "Field", value: t.fieldBuildMs, unit: "ms", digits: 1, when: "once per area", under: cells ? `${fmtNum(t.fieldCellsReached, 0)} of ${fmtNum(cells, 0)} cells reached (${reached}%)` : `${n(t.fieldCellsReached, "cell")} reached` },
    { label: "Edges", value: t.edgeBuildMs, unit: "ms", digits: 1, when: "once per area", under: `${n(t.edgeContours, "contour")} → ${n(s.edgePieces, "piece")}` },
  ];
  const row = (r: TimingRow) => (
    <tr key={r.label} className="border-t border-line">
      <td className="whitespace-nowrap px-3 py-1 font-medium">{r.label}</td>
      <td className={`tnum whitespace-nowrap px-2 py-1 text-right font-semibold ${r.tone === "bad" ? "text-danger" : r.tone === "warn" ? "text-warning" : ""}`}>{fmtNum(r.value, r.digits)} <span className="font-normal text-fg-3">{r.unit}</span></td>
      <td className="px-2 py-1 text-fg-3">{r.when}</td>
      <td className="px-3 py-1 text-fg-2">{r.under}</td>
    </tr>
  );
  const head = (label: string) => (
    <tr className="bg-surface-2 ds-label text-left text-fg-3"><th className="px-3 py-1 font-semibold" colSpan={2}>{label}</th><th className="px-2 py-1 font-semibold">When</th><th className="px-3 py-1 font-semibold">Measured under</th></tr>
  );
  return (
    <Card title="Timings" icon="clock" pad={false} right={<span className="text-[10.5px] text-fg-3">under <b className="tnum font-semibold text-fg-2">{t.entities}</b> entities, <b className="tnum font-semibold text-fg-2">{t.places}</b> places{cells ? <>, <b className="tnum font-semibold text-fg-2">{fmtNum(cells, 0)}</b> cells</> : null}</span>}>
      <table className="w-full border-collapse text-[11.5px]">
        <thead>{head("Per frame")}</thead>
        <tbody>{perFrame.map(row)}</tbody>
        <thead>{head("Area builds")}</thead>
        <tbody>{builds.map(row)}</tbody>
        {extras.length > 0 && (
          <>
            <thead>{head("Also reported")}</thead>
            <tbody>
              {extras.map(([k, v]) => { const e = extraTiming(k, v); return (
                <tr key={k} className="border-t border-line">
                  <td className="px-3 py-1 font-medium">{e.label}</td>
                  <td className="tnum whitespace-nowrap px-2 py-1 text-right font-semibold">{e.value} {e.unit && <span className="font-normal text-fg-3">{e.unit}</span>}</td>
                  <td className="px-2 py-1 text-fg-3" colSpan={2}><span className="font-code">timings.{k}</span>: a timing this page has no row for yet</td>
                </tr>
              ); })}
            </tbody>
          </>
        )}
      </table>
      <p className="border-t border-line px-3 py-2 text-[10.5px] leading-snug text-fg-3">Per-frame costs under 500 µs are invisible next to the HUD's ~16 ms frame. The 2 Hz scans and the builds (once per area) show as a hitch when they are slow, not as a lower frame rate.</p>
    </Card>
  );
}

// ── The map ──────────────────────────────────────────────────────

type MapView = "area" | "route";
const MAP_MAX_H = 440;
const MAP_MIN_H = 180;

/** The grid lines' spacing: 4-10 lines across the window. */
function gridStep(span: number): number {
  for (const s of [5, 10, 25, 50, 100, 250, 500, 1000, 2500]) if (span / s <= 10) return s;
  return 5000;
}

/** The area's pathfinding grid as the frame, drawn the way get_map_image draws it: cell x to the right, cell y down
 *  (the in-game map is this grid turned 45°). The route as a polyline, the player as a dot, the goal at the path's end.
 *  "area" shows the whole grid; "route" the route's bounding box (with the player) padded, so a short path in a big
 *  area is still legible. One uniform scale from the card's width, so markers stay pixel-sized. */
function RouteMap({ size, view, path, player, goalLabel, why }: { size: [number, number]; view: MapView; path: number[]; player?: number[]; goalLabel?: string; why?: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const [cw, setCw] = useState(0);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => setCw(Math.floor(entries[0].contentRect.width)));
    ro.observe(el);
    setCw(Math.floor(el.getBoundingClientRect().width));
    return () => ro.disconnect();
  }, []);
  const [W, H] = size;
  const pts = useMemo(() => {
    const out: [number, number][] = [];
    for (let i = 0; i + 1 < path.length; i += 2) out.push([path[i], path[i + 1]]);
    return out;
  }, [path]);
  const goal = pts.length ? pts[pts.length - 1] : undefined;
  const hasPlayer = !!player && player.length === 2;
  // The window in grid cells: the whole grid, or the route and the player with a margin (at least 60 cells across).
  const win = useMemo(() => {
    if (view === "area") return { x: 0, y: 0, w: W, h: H };
    const xs = pts.map((p) => p[0]), ys = pts.map((p) => p[1]);
    if (hasPlayer) { xs.push(player![0]); ys.push(player![1]); }
    if (xs.length === 0) return { x: 0, y: 0, w: W, h: H };
    let x0 = Math.min(...xs), x1 = Math.max(...xs), y0 = Math.min(...ys), y1 = Math.max(...ys);
    const pad = Math.max(30, (x1 - x0) * 0.15, (y1 - y0) * 0.15);
    x0 = Math.max(0, Math.floor(x0 - pad)); y0 = Math.max(0, Math.floor(y0 - pad));
    x1 = Math.min(W, Math.ceil(x1 + pad)); y1 = Math.min(H, Math.ceil(y1 + pad));
    return { x: x0, y: y0, w: Math.max(1, x1 - x0), h: Math.max(1, y1 - y0) };
  }, [view, pts, player, hasPlayer, W, H]);
  const scale = cw > 0 ? Math.min(cw / win.w, MAP_MAX_H / win.h) : 0;
  const w = win.w * scale, h = win.h * scale;
  const boxH = Math.max(MAP_MIN_H, Math.ceil(h));
  const X = (x: number) => (x - win.x) * scale, Y = (y: number) => (y - win.y) * scale;
  const line = pts.map(([x, y]) => `${X(x)},${Y(y)}`).join(" ");
  const step = gridStep(Math.max(win.w, win.h));
  const vlines: number[] = [], hlines: number[] = [];
  for (let x = Math.ceil(win.x / step) * step; x <= win.x + win.w; x += step) if (x > 0 && x < W) vlines.push(x);
  for (let y = Math.ceil(win.y / step) * step; y <= win.y + win.h; y += step) if (y > 0 && y < H) hlines.push(y);
  // The goal label goes to the side of the goal that has room.
  const labelLeft = goal ? X(goal[0]) > w * 0.6 : false;
  const labelBelow = goal ? Y(goal[1]) < 24 : false;
  const mono = { fontFamily: "var(--font-display)", letterSpacing: ".06em" } as const;
  return (
    <div ref={ref} className="relative w-full overflow-hidden" style={{ height: boxH }}>
      {scale > 0 && (
        <svg width={w} height={h} className="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 select-none" role="img"
          aria-label={`Area grid ${W} by ${H} cells, x right and y down, with the route${hasPlayer ? " and the player" : ""}`}>
          {/* The window's background, then the area's edge where it falls inside the window. */}
          <rect x={0} y={0} width={w} height={h} fill="var(--color-surface-2)" />
          <g stroke="var(--color-line)" strokeWidth={1}>
            {vlines.map((x) => <line key={`v${x}`} x1={X(x) + 0.5} x2={X(x) + 0.5} y1={0} y2={h} />)}
            {hlines.map((y) => <line key={`h${y}`} x1={0} x2={w} y1={Y(y) + 0.5} y2={Y(y) + 0.5} />)}
          </g>
          <rect x={X(0) + 0.5} y={Y(0) + 0.5} width={W * scale - 1} height={H * scale - 1} fill="none" stroke="var(--color-line-2)" />
          <text x={4} y={10} className="fill-fg-3" fontSize={9} style={mono}>{win.x},{win.y}</text>
          <text x={w - 4} y={h - 4} textAnchor="end" className="fill-fg-3" fontSize={9} style={mono}>{win.x + win.w},{win.y + win.h}</text>
          <text x={w - 4} y={10} textAnchor="end" className="fill-fg-3" fontSize={9} style={mono}>x → · y ↓ · {step} cells per line</text>
          {pts.length > 1 && (
            <>
              <polyline points={line} fill="none" stroke="var(--color-surface)" strokeWidth={4} strokeLinejoin="round" strokeLinecap="round" opacity={0.7} />
              <polyline points={line} fill="none" stroke="var(--color-ring)" strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
            </>
          )}
          {goal && (
            <g transform={`translate(${X(goal[0])} ${Y(goal[1])})`}>
              <rect x={-5} y={-5} width={10} height={10} transform="rotate(45)" fill="var(--color-p-spike)" stroke="var(--color-surface)" strokeWidth={1.5} />
              {goalLabel && (
                <text x={labelLeft ? -9 : 9} y={labelBelow ? 14 : -8} textAnchor={labelLeft ? "end" : "start"} fontSize={10.5} fontWeight={700} className="fill-fg" style={{ fontFamily: "var(--font-display)", letterSpacing: ".05em", textTransform: "uppercase", paintOrder: "stroke", stroke: "var(--color-surface-2)", strokeWidth: 3, strokeLinejoin: "round" }}>{goalLabel}</text>
              )}
            </g>
          )}
          {hasPlayer && (
            <g transform={`translate(${X(player![0])} ${Y(player![1])})`}>
              <circle r={9} fill="var(--color-success)" opacity={0.18} className="breathe" />
              <circle r={4} fill="var(--color-success)" stroke="var(--color-surface)" strokeWidth={1.5} />
            </g>
          )}
        </svg>
      )}
      {pts.length === 0 && scale > 0 && (
        <div className="pointer-events-none absolute inset-x-0 top-1/2 -translate-y-1/2 px-6 text-center">
          <span className="inline-block max-w-md rounded-sm border border-line bg-surface px-2 py-1 text-[11px] text-fg-2">{why ? "No path (the objective says why)" : "No path right now"}</span>
        </div>
      )}
    </div>
  );
}
