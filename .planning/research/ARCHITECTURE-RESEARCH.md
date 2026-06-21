# Architecture Research — WPF Timesheet Tool

**Date:** 2026-06-21
**Spec:** `docs/superpowers/specs/2026-06-21-timesheet-tool-design.md`
**Scope:** Layering/boundaries for SQLite → Repository (Dapper) → Service → ViewModel → WPF.

Claim tags: `[VERIFIED]` (tooling/stdlib fact), `[CITED]` (URL), `[ASSUMED]` (design choice derivable from spec, not externally verified).

---

## 0. Layer responsibility cheat-sheet `[ASSUMED]`

| Layer | Owns | Must NOT |
|---|---|---|
| Repository | SQL strings, Dapper calls, `IDbConnection` lifetime, row↔model mapping | business rules, validation, WPF types |
| Service | validation, distribution math, seeding/sync, export, current-user resolution policy | `IDbConnection`/Dapper, `MessageBox`/`OpenFileDialog`, `DataGrid` |
| ViewModel | `INotifyPropertyChanged`, `ICommand`, week-grid shaping, calling services, surfacing validation errors to UI | SQL, file I/O details, business math |
| View (XAML) | layout, bindings, dialogs | logic of any kind |

Key boundary rule `[ASSUMED]`: **Services return results/throw typed exceptions; ViewModels translate those into UI state.** No service references `System.Windows.*`.

---

## 1. Repository contracts

All repos take a connection factory (Section 6), open a **short** connection per call (matches spec §4 concurrency: open→op→close) `[ASSUMED]`. All methods are `async` — Microsoft.Data.Sqlite + Dapper support async `[VERIFIED]`.

```csharp
public interface IUserRepository
{
    Task<IReadOnlyList<User>> GetActiveAsync();
    Task<IReadOnlyList<User>> GetAllAsync();                 // Users tab shows inactive too
    Task<User?> GetByIdAsync(int id);
    Task<User?> GetByWindowsUsernameAsync(string windowsUsername);
    Task<int>  InsertAsync(User user);                       // returns new id
    Task SetWindowsUsernameAsync(int userId, string windowsUsername);
    Task SetActiveAsync(int userId, bool isActive);          // soft delete
    Task UpdateNameAsync(int userId, string name);
}

public interface IRequestRepository
{
    Task<IReadOnlyList<Request>> SearchAsync(string? term); // term null => all; matches request_code OR project
    Task<Request?> GetByIdAsync(int id);
    Task<Request?> GetByCodeAsync(string requestCode);       // used to find hidden 'DEFAULT'
    Task<int>  InsertAsync(Request request);
    Task UpdateAsync(Request request);
}

public interface ITaskRepository
{
    Task<IReadOnlyList<TaskItem>> GetActiveByRequestAsync(int requestId);
    Task<IReadOnlyList<TaskItem>> GetActiveForTimesheetAsync(); // active tasks across active requests + DEFAULT, ordered
    Task<TaskItem?> GetByIdAsync(int id);
    Task<TaskItem?> GetByNameInRequestAsync(int requestId, string taskName); // for DEFAULT sync match
    Task<int>  InsertAsync(TaskItem task);
    Task UpdateAsync(TaskItem task);                          // name + order_index
    Task SetActiveAsync(int taskId, bool isActive);           // soft delete
}

public interface ITimeLogRepository
{
    // upsert keyed by UNIQUE(user_id, task_id, work_date) — see §3.2 schema
    Task UpsertAsync(TimeLog log);                            // INSERT ... ON CONFLICT DO UPDATE
    Task DeleteAsync(int userId, int taskId, DateOnly workDate); // empty cell => remove row
    Task<IReadOnlyList<TimeLog>> GetByUserAndRangeAsync(int userId, DateOnly from, DateOnly to);
    Task<IReadOnlyList<TimeLogReportRow>> GetReportRowsAsync(int userId, DateOnly from, DateOnly to); // joined: project/request_code/task_name
    Task<DateOnly?> GetLastLogDateAsync(int userId);          // for "chưa log N ngày" banner
    // for export across all users / month / optional project filter
    Task<IReadOnlyList<TimeLogReportRow>> GetExportRowsAsync(DateOnly from, DateOnly to, string? projectFilter);
}

public interface ISettingsRepository
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);                 // INSERT OR REPLACE
    Task<IReadOnlyDictionary<string,string>> GetAllAsync();
}

// supporting read-model for joins (lives in Models, not a table)
public sealed record TimeLogReportRow(
    int UserId, string UserName,
    string RequestCode, string Project,
    int TaskId, string TaskName,
    DateOnly WorkDate, decimal Hours);
```

Notes:
- `UpsertAsync` uses SQLite `INSERT ... ON CONFLICT(user_id,task_id,work_date) DO UPDATE` — supported by SQLite 3.24+, which Microsoft.Data.Sqlite bundles `[VERIFIED]`.
- `hours` is `REAL` in DB but exposed as `decimal` to callers so the 1-decimal rule is exact; repo maps `decimal`↔`double` at the Dapper boundary `[ASSUMED]`.
- `work_date` stored as `'YYYY-MM-DD'` TEXT (spec §3.2); repo maps `DateOnly`↔`string` so service/VM never touch the string format `[ASSUMED]`.

---

## 2. Service boundaries

```csharp
public interface ITimeLogService
{
    // Single-cell inline edit. Enforces: >0, ≤1 decimal, weekday-only,
    // and 8h/day/user cap (sums other tasks that day). Upserts on success.
    Task<SaveResult> SaveCellAsync(int userId, int taskId, DateOnly date, decimal hours);
    Task ClearCellAsync(int userId, int taskId, DateOnly date);   // hours==0 / empty
    Task<WeekGrid> GetWeekAsync(int userId, DateOnly mondayOfWeek); // shaped Mon–Fri
    Task<int> CountDaysSinceLastLogAsync(int userId, int workdayWindowN); // weekday count
}

public readonly record struct SaveResult(bool Ok, string? Error);

public interface ISmartInputService
{
    // pure computation — no DB. Returns proposed cells for PREVIEW (spec §5.2).
    IReadOnlyList<CellAssignment> DistributeEven(DateOnly from, DateOnly to, decimal totalHours);
    IReadOnlyList<CellAssignment> FillFull8h(DateOnly from, DateOnly to);
    // remainder lands on last workday; weekends skipped (spec §7)
}
public readonly record struct CellAssignment(DateOnly Date, decimal Hours);

public interface IExportService
{
    Task<byte[]> ExportExcelAsync(ExportFilter filter);  // ClosedXML, returns bytes
    Task<string> ExportMarkdownAsync(ExportFilter filter); // returns markdown text
}
public readonly record struct ExportFilter(int? UserId, int Year, int Month, string? Project);
```

What belongs where `[ASSUMED]`:

| Concern | Service | ViewModel |
|---|---|---|
| 8h cap / positive / 1-decimal / weekday rule | ✅ `TimeLogService` | reads `SaveResult.Error`, paints cell red, blocks save |
| Even-split / full-8h math | ✅ `SmartInputService` (pure) | binds preview grid, "Apply" calls `SaveCellAsync` per cell |
| Excel/Markdown string + workbook build | ✅ `ExportService` | only triggers `SaveFileDialog`, writes returned bytes/text to disk |
| Week navigation (Prev/Next), grid shaping | `GetWeekAsync` returns model | maps to bindable rows/columns, owns `CurrentWeek` |
| Choosing the file path / showing dialogs | ❌ | ✅ (WPF dialogs live in VM/View) |

Critical split: **ExportService produces `byte[]`/`string`; it never touches `SaveFileDialog`** — keeps it unit-testable and WPF-free `[ASSUMED]`. ClosedXML `XLWorkbook.SaveAs(Stream)` writes to a `MemoryStream` → `ToArray()` `[CITED]` https://docs.closedxml.io/.

The **8h cap requires reading the day's other logs**, so `SaveCellAsync` must hit the repo before validating — validation is not purely in-memory `[ASSUMED]`.

---

## 3. DI / composition — RECOMMENDATION: Microsoft.Extensions.DependencyInjection

Recommend `Microsoft.Extensions.DependencyInjection` (+ `Microsoft.Extensions.Hosting` optional) over manual wiring, even for a small app: clean lifetime control, easy test substitution, idiomatic on .NET 8 `[VERIFIED]`. Generic Host is overkill here; a bare `ServiceCollection` in `App.xaml.cs` is enough `[ASSUMED]`.

Wiring shape (`App.xaml.cs`):

```csharp
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        var sc = new ServiceCollection();

        // config / connection factory (DB path resolved from app-local config, see §5)
        sc.AddSingleton<IConnectionFactory, SqliteConnectionFactory>();

        // repositories — stateless, register transient or singleton (no state) 
        sc.AddSingleton<IUserRepository, UserRepository>();
        sc.AddSingleton<IRequestRepository, RequestRepository>();
        sc.AddSingleton<ITaskRepository, TaskRepository>();
        sc.AddSingleton<ITimeLogRepository, TimeLogRepository>();
        sc.AddSingleton<ISettingsRepository, SettingsRepository>();

        // services
        sc.AddSingleton<ITimeLogService, TimeLogService>();
        sc.AddSingleton<ISmartInputService, SmartInputService>();
        sc.AddSingleton<IExportService, ExportService>();
        sc.AddSingleton<IDefaultTaskSyncService, DefaultTaskSyncService>();
        sc.AddSingleton<ICurrentUserService, CurrentUserService>();
        sc.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();

        // viewmodels — transient (fresh per window/tab) 
        sc.AddTransient<MainViewModel>();
        sc.AddTransient<TimesheetViewModel>();
        sc.AddTransient<RequestsViewModel>();
        sc.AddTransient<UsersViewModel>();
        sc.AddTransient<ReportsViewModel>();
        sc.AddTransient<SettingsViewModel>();

        Services = sc.BuildServiceProvider();

        // one-time DB bootstrap (schema + DEFAULT seed + sync) BEFORE first window
        Services.GetRequiredService<IDatabaseInitializer>().Initialize();

        var main = new MainWindow { DataContext = Services.GetRequiredService<MainViewModel>() };
        main.Show();
        base.OnStartup(e);
    }
}
```

Notes `[ASSUMED]`:
- Repos/services are stateless → `Singleton` is fine and cheapest. (Connection is opened **per method**, so singleton repos don't hold a connection.)
- ViewModels `Transient`. `MainViewModel` resolves child VMs via constructor injection.
- No third-party DI container needed.

---

## 4. DEFAULT request seeding + DefaultTasks → Tasks sync

Two distinct responsibilities `[ASSUMED]`:

1. **Schema creation + idempotent seed** → `DatabaseInitializer` (Data layer, runs once at startup).
2. **Ongoing sync** when Settings tab edits `DefaultTasks` → `DefaultTaskSyncService` (Service layer).

```csharp
public interface IDatabaseInitializer
{
    void Initialize();   // CREATE TABLE IF NOT EXISTS …; ensure DEFAULT request; seed defaults
}

public interface IDefaultTaskSyncService
{
    // Reconcile DefaultTasks rows -> Tasks under the hidden 'DEFAULT' request.
    // add new, soft-delete (is_active=0) removed, rename + reorder changed.
    Task SyncAsync();
    Task<int> EnsureDefaultRequestIdAsync();  // create/find request_code='DEFAULT'
}
```

Contract `[ASSUMED]`:
- `EnsureDefaultRequestIdAsync` upserts the hidden request (`request_code='DEFAULT'`, `project='DEFAULT'`) and returns its id. Idempotent.
- `SyncAsync` algorithm: load active `DefaultTasks`; load `Tasks` under DEFAULT request; **match by `task_name`** (no FK between the two tables):
  - in DefaultTasks not in Tasks → `TaskRepository.InsertAsync` under DEFAULT request.
  - in Tasks(DEFAULT) but DefaultTask now inactive/removed → `SetActiveAsync(false)` (soft delete — preserves TimeLogs per §3.3/§7).
  - name/order changed → `UpdateAsync`.
- Called from: `DatabaseInitializer.Initialize()` (first run) **and** `SettingsViewModel` after a DefaultTasks edit `[ASSUMED]`.
- Reports/Export distinguish the group by `request_code='DEFAULT'` (spec §3.3) — already covered by the join in `TimeLogReportRow`.

Why a separate sync service: keeps the reconcile logic testable against repo interfaces, and reusable from both init and Settings tab `[ASSUMED]`.

---

## 5. Current-user resolution flow + settings locality

```csharp
public interface ICurrentUserService
{
    Task<CurrentUserResult> ResolveAsync();   // call once at startup, after DB init
    User? Current { get; }
}

public enum CurrentUserOutcome { Resolved, NeedsSelection }
public readonly record struct CurrentUserResult(CurrentUserOutcome Outcome, User? User);
```

Flow across layers `[ASSUMED]`:

| Step | Where | Detail |
|---|---|---|
| 1. read `Environment.UserName` | `CurrentUserService` (Service) | `Environment.UserName` is a stdlib call, no WPF dep `[VERIFIED]` |
| 2. lookup `Users.windows_username` | `UserRepository.GetByWindowsUsernameAsync` | returns `Resolved` if hit |
| 3. fallback dialog | **ViewModel/View** (`SelectUserDialog`) | service returns `NeedsSelection`; VM owns the dialog (WPF) |
| 4. persist mapping | `UserRepository.SetWindowsUsernameAsync` | called by VM after user picks → updates DB |

Boundary point: the **service decides the policy and reports `NeedsSelection`; it does NOT open the dialog.** The `MainViewModel` shows `SelectUserDialog`, then calls back to persist. This keeps `CurrentUserService` WPF-free and testable `[ASSUMED]`.

### Settings locality — machine-local vs shared DB

Spec §3.2 note explicitly allows splitting `[CITED]` (spec line 116).

| Setting | Store | Rationale |
|---|---|---|
| **Database path** (`.db` location) | **App-local config** (e.g. `%APPDATA%\TimesheetApp\appsettings.json`) | chicken-and-egg: you can't read DB to find the DB; per-machine OneDrive path differs `[ASSUMED]` |
| **Current-user → Windows username mapping** | mixed: identity stored in **DB** (`Users.windows_username`, shared so any machine maps), but the *active selection* is just resolved at startup, not persisted machine-side | spec §5.1 stores mapping in DB; works across machines `[ASSUMED]` |
| **"Cảnh báo chưa log" N days** | **DB `Settings` table** | shared team policy, same for everyone `[ASSUMED]` |
| **TaskTemplates / DefaultTasks** | **DB** (own tables) | shared data `[ASSUMED]` |

Rule of thumb `[ASSUMED]`: *anything needed to **find/open** the DB → app-local config; anything that is **team-shared policy or data** → DB.* So `SettingsRepository` covers DB-side settings; a tiny `IAppConfig` (JSON file) covers DB path only.

```csharp
public interface IAppConfig            // machine-local, NOT in shared DB
{
    string DatabasePath { get; set; }
    void Save();
}
```

`SqliteConnectionFactory` depends on `IAppConfig.DatabasePath`, so changing the path in Settings → rewrite app-local config → factory picks it up `[ASSUMED]`.

---

## 6. Testability

Keep services pure of WPF/file deps via two seams `[ASSUMED]`:

```csharp
public interface IConnectionFactory
{
    IDbConnection Create();   // returns an OPEN or openable SqliteConnection
}

public sealed class SqliteConnectionFactory(IAppConfig cfg) : IConnectionFactory
{
    public IDbConnection Create() => new SqliteConnection($"Data Source={cfg.DatabasePath}");
}
```

Testability rules:
- **Repos depend on `IConnectionFactory`, not a concrete connection** → integration tests inject a factory pointing at an in-memory/temp SQLite file `[VERIFIED]` (Microsoft.Data.Sqlite supports `Data Source=:memory:` / shared cache and temp files). Dapper stays entirely inside repos.
- **Services depend on repo interfaces only** → unit tests use fakes/mocks; no DB, no Dapper, no SQLite needed.
- **`SmartInputService` is pure** (no deps) → trivial unit tests for the `10h/3day → 3.3/3.3/3.4` remainder rule (spec §5.2) and weekend-skip.
- **`ExportService` returns `byte[]`/`string`** → assert markdown text / open the produced `.xlsx` bytes with ClosedXML in-test; no `SaveFileDialog`, no disk required `[CITED]` https://docs.closedxml.io/.
- **`CurrentUserService` returns an outcome enum** instead of opening a dialog → unit-testable; the WPF dialog is exercised only via VM/manual test.
- **Time:** inject an `Func<DateOnly>`/`IClock` into `TimeLogService` and `SmartInputService` so "today"/week-window/"N days since last log" tests are deterministic `[ASSUMED]`.

Suggested test split `[ASSUMED]`:
- Unit (no I/O): `SmartInputService`, `TimeLogService` validation, `ExportService` formatting, `DefaultTaskSyncService` reconcile (mock repos).
- Integration (temp SQLite): each repository's CRUD/upsert + `DefaultTaskSyncService` end-to-end + `DatabaseInitializer` idempotency.
- Manual/UI: `SelectUserDialog`, week-grid inline edit red-warning.

---

## Open risks / flags

- **Dapper `decimal`↔SQLite `REAL` precision** `[ASSUMED]`: store hours as REAL but enforce/round to 1 decimal in `TimeLogService` before upsert; compare with a tolerance in tests. Consider storing as INTEGER tenths if exactness ever bites.
- **Concurrency (spec §4 last-write-wins)** `[CITED]` spec §4: short connections only; do NOT hold a singleton open `IDbConnection`. The `IConnectionFactory.Create()`-per-call pattern enforces this.
- **DEFAULT sync matches by name** `[ASSUMED]`: renaming a DefaultTask is ambiguous (rename vs delete+add). v1 treats unmatched old name as soft-delete + new insert — acceptable since TimeLogs are preserved; flag for UAT.

## Sources
- [NuGet — ClosedXML 0.105.0](https://www.nuget.org/packages/ClosedXML/)
- [ClosedXML documentation (SaveAs/Stream)](https://docs.closedxml.io/)
