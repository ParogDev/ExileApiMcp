import { StrictMode, useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import type { App, McpUiHostContext } from "@modelcontextprotocol/ext-apps";
import { useApp, useHostFonts, useHostStyleVariables } from "@modelcontextprotocol/ext-apps/react";
import { Perf, type HostApi } from "./Perf";
import { PerfStore, type CallTool } from "./store";
import "../styles.css";

// MCP App entry for ui://exile/hud-performance: connects to the host over postMessage, seeds the first report from
// show_hud_performance's result, and refreshes / runs the next steps (profile_plugin, hud_plugin_lint,
// overlay_accuracy) through the host.

const APP_INFO = { name: "ExileApi HUD performance", version: "1.0.0" };

function toolCaller(app: App): CallTool {
  return async (name, args) => {
    const r = await app.callServerTool({ name, arguments: args });
    const text = r.content?.find((c) => c.type === "text")?.text;
    let data: unknown = r.structuredContent;
    if (data === undefined && text) {
      try { data = JSON.parse(text); } catch { /* plain-text result */ }
    }
    return { data, isError: !!r.isError, text };
  };
}

function Root() {
  const [store, setStore] = useState<PerfStore>();
  const [ctx, setCtx] = useState<McpUiHostContext | undefined>();

  const { app, error } = useApp({
    appInfo: APP_INFO,
    capabilities: {},
    onAppCreated: (a) => {
      const s = new PerfStore(toolCaller(a));
      setStore(s);
      a.ontoolinput = (p) => s.setToolInput(p.arguments as Record<string, unknown> | undefined);
      a.ontoolresult = (r) => {
        let data: unknown = r.structuredContent;
        const text = r.content?.find((c) => c.type === "text")?.text;
        if (data === undefined && text) { try { data = JSON.parse(text); } catch { /* text only */ } }
        s.seed(data, text);
      };
      a.ontoolcancelled = () => s.start();
      a.onhostcontextchanged = (c) => setCtx((prev) => ({ ...prev, ...c }));
      a.onteardown = async () => { s.stop(); return {}; };
      a.onerror = (e) => console.error("[hud-performance]", e);
    },
  });

  useHostStyleVariables(app, app?.getHostContext());
  useHostFonts(app, app?.getHostContext());

  useEffect(() => {
    if (!app || !store) return;
    setCtx(app.getHostContext());
    store.start();
    return () => store.stop();
  }, [app, store]);

  const host = useMemo<HostApi>(() => {
    if (!app) return {};
    const caps = app.getHostCapabilities();
    const api: HostApi = {};
    if (caps?.updateModelContext) {
      api.updateModelContext = (text, structured) =>
        void app.updateModelContext({ content: [{ type: "text", text }], structuredContent: structured }).catch(() => {});
    }
    if (caps?.message) {
      api.ask = (text) => void app.sendMessage({ role: "user", content: [{ type: "text", text }] }).catch(() => {});
    }
    if (ctx?.availableDisplayModes?.includes("fullscreen")) {
      const active = ctx.displayMode === "fullscreen";
      api.fullscreen = { active, toggle: () => void app.requestDisplayMode({ mode: active ? "inline" : "fullscreen" }).catch(() => {}) };
    }
    return api;
  }, [app, ctx?.availableDisplayModes, ctx?.displayMode]);

  if (error) return <p className="p-3 text-sm text-danger">Could not connect to the host: {error.message}</p>;
  if (!app || !store) return <p className="p-3 text-sm text-fg-3">Connecting…</p>;

  const inset = ctx?.safeAreaInsets;
  return (
    <div style={{ paddingTop: inset?.top, paddingRight: inset?.right, paddingBottom: inset?.bottom, paddingLeft: inset?.left }}>
      <Perf store={store} host={host} />
    </div>
  );
}

createRoot(document.getElementById("root")!).render(<StrictMode><Root /></StrictMode>);
