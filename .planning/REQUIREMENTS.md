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
| **P1** | Data + Schema (DB init, schema, seed, migrations, connection model) | DATA-01, DATA-02, DATA-03, DATA-04, DATA-05, DATA-06, DATA-07, XC-01, XC-08, XC-09 |
| **P2** | Services (validation, smart-input math, soft-delete, current-user, sync, report projection) | XC-02, XC-03, XC-04, XC-05, XC-06, XC-07, XC-10, SI-01, SI-02, SI-03, SI-04 |
| **P3** | Timesheet + Smart Input UI | TS-01, TS-02, TS-03, TS-04, TS-05, TS-06, TS-07, SI-05, SI-06 |
| **P4** | Requests + Users UI | REQ-01, REQ-02, REQ-03, REQ-04, USR-01, USR-02, USR-03 |
| **P5** | Reports | RPT-01, RPT-02, RPT-03, RPT-04 |
| **P6** | Settings + Export | SET-01, SET-02, SET-03, SET-04, EXP-01, EXP-02, EXP-03, EXP-04 |
| **P7** | Daily Report (Standup) — M2 | DR-01, DR-02, DR-03, DR-04, DR-05, DR-06, DR-07, DR-08, DR-09, DR-10 |
| **P8** | Task List (tracking, tags, holidays, Gantt) — M3 | TL-01, TL-02, TL-03, TL-04, TL-05, TL-06, TL-07, TL-08, TL-09, TL-10, TL-11, TAG-01, TAG-02, HOL-01, HOL-02 |
| **P9** | Local DB Backup + Restore — M5 | BK-01, BK-02, BK-03, BK-04, BK-05, BK-06, BK-07 |
| **P10** | Multi-Team (team scoping, membership, active team, multi-team view) — M4 | TM-01, TM-02, TM-03, TM-04, TM-05, TM-06, TM-07, TM-08, TM-09, TM-10 |

Total: **45 (M1)** + **10 (M2/P7)** + **15 (M3/P8)** + **7 (M5/P9)** + **10 (M4/P10)** requirements. (P11 Export / P12 Retention authored when those phases start — see `.planning/UPCOMING-FEATURES.md`.)

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

### XC-09 — Verify rollback journal is gone after each write (P1)
Statement: After each write transaction, the app verifies the `-journal` sidecar has been removed (clean commit) before the connection is considered released for OneDrive sync.
Acceptance: Following a committed write, no `<db>-journal` file remains; if one persists, the app logs/surfaces a warning rather than letting OneDrive copy a mid-transaction state. Cheap, deterministic check on the write path. (promoted from research §2.1 mitigation 3, user-approved 2026-06-21)

### XC-10 — Backup before bulk writes (P2)
Statement: Before a multi-row write (smart-input apply, DefaultTasks seed/sync), the app makes a one-shot `File.Copy` backup of the `.db`.
Acceptance: A timestamped backup copy is created immediately before the bulk transaction; single-cell edits do NOT trigger a backup (avoids OneDrive churn). On a corrupting bulk write the prior good copy survives. (promoted from research §2.1 mitigation 7, user-approved 2026-06-21)

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

## Daily Report — Standup (P7 / M2)

**Source of truth:** `docs/superpowers/specs/2026-06-25-daily-report-standup-design.md` (approved 2026-06-25).
Each REQ is atomic, testable, user-centric. New persisted data (schema v5); no change to M1 behavior.

### DR-01 — Schema v5: StandupEntries + StandupIssues (additive migration)
Statement: Migration v4→v5 adds `StandupEntries` and `StandupIssues` tables (forward-only, additive, gated on `PRAGMA user_version`).
Acceptance: Opening a v4 DB creates both tables + indexes and sets `user_version=5`; running init twice is idempotent; `StandupIssues.entry_id` FK → `StandupEntries(id)` with `ON DELETE CASCADE`. Existing M1 tables/data untouched.

### DR-02 — Standup entry persistence keyed per (user, day, section)
Statement: A standup row stores `user_id, work_date, section('yesterday'|'today'), request_id?(nullable), request_code, task_text, description, deadline?(nullable), status, order_index, created_at`.
Acceptance: Inserting/reading round-trips all fields; `request_id` is null for ad-hoc codes; `deadline` is nullable; rows are scoped and queryable by `(user_id, work_date)` and by `work_date` (team).

### DR-03 — Ad-hoc request codes (no FK, no reverse-link)
Statement: A standup row may carry a free-text `request_code` not present in `Requests`.
Acceptance: Saving a row whose code is not an existing request persists with `request_id = null` and the typed code; no join to Requests is required to read it; a later-created real request is NOT back-linked.

### DR-04 — Multiple issues per entry, each with optional solution + status
Statement: An entry has zero or more `StandupIssues`, each `issue_text` (required), `solution_text` (nullable = pending), `status ∈ {open, pending, resolved}`.
Acceptance: An entry with no issues persists with none; adding N issues round-trips N rows; deleting an entry cascades its issues; a blank solution renders as pending.

### DR-05 — Status set Todo / In-process / Done / Pending
Statement: A standup entry's `status` is one of the fixed set Todo / In-process / Done / Pending.
Acceptance: The service rejects any status outside the set; the input UI offers exactly these four.

### DR-06 — Edit-lock: own rows editable only for today + yesterday
Statement: A member may add/edit/delete only their own entries, and only when `work_date` is today or yesterday (≤1 day in the past). Older days are read-only; no backfill. Issues are exempt (anyone, anytime).
Acceptance: `CanEditDay(today)` and `CanEditDay(yesterday)` are true; any older date is false; entry add/edit/delete on a locked day is a no-op; issue add/edit/delete is allowed regardless of day or owner.

### DR-07 — Input tab: own standup with request/task picker
Statement: The Input tab shows the signed-in user's Yesterday/Today rows for a selected date and lets them add rows by picking an existing request (its tasks become pickable, deadline/status typed) or typing an ad-hoc code, plus a free-text description and issues.
Acceptance: Selecting an existing request lists its tasks; typing a new code is accepted as ad-hoc; saving creates an entry owned by the current user on the selected date; on a locked day the tab is read-only.

### DR-08 — Board tab: whole-team view for a day
Statement: The Board tab renders one card per active user for the selected date, each showing their Yesterday/Today rows + issues (read-only).
Acceptance: Every active user appears (dynamic count); a user with no rows shows empty sections; cards reflect the persisted data for that date.

### DR-09 — Weekly markdown archive, one file per week, auto-backfill on startup
Statement: A markdown snapshot per week is written to `…/Documents/TimesheetApp/StandupArchives/{yyyyMMdd}_daily.md` (stamp = week's Monday). On every app startup, any completed week (strictly before the current week) that has standup data but no archive file is generated.
Acceptance: Exporting a week writes a Mon–Fri markdown grouped by date→user→section+issues; re-export overwrites idempotently; a startup with an un-archived completed week produces its file; a week with no data produces no file.

### DR-10 — Nav + cross-tab live refresh
Statement: The "Daily Report" sidebar item is enabled (replacing the SOON placeholder) and hosts Input + Board sub-tabs; standup edits broadcast a `DataKind.Standup` change so the board refreshes live.
Acceptance: Clicking the nav shows the Daily Report view; saving in Input updates the Board without a manual reload; switching to the tab loads the selected day.

---

## Task List — tracking, tags, holidays, Gantt (P8 / M3)

**Source of truth:** STEP 2 brainstorm (this session, 2026-06-27) — design decisions confirmed by user via two rounds of questions. Spec to be authored in STEP 5 (`docs/superpowers/specs/2026-06-27-task-list-design.md`).

**Locked design decisions (do not re-litigate):**
- D1 — All tracking fields live at **Backlog level** (not per-task). Tasks keep only `status`. Logged hours & progress roll up from a backlog's tasks.
- D2 — Sidebar restructure to top-level: **Log Work · Backlog · Task List · Daily Report · Reports** (WORKSPACE) + **Users · Settings** (ADMIN). Backlog & Reports are pulled OUT of the old Timesheet sub-tab group.
- D3 — Gantt is drawn with **native WPF (Canvas)** — no charting library/NuGet dependency.
- D4 — `warning` and `late-deadline` are **system-computed** status chips (not editable). Custom tags (icon+color+text) are user-created in Settings and attach **many-to-many** to backlogs.
- D5 — PCT person-in-charge = a **User** (reuse `assignee_user_id`). PCA person-in-charge = chosen from a **PcaContacts list** managed in Settings (like Users).
- D6 — Holiday calendar = a **sub-tab in Settings**; holidays are shared in the DB.
- D7 — Monthly markdown export = **auto-backfill completed months on startup** + a manual "Export this month" button (mirrors the standup weekly archive pattern).
- D8 — Estimates (`rough`, `official`) are a **duration in hours**, not a number of days.
- D9 — Schema migration target **v7**, additive + forward-only, gated on `PRAGMA user_version` (consistent with DATA-05).

> Tags: `[ASSUMED]` marks any detail inferred beyond the user's literal request.

### TL-01 — Schema v7: backlog tracking columns + Tags / BacklogTags / PcaContacts / Holidays (additive migration)
Statement: Migration v6→v7 adds tracking columns to `Backlogs` and creates `Tags`, `BacklogTags`, `PcaContacts`, `Holidays` tables (forward-only, additive, gated on `PRAGMA user_version`).
Acceptance: Opening a v6 DB adds to `Backlogs`: `deadline_internal TEXT` (yyyy-MM-dd, nullable, PCT), `deadline_external TEXT` (nullable, PCA), `rough_estimate_hours REAL` (nullable), `official_estimate_hours REAL` (nullable), `progress_percent INTEGER` (nullable, 0–100), `note TEXT` (nullable), `pca_contact_id INTEGER` (nullable, FK→PcaContacts). Creates `Tags(id, text, icon, color, created_at)`, `BacklogTags(backlog_id, tag_id, PK(backlog_id,tag_id))`, `PcaContacts(id, name, is_active DEFAULT 1)`, `Holidays(holiday_date TEXT PK, description TEXT)`. Sets `user_version=7`; idempotent on re-run; existing M1/M2 data untouched. `assignee_user_id` (v4) is reused as the PCT assignee — not re-added.

### TL-02 — Sidebar restructure: Backlog / Task List / Reports as top-level items
Statement: "Create backlog" is moved out of the Log Work (Timesheet) group to its own top-level sidebar item, at the same level as Log Work and Task List; Reports also becomes top-level; the Task List "SOON" placeholder is replaced by the real view.
Acceptance: Sidebar WORKSPACE shows **Log Work · Backlog · Task List · Daily Report · Reports**; ADMIN shows **Users · Settings**. "Log Work" hosts only the weekly entry grid (no Backlog/Reports sub-tabs). Each top-level item activates its view and reloads its data. No timesheet/standup behavior regresses. [ASSUMED] exact label "Log Work" (was "Timesheet"/"Entry").

### TL-03 — Backlog tracking fields in the editor
Statement: The Backlog create/edit form gains: internal deadline (PCT), external deadline (PCA), rough estimate (hours), official estimate (hours), note, PCT assignee (User dropdown), PCA contact (PcaContacts dropdown), and assigned custom tags.
Acceptance: All fields persist to `Backlogs` (and `BacklogTags` for tags) and round-trip on edit; all are optional; changes are recorded in `BacklogAudit` (existing audit mechanism); the DEFAULT backlog is exempt (no tracking UI).

### TL-04 — Task List overview, scoped per month
Statement: The Task List screen shows an overview of all backlogs (with their tasks) for a selected month: backlog code, project, status, PCT/PCA assignees, internal/external deadlines, progress %, logged hours, estimate, and tag chips.
Acceptance: A month selector filters backlogs by `period_month`; each row shows the listed columns; tasks are viewable per backlog (expand); the DEFAULT backlog is excluded; switching month reloads. [ASSUMED] expandable task rows.

### TL-05 — Logged hours on backlog + estimate as duration
Statement: Each backlog shows total logged hours (sum across all its tasks' TimeLogs) and its estimate as an hours duration.
Acceptance: Logged hours = `SUM(TimeLogs.hours)` over the backlog's tasks (no `is_active` filter on the join, per XC-06); displayed alongside `official_estimate_hours` (falling back to rough when official is null). Whole hours render without decimals.

### TL-06 — Manual progress % input
Statement: Progress is a user-entered percentage (0–100) on the backlog.
Acceptance: Editing the field persists `progress_percent`; values outside 0–100 are rejected; null renders as not-set (not 0%). [ASSUMED] this manual % is independent of the auto schedule-warning (TL-07).

### TL-07 — Auto "warning" (behind-schedule) status chip
Statement: A `warning` chip is shown when a backlog is within ≤2 working days of its internal (PCT) deadline AND is behind schedule, where behind = work-done fraction < time-elapsed fraction.
Acceptance: Behind = `loggedHours / estimateHours < workingDaysElapsed(start, today) / workingDaysTotal(start, internalDeadline)` (**standard done%<elapsed% formula — confirmed by user 2026-06-27**, supersedes the literal request which compared against the remaining-time fraction); "≤2 working days" counts working days (excludes weekends + Holidays); chip not shown when no start/deadline/estimate, when done, or when already past deadline (TL-08 takes over). Internal (PCT) deadline drives the check.

### TL-08 — Auto "late deadline" red status chip
Statement: A red `late deadline` chip is shown when today is past the internal (PCT) deadline and the backlog is not Done.
Acceptance: `today > deadline_internal` AND backlog not in a done state → red chip; a Done backlog never shows it; takes precedence over the `warning` chip.

### TL-09 — Monthly markdown overview export (auto-backfill + manual)
Statement: A markdown overview per month is written to `…/Documents/TimesheetApp/TaskListArchives/{yyyyMM}_tasklist.md`; on startup every completed month (strictly before the current month) with backlog data but no archive file is generated; a manual "Export this month" button also exists.
Acceptance: The file lists each backlog in that `period_month` with status, assignees, deadlines, estimate, logged hours, progress, and tags; it **also** includes backlogs that were moved out of that month to the next (read from `BacklogAudit` `period_month` changes) under a "Moved to next month" section; re-export overwrites idempotently; a month with no data produces no file. [ASSUMED] archive dir/filename pattern mirrors standup.

### TL-10 — Grid ↔ Gantt view toggle with expand/collapse
Statement: The Task List offers two views — a Grid and a Gantt timeline (start → deadline) — with an expand/collapse control for the chart area.
Acceptance: A toggle switches Grid ↔ Gantt without losing the selected month; the Gantt is rendered in a WPF Canvas with one horizontal bar per backlog spanning start→deadline across working days (weekends + Holidays visually skipped/marked), colored by schedule state (normal / warning / late); the chart area can be collapsed and expanded.

### TL-11 — Manage PCA contacts list in Settings
Statement: Settings lets the user add/edit/deactivate PCA contacts, which populate the PCA-assignee dropdown on backlogs.
Acceptance: PcaContacts CRUD persists; a deactivated contact is hidden from the dropdown but still resolves its name on existing backlogs (soft-delete semantics, like Users).

### TAG-01 — Create/manage custom tags in Settings
Statement: Settings has a section to create/edit/delete custom tags, each with an icon, a color, and text content.
Acceptance: Tag CRUD persists to `Tags`; a tag carries `icon` (glyph/emoji), `color` (hex), and `text`; tags are selectable when editing a backlog (TL-03); deleting a tag removes its `BacklogTags` links. [ASSUMED] icon = an emoji/Segoe glyph picker (no image upload).

### TAG-02 — Render tag chips (custom + system) on backlog & Task List
Statement: A backlog displays its assigned custom tags plus any active system chips (`warning`, `late deadline`) as chips showing the configured icon, color, and text.
Acceptance: Custom tags render with their `icon`+`color`+`text`; system chips render with fixed styling (amber warning, red late); chips appear on both the Backlog list and the Task List overview/Gantt.

### HOL-01 — Holiday calendar sub-tab in Settings
Statement: Settings has a calendar sub-tab showing a month where the user clicks a day to mark/unmark it as a holiday (non-working day).
Acceptance: Marking a day inserts a `Holidays` row; unmarking deletes it; the calendar reflects current holidays and weekends; an optional description per holiday persists. Holidays are shared via the DB.

### HOL-02 — Holidays excluded from all working-day calculations
Statement: Marked holidays are excluded from working-day computations everywhere — alongside Sat/Sun.
Acceptance: A shared working-day helper treats Sat/Sun **and** any `Holidays` date as non-working; it is used by smart-input distribution (SI-01/SI-02), the schedule-warning math (TL-07), the "≤2 working days" window, and the Gantt day axis (TL-10). [ASSUMED] smart-input now consults Holidays (extends SI-02 which previously excluded weekends only).

---

## Local DB Backup + Restore (P9 / M5)

**Source of truth:** STEP 2 brainstorm (2026-06-27, confirmed by user). Distinct from the existing **XC-10** `DbBackupHelper` (auto `.bak` next to the DB before bulk writes — that stays). P9 is a deliberate, user-controlled backup to a **local folder the user chooses, ideally outside OneDrive**, with restore. No DB schema change (file-level feature).

**Locked decisions:** manual button **+** scheduled auto-backup; **restore included**; user **chooses the backup folder** (no silent default); backup = **full `.db` file only** (timestamped); keep last N (configurable). `[ASSUMED]` marks inferred detail.

### BK-01 — User-chosen backup folder (Settings + persisted app-local)
Statement: The user selects the local backup folder via a Browse picker in Settings; the path persists app-locally (like the DB path, per DATA-07).
Acceptance: Choosing a folder persists it to `%APPDATA%\TimesheetApp\appsettings.json`; until a folder is set, backup actions are disabled with a clear "choose a folder" hint; the chosen path survives restart. `[ASSUMED]` storage in the same app-local config as DbPath/ArchivePath.

### BK-02 — Manual "Backup now"
Statement: A "Backup now" action copies the full live `.db` to the chosen folder as a timestamped file.
Acceptance: Pressing it writes `timesheet_{yyyyMMddHHmmss}.db` (or similar) into the backup folder and reports success/failure with the resulting path; a no-op with a message if the DB file is missing or no folder is set. The copy is consistent (no open transaction — app uses short connections + `journal_mode=DELETE`, so an idle-time `File.Copy` is safe).

### BK-03 — Scheduled auto-backup (toggle)
Statement: When enabled, the app makes at most one automatic backup per day on startup if none exists for the current day.
Acceptance: With auto-backup ON and no backup dated today in the folder, startup creates one (non-fatal, before the window is interactive, mirroring the standup/tasklist backfill pattern); with it OFF, none is made; a setting persists the toggle. `[ASSUMED]` once-per-day-on-startup cadence (vs interval timer).

### BK-04 — Backup listing
Statement: Settings lists existing backups in the folder with name, timestamp, and size, newest first.
Acceptance: The list reflects the folder contents (timestamp parsed from filename or file metadata), refreshes after a backup/restore/prune, and shows an empty state when none exist.

### BK-05 — Restore from a backup
Statement: The user can select a backup and restore it, replacing the current `.db`, with confirmation and a pre-restore safety copy of the current DB.
Acceptance: Restore prompts for confirmation; before overwriting it makes a safety copy of the current `.db` (so a wrong restore is reversible); it replaces the live `.db` with the chosen backup; afterwards it instructs the user to **restart the app** (and/or restarts) so no stale in-memory/connection state remains. Restore is blocked with a message if a backup file is unreadable. `[ASSUMED]` restart-after-restore rather than live hot-swap (safest given OneDrive + open handles).

### BK-06 — Retention (keep last N)
Statement: The backup folder is pruned to the newest N backups, N configurable.
Acceptance: After a successful backup, older backups beyond N are deleted (best-effort, never failing the backup that just succeeded — mirrors `DbBackupHelper.PruneOldBackups`); N persists and defaults to a sensible value (e.g. 30); pruning only affects this app's own backup files (matched by name pattern), never unrelated files in the folder. `[ASSUMED]` default N=30.

### BK-07 — Settings "Backup & Restore" section
Statement: A Settings section hosts the folder picker, auto-backup toggle, retention N, "Backup now" button, and the backups list with a per-row Restore action.
Acceptance: All controls are present and wired; status messages surface success/failure; the section follows the existing Settings styling/overlay conventions.

---

## Multi-Team (P10 / M4)

**Source of truth:** STEP 2 brainstorm (2026-06-27), locked decisions in `.planning/UPCOMING-FEATURES.md` (P10). Schema **v7→v8** additive. Builds on P8 (Task List) + P9 (backup). **Execution PAUSES for user plan approval** (schema-wide). `[ASSUMED]` marks inferred detail.

**Locked decisions:** Team = new top-level entity; Project enum unchanged; User↔Team many-to-many; backlog-scoped (Standup also `team_id`); Users/Tags/Holidays/PCA/Templates/DefaultTasks global; DEFAULT backlog per team; ONE active team for editing + multi-team checkbox view on Backlog/TaskList/Reports/DailyBoard; migrate existing data into team "Architect Improvement"; first-run setup + validated defaults.

### TM-01 — Schema v8: Teams + UserTeams + team_id (additive migration)
Statement: Migration v7→v8 adds `Teams(id, name, is_active DEFAULT 1, created_at)`, `UserTeams(user_id, team_id, PK(user_id,team_id))`, `team_id` on `Backlogs` and `StandupEntries` (no inline FK, per OneDrive precedent), gated on `PRAGMA user_version`.
Acceptance: Opening a v7 DB creates the tables/columns, sets user_version=8, idempotent; existing M1/M2/M3 data untouched; backward-compatible.

### TM-02 — Data migration: assign existing data to "Architect Improvement"
Statement: The v8 migration (or a one-time bootstrap) creates a team named "Architect Improvement", assigns every existing non-DEFAULT backlog, every StandupEntry, and every existing user (UserTeams) to it; the existing single DEFAULT backlog becomes that team's DEFAULT.
Acceptance: After upgrade, all prior backlogs/standup carry that team_id; all users are members; the team is the active team; no orphaned business data. [ASSUMED] team name fixed to "Architect Improvement" per user.

### TM-03 — Team entity CRUD + membership (Settings)
Statement: Settings lets the user create/rename/deactivate teams and assign/unassign users to teams (many-to-many).
Acceptance: Team CRUD persists; membership add/remove persists in UserTeams; a user may belong to ≥2 teams; deactivating a team hides it from switchers/filters but preserves its data; broadcasts a `DataKind.Teams` change.

### TM-04 — DEFAULT backlog per team + DefaultTasks sync per team
Statement: Each team has its own hidden DEFAULT backlog; DefaultTasks materialize as tasks under every team's DEFAULT backlog.
Acceptance: Creating a team creates its DEFAULT backlog (idempotent); DefaultTaskSync seeds/updates default tasks under each team's DEFAULT; Annual Leave/Meeting hours log under the active team's DEFAULT and attribute to that team in reports.

### TM-05 — Active team selection + sidebar switcher (persisted)
Statement: A single "active team" is selectable from the user's teams via a sidebar switcher; new data (timesheet logs, backlogs, standup) is created under it; the choice persists.
Acceptance: The switcher lists the current user's active teams; changing it reloads team-scoped views; the active team persists app-locally (per machine/user) across restarts; defaults to the migrated/first team. [ASSUMED] persisted in app-local config.

### TM-06 — Working scope = active team
Statement: Timesheet entry, backlog creation, and standup creation are scoped to the active team.
Acceptance: The Timesheet grid shows only the active team's tasks (incl. its DEFAULT); a new backlog is created with team_id = active team; a new standup entry carries the active team. Switching team switches the working set.

### TM-07 — Multi-team display filter (checkbox) on view screens
Statement: Backlog list, Task List, Reports, and Daily Report board offer a multi-select checkbox of teams (from the user's teams) to display aggregated data across the checked teams.
Acceptance: Checking multiple teams shows their combined rows/cards; each screen indicates team per row/card where ambiguous; default selection = the active team; selection persists per screen for the session. [ASSUMED] default = active team only.

### TM-08 — Team-aware reports & exports
Statement: Reports and markdown/Excel exports are team-aware (filter/group by the selected team(s); identify team in output).
Acceptance: Reports reflect the checked teams; exports include a team grouping/column or filter; per-team Annual Leave (TM-04) is separable. [ASSUMED] team grouping in markdown header.

### TM-09 — First-run setup + validated defaults
Statement: On a fresh DB (no teams), a first-run setup creates an initial team, sets it active, seeds its DEFAULT + default tasks, and applies validated default values for all settings.
Acceptance: A brand-new DB ends with exactly one active team, a working DEFAULT, seeded default tasks, and sane defaults (N-days warning=3, backup off, retention defaults, etc.); the app is immediately usable; no null/invalid setting state. [ASSUMED] first-run creates a default-named team the user can rename.

### TM-10 — No regression to team-agnostic features
Statement: Existing M1/M2/M3 behavior continues to work under the team model.
Acceptance: Smart-fill, validation, Daily Report, Task List chips/Gantt, backup all function; all prior tests pass; queries that gained a team dimension still return correct results for the active/checked teams.

---

## Out of Scope (v1) — no REQ-IDs (spec §9 + research)

- Authentication / password login.
- Multi-tenant / cloud database.
- Mobile / cross-platform (Mac).
- Real-time sync / concurrent-write conflict resolution (file-level OneDrive conflict handled by users, accepted risk).
- Notification system (email/Teams alerts).
- Request soft-delete (only Task/User soft-delete in v1 — decision 4).
- True offline multi-writer reconciliation (GUID/ULID + sync engine).
- Advisory edit-lock: **deferred** (user decision 2026-06-21) — the lock itself rides OneDrive sync and can be stale exactly when needed; XC-08 conflict-copy detection covers the dominant failure mode. Revisit only if UAT shows frequent collisions.

---

## Open Questions Blocking Architecture

None. All 8 research open questions were resolved by the user (spec Appendix "Resolved decisions after research") and are encoded above as firm REQs. Architecture (STEP 5 architecture-lead) may proceed.

Resolution (2026-06-21): architecture lead recommended and **user approved** promoting "verify `-journal` gone" → **XC-09** and "backup before bulk writes" → **XC-10**. The advisory single-editor lock remains **deferred** (see Out-of-Scope). No open questions remain.
