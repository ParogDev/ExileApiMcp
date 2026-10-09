// Standalone transport: MCP over the server's local HTTP endpoint (stateless Streamable HTTP, 2026-07-28), the way
// tools/mcp-call.ps1 does it. tools/call and resources/read are one POST each (the answer is JSON or an SSE stream whose
// last data: event is the response); subscriptions/listen is a held SSE stream carrying notifications.

import { parseCallResult, type ListenState, type ToolResult } from "./host";
import type { SettingChangeResult } from "./types";

const PROTOCOL = "2026-07-28";
const CLIENT = { name: "hexile-control-center", version: "1" };

interface JsonRpcResponse { jsonrpc: "2.0"; id?: number | string | null; result?: unknown; error?: { code: number; message: string; data?: unknown }; method?: string; params?: unknown }

export class HttpMcp {
  private seq = 1;
  constructor(private readonly base: string, private readonly token: string) {}

  private headers(method: string, name?: string): Record<string, string> {
    const h: Record<string, string> = {
      Authorization: `Bearer ${this.token}`,
      "Content-Type": "application/json",
      Accept: "application/json, text/event-stream",
      "MCP-Protocol-Version": PROTOCOL,
      "Mcp-Method": method,
    };
    if (name) h["Mcp-Name"] = name;
    return h;
  }

  private body(method: string, params: Record<string, unknown>): string {
    return JSON.stringify({
      jsonrpc: "2.0", id: this.seq++, method,
      params: {
        ...params,
        _meta: {
          "io.modelcontextprotocol/protocolVersion": PROTOCOL,
          "io.modelcontextprotocol/clientInfo": CLIENT,
          "io.modelcontextprotocol/clientCapabilities": {},
        },
      },
    });
  }

  /** One request → one response (JSON, or the last data: event of an SSE answer). */
  private async rpc(method: string, params: Record<string, unknown>, name?: string, signal?: AbortSignal): Promise<unknown> {
    const res = await fetch(`${this.base}/mcp`, { method: "POST", headers: this.headers(method, name), body: this.body(method, params), signal });
    if (res.status === 401) throw new Error("The server rejected the token (401). Open the control center again from the launcher so it gets a fresh one.");
    if (!res.ok) throw new Error(`HTTP ${res.status} ${res.statusText}${await res.text().then((t) => (t ? `: ${t.slice(0, 200)}` : "")).catch(() => "")}`);
    const ct = res.headers.get("content-type") ?? "";
    let msg: JsonRpcResponse;
    if (ct.startsWith("text/event-stream")) {
      const text = await res.text();
      const last = text.split("\n").filter((l) => l.startsWith("data:")).pop();
      if (!last) throw new Error("Empty event stream");
      msg = JSON.parse(last.slice(5).trim());
    } else {
      msg = await res.json();
    }
    if (msg.error) throw new Error(msg.error.message);
    return msg.result;
  }

  async callTool(name: string, args: Record<string, unknown>, opts?: { signal?: AbortSignal }): Promise<ToolResult> {
    const t0 = performance.now();
    const r = (await this.rpc("tools/call", { name, arguments: args }, name, opts?.signal)) as Parameters<typeof parseCallResult>[0];
    return parseCallResult(r, Math.round(performance.now() - t0));
  }

  async readResource(uri: string): Promise<{ text?: string; json?: unknown }> {
    const r = (await this.rpc("resources/read", { uri }, uri)) as { contents?: { uri: string; mimeType?: string; text?: string }[] };
    const text = r.contents?.find((c) => c.text !== undefined)?.text;
    let json: unknown;
    if (text) { try { json = JSON.parse(text); } catch { /* not JSON */ } }
    return { text, json };
  }

  /**
   * subscriptions/listen: a held SSE stream. First notifications/subscriptions/acknowledged (the URIs honoured), then
   * notifications/resources/updated {uri} per change. The server coalesces bursts; we re-read on each. Reconnects with
   * backoff (1 s → 30 s) until unsubscribed.
   */
  listen(uris: string[], onUpdate: (uri: string) => void, onState?: (s: ListenState) => void): () => void {
    let stopped = false;
    let ctrl: AbortController | undefined;
    let backoff = 1000;
    const run = async () => {
      while (!stopped) {
        ctrl = new AbortController();
        onState?.(backoff === 1000 ? "connecting" : "reconnecting");
        try {
          const res = await fetch(`${this.base}/mcp`, {
            method: "POST", headers: this.headers("subscriptions/listen"), signal: ctrl.signal,
            body: this.body("subscriptions/listen", { notifications: { resourceSubscriptions: uris } }),
          });
          if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`);
          const reader = res.body.getReader();
          const dec = new TextDecoder();
          let buf = "";
          for (;;) {
            const { value, done } = await reader.read();
            if (done) break;
            buf += dec.decode(value, { stream: true });
            let i: number;
            while ((i = buf.indexOf("\n\n")) >= 0) {
              const chunk = buf.slice(0, i); buf = buf.slice(i + 2);
              const data = chunk.split("\n").filter((l) => l.startsWith("data:")).map((l) => l.slice(5).trim()).join("\n");
              if (!data) continue;
              let msg: JsonRpcResponse;
              try { msg = JSON.parse(data); } catch { continue; }
              if (msg.method === "notifications/subscriptions/acknowledged") { backoff = 1000; onState?.("live"); }
              else if (msg.method === "notifications/resources/updated") {
                const uri = (msg.params as { uri?: string } | undefined)?.uri;
                if (uri) onUpdate(uri);
              } else if (msg.result !== undefined || msg.error) {
                // The listen request itself completed (server closing the stream): reconnect.
                break;
              }
            }
          }
        } catch (e) {
          if (stopped || (e instanceof DOMException && e.name === "AbortError")) break;
        }
        if (stopped) break;
        onState?.("reconnecting");
        await new Promise((r) => setTimeout(r, backoff));
        backoff = Math.min(30_000, backoff * 2);
      }
      onState?.("off");
    };
    void run();
    return () => { stopped = true; ctrl?.abort(); };
  }

  /** Permission settings go through the control center's own route, never a tool: the server confirms it is a person's page. */
  async postSettings(body: { plugin: string; path: string; value: unknown; game?: string }): Promise<SettingChangeResult> {
    const res = await fetch(`${this.base}/app/api/settings`, {
      method: "POST", headers: { Authorization: `Bearer ${this.token}`, "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify(body),
    });
    const text = await res.text();
    let json: unknown;
    try { json = JSON.parse(text); } catch { /* not JSON */ }
    if (!res.ok) {
      const msg = json && typeof json === "object" && typeof (json as { message?: unknown }).message === "string" ? (json as { message: string }).message : text || res.statusText;
      throw new Error(`${res.status}: ${msg}`);
    }
    // The route answers like hud_settings_set: a SettingChangeResult, or a CallToolResult carrying one.
    const o = json as { structuredContent?: unknown; setting?: unknown } | undefined;
    return (o?.structuredContent ?? o) as SettingChangeResult;
  }
}

const TOKEN_KEY = "hexile.cc.token";

/** The launcher opens /app#t=<token>: keep it for the session and take it out of the URL. */
export function takeToken(): string | undefined {
  const m = /(?:^#|[#&])t=([^&]+)/.exec(location.hash);
  if (m) {
    const token = decodeURIComponent(m[1]);
    try { sessionStorage.setItem(TOKEN_KEY, token); } catch { /* storage blocked */ }
    const rest = location.hash.replace(/(?:^#|[#&])t=[^&]+/, "").replace(/^#&/, "#");
    history.replaceState(null, "", location.pathname + location.search + (rest.length > 1 ? rest : ""));
    return token;
  }
  try { return sessionStorage.getItem(TOKEN_KEY) ?? undefined; } catch { return undefined; }
}
