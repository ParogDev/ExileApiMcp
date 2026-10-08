<#
.SYNOPSIS
    Run the ext-apps basic-host in a container against ExileApiMcp --http (real HUD data).

.DESCRIPTION
    Opens http://localhost:8080 - pick show_player_stats and Call Tool to open the app.
    Needs the server running in HTTP mode first, e.g.
        dotnet run --project MCP\ExileApiMcp -- --http
    The container's proxy (basic-host\proxy.mjs) adds CORS for the basic-host page and the bearer
    token, so the server keeps its no-CORS, token-required hardening. Ports are published on
    127.0.0.1 only. The token goes in as an environment variable passed by name (not on the docker
    command line); `docker inspect` on this container shows it, like any env var.

    -Stop       stop the container
    -Rebuild    rebuild the image (after changing basic-host\*)

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File MCP\ExileApiMcp\ui-src\basic-host.ps1
#>
param(
    [switch]$Stop,
    [switch]$Rebuild,
    [int]$McpPort = 50910
)

$name = 'exileapi-basic-host'
$image = 'hexile/mcp-basic-host:2.0.1'

& docker rm -f $name *> $null
if ($Stop) { Write-Output 'stopped'; exit 0 }

& docker image inspect $image *> $null
if ($Rebuild -or $LASTEXITCODE -ne 0) {
    & docker build -q -t $image (Join-Path $PSScriptRoot 'basic-host')
    if ($LASTEXITCODE -ne 0) { Write-Output 'ERROR: docker build failed'; exit 1 }
}

$tokenFile = Join-Path $env:LOCALAPPDATA 'ExileApiMcp\http-token.txt'
if ($env:MCP_HTTP_TOKEN) { $env:MCP_TOKEN = $env:MCP_HTTP_TOKEN }
elseif (Test-Path $tokenFile) { $env:MCP_TOKEN = (Get-Content $tokenFile -Raw).Trim() }
else { Write-Output "ERROR: no token at $tokenFile - start the server with --http once first"; exit 1 }

& docker run -d --rm --name $name `
    -p 127.0.0.1:8080:8080 -p 127.0.0.1:8081:8081 -p 127.0.0.1:3001:3001 `
    -e MCP_TOKEN -e "MCP_TARGET_PORT=$McpPort" $image | Out-Null
$code = $LASTEXITCODE
Remove-Item Env:MCP_TOKEN
if ($code -ne 0) { Write-Output 'ERROR: docker run failed'; exit 1 }

Start-Sleep -Seconds 2
& docker logs $name 2>&1 | Select-String 'proxy|server|Error' | ForEach-Object { $_.Line }
Write-Output 'basic-host: http://localhost:8080  (stop: basic-host.ps1 -Stop)'
