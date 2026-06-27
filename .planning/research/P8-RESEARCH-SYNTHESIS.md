# P8 "Task List" — Research Synthesis (Mode B, STEP 4)

**Date:** 2026-06-27 · **Inputs:** P8-STACK / P8-FEATURE / P8-ARCHITECTURE / P8-PITFALL research docs.
**Purpose:** consolidate findings, resolve every OPEN QUESTION (decided autonomously per user's "chạy autonomous"), lock the facts STEP 5 (spec) + STEP 6 (plan) build on.

---

## A. Locked architecture facts (VERIFIED — must not regress)

1. **DI** = bare `ServiceCollection` in `App.OnStartup` (repos/services singleton, VMs transient, `WeakReferenceMessenger.Default`). New repos/services → singletons; `TaskListViewModel` → transient.
2. **Migrations** = `DatabaseInitializer`: `const long SchemaVersion` + index-positional `migrations[]` array, all in ONE transaction awaited before any window shows. **v7 = bump const 6→7 + append migration step at the matching index + create new tables `IF NOT EXISTS` in `CreateTables`.** Both the const and array index move together (R1 — highest risk; a bad step bricks startup for every OneDrive user → a v6→v7 upgrade test is the single highest-value test).
3. **Connection seam:** every repo method `using var c = _factory.Create();` (XC-01 OneDrive policy). FK: `assignee_user_id` has NO inline FK; **`pca_contact_id` follows the same no-inline-FK precedent** (safer under OneDrive divergence).
4. **Sidebar is STRING-keyed**, not index-keyed: RadioButtons → `MainViewModel.ActiveView` via `StringMatchConverter`; content panels are sibling `DockPanel`s with `StringMatchToVisibilityConverter`. Today Backlog+Reports are **sub-tabs of a "Timesheet" TabControl** (the `ActivateTabAsync` 0..4 doc comment is stale). TL-02 = add string cases (`backlog`/`reports`/`tasklist`) + sibling panels, delete the sub-tab TabControl + `OnSubTabChanged`. The `ActivateTabAsync(0..4)` index map stays valid (internal reload detail). **Keep the `timesheet` key, only relabel to "Log Work"** (preserves the default + `OnActiveViewChanged`).
5. **Live sync** = `DataChangedMessage(DataKind)` over the messenger; consumers register with a **static lambda** (weak-ref). Add new `DataKind`s: `Tags`, `PcaContacts`, `Holidays`.
6. **Patterns to mirror:** soft-delete CRUD = `IUserRepository`/`UsersViewModel` (→ PcaContacts); editor-overlay = `*EditorViewModel` + `NullToCollapsedConverter`; archive-to-file = `StandupArchiveService` (→ `TaskListArchiveService`); audit = `BacklogRepository.UpdateAsync` field-diff `LogAsync`.
7. **XC-06 hazard (R3):** logged-hours rollup join must NOT filter `is_active` (same bug family already fixed once in `TimeLogRepository`).
8. **Working-day logic is duplicated weekend-only in 3 places** (`SmartInputService.WorkingDays`, `ReportAggregator.LastNWorkingDays`, `MondayOf`); none holiday-aware.
9. **No hex-color converter exists** — invalid hex → UI-thread crash. A defensive `HexToBrushConverter` (try/parse, fallback brush) is required (R: tag chips).
10. **Backlog has no status column** — "Done backlog" must be a **derived** predicate from its tasks.

## B. Locked design decisions (from STEP 2/3) — D1..D9 in REQUIREMENTS.md. Unchanged.

---

## C. OPEN QUESTIONS — RESOLVED (autonomous, per recommendations)

| # | Question | **Decision** |
|---|---|---|
| **Q1** | "≤2 working days" endpoint | Count working days in `(today, deadline_internal]` (today exclusive, deadline inclusive) `<= 2`. |
| **Q2** | "Done backlog" derivation | Done = backlog has **≥1 active task AND every active task `Status=="Done"`**. Zero active tasks → **not Done**. |
| **Q3** | Gantt bar span + markers + no-start | Bar = `start_date → deadline_internal` (drives color = schedule state). Draw a distinct **marker for `deadline_external`** (PCA). No `start_date` → render a **faint placeholder row** with a deadline-only marker (keep visible, don't drop). Missing internal but has `end_date` → fall back `start→end`, neutral color. |
| **Q4** | Chip ordering | **System chips first** (late before warning — only one ever shows), then custom tags ordered by `Tags.id`. |
| **Q5** | Moved-out dedup / moved-back | **Current membership wins**: if a backlog is both in `period_month==M` and has a move-out audit from M, list it in the main section only. Use the latest `period_month` audit row. Only single-step forward moves need handling. |
| **A1** | TL-05 logged-hours window | **ALL-TIME** sum per backlog (no date filter) — matches TL-05 literal ("across all its tasks' TimeLogs") and makes the warning ratio `logged/estimate` correct for backlogs spanning months. Repo: `GetLoggedHoursByBacklogAsync()` → `IReadOnlyDictionary<int,decimal>` for all backlogs (NO `is_active` filter, XC-06). |
| **A2** | Holiday-set delivery to pure calculator | **No separate provider class** (simplicity). Each screen/service loads `IHolidayRepository.GetAllAsync().Select(h=>h.Date).ToHashSet()` once per reload and passes the set into the **pure** `IWorkingDayCalculator`. |
| **A3** | SmartInputService HOL-02 surface | **Overload**: keep `DistributeEven(from,to,total)`/`FillFull8h(from,to)` (delegate to empty-holiday-set, existing SI-01/02 tests stay green) + add holiday-aware overloads consuming `IReadOnlySet<DateOnly>`. The smart-fill panel loads holidays and calls the new overload (satisfies HOL-02). New tests cover the holiday path. |
| **A4** | Tag icon picker scope | Free-text **emoji / Segoe glyph** TextBox (paste/type) + a small curated emoji quick-pick row; color = hex input + predefined swatches. **No image upload.** |

---

## D. Schema v7 (final)

`Backlogs` ADD (all nullable): `deadline_internal TEXT`, `deadline_external TEXT`, `rough_estimate_hours REAL`, `official_estimate_hours REAL`, `progress_percent INTEGER`, `note TEXT`, `pca_contact_id INTEGER` (no inline FK). Reuse `assignee_user_id` = PCT.
New tables: `Tags(id PK, text, icon, color, created_at)`, `BacklogTags(backlog_id, tag_id, PK(backlog_id,tag_id))`, `PcaContacts(id PK, name, is_active DEFAULT 1)`, `Holidays(holiday_date TEXT PK, description TEXT)`.

## E. Top risks carried into planning (from Pitfall)

- **R1 (Critical)** v7 migration bricks startup → add a v6→v7 upgrade unit test; XC-10 backup-before-bulk already covers data safety; keep additive-only.
- **R2 (High)** sidebar restructure loses tab reloads (Reports' 4-call load most at risk) → route every top-level view through `OnActiveViewChanged`; manual UAT on nav.
- **R3 (High)** logged-hours join must skip `is_active` (XC-06).
- **R4 (High)** schedule math div-by-zero/null/reversed-date → pure `IScheduleStateService` that never throws; unit-test the decision table (§1.8 of feature doc).
- **R5 (Medium)** Gantt Canvas non-virtualizing + DPI → read `ActualWidth` post-layout; share the HOL-02 working-day axis so bars/chips agree.

## F. UAT-only (cannot auto-test) — flag for STEP 8a

Gantt rendering/colors/markers, holiday-calendar click toggle, tag-chip visuals + hex colors, sidebar navigation, backlog editor new fields round-trip visually. (Drag&drop already noted untested in STATE.md.)

## G. Wave plan (from Architecture §7, adopted)

W1 Data → W2 Services → W3 Settings UI → W4 Backlog editor → W5 Task List grid → W6 Gantt (after W5, shares files) → W7 Sidebar → W8 DI+startup+export-wiring. `parallelization:false` ⇒ sequential dispatch, `commit_atomic:true` ⇒ commit per task. File-overlap hotspots owned by exactly one wave each: `DatabaseInitializer.cs`/`Entities.cs`/`ReadModels.cs` (W1), `SmartInputService.cs` (W2), `SettingsTab.*` (W3), backlog-editor trio (W4), `TaskListTab.*`+`TaskListViewModel.cs` (W5→W6), `MainWindow.*`/`MainViewModel.cs` (W7), `App.xaml.cs` (W8).
