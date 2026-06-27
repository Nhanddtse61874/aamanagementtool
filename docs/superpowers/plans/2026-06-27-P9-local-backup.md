---
phase: P9
title: Local DB Backup + Restore (M5)
mode: A
schema_target: 7 (no DB schema change — file-level feature)
must_haves:
  observable_truths:
    - User picks a backup folder (Browse, persisted app-local); backup actions disabled until set.
    - "Backup now" copies the full live .db to a timestamped file in the chosen folder.
    - With auto-backup ON, startup makes one backup/day if none exists for today (non-fatal).
    - Settings lists existing backups (name/timestamp/size, newest first).
    - Restore: pick a backup → confirm → safety-copy current .db → replace live .db → prompt restart.
    - Folder pruned to newest N backups (configurable, default 30), best-effort, never failing the backup.
    - A "Backup & Restore" Settings section hosts folder picker, auto toggle, retention N, Backup now, list + Restore.
  required_artifacts:
    - IAppConfig + JsonAppConfig: BackupFolderPath, AutoBackupEnabled, BackupKeepCount (+ setters), persisted to appsettings.json
    - IBackupService + BackupService (BackupNow, ListBackups, Restore, prune)
    - SettingsViewModel + SettingsTab "Backup & Restore" section
    - App.xaml.cs: DI registration + startup once-per-day auto-backup (mirror backfill pattern)
    - Tests for BackupService (backup creates file, prune keeps N, restore safety-copies + swaps, list parses)
  key_links:
    - Backup/restore = file-level copy at idle (short connections + journal_mode=DELETE make File.Copy safe); distinct from XC-10 DbBackupHelper
    - Restore makes a safety copy of current DB before overwrite; instructs restart (no live hot-swap)
    - Prune only matches this app's backup filename pattern; never deletes unrelated files
    - Backup folder default empty → actions gated until user chooses (no silent default — per user)
---

# P9 — Local Backup · Plan (Mode A, lean)

**Refs:** `.planning/REQUIREMENTS.md` (BK-01..07), `.planning/UPCOMING-FEATURES.md` (P9), CLAUDE.md (dotnet skill, surgical, simplicity).
**Integration points (verified):** `IAppConfig`/`JsonAppConfig` (DbPath/ArchivePath pattern), `App.xaml.cs` DI (`AddSingleton<IAppConfig,JsonAppConfig>` ~:46) + startup backfill sequence (~:114-123, mirror for auto-backup), `SettingsViewModel`/`SettingsTab` (section + status-message conventions), existing `DbBackupHelper` (prune pattern to mirror, XC-10 — leave it intact).

## Design
- **Config:** add `BackupFolderPath` (string, "" = unset), `AutoBackupEnabled` (bool, default false), `BackupKeepCount` (int, default 30) to `IAppConfig` + `JsonAppConfig` (persist in appsettings.json alongside DbPath/ArchivePath). Setters mirror existing.
- **Service `IBackupService`/`BackupService`** (singleton; deps `IAppConfig`, `IClock`):
  - `Task<string?> BackupNowAsync()` — copy `IAppConfig.DbPath` → `{folder}/timesheet_{yyyyMMddHHmmss}.db`; null if no folder/db; prune after.
  - `Task<bool> AutoBackupIfDueAsync()` — if AutoBackupEnabled and no backup file dated today, BackupNow; else no-op. Best-effort.
  - `IReadOnlyList<BackupInfo> ListBackups()` — enumerate `{folder}/timesheet_*.db`, parse stamp, size; newest first.
  - `Task RestoreAsync(string backupPath)` — safety-copy current db → `{db}.pre-restore_{stamp}.bak`; `File.Copy(backupPath, dbPath, overwrite:true)`. Throws on unreadable backup.
  - private `Prune()` — keep newest `BackupKeepCount` of `timesheet_*.db`, best-effort (mirror DbBackupHelper.PruneOldBackups).
  - `BackupInfo` record (Path, Timestamp, SizeBytes).
- **Settings UI:** "Backup & Restore" section — folder TextBox + Browse + Apply, AutoBackup toggle, Retention N spinbox, "Backup now" button, backups DataGrid/list (timestamp, size) with a Restore button per row; status text. Restore → confirm dialog → call service → show "restart required" message (and offer to close the app).
- **Startup:** after the tasklist backfill (App.xaml.cs ~:123), add `try { await Services.GetRequiredService<IBackupService>().AutoBackupIfDueAsync(); } catch { trace }` (non-fatal, before window interactive).

## Tasks

<task id="P9-1" model="opus">
<read_first>
src/TimesheetApp/Config/IAppConfig.cs + JsonAppConfig.cs (persist pattern);
src/TimesheetApp/Services/DbBackupHelper.cs (prune pattern, XC-10 — do NOT remove);
src/TimesheetApp/Services/IClock.cs;
src/TimesheetApp/App.xaml.cs (DI ~:46, startup backfill ~:114-137);
src/TimesheetApp/ViewModels/SettingsViewModel.cs + Views/Tabs/SettingsTab.xaml (sections, Browse/Apply, status, overlay/confirm patterns; how ApplyDbPath/ArchivePath work);
.planning/REQUIREMENTS.md BK-01..07.
</read_first>
<action>
1. IAppConfig + JsonAppConfig: add BackupFolderPath/AutoBackupEnabled/BackupKeepCount (+ setters), persisted to appsettings.json (mirror DbPath/ArchivePath). Sensible defaults ("" / false / 30); tolerate missing keys in existing config files (backward compatible).
2. IBackupService + BackupService (+ BackupInfo record) per Design. File-level copy; prune mirrors DbBackupHelper; restore safety-copies first. Guard missing folder/db with clear outcomes (no throw on the happy no-op paths; throw only on a genuinely unreadable selected backup).
3. SettingsViewModel: inject IBackupService; add observable props (BackupFolder, AutoBackupEnabled, BackupKeepCount, Backups collection, status) + commands (BrowseBackupFolder, ApplyBackupSettings, BackupNow, RefreshBackups, Restore(item)). Restore command confirms, calls service, sets a "restart required" status (and optionally Application.Current.Shutdown after user confirm).
4. SettingsTab.xaml: add the "Backup & Restore" section per Design, matching existing styling.
5. App.xaml.cs: register IBackupService singleton; add the startup AutoBackupIfDueAsync call (non-fatal) after the tasklist backfill.
6. Tests (src/TimesheetApp.Tests): BackupService — BackupNow creates a timestamped copy in the folder + returns path; no folder/db → null; prune keeps newest N; ListBackups parses + orders newest-first; Restore writes a pre-restore safety copy then overwrites the db with the backup contents; unreadable backup path throws. Use a temp dir + a fake IClock + a temp IAppConfig (mirror existing service test setup).
</action>
<verify>dotnet build src/TimesheetApp.sln (0 errors) + dotnet test src/TimesheetApp.sln (all 314 prior + new green). Automated <60s.</verify>
<done>Backup folder configurable; manual + scheduled backup; list; restore with safety copy + restart prompt; retention prune; Settings section wired; tests green.</done>
</task>
