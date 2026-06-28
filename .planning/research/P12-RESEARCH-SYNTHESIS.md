# P12 "Retention/Prune" — Research Synthesis (STEP 4)

**Date:** 2026-06-28 · Inputs: P12-ARCHITECTURE + P12-PITFALL research. DESTRUCTIVE feature. **Execution PAUSES for user plan approval.** Resolves RT-05 + locks the safety design; surfaces 3 user-facing decisions.

## A. Locked technical facts (VERIFIED)
- **FKs are RESTRICT (no cascade), `foreign_keys=ON`** → deleting a parent with children is BLOCKED (error 19), never orphans. Only `StandupIssues→StandupEntries` is `ON DELETE CASCADE`. `BacklogTags`/`UserTeams`/`StandupEntries.backlog_id` have NO FK. ⇒ deletion ORDER is forced **children-first**: StandupIssues → StandupEntries → TimeLogs → Tasks → Backlogs → (BacklogTags manual), all in ONE transaction.
- **RT-05 = R5a (time-axis prune)** — RESOLVED: delete TimeLogs/StandupEntries by `work_date` month (`substr(work_date,1,7) <= @cutoff`); delete a Task/Backlog **only when it has ZERO remaining TimeLogs AND `period_month <= @cutoff` AND code ≠ 'DEFAULT'** (guard `NOT EXISTS`). A **spanning backlog** (old period_month, recent in-window logs) keeps ≥1 task → survives. Provably never deletes an in-window row, never orphans. (R5b reassignment rejected.)
- "Business data of month M": TimeLogs/StandupEntries by `work_date` (yyyy-MM-dd), Backlogs by `period_month` (yyyy-MM); per-team DEFAULT backlogs excluded (survive).
- In-app views (Task List/Reports/Daily) already render empty months without crashing (no Single/divide-by-count) — VERIFIED.
- Window math: one tested helper off `IClock.Today` (local), reuse `ExportHubService.AddMonths` (handles year rollover), half-open ISO bounds. Cutoff = the month `M-RetentionMonths` and older.

## B. Safety design (locked, from pitfall)
1. **The true recovery artifact is the pre-prune full `.db` snapshot — NOT the markdown.** The markdown builders are human-readable summaries (no per-day TimeLog rows, exact precision, or BacklogAudit). So: before pruning a month, (a) write the per-team markdown into `{root}/{Team}/db/` (both roots) for human reading AND (b) take a **full `.db` backup** (XC-10) and **verify it exists + non-zero on disk** before any DELETE. A month is pruned only if BOTH the markdown archived to ≥1 root AND the .db snapshot verified. **These prune-snapshots are retained (not auto-deleted).**
2. **Single transaction per month**, marker advanced only post-commit (DELETE-journal → crash-safe, no half-pruned month). Post-commit XC-09 journal-clean verify.
3. **Idempotency:** re-derive "months with business data older than the window" each run (don't trust a marker alone); a `Settings` KV `retention.pruned_through` (yyyy-MM) is an optimization/record. A P9 restore that brings back old data → it gets re-archived+re-pruned (safe, since archive+backup precede delete).
4. **Multi-machine OneDrive guard:** **refuse to prune if `SqliteMaintenance.FindConflictCopies` returns any** conflict copy; only prune months `≤ M-RetentionMonths` (a buffer, never the live window). 
5. **Settings allowlist:** prune touches ONLY TimeLogs, StandupEntries(+Issues cascade), Tasks, Backlogs, BacklogTags(manual, no FK). NEVER Users/Teams/UserTeams/Tags/PcaContacts/Holidays/TaskTemplates/DefaultTasks/Settings.
6. **Off by default + manual + dry-run:** `RetentionEnabled` defaults false; a Settings manual "Run retention now" + a **dry-run** ("what would be pruned") before the real run.

## C. Architecture (adopted)
- `IAppConfig`: `RetentionEnabled` (bool, default false), `RetentionMonths` (int, default 3). App-local.
- `IRetentionService.EnsureRetentionAsync()` + `PreviewAsync()` (dry-run): deps IAppConfig, IConnectionFactory, IClock, IDbBackupHelper, ITeamRepository, the P11 ExportHub/builders (archive), SqliteMaintenance (conflict-copy check), ISettingsRepository (marker). 
- ExportHub gains a `ArchiveMonthForPruneAsync(year,month)` (or the retention service composes the existing per-team builders) writing to `{root}/{Team}/db/{yyyyMM}_pruned_*.md` + a full `.db` snapshot to `{root}/db/` (or a dedicated retained-snapshots folder).
- Startup (App.OnStartup): after the export backfill, if `RetentionEnabled` && a root configured && no conflict copy → `EnsureRetentionAsync()` (best-effort). Manual trigger + dry-run in Settings.
- Settings UI: a "Retention" section — enable toggle, months (default 3), "Preview" (dry-run) + "Run retention now" with a confirm.

## D. Wave plan (3 waves, zero same-wave file overlap)
- **W1** — Config (RetentionEnabled/Months) + the retention SQL core (`IRetentionService` + the month-selection + R5a deletion in one tx, backup+conflict-copy guards, marker) + unit tests (spanning backlog, settings-untouched, FK order, window math, idempotency, dry-run). Owns: IAppConfig/JsonAppConfig, RetentionService(+I), + tests. (No archive wiring yet — archive call stubbed/injected.)
- **W2** — Archive-for-prune (per-team markdown + verified full .db snapshot into the export structure, reuse P11) + DI + startup wiring (gated). Owns: ExportHubService (add prune-archive), App.xaml.cs, RetentionService archive hookup.
- **W3** — Settings "Retention" UI (toggle, months, Preview/dry-run, Run now + confirm) + integration tests. Owns: SettingsViewModel, SettingsTab.

## E. USER-FACING DECISIONS to confirm at the plan gate (do NOT silently decide)
1. **Markdown is a summary, not a full backup** → the design keeps a **full `.db` snapshot per prune** (retained) as the real safety net. Confirm that's acceptable (extra disk for retained snapshots) vs markdown-only (lossy — NOT recommended).
2. **Multi-machine OneDrive risk** → mitigation = off-by-default + manual + refuse-if-conflict-copy + only prune ≤ M-3. Confirm this is acceptable for your 2–5 person setup (no full sync/lock engine — out of scope).
3. **Default OFF + manual dry-run first** → retention won't auto-run until you enable it; first use should be a Preview. Confirm.
