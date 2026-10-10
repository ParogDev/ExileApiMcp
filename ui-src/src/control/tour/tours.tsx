// The tours. Each is data: steps with a target (a data-tour id from ids.ts), a message and, when it needs to, an action
// that puts the app in the right place first (go to a page, type a search). Keep them short: 4-7 steps, one idea each.

import { Kbd } from "../ui";
import type { Tour } from "./engine";
import { T } from "./ids";

export const TOURS: Tour[] = [
  {
    id: "welcome",
    title: "Around the control center",
    summary: "What each page is for, in five steps.",
    steps: [
      { title: "This is the control center", body: <>Everything the Hexile MCP server offers for the HUD, in one place: its tools, every plugin's settings, the observer and the live health of the HUD. Use it between play sessions, where ImGui would get in the way.</> },
      { target: T.nav, title: "The pages", body: <>Overview for a glance, Tools to try any of the server's tools, Settings for every plugin, Observer for what the HUD records while you play. Standalone, the performance, memory, explorer and stats apps are pages too.</>, before: (c) => c.go("overview") },
      { target: T.huds, title: "Which HUDs are up", body: <>One chip per game. A green dot means the bridge answers; the pages act on the selected game. Click a chip to switch when both HUDs run.</> },
      { target: T.livePill, title: "How data arrives", body: <>Standalone, the server pushes changes (subscriptions). Inside Claude, the app holds a call open or polls. The pill says which, and when it last heard something.</> },
      { target: T.showMe, title: "Show me", body: <>Every tour lives here, and next to the controls they explain. Try <Kbd>→</Kbd> and <Kbd>←</Kbd> to move, <Kbd>Esc</Kbd> to leave.</> },
    ],
  },
  {
    id: "try-a-tool",
    title: "Try a tool",
    summary: "Find a tool, fill its form, call it, read the result.",
    steps: [
      { target: T.toolSearch, title: "Search the 95 tools", body: <>Type a word from the name, title or description. <Kbd>/</Kbd> focuses it from anywhere on this page. Let's look for the observer's layers.</>, before: async (c) => { c.go("tools"); await c.wait(50); c.setToolQuery("observe_layers"); } },
      { target: T.toolFamilies, title: "Or browse by family", body: <>Tools are grouped by the server class that declares them: Observe, Memory, Stats, Recording and so on. Click a family to narrow the list.</> },
      { target: T.toolList, title: "Badges say what a call does", body: <><b>read-only</b> never changes anything; <b>writes</b> changes HUD state; <b>destructive</b> asks for a confirmation; <b>typed</b> has a result schema, so the result renders as a form of its own; <b>app</b> opens an MCP App.</>, before: (c) => c.go("tools") },
      { target: T.toolForm, title: "A form from the input schema", body: <>Every argument becomes a field: choices become menus, numbers get their range, <code>game</code> is filled from the selected HUD. Defaults are pre-filled; only what you change is sent.</>, before: async (c) => { c.go("tools", "observe_layers"); await c.wait(100); } },
      { target: T.toolCall, title: "Call it", body: <>Read-only tools call at once. Tools that write ask nothing either, unless they are marked destructive. Held calls (observe_wait, perf_watch) show a running timer and can be stopped.</> },
      { target: T.toolResult, title: "The result, three ways", body: <><b>Result</b> is the structured view (typed tools get their schema's field order), <b>Text</b> is what Claude reads, <b>JSON</b> the raw payload with copy. Every call lands in the history below, so you can re-run or compare.</>, before: async (c) => { c.click(T.toolCall); await c.wait(350); } },
    ],
  },
  {
    id: "add-layer",
    title: "Add an observer layer",
    summary: "Watch any walker path while you play, and read its map.",
    steps: [
      { target: T.observeSwitch, title: "Observation on", body: <>While on, the HUD records its layers, panels opening and closing, area and level changes, and new entity kinds. Read-only, never input. Tell the person playing before turning it on.</>, before: (c) => c.go("observer") },
      { target: T.layersList, title: "Layers are specs, not code", body: <>Each row watches one walker path in one mode: <b>struct</b> diffs the raw bytes, <b>props</b> the scalar properties, <b>dict</b> keys and values, <b>list</b> items added and removed. Pause keeps the spec; remove drops it.</> },
      { target: T.layerAdd, title: "Add one", body: <>Give it an id and a path from <code>GameController</code>, the same paths eval_path and explore_object take. The explorer's "C#" column is a good source.</>, before: (c) => c.click(T.layerAdd) },
      { target: T.layerAddMode, title: "Pick the mode and rate", body: <>Mode decides what a change means. Hz is samples per second; 4 is plenty for state, 10+ for fast-moving lists. List mode can name the key property.</> },
      { target: T.layerAddSubmit, title: "Preflight", body: <>The HUD resolves the path and mode in game first. A broken link comes back by name (the exact segment that failed), shown on the row in red, so a stale path after a patch is never silent.</> },
      { target: T.layerMap, title: "The layer map", body: <>Open a layer's map to see every unit that changed: struct offsets with the HUD's name (or none, when it is unmapped), how often, and the last value. It is where unmapped-but-busy bytes stand out.</>, before: (c) => c.click(T.layerMapOpen) },
    ],
  },
  {
    id: "change-setting",
    title: "Change a setting safely",
    summary: "Pull, change with instant feedback, undo, and what a permission is.",
    steps: [
      { target: T.settingsPull, title: "Pull the current values", body: <>Settings live in the HUD. The page shows when they were last pulled; Pull now re-reads every plugin. Changes you make here apply at once and are saved when the HUD closes.</>, before: async (c) => { c.go("settings"); await c.wait(60); c.click(T.settingsPull); } },
      { target: T.settingsSearch, title: "Find a setting", body: <>Search across every plugin, group and label. Each plugin is a card, its groups are sections.</> },
      { target: T.settingsControl, title: "The right control per kind", body: <>Toggles switch, ranges slide (or take a typed number), lists are menus, colours open a picker, hotkeys show but are set in game. A change is sent as soon as you let go; the value flashes when the HUD confirms it.</> },
      { target: T.settingsUndo, title: "Undo the last change", body: <>After a change, an undo bar waits for 8 seconds. Click it to put the previous value back (it is one more set, so it shows in the HUD too).</> },
      { target: T.settingsPermission, title: "Permissions are different", body: <>Allow C# scripts, HUD instrumentation and plugin reload give agents power, so no MCP tool can change them: an agent must never grant itself more. Standalone, the control center changes them through its own route behind a confirmation that says what it allows. Inside Claude they are read-only.</> },
    ],
  },
  {
    id: "timeline",
    title: "Read the observer timeline",
    summary: "Every lane on one axis: follow, zoom, rule out HUD spikes, open an event.",
    steps: [
      { target: T.tlView, title: "One axis, every lane", body: <>Each row is a lane: the observer's layers first (server, stats, life, buffs, inventories and any you add), then panels (ui), area and level, entity kinds, the HUD's own hiccups and what agents asked. A mark is one event; a taller mark with a number is several in the same pixel.</>, before: async (c) => { c.go("observer", "timeline"); await c.wait(80); }, placement: "below" },
      { target: T.tlLanes, title: "Lanes", body: <>Blue marks are units the HUD maps; hollow ones are unmapped struct offsets, the things worth naming. <b>▲</b> and <b>▼</b> are items added and removed, brackets are panels opening and closing (amber when the HUD has no property for the panel). Click a lane to collapse it.</> },
      { target: T.tlControls, title: "Follow, pause, zoom", body: <>Follow slides the newest events in at the right edge; any drag or wheel pauses it. The wheel zooms around the cursor, shift+wheel pans, the presets pick a window, Fit shows everything in memory. <Kbd>Space</Kbd> pauses, <Kbd>+</Kbd> <Kbd>−</Kbd> zoom.</> },
      { target: T.tlFilters, title: "Rule things out", body: <>Chips hide a kind. <b>Dim in spikes</b> fades layer, ui and entity events that were read inside a HUD frame that ran long (the amber bands): their timing is the HUD's, not the game's. <b>After agent</b> marks the ten seconds after an agent asked the user something: those are the user's actions, not the game's.</> },
      { target: T.tlDetail, title: "Open an event", body: <>Click a mark (or step with <Kbd>←</Kbd> <Kbd>→</Kbd>) for its fields, the window it sits in, and what happened within a second of it, read from the journal on disk across sessions.</>, before: async (c) => { c.click(T.tlSelectLatest); await c.wait(400); }, placement: "above" },
      { target: T.tlAround, title: "Around, companions, series", body: <>Widen the window to ±5 s or ±30 s. For a layer unit, <b>Companions</b> counts what consistently happens at the same moments as its changes (a panel opening 400 ms before, every time), and <b>Series</b> shows its values over time with their shape and relations to other units.</>, placement: "above" },
    ],
  },
  {
    id: "read-timeline",
    title: "Read the performance timeline",
    summary: "Frames, spikes and GC pauses: what the health report shows.",
    steps: [
      { target: T.healthCard, title: "Live HUD health", body: <>Frame rate, frame-time spread and GC pauses from the latest health report. Each report traces the HUD for 3 s, so watching is opt-in.</>, before: (c) => c.go("overview") },
      { target: T.healthWatch, title: "Watch", body: <>Turn it on and reports keep coming: pushed by the server standalone, through held perf_watch calls inside Claude. The sparkline behind the frame rate is the trend across reports.</> },
      { target: T.perfPage, title: "The timeline", body: <>One bar per frame over the 3 s trace. Amber bars are spikes (over 1.25x the expected frame time); the magenta base of a bar is a GC pause that landed in that frame. When every spike has a pause, the verdict says so: the fix is garbage, not plugin work.</>, before: (c) => { if (c.mode === "standalone") c.go("perf"); }, placement: "below" },
      { target: T.perfPage, title: "Where the time goes", body: <>Below the timeline, the average frame is split into plugins, HUD core and the rest, and plugins are ranked by ms or KB per frame. Select one for its Tick / Render split and the Profile and Lint actions.</> },
      { title: "Next steps", body: <>The findings list turns numbers into sentences, and each next step is a tool call you can run from there. Inside Claude, this tour ends on the overview: <code>show_hud_performance</code> opens the full app.</> },
    ],
  },
];
