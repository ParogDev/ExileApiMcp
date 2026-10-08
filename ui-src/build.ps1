<#
.SYNOPSIS
    Build the MCP App UIs in a container and copy the single-file HTML into ..\ui\.

.DESCRIPTION
    No Node/npm on Windows: everything runs in the image from .\Dockerfile, with node_modules in
    a named Docker volume. Output:
      ..\ui\player-stats.html   embedded into ExileApiMcp (commit this)
      ..\ui\data-explorer.html  embedded into ExileApiMcp (commit this)
      ..\ui\memory-view.html    embedded into ExileApiMcp (commit this)
      .\dist\harness.html       dev harness: fake host + fixtures for all apps (not committed; see README.md)

    -Serve     after building, serve dist\ on http://127.0.0.1:5174 (harness: /harness.html?app=stats|explorer|memory).
               Blocks until Ctrl+C; run it in the background from agents.
    -Npm "..." run an npm command instead of building, e.g. -Npm "install -E some-pkg@1.2.3"
               (keeps package-lock.json in sync without npm on the host).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File ui-src\build.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File ui-src\build.ps1 -Serve
#>
param(
    [switch]$Serve,
    [string]$Npm
)

$here = $PSScriptRoot
$image = 'hexile/mcp-ui-build:24.21.0'
$volume = 'exileapi-mcp-ui-node-modules'

# Native exes: check $LASTEXITCODE rather than $ErrorActionPreference='Stop' (CLAUDE.md shell rules).
& docker image inspect $image *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Output "Building image $image ..."
    & docker build -q -t $image $here
    if ($LASTEXITCODE -ne 0) { Write-Output 'ERROR: docker build failed'; exit 1 }
}

$runArgs = @('run', '--rm', '-v', "${here}:/src", '-v', "${volume}:/src/node_modules")

if ($Npm) {
    & docker @runArgs $image sh -c "npm $Npm"
    exit $LASTEXITCODE
}

# npm ci when the lockfile exists (reproducible); npm install only to create it the first time.
$install = if (Test-Path (Join-Path $here 'package-lock.json')) { 'npm ci' } else { 'npm install' }
& docker @runArgs $image sh -c "$install --no-progress >/dev/null && npm run build"
if ($LASTEXITCODE -ne 0) { Write-Output 'ERROR: UI build failed'; exit 1 }

$uiDir = Join-Path (Split-Path $here -Parent) 'ui'
New-Item -ItemType Directory -Force $uiDir | Out-Null
$sizes = foreach ($app in 'player-stats.html', 'data-explorer.html', 'memory-view.html') {
    Copy-Item (Join-Path $here "dist\$app") (Join-Path $uiDir $app) -Force
    "ui\$app ($([math]::Round((Get-Item (Join-Path $uiDir $app)).Length / 1KB)) KB)"
}
Write-Output "OK: $($sizes -join ', '). Rebuild ExileApiMcp to embed them."

if ($Serve) {
    Write-Output 'Serving dist\ on http://127.0.0.1:5174/harness.html (Ctrl+C to stop)'
    # Published on loopback only.
    & docker @runArgs -p 127.0.0.1:5174:5174 $image npm run preview
}
