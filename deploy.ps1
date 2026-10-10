<#
.SYNOPSIS
    Deploy an ExileApiMcp build: every supervised server (Supervisor/README.md) switches to it without restarting its session.

.DESCRIPTION
    Builds a commit of this repo into %LOCALAPPDATA%\ExileApiMcp\builds\<version>-<sha7> (with build.json naming it) and
    points %LOCALAPPDATA%\ExileApiMcp\current.json at it. Supervisors poll that file: each starts a worker on the new
    build, sends new calls to it, and lets the old worker finish its own calls (blocking waits included) before it exits.
    Nothing is interrupted, so no coordination is needed; the HUD shows the rollout (bridge session.list mcpRollout).

    The build comes from a clone used only for deploys (%LOCALAPPDATA%\ExileApiMcp\deploy-src), never from a session's
    checkout, so what runs is always a commit on GitHub. Default commit: the one the scaffolding repo's origin/main points
    MCP/ExileApiMcp at (merged and bumped = released). -Ref takes any commit or branch of this repo's origin.

    Exit codes: 0 deployed (or already current), 2 bad ref, 3 build failed, 4 another deploy is running.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File MCP\ExileApiMcp\deploy.ps1 -Who goofy-hertz -Reason "tool error messages"
#>
param(
    [string]$Ref,
    [string]$Who = $env:USERNAME,
    [string]$Reason,
    [switch]$Force   # rebuild and re-point even when that commit is already current
)

$root = Join-Path $env:LOCALAPPDATA 'ExileApiMcp'
$src = Join-Path $root 'deploy-src'
$builds = Join-Path $root 'builds'
$current = Join-Path $root 'current.json'
New-Item -ItemType Directory -Force $builds | Out-Null

# One deploy at a time: a second one waits up to 5 minutes for the lock (a build takes ~30 s).
$lockPath = Join-Path $root 'deploy.lock'
$lock = $null
$deadline = (Get-Date).AddMinutes(5)
while (-not $lock) {
    try { $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
    catch {
        if ((Get-Date) -gt $deadline) { Write-Output "ERROR: another deploy has held $lockPath for 5 minutes"; exit 4 }
        Start-Sleep -Milliseconds 500
    }
}

try {
    $origin = (& git -C $PSScriptRoot remote get-url origin 2>$null)
    if (-not $origin) { Write-Output "ERROR: no git origin for $PSScriptRoot (is this a checkout of the ExileApiMcp repo?)"; exit 2 }
    if (-not (Test-Path (Join-Path $src '.git'))) {
        & git clone -q $origin $src 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Output "ERROR: git clone $origin failed"; exit 2 }
    }
    & git -C $src fetch -q origin 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Output "ERROR: git fetch in $src failed"; exit 2 }

    if (-not $Ref) {
        # The scaffolding repo is two levels up (MCP\ExileApiMcp); its origin/main names the released MCP commit.
        $scaffold = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
        & git -C $scaffold fetch -q origin 2>&1 | Out-Null
        $tree = (& git -C $scaffold ls-tree origin/main MCP/ExileApiMcp 2>$null)
        if (-not $tree) { Write-Output "ERROR: origin/main of $scaffold has no MCP/ExileApiMcp submodule entry: pass -Ref"; exit 2 }
        $Ref = ($tree -split '\s+')[2]
        $refWhy = "scaffolding origin/main"
    } else { $refWhy = "-Ref $Ref" }

    $sha = (& git -C $src rev-parse --verify -q "$Ref^{commit}" 2>$null)
    if (-not $sha) { $sha = (& git -C $src rev-parse --verify -q "origin/$Ref^{commit}" 2>$null) }
    if (-not $sha) { Write-Output "ERROR: '$Ref' is not a commit of $origin (pushed? merged?)"; exit 2 }

    $prev = $null
    if (Test-Path $current) { try { $prev = Get-Content $current -Raw | ConvertFrom-Json } catch { } }
    if ($prev -and $prev.sha -eq $sha -and -not $Force -and (Test-Path (Join-Path $prev.dir 'ExileApiMcp.dll'))) {
        Write-Output "already current: $($prev.version) ($($sha.Substring(0,7))), deployed $($prev.deployedAt) by $($prev.by)"
        exit 0
    }

    & git -C $src -c advice.detachedHead=false checkout -q --force $sha 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Output "ERROR: git checkout $sha in $src failed"; exit 2 }
    $setup = Get-Content (Join-Path $src 'Hosting\McpSetup.cs') -Raw
    $version = if ($setup -match 'Version\s*=\s*"([^"]+)"') { $Matches[1] } else { 'unknown' }
    $short = $sha.Substring(0, 7)
    $dir = Join-Path $builds "$version-$short"

    if (-not (Test-Path (Join-Path $dir 'ExileApiMcp.dll'))) {
        $tmp = "$dir.tmp-$PID"
        if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
        Write-Output "building $version ($short, $refWhy)..."
        $out = & dotnet build (Join-Path $src 'ExileApiMcp.csproj') -c Release -nologo -v q -o $tmp 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Output "ERROR: build failed:"
            $out | Where-Object { $_ -match 'error' } | Select-Object -First 15 | ForEach-Object { Write-Output "  $_" }
            Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
            exit 3
        }
        @{ version = $version; sha = $sha; builtAt = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json | Set-Content (Join-Path $tmp 'build.json') -Encoding ASCII
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
        Move-Item $tmp $dir
    }

    # Point current.json at it atomically: supervisors poll it, and a half-written file must never be read.
    $info = [ordered]@{
        version = $version; sha = $sha; dir = $dir; deployedAt = (Get-Date).ToUniversalTime().ToString('o'); by = $Who; reason = $Reason
        previous = if ($prev) { [ordered]@{ version = $prev.version; sha = $prev.sha } } else { $null }
    }
    $tmpCurrent = "$current.tmp-$PID"
    [IO.File]::WriteAllText($tmpCurrent, ($info | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
    if (Test-Path $current) { [IO.File]::Replace($tmpCurrent, $current, $null) } else { [IO.File]::Move($tmpCurrent, $current) }
    Add-Content -Path (Join-Path $builds 'deploy.log') -Value "$($info.deployedAt) $version $short by $Who$(if ($Reason) { " ($Reason)" })" -Encoding UTF8

    # Keep the newest 4 builds; an older one still in use can't be deleted (its DLL is loaded) and stays until next time.
    Get-ChildItem $builds -Directory | Where-Object { $_.FullName -ne $dir -and $_.Name -notlike '*.tmp-*' } |
        Sort-Object LastWriteTime -Descending | Select-Object -Skip 3 |
        ForEach-Object { Remove-Item -Recurse -Force $_.FullName -ErrorAction SilentlyContinue }

    # Who will follow it: live supervisors (their status files; a dead pid's file is stale and removed).
    $sups = @()
    $supDir = Join-Path $root 'supervisors'
    if (Test-Path $supDir) {
        foreach ($f in Get-ChildItem $supDir -Filter *.json) {
            $alive = $false
            try { $alive = [bool](Get-Process -Id ([int]$f.BaseName) -ErrorAction Stop) } catch { }
            if (-not $alive) { Remove-Item $f.FullName -ErrorAction SilentlyContinue; continue }
            try { $sups += Get-Content $f.FullName -Raw | ConvertFrom-Json } catch { }
        }
    }
    $following = @($sups | Where-Object { $_.mode -eq 'supervised' }).Count
    $pinned = @($sups | Where-Object { $_.mode -eq 'local' }).Count
    Write-Output "deployed $version ($short) by $Who$(if ($Reason) { " ($Reason)" }); was $(if ($prev) { "$($prev.version) ($($prev.sha.Substring(0,7)))" } else { 'nothing' })"
    Write-Output "$following supervised server(s) switch within a few seconds$(if ($pinned) { "; $pinned pinned to their checkout (HEXILE_MCP_LOCAL) stay" }). Servers started without the supervisor need a session restart."
    exit 0
}
finally {
    if ($lock) { $lock.Close() }
}
