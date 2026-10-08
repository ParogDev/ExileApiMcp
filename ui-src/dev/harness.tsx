// Dev harness: a minimal MCP Apps host for the apps, with fake servers (fakeServer.ts, fakeExplore.ts).
// Every UI state is reachable from URL params, so agents can screenshot states deterministically:
//
//   /harness.html?app=stats&theme=dark&width=380&scenario=offline&vars=none&latency=400&display=fullscreen
//   /harness.html?app=explorer&path=GameController.Player&theme=dark&width=380&scenario=flaky
//
// and drive it from the console / javascript tools through window.harness:
//   stats:    harness.hud.pin("cold_damage_resistance_%")   harness.hud.filter("life", "vitals")
//             harness.hud.select("level")                   harness.server.bumpRandomStat("level")
//   explorer: harness.server.bump()  (life drops)           harness.server.drift = false
//   memory:   harness.server.bump()  harness.server.toggleAffinity()  harness.server.quiet = true
//   all:      harness.setScenario("offline")                harness.log() / harness.context() / harness.messages()
//
//   /harness.html?app=memory&path=GameController.IngameState.ServerData.PlayerStashTabs[0]&theme=dark&width=380
//   /harness.html?app=memory&address=0x41137889400            (a raw read, no struct)

import { StrictMode, useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { AppBridge, PostMessageTransport } from "@modelcontextprotocol/ext-apps/app-bridge";
import type { McpUiDisplayMode, McpUiStyles, McpUiTheme } from "@modelcontextprotocol/ext-apps";
import type { CallToolResult } from "@modelcontextprotocol/client";
import { FakeServer, type CallLogEntry } from "./fakeServer";
import { FakeExplorer } from "./fakeExplore";
import { FakeMemory } from "./fakeMemory";
import "../src/styles.css";

type AppName = "stats" | "explorer" | "memory";

interface FakeHost {
  scenario: string;
  latencyMs: number;
  log: CallLogEntry[];
  onChange?: () => void;
  handle(name: string, args: Record<string, unknown>): Promise<CallToolResult>;
  tick(): void;
}

const params = new URLSearchParams(location.search);
const appName: AppName = params.get("app") === "explorer" ? "explorer" : params.get("app") === "memory" ? "memory" : "stats";
const APPS: Record<AppName, { title: string; html: string; scenarios: string[]; initialTool: string; initialArgs: () => Record<string, unknown> }> = {
  stats: { title: "Player stats", html: "./player-stats.html", scenarios: ["live", "offline", "not-in-game", "empty", "flaky"], initialTool: "stats_ui_state", initialArgs: () => ({}) },
  explorer: { title: "Data explorer", html: "./data-explorer.html", scenarios: ["live", "offline", "flaky"], initialTool: "show_data_explorer", initialArgs: () => ({ path: params.get("path") ?? "GameController", game: "poe2" }) },
  memory: {
    title: "Memory view", html: "./memory-view.html", scenarios: ["live", "offline", "flaky", "ghidra-down"], initialTool: "show_memory_view",
    initialArgs: () => {
      const a: Record<string, unknown> = { game: "poe1" };
      if (params.get("address")) a.address = params.get("address");
      else a.path = params.get("path") ?? "GameController.Player.GetComponent<Life>()";
      if (params.get("type")) a.type = params.get("type");
      // Runner hints (the real show_memory_view has no such arguments yet): open a tab, a preset's setup, or a record's summary.
      if (params.get("mode")) a.mode = params.get("mode");
      if (params.get("preset")) a.preset = params.get("preset");
      if (params.get("experiment")) a.experiment = params.get("experiment");
      return a;
    },
  },
};
const APP = APPS[appName];
const server: FakeHost = appName === "explorer" ? new FakeExplorer() : appName === "memory" ? new FakeMemory() : new FakeServer();
server.scenario = params.get("scenario") ?? "live";
server.latencyMs = Number(params.get("latency") ?? (appName === "stats" ? 40 : 60));
// memory runner: user=acts|nothing|host-timeout|never (never = waits until harness.server.act()); run=follow simulates Claude running one.
if (server instanceof FakeMemory) {
  const u = params.get("user");
  if (u === "nothing" || u === "host-timeout" || u === "acts") server.user = u;
  if (u === "never") server.actMs = 10 * 60_000;
  if (params.get("run") === "follow") setTimeout(() => void server.agentRun(), 1500);
}

// Roughly Claude-like host variables; "none" tests the app's own fallbacks.
const CLAUDE_VARS: McpUiStyles = {
  "--color-background-primary": "light-dark(#ffffff, #262624)",
  "--color-background-secondary": "light-dark(#f5f4ef, #30302e)",
  "--color-background-tertiary": "light-dark(#ebe9e1, #3a3a37)",
  "--color-text-primary": "light-dark(#141413, #faf9f5)",
  "--color-text-secondary": "light-dark(#5e5d59, #c2c0b6)",
  "--color-text-tertiary": "light-dark(#87867f, #9a9893)",
  "--color-border-primary": "light-dark(#e8e6dc, #3e3e3a)",
  "--color-border-secondary": "light-dark(#d6d3c8, #4d4c48)",
  "--color-ring-primary": "light-dark(#d97757, #e08a6c)",
  "--font-sans": "ui-sans-serif, system-ui, 'Segoe UI', sans-serif",
  "--border-radius-md": "8px",
} as McpUiStyles;

interface Ctx { text: string; at: number }

function Harness() {
  const frame = useRef<HTMLIFrameElement>(null);
  const bridge = useRef<AppBridge | null>(null);
  const [theme, setTheme] = useState<McpUiTheme>((params.get("theme") as McpUiTheme) ?? "light");
  const [width, setWidth] = useState(params.get("width") ?? "720");
  const [display, setDisplay] = useState<McpUiDisplayMode>((params.get("display") as McpUiDisplayMode) ?? "inline");
  const [height, setHeight] = useState(400);
  const [scenario, setScenario] = useState(server.scenario);
  const [, force] = useState(0);
  const [contexts, setContexts] = useState<Ctx[]>([]);
  const [messages, setMessages] = useState<string[]>([]);
  const simulate = params.get("simulate") !== "0";

  useEffect(() => {
    server.onChange = () => force((n) => n + 1);
    const t = simulate ? setInterval(() => server.tick(), 2000) : undefined;
    return () => clearInterval(t);
  }, [simulate]);

  // Host: AppBridge without an MCP client; tool calls go to the fake server.
  useEffect(() => {
    const iframe = frame.current!;
    let disposed = false;
    (async () => {
      const html = await (await fetch(APP.html)).text();
      const b = new AppBridge(null, { name: "ExileApi dev harness", version: "1.0.0" },
        { openLinks: {}, serverTools: {}, logging: {}, updateModelContext: { text: {} }, message: { text: {} } },
        {
          hostContext: {
            theme, platform: "web", displayMode: display, availableDisplayModes: ["inline", "fullscreen"],
            styles: params.get("vars") === "none" ? undefined : { variables: CLAUDE_VARS },
            containerDimensions: { maxHeight: 6000 },
          },
        });
      b.oncalltool = (p) => server.handle(p.name, (p.arguments ?? {}) as Record<string, unknown>);
      b.onupdatemodelcontext = async (p) => {
        const text = p.content?.map((c) => (c.type === "text" ? c.text : "")).join(" ") ?? "";
        setContexts((c) => [{ text, at: Date.now() }, ...c].slice(0, 20));
        return {};
      };
      b.onmessage = async (p) => {
        setMessages((m) => [p.content.map((c) => (c.type === "text" ? c.text : "")).join(" "), ...m].slice(0, 10));
        return {};
      };
      b.onsizechange = async ({ height: h }) => { if (h) setHeight(h); };
      b.onrequestdisplaymode = async ({ mode }) => { setDisplay(mode); return { mode }; };
      b.oninitialized = () => {
        // Same order as a real host: the input, then the result of the tool that opened the app.
        const args = APP.initialArgs();
        b.sendToolInput({ arguments: args });
        server.handle(APP.initialTool, args).then((r) => b.sendToolResult(r), (e) => b.sendToolCancelled({ reason: String(e) }));
      };
      await b.connect(new PostMessageTransport(iframe.contentWindow!, iframe.contentWindow!));
      if (disposed) return;
      bridge.current = b;
      iframe.srcdoc = html;
    })();
    return () => { disposed = true; void bridge.current?.close(); };
    // The app is loaded once; theme/display changes are pushed below like a real host does.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => { bridge.current?.sendHostContextChange({ theme }); document.documentElement.dataset.theme = theme; }, [theme]);
  useEffect(() => { bridge.current?.sendHostContextChange({ displayMode: display }); }, [display]);
  useEffect(() => { server.scenario = scenario; }, [scenario]);

  useEffect(() => {
    (window as unknown as { harness: unknown }).harness = {
      app: appName, server, hud: server instanceof FakeServer ? server.hud : undefined,
      setScenario: (s: string) => setScenario(s),
      setTheme, setWidth: (w: number | string) => setWidth(String(w)), setDisplay,
      log: () => server.log, context: () => contexts, messages: () => messages,
    };
  }, [contexts, messages]);

  const full = display === "fullscreen";
  const others = (Object.keys(APPS) as AppName[]).filter((a) => a !== appName);
  // bare=1: only the app's iframe, filling the viewport (for pixel-exact screenshots in a narrow pane).
  if (params.get("bare") === "1") {
    return (
      <iframe ref={frame} title={`${APP.title} app`} sandbox="allow-scripts allow-same-origin allow-forms" className="block bg-surface"
        style={{ width: width.endsWith("%") ? width : `${width}px`, height: full ? "100vh" : height, maxWidth: "100%", border: 0 }} />
    );
  }
  return (
    <div className="flex min-h-screen gap-3 bg-surface-2 p-3 text-fg">
      <aside className="flex w-64 shrink-0 flex-col gap-3 text-xs">
        <h1 className="text-sm font-semibold">{APP.title} · dev harness</h1>
        <div className="flex flex-wrap gap-x-2 text-fg-3">
          {others.map((o) => <a key={o} className="underline-offset-2 hover:underline" href={`?${new URLSearchParams({ ...Object.fromEntries(params), app: o })}`}>switch to {APPS[o].title}</a>)}
        </div>
        <Field label="Theme">
          <Seg value={theme} options={["light", "dark"]} onChange={(v) => setTheme(v as McpUiTheme)} />
        </Field>
        <Field label="Width">
          <Seg value={width} options={["380", "560", "720", "100%"]} onChange={setWidth} />
        </Field>
        <Field label="Display">
          <Seg value={display} options={["inline", "fullscreen"]} onChange={(v) => setDisplay(v as McpUiDisplayMode)} />
        </Field>
        <Field label="Scenario">
          <select value={scenario} onChange={(e) => setScenario(e.target.value)} className="w-full rounded border border-line bg-surface px-2 py-1">
            {APP.scenarios.map((s) => <option key={s}>{s}</option>)}
          </select>
        </Field>
        <Field label={`Latency ${server.latencyMs} ms`}>
          <input type="range" min={0} max={1500} step={10} value={server.latencyMs} onChange={(e) => { server.latencyMs = Number(e.target.value); force((n) => n + 1); }} className="w-full" />
        </Field>
        {server instanceof FakeServer && (
          <Field label="Simulate a change in the HUD panel">
            <div className="flex flex-wrap gap-1">
              <Btn onClick={() => server.hud.pin("cold_damage_resistance_%", !server.state.pinnedStatKeys.includes("cold_damage_resistance_%"))}>toggle pin cold res</Btn>
              <Btn onClick={() => server.hud.filter("life")}>filter "life"</Btn>
              <Btn onClick={() => server.hud.filter("", "resistances")}>category res</Btn>
              <Btn onClick={() => server.hud.select("level")}>select level</Btn>
              <Btn onClick={() => server.hud.sort("value", true)}>sort value ↓</Btn>
              <Btn onClick={() => { server.hud.filter("", "all"); server.hud.select(null); server.hud.sort("category", false); }}>reset view</Btn>
              <Btn onClick={() => server.bumpRandomStat("fire_damage_resistance_%")}>change fire res</Btn>
            </div>
          </Field>
        )}
        {server instanceof FakeExplorer && (
          <Field label="Simulate the game">
            <div className="flex flex-wrap gap-1">
              <Btn onClick={() => server.bump()}>take a hit (life −60)</Btn>
              <Btn onClick={() => { server.drift = !server.drift; force((n) => n + 1); }}>{server.drift ? "freeze values" : "let values drift"}</Btn>
            </div>
            <p className="mt-1 text-fg-3">Injected states: Player has a blocked member and a throwing getter; IngameUi has budget-skipped members; Life values drift. Unknown members error like the bridge.</p>
          </Field>
        )}
        {server instanceof FakeMemory && (
          <Field label="Simulate the game">
            <div className="flex flex-wrap gap-1">
              <Btn onClick={() => server.bump()}>take a hit (life −500)</Btn>
              <Btn onClick={() => server.toggleAffinity()}>toggle stash affinity</Btn>
              <Btn onClick={() => { server.drift = !server.drift; force((n) => n + 1); }}>{server.drift ? "freeze values" : "let values drift"}</Btn>
              <Btn onClick={() => { server.quiet = !server.quiet; force((n) => n + 1); }}>{server.quiet ? "watches find changes" : "watches find nothing"}</Btn>
              <Btn onClick={() => { server.scanMs = server.scanMs ? 0 : 4000; force((n) => n + 1); }}>{server.scanMs ? "code scans instant" : "code scans slow (4 s/offset)"}</Btn>
            </div>
            <p className="mt-1 text-fg-3">Life and the stash tab are real captures; any other address reads as a synthesised object. Paths below Life (e.g. .CurHP) fail with no_address, GameController with no_struct, 0x0 with unreadable. Code lookups: stash Flags (+61) and Affinity (+63) are real Ghidra results and pre-cached; other offsets are synthesised and "scan" first. Scenario ghidra-down fails them.</p>
          </Field>
        )}
        {server instanceof FakeMemory && (
          <Field label="Guided experiments (the fake user)">
            <div className="flex flex-wrap gap-1">
              <Btn onClick={() => server.act()}>user acts now</Btn>
              <Btn onClick={() => { server.user = server.user === "acts" ? "nothing" : "acts"; force((n) => n + 1); }}>{server.user === "nothing" ? "user acts (after 2.5 s)" : "user does nothing (fails)"}</Btn>
              <Btn onClick={() => { server.user = server.user === "host-timeout" ? "acts" : "host-timeout"; force((n) => n + 1); }}>{server.user === "host-timeout" ? "host waits" : "host times out"}</Btn>
              <Btn onClick={() => void server.agentRun()}>simulate Claude running one</Btn>
            </div>
            <p className="mt-1 text-fg-3">Records stash-ctrl-click (4 steps) and stash-switch-tab (2) are real PoE1 runs; new steps are synthesised in their shape (the 2nd repeat adds a "sometimes" change). The fake guide card follows waiting → detected → captured / failed. URL: mode=experiments, preset=stash-ctrl-click, experiment=stash-ctrl-click (summary), user=nothing|never|host-timeout, run=follow.</p>
          </Field>
        )}
        {server instanceof FakeServer && (
          <div className="rounded border border-line bg-surface p-2">
            <div className="mb-1 font-semibold text-fg-2">HUD state · rev {server.state.rev}</div>
            <pre className="max-h-40 overflow-auto font-code text-[10px] whitespace-pre-wrap">{JSON.stringify(server.state, null, 1)}</pre>
          </div>
        )}
        <div className="rounded border border-line bg-surface p-2">
          <div className="mb-1 font-semibold text-fg-2">Model context (latest first)</div>
          {contexts.length === 0 ? <p className="text-fg-3">none yet</p> : contexts.slice(0, 4).map((c) => <p key={c.at} className="mb-1 border-b border-line pb-1">{c.text}</p>)}
          {messages.length > 0 && <><div className="mt-2 mb-1 font-semibold text-fg-2">Messages to chat</div>{messages.map((m, i) => <p key={i} className="mb-1">{m}</p>)}</>}
        </div>
      </aside>
      <section className="flex min-w-0 flex-1 flex-col gap-2">
        <div className="text-xs text-fg-3">iframe {width}{width.endsWith("%") ? "" : "px"} × {full ? "fill" : `${height}px (auto-resize)`}</div>
        <iframe
          ref={frame}
          title={`${APP.title} app`}
          sandbox="allow-scripts allow-same-origin allow-forms"
          className="rounded-lg border border-line bg-surface"
          style={{ width: width.endsWith("%") ? width : `${width}px`, height: full ? "calc(100vh - 12rem)" : height, maxWidth: "100%" }}
        />
        <div className="max-h-40 overflow-auto rounded border border-line bg-surface p-2 font-code text-[10px]">
          {server.log.slice(-40).reverse().map((l, i) => (
            <div key={i} className={l.outcome === "ok" ? "text-fg-2" : "text-danger"}>
              {new Date(l.at).toLocaleTimeString()} {l.name} {JSON.stringify(l.args)} · {l.ms} ms · {l.outcome}
            </div>
          ))}
        </div>
      </section>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="flex flex-col gap-1"><span className="text-fg-2">{label}</span>{children}</label>;
}
function Seg({ value, options, onChange }: { value: string; options: string[]; onChange: (v: string) => void }) {
  return (
    <div className="flex overflow-hidden rounded border border-line">
      {options.map((o) => (
        <button key={o} type="button" onClick={() => onChange(o)} className={`flex-1 px-1.5 py-1 ${o === value ? "bg-fg text-surface" : "bg-surface hover:bg-surface-3"}`}>{o}</button>
      ))}
    </div>
  );
}
function Btn({ children, onClick }: { children: React.ReactNode; onClick: () => void }) {
  return <button type="button" onClick={onClick} className="rounded border border-line bg-surface px-1.5 py-0.5 hover:bg-surface-3">{children}</button>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><Harness /></StrictMode>);
