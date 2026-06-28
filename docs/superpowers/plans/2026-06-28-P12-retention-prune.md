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
  - window math helper (cutoff = months ≤ M-N, local IClock, reuse ExportHub.AddMonths semantics); `PreviewAsync()` → months + per-table row counts that WOULD prune (NO writes).
  - `EnsureRetentionAsync()` core deletion (archive hookup is W2 — here inject an `IPruneArchiver`/delegate or call a seam that W2 fills; for W1 the deletion + guards + marker are the deliverable, with archive abstracted): for each month ≤ cutoff with business data (oldest first): conflict-copy abort guard; `IDbBackupHelper.BackupAsync()` + verify non-zero; (archive seam); then ONE transaction R5a delete children-first (StandupIssues/cascade → StandupEntries → TimeLogs → Tasks[NOT EXISTS TimeLog, period_month≤cutoff, ≠DEFAULT] → Backlogs[NOT EXISTS Task, …] → BacklogTags manual); post-commit advance `Settings` `retention.pruned_through`; post-commit journal-clean check.
- Tests (RT-07): spanning-backlog survives + keeps in-window logs; deletion order no FK error; settings/reference tables untouched byte-for-byte; DEFAULT never pruned; window-math edges (year rollover); idempotent re-run; dry-run writes nothing; backup-missing/zero → no delete; marker advance. Use temp DB.
<verify>build + test green (459 + new).</verify>

## W2 — Archive-for-prune + DI + startup wiring (opus)
Owns: ExportHubService.cs (add prune-archive method), App.xaml.cs, RetentionService archive-seam hookup (its own file — coordinate so W1's seam is filled here).
- ExportHub/archiver: `ArchiveMonthForPruneAsync(year,month)` — per active+inactive team write `{root}/{Team}/db/{yyyyMM}_pruned.md` (reuse P11 BuildMonth/Week builders + ExportService scoped to [teamId]) to BOTH roots; + a full `.db` snapshot via BackupToFolderAsync to a retained location (e.g. `{root}/db/`), returning success only if markdown landed on ≥1 root and the .db snapshot verified non-zero. RetentionService calls this before delete; prunes only on success.
- DI: register IRetentionService (+ archiver) singletons.
- App.OnStartup (after export backfill): if `RetentionEnabled` && a root configured → `EnsureRetentionAsync()` best-effort (try/catch+Trace); the conflict-copy abort lives in the service.
- Tests: archive writes per-team pruned md + verified .db snapshot to both roots; archive-fail (no root / unwritable) → EnsureRetention does NOT delete that month; DI resolves.
<verify>build + test green.</verify>

## W3 — Settings Retention UI (sonnet)
Owns: SettingsViewModel.cs, SettingsTab.xaml(+.cs), + tests.
- "Retention" section: enable toggle (bound RetentionEnabled), months spinner (RetentionMonths, default 3), "Preview" button → show PreviewAsync result (months + counts) in a status/area, "Run retention now" button → confirm dialog → EnsureRetentionAsync + status. Inject IRetentionService.
- Tests: Preview populates without deleting; Run-now calls EnsureRetentionAsync; toggle/months persist. Update SettingsViewModel ctor test sites (trailing optional dep).
<verify>build + test green (whole suite).</verify>

## Overlap notes
RetentionService.cs: W1 (core) defines it; W2 fills the archive seam — keep the seam an injected interface so W2 adds a NEW file (the archiver) rather than re-editing W1's service body where avoidable; if W2 must edit RetentionService.cs it runs after W1 (sequential, fine). App.xaml.cs W2-only; SettingsTab/VM W3-only; IAppConfig/JsonAppConfig W1-only. parallelization:false, commit per wave.
