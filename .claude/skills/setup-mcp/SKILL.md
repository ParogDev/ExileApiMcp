---
name: setup-mcp
description: >
  Set up this ExileApiMcp server so your AI assistant can query live game state.
  Finds the bridge directory, creates .mcp.json, builds, and tells the user to
  restart their AI client.
user_invocable: true
argument_description: Optional path to the ExileApi bridge directory
---

# Setup MCP Server

Automated setup for ExileApiMcp. This skill detects your environment, configures your AI client, and verifies the build -- so you don't have to edit config files by hand.

## Prerequisites

- **What's an AI Bridge?** plugin installed and enabled in ExileApi ([GitHub](https://github.com/ParogDev/WhatsAnAiBridge))
- **.NET 10 SDK** installed (`dotnet --list-sdks` to check)

## Instructions

### Step 1: Locate This Project

The current working directory should contain `ExileApiMcp.csproj`. If not, search for it:
1. Check the current directory and immediate subdirectories
2. If not found, tell the user to run this skill from the ExileApiMcp directory

Store the absolute path to the directory containing `ExileApiMcp.csproj`.

### Step 2: Detect the Bridge Directory

The AI Bridge plugin writes `bridge-port.txt` and `bridge-token.txt` to a bridge directory. Find it by searching in order:

1. If an argument was provided, use that path
2. Check if `BRIDGE_DIR` environment variable is already set
3. Check the default path: `~/Documents/PoeHelper/claude-bridge/`
4. Search common locations: `~/Documents/*/claude-bridge/`

Verify the directory exists and contains `bridge-port.txt` (present when the plugin has been started at least once). If not found, tell the user:
- Make sure ExileApi has been launched at least once with the AI Bridge plugin enabled
- The plugin creates the bridge directory on first run
- They can pass the path directly: `/setup-mcp C:\path\to\claude-bridge`

Store the resolved bridge directory path for later steps.

### Step 3: Build and Verify

```bash
dotnet build "<ExileApiMcp path>"
```

If the build fails:
- Check that .NET 10 SDK is installed: `dotnet --list-sdks`
- If missing, tell the user to install from https://dotnet.microsoft.com/download/dotnet/10.0
- Run `dotnet restore` to fetch NuGet packages and retry

### Step 4: Create .mcp.json

Determine the user's working directory (where they'll use the MCP tools from). This is typically the current working directory.

Check if `.mcp.json` already exists there:
- If it exists and already has an `exileapi` server entry, ask before overwriting
- If it exists with other servers, merge the new entry in
- If it doesn't exist, create it

Use **absolute paths** with double backslashes for Windows:

```json
{
  "mcpServers": {
    "exileapi": {
      "command": "dotnet",
      "args": ["run", "--project", "<absolute path to ExileApiMcp>"],
      "env": {
        "BRIDGE_DIR": "<absolute path to bridge directory>"
      }
    }
  }
}
```

### Step 5: Report

Tell the user:

1. **What was configured:**
   - ExileApiMcp location
   - Bridge directory location
   - `.mcp.json` created/updated at which path

2. **Next steps:**
   - Restart Claude Code (or their AI client) to pick up the new MCP server
   - After restart, run `/mcp` to verify the server appears
   - Launch ExileApi with the game running, then try: "Use `get_bridge_status` to check the connection"

3. **If tools hang after setup:**
   - ExileApi must be in the foreground, or enable **Core > Force Foreground** in ExileApi settings
   - The AI Bridge plugin must be enabled in the plugin list

$ARGUMENTS
