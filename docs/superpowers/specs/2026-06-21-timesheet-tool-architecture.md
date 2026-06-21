# Architecture Spec — WPF Desktop Timesheet Tool

**Date:** 2026-06-21
**Phase:** STEP 5 (Architecture Lead — Mode B)
**Status:** Implementation-ready
**Source of truth (do NOT reopen):**
- Design: `docs/superpowers/specs/2026-06-21-timesheet-tool-design.md` (approved + Resolved-decisions appendix)
- Requirements: `.planning/REQUIREMENTS.md` (43 REQs, locked)
- Research: `.planning/research/RESEARCH-SYNTHESIS.md`, `.planning/research/ARCHITECTURE-RESEARCH.md`

> This spec formalizes the approved design into concrete C# contracts and boundaries. It does NOT re-brainstorm. Every contract cites the REQ-IDs it satisfies. Inferences beyond literal design/REQ text are tagged `[ASSUMED]`. The 8 resolved decisions + `journal_mode=DELETE` are honored verbatim.

---

## 1. Project / Solution Layout

### 1.1 Solution shape

```
AgentArchitectureManagement/            (repo container)
└── src/
    ├── TimesheetApp.sln
    ├── TimesheetApp/                    (net8.0-windows, WinExe, WPF)
    └── TimesheetApp.Tests/             (net8.0, xUnit)
```

`[ASSUMED]` solution lives under `src/`; the design only mandates the `TimesheetApp` project name (design Appendix decision 1). Tests target bare `net8.0` (no WPF reference) so service/repo tests run without a desktop session.

### 1.2 Target frameworks

| Project | TFM | Key props |
|---|---|---|
| `TimesheetApp` | `net8.0-windows` | `<OutputType>WinExe</OutputType>`, `<UseWPF>true</UseWPF>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>` |
| `TimesheetApp.Tests` | `net8.0` | `<Nullable>enable</Nullable>`; references `TimesheetApp` |

Rationale (RESEARCH §1.1): `<UseWPF>` requires the `-windows` TFM; `WinExe` suppresses the console window. Windows-only matches design §9.

### 1.3 NuGet packages (pinned — RESEARCH §1.2)

**`TimesheetApp`:**

| Package | Version | Purpose |
|---|---|---|
| `CommunityToolkit.Mvvm` | 8.4.2 | `[ObservableProperty]`, `[RelayCommand]`, `ObservableObject` |
| `Dapper` | 2.1.79 | micro-ORM over `IDbConnection` (repos only) |
| `Microsoft.Data.Sqlite` | 8.0.x (e.g. 8.0.10) | SQLite ADO.NET provider, native bundled |
| `Microsoft.Extensions.DependencyInjection` | 8.0.x | DI container (bare `ServiceCollection`, no Generic Host) |
| `ClosedXML` | 0.105.0 | Excel `.xlsx` export |
| (Markdown export) | — | no package; custom `StringBuilder` |

**`TimesheetApp.Tests`:** `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Moq` (or `NSubstitute`) for repo mocking, `Microsoft.Data.Sqlite` (temp-file integration tests), `coverlet.collector`.

**Version-pin discipline (RESEARCH §1.2):** pin the **8.0.x** band for `Microsoft.Data.Sqlite` and all `Microsoft.Extensions.*` to match the .NET 8 runtime. Do NOT pull 10.x (targets .NET 10).

### 1.4 Folder structure (per design §8, extended for new seams)

```
TimesheetApp/
├── App.xaml / App.xaml.cs           -- DI composition root (§6)
├── Config/
│   ├── IAppConfig.cs                -- machine-local DB path (DATA-07, SET-01)
│   └── JsonAppConfig.cs             -- %APPDATA%\TimesheetApp\appsettings.json
├── Data/
│   ├── IConnectionFactory.cs        -- short-connection seam (XC-01)
│   ├── SqliteConnectionFactory.cs
│   ├── IDatabaseInitializer.cs / DatabaseInitializer.cs  -- schema+seed+migrations
│   └── Repositories/
│       ├── IUserRepository.cs       / UserRepository.cs
│       ├── IRequestRepository.cs    / RequestRepository.cs
│       ├── ITaskRepository.cs       / TaskRepository.cs
│       ├── ITimeLogRepository.cs    / TimeLogRepository.cs
│       ├── ISettingsRepository.cs   / SettingsRepository.cs
│       └── ITaskTemplateRepository.cs / TaskTemplateRepository.cs
├── Services/
│   ├── IClock.cs / SystemClock.cs   -- deterministic "today" (test seam)
│   ├── ITimeLogService.cs           / TimeLogService.cs
│   ├── ISmartInputService.cs        / SmartInputService.cs
│   ├── IExportService.cs            / ExportService.cs
│   ├── IDefaultTaskSyncService.cs   / DefaultTaskSyncService.cs
│   └── ICurrentUserService.cs       / CurrentUserService.cs
├── Models/
│   ├── User.cs, Request.cs, TaskItem.cs, TimeLog.cs, TaskTemplate.cs,
│   │   DefaultTask.cs, AppSettings keys
│   └── ReadModels.cs                -- TimeLogReportRow, CellAssignment, WeekGrid, ...
├── ViewModels/
│   ├── MainViewModel.cs, TimesheetViewModel.cs, RequestsViewModel.cs,
│   │   UsersViewModel.cs, ReportsViewModel.cs, SettingsViewModel.cs
│   └── TimesheetRowVm.cs            -- bindable Mon–Fri row
└── Views/
    ├── MainWindow.xaml
    ├── Tabs/ (Timesheet/Requests/Users/Reports/Settings .xaml)
    └── Dialogs/SelectUserDialog.xaml
```

---

## 2. Models

`work_date` is `DateOnly`, `hours` is `decimal` at every boundary above the repo. The repo maps `DateOnly↔'YYYY-MM-DD' TEXT` and `decimal↔REAL` at the Dapper edge so service/VM never touch the wire format (RESEARCH §1, ARCH §1 notes). `Task` collides with `System.Threading.Tasks.Task`, so the entity is named **`TaskItem`**.

```csharp
// --- Entities (1:1 with tables) ---

public sealed record User(int Id, string Name, string? WindowsUsername, bool IsActive);

public sealed record Request(int Id, string RequestCode, string Project, DateTimeOffset CreatedAt);

public sealed record TaskItem(int Id, int RequestId, string TaskName, int OrderIndex, bool IsActive);

public sealed record TimeLog(
    int Id, int UserId, int TaskId, DateOnly WorkDate, decimal Hours, DateTimeOffset CreatedAt);

public sealed record TaskTemplate(int Id, string TemplateName, string TaskName, int OrderIndex);

public sealed record DefaultTask(int Id, string TaskName, int OrderIndex, bool IsActive);

// Settings is key-value; no entity record needed (ISettingsRepository returns string/dict).

// --- Read-models (joins / projections; live in Models, not tables) ---

// Flat report/export join row — no is_active filter on the join (XC-06).
public sealed record TimeLogReportRow(
    int UserId, string UserName,
    string RequestCode, string Project,
    int TaskId, string TaskName,
    DateOnly WorkDate, decimal Hours);

// Smart-input preview cell (pure math output; also the apply unit).
public readonly record struct CellAssignment(DateOnly Date, decimal Hours);

// Shaped week for the Timesheet grid (one row per active task, 5 day slots).
public sealed record WeekGrid(DateOnly Monday, IReadOnlyList<WeekRow> Rows);
public sealed record WeekRow(
    int TaskId, string RequestCode, string TaskName, int OrderIndex,
    decimal? Mon, decimal? Tue, decimal? Wed, decimal? Thu, decimal? Fri);  // null = empty = 0h

// Result types
public readonly record struct SaveResult(bool Ok, string? Error);
public readonly record struct ExportFilter(int? UserId, int Year, int Month, string? Project);
public readonly record struct SmartInputResult(
    bool Ok, IReadOnlyList<CellAssignment> Cells, string? Error);  // Ok=false => no-op message (SI-03)

// Current-user resolution
public enum CurrentUserOutcome { Resolved, NeedsSelection }
public readonly record struct CurrentUserResult(CurrentUserOutcome Outcome, User? User);
```

`[ASSUMED]` `CreatedAt` modeled as `DateTimeOffset` (stored ISO-8601 UTC TEXT) since it is an instant, while `WorkDate` is a calendar `DateOnly` (RESEARCH §2.5).

**REQ coverage:** DATA-02 (schema shape), XC-04 (`decimal` hours), XC-05 (`DateOnly` work_date), RPT-01..03 / EXP-01..04 (`TimeLogReportRow`), SI-01/SI-05 (`CellAssignment`), TS-01/TS-05 (`WeekGrid`/`WeekRow`).

---

## 3. Repository Interfaces (Dapper only inside)

All methods `async`; each opens a **short** connection via `IConnectionFactory.Create()`, does the smallest unit of work, disposes. No long-lived connection. Connection string applies `Foreign Keys=True`, `Pooling=False`, and the factory runs `PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON;` on open. Repos contain SQL + Dapper only — no business rules, no `System.Windows.*`.

```csharp
public interface IConnectionFactory
{
    // Returns an OPEN connection with FK on, journal_mode=DELETE, pooling off.
    IDbConnection Create();
}

public interface IUserRepository
{
    Task<IReadOnlyList<User>> GetActiveAsync();
    Task<IReadOnlyList<User>> GetAllAsync();                       // Users tab shows inactive too (USR-01)
    Task<User?> GetByIdAsync(int id);
    Task<User?> GetByWindowsUsernameAsync(string windowsUsername); // XC-07 lookup
    Task<int>  InsertAsync(User user);                             // returns new id (USR-02)
    Task SetWindowsUsernameAsync(int userId, string windowsUsername); // XC-07 persist
    Task SetActiveAsync(int userId, bool isActive);                // soft delete (USR-03)
    Task UpdateNameAsync(int userId, string name);
}

public interface IRequestRepository
{
    Task<IReadOnlyList<Request>> SearchAsync(string? term);        // null => all; matches code OR project (REQ-01)
    Task<Request?> GetByIdAsync(int id);
    Task<Request?> GetByCodeAsync(string requestCode);             // find hidden 'DEFAULT' (DATA-03)
    Task<int>  InsertAsync(Request request);                       // REQ-02
    Task UpdateAsync(Request request);                             // edit name/project (REQ-03)
    // No SetActiveAsync — Requests are NOT soft-deletable in v1 (REQ-04, decision 4).
}

public interface ITaskRepository
{
    Task<IReadOnlyList<TaskItem>> GetActiveByRequestAsync(int requestId);
    Task<IReadOnlyList<TaskItem>> GetActiveForTimesheetAsync();    // active tasks across active requests + DEFAULT, ordered (TS-02)
    Task<TaskItem?> GetByIdAsync(int id);
    Task<TaskItem?> GetByNameInRequestAsync(int requestId, string taskName); // DEFAULT sync match by name (DATA-03/SET-04)
    Task<int>  InsertAsync(TaskItem task);                         // REQ-02/REQ-03/SET-04
    Task UpdateAsync(TaskItem task);                               // name + order_index
    Task SetActiveAsync(int taskId, bool isActive);                // soft delete (REQ-04/SET-04)
}

public interface ITimeLogRepository
{
    // Upsert on UNIQUE(user_id, task_id, work_date) via INSERT ... ON CONFLICT DO UPDATE (TS-07/SI-05).
    Task UpsertAsync(TimeLog log);
    Task DeleteAsync(int userId, int taskId, DateOnly workDate);   // empty cell => remove row (TS-03)
    Task<IReadOnlyList<TimeLog>> GetByUserAndRangeAsync(int userId, DateOnly from, DateOnly to);  // week grid + 8h day-total read (XC-03)
    Task<IReadOnlyList<TimeLogReportRow>> GetReportRowsAsync(int userId, DateOnly from, DateOnly to); // RPT-01..03
    Task<IReadOnlyList<TimeLogReportRow>> GetExportRowsAsync(DateOnly from, DateOnly to, string? projectFilter); // EXP-01..04
    Task<IReadOnlyList<int>> GetUserIdsWithLogsInRangeAsync(DateOnly from, DateOnly to);  // RPT-04 single-range scan
    // Batch upsert in one transaction for smart-input apply (SI-05 atomicity).
    Task UpsertBatchAsync(IReadOnlyList<TimeLog> logs);
}

public interface ISettingsRepository
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);                      // INSERT OR REPLACE (SET-02)
    Task<IReadOnlyDictionary<string,string>> GetAllAsync();
}

// RECONCILIATION (2026-06-21, user-approved): single home for TaskTemplates, used by BOTH
// RequestsViewModel (read all → VM groups by TemplateName, REQ-02) and SettingsViewModel (CRUD, SET-03).
// Template methods are NOT placed on ITaskRepository (that interface stays Task-only).
// DI: AddSingleton<ITaskTemplateRepository, TaskTemplateRepository>().
public interface ITaskTemplateRepository
{
    Task<IReadOnlyList<TaskTemplate>> GetAllAsync();   // all rows ordered by template_name,order_index — P4 read (VM groups) + P6 reload
    Task<int> InsertAsync(TaskTemplate template);      // add a template task row (SET-03)
    Task DeleteAsync(int id);                           // hard delete a template row — seed data, no TimeLog FK (SET-03)
}
```

**Notes & boundaries:**
- `UpsertAsync` SQL (RESEARCH §4.1, SQLite 3.24+ bundled):
  `INSERT INTO TimeLogs(user_id,task_id,work_date,hours,created_at) VALUES(@u,@t,@d,@h,@now) ON CONFLICT(user_id,task_id,work_date) DO UPDATE SET hours=excluded.hours;`
- `GetReportRowsAsync` / `GetExportRowsAsync` use **INNER JOIN by id with NO `is_active` filter** (XC-06, RESEARCH §2.3) so soft-deleted task names (incl. DEFAULT Annual Leave) still resolve.
- `UpsertBatchAsync` wraps all rows in one `BEGIN…COMMIT` for smart-input all-or-nothing (SI-05). `[ASSUMED]` placed in repo (transaction is a data concern); the service decides the row set after preview validation.
- `GetUserIdsWithLogsInRangeAsync` backs RPT-04 as a single range query (RESEARCH §4.1); active users absent from the result are flagged.

**REQ coverage:** DATA-01/02/03/06, XC-01/03/06, TS-02/03/07, REQ-01..04, USR-01..03, RPT-01..04, SET-02, EXP-01..04, SI-05.

---

## 4. Service Interfaces

Services own all business rules and pure math; they depend on **repo interfaces + `IClock`** only — never on `IDbConnection`/Dapper or `System.Windows.*`. They return results/throw typed exceptions; VMs translate to UI state.

```csharp
public interface IClock
{
    DateOnly Today { get; }          // injected for deterministic week/N-day tests
    DateTimeOffset UtcNow { get; }    // created_at stamping
}

public interface ITimeLogService
{
    // Single-cell inline edit. Enforces >0, ≤1 decimal, weekday-only, and the
    // per-cell 8h check AND per-save whole-day 8h cap (reads the day's other
    // logs from storage). Rounds to 1 decimal (AwayFromZero) before upsert.
    Task<SaveResult> SaveCellAsync(int userId, int taskId, DateOnly date, decimal hours);  // XC-02/03/04/05, TS-07
    Task ClearCellAsync(int userId, int taskId, DateOnly date);                             // TS-03 (empty=0 → delete)
    Task<WeekGrid> GetWeekAsync(int userId, DateOnly mondayOfWeek);                          // TS-01/02/05
    // True if every day in the proposed cell set stays ≤ 8h after merge — used by preview (SI-05).
    Task<SaveResult> ValidateDayTotalsAsync(int userId, IReadOnlyList<CellAssignment> cells, int taskId);
    // Commit a validated smart-input set atomically (delegates to UpsertBatchAsync).
    Task<SaveResult> ApplySmartInputAsync(int userId, int taskId, IReadOnlyList<CellAssignment> cells); // SI-05
    // Active users with zero logs in LastNWorkingDays(today, N) — today included (RPT-04).
    Task<IReadOnlyList<User>> GetUsersMissingLogsAsync(int workdayWindowN);                  // RPT-04
}

public interface ISmartInputService
{
    // Pure computation — no DB, no IClock dependency on storage. Returns preview cells (SI-06).
    SmartInputResult DistributeEven(DateOnly from, DateOnly to, decimal totalHours);  // SI-01/02/03
    SmartInputResult FillFull8h(DateOnly from, DateOnly to);                          // SI-02/04
}

public interface IExportService
{
    Task<byte[]> ExportExcelAsync(ExportFilter filter);      // ClosedXML → MemoryStream → ToArray (EXP-01)
    Task<string> ExportMarkdownAsync(ExportFilter filter);    // StringBuilder (EXP-02/03/04)
    // NEVER opens SaveFileDialog — returns bytes/string; the VM writes to disk.
}

public interface IDefaultTaskSyncService
{
    Task<int> EnsureDefaultRequestIdAsync();   // create/find request_code='DEFAULT' (DATA-03, idempotent)
    Task SyncAsync();                           // reconcile DefaultTasks → Tasks under DEFAULT (SET-04)
}

public interface ICurrentUserService
{
    Task<CurrentUserResult> ResolveAsync();     // Environment.UserName → lookup → Resolved|NeedsSelection (XC-07)
    Task SetWindowsUsernameAsync(int userId, string windowsUsername); // persist after dialog pick (XC-07)
    User? Current { get; }
}

public interface IDatabaseInitializer
{
    // Idempotent: CREATE TABLE IF NOT EXISTS; PRAGMA user_version migrations;
    // ensure DEFAULT request; seed DefaultTasks only if empty; initial DefaultTasks→Tasks sync.
    Task InitializeAsync();   // DATA-01/03/04/05
}
```

**Boundary rules (RESEARCH §4.2, ARCH §2/§5):**
- `SaveCellAsync` reads the day's other logs (`GetByUserAndRangeAsync(user, date, date)`) before validating the 8h cap — validation is **not** purely in-memory (XC-03).
- Rounding rule lives only in the service: `Math.Round(hours, 1, MidpointRounding.AwayFromZero)` before upsert; reject `>1`-decimal input and `<=0` (XC-04, SI-03).
- `ExportService` is WPF-free and returns `byte[]`/`string` (EXP-01..04 testable without disk).
- `CurrentUserService.ResolveAsync` reads `Environment.UserName` (stdlib, no WPF) and returns `NeedsSelection` — it **never** opens `SelectUserDialog`; the VM does (XC-07).
- `DatabaseInitializer` (Data, once at startup) vs `DefaultTaskSyncService` (Service, ongoing from Settings edits) — two distinct responsibilities sharing the same reconcile via `SyncAsync` (DATA-03/04 vs SET-04).
- `[ASSUMED]` `ValidateDayTotalsAsync` takes `taskId` so it can subtract the target task's existing same-day hours before adding the new value (`existingDayTotal − existingForThisTaskDay + newValue ≤ 8`, RESEARCH §2.2).

**REQ coverage:** XC-02..07, SI-01..06, RPT-04, EXP-01..04, DATA-01/03/04/05, SET-04.

---

## 5. ViewModel Responsibilities (6 VMs)

VMs use `CommunityToolkit.Mvvm` (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`). They never touch Dapper/SQL; services never touch `System.Windows.*`. VMs own dialogs, week-grid shaping into bindable rows, and red-cell error state.

| VM | Owns | Calls | Never |
|---|---|---|---|
| **MainViewModel** | TabControl host; current-user display (top corner); on startup calls `ICurrentUserService.ResolveAsync()`; on `NeedsSelection` opens `SelectUserDialog` then `SetWindowsUsernameAsync`; shows conflict-copy startup warning banner (XC-08); resolves child VMs via ctor injection | `ICurrentUserService`, `IDatabaseInitializer` (already run in App) | SQL |
| **TimesheetViewModel** | `CurrentWeek` (Monday), Prev/Next nav recomputing concrete column dates; `ObservableCollection<TimesheetRowVm>`; per-column footer totals (`MonTotal..FriTotal`); `SaveCommand.CanExecute=false` when any day >8 (TS-06); red-cell state via `INotifyDataErrorInfo`; hosts the Smart Input panel (two modes + preview) | `ITimeLogService` (`GetWeekAsync`/`SaveCellAsync`/`ClearCellAsync`), `ISmartInputService`, `IClock` | business math, SQL |
| **RequestsViewModel** | request list + search box; create dialog (code/project + optional template + add/remove/reorder tasks); edit (name + add task) | `IRequestRepository`, `ITaskRepository`, `ITaskTemplateRepository` (read templates) | SQL strings |
| **UsersViewModel** | user list w/ Active/Inactive; add user; soft-delete (set inactive) | `IUserRepository` | SQL |
| **ReportsViewModel** | weekly view (totals by day), monthly view (by request/task), drill-down TreeView (Project→Request→Task→Date); "chưa log" banner showing configured N (RPT-04) | `ITimeLogService` (`GetUsersMissingLogsAsync`), `ITimeLogRepository` (`GetReportRowsAsync`), `ISettingsRepository` (N) | SQL, business math |
| **SettingsViewModel** | DB path Browse → writes `IAppConfig`; N-days (default 3) → `Settings` table; TaskTemplates CRUD; DefaultTasks add/edit/hide → then calls `IDefaultTaskSyncService.SyncAsync()` | `IAppConfig`, `ISettingsRepository`, `ITaskTemplateRepository` (template CRUD), `IDefaultTaskSyncService` | SQL |

`TimesheetRowVm : ObservableObject` holds `TaskId`, labels, and 5 nullable `decimal?` day properties (`null=empty=0h`); a `DayChanged` handler raises `SaveCommand.NotifyCanExecuteChanged()` and recomputes footer totals (RESEARCH §4.3). Columns are **static** Mon–Fri XAML (Sat/Sun do not exist as columns — cleanest XC-05 enforcement); headers bind dates via `RelativeSource` to the grid DataContext.

**REQ coverage:** TS-01..07, SI-05/06, REQ-01..04, USR-01..03, RPT-01..04, SET-01..04, XC-07/08.

---

## 6. DI Wiring (App.xaml.cs)

Bare `Microsoft.Extensions.DependencyInjection.ServiceCollection` (no Generic Host). Repos + services **singleton** (stateless — connection opened per method, nothing held). VMs **transient**. `IDatabaseInitializer.InitializeAsync()` runs **once before** the first window.

```csharp
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var sc = new ServiceCollection();

        // Config + connection seam
        sc.AddSingleton<IAppConfig, JsonAppConfig>();                 // %APPDATA% DB path (DATA-07)
        sc.AddSingleton<IClock, SystemClock>();
        sc.AddSingleton<IConnectionFactory, SqliteConnectionFactory>(); // FK=True, Pooling=False, journal=DELETE (XC-01)

        // Repositories (singletons — stateless)
        sc.AddSingleton<IUserRepository, UserRepository>();
        sc.AddSingleton<IRequestRepository, RequestRepository>();
        sc.AddSingleton<ITaskRepository, TaskRepository>();
        sc.AddSingleton<ITimeLogRepository, TimeLogRepository>();
        sc.AddSingleton<ISettingsRepository, SettingsRepository>();
        sc.AddSingleton<ITaskTemplateRepository, TaskTemplateRepository>();

        // Services
        sc.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        sc.AddSingleton<IDefaultTaskSyncService, DefaultTaskSyncService>();
        sc.AddSingleton<ITimeLogService, TimeLogService>();
        sc.AddSingleton<ISmartInputService, SmartInputService>();
        sc.AddSingleton<IExportService, ExportService>();
        sc.AddSingleton<ICurrentUserService, CurrentUserService>();

        // ViewModels (transient)
        sc.AddTransient<MainViewModel>();
        sc.AddTransient<TimesheetViewModel>();
        sc.AddTransient<RequestsViewModel>();
        sc.AddTransient<UsersViewModel>();
        sc.AddTransient<ReportsViewModel>();
        sc.AddTransient<SettingsViewModel>();

        Services = sc.BuildServiceProvider();

        // One-time bootstrap BEFORE first window: schema + migrations + DEFAULT seed + sync
        await Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        var main = new MainWindow { DataContext = Services.GetRequiredService<MainViewModel>() };
        main.Show();
    }
}
```

`[ASSUMED]` `OnStartup` is `async void` to `await InitializeAsync()` before showing the window; alternatively initialize synchronously via `.GetAwaiter().GetResult()`. The DB-path bootstrap order is: `JsonAppConfig` reads `%APPDATA%\TimesheetApp\appsettings.json` (default path on first run) → `SqliteConnectionFactory` uses it → initializer creates the file.

**REQ coverage:** XC-01, DATA-01/03/04/05/07.

---

## 7. Key Algorithms

### 7.1 Integer-tenths even distribution (SI-01) — `SmartInputService.DistributeEven`
```
reject if totalHours <= 0 or has >1 decimal  → SmartInputResult(Ok:false, message)   (SI-03)
days = WorkingDays(from, to)                  // Mon–Fri only (SI-02)
if days.Count == 0 → SmartInputResult(Ok:false, "no working days")                    (SI-03)
totalTenths = round(totalHours * 10)          // 10.0 → 100
baseTenths  = totalTenths / days.Count        // floor 100/3 = 33 → 3.3h
remainder   = totalTenths % days.Count        // 1
each day = baseTenths / 10m; last working day += remainder / 10m   // 3.3,3.3,3.4
```
Parts sum to total exactly (no float drift) — property test: `cells.Sum(Hours) == totalHours`. Edge cases: exact divide `9/3→3,3,3`; single day `5.5/1→[5.5]`; large remainder `10/7→six×1.4 + 1.6`.

### 7.2 Working-day enumeration (SI-02/SI-04/XC-05)
```
WorkingDays(from,to): iterate from→to inclusive, skip DayOfWeek.Saturday/Sunday (InvariantCulture-independent)
FillFull8h(from,to): WorkingDays(from,to).Select(d => new CellAssignment(d, 8m))     (SI-04)
MondayOf(date): date.AddDays(-((int)date.DayOfWeek + 6) % 7)   // week start hard-coded Monday, NOT culture
```

### 7.3 LastNWorkingDays(today, N) — includes today (RPT-04, decisions 3 & 6)
```
walk backward from today, skip weekends, collect until count==N
earliest = the Nth such day; query GetUserIdsWithLogsInRangeAsync(earliest, today)
active users NOT in result set → flagged "[Name] chưa log trong N ngày" (banner shows configured N)
```

### 7.4 Upsert / delete semantics (TS-03/07, SI-05)
- Cell with value → `UpsertAsync` on natural key `UNIQUE(user_id,task_id,work_date)` (no duplicate rows; idempotent on the natural key, not surrogate id).
- Cleared cell → `DeleteAsync(user,task,date)` (empty=0, no zero-row persisted).
- Smart-input apply → `ValidateDayTotalsAsync` in preview, then `UpsertBatchAsync` in one transaction (all-or-nothing).

### 7.5 DEFAULT seeding + DefaultTasks→Tasks sync (DATA-03/04, SET-04)
1. `EnsureDefaultRequestIdAsync`: `INSERT … SELECT 'DEFAULT','DEFAULT',@now WHERE NOT EXISTS(… request_code='DEFAULT')` → return id (idempotent).
2. Seed `DefaultTasks` rows **only if the table is empty** (don't overwrite renamed/hidden — DATA-04).
3. `SyncAsync`: load active DefaultTasks + Tasks under DEFAULT; match by `task_name`:
   - in DefaultTasks not in Tasks → `InsertAsync` under DEFAULT;
   - in Tasks(DEFAULT) but DefaultTask gone/inactive → `SetActiveAsync(false)` (preserves TimeLogs);
   - rename = unmatched old name → soft-delete old + insert new (TimeLogs preserved — decision 7, flag UAT).

### 7.6 Conflict-copy detection (XC-08)
On startup scan the DB folder for `*-<MACHINE>.db` siblings (OneDrive conflict-copy pattern); if any exist, `MainViewModel` shows a visible warning banner. Turns silent data-loss into a visible event (RESEARCH §2.1 mitigation 6).

---

## 8. Traceability Matrix (component → REQ-IDs)

| Architecture component | REQ-IDs satisfied |
|---|---|
| `DatabaseInitializer` (schema/migrations/seed) | DATA-01, DATA-02, DATA-03, DATA-04, DATA-05 |
| `SqliteConnectionFactory` + conn string | DATA-06, XC-01 |
| `IAppConfig` / `JsonAppConfig` | DATA-07, SET-01 |
| `ISettingsRepository` | DATA-07, SET-02, SET-03 (template store) |
| `ITimeLogService` (validation/round/day-total) | XC-02, XC-03, XC-04, XC-05, TS-04, TS-06 |
| `ITimeLogRepository` (upsert/delete/report joins) | XC-06, TS-03, TS-07, RPT-01, RPT-02, RPT-03, EXP-01..04 |
| `ICurrentUserService` + `MainViewModel` dialog | XC-07 |
| Startup conflict-copy scan (`MainViewModel`) | XC-08 |
| `ISmartInputService` (pure math) | SI-01, SI-02, SI-03, SI-04 |
| `ITimeLogService.ValidateDayTotals/ApplySmartInput` + `UpsertBatchAsync` | SI-05 |
| `TimesheetViewModel` Smart Input panel | SI-06 |
| `TimesheetViewModel` + `TimesheetRowVm` + grid XAML | TS-01, TS-02, TS-04, TS-05, TS-06 |
| `RequestsViewModel` + `IRequestRepository`/`ITaskRepository` | REQ-01, REQ-02, REQ-03, REQ-04 |
| `UsersViewModel` + `IUserRepository` | USR-01, USR-02, USR-03 |
| `ReportsViewModel` + `GetUsersMissingLogsAsync` | RPT-01, RPT-02, RPT-03, RPT-04 |
| `SettingsViewModel` + `IDefaultTaskSyncService` | SET-01, SET-02, SET-03, SET-04 |
| `ExportService` (ClosedXML / StringBuilder) | EXP-01, EXP-02, EXP-03, EXP-04 |

**Gap check:** All **43** REQs have an owning component. No orphan REQ. Reverse check: every interface method traces to ≥1 REQ (no speculative surface added).

---

## 9. Test Seams

| Layer | Strategy | Seam |
|---|---|---|
| **Repositories** | Integration against a **temp-file SQLite** (`Data Source=<tempfile>` or `:memory:` shared-cache); assert CRUD/upsert/ON-CONFLICT, FK rejection, `DatabaseInitializer` idempotency (run twice → no dup DEFAULT request/tasks) | `IConnectionFactory` pointed at temp DB |
| **Services** | Pure unit tests with **mocked repo interfaces** + fake `IClock`; `SmartInputService` needs no mocks (pure); `TimeLogService` 8h-cap with mocked `GetByUserAndRangeAsync`; `ExportService` asserts markdown text / reopens `.xlsx` bytes via ClosedXML in-test | repo interfaces + `IClock` |
| **ViewModels** | Light tests with mocked services where logic exists (CanExecute toggles, footer totals); dialogs excluded | service interfaces |
| **UI/manual** | `SelectUserDialog`, inline-edit red border, conflict-copy startup banner | — |

Determinism: inject `IClock` so "today"/week-window/N-day tests are reproducible. `ExportService` returning `byte[]`/`string` (no `SaveFileDialog`) and `CurrentUserService` returning an outcome enum (no dialog) are the two seams that keep services headless-testable.

---

## 10. Research Mitigations NOT in v1 REQs — Promote vs Defer

REQUIREMENTS.md (line 270) flags three research mitigations left out of v1. As architecture lead I recommend:

| Mitigation (RESEARCH §2.1) | Recommendation | Reason |
|---|---|---|
| **Advisory single-editor lock** (`Settings.editing_lock = user@timestamp`, warn other users on open) — mitigation 4 | **DEFER** (do not promote to v1 REQ) | Advisory-only and racy over OneDrive (the lock row itself rides the same eventually-consistent sync, so it can be stale/lost exactly when it matters). It adds UX surface + a Settings write path for partial protection. The accepted-risk concurrency model (design §4) plus XC-08 conflict-copy detection already make the dominant failure mode *visible*. Revisit only if UAT shows frequent collisions. |
| **Verify `-journal` gone after each write batch** — mitigation 3 | **PROMOTE to a small v1 REQ** | Cheap, deterministic, no UX cost: after a write transaction, the connection factory / repo can assert the sibling `<db>-journal` file is absent; if present, a transaction was interrupted → warn rather than let OneDrive sync a half-state. Low effort, directly reduces the HIGH-impact mid-transaction-copy corruption (RESEARCH §2.1c). Suggested ID **XC-09**. `[ASSUMED]` — needs a one-line user OK before planning since it is new scope. |
| **Cheap backup before bulk writes** (copy `.db` before smart-input apply / template seed) — mitigation 7 | **PROMOTE, scoped to bulk only** | Smart-input apply + DefaultTasks seed are the only multi-row writes; a `File.Copy(db, db+".bak")` while no transaction is open is trivial and bounds blast radius of a corrupting bulk write. Keep it to bulk operations (not every cell edit) to avoid churn on OneDrive. Suggested ID **XC-10**. `[ASSUMED]` — new scope, needs user OK. |

> Recommendation summary: **defer the advisory lock; promote `-journal` verification (XC-09) and pre-bulk backup (XC-10)** as two small additive REQs before STEP 6 planning. Both are low-effort, high-leverage against the CRITICAL OneDrive-corruption risk and do not reopen any approved decision. They are flagged here rather than silently added to honor the REQUIREMENTS.md no-fabrication rule.

---

## Appendix — Honored constraints (no reopening)

8 resolved decisions (design Appendix) + `journal_mode=DELETE`, all encoded above:
1. Markdown DEFAULT header grouped by **task name** → §2 read-model + EXP-03.
2. Smart Input **overwrites** (upsert), validated post-merge in preview → §4 `ApplySmartInput`/`ValidateDayTotals`, §7.4.
3. "Chưa log" window **includes today** → §7.3.
4. Requests **not** soft-deletable (no `is_active`, no `SetActiveAsync` on `IRequestRepository`) → §3.
5. 8h validation **per-cell + per-save** → §4 `SaveCellAsync` (both levels), TS-04/TS-06.
6. Banner shows **configured N** (not actual gap) → §7.3, ReportsViewModel.
7. DefaultTask rename = **soft-delete + insert** (TimeLogs preserved) → §7.5.
8. Hours **REAL**, round 1 decimal `AwayFromZero` before upsert → §4 rounding rule.
- `journal_mode=DELETE` (not WAL) + `Pooling=False` + `Foreign Keys=True` + short connections → §3, §6, XC-01.
