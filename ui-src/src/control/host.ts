// One host interface for both ways the control center runs: as an MCP App inside Claude (ext-apps, tool calls go through
// the host; no subscriptions) and standalone, served by the MCP server at /app (talks MCP over HTTP itself, can listen).
// Every screen is written against this and nothing else.

import type { SettingChangeResult } from "./types";

export interface ToolResult {
  data: unknown;
  isError: boolean;
  text?: string;
  ms: number;
}

export type ToolCall = (name: string, args: Record<string, unknown>, opts?: { signal?: AbortSignal }) => Promise<ToolResult>;

export interface Host {
  mode: "mcp-app" | "standalone";
  callTool: ToolCall;
  /** Standalone only: resources/read. */
  readResource?: (uri: string) => Promise<{ text?: string; json?: unknown }>;
  /** Standalone only: subscriptions/listen. Returns an unsubscribe. `onState` reports the stream's health. */
  listen?: (uris: string[], onUpdate: (uri: string) => void, onState?: (s: ListenState) => void) => () => void;
  /** Standalone only: POST /app/api/settings (permission settings, behind an explicit confirmation). */
  postSettings?: (body: { plugin: string; path: string; value: unknown; game?: string }) => Promise<SettingChangeResult>;
  /** MCP App only. */
  updateModelContext?: (text: string, structured: Record<string, unknown>) => void;
  ask?: (text: string) => void;
  fullscreen?: { active: boolean; toggle: () => void };
  /** Theme control: the host's in MCP-App mode (read-only), ours standalone. */
  theme: "light" | "dark";
  setTheme?: (t: "light" | "dark") => void;
}

export type ListenState = "connecting" | "live" | "reconnecting" | "off";

/** Parse a CallToolResult-like object into our ToolResult (structuredContent first, JSON text as fallback). */
export function parseCallResult(r: { content?: { type: string; text?: string }[]; structuredContent?: unknown; isError?: boolean }, ms: number): ToolResult {
  const text = r.content?.find((c) => c.type === "text")?.text;
  let data: unknown = r.structuredContent;
  if (data === undefined && text) {
    try { data = JSON.parse(text); } catch { /* plain-text result */ }
  }
  return { data, isError: !!r.isError, text, ms };
}

/** The error message of a failed call or an error-shaped result, for people. */
export function errorText(e: unknown): string {
  if (e instanceof Error) return e.message;
  if (typeof e === "string") return e;
  if (e && typeof e === "object") {
    const o = e as Record<string, unknown>;
    if (typeof o.message === "string") return o.message;
    if (typeof o.error === "string") return o.error;
  }
  return String(e);
}

/** Does this result say "bridge unreachable"? (the server's wording, both as a thrown error and as a text result). */
export function isUnreachable(msg: string | undefined): boolean {
  return !!msg && /not reachable|unreachable|connection refused|is the hud running/i.test(msg);
}
