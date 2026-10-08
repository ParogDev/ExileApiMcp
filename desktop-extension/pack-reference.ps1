<#
.SYNOPSIS
    Package the official ext-apps reference MCP App (server-basic-vanillajs) as a Claude Desktop
    extension, for A/B-testing MCP App rendering in Claude Desktop against ExileApiMcp.

.DESCRIPTION
    If the reference app's panel renders in Desktop Chat and ours doesn't, the difference is in our
    server; if neither renders, it's Desktop. Desktop runs node extensions with its bundled Node, so no
    Node is needed on Windows. Built in a Node container with npm install scripts off.
    Output: desktop-extension\dist\ext-apps-reference-<version>.mcpb. Ask a Chat: "what time is it?
    use the get-time tool".

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File MCP\ExileApiMcp\desktop-extension\pack-reference.ps1
#>
param([string]$Version = '2.0.1')

$here = $PSScriptRoot
$stage = Join-Path $here 'dist\reference-staging'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage 'server') | Out-Null

$manifest = @"
{
  "manifest_version": "0.3",
  "name": "ext-apps-reference",
  "display_name": "MCP Apps reference (basic, vanilla JS)",
  "version": "$Version",
  "description": "Official ext-apps example server (get-time + an MCP App), for testing MCP App rendering in Claude Desktop.",
  "author": { "name": "modelcontextprotocol/ext-apps" },
  "server": {
    "type": "node",
    "entry_point": "server/node_modules/@modelcontextprotocol/server-basic-vanillajs/dist/index.js",
    "mcp_config": {
      "command": "node",
      "args": ["`${__dirname}/server/node_modules/@modelcontextprotocol/server-basic-vanillajs/dist/index.js", "--stdio"]
    }
  },
  "compatibility": { "platforms": ["win32", "darwin", "linux"] }
}
"@
[IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), $manifest, (New-Object Text.UTF8Encoding $false))

$out = "ext-apps-reference-$Version.mcpb"
& docker run --rm -v "${here}\dist:/work" -w /work -e npm_config_ignore_scripts=true -e npm_config_update_notifier=false -e npm_config_fund=false -e npm_config_audit=false `
    node:24.21.0-alpine sh -c "cd reference-staging/server && npm init -y >/dev/null && npm install --omit=dev --no-progress @modelcontextprotocol/server-basic-vanillajs@$Version >/dev/null && cd /work && npx -y @anthropic-ai/mcpb@2.1.2 validate reference-staging/manifest.json && npx -y @anthropic-ai/mcpb@2.1.2 pack reference-staging $out"
if ($LASTEXITCODE -ne 0) { Write-Output 'ERROR: packing failed'; exit 1 }
Remove-Item -Recurse -Force $stage
Write-Output "OK: desktop-extension\dist\$out - double-click to install, then ask a Chat to use get-time."
