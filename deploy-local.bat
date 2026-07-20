@echo off
REM ============================================================================================
REM  deploy-local.bat  —  single-process local run.
REM
REM  Builds the Angular UI, copies it into the API's wwwroot, and starts the API so that the UI
REM  AND /api are served from ONE process, ONE port, ONE origin. No `ng serve`, no proxy — which
REM  is the whole point: the SameSite=Lax auth cookie is then same-origin and survives, so no more
REM  "opened the wrong port and got logged out".
REM
REM     OPEN:  http://localhost:5080      (NOT 4200 — there is no `ng serve` here.)
REM
REM  WHICH DATABASE? src\TimesheetApp.Api\appsettings.json decides, and it ships a working
REM  default: data\timesheet.db BESIDE THE APP. A fresh clone therefore just runs, with its own
REM  local database, and touches nothing that already exists on this machine.
REM
REM     TO USE A REAL DATABASE, EDIT THAT FILE. Point DbPath at it. An existing database is
REM     OPENED as-is, never replaced. Point KeyRingPath somewhere backed up too.
REM
REM  Deleting a key rather than changing it makes the API refuse to start and name the key. That
REM  is deliberate: before M11 an unset path fell through silently to the desktop app's %%APPDATA%%
REM  location, which named the live company database.
REM
REM  To let OTHER machines on the LAN reach it, change the --urls at the bottom to
REM  http://0.0.0.0:5080  and open Windows Firewall for inbound TCP 5080. That is the deferred
REM  "who hosts it" decision, so this script defaults to localhost only.
REM ============================================================================================
setlocal
cd /d "%~dp0"

echo [1/3] Building the web UI...
pushd src\timesheet-web
if not exist node_modules\@angular\cli ( call npm ci )
call npm run build || (echo BUILD FAILED & popd & pause & exit /b 1)
popd

echo [2/3] Copying the UI into the API's wwwroot...
if not exist src\timesheet-web\dist\worklog\browser\index.html (
  echo ERROR: expected Angular output at src\timesheet-web\dist\worklog\browser was not found.
  pause & exit /b 1
)
rmdir /s /q src\TimesheetApp.Api\wwwroot 2>nul
xcopy /e /i /y src\timesheet-web\dist\worklog\browser src\TimesheetApp.Api\wwwroot >nul

echo [3/3] Starting the API (serves the UI + /api on ONE port)...
echo.
echo   OPEN:  http://localhost:5080
echo.
echo   READ THE BANNER THE API PRINTS. It names the database that actually WON,
echo   every start. Since M11, editing appsettings.json changes which database
echo   opens -- that is the intent, and it is also how you open the wrong one.
echo.
cd src\TimesheetApp.Api
dotnet run -c Release --urls http://localhost:5080
