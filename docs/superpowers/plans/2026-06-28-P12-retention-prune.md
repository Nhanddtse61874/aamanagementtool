---
phase: P12
title: 3-Month Data Retention / Prune (DESTRUCTIVE)
mode: B
schema_target: 8 (+ 1 Settings KV marker; no new table)
pause_before_execute: true   # user must approve this plan before W1 (deletes business data)
must_haves:
  observable_truths:
    - Retention is OFF by default; enabling it + months (default 3) persists app-local.
    - With retention on, business data older than the N-month window is archived (per-team markdown + a verified full .db snapshot) then deleted; the live window {M..M-(N-1)} is never touched.
    - Deletion never removes an in-window TimeLog/Standup row and never orphans (spanning backlog survives); FKs (RESTRICT) never throw.
    - Only TimeLogs/StandupEntries(+Issues)/Tasks/Backlogs/BacklogTags are ever deleted; Users/Teams/Tags/PCA/Holidays/Templates/DefaultTasks/Settings + each team DEFAULT survive byte-for-byte.
    - A month is pruned only if its markdown archived to ≥1 root AND a non-zero .db snapshot was verified on disk first; one transaction per month; marker advances post-commit.
    - Run aborts if a OneDrive conflict copy is present; a Settings "Preview" dry-run shows what would be pruned without writing; "Run now" requires confirm.
    - Pruned months render empty in Task List/Reports/Daily without crashing. All 459 prior tests stay green.
  required_artifacts:
    - IAppConfig.RetentionEnabled/RetentionMonths (+JsonAppConfig)
    - IRetentionService/RetentionService (PreviewAsync dry-run + EnsureRetentionAsync: window math, R5a deletion in one tx, backup+verify+conflict-copy guards, Settings marker)
    - ExportHub/archive: per-team {root}/{Team}/db/{yyyyMM}_pruned.md + verified .db snapshot before prune
    - App.xaml.cs startup gate + Settings "Retention" UI (toggle, months, Preview, Run now+confirm)
  key_links:
    - R5a time-axis delete; children-first order (StandupIssues→StandupEntries→TimeLogs→Tasks→Backlogs→BacklogTags); NOT EXISTS guards; DEFAULT excluded
    - recovery = the full .db snapshot (markdown is a summary); verify snapshot exists+non-zero BEFORE delete; archive→backup→verify→delete→commit→advance-marker
    - settings/reference tables NEVER touched (allowlist); single tx per month; idempotent (re-derive months, marker is record)
    - abort on conflict copy; only prune ≤ M-N; off by default + manual + dry-run
---

# P12 — Retention/Prune · Plan (Mode B, 3 waves). **DO NOT START W1 until the user approves (DESTRUCTIVE).**

**Refs:** spec `docs/superpowers/specs/2026-06-28-retention-prune-design.md`; `.planning/research/P12-*` (architecture R5a + pitfall showstoppers + synthesis §E user decisions); REQUIREMENTS RT-01..07; CLAUDE.md. Baseline = 459 tests. Build/test gate each wave.

## W1 — Config + retention core (SQL, guards, dry-run) (model opus)
Owns: IAppConfig.cs, JsonAppConfig.cs, RetentionService.cs(+I), + tests.
- IAppConfig: `RetentionEnabled` (default false) + `RetentionMonths` (default 3) + setters, app-local, backward-compatible.
- IRetentionService:
  - **Window math (FIX-A):** DUPLICATE the 3-line `AddMonths` helper into RetentionService (it's `private static` in ExportHubService.cs:178 — NOT reusable; cite as reference). **Cutoff (BLOCKER-2, PIN THIS):** `@cutoff = AddMonths(IClock.Today firstOfMonth, -RetentionMonths)` formatted `"yyyy-MM"`; a month is prunable iff its key `<= @cutoff` using **string-prefix compare `substr(col,1,7) <= @cutoff`** (work_date `yyyy-MM-dd`, period_month `yyyy-MM` are zero-padded ISO — do NOT hand-roll last-day/half-open math).
  - `PreviewAsync()` → months + per-table row counts that WOULD prune (NO writes — guaranteed write-free; W3 "Preview" calls only this).
  - **`EnsureRetentionAsync()` flow (BLOCKER-3 ordering):** FIRST: if `SqliteMaintenance.FindConflictCopies(DbPath)` non-empty → ABORT whole run (no archive/backup/delete). Then for each prunable month (oldest first): (archive seam — W2 fills, returns the verified retained snapshot path); **verify THAT snapshot file `File.Exists && Length>0` immediately before BEGIN** (BLOCKER-1 — if absent/zero, ABORT this month, no delete); then ONE transaction, R5a delete children-first: **explicit `DELETE StandupIssues` (SUGGESTION-2, don't rely on cascade)** → StandupEntries (`substr(work_date,1,7)<=@cutoff`) → TimeLogs (`substr(work_date,1,7)<=@cutoff`) → Tasks (`NOT EXISTS TimeLog AND backlog.period_month<=@cutoff AND period_month NOT NULL AND code<>'DEFAULT'`) → Backlogs (`NOT EXISTS Task AND period_month<=@cutoff AND period_month NOT NULL AND code<>'DEFAULT'`) → BacklogTags for deleted backlogs (manual, no FK). **KEEP BacklogAudit (SUGGESTION-3 — do not delete; TaskList moved-to-next-month archive reads it).** Commit; post-commit advance `Settings` `retention.pruned_through`; post-commit `SqliteMaintenance` journal-clean (XC-09) check.
  - The archive is an injected seam (`IPruneArchiver` interface) so W1 owns the deletion+guards and W2 adds the archiver as a NEW file.
- Tests (RT-07): spanning-backlog survives + keeps in-window logs; deletion order no FK error (error-19-free); settings/reference tables byte-for-byte untouched; DEFAULT never pruned; window-math edges (year rollover, leap); idempotent re-run; **dry-run writes nothing**; **snapshot-missing/zero → no delete**; **conflict-copy present → abort (zero deletes)** (SUGGESTION-1); **post-commit journal gone (XC-09)** (SUGGESTION-1); marker advance; BacklogAudit retained. Use temp DB + a fake IPruneArchiver.
<verify>build + test green (459 + new).</verify>

## W2 — Archive-for-prune + DI + startup wiring (opus)
Owns: PruneArchiver.cs(+I) (NEW — implements W1's IPruneArchiver seam), ExportHubService.cs (only if a small helper is needed), App.xaml.cs.
- PruneArchiver.ArchiveMonthForPruneAsync(year,month) → returns the verified retained `.db` snapshot path (or null on failure): per active+inactive team (`ITeamRepository.GetAllAsync`) write `{root}/{Team}/db/{yyyyMM}_pruned.md` (reuse P11 `BuildMonthMarkdownAsync`/`BuildWeekMarkdownAsync` + `ExportService.ExportMarkdownAsync` scoped to `[teamId]`) to BOTH roots; **and (BLOCKER-1) write the full `.db` snapshot via plain `File.Copy` to a DEDICATED never-auto-pruned folder** `{root}/db/prune-snapshots/timesheet_{yyyyMM}_pre-prune_{stamp}.db` (NOT `BackupToFolderAsync`/`BackupAsync` — those auto-prune to 10/30 and would delete the recovery artifact). Return success (snapshot path) only if markdown landed on ≥1 root AND the snapshot `File.Exists && Length>0`. RetentionService verifies that path again right before delete.
- DI: register `IRetentionService` + `IPruneArchiver` singletons.
- App.OnStartup (BLOCKER-3): INSIDE the existing `if (export root configured)` block, AFTER the export `BackfillAsync()` (and after EnsureBootstrapped + DefaultTaskSync), if `RetentionEnabled` → `await IRetentionService.EnsureRetentionAsync()` best-effort (try/catch+Trace). No-root machine never prunes. Conflict-copy abort is the service's first step.
- Tests: archiver writes per-team pruned md + a snapshot in `prune-snapshots/` that a subsequent BackupAsync/export-prune does NOT remove (BLOCKER-1 regression); archive-fail (no root / unwritable) → returns null → EnsureRetention does NOT delete that month; DI resolves IRetentionService + IPruneArchiver.
<verify>build + test green.</verify>

## W3 — Settings Retention UI (sonnet)
Owns: SettingsViewModel.cs, the real Settings view XAML(+.cs), + tests.
- **FIX-B: first `Glob **/Settings*.xaml` to confirm the real view filename** (likely `SettingsView.xaml`, NOT `SettingsTab.xaml`) and own that actual file.
- "Retention" section: enable toggle (bound RetentionEnabled), months spinner (RetentionMonths, default 3), "Preview" button → calls ONLY `PreviewAsync` (write-free, SUGGESTION-4) → show months + counts in a status/area, "Run retention now" button → confirm dialog → `EnsureRetentionAsync` + status. Inject IRetentionService as a trailing optional ctor dep (SettingsViewModel already ends with `IMessenger?`, `IExportHubService?` — add `IRetentionService?`); update the ctor test call sites.
- Tests: Preview populates without deleting; Run-now calls EnsureRetentionAsync; toggle/months persist. Update SettingsViewModel ctor test sites (trailing optional dep).
<verify>build + test green (whole suite).</verify>

## Overlap notes
RetentionService.cs: W1 (core) defines it; W2 fills the archive seam — keep the seam an injected interface so W2 adds a NEW file (the archiver) rather than re-editing W1's service body where avoidable; if W2 must edit RetentionService.cs it runs after W1 (sequential, fine). App.xaml.cs W2-only; SettingsTab/VM W3-only; IAppConfig/JsonAppConfig W1-only. parallelization:false, commit per wave.
