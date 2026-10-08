// Dev-only bridge between the ext-apps basic-host (a browser page) and ExileApiMcp --http.
//
// basic-host's browser code talks to the MCP server cross-origin and can't send our bearer token.
// Rather than loosening the server (no CORS, token required), this proxy runs next to basic-host
// in its container and:
//   - answers CORS for the basic-host page only (http://localhost:8080 / 127.0.0.1:8080);
//   - forwards /mcp to the server on the Windows host (host.docker.internal:<port>), rewriting
//     Host to 127.0.0.1 (the server's Host allowlist) and adding the bearer token, which is
//     passed in as MCP_TOKEN (docker -e by name, never on a command line) and never sent to the browser;
//   - streams responses (SSE) straight through.
import http from "node:http";

const LISTEN = Number(process.env.PROXY_PORT ?? 3001);
const TARGET_HOST = process.env.MCP_TARGET_HOST ?? "host.docker.internal";
const TARGET_PORT = Number(process.env.MCP_TARGET_PORT ?? 50910);
const ALLOWED_ORIGINS = new Set(["http://localhost:8080", "http://127.0.0.1:8080"]);
const token = (process.env.MCP_TOKEN ?? "").trim();
if (token.length < 16) throw new Error("MCP_TOKEN not set: start this container with ../basic-host.ps1");

const cors = (origin) =>
  ALLOWED_ORIGINS.has(origin)
    ? {
        "access-control-allow-origin": origin,
        "access-control-allow-methods": "GET, POST, DELETE, OPTIONS",
        "access-control-expose-headers": "mcp-session-id, mcp-protocol-version, www-authenticate",
        "access-control-max-age": "600",
        vary: "Origin",
      }
    : {};

http
  .createServer((req, res) => {
    const origin = req.headers.origin ?? "";
    if (origin && !ALLOWED_ORIGINS.has(origin)) {
      res.writeHead(403).end("origin not allowed");
      return;
    }
    if (req.method === "OPTIONS") {
      res.writeHead(204, { ...cors(origin), "access-control-allow-headers": req.headers["access-control-request-headers"] ?? "*" }).end();
      return;
    }
    if (!req.url?.startsWith("/mcp")) {
      res.writeHead(404).end();
      return;
    }
    const headers = { ...req.headers, host: `127.0.0.1:${TARGET_PORT}`, authorization: `Bearer ${token}` };
    delete headers.origin;
    delete headers.referer;
    const upstream = http.request({ host: TARGET_HOST, port: TARGET_PORT, path: req.url, method: req.method, headers }, (up) => {
      res.writeHead(up.statusCode ?? 502, { ...up.headers, ...cors(origin) });
      up.pipe(res);
    });
    upstream.on("error", (e) => {
      console.error(`[proxy] ${req.method} ${req.url}: ${e.message}`);
      if (!res.headersSent) res.writeHead(502, cors(origin));
      res.end(`ExileApiMcp --http not reachable on ${TARGET_HOST}:${TARGET_PORT}: ${e.message}`);
    });
    req.pipe(upstream);
    res.on("close", () => upstream.destroy());
  })
  .listen(LISTEN, () => console.log(`[proxy] http://localhost:${LISTEN}/mcp -> ${TARGET_HOST}:${TARGET_PORT}/mcp (bearer added)`));
