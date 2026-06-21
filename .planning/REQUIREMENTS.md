# REQUIREMENTS — WPF Desktop Timesheet Tool

**Date:** 2026-06-21
**Phase:** STEP 5 (Discovery / Spec — Mode B)
**Source of truth:** `docs/superpowers/specs/2026-06-21-timesheet-tool-design.md` (approved) + `.planning/research/RESEARCH-SYNTHESIS.md`
**Status:** Locked for architecture

> Scope: internal tool for a 2–5 person team. Requirements are intentionally tight — no speculative/enterprise behavior. Each REQ is atomic, testable, and user-centric. The 8 resolved decisions (spec Appendix "Resolved decisions after research") are honored verbatim and are NOT reopened.
>
> Tags: `[ASSUMED]` marks any requirement (or acceptance detail) inferred beyond the literal spec text.

---

## Phase Mapping (100% coverage)

| Phase | Theme | REQ-IDs |
|---|---|---|
| **P1** | Data + Schema (DB init, schema, seed, migrations, connection model) | DATA-01, DATA-02, DATA-03, DATA-04, DATA-05, DATA-06, DATA-07, XC-01, XC-08 |
| **P2** | Services (validation, smart-input math, soft-delete, current-user, sync, report projection) | XC-02, XC-03, XC-04, XC-05, XC-06, XC-07, SI-01, SI-02, SI-03, SI-04 |
| **P3** | Timesheet + Smart Input UI | TS-01, TS-02, TS-03, TS-04, TS-05, TS-06, TS-07, SI-05, SI-06 |
| **P4** | Requests + Users UI | REQ-01, REQ-02, REQ-03, REQ-04, USR-01, USR-02, USR-03 |
| **P5** | Reports | RPT-01, RPT-02, RPT-03, RPT-04 |
| **P6** | Settings + Export | SET-01, SET-02, SET-03, SET-04, EXP-01, EXP-02, EXP-03, EXP-04 |

Total: **43 requirements**, every one mapped to exactly one phase.

---

## Data / Schema (P1)

### DATA-01 — Database file auto-create + schema bootstrap
Statement: On startup the app creates the `.db` file (if missing) and all tables idempotently.
Acceptance: First launch on a fresh path produces a valid `.db` with `Users`, `Requests`, `Tasks`, `TaskTemplates`, `TimeLogs`, `DefaultTasks`, `Settings`. Running bootstrap twice creates no duplicate tables/rows. `CREATE TABLE IF NOT EXISTS` used; wrapped in one transaction.

### DATA-02 — Core schema matches spec §3
Statement: Tables and columns exactly match the resolved schema in spec §3.1–§3.2.
Acceptance: `Users` has `windows_username` (nullable); `TimeLogs` has single FK `task_id`, `hours REAL`, and `UNIQUE(user_id, task_id, work_date)`; `Tasks.request_id` FK → `Requests`; soft-delete `is_active` columns present on `Users`, `Tasks`, `DefaultTasks`. Requests has **no** `is_active` column (decision 4).

### DATA-03 — Hidden DEFAULT request + DefaultTasks unification
Statement: A hidden Request (`request_code='DEFAULT'`, `project='DEFAULT'`) exists, and each active DefaultTask is materialized as a Task under it.
Acceptance: After init, exactly one DEFAULT request exists (idempotent). Each active `DefaultTasks` row has a matching `Task` under the DEFAULT request keyed by `task_name`. Every TimeLog (normal or Annual Leave/Meeting) points to a `Tasks.id` — one FK only.

### DATA-04 — DefaultTasks seeded only when empty
Statement: Seed default tasks (Annual Leave, Meeting, Other…) only if the `DefaultTasks` table is empty.
Acceptance: On a DB where a user has renamed/hidden defaults, relaunch does NOT re-insert or overwrite them. Fresh DB gets the seed set. [ASSUMED] (research §1.7, beyond literal spec)

### DATA-05 — Versioned migrations via `PRAGMA user_version`
Statement: Schema version is tracked with `PRAGMA user_version`; pending additive migrations run forward-only.
Acceptance: Startup reads `user_version`; if `< target`, runs pending step scripts in a transaction then sets `user_version=target`. Additive `ALTER TABLE … ADD COLUMN` guarded so an old client opening a newer DB still works (backward-compatible). [ASSUMED] (research §1.6)

### DATA-06 — Foreign-key enforcement on every connection
Statement: Foreign keys are enforced for every connection.
Acceptance: Every opened connection has FK enforcement ON (`Foreign Keys=True` in connection string or `PRAGMA foreign_keys=ON`). A test inserting a TimeLog with a non-existent `task_id` is rejected.

### DATA-07 — Settings key-value store + locality split
Statement: A `Settings(key,value)` table backs shared settings; the DB **path** is stored app-locally, not in the shared DB.
Acceptance: N-days warning, TaskTemplates, DefaultTasks, and windows_username identity persist in the shared DB. The DB file path persists in app-local config (`%APPDATA%\TimesheetApp\appsettings.json`) to avoid the chicken-and-egg of reading the path from the DB it points to. [ASSUMED] locality detail (research §4.2)

---

## Cross-cutting (validation, soft-delete, current-user, concurrency) — P1/P2

### XC-01 — `journal_mode=DELETE` + OneDrive-safe connection policy (P1)
Statement: The app uses rollback-journal mode (not WAL) and short open→work→close connections.
Acceptance: `PRAGMA journal_mode=DELETE` set; `Pooling=False`; no long-lived/static open connection; each repository op opens and disposes its own connection via `IConnectionFactory.Create()`. Verifiable: no `-wal`/`-shm` sidecar is ever created. (decision: journal_mode=DELETE)

### XC-02 — Per-cell 8h immediate validation (P2)
Statement: A single cell value is rejected at entry if it alone exceeds 8h, with immediate red feedback.
Acceptance: Entering `>8` in one cell shows a red error and the value is not committed. (decision 5: per-cell red immediately)

### XC-03 — Per-save whole-day 8h validation (P2)
Statement: A save is blocked if the sum of all task hours for a user on a given date exceeds 8h.
Acceptance: Service sums all logs for `(user, date)` after merge; if `>8`, the save is rejected with a per-day message and **nothing is written**. Validation reads the day's other logs from storage (not purely in-memory). (decision 5: per-save day total)

### XC-04 — Hours precision: positive, ≤1 decimal, REAL, rounded before upsert
Statement: Hours must be `>0` with at most 1 decimal place; stored as REAL and rounded to 1 decimal before upsert.
Acceptance: `0`, negatives, and values like `2.55` are rejected (or rounded per rule) before write; `TimeLogService` applies `Round(v,1,AwayFromZero)` before upsert; whole numbers persist cleanly. (decision 8: REAL + round 1 decimal)

### XC-05 — Weekday-only entry (Mon–Fri)
Statement: Time can only be logged for Monday–Friday; Sat/Sun are never enterable. (P2 rule; surfaced in P3 UI)
Acceptance: Service rejects any `work_date` on Sat/Sun; the Timesheet grid has no Sat/Sun columns. Week start hard-coded to Monday (not culture-derived).

### XC-06 — Soft-delete only; reports never filter `is_active` on joins (P2)
Statement: Users/Tasks are soft-deleted (never hard-deleted when TimeLogs exist); report/export joins resolve names regardless of `is_active`.
Acceptance: Soft-deleting a User/Task sets `is_active=0` and hides it from grids/dropdowns but preserves all TimeLogs. Report/export queries `INNER JOIN` by id with **no** `is_active` filter, so soft-deleted tasks (including DEFAULT Annual Leave) still render their names. Service refuses hard-DELETE of any User/Task with TimeLogs.

### XC-07 — Current-user resolution via Windows username (P2)
Statement: On launch the app maps `Environment.UserName` to `Users.windows_username`; on no match it prompts once and persists the mapping.
Acceptance: Match → current user set and name shown top-corner. No match → `CurrentUserService` returns `NeedsSelection`; VM opens `SelectUserDialog`; choice is persisted to `Users.windows_username` and not asked again. Service returns an outcome enum and never opens the dialog itself.

### XC-08 — Conflict-copy detection on startup (P1)
Statement: On startup the app scans the DB folder for OneDrive conflict-copy siblings and alerts the user.
Acceptance: When a `*-<MACHINE>.db` sibling exists next to the DB, a visible warning is shown at startup. [ASSUMED] (research §2.1 mitigation 6 — mandatory mitigation, not in literal spec)

---

## Smart Input — math (P2) + UI (P3)

### SI-01 — Even-distribution math (integer-tenths) (P2)
Statement: "Chia đều" splits total hours evenly across working days using integer-tenths arithmetic; remainder lands on the last working day.
Acceptance: `10h / 3 days → 3.3, 3.3, 3.4`; parts always sum exactly to total (no float drift). Exact-divide, single-day, and large-remainder cases pass property test.

### SI-02 — Working-day enumeration excludes weekends (P2)
Statement: Smart input counts only Mon–Fri days in the date range.
Acceptance: A range spanning a weekend distributes only across weekdays; Sat/Sun receive nothing. Culture-independent `DayOfWeek` checks.

### SI-03 — Zero-working-day + bad-input guard (P2)
Statement: A range with no working days (all weekend, or From > To) or invalid total is a safe no-op with a clear message.
Acceptance: `nDays==0` never divides by zero — returns no-op + message. Totals with `>1` decimal are rejected before rounding silently changes them. [ASSUMED] precision-reject detail (research §2.4)

### SI-04 — Full-8h mode (P2)
Statement: "Full 8h" fills 8h into every working day in the range.
Acceptance: Each Mon–Fri day in range = 8h; weekends skipped.

### SI-05 — Smart input overwrites existing cells, validated in preview (P3)
Statement: Applying smart input **overwrites** (upserts) target cells, and the post-merge day total is validated against 8h in the preview before save.
Acceptance: Target cells are set (not added to) per `UNIQUE(user,task,date)`. Preview shows resulting cells; if any day's post-merge total `>8`, the whole apply is rejected with a per-day breakdown and nothing is saved. Apply is atomic (single transaction — all-or-nothing). (decision 2: overwrite)

### SI-06 — Smart Input panel UI (two modes + preview) (P3)
Statement: A panel below the grid offers Mode 1 (Chia đều: Task, From, To, Total) and Mode 2 (Full 8h: Task, From, To), each producing a preview before save.
Acceptance: Both modes render their inputs; pressing the action button shows a preview the user must confirm before any write.

---

## Timesheet tab (P3)

### TS-01 — Weekly Mon–Fri grid with Prev/Next navigation
Statement: The Timesheet tab shows a Mon–Fri grid for the current week with Prev/Next week navigation.
Acceptance: 5 day columns (no Sat/Sun); Prev/Next shifts the week; column headers show concrete dates (e.g. `Tue 17/06`) recomputed on navigation.

### TS-02 — Rows = active Tasks (DefaultTasks + active Requests' Tasks)
Statement: Grid rows are the active Tasks: DEFAULT-request tasks plus tasks from active Requests.
Acceptance: Soft-deleted tasks do not appear as rows; DEFAULT tasks (Annual Leave, Meeting, …) appear alongside request tasks.

### TS-03 — Inline cell edit with empty = 0
Statement: Each cell is inline-editable; an empty cell means 0 hours.
Acceptance: Editing a cell commits hours; clearing a cell deletes the underlying log (empty = 0, no zero-row persisted).

### TS-04 — Per-cell red warning on invalid/over-limit entry
Statement: An invalid or over-limit cell shows a red visual error.
Acceptance: `INotifyDataErrorInfo`-driven red border on `>8`, negative, or `>1`-decimal entry. (ties to XC-02/XC-04)

### TS-05 — Per-column day totals in footer
Statement: A footer shows the total hours per day column.
Acceptance: Below the grid, a single-row totals strip shows Mon..Fri totals, updating live as cells change.

### TS-06 — Save blocked when any day exceeds 8h
Statement: The Save action is disabled/blocked while any day column total exceeds 8h.
Acceptance: `SaveCommand.CanExecute` is false when any column total `>8`; re-evaluated on each cell change. (ties to XC-03)

### TS-07 — Upsert/delete persistence on the natural key
Statement: Saving a cell upserts on `(user_id, task_id, work_date)`; clearing deletes.
Acceptance: Re-entering a value updates the same row (no duplicate); empty cell removes the row. Idempotent on the natural key, not the surrogate id.

---

## Requests tab (P4)

### REQ-01 — Request list with search
Statement: The Requests tab lists requests with search by `request_code` or project name.
Acceptance: Typing a code/project substring filters the visible list.

### REQ-02 — Create request with optional template
Statement: Creating a request takes `request_code` + project, optionally applies a template to auto-generate tasks, and allows custom add/remove/reorder before save.
Acceptance: Saving creates the Request and its Tasks (template-generated and/or custom), with `order_index` reflecting the chosen order.

### REQ-03 — Edit request (name + add task)
Statement: An existing request's project/name can be edited and tasks added.
Acceptance: Edits persist; new tasks appear in the Timesheet grid for active requests.

### REQ-04 — Soft-delete task (not request) in v1
Statement: A Task can be soft-deleted (hidden from Timesheet, logs preserved); Requests are NOT soft-deletable in v1.
Acceptance: Soft-deleting a task sets `is_active=0` and removes it from the grid while keeping its TimeLogs. No UI/affordance to delete a Request (no `is_active` on Requests). (decision 4)

---

## Users tab (P4)

### USR-01 — User list with Active/Inactive status
Statement: The Users tab lists users with name and Active/Inactive status.
Acceptance: Both active and inactive users are visible with their status indicated.

### USR-02 — Add user
Statement: A new user is added by entering a name.
Acceptance: Saving inserts a User (`is_active=1`); the user becomes selectable.

### USR-03 — Soft-delete user
Statement: A user can be soft-deleted (set inactive), preserving all their TimeLogs.
Acceptance: Soft-delete sets `is_active=0`, removes the user from selection dropdowns, and keeps all TimeLogs intact and exportable.

---

## Reports tab (P5)

### RPT-01 — Weekly view (totals by day)
Statement: Select user + week → table of total hours by day.
Acceptance: Choosing a user and week renders per-day totals for that week.

### RPT-02 — Monthly view (totals by request/task)
Statement: Select user + month → table of total hours grouped by request/task.
Acceptance: Choosing a user and month renders totals grouped by request and task.

### RPT-03 — Drill-down Project → Request → Task → Date
Statement: A hierarchical drill-down exposes hours at Project → Request → Task → Date.
Acceptance: A tree (or equivalent) expands Project to Request to Task to per-date hours; soft-deleted task names still resolve (no `is_active` filter on joins).

### RPT-04 — "Chưa log" banner using configured N (includes today)
Statement: A banner warns when an active user has zero logs in the last N working days, where the window includes today, and the banner displays the configured N.
Acceptance: Scanning active users, any with no logs in `LastNWorkingDays(today, N)` (today included, weekends excluded) are flagged with `"[Name] chưa log trong N ngày"` where the number shown is the configured N (not the actual gap). (decisions 3 & 6)

---

## Settings tab (P6)

### SET-01 — Database path with Browse
Statement: Settings lets the user view/change the `.db` path with a Browse button.
Acceptance: Path is editable via a file picker and persisted to app-local config (per DATA-07); a consistent canonical path is used from each machine.

### SET-02 — N-days "chưa log" warning (default 3)
Statement: Settings lets the user set the N-days warning window, defaulting to 3.
Acceptance: Changing N persists to the shared `Settings` table and drives RPT-04; default value is 3.

### SET-03 — Manage TaskTemplates
Statement: Settings lets the user add/edit/delete TaskTemplates and their task lists.
Acceptance: Template CRUD persists; templates are selectable when creating a Request (REQ-02).

### SET-04 — Manage DefaultTasks with sync (rename = soft-delete + insert)
Statement: Settings lets the user add/edit/hide DefaultTasks, synced into the DEFAULT request's Tasks; a rename is treated as soft-delete of the old + insert of the new (TimeLogs preserved).
Acceptance: Add → new active Task under DEFAULT; hide → soft-delete that Task; rename → old name's Task soft-deleted and a new Task inserted, with the old name's TimeLogs preserved and still exportable. (decision 7)

---

## Export (P6)

### EXP-01 — Excel export (ClosedXML) compatible with legacy format
Statement: Export to `.xlsx` in a format compatible with the prior Excel file, filterable by user, month, and project.
Acceptance: A generated workbook opens and matches the legacy layout for the chosen user/month/project filters; built with ClosedXML.

### EXP-02 — Markdown export structure
Statement: Export to Markdown grouped `# Timesheet — yyyy/MM` → `## {user}` → `### {request_code} — {project}` → `| Date | Task | Hours |` table.
Acceptance: Output matches spec §6.2 structure; real requests render `### {code} — {project}`.

### EXP-03 — DEFAULT markdown header grouped by task name
Statement: DEFAULT-request entries are sub-grouped by task name so the header reads e.g. `### DEFAULT — Annual Leave` (not `### DEFAULT — DEFAULT`).
Acceptance: A DEFAULT Annual Leave log renders under `### DEFAULT — Annual Leave`; multiple DEFAULT task names produce separate sub-headers. (decision 1)

### EXP-04 — Markdown hours/escaping formatting
Statement: Hours render as integer when whole (`4` not `4.0`) else 1 decimal (`3.5`); `|` in task names is escaped.
Acceptance: Whole hours show no decimal; fractional show 1 decimal; a task name containing `|` is escaped (`\|`) so the table stays valid. [ASSUMED] formatting detail (research §4.1, refines spec example)

---

## Out of Scope (v1) — no REQ-IDs (spec §9 + research)

- Authentication / password login.
- Multi-tenant / cloud database.
- Mobile / cross-platform (Mac).
- Real-time sync / concurrent-write conflict resolution (file-level OneDrive conflict handled by users, accepted risk).
- Notification system (email/Teams alerts).
- Request soft-delete (only Task/User soft-delete in v1 — decision 4).
- True offline multi-writer reconciliation (GUID/ULID + sync engine).
- Advisory edit-lock is **listed as a research mitigation but is NOT a v1 requirement** unless promoted later; not included above to avoid speculative scope.

---

## Open Questions Blocking Architecture

None. All 8 research open questions were resolved by the user (spec Appendix "Resolved decisions after research") and are encoded above as firm REQs. Architecture (STEP 5 architecture-lead) may proceed.

[ASSUMED] note: The advisory single-editor lock (research §2.1 mitigation 4) and "verify `-journal` gone after each write" (mitigation 3) and "backup before bulk writes" (mitigation 7) are recommended mitigations not captured as v1 REQs. If the architecture lead wants them enforced in v1, they should be promoted to REQs before planning — flagged here rather than silently added.
