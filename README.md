# ExileApiMcp

MCP server that exposes live Path of Exile game state as [Model Context Protocol](https://modelcontextprotocol.io/) tools. Connects to the [What's an AI Bridge?](https://github.com/ParogDev/WhatsAnAiBridge) ExileApi plugin over TCP and makes game data available to Claude Code, VS Code Copilot, or any MCP-compatible client.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [What's an AI Bridge?](https://github.com/ParogDev/WhatsAnAiBridge) plugin installed and running in ExileApi

## Quick Start

### 1. Install the plugin

Follow the [What's an AI Bridge?](https://github.com/ParogDev/WhatsAnAiBridge) setup instructions. Once the plugin is running, it writes `bridge-port.txt` and `bridge-token.txt` to its bridge directory (default: `<HUD install>/claude-bridge/`).

### 2. Clone this repo

```bash
git clone https://github.com/ParogDev/ExileApiMcp.git
```

### 3. Configure your MCP client

Add to your MCP client config (e.g. Claude Code `settings.json`, VS Code `mcp.json`):

```json
{
  "mcpServers": {
    "exileapi": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\ExileApiMcp"],
      "env": {
        "BRIDGE_DIR": "C:\\path\\to\\your\\HUD\\claude-bridge"
      }
    }
  }
}
```

#### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `BRIDGE_DIR` | `~/Documents/PoeHelper/claude-bridge` | Directory where the plugin writes `bridge-port.txt` and `bridge-token.txt` |
| `BRIDGE_PORT` | `50900` | Fallback port if `bridge-port.txt` is not found |

The MCP server reads the actual port from `bridge-port.txt` (written by the plugin), so `BRIDGE_PORT` is only used as a fallback.

## Available Tools

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
| `get_bridge_status` | Connected clients, pending requests |

### Reflection

| Tool | Description |
|------|-------------|
| `eval_path` | Walk the ExileApi object graph by dotted path (e.g. `GameController.Player.GetComponent<Life>().CurHP`) |
| `describe_type` | List public properties and methods at a path to discover what's available |

### Recording

| Tool | Description |
|------|-------------|
| `record_start` | Start recording gameplay snapshots at a configurable interval |
| `record_stop` | Stop recording and get stats |
| `record_status` | Check if recording is active |
| `snapshot` | Capture a single frame immediately |
| `recording_list` | List saved `.jsonl` files |
| `recording_load` | Load a recording for playback |
| `recording_frame` | Read a specific frame |
| `recording_search` | Find frames containing a substring |
| `recording_summary` | Unique paths, unique buffs, frame count, time range |

## Architecture

```
Path of Exile (ExileApi HUD)
         |
  [What's an AI Bridge?]   <-- in-game plugin, main thread
    TCP JSON-RPC 2.0 on localhost
         |
  [ExileApiMcp]             <-- this project, standalone process
    MCP server (stdio transport)
         |
  Claude Code / MCP Client
```

The two-process design means you can restart the MCP server without reloading the game, and the plugin stays responsive by processing queries on the game thread under a time budget.

## Authentication

The plugin generates a 256-bit random token on each startup, written to `bridge-token.txt`. The MCP server reads this automatically. If the plugin restarts, the MCP server reconnects and picks up the new token.

## About

Part of the [WhatsA plugin family](https://github.com/ParogDev/WhatsAnAiBridge#whatsa-plugin-family) for ExileApi. Built with AI-assisted development using Claude Code.
