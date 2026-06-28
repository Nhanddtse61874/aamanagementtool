---
phase: P11
title: Export Restructure (per-team folders, 2-root mirror)
mode: B
schema_target: 8 (no DB schema change)
must_haves:
  observable_truths:
    - Settings has two export-root pickers (SharePoint/shared + local), persisted app-local.
    - Export writes {root}/{Team}/{timesheet,daily,tasklist}/ per-team markdown + one {root}/db/ .db copy, mirrored to both roots.
    - Each team folder contains only that team's data; '|' escaped; no-data period → no file.
    - Startup backfills completed weeks/months into the structure when a root is configured (else legacy flat archive); a manual "Export now" regenerates current+completed.
    - Team names are sanitized to safe folder segments; one failing root doesn't abort the other.
    - All 430 prior tests stay green (legacy all-teams builders unchanged).
  required_artifacts:
    - IAppConfig.ExportRoot1Path/ExportRoot2Path (+ JsonAppConfig) + Settings UI pickers + "Export now"
    - IPathSanitizer/PathSanitizer (or static util) — safe folder segments
    - Per-team markdown builders on StandupArchiveService + TaskListArchiveService (+ ExportService scoped via TeamIds)
    - IExportHubService/ExportHubService (orchestrate per-team × 2-root, .db copy, BackfillAsync + ExportNowAsync)
    - App.xaml.cs DI + startup wiring (hub backfill when roots configured, else legacy)
  key_links:
    - Per-team content reuses the existing builders scoped to [teamId] (P10 team filters) — no logic duplication, legacy all-teams path unchanged
    - .db copy reuses BackupService copy+prune at {root}/db (one per root, not per team)
    - Best-effort per root; sanitized team names; idempotent overwrite; no-data→no-file
    - Hub supersedes flat archives only when a root is configured (backward-compat fallback)
---

# P11 — Export Restructure · Plan (Mode B, 3 waves)

**Refs:** spec `docs/superpowers/specs/2026-06-28-export-restructure-design.md`; REQUIREMENTS EX-01..07; this session's export-subsystem map; CLAUDE.md. Baseline = 430 tests green. Build/test gate each wave.

## W1 — Config + path sanitizer + Settings UI (model opus)
Owns: IAppConfig.cs, JsonAppConfig.cs, PathSanitizer.cs(+I), SettingsViewModel.cs, SettingsTab.xaml(+.cs).
- IAppConfig + JsonAppConfig: `ExportRoot1Path`/`ExportRoot2Path` (+ setters), default "", backward-compatible (mirror ArchivePath).
- `IPathSanitizer.SanitizeSegment(name)` + impl: strip `Path.GetInvalidFileNameChars()`, trim/collapse; empty → `team-{id}` fallback (provide an overload or document caller passes id). Unit-tested.
- SettingsViewModel: two export-root props + Browse/Apply commands (copy ArchivePath pattern) + an `ExportNow` command + status (command wired to the hub in W3 — for W1 it can be a stub that W3 fills, or add the hub dep in W3). SettingsTab.xaml: an "Export logs" section with the two pickers + Export now button + status.
- Tests: config persist/default round-trip; sanitizer (invalid chars, empty→fallback, normal name).
<verify>build + test green (430 + new).</verify>

## W2 — Per-team builders + ExportHubService (opus)
Owns: StandupArchiveService.cs(+I), TaskListArchiveService.cs(+I), ExportService.cs(+I if needed), BackupService.cs(+I), ExportHubService.cs(+I).
- **FIX-3 (builder extraction is a FETCH-RELOCATING refactor, not a rename):**
  - StandupArchiveService: add `BuildWeekMarkdownAsync(IReadOnlyList<int>? teamIds, DateOnly weekMonday)→string?` that RELOCATES the fetch from `ExportWeekAsync` (entries/issues/names/teamNames) passing `teamIds` to `GetEntriesForRangeAsync(monday,friday,teamIds)`, returns null when `entries.Count==0`, then calls the existing static `BuildMarkdown(...)`. `ExportWeekAsync` delegates with `teamIds=null` and MUST stay byte-identical (legacy flat file).
  - TaskListArchiveService: the month build is fully inline in `ExportMonthAsync` (no separate method today) — extract `BuildMonthMarkdownAsync(IReadOnlyList<int>? teamIds, int year, int month)→string?` relocating ~lines 52-102, changing `SearchAsync(null)`→`SearchAsync(null, teamIds)`; `GetLoggedHoursByBacklogAsync()`/`GetAuditAsync` stay all-team but are keyed by backlog id over the team-filtered set (correct — add a test asserting other teams' rows are excluded). `ExportMonthAsync` delegates with `teamIds=null`, byte-identical.
- ExportService: ExportMarkdownAsync(filter) with `TeamIds=[teamId]` already yields single-team content (month-based via Year/Month — matches `{yyyyMM}_timesheet.md`); the hub supplies the filename (service returns content only).
- **FIX-1 (.db copy — BackupService is NOT drop-in):** `BackupNowAsync()` is hard-wired to `_config.BackupFolderPath`. Add an overload `BackupToFolderAsync(string folder, int keep)→string?` (File.Copy `_config.DbPath`→`{folder}/timesheet_{yyyyMMddHHmmssfff}.db` + prune to `keep` using the existing `timesheet_*.db` pattern); refactor `BackupNowAsync` to call it with `(BackupFolderPath, BackupKeepCount)`. The hub calls `BackupToFolderAsync("{root}/db", BackupKeepCount)`. Do NOT call the parameterless BackupNowAsync from the hub.
- IExportHubService/ExportHubService: `ExportNowAsync()` + `BackfillAsync()`; for each configured root (skip empty) × **each team from `ITeamRepository.GetAllAsync()`** (FIX-2: incl. inactive teams that have data, per EX-02; sanitized name): write `{root}/{team}/timesheet|daily|tasklist/{file}` from the per-team builders; write one `{root}/db/timesheet_{stamp}.db` per root via `BackupToFolderAsync`. Best-effort per root (one failing root doesn't abort the other); status string; idempotent overwrite; no-data→no-file. Inject IAppConfig, ITeamRepository, the 3 builders, IBackupService, IClock, IPathSanitizer.
- Tests: per-team builder yields single-team content (other teams excluded); hub writes the full structure to two temp roots + mirrors; skips empty root; one-root-failure isolates; .db copy in {root}/db; backfill generates completed periods; idempotent. **`<verify>` MUST run StandupArchiveServiceTests + TaskListArchiveServiceTests explicitly (byte-identical legacy path).**
<verify>build + test green incl. the two archive test files (legacy output unchanged).</verify>

## W3 — DI + startup wiring + Export now (sonnet)
Owns: App.xaml.cs, SettingsViewModel.cs (ExportNow command body), + the SettingsViewModel ctor test sites.
- Register IPathSanitizer + IExportHubService (singletons).
- App.OnStartup: if `ExportRoot1Path` or `ExportRoot2Path` set → `await IExportHubService.BackfillAsync()` (best-effort try/catch + Trace, mirroring App.xaml.cs:59-65) INSTEAD of the two legacy flat backfills; else keep `BackfillMissingWeeksAsync`/`BackfillMissingMonthsAsync`. Keep ordering after `EnsureBootstrappedAsync` + `DefaultTaskSync` (teams must exist).
- **FIX-4:** inject `IExportHubService` into SettingsViewModel for the ExportNow command → `ExportNowAsync()` + status. This changes the SettingsViewModel ctor → UPDATE all 6 positional call sites: SettingsViewModelTests.cs (~:75,:329,:539) + MainViewModelTests.cs (~:54,:189,:215). (Add as a trailing optional `IExportHubService? exportHub = null` to minimize churn while still DI-injected.)
- Tests: DI resolves the hub + sanitizer + SettingsViewModel + MainViewModel; startup-branch selection reasoned. Full suite green.
<verify>build + test green (whole suite).</verify>

## Overlap notes
SettingsViewModel.cs touched by W1 (UI/props) + W3 (ExportNow body) — sequential. App.xaml.cs W3-only. Archive services W2-only. parallelization:false, commit per wave.
