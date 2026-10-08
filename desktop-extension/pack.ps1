<#
.SYNOPSIS
    Package ExileApiMcp as a Claude Desktop Extension (.mcpb) - double-click to install in Claude Desktop.

.DESCRIPTION
    Publishes the server (framework-dependent, win-x64; needs the .NET 10 runtime) into a staging folder
    next to manifest.json, then runs the official packer (@anthropic-ai/mcpb, pinned) in a Node
    container - no Node on Windows. Output: desktop-extension\dist\exileapi-<version>.mcpb

    Unlike a claude_desktop_config.json entry, an installed extension is managed by Claude Desktop
    (enable/disable in Settings > Extensions) and survives Desktop rewriting its config. The bundle is a
    snapshot: re-pack and reinstall after changing the server.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File MCP\ExileApiMcp\desktop-extension\pack.ps1
#>
$here = $PSScriptRoot
$proj = Join-Path (Split-Path $here -Parent) 'ExileApiMcp.csproj'
$staging = Join-Path $here 'dist\staging'
$version = ((Get-Content (Join-Path $here 'manifest.json') -Raw) | ConvertFrom-Json).version

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force $staging | Out-Null

# Native exes: check $LASTEXITCODE (CLAUDE.md shell rules).
& dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -nologo -v q -o (Join-Path $staging 'server')
if ($LASTEXITCODE -ne 0) { Write-Output 'ERROR: dotnet publish failed'; exit 1 }
Copy-Item (Join-Path $here 'manifest.json') $staging
if (Test-Path (Join-Path $here 'icon.png')) { Copy-Item (Join-Path $here 'icon.png') $staging }

$out = "exileapi-desktop-$version.mcpb"
& docker run --rm -v "${here}\dist:/work" -w /work -e npm_config_ignore_scripts=true -e npm_config_update_notifier=false `
    node:24.21.0-alpine sh -c "npx -y @anthropic-ai/mcpb@2.1.2 validate staging/manifest.json && npx -y @anthropic-ai/mcpb@2.1.2 pack staging $out"
if ($LASTEXITCODE -ne 0) { Write-Output 'ERROR: mcpb pack failed'; exit 1 }
Remove-Item -Recurse -Force $staging
$size = [math]::Round((Get-Item (Join-Path $here "dist\$out")).Length / 1MB, 1)
Write-Output "OK: desktop-extension\dist\$out ($size MB). Install: double-click it, or Claude Desktop > Settings > Extensions."
