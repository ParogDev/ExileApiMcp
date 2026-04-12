# ExileApiMcp

MCP server that exposes live Path of Exile 2 game state as [Model Context Protocol](https://modelcontextprotocol.io/) tools. It connects to the [What's an AI Bridge?](https://github.com/ParogDev/WhatsAnAiBridge) ExileApi plugin over TCP and makes game data queryable from Claude Code, VS Code Copilot, or any MCP-compatible AI client.

> **What does this actually do?** When you're developing ExileApi plugins with an AI assistant, the AI can't see your game. This MCP server gives it eyes -- it can check your character's health, see nearby monsters, inspect UI panels, and explore the full ExileApi object graph in real time. Instead of you copy-pasting game data, the AI queries it directly.

## How It Works

```
Path of Exile 2 (ExileApi HUD)
         |
  [What's an AI Bridge?]   <-- in-game plugin, runs on game thread
    TCP JSON-RPC 2.0 on localhost:50900
         |
  [ExileApiMcp]             <-- this project, standalone console app
    MCP server (stdio transport)
         |
  Claude Code / VS Code / any MCP client
```

1. The **What's an AI Bridge?** plugin runs inside ExileApi and serves game state over a local TCP connection
2. **ExileApiMcp** (this project) connects to that plugin and translates requests into MCP tools
3. Your AI assistant calls those tools to read live game data

The two-process split means you can restart the MCP server without reloading the game, and the game stays responsive because queries are processed under a time budget on the main thread.

## Prerequisites

| Requirement | Details |
|------------|---------|
| **ExileApi HUD** | Installed and running. ExileApi is a third-party overlay framework for Path of Exile 2 |
| **What's an AI Bridge?** | [Plugin](https://github.com/ParogDev/WhatsAnAiBridge) installed in ExileApi's `Plugins/Source/` folder and enabled |
| **.NET 10 SDK** | [Download here](https://dotnet.microsoft.com/download/dotnet/10.0) -- this is currently a preview SDK. Install the SDK (not just the runtime) |
| **An MCP-compatible client** | [Claude Code](https://docs.anthropic.com/en/docs/claude-code), VS Code with Copilot, or any client supporting the [MCP standard](https://modelcontextprotocol.io/) |

## Setup

### Step 1: Verify the plugin is running

Launch ExileApi with Path of Exile 2 running. In the ExileApi plugin list, make sure **What's an AI Bridge?** is enabled. You should see a small status indicator on screen:
- **Green dot** = TCP server is up and idle
- **Yellow dot** = processing a query  
- **Grey dot** = TCP server is disabled (check plugin settings)

The plugin writes two files to its bridge directory (default: `<ExileApi install>/claude-bridge/`):
- `bridge-port.txt` -- the TCP port it's listening on
- `bridge-token.txt` -- a random auth token (regenerated each plugin start)

### Step 2: Clone this repo

```bash
git clone https://github.com/ParogDev/ExileApiMcp.git
```

### Step 3: Test the connection (optional)

You can verify everything works before configuring your AI client:

```bash
cd ExileApiMcp
dotnet run
```

If the plugin is running, you'll see:
```
[ExileApiMcp] Connecting to plugin on 127.0.0.1:50900
[ExileApiMcp] Bridge directory: C:\Users\You\Documents\PoeHelper\claude-bridge
[BridgeClient] Connected to plugin on port 50900
```

If the plugin is **not** running, you'll see connection retries -- this is normal:
```
[BridgeClient] Connection failed: Connection refused, retrying in 1s
[BridgeClient] Connection failed: Connection refused, retrying in 1.5s
```

Press `Ctrl+C` to stop. The MCP server will reconnect automatically when configured as a client tool.

### Step 4: Configure your MCP client

#### Claude Code

Create or edit `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "exileapi": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\ExileApiMcp"],
      "env": {
        "BRIDGE_DIR": "C:\\path\\to\\ExileApi\\claude-bridge"
      }
    }
  }
}
```

Replace the paths:
- `C:\\path\\to\\ExileApiMcp` -- where you cloned this repo
- `C:\\path\\to\\ExileApi\\claude-bridge` -- your ExileApi install's `claude-bridge` folder

Then restart Claude Code. The MCP tools will appear automatically. You can verify with `/mcp` in Claude Code.

#### VS Code (Copilot)

Add to your workspace `.vscode/mcp.json`:

```json
{
  "servers": {
    "exileapi": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\ExileApiMcp"],
      "env": {
        "BRIDGE_DIR": "C:\\path\\to\\ExileApi\\claude-bridge"
      }
    }
  }
}
```

#### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `BRIDGE_DIR` | `~/Documents/PoeHelper/claude-bridge` | Directory containing `bridge-port.txt` and `bridge-token.txt` |
| `BRIDGE_PORT` | `50900` | Fallback port (only used if `bridge-port.txt` doesn't exist) |

## Available Tools

Once connected, your AI assistant can call these tools directly.

### Game State

| Tool | Description |
|------|-------------|
| `get_all` | Combined snapshot: player + area + entities + NPC dialog + map data |
| `get_player` | HP/ES/Mana, position, buffs, skills |
| `get_area` | Zone name, level, act |
| `get_entities` | Nearby entities sorted by distance, with range and type filters |
| `get_npc_dialog` | NPC dialog visibility, lines, lore talk flag |
| `get_map_data` | Map stats, quest flags, dialog depth |
| `get_ui_panels` | Visible UI panels with hierarchical child text |
| `get_stash` | Stash tabs with name, type, color, flags, affinity |
| `get_player_stats` | Full untruncated GameStat dictionary (500+ entries) |
| `deep_scan` | Deep component dump for entities matching a path filter |
| `get_bridge_status` | Connection health: connected clients, pending requests |

### Reflection

These tools let the AI explore the ExileApi object graph without writing plugin code:

| Tool | Description |
|------|-------------|
| `eval_path` | Walk the object graph by dotted path (e.g. `GameController.Player.GetComponent<Life>().CurHP`) |
| `describe_type` | List public properties and methods at a path to discover what data is available |

### Recording

Capture gameplay snapshots for offline analysis (useful for debugging without the game running):

| Tool | Description |
|------|-------------|
| `record_start` | Start recording at a configurable interval (default 200ms) |
| `record_stop` | Stop recording and get stats |
| `record_status` | Check if recording is active |
| `snapshot` | Capture a single frame immediately |
| `recording_list` | List saved `.jsonl` recording files |
| `recording_load` | Load a recording for playback |
| `recording_frame` | Read a specific frame by index |
| `recording_search` | Find frames containing a substring |
| `recording_summary` | Unique entity paths, buff names, frame count, time range |

## Troubleshooting

### Tools hang or time out

The MCP server can't reach the plugin. Check:
- Is ExileApi running with Path of Exile 2?
- Is the **What's an AI Bridge?** plugin enabled? (check ExileApi's plugin list)
- Does `bridge-port.txt` exist in your `BRIDGE_DIR`? If not, the plugin hasn't started its TCP server
- Is another process using port 50900? The plugin will write the actual port to `bridge-port.txt`

### "Not connected to plugin" errors

The MCP server started but lost its connection. This happens when:
- ExileApi was closed or crashed
- The plugin was disabled/reloaded
- The game disconnected

The server will reconnect automatically on the next tool call.

### Token errors / auth failures

The plugin generates a new auth token every time it starts. If you see auth errors:
- The plugin was restarted after the MCP server connected
- The MCP server will re-read the token on reconnection -- just retry the tool call

### MCP server won't start

- Make sure you have the **.NET 10 SDK** (not just the runtime): `dotnet --list-sdks`
- Run `dotnet restore` in the ExileApiMcp directory to fetch NuGet packages
- Check that the path in your MCP config points to the directory containing `ExileApiMcp.csproj`

### Claude Code shows "Failed to reconnect to exileapi"

This usually means the MCP server process crashed or the config path is wrong. Check:
- The `--project` path in your `.mcp.json` is correct
- Run `dotnet build` in the ExileApiMcp directory to check for build errors
- Try `/mcp` in Claude Code to see connection status

## Authentication

The plugin generates a 256-bit random token on each startup, written to `bridge-token.txt`. The MCP server reads this file automatically. Tokens are regenerated every time the plugin starts, so stale tokens are never reused. All communication stays on `127.0.0.1` (localhost only).

## About

Part of the [WhatsA plugin family](https://github.com/ParogDev/WhatsAnAiBridge#whatsa-plugin-family) for ExileApi. Built with AI-assisted development using Claude Code.
