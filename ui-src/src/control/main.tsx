// Entry for ui://exile/control-center (MCP App inside a host) and for /app (standalone, served by the MCP server).
// Detection: a token in the URL hash or the session means standalone (the launcher opens /app#t=<token>); otherwise, a
// page inside an iframe is an MCP App and connects to its host over postMessage; a page on its own with no token shows
// how to get one.

import { StrictMode, useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import type { App, McpUiHostContext } from "@modelcontextprotocol/ext-apps";
import { useApp, useHostFonts, useHostStyleVariables } from "@modelcontextprotocol/ext-apps/react";
import { ControlCenter } from "./ControlCenter";
import { parseCallResult, type Host } from "./host";
import { HttpMcp, takeToken } from "./http";
import { ControlStore } from "./store";
import { Button } from "./ui";
import "../styles.css";

const APP_INFO = { name: "Hexile control center", version: "1.0.0" };
const THEME_KEY = "hexile.cc.theme";

function McpRoot() {
  const [store, setStore] = useState<ControlStore>();
  const [ctx, setCtx] = useState<McpUiHostContext | undefined>();
  const [theme, setTheme] = useState<"light" | "dark">("light");

  const { app, error } = useApp({
    appInfo: APP_INFO,
    capabilities: {},
    onAppCreated: (a: App) => {
      const host: Host = {
        mode: "mcp-app",
        theme: "light",
        callTool: async (name, args) => {
          const t0 = performance.now();
          const r = await a.callServerTool({ name, arguments: args });
          return parseCallResult(r, Math.round(performance.now() - t0));
        },
      };
      const s = new ControlStore(host);
      setStore(s);
      a.ontoolresult = (r) => {
        let data: unknown = r.structuredContent;
        const text = r.content?.find((c) => c.type === "text")?.text;
        if (data === undefined && text) { try { data = JSON.parse(text); } catch { /* text only */ } }
        s.seedCatalog(data);
      };
      a.onhostcontextchanged = (c) => setCtx((prev) => ({ ...prev, ...c }));
      a.onteardown = async () => { s.stop(); return {}; };
      a.onerror = (e) => console.error("[control-center]", e);
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

  useEffect(() => { if (ctx?.theme === "dark" || ctx?.theme === "light") setTheme(ctx.theme); }, [ctx?.theme]);

  // The host's optional features, refreshed into the store's host object (same identity, so stores keep working).
  useMemo(() => {
    if (!app || !store) return;
    const caps = app.getHostCapabilities();
    const h = store.host;
    h.theme = theme;
    h.updateModelContext = caps?.updateModelContext ? (text, structured) => void app.updateModelContext({ content: [{ type: "text", text }], structuredContent: structured }).catch(() => {}) : undefined;
    h.ask = caps?.message ? (text) => void app.sendMessage({ role: "user", content: [{ type: "text", text }] }).catch(() => {}) : undefined;
    if (ctx?.availableDisplayModes?.includes("fullscreen")) {
      const active = ctx.displayMode === "fullscreen";
      h.fullscreen = { active, toggle: () => void app.requestDisplayMode({ mode: active ? "inline" : "fullscreen" }).catch(() => {}) };
    } else h.fullscreen = undefined;
  }, [app, store, ctx?.availableDisplayModes, ctx?.displayMode, theme]);

  if (error) return <p className="p-3 text-sm text-danger">Could not connect to the host: {error.message}</p>;
  if (!app || !store) return <p className="p-3 text-sm text-fg-3">Connecting…</p>;
  const inset = ctx?.safeAreaInsets;
  return (
    <div style={{ paddingTop: inset?.top, paddingRight: inset?.right, paddingBottom: inset?.bottom, paddingLeft: inset?.left }}>
      <ControlCenter key={theme} store={store} />
    </div>
  );
}

function StandaloneRoot({ token }: { token: string }) {
  const [theme, setThemeState] = useState<"light" | "dark">(() => {
    try { const t = localStorage.getItem(THEME_KEY); if (t === "light" || t === "dark") return t; } catch { /* blocked */ }
    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  });
  useEffect(() => { document.documentElement.dataset.theme = theme; try { localStorage.setItem(THEME_KEY, theme); } catch { /* blocked */ } }, [theme]);
  const store = useMemo(() => {
    const mcp = new HttpMcp(location.origin, token);
    const host: Host = {
      mode: "standalone",
      theme,
      callTool: (n, a, o) => mcp.callTool(n, a, o),
      readResource: (u) => mcp.readResource(u),
      listen: (u, f, s) => mcp.listen(u, f, s),
      postSettings: (b) => mcp.postSettings(b),
      setTheme: setThemeState,
    };
    const s = new ControlStore(host);
    s.routeFromHash();
    return s;
  }, [token]); // eslint-disable-line react-hooks/exhaustive-deps
  store.host.theme = theme;
  useEffect(() => { store.start(); const onHash = () => store.routeFromHash(); window.addEventListener("hashchange", onHash); return () => { window.removeEventListener("hashchange", onHash); store.stop(); }; }, [store]);
  return <ControlCenter key={theme} store={store} />;
}

function NoToken({ onToken }: { onToken: (t: string) => void }) {
  const [t, setT] = useState("");
  return (
    <div className="mx-auto flex max-w-md flex-col gap-3 p-6">
      <h1 className="text-[13px] font-semibold">Hexile control center</h1>
      <p className="text-[12px] leading-relaxed text-fg-2">This page talks to the MCP server with a bearer token, and none arrived. Open it from the launcher (it opens <code className="font-code">/app#t=…</code> with the server's token), or paste the token from <code className="font-code">%LOCALAPPDATA%\ExileApiMcp\http-token.txt</code>.</p>
      <form className="flex gap-2" onSubmit={(e) => { e.preventDefault(); if (t.trim()) onToken(t.trim()); }}>
        <input type="password" value={t} onChange={(e) => setT(e.target.value)} placeholder="token" aria-label="Bearer token" className="h-7 min-w-0 flex-1 rounded-md border border-line bg-surface px-2 font-code text-[12px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" />
        <Button type="submit" tone="primary" size="sm">Use it</Button>
      </form>
    </div>
  );
}

function Root() {
  const [token, setToken] = useState(() => takeToken());
  const inIframe = window.parent !== window;
  if (token) return <StandaloneRoot token={token} />;
  if (inIframe) return <McpRoot />;
  return <NoToken onToken={(t) => { try { sessionStorage.setItem("hexile.cc.token", t); } catch { /* blocked */ } setToken(t); }} />;
}

createRoot(document.getElementById("root")!).render(<StrictMode><Root /></StrictMode>);
