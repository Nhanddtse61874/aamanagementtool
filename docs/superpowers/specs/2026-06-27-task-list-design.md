# Task List (M3 / P8) — Design Spec

**Status:** Approved direction (STEP 2/3), research-backed (STEP 4). Authored STEP 5, 2026-06-27.
**Mode:** B · **Schema:** v6 → **v7** (additive, forward-only).
**Source of truth:** this doc + `.planning/REQUIREMENTS.md` (TL-01..11, TAG-01/02, HOL-01/02, D1..D9) + `.planning/research/P8-RESEARCH-SYNTHESIS.md` (resolved Q1..Q5, A1..A4).
**Inherited rules honored:** XC-01 (OneDrive conn policy), XC-06 (no `is_active` on report joins), XC-09 (journal clean), XC-10 (backup before bulk), DATA-05 (versioned migrations).

---

## 1. Overview

Add a **Task List** capability: a per-month overview of all backlogs with tracking metadata (dual deadlines, dual estimates, dual persons-in-charge, manual progress %, note), logged-hours roll-up, auto schedule chips (warning / late), user-defined tags, a Grid↔Gantt view, a monthly markdown archive, and holiday-aware working-day math. The sidebar is restructured so **Backlog**, **Task List**, and **Reports** become top-level items (siblings of **Log Work** and **Daily Report**).

All tracking lives at the **Backlog level** (D1); tasks keep only `Status`. Logged hours and the schedule chips roll up from a backlog's tasks.

## 2. Data model (schema v7)

### 2.1 `Backlogs` — add columns (all nullable)
| Column | Type | Meaning |
|---|---|---|
| `deadline_internal` | TEXT `yyyy-MM-dd` | PCT (internal) deadline — drives chips & Gantt |
| `deadline_external` | TEXT `yyyy-MM-dd` | PCA (external) deadline — Gantt marker only |
| `rough_estimate_hours` | REAL | rough estimate (duration, hours) |
| `official_estimate_hours` | REAL | official estimate (duration, hours) |
| `progress_percent` | INTEGER | manual 0–100, null = not set |
| `note` | TEXT | free text |
| `pca_contact_id` | INTEGER | FK→PcaContacts (no inline FK constraint, mirrors `assignee_user_id`) |

`assignee_user_id` (v4) is **reused** as the PCT person-in-charge. The DEFAULT backlog is exempt from all tracking UI.

### 2.2 New tables
```sql
CREATE TABLE IF NOT EXISTS Tags (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  text TEXT NOT NULL, icon TEXT NOT NULL, color TEXT NOT NULL,
  created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS BacklogTags (
  backlog_id INTEGER NOT NULL, tag_id INTEGER NOT NULL,
  PRIMARY KEY (backlog_id, tag_id));
CREATE TABLE IF NOT EXISTS PcaContacts (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1);
CREATE TABLE IF NOT EXISTS Holidays (
  holiday_date TEXT PRIMARY KEY, description TEXT);
```

### 2.3 Entities (`Models/Entities.cs`, append)
```csharp
public sealed record Tag(int Id, string Text, string Icon, string Color, DateTimeOffset CreatedAt);
public sealed record PcaContact(int Id, string Name, bool IsActive);
public sealed record Holiday(DateOnly Date, string? Description);
```
`Backlog` record extended with the 7 v7 fields (all nullable-defaulted so existing ctors compile — same technique as v2/v4 fields).

### 2.4 Read models (`Models/ReadModels.cs`, append)
```csharp
public enum ScheduleState { Normal, Warning, Late }
public sealed record TaskListRow(
  int BacklogId, string BacklogCode, string Project, string? Type,
  string? PctAssigneeName, string? PcaContactName,
  DateOnly? DeadlineInternal, DateOnly? DeadlineExternal, DateOnly? StartDate,
  int? ProgressPercent, decimal LoggedHours, decimal? EstimateHours,
  ScheduleState ScheduleState, IReadOnlyList<Tag> Tags, IReadOnlyList<TaskItem> Tasks);
public sealed record GanttBar(int BacklogId, string BacklogCode,
  DateOnly? Start, DateOnly? End, int StartDayIndex, int SpanWorkingDays,
  int? ExternalMarkerIndex, bool HasStart, ScheduleState ScheduleState);
public sealed record GanttModel(IReadOnlyList<DateOnly> Axis, IReadOnlyList<GanttBar> Bars);
```

### 2.5 Migration (DATA-05 pattern)
Bump `DatabaseInitializer.SchemaVersion` 6→7; append one migration step at the matching index doing the 7 `ALTER TABLE Backlogs ADD COLUMN …`; add the 4 `CREATE TABLE IF NOT EXISTS` in `CreateTables`. Whole init stays one transaction. Backward-compatible (an old client opening a v7 DB ignores unknown columns/tables). **A v6→v7 upgrade unit test is mandatory** (R1).

## 3. Repositories

- **`ITagRepository`** (new): `GetAllAsync`, `InsertAsync`→id, `UpdateAsync`, `DeleteAsync` (deletes `BacklogTags` links then the tag, one tx). Hard-delete (TAG-01 "delete").
- **`IPcaContactRepository`** (new, mirrors `IUserRepository` soft-delete): `GetAllAsync` (incl inactive), `GetActiveAsync`, `GetByIdAsync`, `InsertAsync`→id, `UpdateNameAsync`, `SetActiveAsync`.
- **`IHolidayRepository`** (new): `GetAllAsync`, `GetForMonthAsync(y,m)`, `UpsertAsync(date, desc?)`, `DeleteAsync(date)`.
- **`IBacklogRepository`** extend: the 7 v7 columns flow through the existing `Backlog`-typed `Insert/Update` (grow `BacklogRaw`/`Cols`/param objects); audit new columns via existing `LogAsync` diff. Add tag-link methods: `GetTagIdsAsync(int backlogId)`, `SetTagsAsync(int backlogId, IReadOnlyList<int> tagIds)` (replace-all in one tx), `GetTagIdsForAllAsync()` (bulk, avoid N+1).
- **`ITimeLogRepository`** extend: `GetLoggedHoursByBacklogAsync()` → `IReadOnlyDictionary<int,decimal>` = `SELECT t.backlog_id, SUM(l.hours) FROM TimeLogs l JOIN Tasks t ON t.id=l.task_id GROUP BY t.backlog_id` — **ALL-TIME, NO `is_active` filter** (A1, XC-06).

All new repos follow the `using var c = _factory.Create();` per-method seam (XC-01); REAL↔decimal at the Raw-DTO boundary (`(double)` write, `(decimal)` read). `pca_contact_id` has no inline FK.

## 4. Services

### 4.1 `IWorkingDayCalculator` (pure, singleton) — HOL-02
```csharp
bool IsWorkingDay(DateOnly d, IReadOnlySet<DateOnly> holidays);              // false for Sat/Sun/holiday
IReadOnlyList<DateOnly> WorkingDaysBetween(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays);
int CountWorkingDays(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays);   // inclusive both ends
```
No DB / no clock inside → deterministic unit tests. Callers load `IHolidayRepository.GetAllAsync()→HashSet` once per reload and pass it in (A2).

### 4.2 `SmartInputService` (HOL-02, regression-guarded) — A3
Keep `DistributeEven(from,to,total)` / `FillFull8h(from,to)` (delegate to empty holiday set → existing SI-01/02 tests stay green). Add holiday-aware overloads taking `IReadOnlySet<DateOnly> holidays` that route through `IWorkingDayCalculator`. The smart-fill panel loads holidays and calls the new overload.

### 4.3 `IScheduleStateService` (pure, singleton) — TL-07/08
```csharp
ScheduleState Evaluate(DateOnly today, DateOnly? startDate, DateOnly? internalDeadline,
  decimal? estimateHours, decimal loggedHours, bool isDone,
  IReadOnlySet<DateOnly> holidays, IWorkingDayCalculator calc);
```
**Never throws** (R4). Logic (decision table §6.4):
1. `isDone` → `Normal`.
2. no `internalDeadline` → `Normal`.
3. `today > internalDeadline` && !done → **`Late`** (precedence).
4. else if `startDate` present AND `estimate>0` AND within ≤2 working days `(today, internalDeadline] ≤ 2` (Q1) AND `behind` → **`Warning`**.
5. else `Normal`.
where `behind = loggedHours/estimate < elapsed/total`, `elapsed = CountWorkingDays(start, min(today,deadline))`, `total = CountWorkingDays(start, deadline)`; if `total<=0` → not behind (hide). `estimate = official ?? rough` (>0 else undefined → no warning). Guard every divide.

### 4.4 `ITaskListArchiveService` (TL-09) — mirrors `StandupArchiveService`
`ExportMonthAsync(int year,int month)` → writes `…/Documents/TimesheetApp/TaskListArchives/{yyyyMM}_tasklist.md` (dir from `IAppConfig.ArchivePath` fallback next-to-db), returns null + no file when the month has no data. `BackfillMissingMonthsAsync()` on startup: every month strictly before the current one with data (current members OR moved-out) but no file → generate. Idempotent overwrite. `|` escaped.

## 5. UI

### 5.1 Navigation (TL-02)
Sidebar WORKSPACE: **Log Work · Backlog · Task List · Daily Report · Reports**; ADMIN: **Users · Settings**. Implementation: relabel `timesheet` RadioButton → "Log Work" (keep key); add `backlog`/`reports`/`tasklist` RadioButtons + sibling content `DockPanel`s; delete the Timesheet sub-tab `TabControl` + `OnSubTabChanged`. `OnActiveViewChanged` routes each top-level key to its load (`backlog`→`Backlogs.LoadAsync`, `reports`→`ActivateTabAsync(3)`, `tasklist`→`TaskList.LoadAsync`). Index map 0..4 unchanged. **No Timesheet/Standup regression** (R2 — UAT nav).

### 5.2 Backlog editor (TL-03) — extend `BacklogEditorViewModel` + `BacklogsTab.xaml`
Adds: internal/external deadline pickers, rough/official estimate (hours), note, manual progress %, PCT assignee combo (existing), PCA contact combo (active PcaContacts + "Unassigned" sentinel), tag multi-select (checkbox list of all tags). Save extends the `Backlog` ctor + calls `SetTagsAsync`. New columns audited. DEFAULT backlog: no tracking UI.

### 5.3 Task List screen (TL-04/05/06/07/08/10, TAG-02) — `TaskListTab.xaml` + `TaskListViewModel`
- Month selector (year+month combos, like editor) → loads backlogs with `period_month==selected` (DEFAULT excluded).
- **Grid view:** columns = code, project, type, PCT assignee, PCA contact, internal deadline, external deadline, progress % (bar + number; null = "—"), logged hours, estimate (`official ?? rough`, whole-number formatting), chips (system + custom). Expand a row to see its tasks (+status).
- **Chips (TAG-02):** order = system first (late before warning), then custom tags by id. Custom = `HexToBrushConverter` background + icon glyph + text. System = fixed amber/red theme brushes.
- **Progress vs schedule are independent** (§6.3): manual % bar ≠ warning chip; warning never reads `progress_percent`.
- **Toggle Grid↔Gantt** + **collapse/expand** the chart area (`IsGantt`, `IsChartCollapsed`), preserving the selected month.
- **Manual "Export this month"** button → `TaskListArchiveService.ExportMonthAsync`.
- Registers `DataChangedMessage` (reload on `Backlogs|Tasks|Logs|Tags|Holidays`).

### 5.4 Gantt (TL-10, D3) — Canvas in `TaskListTab`
`GanttModel` built in the VM: `Axis` = working days (weekends+holidays excluded) over `min(start) .. max(deadline_internal)`; each `GanttBar` carries `StartDayIndex`/`SpanWorkingDays` (start→`deadline_internal`) + `ExternalMarkerIndex` (PCA marker) + `ScheduleState` color. Pixel layout (X = index*dayWidth, etc.) drawn in code-behind reading `ActualWidth` post-layout (R5). **No start_date** → faint placeholder row + deadline-only marker (Q3). Missing internal but has end_date → `start→end`, neutral. Collapsible.

### 5.5 Settings additions — extend `SettingsViewModel` + `SettingsTab.xaml`
- **Tags CRUD (TAG-01):** list + editor overlay (text, emoji/glyph free-text + quick-pick, color hex + swatches). Hard-delete removes links.
- **PCA contacts CRUD (TL-11):** list + add/rename/deactivate (soft-delete), mirrors Users.
- **Holiday calendar (HOL-01):** a month grid (UniformGrid of day cells; prev/next month) where clicking a day toggles `Holidays` upsert/delete; weekends shown distinct from holidays; optional description. Own small `HolidayCalendarViewModel` owned by SettingsViewModel.
Each command broadcasts the matching `DataKind` so editor/Task List refresh.

## 6. Behavior rules (canonical)

### 6.1 Working-day math (HOL-02)
A day is non-working iff Sat/Sun **or** in `Holidays`. `CountWorkingDays(a,b)` = inclusive count. Used by smart-input, schedule math, the ≤2-day window, and the Gantt axis — one helper, so everything agrees.

### 6.2 "Done backlog" (Q2)
Derived: ≥1 active task AND all active tasks `Status=="Done"`. Zero active tasks → not Done.

### 6.3 Manual % vs auto chip (§2 feature)
Independent. `progress_percent` = human judgment (null≠0). Warning chip = objective `logged/estimate` vs elapsed. Never cross-wire.

### 6.4 Chip decision table (TL-07/08)
| Condition | Warning | Late |
|---|---|---|
| Done | hide | hide |
| no internal deadline | hide | hide |
| today > internal deadline, not Done | hide | **show** |
| null/≤0 estimate (within window, behind) | hide | — |
| no start_date (estimate+deadline present) | hide | (late may apply) |
| within ≤2 wd, behind, all present, not Done | **show** | hide |
| within ≤2 wd, on/ahead | hide | hide |

### 6.5 Monthly export carry-over (TL-09, Q5)
Month `M` export = (a) current members `period_month==M` + (b) moved-out: backlogs with a `BacklogAudit` row `field='period_month' AND old_value=M` not currently in `M` (dedup: current membership wins, latest audit decides) → "Moved to next month" section. No data → no file.

## 7. Estimate precedence (TL-05, A1)
Display + warning math both use `official_estimate_hours ?? rough_estimate_hours`. Logged hours = all-time `SUM(hours)` per backlog (XC-06 join). Whole hours render without decimals.

## 8. Testing strategy
**Unit (must):** v6→v7 migration upgrade (R1); `IWorkingDayCalculator` (weekends+holidays, counts, edges); `IScheduleStateService` decision table incl. div-by-zero/null/reversed/today-before-start (R4); `GetLoggedHoursByBacklogAsync` no-`is_active` (R3); repo CRUD (Tag/Pca/Holiday + BacklogTags + new backlog columns round-trip + audit); SmartInput holiday overload (HOL-02); GanttModel axis/index geometry; TaskListArchive markdown incl. moved-out section + no-data→no-file + idempotent overwrite; estimate precedence.
**UAT-only (§F synthesis):** Gantt visuals/colors/markers, holiday-calendar click, tag-chip rendering + hex, sidebar nav, backlog editor fields round-trip, Grid↔Gantt toggle/collapse.

## 9. Out of scope (this milestone)
Per-task tracking fields; PCA contact directory beyond name; image-upload tag icons; recurring/auto holidays (manual marking only); migrating RPT-04's `LastNWorkingDays` to the new calculator (stays weekend-only, unchanged); editing an already-archived month's file other than re-export.
