@echo off
rem Launch ExileApiMcp for an MCP client: stdio by default, or pass --http [--port N].
rem
rem Builds into bin\launch\build, then runs a private copy (bin\launch\run-*). A running server
rem therefore never locks the build output, so any number of clients (Claude Code sessions,
rem Claude Desktop, an --http instance) can run while you keep rebuilding. Build output goes to
rem stderr: on stdio, stdout belongs to the MCP protocol.
setlocal
set "PROJ=%~dp0"
set "LAUNCH=%PROJ%bin\launch"
set "BUILD=%LAUNCH%\build"

dotnet build "%PROJ%ExileApiMcp.csproj" -c Debug -nologo -v q -o "%BUILD%" 1>&2
if errorlevel 1 echo [ExileApiMcp] build failed - starting the last good build 1>&2
if not exist "%BUILD%\ExileApiMcp.dll" (
  echo [ExileApiMcp] no build to run 1>&2
  exit /b 1
)

rem Remove copies whose server has exited: a running server's DLL can't be opened for writing.
for /d %%D in ("%LAUNCH%\run-*") do (
  2>nul (>>"%%D\ExileApiMcp.dll" call ) && rd /s /q "%%D" 2>nul
)

set "RUN=%LAUNCH%\run-%RANDOM%%RANDOM%"
robocopy "%BUILD%" "%RUN%" /e /njh /njs /nfl /ndl /np >nul
dotnet "%RUN%\ExileApiMcp.dll" %*
