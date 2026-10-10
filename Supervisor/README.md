# Supervisor: updating the server without restarting sessions

An agent can't restart its own MCP server: the client (Claude Code, Claude Desktop) owns the stdio process, and reconnecting
it is a user action. So the client launches this supervisor (through `run.cmd`), and the supervisor runs the real server as a
child, a "worker". When a new build is deployed, the supervisor starts a worker on it, sends every new call there, and lets
the old worker finish the calls it already has before it exits. That includes an hour-long `observe_wait`. Nothing is cut and
nobody waits, so a deploy needs no coordination between sessions.

## The deployed build

`deploy.ps1` (or the `mcp_deploy` tool) builds a commit into `%LOCALAPPDATA%\ExileApiMcp\builds\<version>-<sha7>\`, writes a
`build.json` naming it, and replaces `%LOCALAPPDATA%\ExileApiMcp\current.json` atomically:

```json
{ "version": "3.60.0", "sha": "<40 hex>", "dir": "...\\builds\\3.60.0-abc1234", "deployedAt": "...", "by": "<session label>", "reason": "...", "previous": { "version": "...", "sha": "..." } }
```

The default commit is the one the scaffolding repo's `origin/main` points `MCP/ExileApiMcp` at: merged and bumped means
released. Builds come from a clone used only for deploys (`%LOCALAPPDATA%\ExileApiMcp\deploy-src`), never from a session's
checkout. The newest four builds are kept, plus any older one still loaded.

Supervisors poll `current.json` every 2 s. Before the first deploy, or if the deployed build doesn't start, a supervisor runs the
build `run.cmd` made in its checkout ("local").

## Modes

| Mode | How | Follows deploys |
|---|---|---|
| supervised | `run.cmd` (default) | yes |
| local | `HEXILE_MCP_LOCAL=1`: pinned to the checkout's own build, for MCP development | no |
| unsupervised | `HEXILE_MCP_UNSUPERVISED=1`, or a session started before the supervisor existed | no: restart the session |

## stdio routing

Newline-delimited JSON-RPC.
- **Client requests** go to the active worker. They're queued during a swap or a restart, and their ids are remembered as owed.
- **Server-to-client requests** get their ids rewritten (`sup<n>`), so two workers can't collide.
- **Client notifications** go to every live worker, except `notifications/cancelled`, which goes only to the worker that owns
  that request.
- **A new worker** gets the client's `initialize` and `notifications/initialized` replayed; its answer stays in the supervisor.
  A stateless client (MCP 2026-07-28, no initialize) needs nothing replayed; a `ping` proves the worker answers.
- **After a swap**, the client gets `notifications/tools/list_changed` (and the prompts/resources ones when the server
  declares them).
- **A worker that dies** fails the calls it owed with its exit code and last log line, and another worker is started. Three
  exits in a minute stop the restarts, and calls get the reason.

## http

The supervisor owns the public port (50910) and pipes each TCP connection to the active worker's private loopback port; the
worker still does the Host/Origin/token checks. On a swap the old worker gets `drain` on stdin. It then stops accepting,
finishes running requests (shutdown timeout 65 min) and exits.

## Identity and status

- **The same session to the HUD:** workers get `HEXILE_SESSION_PID` (the supervisor's pid), so a swap stays the same
  session. Workers say hello to the HUD bridges at start, with their build in `session.hello` `mcp`.
- **Status file:** each supervisor writes `%LOCALAPPDATA%\ExileApiMcp\supervisors\<pid>.json` with its workers, their builds
  and states (active / draining), and the last swap (ok or the error).
- **Readers:** `mcp_status` and `deploy.ps1` read the status files.

## Keep it small

The supervisor itself only changes with a session restart. It has no package references, and server logic doesn't belong
here.
