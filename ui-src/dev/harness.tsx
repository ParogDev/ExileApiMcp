// Dev harness: a minimal MCP Apps host for player-stats.html with a fake server (fakeServer.ts).
// Every UI state is reachable from URL params, so agents can screenshot states deterministically:
//
//   /harness.html?theme=dark&width=380&scenario=offline&vars=none&latency=400&display=fullscreen
//
// and drive it from the console / javascript tools through window.harness:
//   harness.hud.pin("cold_damage_resistance_%")   harness.hud.filter("life", "vitals")
//   harness.hud.select("level")                   harness.server.bumpRandomStat("level")
//   harness.setScenario("offline")                harness.log() / harness.context()

import { StrictMode, useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { AppBridge, PostMessageTransport } from "@modelcontextprotocol/ext-apps/app-bridge";
import type { McpUiDisplayMode, McpUiStyles, McpUiTheme } from "@modelcontextprotocol/ext-apps";
import { FakeServer, type ScenarioName } from "./fakeServer";
import "../src/styles.css";

const params = new URLSearchParams(location.search);
const server = new FakeServer();
server.scenario = (params.get("scenario") as ScenarioName) ?? "live";
server.latencyMs = Number(params.get("latency") ?? 40);

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
  const [scenario, setScenario] = useState<ScenarioName>(server.scenario);
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
      const html = await (await fetch("./player-stats.html")).text();
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
        // Same order as a real host: the input, then the result of show_player_stats.
        b.sendToolInput({ arguments: {} });
        server.handle("stats_ui_state", {}).then((r) => b.sendToolResult(r), (e) => b.sendToolCancelled({ reason: String(e) }));
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
      server, hud: server.hud,
      setScenario: (s: ScenarioName) => setScenario(s),
      setTheme, setWidth: (w: number | string) => setWidth(String(w)), setDisplay,
      log: () => server.log, context: () => contexts, messages: () => messages,
    };
  }, [contexts, messages]);

  const full = display === "fullscreen";
  return (
    <div className="flex min-h-screen gap-3 bg-surface-2 p-3 text-fg">
      <aside className="flex w-64 shrink-0 flex-col gap-3 text-xs">
        <h1 className="text-sm font-semibold">Player stats · dev harness</h1>
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
          <select value={scenario} onChange={(e) => setScenario(e.target.value as ScenarioName)} className="w-full rounded border border-line bg-surface px-2 py-1">
            {["live", "offline", "not-in-game", "empty", "flaky"].map((s) => <option key={s}>{s}</option>)}
          </select>
        </Field>
        <Field label={`Latency ${server.latencyMs} ms`}>
          <input type="range" min={0} max={1500} step={10} value={server.latencyMs} onChange={(e) => { server.latencyMs = Number(e.target.value); force((n) => n + 1); }} className="w-full" />
        </Field>
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
        <div className="rounded border border-line bg-surface p-2">
          <div className="mb-1 font-semibold text-fg-2">HUD state · rev {server.state.rev}</div>
          <pre className="max-h-40 overflow-auto font-code text-[10px] whitespace-pre-wrap">{JSON.stringify(server.state, null, 1)}</pre>
        </div>
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
          title="player-stats app"
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
