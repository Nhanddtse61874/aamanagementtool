# 3-Month Retention / Prune (M7 / P12) — Design Spec — DESTRUCTIVE

**Status:** Research-backed (STEP 4). Authored STEP 5, 2026-06-28. **Execution PAUSES for user plan approval.** Mode B. Builds on P10 (team) + P11 (export structure). Schema v8 (+ 1 Settings KV marker, no new table).
**Source:** `.planning/REQUIREMENTS.md` (RT-01..07) + `.planning/research/P12-*` (architecture + pitfall + synthesis). The synthesis §E lists 3 user-facing decisions to confirm at the gate.

## 1. Behavior
When retention is **enabled**, business data older than `RetentionMonths` (default 3) months is **archived then deleted** from the live DB, so the DB holds only the recent window. Default **OFF**; opt-in; manual run + dry-run.

## 2. What "older than the window" means
Current month M (local, `IClock.Today`). Live window = {M, M-1, …, M-(N-1)}. **Cutoff** = first month strictly before the window (i.e. `M-N` and older). TimeLogs/StandupEntries selected by `work_date` month; Backlogs by `period_month`. Half-open ISO bounds; window-math helper reuses `ExportHubService.AddMonths` (year rollover). The live window is NEVER touched.

## 3. RT-05 deletion rule = R5a (time-axis) — RESOLVED
Per pruned month (oldest first), in ONE transaction, children-first (FKs are RESTRICT, `foreign_keys=ON`):
1. `DELETE StandupIssues` for entries in the month (or rely on `ON DELETE CASCADE` from StandupEntries).
2. `DELETE StandupEntries WHERE substr(work_date,1,7) = @m`.
3. `DELETE TimeLogs WHERE substr(work_date,1,7) = @m`.
4. `DELETE Tasks WHERE backlog has period_month ≤ cutoff AND backlog code≠'DEFAULT' AND NOT EXISTS(any TimeLog on that task)`.
5. `DELETE Backlogs WHERE period_month ≤ cutoff AND code≠'DEFAULT' AND NOT EXISTS(any Task)`.
6. `DELETE BacklogTags` for any backlog just deleted (no FK — manual).
**Spanning backlog** (old period_month but a Task still has an in-window TimeLog) keeps that Task ⇒ the backlog survives. No in-window row is ever deleted; no orphan/FK violation. DEFAULT backlogs per team always survive.

## 4. Safety (locked)
- **Recovery artifact = the full pre-prune `.db` snapshot** (markdown is a human summary, NOT byte-exact). Before deleting a month: (a) write per-team markdown to `{root}/{Team}/db/{yyyyMM}_pruned.md` (both roots, reuse P11 builders); (b) take a full `.db` backup via `IDbBackupHelper`/`BackupService` and **verify it exists + non-zero on disk**; only then DELETE. These prune snapshots are **retained** (not auto-deleted). A month is pruned only if markdown landed on ≥1 root AND the .db snapshot verified.
- **Single transaction per month**, `Settings` marker `retention.pruned_through` advanced post-commit; DELETE-journal → crash leaves a consistent DB; post-commit XC-09 journal-clean check.
- **Multi-machine guard:** abort the whole run if `SqliteMaintenance.FindConflictCopies` finds any conflict copy; only ever prune `≤ M-N`.
- **Idempotency:** re-derive months-with-old-business-data each run; the marker is an optimization/record. A P9 restore re-introducing old data → re-archived + re-pruned safely.
- **Settings allowlist:** ONLY TimeLogs, StandupEntries(+Issues), Tasks, Backlogs, BacklogTags are ever deleted. Users/Teams/UserTeams/Tags/PcaContacts/Holidays/TaskTemplates/DefaultTasks/Settings are never touched (RT-04).

## 5. Service + config + triggers
- `IAppConfig`: `RetentionEnabled` (bool=false), `RetentionMonths` (int=3), app-local.
- `IRetentionService`: `Task<RetentionPreview> PreviewAsync()` (dry-run — months + row counts that WOULD be pruned, no writes) and `Task<string> EnsureRetentionAsync()` (archive→backup→verify→delete per month; status string). Deps: IAppConfig, IConnectionFactory, IClock, IDbBackupHelper, ITeamRepository, the P11 export builders/hub (archive), SqliteMaintenance, ISettingsRepository.
- **Startup** (App.OnStartup, after export backfill): if `RetentionEnabled` && a root configured && no conflict copy → `EnsureRetentionAsync()` (best-effort try/catch + Trace).
- **Settings "Retention" UI:** enable toggle, months spinner (default 3), **"Preview"** (shows the dry-run result), **"Run retention now"** (confirm dialog → EnsureRetentionAsync + status). 

## 6. In-app impact (RT-06)
Month-scoped Task List/Reports/Daily render pruned (empty) months without crashing (verified). The only in-app trace of pruned months is gone from the DB; the markdown + .db snapshot are the record. `GetLoggedHoursByBacklogAsync` naturally drops to in-window hours for surviving spanning backlogs.

## 7. Testing (RT-07 — non-negotiable)
Unit: window math edges (year rollover, local boundary); R5a **spanning-backlog** (survives, in-window logs kept); deletion FK order (no error 19); **settings/reference tables untouched** (byte-for-byte); DEFAULT never pruned; archive-fails → NO prune; .db-snapshot-missing → NO prune; conflict-copy present → abort; idempotent re-run (no double work, no re-delete); dry-run writes nothing; marker advance. Keep all 459 prior tests green.
UAT: enable + Preview + Run now on a real DB with >3 months of data; verify the snapshot/markdown on disk; verify settings survive; verify pruned months show empty in-app.

## 8. Out of scope
A full multi-writer sync/lock engine (accepted risk — small team; mitigated by conflict-copy abort + off-by-default); byte-exact markdown (the .db snapshot is the recovery path); auto-deletion of retained prune snapshots.
