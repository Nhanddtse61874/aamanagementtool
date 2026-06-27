# P8 "Task List" — Architecture Research (Mode B, STEP 4)

**Phase:** P8 / M3 — Task List (tracking, tags, holidays, Gantt)
**Stack:** WPF .NET 8, MVVM (CommunityToolkit.Mvvm), SQLite + Dapper, repository pattern, bare `ServiceCollection` DI.
**Date:** 2026-06-27
**Source REQs:** TL-01..11, TAG-01/02, HOL-01/02, decisions D1–D9 (`.planning/REQUIREMENTS.md`).
**Tagging:** every claim is `[VERIFIED]` (confirmed in code, file:line), `[CITED]` (from REQ/decision/spec), or `[ASSUMED]` (inferred).

> This feeds STEP 5 (spec) and STEP 6 (planning). It does **not** decide product behavior — only how P8 slots into the existing architecture without regressing M1/M2.

---

## 0. Existing architecture — confirmed facts (the baseline P8 must not break)

- **DI is a bare `ServiceCollection` composition root in `App.OnStartup`** `[VERIFIED App.xaml.cs:41-102]`. Repos + services are **singletons** (stateless, one short connection per method); ViewModels are **transient** `[VERIFIED App.xaml.cs:60-100]`. `IMessenger` = `WeakReferenceMessenger.Default` singleton `[VERIFIED App.xaml.cs:50-51]`.
- **Startup order:** `InitializeAsync()` (schema+migrations+DEFAULT seed) → standup archive backfill → `DefaultTaskSyncService.SyncAsync()` → `MainViewModel.InitializeAsync()` → show window `[VERIFIED App.xaml.cs:105-132]`. This is the hook point for **TL-09 monthly backfill** (mirror line 109).
- **Connection seam:** every repo takes `IConnectionFactory`, calls `_factory.Create()` per method, `using` disposes it `[VERIFIED BacklogRepository.cs:14-16,19-21]`. New repos MUST follow this exact pattern (XC-01 OneDrive policy).
- **Schema is `DatabaseInitializer`**: `const long SchemaVersion` + a `migrations[]` array of `Action<IDbConnection,IDbTransaction>`, indexed so step N runs when `user_version < N+1`, all in one transaction `[VERIFIED DatabaseInitializer.cs:14,152-219]`. Current `SchemaVersion = 6` `[VERIFIED DatabaseInitializer.cs:14]`. Tables created idempotently in `CreateTables`; legacy `Requests`/`RequestAudit` were **renamed to `Backlogs`/`BacklogAudit` by the v6 migration** `[VERIFIED DatabaseInitializer.cs:195-205]`.
- **Entities** (records, 1:1 tables) in `Models/Entities.cs`; **read/projection models** in `Models/ReadModels.cs` `[VERIFIED]`. `Backlog` already has `AssigneeUserId` (v4) — this is the **PCT assignee** per D5, **do not re-add** `[VERIFIED Entities.cs:10-14; CITED TL-01]`.
- **Cross-tab live sync:** `DataChangedMessage(DataKind Kind)` broadcast over the messenger; consumers `Register<TVm, DataChangedMessage>(this, static (vm,m)=>…)` with a static lambda to keep the weak ref `[VERIFIED DataChangedMessage.cs:1-19; TimesheetViewModel.cs:61-65; ReportsViewModel.cs:55; DailyReportViewModel.cs:34]`. `DataKind` enum currently: `Backlogs, Tasks, Users, Logs, Templates, DefaultTasks, Standup` `[VERIFIED DataChangedMessage.cs:4-13]`.
- **Sidebar nav** is RadioButtons bound to `MainViewModel.ActiveView` (a string) via `StringMatchConverter` (IsChecked) + `StringMatchToVisibilityConverter` (content panel visibility) `[VERIFIED MainWindow.xaml:63-87,144-221; StringMatchConverter.cs]`. There is **no TabControl-of-pages**; each destination is a sibling `DockPanel` whose `Visibility` binds to `ActiveView`.
- **CRITICAL discrepancy to fix in TL-02:** `MainViewModel`'s doc comment + `ActivateTabAsync` claim "0 Timesheet, 1 Backlog, 2 Users, 3 Reports, 4 Settings" `[VERIFIED MainViewModel.cs:127-146]`, **but the live `MainWindow.xaml` puts Backlog + Reports as *sub-tabs* under a single "Timesheet" `TabControl`** (Entry/Backlog/Reports) `[VERIFIED MainWindow.xaml:143-163]`, and `OnActiveViewChanged` only maps `timesheet→0, users→2, settings→4` `[VERIFIED MainViewModel.cs:90-95]`. The sub-tab `SelectionChanged` handler `OnSubTabChanged` maps sub-tab index `0→0, 1→1, 2→3` `[VERIFIED MainWindow.xaml.cs:15-23]`. So today **Backlog and Reports are NOT top-level**; TL-02 must promote them. `[VERIFIED]`
- **Working-day / weekend logic is duplicated in 3 places, none holiday-aware:**
  - `SmartInputService.WorkingDays(from,to)` — private static, Mon–Fri only `[VERIFIED SmartInputService.cs:45-52]`.
  - `ReportAggregator` (in `TimeLogService.cs`'s aggregator partial) `IsWeekend` + `LastNWorkingDays` `[VERIFIED TimeLogService.cs:233,238-247]`.
  - `MondayOf` repeated in `StandupArchiveService.cs:135` and `TimesheetViewModel` `[VERIFIED]`.
  This is the extraction target for **HOL-02 / §3 below.**
- **Soft-delete CRUD model to mirror for PCA contacts (TL-11):** `IUserRepository` + `UsersViewModel` — `GetAllAsync` (incl. inactive) / `GetActiveAsync` / `InsertAsync` / `SetActiveAsync(id,false)`, VM broadcasts `DataChangedMessage(DataKind.Users)` `[VERIFIED IUserRepository.cs:8-18; UsersViewModel.cs:33-51]`.
- **Editor-overlay pattern** (for TL-03 backlog editor + Settings CRUD overlays): a nullable `*EditorViewModel` property on the parent VM; XAML shows a modal `Border` whose `Visibility` binds via `NullToCollapsedConverter` `[VERIFIED SettingsViewModel.cs:46; SettingsTab.xaml:84-164; RequestEditorViewModel.cs]`.
- **Audit:** `BacklogRepository.UpdateAsync` already diffs fields and writes `BacklogAudit` rows; adding new audited columns = add `LogAsync(...)` calls there `[VERIFIED BacklogRepository.cs:74-128; CITED TL-03 "recorded in BacklogAudit"]`.
- **Export is headless:** `IExportService` returns `byte[]`/`string`, never opens dialogs `[VERIFIED IExportService.cs:7-11]`. Archive-to-file pattern is `StandupArchiveService` (`ExportWeekAsync`, `BackfillMissingWeeksAsync`, `FileNameFor`, dir from `IAppConfig.ArchivePath` fallback next-to-db) `[VERIFIED StandupArchiveService.cs:27-75]` — the template for **TL-09**.
- **ClosedXML** is referenced; tests live in `src/TimesheetApp.Tests` `[VERIFIED *.csproj glob]`.

---

## 1. New repositories / interfaces + extensions to existing repos

All new repos: `sealed class … : I…`, ctor takes `IConnectionFactory _factory`, one `using var c = _factory.Create();` per method (mirrors `BacklogRepository`) `[VERIFIED pattern]`. Register as **singletons** in `App.xaml.cs` next to the existing repo block (line 60-67) `[VERIFIED]`.

### 1a. `ITagRepository` (TAG-01/02) — NEW
```csharp
public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> GetAllAsync();
    Task<int> InsertAsync(Tag tag);                 // returns new id
    Task UpdateAsync(Tag tag);                       // icon/color/text
    Task DeleteAsync(int tagId);                      // hard delete; cascades BacklogTags (see 1b)
}
```
`[CITED TAG-01 "Tag CRUD persists … deleting a tag removes its BacklogTags links"]`. Tags are **hard-deleted** (no soft-delete; TAG-01 says "delete") — implementation deletes `BacklogTags` rows for the tag first, then the `Tags` row, in one transaction `[ASSUMED transaction grouping; CITED delete-cascades-links]`.

### 1b. Backlog↔Tag link — extend `IBacklogRepository`, NOT a separate public repo
Many-to-many `BacklogTags(backlog_id, tag_id)` is small and always read alongside a backlog. Put it on the existing `IBacklogRepository` to avoid a thin extra repo:
```csharp
// add to IBacklogRepository
Task<IReadOnlyList<int>> GetTagIdsAsync(int backlogId);
Task SetTagsAsync(int backlogId, IReadOnlyList<int> tagIds);  // replace-all in one tx (delete+insert)
Task<IReadOnlyDictionary<int,IReadOnlyList<int>>> GetTagIdsForAllAsync();  // Task List bulk load (avoid N+1)
```
`[ASSUMED placement on IBacklogRepository — keeps the join with its aggregate root; CITED TL-03 BacklogTags persist + TAG-02 chips on list]`. `SetTagsAsync` = delete-all-then-insert (mirrors template save at `SettingsViewModel.cs:113-118`) `[VERIFIED pattern]`.

### 1c. `IPcaContactRepository` (TL-11, D5) — NEW, mirrors `IUserRepository` soft-delete
```csharp
public interface IPcaContactRepository
{
    Task<IReadOnlyList<PcaContact>> GetAllAsync();    // incl. inactive (Settings list)
    Task<IReadOnlyList<PcaContact>> GetActiveAsync(); // dropdown source
    Task<PcaContact?> GetByIdAsync(int id);           // name resolution on existing backlogs
    Task<int> InsertAsync(PcaContact c);
    Task UpdateNameAsync(int id, string name);
    Task SetActiveAsync(int id, bool isActive);       // soft-delete (TL-11)
}
```
`[CITED TL-11 "soft-delete semantics, like Users"; VERIFIED mirrors IUserRepository.cs:8-18]`.

### 1d. `IHolidayRepository` (HOL-01) — NEW
```csharp
public interface IHolidayRepository
{
    Task<IReadOnlyList<Holiday>> GetAllAsync();        // calendar render + working-day calc seed
    Task<IReadOnlyList<Holiday>> GetForMonthAsync(int year, int month); // HOL-01 calendar paint
    Task UpsertAsync(DateOnly date, string? description); // mark / set description
    Task DeleteAsync(DateOnly date);                       // unmark
}
```
PK is `holiday_date` (TEXT yyyy-MM-dd) `[CITED TL-01 "Holidays(holiday_date TEXT PK, description TEXT)"]`. `GetAllAsync` is what the working-day calculator loads (§3) `[ASSUMED]`.

### 1e. Extend `IBacklogRepository` for the new tracking columns (TL-01/03)
The `Backlog` record + `BacklogRaw` + `Cols` const + `Insert/Update` param objects all extend with the v7 columns (see §2). **No new method signatures** beyond tags (1b): `InsertAsync`/`UpdateAsync` already take a `Backlog`, so growing the record covers it `[VERIFIED BacklogRepository.cs:12,54-128]`. New audit `LogAsync` calls for `deadline_internal, deadline_external, rough_estimate_hours, official_estimate_hours, progress_percent, note, pca_contact_id` `[CITED TL-03 "changes recorded in BacklogAudit"]`.

### 1f. Extend `ITimeLogRepository` — per-backlog logged-hours sum (TL-05)
Existing `GetReportRowsAsync` already joins `TimeLogs→Tasks→Backlogs` with **no `is_active` filter (XC-06)** `[VERIFIED TimeLogRepository.cs:44-61]`. Add a roll-up that returns `SUM(hours)` per backlog for a month, in one query (avoid N+1 over backlogs):
```csharp
// add to ITimeLogRepository
Task<IReadOnlyDictionary<int,decimal>> GetLoggedHoursByBacklogAsync(DateOnly from, DateOnly to);
// SELECT t.backlog_id, SUM(l.hours) FROM TimeLogs l JOIN Tasks t ON t.id=l.task_id
// WHERE l.work_date BETWEEN @from AND @to GROUP BY t.backlog_id;  (NO is_active filter — XC-06)
```
`[CITED TL-05 "SUM(TimeLogs.hours) over the backlog's tasks, no is_active filter, per XC-06"; VERIFIED join shape exists at TimeLogRepository.cs:53-56]`. Note: TL-05 says "across all its tasks' TimeLogs" with no date bound; the Task List is month-scoped (TL-04), so a ranged sum keyed to the selected month is the right shape `[ASSUMED month-range vs all-time — flag for spec]`.

---

## 2. New entities + read models (field lists)

### Entities (`Models/Entities.cs`) — append (do not reorder existing) `[VERIFIED file is append-friendly: records with default params]`
```csharp
public sealed record Tag(int Id, string Text, string Icon, string Color, DateTimeOffset CreatedAt);
public sealed record PcaContact(int Id, string Name, bool IsActive);
public sealed record Holiday(DateOnly Date, string? Description);
```
And **extend the existing `Backlog` record** with v7 fields (all nullable, defaulted so existing ctors compile — same technique used for v2/v4 fields) `[VERIFIED Entities.cs:10-14 pattern]`:
```csharp
public sealed record Backlog(
    int Id, string BacklogCode, string Project, DateTimeOffset CreatedAt,
    DateOnly? StartDate = null, DateOnly? EndDate = null,
    string? PeriodMonth = null, string? Type = null,
    int? AssigneeUserId = null,                 // v4 = PCT (reused, D5)
    DateOnly? DeadlineInternal = null,          // v7 (PCT deadline)
    DateOnly? DeadlineExternal = null,          // v7 (PCA deadline)
    decimal? RoughEstimateHours = null,         // v7
    decimal? OfficialEstimateHours = null,      // v7
    int? ProgressPercent = null,                // v7 (0–100, null = not set)
    string? Note = null,                        // v7
    int? PcaContactId = null);                  // v7 FK→PcaContacts
```
`[CITED TL-01 column list]`. `BacklogTags` has no entity record — it's pure link rows surfaced as `IReadOnlyList<int>` tag ids (§1b) `[ASSUMED]`.

### Read models (`Models/ReadModels.cs`) — append
**Schedule state enum + computed result (drives chips + Gantt color):**
```csharp
public enum ScheduleState { Normal, Warning, Late }   // TL-07/08/10
```
**Task List row (TL-04/05/06/07/08, TAG-02):**
```csharp
public sealed record TaskListRow(
    int BacklogId, string BacklogCode, string Project, string? Type,
    string? PctAssigneeName, string? PcaContactName,
    DateOnly? DeadlineInternal, DateOnly? DeadlineExternal,
    DateOnly? StartDate,
    int? ProgressPercent,
    decimal LoggedHours,
    decimal? EstimateHours,                 // official ?? rough (TL-05)
    ScheduleState ScheduleState,            // computed (TL-07/08, §6)
    IReadOnlyList<Tag> Tags,                // custom chips (TAG-02)
    IReadOnlyList<TaskItem> Tasks);         // expandable rows (TL-04)
```
`[CITED TL-04 column list, TL-05 logged+estimate, TL-07/08 state, TAG-02 chips, TL-04 "expandable task rows"]`.
**Gantt bar layout (TL-10) — VM-built, pixel layout is a View concern but the geometry model is testable:**
```csharp
public sealed record GanttBar(
    int BacklogId, string BacklogCode,
    DateOnly Start, DateOnly End,           // start→deadline_internal
    int StartDayIndex, int SpanWorkingDays, // position on the working-day axis
    ScheduleState ScheduleState);           // color
public sealed record GanttModel(
    IReadOnlyList<DateOnly> Axis,           // working days (weekends+Holidays excluded), the day columns
    IReadOnlyList<GanttBar> Bars);
```
`[CITED TL-10 "one horizontal bar per backlog spanning start→deadline across working days, weekends+Holidays skipped, colored by schedule state"; D3 native Canvas]`. The **pixel** math (X = index * dayWidth, etc.) lives in the Gantt VM/control, fed by `GanttModel` `[ASSUMED — keeps the axis/index logic unit-testable, Canvas drawing in code-behind]`.
**Moved-out tracking for TL-09 export** is read from `BacklogAudit` `period_month` changes — no new model needed; an in-export query/struct suffices `[CITED TL-09 "read from BacklogAudit period_month changes"; VERIFIED audit stores period_month at BacklogRepository.cs:117]`.

---

## 3. Shared working-day service (HOL-02, supports SI/TL-07/Gantt) — `IWorkingDayCalculator`

**Goal:** one holiday-aware helper replacing the 3 duplicated weekend helpers, **without regressing SI-01/SI-02** (smart-input math currently has zero DB dependency — it's pure) `[VERIFIED SmartInputService.cs:5 "No DB"]`.

**Tension:** the calculator must know Holidays (DB-backed, HOL-02) but `SmartInputService` is deliberately pure. Resolution: the calculator is a **service that takes a holiday set**, and the *holiday set is loaded once and passed in* — keeping the math itself pure and testable.

```csharp
public interface IWorkingDayCalculator
{
    bool IsWorkingDay(DateOnly d, IReadOnlySet<DateOnly> holidays);
    IReadOnlyList<DateOnly> WorkingDaysBetween(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays);
    int CountWorkingDays(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays);
    IReadOnlyList<DateOnly> LastNWorkingDays(DateOnly today, int n, IReadOnlySet<DateOnly> holidays);
}
```
- **Pure** (no `IConnectionFactory`, no `IClock`) → registered singleton, trivially unit-tested with a hand-built holiday set `[ASSUMED design; mirrors SmartInputService purity VERIFIED]`.
- **Holiday loading** is the caller's job: a thin `IHolidayProvider` (or just call `IHolidayRepository.GetAllAsync()` → `.Select(h=>h.Date).ToHashSet()`) at the point of use (TL-07 schedule service, Gantt VM load, smart-input apply). `[ASSUMED]`
- **SI regression guard:** `SmartInputService.DistributeEven/FillFull8h` keep their signatures `(from,to,total)` — internally they call `IWorkingDayCalculator.WorkingDaysBetween(from,to,holidays)` instead of the private `WorkingDays` `[VERIFIED current private helper SmartInputService.cs:45-52]`. To honor HOL-02 ("smart-input now consults Holidays") the SI service gains a holiday-set parameter OR the smart-fill **panel** (`SmartInputPanelVm`) loads holidays and the math overload accepts them. **Recommended:** add an overload `DistributeEven(from,to,total, IReadOnlySet<DateOnly> holidays)` and keep the weekend-only overload delegating with an empty set, so **existing SI unit tests pass unchanged** `[ASSUMED — minimizes SI-01/02 regression risk; CITED HOL-02 "[ASSUMED] smart-input now consults Holidays"]`.
- **Migration of `ReportAggregator.LastNWorkingDays`** (RPT-04) to the calculator is **optional** and out of P8 scope unless free — RPT-04 is weekend-only by spec and the calc default (empty holiday set) preserves behavior `[VERIFIED TimeLogService.cs:238-247; ASSUMED leave RPT-04 alone to avoid scope creep]`.

---

## 4. New ViewModels + Views (registration + shell wiring)

### 4a. `TaskListViewModel` (transient) — TL-04/05/06/07/08/10, TAG-02
Ctor deps (all existing or new singletons): `IBacklogRepository`, `ITaskRepository`, `ITimeLogRepository`, `ITagRepository`, `IPcaContactRepository`, `IUserRepository`, `IHolidayRepository`, `IWorkingDayCalculator`, `IScheduleStateService` (§6), `IClock`, `IMessenger?`. Holds: `ObservableCollection<TaskListRow> Rows`, `SelectedMonthNumber/Year` (mirror `BacklogEditorViewModel` month combos `[VERIFIED RequestEditorViewModel.cs:36-37,53-55]`), `bool IsGantt` toggle (TL-10), `bool IsChartCollapsed`, `GanttModel? Gantt`, `ExportThisMonthCommand` (TL-09 manual). Registers via `_messenger.Register<TaskListViewModel,DataChangedMessage>` reloading on `Backlogs|Tasks|Logs` `[VERIFIED pattern TimesheetViewModel.cs:61-65; CITED TL-04 month reload]`.
- **View:** `Views/Tabs/TaskListTab.xaml` (UserControl), grid via `DataGrid`/`ItemsControl`; Gantt via a `Canvas` inside a collapsible `Expander`/toggled panel (D3, no NuGet) `[CITED D3, TL-10]`.

### 4b. Gantt rendering — VM data + Canvas code-behind
`GanttModel` (§2) is built in `TaskListViewModel` (axis = `IWorkingDayCalculator.WorkingDaysBetween` over min-start..max-deadline, holidays excluded). The **Canvas drawing** (rectangles, colors by `ScheduleState`, weekend/holiday gaps) is in `TaskListTab.xaml.cs` code-behind reacting to `Gantt` changes `[ASSUMED — pixel layout is intrinsically View; the testable geometry is `StartDayIndex`/`SpanWorkingDays` on the VM side; CITED TL-10 Canvas]`.

### 4c. Settings additions — Tags CRUD, PCA CRUD, Holiday calendar (TAG-01, TL-11, HOL-01)
Extend the **existing `SettingsViewModel`** (singleton-of-deps, transient VM) with three sections rather than new top-level VMs — matches how SET-01..05 already coexist in one VM/tab `[VERIFIED SettingsViewModel.cs; SettingsTab.xaml]`. Add deps `ITagRepository`, `IPcaContactRepository`, `IHolidayRepository`. New `ObservableCollection`s + `[RelayCommand]`s mirroring the template-CRUD + Users-CRUD patterns. Each command broadcasts a new `DataKind` (§7 / §5 enum) so backlog editor dropdowns/Task List refresh.
- **Holiday calendar (HOL-01):** a month grid (custom `ItemsControl`/`UniformGrid` of day cells) with a click command toggling `IHolidayRepository.Upsert/Delete`; weekends shown distinct from holidays `[CITED HOL-01]`. Likely its own small sub-VM `HolidayCalendarViewModel` owned by `SettingsViewModel` to keep the calendar state contained `[ASSUMED]`.
- **View:** append three `SectionTitle` blocks + an editor overlay for Tag (icon+color+text picker) into `SettingsTab.xaml` (mirrors the template overlay at lines 84-164) `[VERIFIED SettingsTab.xaml:84-164]`. **File-overlap hotspot** (§7).

### 4d. Backlog editor additions (TL-03) — extend `BacklogEditorViewModel` + `BacklogsTab.xaml`
Add observable props: `DeadlineInternal`, `DeadlineExternal`, `RoughEstimateHours`, `OfficialEstimateHours`, `Note`, `ProgressPercent`, `SelectedPcaContact` (+ `PcaContacts` list, `Unassigned`-style sentinel mirroring the existing assignee combo at `RequestEditorViewModel.cs:13,40-43`), and a tag multi-select (`ObservableCollection<TagPick>` of checkboxes). `BacklogsViewModel.SaveNew/SaveEditAsync` extend the `Backlog` ctor call + add `SetTagsAsync` `[VERIFIED RequestEditorViewModel.cs:40-43; RequestsViewModel.cs:160-195]`. DEFAULT backlog exempt (no tracking UI) `[CITED TL-03]`.

### 4e. DI registration (App.xaml.cs) — additions
```csharp
// repos (singletons, next to line 67)
sc.AddSingleton<ITagRepository, TagRepository>();
sc.AddSingleton<IPcaContactRepository, PcaContactRepository>();
sc.AddSingleton<IHolidayRepository, HolidayRepository>();
// services (singletons, next to line 77)
sc.AddSingleton<IWorkingDayCalculator, WorkingDayCalculator>();
sc.AddSingleton<IScheduleStateService, ScheduleStateService>();   // §6
sc.AddSingleton<ITaskListArchiveService, TaskListArchiveService>(); // TL-09
// VM (transient, next to line 100)
sc.AddTransient<TaskListViewModel>();
```
Plus inject `TaskListViewModel taskList` into `MainViewModel` ctor (both ctors) and add a `public TaskListViewModel TaskList { get; }` property `[VERIFIED MainViewModel.cs:29-74 ctor shape]`. Add TL-09 backfill call in `OnStartup` mirroring line 109 `[VERIFIED App.xaml.cs:109]`.

---

## 5. Sidebar restructure (TL-02) — exact changes

Target sidebar: **WORKSPACE = Log Work · Backlog · Task List · Daily Report · Reports** ; **ADMIN = Users · Settings** `[CITED D2, TL-02]`.

**`MainWindow.xaml`:**
1. **Rename** the "Timesheet" nav RadioButton → `Content="Log Work"`, keep `ConverterParameter=timesheet` (or rename to `logwork` — but reusing `timesheet` avoids touching `ActiveView` default + `OnActiveViewChanged`; **recommend keep the `timesheet` key, change only the label**) `[VERIFIED MainWindow.xaml:63-65; MainViewModel.cs:86 default "timesheet"]`.
2. **Add** two new nav RadioButtons: `Content="Backlog"` (`ConverterParameter=backlog`) and `Content="Reports"` (`ConverterParameter=reports`), placed per D2 order. **Replace** the disabled "Task List SOON" Grid (lines 72-79) with an enabled `Content="Task List"` (`ConverterParameter=tasklist`) `[VERIFIED MainWindow.xaml:71-79]`.
3. **Split the content host:** the current "Timesheet" `DockPanel` (lines 143-163) holds a 3-tab `TabControl` (Entry/Backlog/Reports). Restructure to **three sibling content `DockPanel`s**: `timesheet` (Entry grid only — drop the `TabControl`), `backlog` (`BacklogsTab`), `reports` (`ReportsTab`), `tasklist` (`TaskListTab`), each `Visibility` bound to `ActiveView` via `StringMatchToVisibilityConverter` (same as Users/Settings panels) `[VERIFIED MainWindow.xaml:144-221 pattern]`.
4. **Delete** the `OnSubTabChanged` handler + `x:Name="SubTabs"` `TabControl` (no more sub-tabs) `[VERIFIED MainWindow.xaml.cs:15-23; MainWindow.xaml:152]`.

**`MainViewModel.cs`:**
1. `OnActiveViewChanged` — extend the switch to map every top-level view to a load `[VERIFIED MainViewModel.cs:90-95]`:
```csharp
switch (value)
{
    case "dailyreport": _ = SafeLoad(() => DailyReport.LoadAsync()); return;
    case "tasklist":     _ = SafeLoad(() => TaskList.LoadAsync()); return;
    case "backlog":      _ = SafeLoad(() => Backlogs.LoadAsync()); return;
    case "reports":      _ = ActivateTabAsync(3); return;  // reuse existing Reports multi-load
    case "timesheet":    _ = ActivateTabAsync(0); return;
    case "users":        _ = ActivateTabAsync(2); return;
    case "settings":     _ = ActivateTabAsync(4); return;
}
```
2. `ActivateTabAsync` index map can **stay as-is** (0/1/2/3/4 still resolve the right VMs) `[VERIFIED MainViewModel.cs:129-146]` — the restructure routes through `OnActiveViewChanged` by string, so the **index seam keeps working and nothing else breaks**. The stale doc comment at lines 124-128 should be corrected `[VERIFIED]`.
3. **No regression** to Timesheet/Standup: their VMs, commands, and messenger subscriptions are untouched; only the *navigation container* changes `[CITED TL-02 "No timesheet/standup behavior regresses"]`.

**Why nothing breaks:** nav is string-keyed (`ActiveView`), not index-keyed; the index map is an internal reload detail. Promoting Backlog/Reports = adding two string cases + two visibility panels + deleting the sub-tab `TabControl`. `[VERIFIED architecture is string-driven, MainWindow.xaml:144-221]`

---

## 6. Schedule-state computation (TL-07/08) — a **service**, not the VM

Put the chip logic in a pure `IScheduleStateService` so it's unit-testable independent of WPF (the formula is explicit and adversarial-edge-prone) `[CITED TL-07 formula; ASSUMED service placement for testability — consistent with SmartInputService/ReportAggregator being pure services]`.

```csharp
public interface IScheduleStateService
{
    ScheduleState Evaluate(
        DateOnly today,
        DateOnly? startDate, DateOnly? internalDeadline,
        decimal? estimateHours, decimal loggedHours,
        bool isDone,
        IReadOnlySet<DateOnly> holidays,
        IWorkingDayCalculator calc);
}
```
**Logic (verbatim from TL-07/08):**
- `isDone` → `Normal` (never warning/late) `[CITED TL-07/08]`.
- missing start/deadline/estimate → `Normal` `[CITED TL-07 "not shown when no start/deadline/estimate"]`.
- `today > internalDeadline` && not done → **`Late`** (takes precedence) `[CITED TL-08]`.
- else if within `≤2 working days` of `internalDeadline` (counted via `calc.CountWorkingDays`, weekends+Holidays excluded) **AND** `loggedHours/estimateHours < workingDaysElapsed(start,today)/workingDaysTotal(start,internalDeadline)` → **`Warning`** `[CITED TL-07 confirmed done%<elapsed% formula]`.
- else `Normal`.

Consumed by `TaskListViewModel` (per-row chip + Gantt bar color) and `BacklogsViewModel` (TAG-02 chips on the Backlog list) `[CITED TAG-02 "chips on both Backlog list and Task List/Gantt"]`. The service takes `today` + holiday set as params (no `IClock`/DB inside) so tests are deterministic `[ASSUMED; mirrors IClock-injected pattern VERIFIED ReportAggregator]`.

---

## 7. Phase decomposition — sub-phases / waves (zero same-wave file overlap)

`commit_atomic: true`, `parallelization: false` `[VERIFIED config.json:4,6]` → sequential dispatch, commit per task. Waves group by **dependency + file overlap**; same-wave plans must touch disjoint files (STEP 6 rule).

**File-overlap hotspots** (force serialization): `DatabaseInitializer.cs` (one migration array — TL-01 owns it alone), `Models/Entities.cs` + `Models/ReadModels.cs` (TL-01 appends; later waves only read), `App.xaml.cs` (DI — every wave that adds a registration; keep DI edits batched or accept serialization), `MainWindow.xaml` + `MainViewModel.cs` (TL-02 owns alone), `SettingsTab.xaml` + `SettingsViewModel.cs` (Settings wave owns), `BacklogEditorViewModel.cs` + `RequestsViewModel.cs` + `BacklogsTab.xaml` (TL-03 wave owns).

| Wave | Sub-phase | Touches (owned files) | REQs | Dep |
|---|---|---|---|---|
| **W1** | **Data layer**: v7 migration + new entities/read-models + new repos + IBacklogRepo/ITimeLogRepo extensions | `DatabaseInitializer.cs`, `Entities.cs`, `ReadModels.cs`, `TagRepository.cs`+`I…`, `PcaContactRepository.cs`+`I…`, `HolidayRepository.cs`+`I…`, `BacklogRepository.cs`, `IBacklogRepository.cs`, `TimeLogRepository.cs`, `ITimeLogRepository.cs` | TL-01, TL-05(query), TAG-01(store), TL-11(store), HOL-01(store) | — |
| **W2** | **Services**: `IWorkingDayCalculator`, `IScheduleStateService`, refactor `SmartInputService` to use calculator (SI guard), TL-09 archive service | `WorkingDayCalculator.cs`+`I…`, `ScheduleStateService.cs`+`I…`, `SmartInputService.cs`, `TaskListArchiveService.cs`+`I…` | HOL-02, TL-07, TL-08, TL-09 | W1 |
| **W3** | **Settings UI**: Tags CRUD + PCA CRUD + Holiday calendar | `SettingsViewModel.cs`, `SettingsTab.xaml`(+`.cs`), `HolidayCalendarViewModel.cs` | TAG-01, TL-11, HOL-01 | W1, (W2 for calc in calendar) |
| **W4** | **Backlog editor**: tracking fields + tags + PCA on editor | `BacklogEditorViewModel.cs`, `RequestsViewModel.cs`, `RequestsTab.xaml`(BacklogsTab) | TL-03 | W1 |
| **W5** | **Task List UI (grid)**: VM + grid view + schedule chips + month/progress | `TaskListViewModel.cs`, `TaskListTab.xaml`(+`.cs`) | TL-04, TL-05, TL-06, TAG-02(render) | W1, W2 |
| **W6** | **Gantt**: GanttModel build + Canvas render + toggle/collapse | `TaskListTab.xaml`(+`.cs`), `TaskListViewModel.cs` | TL-10 | **W5 (same files → must follow W5, not parallel)** |
| **W7** | **Sidebar restructure**: promote Backlog/Reports/Task List | `MainWindow.xaml`(+`.cs`), `MainViewModel.cs` | TL-02 | W5 (needs TaskListTab to exist) |
| **W8** | **DI + startup wiring**: register all new repos/services/VM, TL-09 startup backfill | `App.xaml.cs` | (wiring for TL-01..11) | all |

**Notes for STEP 6:**
- W3 and W4 are **file-disjoint** (Settings vs Backlog editor) → could be same wave under parallelization, but config is `parallelization:false` so they run sequentially anyway `[VERIFIED config.json:4]`.
- W6 **shares files with W5** (`TaskListTab.*`, `TaskListViewModel.cs`) → MUST be a later wave, never same-wave `[CITED STEP 6 zero-overlap rule]`.
- `App.xaml.cs` is edited only in **W8** to avoid repeated-file conflicts; alternatively each feature wave appends its own DI line and W8 is dropped — but that makes `App.xaml.cs` a cross-wave hotspot, so **batching DI in W8 is cleaner** `[ASSUMED; VERIFIED App.xaml.cs is the single DI root]`.
- TL-02's index-map correction + stale-comment fix ride in W7.

---

## Open items to resolve in STEP 5 (spec), not blocking architecture
1. **TL-05 logged-hours window**: all-time vs selected-month range (Task List is month-scoped). Recommend month-range `[ASSUMED §1f]`.
2. **Holiday-set delivery to the pure calculator**: `IHolidayProvider` wrapper vs callers loading `GetAllAsync()` each time. Recommend a tiny cached provider to avoid re-querying per row `[ASSUMED §3]`.
3. **SmartInputService HOL-02 surface**: overload with holiday set vs panel-level holiday loading — affects whether existing SI unit tests change `[ASSUMED §3]`.
4. **Tag icon picker** scope: emoji/Segoe glyph text input vs curated palette (TAG-01 `[ASSUMED]` says no image upload) `[CITED TAG-01]`.
