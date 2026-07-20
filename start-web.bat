@echo off
REM ============================================================================
REM  Worklog — start the web app locally.
REM
REM  Opens TWO windows:
REM    1. the API   -> http://localhost:5080   (talks to whatever DbPath names)
REM    2. the web   -> http://localhost:4200   (this is the one you open)
REM
REM  ðŸ”´ WHICH DATABASE? Since M11 the API refuses to start unless
REM     TimesheetApp:DbPath / ConfigPath / KeyRingPath are set, and there is NO
REM     fallback to the old %%APPDATA%% defaults -- that silent fallback WAS the
REM     bug. It is whatever src\TimesheetApp.Api\appsettings.json says, and the
REM     API prints the resolved path in a banner on every start. READ IT.
REM
REM  🔴 OPEN http://localhost:4200 -- NEVER http://localhost:5080 DIRECTLY.
REM     The web server proxies /api and /hubs to the API, so the browser sees
REM     ONE origin. That is the only way the session cookie survives: it is
REM     SameSite=Lax, and a Lax cookie is NOT sent across origins. Open the API
REM     port directly and you will log in successfully and then be logged out
REM     on every single request.
REM
REM  (The old "CLOSE THE WPF APP FIRST" warning is gone with the app itself: M10
REM   deleted src\TimesheetApp\ on 2026-07-19. There is exactly one writer now,
REM   which is the entire reason that migration existed.)
REM ============================================================================

setlocal
cd /d "%~dp0"

echo.
echo  Worklog — starting...
echo.

REM --- one-time: install the web dependencies if they are missing -------------
if not exist "src\timesheet-web\node_modules\@angular\cli" (
    echo  [1/3] Installing web dependencies ^(first run only, ~2 min^)...
    pushd src\timesheet-web
    call npm ci
    popd
    echo.
)

REM --- 1. the API ------------------------------------------------------------
echo  [2/3] Starting the API on http://localhost:5080 ...
start "Worklog API  (do not close)" cmd /k "cd /d "%~dp0src\TimesheetApp.Api" && dotnet run --urls http://localhost:5080"

REM --- 2. the web ------------------------------------------------------------
echo  [3/3] Starting the web app on http://localhost:4200 ...
start "Worklog WEB  (do not close)" cmd /k "cd /d "%~dp0src\timesheet-web" && npm start"

echo.
echo  ---------------------------------------------------------------------
echo   Two windows are starting. Give them ~30 seconds.
echo.
echo   THEN OPEN:   http://localhost:4200
echo.
echo   To stop: close both windows.
echo  ---------------------------------------------------------------------
echo.
pause
