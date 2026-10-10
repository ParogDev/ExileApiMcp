@echo off
rem Launch ExileApiMcp for an MCP client: stdio by default, or pass --http [--port N].
rem
rem The client talks to the supervisor (Supervisor\README.md), which runs the server as a child: the deployed build
rem (%LOCALAPPDATA%\ExileApiMcp\current.json, see deploy.ps1) when there is one, else the build made here. A deploy swaps
rem the server without restarting the session. HEXILE_MCP_LOCAL=1 pins this session to the build made here (MCP
rem development); HEXILE_MCP_UNSUPERVISED=1 runs the server directly, without the supervisor (the old way).
rem
rem Runs a private copy of the build (bin\launch\run-*), so a running server never locks the build
rem output and any number of clients (Claude Code sessions, Claude Desktop, an --http instance) can
rem run while you keep rebuilding. Build output goes to stderr/nul: on stdio, stdout is the protocol.
rem
rem stdio starts the last good build immediately and refreshes the build in the background for the
rem next start: clients such as Claude Desktop probe and restart servers within a second, and a
rem server that is still building misses that window. --http (and the first ever start) build first.
setlocal
set "PROJ=%~dp0"
set "LAUNCH=%PROJ%bin\launch"
set "BUILD=%LAUNCH%\build"
set "SUPPROJ=%PROJ%Supervisor\ExileApiMcp.Supervisor.csproj"

set "FAST="
if exist "%BUILD%\ExileApiMcp.dll" if exist "%BUILD%\supervisor\ExileApiMcp.Supervisor.dll" set "FAST=1"
for %%A in (%*) do if /i "%%A"=="--http" set "FAST="

if defined FAST (
  echo [ExileApiMcp] starting the last build; refreshing it in the background for the next start 1>&2
  start "" /b cmd /c "dotnet build "%PROJ%ExileApiMcp.csproj" -c Debug -nologo -v q -o "%BUILD%" <nul >nul 2>&1 & dotnet build "%SUPPROJ%" -c Debug -nologo -v q -o "%BUILD%\supervisor" <nul >nul 2>&1"
) else (
  dotnet build "%PROJ%ExileApiMcp.csproj" -c Debug -nologo -v q -o "%BUILD%" 1>&2
  if errorlevel 1 echo [ExileApiMcp] build failed - starting the last good build 1>&2
  dotnet build "%SUPPROJ%" -c Debug -nologo -v q -o "%BUILD%\supervisor" 1>&2
  if errorlevel 1 echo [ExileApiMcp] supervisor build failed - starting the last good one 1>&2
)
if not exist "%BUILD%\ExileApiMcp.dll" (
  echo [ExileApiMcp] no build to run 1>&2
  exit /b 1
)

rem Remove copies nobody runs any more: a running supervisor's (or, without one, server's) DLL can't be opened for writing.
for /d %%D in ("%LAUNCH%\run-*") do (
  if exist "%%D\supervisor\ExileApiMcp.Supervisor.dll" (
    2>nul (>>"%%D\supervisor\ExileApiMcp.Supervisor.dll" call ) && 2>nul (>>"%%D\ExileApiMcp.dll" call ) && rd /s /q "%%D" 2>nul
  ) else (
    2>nul (>>"%%D\ExileApiMcp.dll" call ) && rd /s /q "%%D" 2>nul
  )
)

set "RUN=%LAUNCH%\run-%RANDOM%%RANDOM%"
robocopy "%BUILD%" "%RUN%" /e /njh /njs /nfl /ndl /np >nul
rem The scaffolding repo root (tools\restart-hud.ps1 for hud_restart): the private copy runs from bin\launch, so it can't
rem find it by its own location. The cwd is usually the repo (or a worktree of it) too; this is the fallback.
if not defined HEXILE_REPO set "HEXILE_REPO=%PROJ%..\.."
if not "%HEXILE_MCP_UNSUPERVISED%"=="1" if exist "%RUN%\supervisor\ExileApiMcp.Supervisor.dll" (
  dotnet "%RUN%\supervisor\ExileApiMcp.Supervisor.dll" --fallback "%RUN%" %*
  exit /b
)
dotnet "%RUN%\ExileApiMcp.dll" %*
