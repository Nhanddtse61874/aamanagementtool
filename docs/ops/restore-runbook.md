# Offline database restore — runbook

**M10 Blocker 2 / P2** (`.planning/M10-BLOCKERS.md`). Read this before you ever run `--restore` against a
real database. It is short on purpose — every step here exists because a shorter version of it was found to
be unsafe by an adversarial review, and the paragraph explaining why is attached to the step.

## What this is, and what it is not

This is an **offline** restore: it is run against a **stopped** API, by hand, by one operator at the
keyboard. It is not a web page, not an admin button, and it does not run while the API is up. Restoring
while the app is running overwrites the live `.db` in place while pooled connections are still open on it —
that corrupts live readers, which is exactly why the in-app Settings screen (WPF) never exposed this either
and why the web API does not have a `/restore` route.

**Read this too:** the tool earns "no other connection in *this* process" by construction — it never starts
Kestrel. It does **not** earn "no *other copy* of the API is running" by construction. That part is on you,
Step 1 below, backed up (not replaced) by an automated hint in Step 4.

## Prerequisites

- You know where the **built** `TimesheetApp.Api.exe` is, or you are prepared to build it (Step 2).
- You have a backup file to restore from (`timesheet_*.db`, produced by the app's own Backup feature). It
  lives in whatever folder `BackupFolderPath` points at — see Step 4 for how to find that folder from the
  live config rather than guessing.
- You are physically at the machine that hosts the API. This procedure does not work over a remote desktop
  session you might disconnect from mid-restore.

## Step 1 — Stop the API, and *confirm* it is stopped

Close the console window running the API (titled `Worklog API (do not close)` if you started it via
`start-web.bat`, or the plain `dotnet run` / `TimesheetApp.Api.exe` window if you started it another way).

Then **confirm the process is actually gone** — closing a window is not always the same thing as the
process exiting. From a separate terminal:

```
tasklist | findstr TimesheetApp.Api
```

If a row comes back, the process is still running — end it (Task Manager, or `taskkill /IM
TimesheetApp.Api.exe /F` if you are certain it is the right one) before continuing.

**Why this step is not optional, and why it is Step 1, not a footnote:** the restore tool's own port probe
(Step 4) is a *heuristic*, not a guarantee. It catches the common case — a window someone forgot about — but
it cannot see every way a process might still hold the database open. This manual check is the actual
safety net; the tool's probe only backs it up.

## Step 2 — Locate (or build) the executable

Do **not** use `dotnet run` for this. `dotnet run` builds first and adds its own argument handling in
front of yours, which makes `--restore <path>` ambiguous with `dotnet run`'s own flags. Use the built
assembly directly.

From `src\TimesheetApp.Api`:

```
dotnet build -c Release
```

This produces `TimesheetApp.Api.exe` at:

```
src\TimesheetApp.Api\bin\Release\net8.0\TimesheetApp.Api.exe
```

(If the API was already built via `deploy-local.bat`, that exe already exists at this same path — you do
not need to rebuild, but rebuilding is harmless and guarantees you are running current code.)

## Step 3 — Run the restore command

From the `bin\Release\net8.0` folder (or with the full path):

```
TimesheetApp.Api.exe --restore "C:\path\to\your\backup\timesheet_20260627093015123.db"
```

Quote the backup path if it contains spaces.

## Step 4 — Read everything it prints, in order, before trusting the result

The tool prints, in this order:

1. **A banner** — `Database : <path>` and `Config : <path>`. This is not decoration. `JsonAppConfig`
   resolves paths **per Windows user**, so if you are logged in as the wrong account, or a config file is
   missing/corrupt, the tool can resolve a *different* database than production and restore into it
   successfully — while the real production database sits untouched and now looks like the restore "didn't
   work." **Stop and double-check this line before reading anything else.** The real database folder is
   whatever this line says — it is deliberately **not** documented as a fixed path here (and it is
   deliberately **not** `%APPDATA%`, which holds only `appsettings.json`, never the database itself).
   `%APPDATA%\TimesheetApp\appsettings.json` is the config; the line above tells you where the `.db` it
   points to actually is.
2. **A missing-database refusal**, if the path from line 1 does not exist. The tool will refuse and exit
   non-zero rather than create a fresh empty database at that path — which is exactly the failure mode a
   wrong path produces if nothing catches it (a brand-new, empty, but perfectly working-looking app).
3. **A port-5080 check.** If it reports the port occupied, it refuses and exits non-zero — go back to
   Step 1. If it reports the port free, it says so explicitly as advisory: a free port is consistent with
   the API being stopped, but is not proof by itself (see the callout in Step 1).
4. **The restore itself**, followed by a **post-restore integrity check** on the live database file. A
   pre-restore safety copy (`<db>.pre-restore_<timestamp>.bak`) is written next to the database
   automatically before anything is overwritten — this is your undo if the wrong backup was chosen.
5. **A final line and an exit code.**

## Step 5 — Act on the exit code

- **Exit code `0`** — printed as `Restore complete and verified (PRAGMA integrity_check = ok)`. Start the
  API normally.
- **Any non-zero exit code** — something failed, and it is described in the last lines printed. **Do not
  start the API.** In particular:
  - If it failed *before* "Restoring '...' over '...'" printed, nothing was touched — the live database is
    exactly as it was.
  - If it failed *after* that line with a post-restore integrity failure, the pre-restore safety copy
    (`*.pre-restore_*.bak`, same folder as the database) is the way back — restore is idempotent, so you
    can run this whole procedure again pointing at that `.bak` file.

There is no scenario in which exit code `0` and a broken database can coexist — that is the entire reason
the post-restore check exists. If you see `0`, trust it.

## What this runbook deliberately does not claim

- It does not claim the port probe guarantees no other process holds the database open. It is a heuristic
  over a single, fixed, well-known port (5080) that this deployment always binds while running — nothing
  more.
- It does not claim "quiescence by construction." The tool guarantees no *second* connection exists *within
  itself* (it never starts Kestrel or a DI container before restoring). It does not and cannot guarantee no
  other **process** touches the database at the same time — that guarantee comes only from Step 1.
