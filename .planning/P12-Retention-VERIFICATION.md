# P12 Retention/Prune — Goal-Backward Verification (STEP 8b)

**Phase:** P12 — 3-Month Data Retention / Prune (DESTRUCTIVE)
**Branch:** feature/task-list-2026-06-27 (all 3 waves committed: 95fb3fc W1, c852ab0 W2, e5df032 W3)
**Verified:** 2026-06-28
**Method:** cross-referenced plan `must_haves` + RT-01..07 against real code under `src/TimesheetApp`; ran full test suite.
**Test gate:** `dotnet test` → **493 passed / 0 failed** (459 baseline + 34 new P12 tests).

---

## 1. must_haves coverage

### observable_truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| OT-1 | Retention OFF by default; enabling + months (default 3) persists app-local | **MET** | `IAppConfig.RetentionEnabled`/`RetentionMonths` (IAppConfig.cs:44-48); `JsonAppConfig` defaults false/3 (JsonAppConfig.cs:27,58-59) and `Save()` on set (JsonAppConfig.cs:121-131). Test `LoadAsync_LoadsRetentionFromConfig`, `ApplyRetentionSettings_PersistsToAppConfig`. |
| OT-2 | Old-than-window data archived (per-team md + verified .db snapshot) then deleted; live window {M..M-(N-1)} never touched | **MET** | `EnsureRetentionAsync` archives via `IPruneArchiver` then deletes (RetentionService.cs:112-156); cutoff = first-of-month − N (RetentionService.cs:56-61); all delete predicates bound by `<= @cutoff`. Test `EnsureRetention_prunes_old_keeps_window_and_spanning_backlog`. |
| OT-3 | Never removes in-window TimeLog/Standup; never orphans (spanning backlog survives); RESTRICT FKs never throw | **MET** | Children-first delete order + `NOT EXISTS` guards on Tasks/Backlogs (RetentionService.cs:234-256). Test asserts spanning backlog+task survive, in-window logs kept, no FK error 19. |
| OT-4 | Only TimeLogs/StandupEntries(+Issues)/Tasks/Backlogs/BacklogTags deleted; Users/Teams/Tags/PCA/Holidays/Templates/DefaultTasks/Settings + each team DEFAULT survive | **MET** | Delete SQL touches only the 6 allowlisted tables (+BacklogAudit for deleted backlogs only); `backlog_code <> 'DEFAULT'` excludes DEFAULT. Test `EnsureRetention_leaves_reference_tables_and_DEFAULT_untouched` (8 reference tables count-stable; both team + global DEFAULT survive). |
| OT-5 | Month pruned only if md archived to ≥1 root AND non-zero .db snapshot verified on disk first; one tx/month; marker advances post-commit | **MET (one deviation, sound)** | Archiver returns verified snapshot path only if md on ≥1 root AND snapshot non-zero (PruneArchiver.cs:71-82,166); service re-verifies `File.Exists && Length>0` before delete (RetentionService.cs:119-123); marker set post-commit (RetentionService.cs:159). **Deviation:** plan says "one transaction per month"; implementation archives each month then prunes all archived months in **one combined transaction** bounded by `effectiveCutoff` (RetentionService.cs:134-156). Net effect identical (atomic, crash-safe, contiguous-prefix only); see GAP-1. |
| OT-6 | Aborts on OneDrive conflict copy; Settings "Preview" dry-run write-free; "Run now" requires confirm | **MET** | Conflict-copy guard is step (a), aborts whole run (RetentionService.cs:88-91); `PreviewAsync` read-only (RetentionService.cs:63-84, test `Preview_writes_nothing`); WPF confirm dialog `OnRunRetention` (SettingsTab.xaml.cs:138-152). |
| OT-7 | Pruned months render empty in Task List/Reports/Daily without crashing; all 459 prior tests green | **MET (in-app = UAT-pending)** | All month-scoped views already query by month and return empty sets for monthless data (no special-casing needed — deletion just removes rows). 493/493 tests green (459 prior intact). Empty-render-no-crash in live UI is UAT-pending (RT-06 acceptance is a manual in-app check). |

### required_artifacts

| Artifact | Status | Evidence |
|----------|--------|----------|
| IAppConfig.RetentionEnabled/RetentionMonths (+JsonAppConfig) | **MET** | IAppConfig.cs:44-48; JsonAppConfig.cs:20-21,27,58-59,70-71,121-131. |
| IRetentionService/RetentionService (PreviewAsync + EnsureRetentionAsync: window math, R5a one-tx delete, backup+verify+conflict guards, marker) | **MET** | IRetentionService.cs (interface + RetentionPreview records); RetentionService.cs (full impl). |
| ExportHub/archive: per-team `{root}/{Team}/db/{yyyyMM}_pruned.md` + verified .db snapshot before prune | **MET** | PruneArchiver.cs:144-147 (md path), :151-167 (snapshot to `{root}/db/prune-snapshots/`). |
| App.xaml.cs startup gate + Settings "Retention" UI (toggle, months, Preview, Run now+confirm) | **MET** | App.xaml.cs:87-91 (gated on RetentionEnabled, inside root-configured block); SettingsTab.xaml:106-131 (toggle/months/Preview/Run now); SettingsViewModel.cs:268-336. |

### key_links

| Key link | Status | Evidence |
|----------|--------|----------|
| R5a time-axis delete; children-first (Issues→Entries→TimeLogs→Tasks→Backlogs→BacklogTags); NOT EXISTS guards; DEFAULT excluded | **MET** | RetentionService.cs:141-147 order; SQL :219-260. DELETE order: StandupIssues→StandupEntries→TimeLogs→Tasks→(BacklogAudit-for-deleted)→Backlogs→BacklogTags. |
| recovery = full .db snapshot; verify exists+non-zero BEFORE delete; archive→backup→verify→delete→commit→advance-marker | **MET** | Flow: conflict-guard → per-month archive+verify (:112-126) → XC-10 `_backup.BackupAsync()` (:132) → tx delete (:137-156) → commit → `SetAsync(MarkerKey)` (:159). |
| settings/reference NEVER touched (allowlist); single tx; idempotent (re-derive months, marker is record) | **MET** | Months re-derived each run from live data (`MonthsWithOldDataSql` :188-197, not from marker). Test `EnsureRetention_is_idempotent` ("nothing to prune" on re-run). |
| abort on conflict copy; only prune ≤ M-N; off by default + manual + dry-run | **MET** | Conflict abort (:88-91); cutoff `≤ M-N` (:56-61); off-by-default (config); manual Run + Preview dry-run (UI). |

---

## 2. RT coverage (RT-01..07 → implementing artifact)

| REQ | Status | Implementing artifact | Evidence / test |
|-----|--------|----------------------|-----------------|
| **RT-01** Retention config (opt-in; needs export root) | **MET** | `IAppConfig`/`JsonAppConfig`; startup gate | Defaults false/3; startup prunes only inside `if (ExportRoot1/2 configured)` block AND `RetentionEnabled` (App.xaml.cs:76-91). Archiver returns null when no root → no prune (PruneArchiver.cs:58; test `Archive_returns_null_when_no_root_configured`). |
| **RT-02** Prune trigger (startup + manual); oldest-first; window untouched; idempotent skip | **MET** | App.OnStartup + `RunRetentionCommand`; `EnsureRetentionAsync` | Months ordered ascending (RetentionService.cs:99); startup (App.xaml.cs:89) + manual (SettingsViewModel.cs:319). Test `EnsureRetention_is_idempotent`. |
| **RT-03** Archive-before-prune (md ≥1 root + .db backup BEFORE delete; no archive → no prune) | **MET** | `PruneArchiver` + verify gate in service | PruneArchiver writes md to both roots + snapshot (PruneArchiver.cs:67-82); service skips month on null/zero snapshot (RetentionService.cs:119-123). Tests `EnsureRetention_skips_month_when_archive_returns_null`, `..._snapshot_is_zero_bytes`, `..._prunes_only_contiguous_archived_prefix`. |
| **RT-04** Prune scope (business only, never settings; team DEFAULT survives) | **MET** | R5a delete SQL allowlist | Test `EnsureRetention_leaves_reference_tables_and_DEFAULT_untouched` (Users/Teams/UserTeams/PCA/Holidays/Templates/DefaultTasks/Tags byte-count-stable; both DEFAULT backlogs survive). |
| **RT-05** FK-safe deletion = R5a (no in-window loss, no orphan, spanning backlog) | **MET** | `DeleteTasksSql`/`DeleteBacklogsSql` NOT EXISTS guards | Test `EnsureRetention_prunes_old_keeps_window_and_spanning_backlog`; `..._never_deletes_null_period_month_backlog`; `..._cleans_orphan_backlogtags_keeps_surviving`. |
| **RT-06** In-app behavior after prune (empty months, no crash) | **PARTIAL / UAT-pending** | (no code change needed — views query by month) | No regression in 493 tests; deletion only removes rows so month-scoped queries return empty. Live in-app empty-render verification is a manual UAT item (design §6, plan OT-7). |
| **RT-07** Transactional + backup + idempotent marker + tests + dry-run | **MET** | `EnsureRetentionAsync` tx + `_backup` + marker; full test class | One tx (RetentionService.cs:137-155); XC-10 backup (:132); XC-09 journal check (:162, test `EnsureRetention_leaves_no_journal`); marker (test `EnsureRetention_advances_marker`); dry-run write-free (test `Preview_writes_nothing`); window-math edge tests (rollover, leap). |

---

## 3. GAPS (ranked)

**GAP-1 — Transaction granularity deviates from "one transaction per month" (LOW / accept).**
Plan must_have OT-5 and key_links say "single tx per month / one transaction per month." The implementation instead archives every prunable month first (oldest-first), tracks the contiguous successfully-archived `effectiveCutoff`, then deletes ALL archived months in a **single combined transaction** bounded by `effectiveCutoff` (RetentionService.cs:134-156). This is semantically equivalent or safer for the stated goals: atomic, DELETE-journal crash-safe (no half-pruned month), and the contiguous-prefix logic guarantees no month past a failed archive is ever deleted. It does mean all pruned months commit together rather than month-by-month. No correctness loss; documented in code comments. **Recommend: accept** (the must_have intent — atomicity + crash safety + archive-before-delete — is fully met).

**GAP-2 — RT-06 in-app empty-month render is UAT-pending (LOW).**
No automated test exercises the live Task List/Reports/Daily UI selecting a pruned month. Plan explicitly defers this to UAT (design §7 UAT line; OT-7). No code suggests a crash risk (deletion only removes rows; views already handle empty months). **Recommend: confirm in UAT** (STEP 8a), not a blocker.

**GAP-3 — BacklogAudit deletion is a documented deviation from the plan (LOW / sound).**
Plan/research said "KEEP BacklogAudit — do not delete." Implementation deletes BacklogAudit rows **only for backlogs that are themselves deleted** (RetentionService.cs:145,245-250), because `BacklogAudit.backlog_id` is a NOT NULL RESTRICT FK → Backlogs(id) (a v6 rename of RequestAudit); keeping them would raise SQLite error 19. Audit for **surviving** backlogs is fully retained, so the TaskList "moved-to-next-month" archive still works. Deviation is explicitly documented in code (:148-154) and covered by test `EnsureRetention_retains_BacklogAudit_for_surviving_removes_for_deleted`. **Recommend: accept** (the literal plan instruction was infeasible given the schema; the chosen behavior preserves the underlying intent and is the only FK-safe option).

No Critical or Important gaps found.

---

## 4. Verdict

### VERIFIED-WITH-GAPS

All 4 required_artifacts, all 4 key_links, and 6 of 7 observable_truths are **MET** with cited code + passing tests; OT-7 / RT-06 in-app rendering is **UAT-pending** (deferred to STEP 8a by design). All 7 RT requirements map to implementing artifacts (RT-01..05, RT-07 fully MET; RT-06 PARTIAL/UAT-pending). The 3 gaps are all LOW-severity and either equivalent-or-safer deviations explicitly documented in code (GAP-1, GAP-3) or an intentionally-deferred manual UAT check (GAP-2) — **none block release**. Full suite green at **493/493** (baseline 459 preserved).

Proceed to STEP 8a UAT to close RT-06 (enable + Preview + Run now on a real >3-month DB; verify snapshot/markdown on disk, settings survive, pruned months render empty).
