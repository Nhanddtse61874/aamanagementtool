# B8 — Backup (two separate services) — Adversarial Refutation

Read-only audit. No build, no run, no DB access. Every verdict cites file:line on both sides.

| Feature | Original | Refuted? | Final | Evidence |
|---|---|---|---|---|
| BackupService: manual "Backup now" trigger (user click) | COVERED [VERIFIED] | **YES** | **PARTIAL** | Trigger wired: `SettingsEndpoints.cs:1051-1059` → `settings.component.ts:544-555` → `settings.component.html:359`. But the **failure signal is inverted**: WPF `SettingsViewModel.cs:276-278` distinguishes `null` ("No backup made — choose a folder and make sure the database file exists.") from a written path; web `settings.component.ts:550-551` renders `Backup written to ${r.value ?? 'the configured folder'}` + toast `'Backup complete'` — i.e. **reports success when no file was written**. `SettingsEndpoints.cs:1159` `SettingsOpsResult(string? Value)` is nullable and `:1054` returns `Results.Ok(...)` unconditionally, so `null` reaches the browser as a 200. |

## Why the claim does not survive

### 1. The trigger itself is genuinely wired (this half of the claim holds)

Traced end to end, no gaps:

- Route: `src/TimesheetApp.Api/Endpoints/SettingsEndpoints.cs:1051-1059` — `POST /api/ops/backup/run`, calls `IBackupService.BackupNowAsync()`.
- Gate: `.RequireAuthorization(AuthSetup.AdminPolicy)` (`:1056`) **plus** an in-handler `if (!ctx.IsAdmin) return 403` (`:1053`).
- DTO: `SettingsEndpoints.cs:1159` → generated `src/timesheet-web/src/app/api/models/settings-ops-result.ts:4`.
- Generated fn: `src/timesheet-web/src/app/api/fn/ops/ops-backup-run.ts:15-30`, `PATH = '/api/ops/backup/run'`.
- Service: `src/timesheet-web/src/app/services/worklog.service.ts:1195-1197`.
- Component: `src/timesheet-web/src/app/pages/settings/settings.component.ts:544-555`.
- Template: `src/timesheet-web/src/app/pages/settings/settings.component.html:359` (inside `@if (tab() === 'ops')`, `:337`).
- Route gate: `src/timesheet-web/src/app/app.routes.ts:53` — `adminGuard` on `/settings`.

So an admin can click a button and cause `BackupNowAsync()` to run. That much is real.

### 2. What the auditor missed — the web reports a backup that did not happen

`BackupService.BackupNowAsync()` (`src/TimesheetApp.Core/Services/BackupService.cs:35-44`) returns **`null`** — not an exception — when the folder is blank, the DB path is blank, or the DB file is missing:

```csharp
if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
    return null;
```

Two consumers, two opposite conclusions from the same `null`:

- **WPF** (`src/TimesheetApp/ViewModels/SettingsViewModel.cs:275-279`) branches on it and tells the truth:
  `"No backup made — choose a folder and make sure the database file exists."`
- **Web** (`src/timesheet-web/src/app/pages/settings/settings.component.ts:550-551`) coalesces it away:
  `Backup written to ${r.value ?? 'the configured folder'}` + `toast.show('Backup complete')`.

The `??` swallows the one value that means *nothing was written*. The endpoint cannot save it either — `:1054` wraps the `null` in a 200 `Results.Ok`, so `error:` (`:553`) never fires. On a backup feature this is the worst possible direction for the error to point: the user is told their data is safe at the exact moment it is not.

### 3. The null branch is reachable by default, and unfixable from the web

`src/TimesheetApp.Core/Config/JsonAppConfig.cs:59` — `_backupFolderPath = model?.BackupFolderPath ?? "";` — the folder defaults to **empty string**. A server whose `appsettings.json` omits `BackupFolderPath` returns `null` from **every** call to this endpoint, and the web says "Backup complete" **every time**.

The admin also has no way to notice or fix it from the browser: `settings.component.ts:45-60` records that the backup-folder / auto-backup / keep-count inputs were deliberately removed, and `settings.component.html:346-357` states the folders "are not editable from the web". So the misconfiguration that triggers the silent failure is invisible in the only UI that will still exist after M10.

### 4. Two smaller WPF-side rules also absent from the inventory

- **Pre-flight disable**: `src/TimesheetApp/Views/Tabs/SettingsTab.xaml:108-109` — `IsEnabled="{Binding HasBackupFolder}"`. WPF will not let you click the button with no folder set. The web button is disabled only on `busy()` (`settings.component.html:359`).
- **Post-click confirmation**: the WPF command's last statement is `RefreshBackups()` (`SettingsViewModel.cs:279`), re-listing the folder into the `Backups` collection rendered at `SettingsTab.xaml:115-134` with timestamp and byte size. A grep of the entire Angular app for `backup` (`src/timesheet-web/src/app/**`) returns **no listing call at all** — `ListBackups`/`BackupInfo` appear nowhere on the web side. Even on the success path the web shows only a server-side path string the browser cannot verify.

### 5. What survives regardless

The backup *engine* is Core and cannot be lost by M10: `src/TimesheetApp.Core/Services/BackupService.cs` (online WAL-safe copy `:53`, prune-to-keep-count `:130-144`, stamp parse `:147-155`, `RestoreAsync` `:97-126`), plus `src/TimesheetApp.Api/Program.cs:104` registering it. Only the WPF trigger, its `HasBackupFolder` guard, its null-branch message and its backup list die.

## Recommendation

Do not accept COVERED. The gap is one line of Angular (`settings.component.ts:550-551`) — branch on `r.value === null` and show a failure message instead of a coalesced success, or make `SettingsEndpoints.cs:1054` return a non-200 when `BackupNowAsync()` yields `null`. Until then, the web's "Backup now" is a happy-path-only replacement that lies on the unhappy path.
