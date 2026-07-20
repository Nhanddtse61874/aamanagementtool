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
REM     OTHERS OPEN:  http://<this-machine's-LAN-IP>:5080
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
REM  🔴 THIS SCRIPT BINDS 0.0.0.0 — IT IS REACHABLE FROM THE WHOLE LOCAL NETWORK (changed
REM  2026-07-20, at the user's request; it used to be localhost-only).
REM
REM  Three things follow from that, and none of them are theoretical:
REM
REM    1. CHANGE THE ADMIN PASSWORD FIRST. A fresh database seeds admin/admin. Until you change
REM       it, anyone on this Wi-Fi can sign in as an administrator -- and an administrator can
REM       delete teams and run the destructive retention prune. Change Password is in the
REM       sidebar under ACCOUNT.
REM    2. THERE IS NO HTTPS. Passwords cross the network in plain text. That is a knowingly
REM       accepted risk on a trusted internal network and nowhere else.
REM    3. WINDOWS FIREWALL MUST ALLOW INBOUND TCP 5080, or nothing outside this machine can
REM       connect and it looks exactly like this change did not work. Once, as Administrator:
REM
REM         New-NetFirewallRule -DisplayName "Worklog 5080" -Direction Inbound ^
REM           -Protocol TCP -LocalPort 5080 -Action Allow -Profile Private
REM
REM  To go back to this-machine-only, put `localhost` back in the --urls at the bottom.
REM  The auth cookie needs no change either way: SecurePolicy is SameAsRequest (AuthSetup.cs),
REM  so it survives plain HTTP. Were it Always, you would log in and be logged straight out.
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
echo   OPEN HERE:      http://localhost:5080
for /f "tokens=2 delims=:" %%I in ('ipconfig ^| findstr /c:"IPv4 Address"') do (
  for /f "tokens=1" %%J in ("%%I") do echo   OTHERS OPEN:    http://%%J:5080
)
echo.
echo   *** THIS IS REACHABLE FROM THE WHOLE LOCAL NETWORK. ***
echo   Change the admin password before anyone else connects -- a fresh database
echo   seeds admin/admin, and an admin can delete teams and prune data. There is
echo   no HTTPS, so passwords cross the network in plain text.
echo   If nobody else can connect, allow inbound TCP 5080 in Windows Firewall.
echo.
echo   READ THE BANNER THE API PRINTS. It names the database that actually WON,
echo   every start. Since M11, editing appsettings.json changes which database
echo   opens -- that is the intent, and it is also how you open the wrong one.
echo.
cd src\TimesheetApp.Api
dotnet run -c Release --urls http://0.0.0.0:5080
