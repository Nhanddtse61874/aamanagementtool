# P12 — Architecture Research: 3-Month Data Retention / Prune (DESTRUCTIVE)

**Phase:** P12 / M7 · **Mode:** B · **STEP:** 4 (Architecture Research)
**Branch:** `feature/task-list-2026-06-27` · **Schema:** v8 (team-aware) · **Date:** 2026-06-28
**Scope:** Archive the oldest business month to per-team markdown, then DELETE it from the live DB. Never touch settings/reference data. Crux = **RT-05 FK-safe deletion rule**.

Claim tags: `[VERIFIED]` (read in repo, file:line) · `[CITED]` (SQLite/.NET doc) · `[ASSUMED]` (inferred design).

---

## 1. FK reality (CRITICAL — empirically verified)

### 1.1 What FKs exist
- `Tasks.backlog_id → Backlogs(id)` — declared `FOREIGN KEY (request_id) REFERENCES Requests(id)` `[VERIFIED DatabaseInitializer.cs:53]`, then the v6 migration `ALTER TABLE Requests RENAME TO Backlogs; ALTER TABLE Tasks RENAME COLUMN request_id TO backlog_id;` `[VERIFIED DatabaseInitializer.cs:239,244]`.
- `TimeLogs.user_id → Users(id)` and `TimeLogs.task_id → Tasks(id)` `[VERIFIED DatabaseInitializer.cs:70-71]`.
- `StandupIssues.entry_id → StandupEntries(id) ON DELETE CASCADE` `[VERIFIED DatabaseInitializer.cs:116]` — the ONLY cascade in the schema.
- `StandupEntries.user_id → Users(id)` `[VERIFIED DatabaseInitializer.cs:102]`. **`StandupEntries.backlog_id` and `.team_id` have NO inline FK** (added as plain nullable columns) `[VERIFIED DatabaseInitializer.cs:246,266-267]` — deleting a Backlog or Team does NOT touch / block standup rows.
- `BacklogTags`, `UserTeams` — composite-PK link tables, **no inline FK** `[VERIFIED DatabaseInitializer.cs:131-135,158-162]`.

### 1.2 FK enforcement is ON per connection
Connection string sets `ForeignKeys = true` AND `PRAGMA foreign_keys=ON` is re-issued on the live handle `[VERIFIED SqliteConnectionFactory.cs:36,45]`; test pins it `[VERIFIED SqliteConnectionFactoryTests.cs:32-39]`. **Every** delete therefore enforces FKs.

### 1.3 Empirical probe (ran against the real Microsoft.Data.Sqlite 8.0 from the test bin, reproducing the v6 rename)
```
Tasks DDL after rename:
  CREATE TABLE Tasks(... backlog_id INTEGER NOT NULL, ... FOREIGN KEY(backlog_id) REFERENCES "Backlogs"(id))
Test 1: DELETE Backlog with child Task   -> BLOCKED (SQLite Error 19: FOREIGN KEY constraint failed)
Test 2: DELETE Task with child TimeLog   -> BLOCKED (SQLite Error 19)
Test 3: DELETE TimeLogs -> Tasks -> Backlogs (children first) -> all OK
```
**Conclusions `[VERIFIED probe]`:**
1. The v6 `RENAME TO` **rewrote the FK** to reference `"Backlogs"(id)`. The FK is live (modern SQLite default `legacy_alter_table=OFF` updates FK references on rename) `[CITED sqlite.org/lang_altertable]`.
2. These FKs are **RESTRICT** (SQLite default — no `ON DELETE` action): deleting a parent with children is **blocked**, NOT cascaded, NOT orphaned.
3. **Therefore the prune MUST delete children-first**: `StandupIssues` (auto-cascades on entry delete) → `StandupEntries` → `TimeLogs` → `Tasks` → `Backlogs`. Any other order raises Error 19 and the whole transaction rolls back.

---

## 2. RT-05 deletion rule — **RECOMMENDATION: R5a (time-axis prune)** ✅

### 2.1 What "business data of month M" means per table `[VERIFIED]`
| Table | "belongs to month M" via | Format |
|---|---|---|
| `TimeLogs` | `substr(work_date,1,7) = M` | `work_date` = `yyyy-MM-dd` `[VERIFIED TimeLogRepository.cs:143]` |
| `StandupEntries` | `substr(work_date,1,7) = M` | `work_date` = `yyyy-MM-dd` `[VERIFIED StandupRepository.cs:81]` |
| `StandupIssues` | via parent entry (cascade) | n/a |
| `Backlogs` | `period_month = M` | `period_month` = `yyyy-MM` `[VERIFIED Entities.cs:12,16; RepositoryCrudTests.cs:49]` |
| `Tasks` | via parent backlog | n/a (no own date) |

`work_date` (`yyyy-MM-dd`) and `period_month` (`yyyy-MM`) are both lexically month-comparable via the 7-char prefix. `[VERIFIED]`

### 2.2 The hazard the rule must avoid
A long-lived backlog created in an old month (e.g. `period_month='2026-03'`) can still have **Tasks whose TimeLogs fall inside the live window** (e.g. a `2026-06` log) — backlogs are never auto-closed; `TimesheetViewModel` even lets a backlog be "moved" to the next month `[VERIFIED TimesheetViewModel.cs:395-411]`. Deleting that backlog/task by `period_month` alone would destroy an **in-window** TimeLog → data loss. This is exactly RT-05's spanning-backlog case.

### 2.3 Why R5b (period_month + reassignment) is rejected
R5b would have to either keep the backlog but reassign its in-window logs to another backlog (there is no sane target — Tasks belong to exactly one backlog `[VERIFIED Tasks.backlog_id NOT NULL, DatabaseInitializer.cs:48-50]`), or rewrite `task_id` on live logs (mutating in-window business data, brittle, audit-confusing). It also conflicts with the existing TaskList archive semantics that already key the *backlog* dimension on `period_month` audit history `[VERIFIED TaskListArchiveService.cs:76-87]`. **Reject.**

### 2.4 Recommended rule R5a (time-axis prune; keep spanning backlogs)
> For each prunable month **M** (see §6): delete time-stamped rows whose date is in M; delete a `Task`+`Backlog` **only when it has ZERO remaining TimeLogs after the time-prune** AND its `period_month ≤ last-pruned-month`. Otherwise keep the backlog/tasks (they still carry in-window logs) and only the old dated rows are gone.

This guarantees: (a) **no in-window row is ever deleted** (we only delete by `work_date` in pruned months); (b) **no orphan** (a Task/Backlog is removed only once it has no children left); (c) settings untouched (we never touch Users/Teams/Tags/etc.).

### 2.5 Precise SQL + ORDER (one transaction; children-first per §1.3)
Let `@cutoff = 'yyyy-MM'` = the newest month that is being pruned through (e.g. with current month 2026-06, N=3, the live window is {2026-06,2026-05,2026-04}, so `@cutoff='2026-03'` and we prune every month `≤ '2026-03'`). Use **string prefix comparison** — robust because both formats are zero-padded ISO `[VERIFIED §2.1]`.

**Step A — StandupIssues** (auto-cascades, but explicit delete keeps it transaction-scoped and testable):
```sql
DELETE FROM StandupIssues
 WHERE entry_id IN (SELECT id FROM StandupEntries WHERE substr(work_date,1,7) <= @cutoff);
```
**Step B — StandupEntries** (no FK to Backlogs, so safe any time; team_id preserved in markdown):
```sql
DELETE FROM StandupEntries WHERE substr(work_date,1,7) <= @cutoff;
```
**Step C — TimeLogs** (the time axis — this is what frees Tasks/Backlogs for removal):
```sql
DELETE FROM TimeLogs WHERE substr(work_date,1,7) <= @cutoff;
```
**Step D — Tasks** that now have zero TimeLogs AND whose backlog is in a pruned month and is not the per-team DEFAULT:
```sql
DELETE FROM Tasks
 WHERE NOT EXISTS (SELECT 1 FROM TimeLogs l WHERE l.task_id = Tasks.id)
   AND backlog_id IN (
       SELECT id FROM Backlogs
        WHERE backlog_code <> 'DEFAULT'
          AND period_month IS NOT NULL
          AND period_month <= @cutoff);
```
**Step E — Backlogs** that are in a pruned month, are not DEFAULT, and now have zero remaining Tasks (so no FK from Tasks) — the NOT EXISTS double-checks against orphaning:
```sql
DELETE FROM Backlogs
 WHERE backlog_code <> 'DEFAULT'
   AND period_month IS NOT NULL
   AND period_month <= @cutoff
   AND NOT EXISTS (SELECT 1 FROM Tasks t WHERE t.backlog_id = Backlogs.id);
```
Also delete the now-dangling `BacklogTags` (no FK, so manual) and optionally trim `BacklogAudit` rows for deleted backlogs — **but keep audit** if the TaskList archive's "moved to next month" history is wanted (`TaskListArchiveService` reads `BacklogAudit period_month` `[VERIFIED TaskListArchiveService.cs:125-132]`). **Recommendation [ASSUMED]:** delete orphaned `BacklogTags` for removed backlogs; **keep** `BacklogAudit` (it is settings-adjacent history, cheap, and already powers archives).
```sql
DELETE FROM BacklogTags
 WHERE backlog_id NOT IN (SELECT id FROM Backlogs);
```

**Order rationale:** A→B clears standup (issues before entries — cascade-safe either way). C clears all old TimeLogs so D's `NOT EXISTS(TimeLogs)` can fire. D removes childless old Tasks so E's `NOT EXISTS(Tasks)` can fire. E removes childless old Backlogs. A backlog that still has any in-window TimeLog keeps at least one Task (that Task fails D's `NOT EXISTS`), so E's guard fails and the backlog survives — **spanning backlog preserved.** `[VERIFIED logic vs probe §1.3]`

### 2.6 Edge cases the rule handles
- **DEFAULT backlog per team** is explicitly excluded in D/E (`backlog_code <> 'DEFAULT'`) → each team's DEFAULT (Annual Leave/Meeting) survives, satisfying RT-04 `[VERIFIED GetDefaultForTeamAsync, BacklogRepository.cs:63]`.
- **Backlog with `period_month = NULL`** (e.g. legacy/unset) is never deleted (guarded by `period_month IS NOT NULL`) — conservative, no orphan risk.
- **Standup ad-hoc rows** (`backlog_id` null) delete purely by `work_date` — no FK concern `[VERIFIED DR-03; StandupRepository.cs:73]`.

---

## 3. Archive-before-prune (RT-03) — reuse P11 builders

### 3.1 Builders to reuse (already team-scoped) `[VERIFIED]`
- `ITaskListArchiveService.BuildMonthMarkdownAsync(teamIds, y, m)` `[VERIFIED ITaskListArchiveService.cs:13]`
- `IStandupArchiveService.BuildWeekMarkdownAsync(teamIds, monday)` `[VERIFIED IStandupArchiveService.cs:12]`
- `IExportService.ExportMarkdownAsync(new ExportFilter(null, y, m, null, teamIds))` for timesheet `[VERIFIED ExportHubService.cs:112]`
All accept `IReadOnlyList<int>? teamIds` and return `null` on no-data — exactly the per-team pattern `ExportHubService.ExportRootAsync` already uses `[VERIFIED ExportHubService.cs:83-128]`.

### 3.2 Where pruned-month markdown goes
P12 target is **`{root}/{Team}/db/`** (per UPCOMING-FEATURES P11 §30 + REQUIREMENTS RT-03) — distinct from the existing P11 whole-`.db` copy at **`{root}/db/`** `[VERIFIED ExportHubService.cs:132; P11-SUMMARY.md:10]`. The per-team `db/` subfolder is **currently never written** by the hub (only `timesheet/daily/tasklist`) `[VERIFIED ExportHubService.cs:97-128]` — P12 introduces it. Standup is weekly-filed; a month spans ~4–5 week files. **[ASSUMED]** write, per team, into `{root}/{TeamName}/db/`: the month's `{yyyyMM}_tasklist.md`, `{yyyyMM}_timesheet.md`, and each `{yyyyMMdd}_daily.md` for weeks overlapping month M (reuse `WeekMondaysFor`-style enumeration). Sanitize team segment with the existing `IPathSanitizer.SanitizeSegment` + collision suffix `[VERIFIED ExportHubService.cs:81-90; EX-07]`.

### 3.3 The "archived to ≥1 root or don't prune" guard (RT-03)
Per team × per root, attempt the markdown writes; track success per root (mirror `ExportHubService` best-effort `try/catch` per root `[VERIFIED ExportHubService.cs:57-69]`). A month is pruned **only if** at least one root received the complete set of that month's per-team files. If all roots fail (or no root configured), **skip the prune for that month**, leave data, surface a warning. `[ASSUMED]` Implement as: build markdown for every team first → write to each root → if `≥1 root fully written`, proceed to delete; else abort that month.

---

## 4. Retention marker / idempotency (RT-07) — **RECOMMEND: a single Settings key**

Use the existing `Settings(key,value)` KV store via `ISettingsRepository` `[VERIFIED SettingsRepository.cs; DATA-07]` — no new table.
- Key **`retention.pruned_through`**, value = last fully-pruned month `yyyy-MM` (e.g. `2026-03`). `[ASSUMED key name]`
- On run: compute the set of prunable months (≤ cutoff, §6). Skip any month `≤ retention.pruned_through`. After a month's successful archive+delete, advance the marker to that month.
- **Settings is intentionally NOT deleted by the prune** (RT-04) `[VERIFIED RT-04]`, so the marker survives prunes — correct.
- Simpler than a table, matches every other persisted scalar (N-days warning, etc.) `[VERIFIED SET-02]`. **Recommended.**

> Note: a Settings key lives in the **shared OneDrive DB**, so two machines share one marker — exactly right (the data is shared; pruning once globally is correct). The app-local config is reserved for per-machine prefs `[VERIFIED IAppConfig.cs:25-30]`; the marker belongs in the DB, not app-local.

---

## 5. Backup + transaction (RT-03/07)

### 5.1 Backup before prune
Two backup mechanisms exist:
- `IDbBackupHelper.BackupAsync()` (XC-10) — timestamped sibling next to the live `.db` `[VERIFIED IDbBackupHelper.cs:1-11]`.
- `IBackupService.BackupNowAsync()` / `BackupToFolderAsync(folder, keep)` (P9/P11) — copy to the user's backup folder / a given folder `[VERIFIED BackupService.cs:29-46]`.

**Recommendation:** call `IDbBackupHelper.BackupAsync()` **once, immediately before** the prune transaction (RT-07 references XC-10 by name) `[VERIFIED RT-03/07]`. File-level `File.Copy` is consistent at idle because the app uses short connections + `journal_mode=DELETE` (no open tx, no `-wal`) `[VERIFIED SqliteConnectionFactory.cs:8-10,45; BackupService.cs:9-10]`. **[ASSUMED]** Optionally also drop a copy into `{root}/db/` via `BackupToFolderAsync` so the off-OneDrive root has the pre-prune snapshot too.

### 5.2 One transaction via IConnectionFactory
All deletes (§2.5 Steps A–E + BacklogTags) run in **one** `conn.BeginTransaction()` opened from `IConnectionFactory.Create()` and committed once `[VERIFIED pattern: TimeLogRepository.UpsertBatchAsync:103-114; BacklogRepository.SetTagsAsync:199-213]`. A mid-prune crash → transaction never commits → DB is byte-consistent (DELETE-journal rollback) — **no partial month**. The marker is advanced **inside the same flow but only after commit** so a crash never marks an un-deleted month done. **[ASSUMED]** sequence per month: archive → verify ≥1 root → `BackupAsync()` → `BEGIN; deletes; COMMIT;` → set marker.

### 5.3 Backup is NOT in the prune transaction
`BackupAsync` is a file copy, not SQL — it precedes `BEGIN`. Correct (a backup taken inside a tx would copy uncommitted-then-rolled-back state on crash).

---

## 6. Config + service + startup

### 6.1 Config (IAppConfig — app-local, opt-in) `[VERIFIED IAppConfig.cs pattern]`
Add (mirroring `AutoBackupEnabled` / `BackupKeepCount`):
- `bool RetentionEnabled` (default **false**) `[VERIFIED RT-01 default false]`
- `int RetentionMonths` (default **3**) `[VERIFIED RT-01 default 3]`
Persisted in `%APPDATA%\TimesheetApp\appsettings.json` like the other P9/P11 keys; old config files default missing keys (backward-compatible, the established convention) `[VERIFIED IAppConfig.cs:26-39]`.

### 6.2 Service: `IRetentionService.EnsureRetentionAsync()`
New singleton service (DI like `IExportHubService`) `[VERIFIED App.xaml.cs:155]`. Dependencies: `IAppConfig`, `ISettingsRepository` (marker), `IConnectionFactory` (tx deletes), `IDbBackupHelper` (backup), `ITeamRepository`, the three builders (`ITaskListArchiveService`/`IStandupArchiveService`/`IExportService`), `IPathSanitizer`, `IClock`. Flow:
1. No-op if `!RetentionEnabled` OR both export roots empty `[VERIFIED RT-01 — needs a root]` (check `ExportRoot1Path`/`ExportRoot2Path` `[VERIFIED IAppConfig.cs:35-39]`).
2. Compute live window {M, M-1, … M-(N-1)} from `IClock.Today`; `@cutoff` = month just before the window (reuse `AddMonths` math `[VERIFIED ExportHubService.cs:178-182]`).
3. Enumerate prunable months that still hold business data and are `> retention.pruned_through`, oldest-first `[VERIFIED RT-02]`.
4. Per month: archive (§3) → guard (§3.3) → backup (§5.1) → one-tx delete (§2.5) → advance marker.

### 6.3 Startup placement
In `App.OnStartup`, run **after** the structured-export backfill (`ExportHubService.BackfillAsync`) and gated on enabled + a configured export root `[VERIFIED App.xaml.cs:75-81]`. Ordering matters: export backfill should snapshot completed periods into the structure first; retention then archives+prunes the oldest. Best-effort `try/catch` + `Trace.TraceWarning` like the other startup hooks `[VERIFIED App.xaml.cs:79-90]` — a prune failure must never block launch. **[ASSUMED]** Insert immediately after the `if (ExportRoot configured) { BackfillAsync }` block.

### 6.4 Manual trigger
A "Prune now / Archive & delete old months" button in Settings (alongside the P9 Backup & Restore + P11 Export-now sections) calling `EnsureRetentionAsync()`, with a confirmation dialog (destructive) and a dry-run/preview of which months will be deleted `[VERIFIED RT-07 dry-run; SettingsViewModel hosts Backup/Export]`. **[ASSUMED]** A dry-run mode on the service returns the month list + row counts without deleting.

---

## 7. In-app impact (RT-06) — month-scoped views tolerate empty months `[VERIFIED]`

- **Task List**: filters `b.PeriodMonth == monthKey`; empty → empty `rows` list, no crash; logged-hours lookup uses `TryGetValue(... ) ? h : 0m` `[VERIFIED TaskListViewModel.cs:131-137]`.
- **Reports / Timesheet**: read via `GetReportRowsAsync` / `GetByUserAndRangeAsync` with `work_date BETWEEN` — an empty range returns an empty list, mapped to empty projections; no `Single`/divide-by-count on the result `[VERIFIED TimeLogRepository.cs:30-67]`. Smart-input `nDays==0` guard already exists `[VERIFIED SI-03]`.
- **Daily board**: `GetEntriesForDayAsync` returns `[]` for a pruned day; board renders empty sections `[VERIFIED StandupRepository.cs:35-46; DR-08]`.
- **Roll-ups**: `GetLoggedHoursByBacklogAsync` is all-time `GROUP BY` — after a prune the pruned logs are simply absent; a surviving spanning backlog shows only its remaining (in-window) hours `[VERIFIED TimeLogRepository.cs:116-127]`. Acceptable per RT-06 (DB reflects retained window; markdown holds the rest).
- **No query assumes a month has data.** The only record of pruned months is the markdown — matches RT-06. `[VERIFIED]`

**One caveat [ASSUMED]:** the TaskList monthly archive backfill reads `period_month` audit history to render a "Moved to next month" section `[VERIFIED TaskListArchiveService.cs:125-132]`. If `BacklogAudit` is preserved (recommended §2.5) this keeps working; if a future change prunes audit, that section degrades for pruned months (already archived to markdown, so acceptable).

---

## 8. Wave decomposition (zero same-wave file overlap)

| Wave | Plan | New/edited files (no overlap within a wave) | Notes |
|---|---|---|---|
| **W1** | Config + marker | `IAppConfig.cs`, `JsonAppConfig.cs` (+ test) | `RetentionEnabled`/`RetentionMonths`; marker key constant. Pure additive. |
| **W1** | Retention SQL core (service, no startup wiring) | **NEW** `IRetentionService.cs`, `RetentionService.cs`, `RetentionServiceTests.cs` | The §2.5 deletion + §6.2 month-selection + §4 marker + §5.2 one-tx. Self-contained; depends only on existing repo interfaces. **No overlap with the config plan's files.** |
| **W2** | Per-team `db/` archive writer | edit `ExportHubService.cs`/**NEW** helper used by RetentionService for `{root}/{Team}/db/` month snapshots (§3.2) | Depends on W1 service shape. If editing `ExportHubService` here, W2 must be alone touching it. **[ASSUMED]** prefer a small `IRetentionArchiveWriter` to avoid touching ExportHubService. |
| **W2** | Startup wiring + DI | `App.xaml.cs` | Register `IRetentionService`; call after export backfill (§6.3). Alone in editing `App.xaml.cs`. |
| **W3** | Settings UI (manual trigger + dry-run + retention toggle/N) | `SettingsViewModel.cs`, `SettingsView.xaml(.cs)` | Mirrors P9/P11 Settings sections. Alone in editing Settings files. |
| **W3** | Integration / FK-safety / idempotency tests | **NEW** `RetentionIntegrationTests.cs` (spanning backlog, settings-not-deleted, archive-before-delete, idempotent re-run, disabled/no-root no-op) | Distinct new test file; no source overlap. |

**Overlap check:** `App.xaml.cs` (W2), `ExportHubService.cs` (W2, if touched), `SettingsViewModel/View` (W3), `IAppConfig`/`JsonAppConfig` (W1), `RetentionService` (W1) are each touched by exactly one plan within their wave. `[VERIFIED file list above]`

---

## 9. Risks & open items for spec
- **R-1 (resolved):** FKs are RESTRICT → must delete children-first. `[VERIFIED probe]`
- **R-2:** Per-team `{Team}/db/` folder is new — the hub never wrote it; P12 owns its creation + sanitized naming. `[VERIFIED]`
- **R-3 [ASSUMED]:** Keep `BacklogAudit` (don't prune it) so TaskList "moved-to-next-month" archives keep working; confirm in spec.
- **R-4 [ASSUMED]:** Standup month archive granularity = the overlapping weekly files (standup is weekly-filed); confirm we copy all weeks intersecting month M into `{Team}/db/`.
- **R-5 [ASSUMED]:** Marker = single Settings key `retention.pruned_through`; confirm name.
- **R-6:** `BacklogTags` orphan cleanup is manual (no FK); included in §2.5. `[VERIFIED no FK]`
