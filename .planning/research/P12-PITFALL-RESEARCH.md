# P12 — Pitfall Research (3-Month Data Retention / Prune) — DESTRUCTIVE

**Phase:** STEP 4 (Mode B) · **Date:** 2026-06-28 · **Branch:** `feature/task-list-2026-06-27` · **Schema:** v8
**Author:** Pitfall Research agent. **Stance:** paranoid. This is the single highest-risk feature in the project — it permanently deletes business data from a OneDrive-synced SQLite file shared by a 2–5 person team.

Tag legend: `[VERIFIED]` = read in code this session (file:line); `[CITED]` = SQLite/.NET documented behavior; `[ASSUMED]` = inferred, must be confirmed in spec/UAT.

---

## Ground truth established from the codebase (the constraints any plan inherits)

- **Dates are TEXT, sortable ISO.** `work_date` / `deadline` / `created_at` stored as `yyyy-MM-dd` / ISO-8601 strings; `period_month` is `yyyy-MM`. So month/range selection is a **string prefix/`>=`/`<=` comparison**, not a date function. `[VERIFIED]` TimeLogRepository.cs:143; StandupRepository.cs:174; Entities.cs:13-14; DatabaseInitializer.cs:10.
- **work_date is LOCAL date, created_at is UTC.** `TimeLog.WorkDate` is the user's local calendar day; `created_at`/backup stamps use `_clock.UtcNow`. Two different time bases live in the same rows. `[VERIFIED]` TimeLogRepository.cs:139-140; BackupService.cs:41.
- **FK graph (enforced — `foreign_keys=ON` every connection):** `[VERIFIED]` SqliteConnectionFactory.cs:36,45
  - `TimeLogs.task_id → Tasks(id)` (NO cascade)
  - `TimeLogs.user_id → Users(id)` (NO cascade)
  - `Tasks.backlog_id → Backlogs(id)` (declared `REFERENCES Requests`; SQLite rewrites the ref on `RENAME TO Backlogs` since `legacy_alter_table` defaults OFF) `[CITED]` SQLite ALTER TABLE docs / `[VERIFIED]` DatabaseInitializer.cs:53,239
  - `StandupIssues.entry_id → StandupEntries(id)` **ON DELETE CASCADE**
  - `StandupEntries.user_id → Users(id)` (NO cascade)
  - **`BacklogTags`, `UserTeams` have NO FK** (junction tables, plain PK). `[VERIFIED]` DatabaseInitializer.cs:131-162
- **No FK from TimeLogs/Standup to Teams/Backlogs other than via task_id.** `team_id` columns are loose (nullable, no inline FK). `[VERIFIED]` DatabaseInitializer.cs:265-267
- **Deletion order forced by FK:** must delete `TimeLogs` (child) **before** `Tasks`, and `Tasks` before `Backlogs`. `StandupIssues` auto-cascade when `StandupEntries` deleted. `BacklogTags` rows for a deleted backlog are **orphaned silently** (no FK to clean them). `[VERIFIED]`/`[CITED]`
- **Single-tx writes already the norm:** `UpsertBatchAsync` wraps the batch in one `BeginTransaction`/`Commit`. The init runs in one tx. P12 must follow this. `[VERIFIED]` TimeLogRepository.cs:103-114; DatabaseInitializer.cs:25-34.
- **XC-09 journal cleanliness check exists:** `SqliteMaintenance.IsJournalGone(dbPath)` → `!File.Exists(dbPath + "-journal")`. A destructive write MUST verify this after commit and route a warning to `IJournalWarningSink` before OneDrive is allowed to sync. `[VERIFIED]` SqliteMaintenance.cs:31-32; App.xaml.cs:99-103.
- **Two backup mechanisms exist and must be reused:** `IDbBackupHelper.BackupAsync()` (`.bak` next to DB, XC-10) and `IBackupService.BackupToFolderAsync(folder, keep)` (timestamped full-DB copy into export `{root}/db`). `[VERIFIED]` DbBackupHelper.cs:25-36; BackupService.cs:34-46.
- **Archive builders are team-scoped and return `null` on no-data:** `ExportHubService` already iterates teams, sanitizes segments, writes per-team md, and copies the whole DB to `{root}/db`. `TaskListArchiveService.BuildMonthMarkdownAsync(teamIds, y, m)` and `StandupArchiveService.BuildWeekMarkdownAsync` are the reuse surface. `[VERIFIED]` ExportHubService.cs:73-133; TaskListArchiveService.cs:68-114.
- **`period_month` ≠ time axis.** A backlog has ONE `period_month` (the month it "belongs to") but its Tasks can accrue TimeLogs in ANY month, including in-window months. `MoveMonthAsync` only advances the marker; logs stay put and are audited (`BacklogAudit field='period_month'`). **This is the spanning-backlog trap, confirmed real by the data model.** `[VERIFIED]` Entities.cs:13-23; BacklogRepository.cs:157; STATE.md "MoveMonthAsync".
- **Startup is already a chain of best-effort backfills** (auto-backup → team bootstrap → default-task sync → export backfill). A startup prune would slot in here and must be **at least as defensive** (it is the only one that deletes). `[VERIFIED]` App.xaml.cs:55-91.

---

## TOP 6 PITFALLS (ranked by severity)

### #1 — SHOWSTOPPER · Archive "succeeded" but is incomplete → silent permanent loss
The whole safety model is "archive to markdown, then delete, không lo bị mất." But the existing markdown builders are **lossy summaries, not faithful dumps**, and they can return a file that *looks* fine while missing data:

- `TaskListArchiveService` md is a **per-backlog summary table** (code, project, type, PCT, PCA, deadlines, estimate, logged-total, progress, tags, schedule) — it does **NOT** contain individual TimeLog rows, per-day hours, exact hours precision, the audit trail (`BacklogAudit`), task-level status, or `Note` body text in a re-importable form. `[VERIFIED]` TaskListArchiveService.cs:165-204.
- Timesheet md (`| Date | Task | Hours |`) captures the logs but hours are **formatted for humans** (whole → no decimal; `0.#`) — round-trip precision is "good enough to read," not "exact bytes." `[VERIFIED]` ExportHubService.cs:137; TaskListArchiveService.cs:241-247.
- A team with no logs that month → builder returns `null` → **no file** — which is correct, but it means "archive present" is not provable by file existence alone for every kind. `[VERIFIED]` ExportHubService.cs:103,114,124.
- The whole-DB `.db` copy at `{root}/db/` (the real recovery artifact) is written **once per root, AFTER all the per-team md loops** — if that copy throws, the md may already be written and a naive caller could think "archived." `[VERIFIED]` ExportHubService.cs:131-132.

**Why catastrophic:** the user's mental model ("markdown = I can't lose it") is FALSE for the markdown alone. The only genuinely complete, recoverable artifact is the **full `.db` snapshot**. If prune ever runs gated only on "md written," deleted exact hours / audit history are gone forever.

**Mitigations (plan MUST address):**
1. **Define the recovery artifact = the pre-prune full `.db` copy, not the markdown.** RT-03 must require a fresh `BackupService.BackupToFolderAsync({root}/db, keep)` **AND** an XC-10 `.bak` taken in the SAME prune run, both verified to exist on disk with non-zero size, BEFORE any DELETE. Markdown is the human-readable bonus, not the safety net.
2. **Verify-after-archive, not just write-after.** After writing the month's md to ≥1 root, re-stat the files (`File.Exists` + `Length > 0`) and confirm the `.db` snapshot exists; only then proceed to delete. If the `.db` snapshot for this prune run is absent, ABORT the month (no delete).
3. **Per-month md, not just per-month-bucketed-into-the-monthly-overview.** The monthly tasklist md groups by `period_month`; a spanning backlog's *in-pruned-month TimeLogs* (rows #2) won't appear in a tasklist summary at all. Add a **timesheet md scoped to the pruned month** (already producible via `ExportService.ExportMarkdownAsync(new ExportFilter(null, y, m, null, teamIds))`) so the actual deleted logs are captured per team. `[VERIFIED]` ExportHubService.cs:112.
4. State explicitly in spec: exact-hours precision and `BacklogAudit` are recoverable ONLY from the `.db` snapshot, NOT the markdown — and that this is acceptable (`[ASSUMED]` accept; confirm with user).

---

### #2 — SHOWSTOPPER · The spanning-backlog FK/orphan trap (RT-05, the named crux)
A backlog from an old `period_month` (e.g. `2026-03`) whose Tasks still receive TimeLogs in the **live window** (April/May/June). The data model guarantees this happens: `period_month` is a single static marker, TimeLogs are independent rows on any date. `[VERIFIED]` Entities.cs / BacklogRepository.cs:157.

**Two ways to get it catastrophically wrong:**
- **(a) Prune by `period_month`** → delete the `2026-03` backlog + its Tasks + ALL their TimeLogs → you just deleted **in-window April/May/June hours** that were never archived. Irreversible in-window loss.
- **(b) Delete TimeLogs by month but also delete the Tasks/Backlog** while in-window logs still reference those tasks → **FK violation** (`SqliteException`), the whole prune transaction rolls back; or if FK were off, **orphaned `task_id`** breaks every report JOIN.

**Mitigation — adopt R5a (time-axis prune), spec it precisely:**
1. **Delete on the TIME axis only:** `DELETE FROM TimeLogs WHERE work_date >= @monthStart AND work_date <= @monthEnd` (string ISO bounds), and `DELETE FROM StandupEntries WHERE work_date BETWEEN ...` (issues cascade). Use **half-open or inclusive ISO string bounds** computed once — `@monthStart='2026-03-01'`, `@monthEnd='2026-03-31'` — never a `LIKE '2026-03%'` (works but fragile) and never a date function.
2. **Delete a Backlog + its Tasks ONLY IF** the backlog has **zero remaining TimeLogs across ALL its tasks** after the month's logs are deleted **AND** its `period_month` is in a pruned month. Concretely: a backlog is deletable iff `NOT EXISTS (SELECT 1 FROM TimeLogs l JOIN Tasks t ON t.id=l.task_id WHERE t.backlog_id=@bid)` — i.e. no in-window logs survive. Otherwise **keep the backlog and its tasks**, only the old logs are dropped.
3. **Strict deletion order inside the single tx:** TimeLogs (month) → StandupEntries (month, cascades issues) → then, for each now-empty prunable backlog: Tasks → Backlog → its `BacklogTags` rows (manual delete — no FK cleans them) → its `BacklogAudit` rows. `[VERIFIED]` no-FK on BacklogTags DatabaseInitializer.cs:131-135.
4. **NEVER delete a team's DEFAULT backlog or its Tasks** even when empty — it holds recurring Annual Leave/Meeting tasks needed for future logging. Guard on `backlog_code='DEFAULT'`. `[VERIFIED]` EnsureDefaultBacklog DatabaseInitializer.cs:283-294; STATE.md "DEFAULT backlog never gets a month".
5. **Unit test the spanning backlog** with in-window logs (assert logs survive, backlog survives, no FK throw, no orphan) — RT-05/RT-07 require it.

---

### #3 — SHOWSTOPPER · Multi-machine OneDrive: prune machine A's view, destroy machine B's unsynced month
The DB lives in OneDrive and is opened by 2–5 people on different machines with file-level (not row-level) conflict handling — explicitly an **accepted-risk, no real-time sync** design. `[VERIFIED]` REQUIREMENTS.md:553-558; XC-01/XC-08.

**Failure:** Machine A starts up in month 4, prunes month 1, OneDrive uploads the smaller DB. Machine B had **unsynced month-1 edits** (added offline, or just hadn't synced down A's version). On sync, either (a) B's edits are lost when A's pruned copy wins, or (b) OneDrive creates a **conflict copy** (`timesheet-DESKTOP-B.db`) and the two diverge — now there are two DBs, one pruned one not, and a user may keep the wrong one. Worse: a **conflict copy created mid-prune** (transaction in flight) can capture a half-deleted state if the `-journal` hasn't been cleared. `[VERIFIED]` XC-08 / SqliteMaintenance.cs:11-32.

**Mitigations (plan MUST address):**
1. **Only prune months WELL past the window.** N=3 keeps {M, M-1, M-2}; prune `≤ M-3`. By the time a month is 3+ months old, the odds anyone has unsynced edits for it are negligible for a small team. Spec the buffer explicitly — do NOT prune the month that just fell out of the window the instant the calendar rolls; consider a grace period (`[ASSUMED]` e.g. prune `≤ M-4` or require the month-edge to be ≥N days past). Confirm with user.
2. **Refuse to prune if a conflict copy exists.** Call `SqliteMaintenance.FindConflictCopies(dbPath)` at the START of the prune run; if any sibling exists, **abort with a surfaced warning** ("resolve OneDrive conflict before pruning"). A prune while the DB is in a divergent state is the worst-case data destroyer. `[VERIFIED]` SqliteMaintenance.cs:11-29.
3. **Single transaction + post-commit journal verify (XC-09).** Wrap the whole prune (all months) — or each month — in ONE `BeginTransaction`/`Commit`; after commit call `IsJournalGone` and route to `IJournalWarningSink`; do NOT release the file for sync (or warn loudly) if a `-journal` lingers. `[VERIFIED]` SqliteMaintenance.cs:31; App.xaml.cs:99-103.
4. **Make startup auto-prune OFF by default (RT-01 already says so) and require an export root.** Manual, deliberate, single-operator pruning on a known-synced machine is far safer than every machine auto-pruning on every launch. `[VERIFIED]` RT-01 REQUIREMENTS.md:519-521.
5. `[ASSUMED]`/accept-risk for a 2–5 person tool: do NOT build a sync/lock engine (out of scope, REQUIREMENTS.md:557-558). The buffer + conflict-copy refusal + manual-trigger are the proportionate mitigation. Confirm acceptance with user.

---

### #4 — SHOWSTOPPER · Window math: off-by-one, local-vs-UTC month boundary, string-month edges
The prune-month selection is the trigger for all deletion; an off-by-one here deletes a live month.

**Concrete bug surfaces in THIS codebase:**
- **Local vs UTC "current month".** `IClock.Today` = `DateOnly.FromDateTime(DateTime.Now)` (LOCAL); `UtcNow` is separate. `ExportHubService.MonthsFor` already uses `_clock.Today` for month math. Prune MUST compute the window from the **same local `Today`** that `work_date` is recorded in — mixing `UtcNow.Date` would shift the boundary by up to a day near midnight / month-end and prune the wrong month. `[VERIFIED]` IClock.cs:14; ExportHubService.cs:144,178-182.
- **Inclusive `M-N` arithmetic.** With N=3 and current month M, live = {M, M-1, M-2}; first prunable = **M-3** (strictly older). Mirror `ExportHubService.AddMonths(year, month, -i)` exactly — it constructs `new DateTime(y, m, 1).AddMonths(delta)` which handles year rollover (Jan→Dec) correctly. Do NOT hand-roll `month-3` (breaks at Jan/Feb). `[VERIFIED]` ExportHubService.cs:178-182.
- **Month string bounds.** "month M" as a delete predicate must be `work_date >= 'YYYY-MM-01' AND work_date <= 'YYYY-MM-{lastDay}'`. Last day must be `DateTime.DaysInMonth(y,m)` — a `LIKE 'YYYY-MM%'` would also match but is brittle; a `< 'YYYY-(M+1)-01'` half-open bound is cleanest and avoids the 28/29/30/31 question. `[CITED]` / `[VERIFIED]` ISO string compare TimeLogRepository.cs:98.
- **Standup `work_date` is also local ISO**, same predicate applies. `[VERIFIED]` StandupRepository.cs:48-58.

**Mitigations:** centralize window math in ONE pure, fully unit-tested helper (mirror `WorkingDayCalculator` style — `[VERIFIED]` exists); reuse `ExportHubService.AddMonths`; drive it off `_clock.Today` (local); test Jan/Feb rollover, leap-Feb, year boundary, and the exact M-2/M-3 edge (M-2 survives, M-3 prunes). RT-07 lists "month selection" + "window math" as required tests.

---

### #5 — IMPORTANT · Settings/reference tables accidentally touched; idempotency drift; restore re-prune
RT-04 demands `Users/Teams/UserTeams/Tags/BacklogTags/PcaContacts/Holidays/TaskTemplates/DefaultTasks/Settings` are byte-for-byte unchanged.

**Risks:**
- **Over-broad deletes.** A `DELETE FROM Tasks WHERE ...` that forgets the DEFAULT guard, or a `DELETE FROM BacklogTags` keyed wrongly, can strip reference data. **`BacklogTags` is the sharp edge** — it has no FK, so deleting a pruned backlog's links is a MANUAL step (#2.3), but the same query must NOT touch links of surviving backlogs. `[VERIFIED]` DatabaseInitializer.cs:131-135.
- **Retention marker drift / re-appearing month.** RT-07 wants a persisted "last-pruned-through month" marker (Settings key or table). If it's stored in the **shared `Settings` table**, it syncs over OneDrive — good for idempotency across machines, but a **restore (P9) brings back an OLD marker** alongside restored data, so a restored month looks "already pruned" and is skipped even though its data is back (or the inverse: data restored, marker says not-pruned → re-prune deletes it again, this time maybe before re-archiving). `[VERIFIED]` BK-05 restore replaces whole `.db` BackupService.cs:78-100.
- **Re-run after partial crash.** If the tx is per-month and the app dies between months, the marker must reflect only fully-committed months.

**Mitigations:**
1. **Allowlist the deletable tables explicitly in code** (TimeLogs, StandupEntries[+cascade Issues], Tasks, Backlogs, BacklogTags, BacklogAudit) and assert in a test that every OTHER table's `COUNT(*)` and content hash is unchanged after a prune (RT-04 / RT-07 "settings-not-deleted" test). `[VERIFIED]` required REQUIREMENTS.md:533,545.
2. **Marker = the source of "already done," but ALSO re-check data presence.** Prune should be **idempotent by re-deriving** which months still hold business data (like `TaskListArchiveService.BackfillMissingMonthsAsync` re-scans), and treat the marker as an optimization/skip — NOT the sole authority. After a restore, a month with data older than the window will simply be re-detected and (after re-archive) re-pruned safely; a month already empty is a natural no-op. `[VERIFIED]` BackfillMissingMonthsAsync pattern TaskListArchiveService.cs:116-146.
3. **Store the marker in shared `Settings`** so multi-machine runs don't double-prune, BUT make the re-archive-before-delete unconditional so a stale marker can never cause delete-without-archive (#1 covers this).
4. **Per-month commit** so a crash leaves a consistent DB (the month either fully pruned or fully present). `[VERIFIED]` single-tx norm TimeLogRepository.cs:103-114.

---

### #6 — IMPORTANT · Interaction with reports / month views / export backfill / multi-team
After prune, old months are gone from the live DB; the rest of the app must not assume they exist.

**Risks:**
- **Reports/Task List/Daily month-selectors** on a pruned month → empty result (acceptable per RT-06) but must not crash on null/empty (e.g. divide-by-zero in roll-ups, empty-group rendering). `[VERIFIED]` RT-06 REQUIREMENTS.md:539-541.
- **Export backfill re-creating files for pruned months.** `ExportHubService.BackfillAsync` reaches back 12 months and the tasklist/standup backfills re-scan all months with data. After prune the month has **no data** → builders return `null` → no file regenerated → fine. BUT if prune deletes data AFTER an export already wrote that month's md, the md stays (good — it's the archive). Confirm prune does NOT delete the already-written archive files. `[VERIFIED]` ExportHubService.cs:99,141-157; TaskListArchiveService.cs:116-146.
- **`GetLoggedHoursByBacklogAsync` is all-time** (no date filter) — after pruning old logs, a surviving spanning backlog's "logged hours" drops to only in-window hours. That's correct post-prune but may surprise users / break a schedule calc that assumed full history. `[VERIFIED]` TimeLogRepository.cs:116-127.
- **Multi-team scope:** prune must be a **global month operation across all teams** (the calendar month is global), archiving **per team** into each team's `{root}/{Team}/db/` (P11 structure), but deleting the month's rows regardless of team. A per-team-only prune would leave other teams' old data and defeat the retention goal. `[ASSUMED]` global-month / per-team-archive; confirm. `[VERIFIED]` per-team archive surface ExportHubService.cs:83-129.

**Mitigations:** verify each month-scoped VM handles empty gracefully (UAT + a "pruned month shows empty, no crash" check); prune writes per-team md for EVERY team that had data in the month (loop teams like ExportHubService), deletes rows globally by date; never delete files under the export roots.

---

## Other notable pitfalls (below top-6 but plan should note)

- **`BacklogAudit` rows for a pruned backlog** have no FK and are not auto-cleaned; the `period_month` audit rows are what `TaskListArchiveService` reads to build the "Moved to next month" section — if you delete a backlog but keep its audit, backfill may still try to render a now-deleted backlog. Decide: delete audit with the backlog (and accept the moved-out section loses it) OR keep audit (orphan, harmless rows). `[VERIFIED]` TaskListArchiveService.cs:84-90; BacklogRepository.cs:147.
- **Disk-full / unwritable root mid-prune.** Archive write to `{root}/db` can throw; `ExportHubService` swallows per-root failures and continues — but for PRUNE, "all roots failed" must HARD-ABORT the delete (RT-03). Don't inherit the export's best-effort swallow for the safety gate. `[VERIFIED]` ExportHubService.cs:64-68.
- **Two-decimal/`REAL` float drift** is not a prune risk per se, but means md-only "recovery" can't reproduce exact stored values — reinforces #1 (full `.db` is the real artifact).
- **Startup ordering:** if auto-prune is added to `App.OnStartup`, it MUST run AFTER team bootstrap + default-task sync (needs teams/DEFAULT) and AFTER the export backfill that writes the archive it depends on — and its failure must NOT be silently swallowed like the other backfills, OR if swallowed, must never have deleted anything (archive-first guarantees this). `[VERIFIED]` App.xaml.cs:55-91.
- **`legacy_alter_table` assumption:** the Tasks→Backlogs FK rewrite on RENAME relies on SQLite default behavior; add a test asserting `PRAGMA foreign_key_list(Tasks)` points at `Backlogs` so a deletion-order plan rests on a verified FK, not an assumption. `[CITED]` SQLite docs / `[VERIFIED]` not currently asserted.

---

## Showstoppers vs accepted-risk (for the plan)

**Plan MUST address (showstoppers):**
1. Archive-is-lossy → recovery artifact = pre-prune full `.db` copy, verified on disk before any delete (#1).
2. R5a time-axis prune + "delete backlog/tasks only if no surviving logs" + DEFAULT/BacklogTags handling (#2).
3. Conflict-copy refusal + prune only `≤ M-3` (with buffer) + single tx + XC-09 journal verify (#3).
4. Window math in one tested helper off local `_clock.Today`, reusing `AddMonths`, half-open ISO bounds (#4).
5. Settings allowlist + idempotent re-derive + restore-safe marker + per-month commit (#5).

**Accepted risk for a 2–5 person tool (confirm with user, do NOT over-engineer):**
- No real-time multi-writer sync/lock engine (out of scope) — mitigated by manual trigger + ≥M-3 buffer + conflict-copy refusal (#3).
- Markdown is a human-readable summary, NOT byte-exact recovery; exact hours + audit recoverable only from the `.db` snapshot (#1).
- Off-by-default auto-prune; the safe default is manual, single-operator, on a synced machine (RT-01).

## Required tests (RT-07) — unit unless noted
- **Spanning backlog:** old `period_month`, tasks with in-window logs → in-window logs survive, backlog+tasks survive, no FK throw, no orphan (#2).
- **Archive-fail → no prune:** archive throws / `.db` snapshot absent → zero rows deleted, warning surfaced (#1, RT-03).
- **Settings untouched:** snapshot every reference table before/after → byte/row-count identical (#5, RT-04).
- **Idempotency / re-run:** prune twice → second run is a no-op (no further deletes, no errors) (#5, RT-07).
- **Window math:** M-2 survives / M-3 prunes; Jan/Feb + year rollover; leap Feb last-day bound (#4).
- **DEFAULT backlog never pruned** even when it has no in-window logs (#2.4).
- **Conflict-copy present → abort** (#3.2) — unit via `SqliteMaintenance.FindConflictCopies` seam.
- **Dry-run/verify path** reports what WOULD be deleted without deleting (RT-07).
- **UAT (mouse/integration):** select a pruned month in Reports/TaskList/Daily → empty, no crash (#6, RT-06); md + `.db` land in both `{root}/{Team}/db/`.
