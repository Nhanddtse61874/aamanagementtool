---
# must_haves (goal-backward) — Phase P1: Data + Schema
observable_truths:
  - "On a fresh path, App startup creates a valid .db with all 7 tables (Users, Requests, Tasks, TaskTemplates, TimeLogs, DefaultTasks, Settings); running init twice creates no duplicate tables or rows (DATA-01)."
  - "Schema matches spec §2/§3 exactly: Users.windows_username nullable; TimeLogs has single FK task_id + hours REAL + UNIQUE(user_id,task_id,work_date); Tasks.request_id FK→Requests; is_active on Users/Tasks/DefaultTasks; Requests has NO is_active (DATA-02)."
  - "Exactly one hidden Request request_code='DEFAULT' exists after init, idempotent across re-runs (DATA-03)."
  - "DefaultTasks seed set is inserted only when the DefaultTasks table is empty; a renamed/hidden default is NOT overwritten on relaunch (DATA-04)."
  - "PRAGMA user_version drives forward-only additive migrations inside a transaction (DATA-05)."
  - "Every opened connection enforces foreign keys: inserting a TimeLog with a non-existent task_id is rejected (DATA-06)."
  - "Settings(key,value) table backs shared settings; the DB path itself lives in app-local config not the shared DB (DATA-07)."
  - "journal_mode=DELETE + Pooling=False + short per-op connections: no -wal/-shm sidecar is ever created (XC-01)."
  - "Startup scans the DB folder for *-<MACHINE>.db conflict-copy siblings and reports them (XC-08)."
  - "After a committed write, the <db>-journal sidecar is gone; if it persists the helper reports a warning (XC-09)."
required_artifacts:
  - "src/TimesheetApp.sln + src/TimesheetApp/TimesheetApp.csproj (net8.0-windows, WinExe, UseWPF) + src/TimesheetApp.Tests/TimesheetApp.Tests.csproj (net8.0)."
  - "src/TimesheetApp/Models/*.cs — entity records (User, Request, TaskItem, TimeLog, TaskTemplate, DefaultTask) using names VERBATIM from spec §2."
  - "src/TimesheetApp/Config/IAppConfig.cs + JsonAppConfig.cs — %APPDATA% DB-path locality split (DATA-07)."
  - "src/TimesheetApp/Data/IConnectionFactory.cs + SqliteConnectionFactory.cs — FK on, journal_mode=DELETE, Pooling=False (XC-01, DATA-06)."
  - "src/TimesheetApp/Data/SqliteMaintenance.cs — conflict-copy scan (XC-08) + verify-journal-gone helper (XC-09)."
  - "src/TimesheetApp/Data/IDatabaseInitializer.cs + DatabaseInitializer.cs — idempotent CREATE TABLE IF NOT EXISTS for all 7 tables + user_version migrations + DEFAULT request seed + DefaultTasks-when-empty seed (DATA-01..05)."
key_links:
  - "SqliteConnectionFactory.Create() opens connection → runs PRAGMA journal_mode=DELETE + PRAGMA foreign_keys=ON every time → repos/initializer consume IDbConnection."
  - "DatabaseInitializer.InitializeAsync() uses IConnectionFactory.Create() → CREATE TABLE IF NOT EXISTS (all 7) → user_version migration loop → EnsureDefaultRequest → seed DefaultTasks if empty."
  - "JsonAppConfig.DbPath → SqliteConnectionFactory connection string → the .db file the initializer creates."
  - "SqliteMaintenance.FindConflictCopies(dbPath) + EnsureJournalGone(dbPath) operate on the same path JsonAppConfig.DbPath yields."
---

# Phase P1 — Data + Schema Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each .NET coding task MUST load `skills/implementer-dotnet-csharp/SKILL.md` (Mode A skill) or dispatch the `implementer-dotnet-csharp` agent (Mode B) per `stack-skill-rule-map.md`.

**Goal:** Build the `src/TimesheetApp` + `src/TimesheetApp.Tests` solution skeleton with the connection seam, the OneDrive-safe connection policy, the idempotent schema/seed/migration initializer, the conflict-copy + journal-verify maintenance helpers, and the entity model records — and nothing above the data layer.

**Architecture:** A single SDK-style WPF app project (`net8.0-windows`) plus a headless xUnit test project (`net8.0`) that references it. The data layer is reached only through `IConnectionFactory.Create()`, which returns a short-lived `SqliteConnection` with `Foreign Keys=True`, `Pooling=False`, and `PRAGMA journal_mode=DELETE` applied on every open (the OneDrive-safety contract from XC-01). `DatabaseInitializer` is idempotent: `CREATE TABLE IF NOT EXISTS` for all 7 tables, `PRAGMA user_version` forward-only migrations, a `WHERE NOT EXISTS` DEFAULT-request seed, and a seed-DefaultTasks-only-when-empty step. Repository/initializer tests run against a real temp-file SQLite DB created via the same factory pointed at a throwaway path — never `:memory:` for journal/sidecar assertions (XC-01/XC-09 need a real file on disk).

**Tech Stack:** C# / .NET 8 · WPF (app TFM only, no UI built here) · Microsoft.Data.Sqlite 8.0.10 · Dapper 2.1.79 · CommunityToolkit.Mvvm 8.4.2 · Microsoft.Extensions.DependencyInjection 8.0.x · ClosedXML 0.105.0 · xUnit + xunit.runner.visualstudio + Microsoft.NET.Test.Sdk + coverlet.collector.

**Model profile note (config.json):** `model_profile: "quality"` is active. Each task's `<model>` below is the **base** assignment from `model_defaults` (mechanical=haiku, standard=sonnet, complex=opus). The controller applies the quality shift at dispatch time (haiku→sonnet, sonnet→opus, opus clamped). Do NOT re-shift inside the plan.

**Scope guard:** P1 builds the solution, NuGet refs, Models (records only), `IAppConfig`/`JsonAppConfig`, `IConnectionFactory`/`SqliteConnectionFactory`, `SqliteMaintenance` (conflict-copy + journal-verify), and `IDatabaseInitializer`/`DatabaseInitializer`. NO repositories, NO services, NO ViewModels, NO Views, NO `App.xaml` DI composition. Those belong to P2+. Every changed line traces to a P1 REQ-ID cited in its task.

---

## Wave / File-overlap map

| Wave | Task | Owner stack | `<model>` (base) | Files (no intra-wave overlap) |
|---|---|---|---|---|
| **Wave 1** | Task 1 — Solution + projects + NuGet | dotnet | haiku | `src/TimesheetApp.sln`, `src/TimesheetApp/TimesheetApp.csproj`, `src/TimesheetApp.Tests/TimesheetApp.Tests.csproj`, `src/TimesheetApp/Class1placeholder` removal |
| **Wave 2** | Task 2 — Model records | dotnet | haiku | `src/TimesheetApp/Models/Entities.cs`, `src/TimesheetApp.Tests/Models/EntitiesTests.cs` |
| **Wave 2** | Task 3 — IAppConfig + JsonAppConfig | dotnet | sonnet | `src/TimesheetApp/Config/IAppConfig.cs`, `src/TimesheetApp/Config/JsonAppConfig.cs`, `src/TimesheetApp.Tests/Config/JsonAppConfigTests.cs` |
| **Wave 3** | Task 4 — IConnectionFactory + SqliteConnectionFactory | dotnet | opus | `src/TimesheetApp/Data/IConnectionFactory.cs`, `src/TimesheetApp/Data/SqliteConnectionFactory.cs`, `src/TimesheetApp.Tests/Data/SqliteConnectionFactoryTests.cs` |
| **Wave 4** | Task 5 — SqliteMaintenance (conflict-copy + journal-verify) | dotnet | sonnet | `src/TimesheetApp/Data/SqliteMaintenance.cs`, `src/TimesheetApp.Tests/Data/SqliteMaintenanceTests.cs` |
| **Wave 4** | Task 6 — IDatabaseInitializer + DatabaseInitializer | dotnet | opus | `src/TimesheetApp/Data/IDatabaseInitializer.cs`, `src/TimesheetApp/Data/DatabaseInitializer.cs`, `src/TimesheetApp.Tests/Data/DatabaseInitializerTests.cs` |

Wave 3 depends on Wave 1 (projects) + Wave 2 (no hard dep but ordered after for context budget). Wave 4 depends on Wave 3 (`IConnectionFactory`). Task 5 and Task 6 are in the same wave and touch disjoint files (`SqliteMaintenance.cs` vs `DatabaseInitializer.cs`) — zero overlap, may run in parallel.

---

## Wave 1

### Task 1: Solution skeleton + projects + NuGet references

**REQ trace:** scaffolding for DATA-01..07, XC-01, XC-08, XC-09 (no project = no phase).

**Files:**
- Create: `src/TimesheetApp.sln`
- Create: `src/TimesheetApp/TimesheetApp.csproj`
- Create: `src/TimesheetApp/App.xaml`
- Create: `src/TimesheetApp/App.xaml.cs`
- Create: `src/TimesheetApp.Tests/TimesheetApp.Tests.csproj`
- Create: `src/TimesheetApp.Tests/SmokeTests.cs`

`<model>`: haiku

- [ ] **Step 1: Create the app project file**

`src/TimesheetApp/TimesheetApp.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>TimesheetApp</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageReference Include="Dapper" Version="2.1.79" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.10" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1" />
    <PackageReference Include="ClosedXML" Version="0.105.0" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create a minimal App.xaml shell so the WPF project compiles**

`src/TimesheetApp/App.xaml`:
```xml
<Application x:Class="TimesheetApp.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
```

`src/TimesheetApp/App.xaml.cs`:
```csharp
using System.Windows;

namespace TimesheetApp;

public partial class App : Application
{
}
```

> Note: full DI composition root (spec §6) is deferred to P2 — this is the minimal shell that lets the WPF project build. Do not add `ServiceCollection` wiring here.

- [ ] **Step 3: Create the test project file**

`src/TimesheetApp.Tests/TimesheetApp.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.10" />
    <PackageReference Include="coverlet.collector" Version="6.0.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\TimesheetApp\TimesheetApp.csproj" />
  </ItemGroup>

</Project>
```

> The test project targets bare `net8.0` and references the `net8.0-windows` app. This cross-TFM reference compiles because the test code touches only the non-WPF data/model types. xUnit version pins are the current 2.9.x line; if `dotnet restore` reports a newer servicing patch, accept it — do not pull 3.x.

- [ ] **Step 4: Write the smoke test**

`src/TimesheetApp.Tests/SmokeTests.cs`:
```csharp
namespace TimesheetApp.Tests;

public class SmokeTests
{
    [Fact]
    public void Solution_Builds_And_Test_Runner_Works()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 5: Create the solution and add both projects**

Run (PowerShell, from repo root):
```powershell
dotnet new sln -n TimesheetApp -o src
dotnet sln src/TimesheetApp.sln add src/TimesheetApp/TimesheetApp.csproj
dotnet sln src/TimesheetApp.sln add src/TimesheetApp.Tests/TimesheetApp.Tests.csproj
```
> If `dotnet new sln` complains the .sln already exists from a prior step, skip the create and run only the two `add` commands.

- [ ] **Step 6: Restore + build + run the smoke test (verify)**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo
```
Expected: build succeeds for both projects; `1 Passed`. Must complete well under 60s.

- [ ] **Step 7: Commit**

```bash
git add src/TimesheetApp.sln src/TimesheetApp/ src/TimesheetApp.Tests/
git commit -m "chore(p1): scaffold TimesheetApp solution + test project + NuGet refs (DATA/XC P1)"
```

**`<done>` (grep-verifiable):**
- `grep -q "net8.0-windows" src/TimesheetApp/TimesheetApp.csproj`
- `grep -q "Microsoft.Data.Sqlite" src/TimesheetApp/TimesheetApp.csproj`
- `grep -q "TimesheetApp.csproj" src/TimesheetApp.Tests/TimesheetApp.Tests.csproj`
- `dotnet test src/TimesheetApp.sln --nologo` exits 0.

---

## Wave 2

### Task 2: Model records (entities)

**REQ trace:** DATA-02 (schema shape mirrored in types). Names taken VERBATIM from spec §2.

**Files:**
- Create: `src/TimesheetApp/Models/Entities.cs`
- Test: `src/TimesheetApp.Tests/Models/EntitiesTests.cs`

`<model>`: haiku

- [ ] **Step 1: Write the failing test**

`src/TimesheetApp.Tests/Models/EntitiesTests.cs`:
```csharp
using TimesheetApp.Models;

namespace TimesheetApp.Tests.Models;

public class EntitiesTests
{
    [Fact]
    public void TimeLog_Carries_DateOnly_WorkDate_And_Decimal_Hours()
    {
        var log = new TimeLog(
            Id: 1, UserId: 2, TaskId: 3,
            WorkDate: new DateOnly(2026, 6, 21),
            Hours: 7.5m,
            CreatedAt: new DateTimeOffset(2026, 6, 21, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 6, 21), log.WorkDate);
        Assert.Equal(7.5m, log.Hours);
    }

    [Fact]
    public void User_WindowsUsername_Is_Nullable_And_IsActive_Tracked()
    {
        var user = new User(Id: 1, Name: "Anh", WindowsUsername: null, IsActive: true);
        Assert.Null(user.WindowsUsername);
        Assert.True(user.IsActive);
    }

    [Fact]
    public void TaskItem_Has_RequestId_OrderIndex_IsActive()
    {
        var task = new TaskItem(Id: 1, RequestId: 9, TaskName: "Annual Leave", OrderIndex: 0, IsActive: true);
        Assert.Equal(9, task.RequestId);
        Assert.Equal("Annual Leave", task.TaskName);
        Assert.Equal(0, task.OrderIndex);
    }

    [Fact]
    public void Request_Has_Code_Project_CreatedAt_No_IsActive_Member()
    {
        var request = new Request(Id: 1, RequestCode: "DEFAULT", Project: "DEFAULT",
            CreatedAt: DateTimeOffset.UnixEpoch);
        Assert.Equal("DEFAULT", request.RequestCode);
        // Requests are NOT soft-deletable in v1 (DATA-02 decision 4) -> no IsActive property.
        Assert.Null(typeof(Request).GetProperty("IsActive"));
    }

    [Fact]
    public void DefaultTask_And_TaskTemplate_Shapes()
    {
        var dt = new DefaultTask(Id: 1, TaskName: "Meeting", OrderIndex: 1, IsActive: true);
        var tpl = new TaskTemplate(Id: 1, TemplateName: "Std", TaskName: "Dev", OrderIndex: 0);
        Assert.Equal("Meeting", dt.TaskName);
        Assert.Equal("Std", tpl.TemplateName);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo
```
Expected: COMPILE FAIL — `TimesheetApp.Models` namespace / types `TimeLog`, `User`, etc. do not exist.

- [ ] **Step 3: Write minimal implementation**

`src/TimesheetApp/Models/Entities.cs`:
```csharp
namespace TimesheetApp.Models;

// --- Entities (1:1 with tables). Names are VERBATIM from architecture spec §2. ---
// 'Task' collides with System.Threading.Tasks.Task -> the entity is named TaskItem.

public sealed record User(int Id, string Name, string? WindowsUsername, bool IsActive);

public sealed record Request(int Id, string RequestCode, string Project, DateTimeOffset CreatedAt);

public sealed record TaskItem(int Id, int RequestId, string TaskName, int OrderIndex, bool IsActive);

public sealed record TimeLog(
    int Id, int UserId, int TaskId, DateOnly WorkDate, decimal Hours, DateTimeOffset CreatedAt);

public sealed record TaskTemplate(int Id, string TemplateName, string TaskName, int OrderIndex);

public sealed record DefaultTask(int Id, string TaskName, int OrderIndex, bool IsActive);
```

> Read-models (`TimeLogReportRow`, `WeekGrid`, `CellAssignment`, result structs) from spec §2 are deferred to the phase that owns their producing service/VM (P2/P3/P5/P6). P1 ships only the 6 table-backed entities. Do not add speculative read-models here (YAGNI).

- [ ] **Step 4: Run test to verify it passes**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo
```
Expected: PASS (all `EntitiesTests` green).

- [ ] **Step 5: Commit**

```bash
git add src/TimesheetApp/Models/Entities.cs src/TimesheetApp.Tests/Models/EntitiesTests.cs
git commit -m "feat(p1): add entity model records (DATA-02)"
```

**`<done>` (grep-verifiable):**
- `grep -q "public sealed record TaskItem" src/TimesheetApp/Models/Entities.cs`
- `grep -q "DateOnly WorkDate" src/TimesheetApp/Models/Entities.cs`
- `grep -q "string? WindowsUsername" src/TimesheetApp/Models/Entities.cs`
- `! grep -q "IsActive" <(grep "record Request(" src/TimesheetApp/Models/Entities.cs)` (Request has no IsActive)

---

### Task 3: IAppConfig + JsonAppConfig (DB-path locality split)

**REQ trace:** DATA-07 (DB path stored app-locally in `%APPDATA%\TimesheetApp\appsettings.json`, not in the shared DB).

**Files:**
- Create: `src/TimesheetApp/Config/IAppConfig.cs`
- Create: `src/TimesheetApp/Config/JsonAppConfig.cs`
- Test: `src/TimesheetApp.Tests/Config/JsonAppConfigTests.cs`

`<model>`: sonnet

- [ ] **Step 1: Write the failing test**

`src/TimesheetApp.Tests/Config/JsonAppConfigTests.cs`:
```csharp
using TimesheetApp.Config;

namespace TimesheetApp.Tests.Config;

public class JsonAppConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public JsonAppConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "appsettings.json");
    }

    [Fact]
    public void First_Run_Returns_Default_Path_When_No_File_Exists()
    {
        var cfg = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"C:\shared\timesheet.db", cfg.DbPath);
    }

    [Fact]
    public void SetDbPath_Persists_To_Json_And_Survives_Reload()
    {
        var cfg = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        cfg.SetDbPath(@"D:\OneDrive\team\timesheet.db");

        Assert.True(File.Exists(_configPath));

        var reloaded = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"D:\OneDrive\team\timesheet.db", reloaded.DbPath);
    }

    [Fact]
    public void SetDbPath_Creates_Parent_Directory_If_Missing()
    {
        var nested = Path.Combine(_dir, "sub", "deep", "appsettings.json");
        var cfg = new JsonAppConfig(nested, defaultDbPath: @"C:\shared\timesheet.db");
        cfg.SetDbPath(@"E:\x\timesheet.db");
        Assert.True(File.Exists(nested));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo --filter FullyQualifiedName~JsonAppConfigTests
```
Expected: COMPILE FAIL — `TimesheetApp.Config.JsonAppConfig` does not exist.

- [ ] **Step 3: Write minimal implementation**

`src/TimesheetApp/Config/IAppConfig.cs`:
```csharp
namespace TimesheetApp.Config;

// DATA-07 / SET-01: the .db PATH is stored app-locally (%APPDATA%), never inside the
// shared DB (avoids the chicken-and-egg of reading the path from the DB it points to).
public interface IAppConfig
{
    string DbPath { get; }
    void SetDbPath(string dbPath);
}
```

`src/TimesheetApp/Config/JsonAppConfig.cs`:
```csharp
using System.IO;
using System.Text.Json;

namespace TimesheetApp.Config;

// Reads/writes %APPDATA%\TimesheetApp\appsettings.json. The default ctor resolves the
// canonical app-local path; the (path, default) ctor is the test/DI seam.
public sealed class JsonAppConfig : IAppConfig
{
    private sealed record Model(string DbPath);

    private readonly string _configPath;
    private string _dbPath;

    public JsonAppConfig()
        : this(DefaultConfigPath(), DefaultDbPath())
    {
    }

    public JsonAppConfig(string configPath, string defaultDbPath)
    {
        _configPath = configPath;
        _dbPath = Load(configPath) ?? defaultDbPath;
    }

    public string DbPath => _dbPath;

    public void SetDbPath(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new Model(dbPath),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }

    private static string? Load(string configPath)
    {
        if (!File.Exists(configPath)) return null;
        try
        {
            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(configPath));
            return string.IsNullOrWhiteSpace(model?.DbPath) ? null : model!.DbPath;
        }
        catch (JsonException)
        {
            return null; // corrupt config -> fall back to default
        }
    }

    private static string DefaultConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "TimesheetApp", "appsettings.json");
    }

    private static string DefaultDbPath()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "TimesheetApp", "timesheet.db");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo --filter FullyQualifiedName~JsonAppConfigTests
```
Expected: PASS (3 tests green).

- [ ] **Step 5: Commit**

```bash
git add src/TimesheetApp/Config/ src/TimesheetApp.Tests/Config/
git commit -m "feat(p1): app-local DB path config via JsonAppConfig (DATA-07)"
```

**`<done>` (grep-verifiable):**
- `grep -q "interface IAppConfig" src/TimesheetApp/Config/IAppConfig.cs`
- `grep -q "appsettings.json" src/TimesheetApp/Config/JsonAppConfig.cs`
- `grep -q "SpecialFolder.ApplicationData" src/TimesheetApp/Config/JsonAppConfig.cs`

---

## Wave 3

### Task 4: IConnectionFactory + SqliteConnectionFactory (OneDrive-safe connection policy)

**REQ trace:** XC-01 (journal_mode=DELETE + Pooling=False + short connections, no -wal/-shm), DATA-06 (FK enforced every connection).

**Files:**
- Create: `src/TimesheetApp/Data/IConnectionFactory.cs`
- Create: `src/TimesheetApp/Data/SqliteConnectionFactory.cs`
- Test: `src/TimesheetApp.Tests/Data/SqliteConnectionFactoryTests.cs`

`<model>`: opus

- [ ] **Step 1: Write the failing test**

`src/TimesheetApp.Tests/Data/SqliteConnectionFactoryTests.cs`:
```csharp
using System.Data;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;
using TimesheetApp.Data;

namespace TimesheetApp.Tests.Data;

public class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IConnectionFactory _factory;

    public SqliteConnectionFactoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-cf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        var cfg = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), _dbPath);
        _factory = new SqliteConnectionFactory(cfg);
    }

    [Fact]
    public void Create_Returns_Open_Connection()
    {
        using var conn = _factory.Create();
        Assert.Equal(ConnectionState.Open, conn.State);
    }

    [Fact]
    public void Foreign_Keys_Are_On_For_Every_Connection()
    {
        using var conn = _factory.Create();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys;";
        var result = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(1L, result); // DATA-06
    }

    [Fact]
    public void Journal_Mode_Is_Delete_Not_Wal()
    {
        using var conn = _factory.Create();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string)cmd.ExecuteScalar()!;
        Assert.Equal("delete", mode.ToLowerInvariant()); // XC-01
    }

    [Fact]
    public void No_Wal_Or_Shm_Sidecar_Is_Created_By_A_Write()
    {
        // Do real work through a short connection, then dispose it.
        using (var conn = _factory.Create())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS Probe(id INTEGER PRIMARY KEY); " +
                              "INSERT INTO Probe DEFAULT VALUES;";
            cmd.ExecuteNonQuery();
        }

        Assert.False(File.Exists(_dbPath + "-wal"), "-wal sidecar must never exist (XC-01)");
        Assert.False(File.Exists(_dbPath + "-shm"), "-shm sidecar must never exist (XC-01)");
        Assert.True(File.Exists(_dbPath), "the single .db file is the only at-rest artifact");
    }

    [Fact]
    public void Pooling_Is_Off_So_Dispose_Releases_The_File_Handle()
    {
        using (var conn = _factory.Create())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS Probe(id INTEGER PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }
        // With Pooling=False the handle is released on Dispose; the file is movable/deletable.
        File.Move(_dbPath, _dbPath + ".moved");
        Assert.True(File.Exists(_dbPath + ".moved"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo --filter FullyQualifiedName~SqliteConnectionFactoryTests
```
Expected: COMPILE FAIL — `TimesheetApp.Data.IConnectionFactory` / `SqliteConnectionFactory` do not exist.

- [ ] **Step 3: Write minimal implementation**

`src/TimesheetApp/Data/IConnectionFactory.cs`:
```csharp
using System.Data;

namespace TimesheetApp.Data;

public interface IConnectionFactory
{
    // Returns an OPEN connection with FK on, journal_mode=DELETE, pooling off.
    IDbConnection Create();
}
```

`src/TimesheetApp/Data/SqliteConnectionFactory.cs`:
```csharp
using System.Data;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;

namespace TimesheetApp.Data;

// XC-01: short open->work->close connections, journal_mode=DELETE (NOT WAL -> no -wal/-shm
// sidecars to sync out of band over OneDrive), Pooling=False (Dispose truly releases the
// file handle so OneDrive can upload). DATA-06: PRAGMA foreign_keys=ON every connection.
public sealed class SqliteConnectionFactory : IConnectionFactory
{
    private readonly IAppConfig _config;

    public SqliteConnectionFactory(IAppConfig config)
    {
        _config = config;
    }

    public IDbConnection Create()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _config.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();

        var conn = new SqliteConnection(connectionString);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            // DELETE journal (rollback) + belt-and-suspenders FK pragma on the live handle.
            cmd.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }

        return conn;
    }
}
```

> `ForeignKeys = true` in the connection string *and* `PRAGMA foreign_keys=ON` are both applied: the connection-string key is the durable contract, the pragma is the explicit on-handle guarantee the test asserts. `journal_mode=DELETE` must run per-connection because it is a connection-scoped pragma in non-WAL mode.

- [ ] **Step 4: Run test to verify it passes**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo --filter FullyQualifiedName~SqliteConnectionFactoryTests
```
Expected: PASS (5 tests green), under 60s.

- [ ] **Step 5: Commit**

```bash
git add src/TimesheetApp/Data/IConnectionFactory.cs src/TimesheetApp/Data/SqliteConnectionFactory.cs src/TimesheetApp.Tests/Data/SqliteConnectionFactoryTests.cs
git commit -m "feat(p1): OneDrive-safe SqliteConnectionFactory (journal=DELETE, pooling off, FK on) (XC-01, DATA-06)"
```

**`<done>` (grep-verifiable):**
- `grep -q "Pooling = false" src/TimesheetApp/Data/SqliteConnectionFactory.cs`
- `grep -q "journal_mode=DELETE" src/TimesheetApp/Data/SqliteConnectionFactory.cs`
- `grep -q "ForeignKeys = true" src/TimesheetApp/Data/SqliteConnectionFactory.cs`
- the `No_Wal_Or_Shm_Sidecar` test passes.

---

## Wave 4

### Task 5: SqliteMaintenance — conflict-copy detection + verify-journal-gone

**REQ trace:** XC-08 (scan DB folder for `*-<MACHINE>.db` conflict-copy siblings), XC-09 (verify `<db>-journal` gone after write; warn if present).

**Files:**
- Create: `src/TimesheetApp/Data/SqliteMaintenance.cs`
- Test: `src/TimesheetApp.Tests/Data/SqliteMaintenanceTests.cs`

`<model>`: sonnet

- [ ] **Step 1: Write the failing test**

`src/TimesheetApp.Tests/Data/SqliteMaintenanceTests.cs`:
```csharp
using TimesheetApp.Data;

namespace TimesheetApp.Tests.Data;

public class SqliteMaintenanceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SqliteMaintenanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-maint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        File.WriteAllText(_dbPath, "");
    }

    [Fact]
    public void FindConflictCopies_Returns_Empty_When_None_Exist()
    {
        var hits = SqliteMaintenance.FindConflictCopies(_dbPath);
        Assert.Empty(hits);
    }

    [Fact]
    public void FindConflictCopies_Detects_Machine_Suffixed_Sibling()
    {
        // OneDrive conflict-copy pattern: <name>-<MACHINE>.db next to the canonical file.
        var conflict = Path.Combine(_dir, "timesheet-DESKTOP-AB12.db");
        File.WriteAllText(conflict, "");

        var hits = SqliteMaintenance.FindConflictCopies(_dbPath);

        Assert.Contains(hits, p => Path.GetFileName(p) == "timesheet-DESKTOP-AB12.db");
    }

    [Fact]
    public void FindConflictCopies_Ignores_The_Canonical_File_Itself()
    {
        var hits = SqliteMaintenance.FindConflictCopies(_dbPath);
        Assert.DoesNotContain(hits, p => Path.GetFullPath(p) == Path.GetFullPath(_dbPath));
    }

    [Fact]
    public void IsJournalGone_True_When_No_Journal_Sidecar()
    {
        Assert.True(SqliteMaintenance.IsJournalGone(_dbPath));
    }

    [Fact]
    public void IsJournalGone_False_When_Journal_Sidecar_Persists()
    {
        File.WriteAllText(_dbPath + "-journal", "leftover");
        Assert.False(SqliteMaintenance.IsJournalGone(_dbPath)); // XC-09 warn condition
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo --filter FullyQualifiedName~SqliteMaintenanceTests
```
Expected: COMPILE FAIL — `TimesheetApp.Data.SqliteMaintenance` does not exist.

- [ ] **Step 3: Write minimal implementation**

`src/TimesheetApp/Data/SqliteMaintenance.cs`:
```csharp
using System.IO;

namespace TimesheetApp.Data;

// XC-08: scan the DB folder for OneDrive conflict-copy siblings ("<name>-<MACHINE>.db")
// so silent data divergence becomes a visible startup event.
// XC-09: after a committed write the rollback journal must be gone; a lingering
// "<db>-journal" means a transaction was interrupted -> caller surfaces a warning.
public static class SqliteMaintenance
{
    public static IReadOnlyList<string> FindConflictCopies(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return Array.Empty<string>();

        var stem = Path.GetFileNameWithoutExtension(dbPath); // "timesheet"
        var ext = Path.GetExtension(dbPath);                  // ".db"
        var canonicalFull = Path.GetFullPath(dbPath);

        var results = new List<string>();
        // Pattern "<stem>-*<ext>" catches "timesheet-DESKTOP-AB12.db" but not "timesheet.db".
        foreach (var file in Directory.EnumerateFiles(dir, $"{stem}-*{ext}"))
        {
            if (Path.GetFullPath(file) == canonicalFull) continue;
            results.Add(file);
        }
        return results;
    }

    public static bool IsJournalGone(string dbPath)
        => !File.Exists(dbPath + "-journal");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo --filter FullyQualifiedName~SqliteMaintenanceTests
```
Expected: PASS (5 tests green).

- [ ] **Step 5: Commit**

```bash
git add src/TimesheetApp/Data/SqliteMaintenance.cs src/TimesheetApp.Tests/Data/SqliteMaintenanceTests.cs
git commit -m "feat(p1): conflict-copy detection + verify-journal-gone helpers (XC-08, XC-09)"
```

**`<done>` (grep-verifiable):**
- `grep -q "FindConflictCopies" src/TimesheetApp/Data/SqliteMaintenance.cs`
- `grep -q "IsJournalGone" src/TimesheetApp/Data/SqliteMaintenance.cs`
- `grep -q -- "-journal" src/TimesheetApp/Data/SqliteMaintenance.cs`

---

### Task 6: IDatabaseInitializer + DatabaseInitializer — schema + seed + migrations

**REQ trace:** DATA-01 (auto-create + idempotent bootstrap, one transaction), DATA-02 (exact schema), DATA-03 (hidden DEFAULT request, idempotent), DATA-04 (DefaultTasks seeded only when empty), DATA-05 (`PRAGMA user_version` forward-only migrations). Verifies DATA-06 FK rejection via the temp DB.

**Files:**
- Create: `src/TimesheetApp/Data/IDatabaseInitializer.cs`
- Create: `src/TimesheetApp/Data/DatabaseInitializer.cs`
- Test: `src/TimesheetApp.Tests/Data/DatabaseInitializerTests.cs`

`<model>`: opus

- [ ] **Step 1: Write the failing test**

`src/TimesheetApp.Tests/Data/DatabaseInitializerTests.cs`:
```csharp
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;
using TimesheetApp.Data;

namespace TimesheetApp.Tests.Data;

public class DatabaseInitializerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IConnectionFactory _factory;
    private readonly DatabaseInitializer _sut;

    private static readonly string[] ExpectedTables =
    {
        "Users", "Requests", "Tasks", "TaskTemplates", "TimeLogs", "DefaultTasks", "Settings"
    };

    public DatabaseInitializerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        var cfg = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), _dbPath);
        _factory = new SqliteConnectionFactory(cfg);
        _sut = new DatabaseInitializer(_factory);
    }

    private long Count(IDbConnection c, string sql) => c.ExecuteScalar<long>(sql);

    [Fact]
    public async Task InitializeAsync_Creates_All_Seven_Tables()
    {
        await _sut.InitializeAsync();
        using var c = _factory.Create();
        foreach (var table in ExpectedTables)
        {
            var found = c.ExecuteScalar<string?>(
                "SELECT name FROM sqlite_master WHERE type='table' AND name=@t;", new { t = table });
            Assert.Equal(table, found);
        }
    }

    [Fact]
    public async Task InitializeAsync_Is_Idempotent_No_Duplicate_Default_Request()
    {
        await _sut.InitializeAsync();
        await _sut.InitializeAsync();
        await _sut.InitializeAsync();

        using var c = _factory.Create();
        var defaults = Count(c, "SELECT COUNT(*) FROM Requests WHERE request_code='DEFAULT';");
        Assert.Equal(1, defaults); // DATA-03 idempotent
    }

    [Fact]
    public async Task InitializeAsync_Seeds_DefaultTasks_Only_When_Empty()
    {
        await _sut.InitializeAsync();
        using (var c = _factory.Create())
        {
            // user renames a default -> simulate a curated table
            c.Execute("DELETE FROM DefaultTasks;");
            c.Execute("INSERT INTO DefaultTasks(task_name, order_index, is_active) VALUES('Custom Only', 0, 1);");
        }

        await _sut.InitializeAsync(); // relaunch must NOT re-seed over the curated row

        using (var c = _factory.Create())
        {
            var count = Count(c, "SELECT COUNT(*) FROM DefaultTasks;");
            Assert.Equal(1, count); // DATA-04: not re-seeded
            var name = c.ExecuteScalar<string>("SELECT task_name FROM DefaultTasks;");
            Assert.Equal("Custom Only", name);
        }
    }

    [Fact]
    public async Task InitializeAsync_Sets_User_Version_To_Target()
    {
        await _sut.InitializeAsync();
        using var c = _factory.Create();
        var version = Count(c, "PRAGMA user_version;");
        Assert.True(version >= 1, "user_version must advance to the schema target (DATA-05)");
    }

    [Fact]
    public async Task Schema_Has_Unique_Natural_Key_On_TimeLogs()
    {
        await _sut.InitializeAsync();
        using var c = _factory.Create();
        // seed FK targets
        c.Execute("INSERT INTO Users(name, is_active) VALUES('U', 1);");
        c.Execute("INSERT INTO Requests(request_code, project, created_at) VALUES('R1','P','2026-06-21T00:00:00Z');");
        c.Execute("INSERT INTO Tasks(request_id, task_name, order_index, is_active) VALUES(2,'T',0,1);");
        c.Execute("INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at) " +
                  "VALUES(1,1,'2026-06-22',8.0,'2026-06-21T00:00:00Z');");

        var ex = Assert.ThrowsAny<SqliteException>(() =>
            c.Execute("INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at) " +
                      "VALUES(1,1,'2026-06-22',4.0,'2026-06-21T00:00:00Z');"));
        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase); // DATA-02
    }

    [Fact]
    public async Task Foreign_Keys_Reject_TimeLog_With_Missing_Task()
    {
        await _sut.InitializeAsync();
        using var c = _factory.Create();
        c.Execute("INSERT INTO Users(name, is_active) VALUES('U', 1);");

        var ex = Assert.ThrowsAny<SqliteException>(() =>
            c.Execute("INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at) " +
                      "VALUES(1, 9999, '2026-06-22', 8.0, '2026-06-21T00:00:00Z');"));
        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase); // DATA-06
    }

    [Fact]
    public async Task Requests_Table_Has_No_IsActive_Column()
    {
        await _sut.InitializeAsync();
        using var c = _factory.Create();
        var cols = c.Query<string>("SELECT name FROM pragma_table_info('Requests');").ToList();
        Assert.DoesNotContain("is_active", cols); // DATA-02 decision 4
        Assert.Contains("windows_username",
            c.Query<string>("SELECT name FROM pragma_table_info('Users');").ToList()); // DATA-02
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo --filter FullyQualifiedName~DatabaseInitializerTests
```
Expected: COMPILE FAIL — `TimesheetApp.Data.DatabaseInitializer` / `IDatabaseInitializer` do not exist.

- [ ] **Step 3: Write minimal implementation**

`src/TimesheetApp/Data/IDatabaseInitializer.cs`:
```csharp
namespace TimesheetApp.Data;

public interface IDatabaseInitializer
{
    // Idempotent: CREATE TABLE IF NOT EXISTS (all 7); PRAGMA user_version migrations;
    // ensure hidden DEFAULT request; seed DefaultTasks only if the table is empty.
    Task InitializeAsync();   // DATA-01/02/03/04/05
}
```

`src/TimesheetApp/Data/DatabaseInitializer.cs`:
```csharp
using System.Data;
using Dapper;

namespace TimesheetApp.Data;

// DATA-01..05. Schema mirrors architecture spec §2/§3:
//  - Users.windows_username nullable; is_active on Users/Tasks/DefaultTasks.
//  - Requests has NO is_active (decision 4).
//  - TimeLogs: single FK task_id, hours REAL, UNIQUE(user_id,task_id,work_date).
//  - work_date / created_at stored as TEXT (ISO-8601), culture-neutral.
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    // Bump SchemaVersion and append a step to Migrations[] for any future additive change.
    private const long SchemaVersion = 1;

    private readonly IConnectionFactory _factory;

    public DatabaseInitializer(IConnectionFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync()
    {
        using var conn = _factory.Create();
        using var tx = conn.BeginTransaction();

        CreateTables(conn, tx);
        RunMigrations(conn, tx);
        EnsureDefaultRequest(conn, tx);
        SeedDefaultTasksIfEmpty(conn, tx);

        tx.Commit();
        return Task.CompletedTask;
    }

    private static void CreateTables(IDbConnection conn, IDbTransaction tx)
    {
        const string ddl = @"
CREATE TABLE IF NOT EXISTS Users (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT    NOT NULL,
    windows_username TEXT,
    is_active       INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS Requests (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    request_code TEXT    NOT NULL,
    project      TEXT    NOT NULL,
    created_at   TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS Tasks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id  INTEGER NOT NULL,
    task_name   TEXT    NOT NULL,
    order_index INTEGER NOT NULL DEFAULT 0,
    is_active   INTEGER NOT NULL DEFAULT 1,
    FOREIGN KEY (request_id) REFERENCES Requests(id)
);

CREATE TABLE IF NOT EXISTS TaskTemplates (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    template_name TEXT    NOT NULL,
    task_name     TEXT    NOT NULL,
    order_index   INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS TimeLogs (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    INTEGER NOT NULL,
    task_id    INTEGER NOT NULL,
    work_date  TEXT    NOT NULL,
    hours      REAL    NOT NULL,
    created_at TEXT    NOT NULL,
    FOREIGN KEY (user_id) REFERENCES Users(id),
    FOREIGN KEY (task_id) REFERENCES Tasks(id),
    UNIQUE (user_id, task_id, work_date)
);

CREATE TABLE IF NOT EXISTS DefaultTasks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    task_name   TEXT    NOT NULL,
    order_index INTEGER NOT NULL DEFAULT 0,
    is_active   INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS Settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);";
        conn.Execute(ddl, transaction: tx);
    }

    private static void RunMigrations(IDbConnection conn, IDbTransaction tx)
    {
        // DATA-05: forward-only additive migrations gated on PRAGMA user_version.
        // Step index N runs when current user_version < N+1. All steps additive (ADD COLUMN /
        // CREATE TABLE) so an old client opening a newer DB still works.
        var migrations = new Action<IDbConnection, IDbTransaction>[]
        {
            // v1 -> baseline schema already created above; nothing extra to alter.
            static (_, _) => { },
        };

        var current = conn.ExecuteScalar<long>("PRAGMA user_version;", transaction: tx);
        for (var step = current; step < migrations.Length; step++)
        {
            migrations[step](conn, tx);
        }

        if (current < SchemaVersion)
        {
            // PRAGMA cannot be parameterized; SchemaVersion is a compile-time constant.
            conn.Execute($"PRAGMA user_version = {SchemaVersion};", transaction: tx);
        }
    }

    private static void EnsureDefaultRequest(IDbConnection conn, IDbTransaction tx)
    {
        // DATA-03: exactly one hidden DEFAULT request, idempotent.
        conn.Execute(
            @"INSERT INTO Requests(request_code, project, created_at)
              SELECT 'DEFAULT', 'DEFAULT', @now
              WHERE NOT EXISTS (SELECT 1 FROM Requests WHERE request_code = 'DEFAULT');",
            new { now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            transaction: tx);
    }

    private static void SeedDefaultTasksIfEmpty(IDbConnection conn, IDbTransaction tx)
    {
        // DATA-04: seed the default set ONLY when the table is empty, so a user who has
        // renamed/hidden defaults is never overwritten on relaunch.
        var any = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM DefaultTasks;", transaction: tx);
        if (any > 0) return;

        var seeds = new[] { "Annual Leave", "Meeting", "Other" };
        for (var i = 0; i < seeds.Length; i++)
        {
            conn.Execute(
                "INSERT INTO DefaultTasks(task_name, order_index, is_active) VALUES(@name, @order, 1);",
                new { name = seeds[i], order = i },
                transaction: tx);
        }
    }
}
```

> The initial `DefaultTasks → Tasks` materialization under the DEFAULT request (spec §7.5 step 3, DATA-03 "each active DefaultTask has a matching Task") is owned by `DefaultTaskSyncService.SyncAsync()` in **P2** and called from the App composition root at startup. P1 ships the seed + DEFAULT-request guarantee; it does not duplicate the sync logic here (DRY — one reconcile path). The DATA-03 test in P1 asserts the single DEFAULT request; the Task-materialization assertion lives with the sync service in P2.

- [ ] **Step 4: Run test to verify it passes**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo --filter FullyQualifiedName~DatabaseInitializerTests
```
Expected: PASS (all 7 tests green), under 60s.

- [ ] **Step 5: Full-suite regression run**

Run:
```powershell
dotnet test src/TimesheetApp.sln --nologo
```
Expected: every test from Tasks 1-6 green.

- [ ] **Step 6: Commit**

```bash
git add src/TimesheetApp/Data/IDatabaseInitializer.cs src/TimesheetApp/Data/DatabaseInitializer.cs src/TimesheetApp.Tests/Data/DatabaseInitializerTests.cs
git commit -m "feat(p1): idempotent schema+seed+migration DatabaseInitializer (DATA-01..05, DATA-06 verified)"
```

**`<done>` (grep-verifiable):**
- `grep -q "CREATE TABLE IF NOT EXISTS Users" src/TimesheetApp/Data/DatabaseInitializer.cs`
- `grep -q "UNIQUE (user_id, task_id, work_date)" src/TimesheetApp/Data/DatabaseInitializer.cs`
- `grep -q "request_code = 'DEFAULT'" src/TimesheetApp/Data/DatabaseInitializer.cs`
- `grep -q "PRAGMA user_version" src/TimesheetApp/Data/DatabaseInitializer.cs`
- `grep -q "SELECT COUNT(\*) FROM DefaultTasks" src/TimesheetApp/Data/DatabaseInitializer.cs`
- `! grep -q "is_active" <(sed -n '/CREATE TABLE IF NOT EXISTS Requests/,/);/p' src/TimesheetApp/Data/DatabaseInitializer.cs)`
- full `dotnet test` exits 0.

---

## Self-Review

**1. Spec coverage (P1 REQ scope):**

| REQ | Task | Covered? |
|---|---|---|
| DATA-01 (auto-create + idempotent, one tx) | Task 6 | yes — `InitializeAsync` in a transaction, idempotency test |
| DATA-02 (exact schema) | Task 6 (+ Task 2 types) | yes — table DDL, UNIQUE key test, Requests-no-is_active test, windows_username test |
| DATA-03 (hidden DEFAULT request idempotent) | Task 6 | yes — `EnsureDefaultRequest` + idempotency test |
| DATA-04 (DefaultTasks seeded only when empty) | Task 6 | yes — `SeedDefaultTasksIfEmpty` + curated-row test |
| DATA-05 (`PRAGMA user_version` migrations) | Task 6 | yes — `RunMigrations` + user_version test |
| DATA-06 (FK every connection) | Task 4 (+ Task 6 verify) | yes — pragma test + FK-rejection test |
| DATA-07 (Settings store + path locality) | Task 3 (path) + Task 6 (Settings table) | yes — JsonAppConfig + Settings DDL |
| XC-01 (journal=DELETE + short conns, no sidecars) | Task 4 | yes — journal_mode test + no -wal/-shm test + pooling-off test |
| XC-08 (conflict-copy detection) | Task 5 | yes — `FindConflictCopies` + suffix test |
| XC-09 (verify -journal gone) | Task 5 | yes — `IsJournalGone` + persists test |

All 10 P1 REQs have an owning task. No P1 REQ is unplanned.

**2. Placeholder scan:** No "TBD/TODO/add validation here". Every code step contains full C#. Deferred items (read-models, DI composition, DefaultTask→Task materialization sync) are explicitly assigned to named later phases with a DRY rationale, not left as in-task placeholders.

**3. Type consistency:** `IConnectionFactory.Create()` → `IDbConnection` used identically in Tasks 4/6. `JsonAppConfig(configPath, defaultDbPath)` ctor signature consistent across Tasks 3/4/6 tests. Entity record member names (`WorkDate`, `WindowsUsername`, `RequestCode`) match spec §2 verbatim and are not re-typed elsewhere in P1. SQL column names (`work_date`, `windows_username`, `request_code`, `is_active`) consistent between DDL (Task 6) and the tests that query them.

**Scope boundary respected:** no repositories, services, VMs, Views, or DI composition built in P1.
