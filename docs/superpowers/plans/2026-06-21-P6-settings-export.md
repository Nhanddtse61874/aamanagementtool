---
must_haves:
  observable_truths:
    - "Markdown export of a real request renders `### {request_code} — {project}` and a `| Date | Task | Hours |` table (EXP-02)."
    - "Markdown export of DEFAULT-request rows sub-groups by task name → header reads `### DEFAULT — Annual Leave`, never `### DEFAULT — DEFAULT` (EXP-03)."
    - "Markdown hours render integer-when-whole (`4` not `4.0`) else 1 decimal (`3.5`); a `|` in a task name is escaped to `\\|` (EXP-04)."
    - "Excel export produces a real `.xlsx` byte[] that ClosedXML can reopen, with header row + one data row per filtered TimeLog, filterable by user/month/project (EXP-01)."
    - "Settings N-days warning defaults to 3 and round-trips through the shared `Settings` table (SET-02)."
    - "Settings DB-path Browse writes the chosen path to app-local `IAppConfig`, not the shared DB (SET-01)."
    - "TaskTemplate CRUD persists and reloads (SET-03)."
    - "Editing DefaultTasks then saving calls `IDefaultTaskSyncService.SyncAsync()`; a rename is soft-delete-old + insert-new with TimeLogs preserved (SET-04)."
  required_artifacts:
    - "src/TimesheetApp/Services/ExportService.cs implements IExportService (Markdown via StringBuilder + Excel via ClosedXML); returns byte[]/string, never opens SaveFileDialog."
    - "src/TimesheetApp/ViewModels/SettingsViewModel.cs (CommunityToolkit.Mvvm) wiring IAppConfig + ISettingsRepository + ITaskTemplateRepository (template CRUD) + IDefaultTaskSyncService + IExportService."
    - "src/TimesheetApp/Views/Tabs/SettingsTab.xaml + .xaml.cs binding the SettingsViewModel."
    - "src/TimesheetApp.Tests/Services/ExportServiceTests.cs — markdown structure, DEFAULT-by-task-name, hours/escaping, Excel reopen assertions."
    - "src/TimesheetApp.Tests/ViewModels/SettingsViewModelTests.cs — N-days default/persist, DB-path browse, template CRUD, DefaultTask sync trigger."
  required_wiring:
    - "ExportService → ITimeLogRepository.GetExportRowsAsync(from,to,projectFilter) for the row set, IUserRepository.GetByIdAsync for the user filter, IRequestRepository.GetByCodeAsync('DEFAULT') to detect the DEFAULT request."
    - "SettingsViewModel → IDefaultTaskSyncService.SyncAsync() invoked after any DefaultTask add/edit/hide."
    - "SettingsViewModel.SaveDbPath → IAppConfig.DbPath setter (app-local appsettings.json), never ISettingsRepository."
    - "SettingsViewModel N-days → ISettingsRepository.GetAsync/SetAsync('warning_days') in the shared DB."
    - "App.xaml.cs DI already registers IExportService + SettingsViewModel (spec §6) — no DI change needed in P6 beyond confirming the registrations exist."
  key_links:
    - "EXP-01 → ExportService.ExportExcelAsync + ExportServiceTests Excel-reopen test"
    - "EXP-02 → ExportService.ExportMarkdownAsync + markdown-structure test"
    - "EXP-03 → ExportService DEFAULT-by-task-name grouping + DEFAULT-header test"
    - "EXP-04 → ExportService.FormatHours + EscapePipe + formatting test"
    - "SET-01 → SettingsViewModel.BrowseDbPathCommand → IAppConfig.DbPath"
    - "SET-02 → SettingsViewModel.WarningDays (default 3) → ISettingsRepository('warning_days')"
    - "SET-03 → SettingsViewModel template CRUD → ITaskTemplateRepository (canonical, from P4 Task 1)"
    - "SET-04 → SettingsViewModel DefaultTask edit → IDefaultTaskSyncService.SyncAsync()"
---

# P6 — Settings + Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Stack skill MANDATORY: load `skills/implementer-dotnet-csharp/SKILL.md` for every task (all tasks are .NET/C#/WPF).

**Goal:** Build the Settings tab (`SettingsViewModel` + `SettingsTab.xaml`) and the headless `ExportService` (Excel via ClosedXML + Markdown via StringBuilder), wiring DB-path app-local config, N-days shared setting, TaskTemplate CRUD, and DefaultTask sync — covering SET-01..04 and EXP-01..04.

**Architecture:** `ExportService` is WPF-free and pure-ish: it pulls flat rows from `ITimeLogRepository.GetExportRowsAsync` and emits `byte[]` (Excel) / `string` (Markdown), never touching `SaveFileDialog` (spec §4, line 268). The DEFAULT request (`request_code='DEFAULT'`) is sub-grouped by task name in Markdown so the header reads `### DEFAULT — Annual Leave` (decision 1, EXP-03). `SettingsViewModel` (CommunityToolkit.Mvvm) owns the DB-path Browse → `IAppConfig` (app-local), N-days → shared `Settings` table, TaskTemplate CRUD, and DefaultTask edits that trigger `IDefaultTaskSyncService.SyncAsync()` (spec §5 SettingsViewModel row). XAML is manual-verify only.

**Tech Stack:** .NET 8 (`net8.0-windows`, WPF) · CommunityToolkit.Mvvm 8.4.2 · ClosedXML 0.105.0 · custom Markdown `StringBuilder` · xUnit + Moq (mocked repos) · `Microsoft.Data.Sqlite` (already present from P1).

**Depends on (consume verbatim — DO NOT redefine):**
- P1: `IConnectionFactory`, `ISettingsRepository`, `ITaskRepository`, `IRequestRepository`, `ITimeLogRepository`, `IUserRepository`, `IAppConfig`, models (`TaskTemplate`, `DefaultTask`, `TimeLogReportRow`, `ExportFilter`, `User`, `Request`, `TaskItem`).
- P2: `IDefaultTaskSyncService` (`EnsureDefaultRequestIdAsync`, `SyncAsync`), `IClock`.

> Interface signatures are quoted from `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md` §3–§4 and used VERBATIM. If a P1/P2 signature differs at execution time, STOP and reconcile — do not silently re-shape.

---

## Wave Plan (zero file overlap within a wave)

| Wave | Task | Files (exclusive) | Model | REQs |
|---|---|---|---|---|
| **1** | Task 1 — ExportService (Markdown + Excel, TDD hard) | `Services/ExportService.cs`, `Tests/Services/ExportServiceTests.cs` | opus | EXP-01, EXP-02, EXP-03, EXP-04 |
| **1** | Task 2 — SettingsViewModel logic (TDD) | `ViewModels/SettingsViewModel.cs`, `Tests/ViewModels/SettingsViewModelTests.cs` | sonnet | SET-01, SET-02, SET-03, SET-04 |
| **2** | Task 3 — SettingsTab.xaml (manual-verify) | `Views/Tabs/SettingsTab.xaml`, `Views/Tabs/SettingsTab.xaml.cs` | haiku | SET-01, SET-02, SET-03, SET-04 |

Wave 1 tasks touch disjoint file sets (Export vs Settings VM) → run in parallel. Wave 2 (XAML) depends on Wave 1 (binds `SettingsViewModel` members) → later wave. No two tasks in the same wave modify the same file.

> **Model-profile note (config `model_profile: quality`):** at dispatch the quality profile bumps each `<model>` one tier (haiku→sonnet, sonnet→opus, opus clamped). So effective dispatch models are: Task 1 opus (clamped), Task 2 opus (sonnet→opus), Task 3 sonnet (haiku→sonnet). The `<model>` tags above are the plan-authored base tiers; pre-task confirmation may override.

---

## Task 1: ExportService (Markdown + Excel) — TDD hard

**Model:** opus (pure, high-value, exact-output assertions; per profile `quality` this is clamped at opus).

**Files:**
- Create: `src/TimesheetApp/Services/ExportService.cs`
- Test: `src/TimesheetApp.Tests/Services/ExportServiceTests.cs`

**Read first:**
- `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md` §4 (`IExportService` lines 263-268: returns `byte[]`/`string`, NEVER `SaveFileDialog`), §2 (`TimeLogReportRow` lines 121-125, `ExportFilter` line 138), §3 (`ITimeLogRepository.GetExportRowsAsync` line 204, `IRequestRepository.GetByCodeAsync` line 180, `IUserRepository.GetByIdAsync`).
- `.planning/research/FEATURE-RESEARCH.md` §5 (Markdown format lines 281-339: structure, `Fmt`, escape `|`, DEFAULT-by-task-name ambiguity resolved to task-name per EXP-03).
- `.planning/research/STACK-RESEARCH.md` §3 (ClosedXML API lines 81-96).
- `skills/implementer-dotnet-csharp/SKILL.md`.

**Contract (consume verbatim from spec §4):**
```csharp
public interface IExportService
{
    Task<byte[]> ExportExcelAsync(ExportFilter filter);      // ClosedXML → MemoryStream → ToArray (EXP-01)
    Task<string> ExportMarkdownAsync(ExportFilter filter);    // StringBuilder (EXP-02/03/04)
}
```
`ExportFilter` (spec §2 line 138): `public readonly record struct ExportFilter(int? UserId, int Year, int Month, string? Project);`
`TimeLogReportRow` (spec §2 lines 121-125):
```csharp
public sealed record TimeLogReportRow(
    int UserId, string UserName,
    string RequestCode, string Project,
    int TaskId, string TaskName,
    DateOnly WorkDate, decimal Hours);
```

**Row sourcing:** `ITimeLogRepository.GetExportRowsAsync(DateOnly from, DateOnly to, string? projectFilter)` (spec §3 line 204). `ExportService` converts `(Year, Month)` → `from = new DateOnly(Year, Month, 1)`, `to = from.AddMonths(1).AddDays(-1)`, passes `Project` as `projectFilter`, then filters in-memory by `UserId` when `filter.UserId is int uid` (the repo method has no user param, so user filtering happens in the service). DEFAULT detection: a row's `RequestCode == "DEFAULT"`.

---

- [ ] **Step 1.1: Write the failing tests (full file)**

`src/TimesheetApp.Tests/Services/ExportServiceTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Moq;
using TimesheetApp.Models;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

public class ExportServiceTests
{
    private static TimeLogReportRow Row(
        int userId, string userName, string code, string project,
        int taskId, string taskName, string date, decimal hours) =>
        new(userId, userName, code, project, taskId, taskName,
            DateOnly.Parse(date), hours);

    private static (ExportService svc, Mock<ITimeLogRepository> logs,
                    Mock<IUserRepository> users, Mock<IRequestRepository> reqs)
        Build(IReadOnlyList<TimeLogReportRow> rows)
    {
        var logs = new Mock<ITimeLogRepository>();
        logs.Setup(r => r.GetExportRowsAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<string?>()))
            .ReturnsAsync(rows);
        var users = new Mock<IUserRepository>();
        var reqs = new Mock<IRequestRepository>();
        var svc = new ExportService(logs.Object, users.Object, reqs.Object);
        return (svc, logs, users, reqs);
    }

    // ---------- EXP-02: Markdown structure ----------
    [Fact]
    public async Task Markdown_RealRequest_RendersHeaderAndTable()
    {
        var rows = new[]
        {
            Row(1, "Nguyen Van A", "REQ-001", "ProjectX", 10, "Implement", "2026-06-16", 4m),
            Row(1, "Nguyen Van A", "REQ-001", "ProjectX", 11, "Review",    "2026-06-16", 4m),
            Row(1, "Nguyen Van A", "REQ-001", "ProjectX", 12, "Testing",   "2026-06-17", 3m),
        };
        var (svc, _, _, _) = Build(rows);

        var md = await svc.ExportMarkdownAsync(new ExportFilter(null, 2026, 6, null));

        Assert.Contains("# Timesheet — 2026/06", md);
        Assert.Contains("## Nguyen Van A", md);
        Assert.Contains("### REQ-001 — ProjectX", md);
        Assert.Contains("| Date | Task | Hours |", md);
        Assert.Contains("| --- | --- | --- |", md);
        Assert.Contains("| 2026-06-16 | Implement | 4 |", md);
        Assert.Contains("| 2026-06-17 | Testing | 3 |", md);
    }

    // ---------- EXP-03: DEFAULT grouped by task name ----------
    [Fact]
    public async Task Markdown_DefaultRequest_HeaderUsesTaskNameNotProject()
    {
        var rows = new[]
        {
            Row(1, "Nguyen Van A", "DEFAULT", "DEFAULT", 90, "Annual Leave", "2026-06-20", 8m),
            Row(1, "Nguyen Van A", "DEFAULT", "DEFAULT", 91, "Meeting",      "2026-06-19", 2m),
        };
        var (svc, _, _, _) = Build(rows);

        var md = await svc.ExportMarkdownAsync(new ExportFilter(null, 2026, 6, null));

        Assert.Contains("### DEFAULT — Annual Leave", md);
        Assert.Contains("### DEFAULT — Meeting", md);
        Assert.DoesNotContain("### DEFAULT — DEFAULT", md);
        // Annual Leave row sits under its own header
        Assert.Contains("| 2026-06-20 | Annual Leave | 8 |", md);
        Assert.Contains("| 2026-06-19 | Meeting | 2 |", md);
    }

    // ---------- EXP-04: hours formatting + pipe escaping ----------
    [Fact]
    public async Task Markdown_Hours_IntegerWhenWhole_OneDecimalOtherwise()
    {
        var rows = new[]
        {
            Row(1, "U", "REQ-001", "P", 1, "T", "2026-06-16", 4m),    // whole -> "4"
            Row(1, "U", "REQ-001", "P", 1, "T", "2026-06-17", 3.5m),  // frac  -> "3.5"
        };
        var (svc, _, _, _) = Build(rows);

        var md = await svc.ExportMarkdownAsync(new ExportFilter(null, 2026, 6, null));

        Assert.Contains("| 2026-06-16 | T | 4 |", md);
        Assert.Contains("| 2026-06-17 | T | 3.5 |", md);
        Assert.DoesNotContain("4.0", md);
    }

    [Fact]
    public async Task Markdown_TaskNameWithPipe_IsEscaped()
    {
        var rows = new[]
        {
            Row(1, "U", "REQ-001", "P", 1, "A|B", "2026-06-16", 1m),
        };
        var (svc, _, _, _) = Build(rows);

        var md = await svc.ExportMarkdownAsync(new ExportFilter(null, 2026, 6, null));

        Assert.Contains(@"| 2026-06-16 | A\|B | 1 |", md);
    }

    [Fact]
    public async Task Markdown_FiltersByUserId_WhenProvided()
    {
        var rows = new[]
        {
            Row(1, "Alice", "REQ-001", "P", 1, "T", "2026-06-16", 4m),
            Row(2, "Bob",   "REQ-001", "P", 1, "T", "2026-06-16", 4m),
        };
        var (svc, _, _, _) = Build(rows);

        var md = await svc.ExportMarkdownAsync(new ExportFilter(2, 2026, 6, null));

        Assert.Contains("## Bob", md);
        Assert.DoesNotContain("## Alice", md);
    }

    // ---------- EXP-01: Excel reopen ----------
    [Fact]
    public async Task Excel_ProducesReopenableWorkbook_WithHeaderAndRows()
    {
        var rows = new[]
        {
            Row(1, "Alice", "REQ-001", "ProjectX", 10, "Implement", "2026-06-16", 4m),
            Row(1, "Alice", "DEFAULT", "DEFAULT",  90, "Annual Leave", "2026-06-20", 8m),
        };
        var (svc, _, _, _) = Build(rows);

        var bytes = await svc.ExportExcelAsync(new ExportFilter(null, 2026, 6, null));

        Assert.NotEmpty(bytes);
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet(1);
        // header row
        Assert.Equal("User",    ws.Cell(1, 1).GetString());
        Assert.Equal("Request", ws.Cell(1, 2).GetString());
        Assert.Equal("Project", ws.Cell(1, 3).GetString());
        Assert.Equal("Task",    ws.Cell(1, 4).GetString());
        Assert.Equal("Date",    ws.Cell(1, 5).GetString());
        Assert.Equal("Hours",   ws.Cell(1, 6).GetString());
        // first data row
        Assert.Equal("Alice",     ws.Cell(2, 1).GetString());
        Assert.Equal("REQ-001",   ws.Cell(2, 2).GetString());
        Assert.Equal("Implement", ws.Cell(2, 4).GetString());
        Assert.Equal("2026-06-16",ws.Cell(2, 5).GetString());
        Assert.Equal(4d,          ws.Cell(2, 6).GetDouble());
        // second data row present
        Assert.Equal("Annual Leave", ws.Cell(3, 4).GetString());
        Assert.Equal(8d,             ws.Cell(3, 6).GetDouble());
    }

    [Fact]
    public async Task Excel_PassesMonthRangeAndProjectFilterToRepo()
    {
        var (svc, logs, _, _) = Build(Array.Empty<TimeLogReportRow>());

        await svc.ExportExcelAsync(new ExportFilter(null, 2026, 6, "ProjectX"));

        logs.Verify(r => r.GetExportRowsAsync(
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            "ProjectX"), Times.Once);
    }
}
```

- [ ] **Step 1.2: Run tests to verify they fail**

Run: `dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~ExportServiceTests"`
Expected: FAIL — `ExportService` does not exist / no `IExportService` ctor `(ITimeLogRepository, IUserRepository, IRequestRepository)`. (<60s.)

- [ ] **Step 1.3: Write the implementation (full file)**

`src/TimesheetApp/Services/ExportService.cs`:

```csharp
using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

public sealed class ExportService : IExportService
{
    private const string DefaultCode = "DEFAULT";

    private readonly ITimeLogRepository _logs;
    private readonly IUserRepository _users;
    private readonly IRequestRepository _requests;

    public ExportService(
        ITimeLogRepository logs,
        IUserRepository users,
        IRequestRepository requests)
    {
        _logs = logs;
        _users = users;
        _requests = requests;
    }

    public async Task<string> ExportMarkdownAsync(ExportFilter filter)
    {
        var rows = await LoadAsync(filter);
        var sb = new StringBuilder();
        sb.Append("# Timesheet — ")
          .Append(filter.Year.ToString("D4", CultureInfo.InvariantCulture))
          .Append('/')
          .Append(filter.Month.ToString("D2", CultureInfo.InvariantCulture))
          .AppendLine().AppendLine();

        // user -> "group label" -> rows.
        // Real request group label = "{code} — {project}".
        // DEFAULT group label = "{code} — {task_name}" (EXP-03, decision 1).
        foreach (var userGroup in rows
                     .GroupBy(r => (r.UserId, r.UserName))
                     .OrderBy(g => g.Key.UserName, StringComparer.Ordinal))
        {
            sb.Append("## ").Append(userGroup.Key.UserName).AppendLine().AppendLine();

            var groups = userGroup
                .GroupBy(r => GroupKey(r))
                .OrderBy(g => g.Key.Sort, StringComparer.Ordinal);

            foreach (var grp in groups)
            {
                sb.Append("### ").Append(grp.Key.Header).AppendLine();
                sb.AppendLine("| Date | Task | Hours |");
                sb.AppendLine("| --- | --- | --- |");
                foreach (var row in grp.OrderBy(r => r.WorkDate).ThenBy(r => r.TaskId))
                {
                    sb.Append("| ")
                      .Append(row.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                      .Append(" | ")
                      .Append(EscapePipe(row.TaskName))
                      .Append(" | ")
                      .Append(FormatHours(row.Hours))
                      .AppendLine(" |");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    public async Task<byte[]> ExportExcelAsync(ExportFilter filter)
    {
        var rows = await LoadAsync(filter);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Timesheet");

        ws.Cell(1, 1).Value = "User";
        ws.Cell(1, 2).Value = "Request";
        ws.Cell(1, 3).Value = "Project";
        ws.Cell(1, 4).Value = "Task";
        ws.Cell(1, 5).Value = "Date";
        ws.Cell(1, 6).Value = "Hours";

        var r = 2;
        foreach (var row in rows
                     .OrderBy(x => x.UserName, StringComparer.Ordinal)
                     .ThenBy(x => x.RequestCode, StringComparer.Ordinal)
                     .ThenBy(x => x.WorkDate)
                     .ThenBy(x => x.TaskId))
        {
            ws.Cell(r, 1).Value = row.UserName;
            ws.Cell(r, 2).Value = row.RequestCode;
            ws.Cell(r, 3).Value = row.Project;
            ws.Cell(r, 4).Value = row.TaskName;
            ws.Cell(r, 5).Value = row.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            ws.Cell(r, 6).Value = (double)row.Hours;
            r++;
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // --- helpers ---

    private async Task<IReadOnlyList<TimeLogReportRow>> LoadAsync(ExportFilter filter)
    {
        var from = new DateOnly(filter.Year, filter.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var rows = await _logs.GetExportRowsAsync(from, to, filter.Project);
        if (filter.UserId is int uid)
            rows = rows.Where(x => x.UserId == uid).ToList();
        return rows;
    }

    private static (string Sort, string Header) GroupKey(TimeLogReportRow r) =>
        r.RequestCode == DefaultCode
            ? ($"{r.RequestCode}|{r.TaskName}", $"{r.RequestCode} — {r.TaskName}")   // EXP-03
            : ($"{r.RequestCode}|{r.Project}",  $"{r.RequestCode} — {r.Project}");   // EXP-02

    // "4" not "4.0"; "3.5" stays "3.5" (EXP-04).
    internal static string FormatHours(decimal h) =>
        h == Math.Truncate(h)
            ? ((long)h).ToString(CultureInfo.InvariantCulture)
            : h.ToString("0.#", CultureInfo.InvariantCulture);

    // Escape table-breaking pipe in task names (EXP-04).
    internal static string EscapePipe(string s) => s.Replace("|", @"\|");
}
```

- [ ] **Step 1.4: Run tests to verify they pass**

Run: `dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~ExportServiceTests"`
Expected: PASS — all 7 tests green. (<60s.)

- [ ] **Step 1.5: Commit**

```bash
git add src/TimesheetApp/Services/ExportService.cs src/TimesheetApp.Tests/Services/ExportServiceTests.cs
git commit -m "feat(P6): ExportService markdown+excel (EXP-01..04)"
```

**Done (grep-verifiable):**
- `grep -q "class ExportService : IExportService" src/TimesheetApp/Services/ExportService.cs`
- `grep -q "DEFAULT — \" + ... task name" ⇒ grep -q 'r.RequestCode == DefaultCode' src/TimesheetApp/Services/ExportService.cs` (DEFAULT-by-task-name branch present — EXP-03)
- `grep -q 'Replace("|"' src/TimesheetApp/Services/ExportService.cs` (pipe escaping — EXP-04)
- `grep -q "wb.SaveAs(ms)" src/TimesheetApp/Services/ExportService.cs` (Excel via MemoryStream, no SaveFileDialog — EXP-01)
- `grep -q "ExportServiceTests" src/TimesheetApp.Tests/Services/ExportServiceTests.cs` and the 7 tests pass.

---

## Task 2: SettingsViewModel (logic) — TDD

**Model:** sonnet (VM logic with mocked services).

**Files:**
- Create: `src/TimesheetApp/ViewModels/SettingsViewModel.cs`
- Test: `src/TimesheetApp.Tests/ViewModels/SettingsViewModelTests.cs`

**Read first:**
- `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md` §5 (SettingsViewModel row, line 314: owns DB-path Browse → `IAppConfig`; N-days default 3 → `Settings`; TaskTemplates CRUD; DefaultTasks add/edit/hide → `IDefaultTaskSyncService.SyncAsync()`), §4 (`IDefaultTaskSyncService` lines 270-274), §3 (`ISettingsRepository` lines 210-215, `ITaskRepository.InsertAsync/UpdateAsync/SetActiveAsync`).
- `.planning/research/STACK-RESEARCH.md` §1 (CommunityToolkit.Mvvm: `[ObservableProperty]`, `[RelayCommand]`, classes must be `partial`).
- `skills/implementer-dotnet-csharp/SKILL.md`.

**Consumed contracts (verbatim from spec — DO NOT invent new interfaces):**
- `IAppConfig` (spec §1.4 line 61, §6 line 337): treat as `{ string DbPath { get; set; } }` — app-local `%APPDATA%\TimesheetApp\appsettings.json`. `[ASSUMED]` member name `DbPath`; if P1 named it differently, STOP and reconcile (item 1 below).
- `ISettingsRepository` (spec §3 lines 210-215): `Task<string?> GetAsync(string key)`, `Task SetAsync(string key, string value)`.
- `IDefaultTaskSyncService` (spec §4 lines 270-274): `Task SyncAsync()`.
- **Template persistence = `ITaskTemplateRepository`** (RECONCILED 2026-06-21, user-approved). The canonical single home for TaskTemplates is `ITaskTemplateRepository`, created in **P4 Task 1** and used by BOTH RequestsViewModel (read) and SettingsViewModel (CRUD). Template methods are NOT on `ITaskRepository`. Methods consumed here (architecture spec §3 reconciliation block):
  ```csharp
  Task<IReadOnlyList<TaskTemplate>> GetAllAsync();   // load/reload templates (SET-03)
  Task<int> InsertAsync(TaskTemplate template);      // add (SET-03)
  Task DeleteAsync(int id);                           // delete (SET-03)
  ```

> Dependency note (executor): `ITaskTemplateRepository` is delivered by **P4 Task 1** (interface + Dapper impl + DI registration). Since P4 builds before P6 in the wave order, the interface already exists at compile time. Do NOT add template methods to `ITaskRepository` and do NOT create any other template interface.

**Settings key constant:** `warning_days` (shared `Settings` table); default `3` (SET-02).

- [ ] **Step 2.1: Write the failing tests (full file)**

`src/TimesheetApp.Tests/ViewModels/SettingsViewModelTests.cs`:

```csharp
using System.Threading.Tasks;
using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public class SettingsViewModelTests
{
    private static SettingsViewModel Build(
        out Mock<IAppConfig> config,
        out Mock<ISettingsRepository> settings,
        out Mock<ITaskTemplateRepository> templates,
        out Mock<IDefaultTaskSyncService> sync,
        string? warningDays = "3")
    {
        config = new Mock<IAppConfig>();
        config.Setup(c => c.DbPath).Returns(@"C:\old\timesheet.db");
        settings = new Mock<ISettingsRepository>();
        settings.Setup(s => s.GetAsync("warning_days")).ReturnsAsync(warningDays);
        templates = new Mock<ITaskTemplateRepository>();
        // Canonical template store = ITaskTemplateRepository (reconciliation 2026-06-21).
        templates.Setup(t => t.GetAllAsync())
             .ReturnsAsync(new[] { new TaskTemplate(1, "Std", "Implement", 0) });
        sync = new Mock<IDefaultTaskSyncService>();
        return new SettingsViewModel(config.Object, settings.Object, templates.Object, sync.Object);
    }

    // ---------- SET-02: N-days default 3 + persist ----------
    [Fact]
    public async Task WarningDays_DefaultsTo3_WhenSettingMissing()
    {
        var vm = Build(out _, out _, out _, out _, warningDays: null);
        await vm.LoadAsync();
        Assert.Equal(3, vm.WarningDays);
    }

    [Fact]
    public async Task WarningDays_LoadsPersistedValue()
    {
        var vm = Build(out _, out _, out _, out _, warningDays: "7");
        await vm.LoadAsync();
        Assert.Equal(7, vm.WarningDays);
    }

    [Fact]
    public async Task SaveWarningDays_PersistsToSharedSettingsTable()
    {
        var vm = Build(out _, out var settings, out _, out _);
        await vm.LoadAsync();
        vm.WarningDays = 5;
        await vm.SaveWarningDaysCommand.ExecuteAsync(null);
        settings.Verify(s => s.SetAsync("warning_days", "5"), Times.Once);
    }

    // ---------- SET-01: DB path -> app-local config, NOT shared DB ----------
    [Fact]
    public async Task ApplyDbPath_WritesToAppConfig_NotSettingsRepo()
    {
        var vm = Build(out var config, out var settings, out _, out _);
        await vm.LoadAsync();
        vm.DbPath = @"D:\new\timesheet.db";
        await vm.ApplyDbPathCommand.ExecuteAsync(null);
        config.Verify(c => c.SetDbPath(@"D:\new\timesheet.db"), Times.Once);
        settings.Verify(s => s.SetAsync(It.Is<string>(k => k.Contains("path")), It.IsAny<string>()), Times.Never);
    }

    // ---------- SET-03: template CRUD (via ITaskTemplateRepository) ----------
    [Fact]
    public async Task LoadAsync_LoadsTemplates()
    {
        var vm = Build(out _, out _, out var templates, out _);
        await vm.LoadAsync();
        Assert.Single(vm.Templates);
        Assert.Equal("Std", vm.Templates[0].TemplateName);
    }

    [Fact]
    public async Task AddTemplate_InsertsAndReloads()
    {
        var vm = Build(out _, out _, out var templates, out _);
        await vm.LoadAsync();
        vm.NewTemplateName = "Bugfix";
        vm.NewTemplateTaskName = "Triage";
        await vm.AddTemplateCommand.ExecuteAsync(null);
        templates.Verify(t => t.InsertAsync(
            It.Is<TaskTemplate>(x => x.TemplateName == "Bugfix" && x.TaskName == "Triage")),
            Times.Once);
        templates.Verify(t => t.GetAllAsync(), Times.Exactly(2)); // initial load + reload
    }

    [Fact]
    public async Task DeleteTemplate_RemovesAndReloads()
    {
        var vm = Build(out _, out _, out var templates, out _);
        await vm.LoadAsync();
        vm.SelectedTemplate = vm.Templates[0];
        await vm.DeleteTemplateCommand.ExecuteAsync(null);
        templates.Verify(t => t.DeleteAsync(1), Times.Once);
        templates.Verify(t => t.GetAllAsync(), Times.Exactly(2));
    }

    // ---------- SET-04: DefaultTask edit triggers sync ----------
    [Fact]
    public async Task SaveDefaultTasks_CallsSync()
    {
        var vm = Build(out _, out _, out _, out var sync);
        await vm.LoadAsync();
        await vm.SaveDefaultTasksCommand.ExecuteAsync(null);
        sync.Verify(s => s.SyncAsync(), Times.Once);
    }
}
```

- [ ] **Step 2.2: Run tests to verify they fail**

Run: `dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~SettingsViewModelTests"`
Expected: FAIL — `SettingsViewModel` not defined. (<60s.)

- [ ] **Step 2.3: Write the implementation (full file)**

> Template CRUD binds to `ITaskTemplateRepository` (canonical, delivered by P4 Task 1). Do NOT add template methods to `ITaskRepository` and do NOT create any other template interface.

`src/TimesheetApp/ViewModels/SettingsViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const string WarningDaysKey = "warning_days";
    private const int DefaultWarningDays = 3;

    private readonly IAppConfig _config;
    private readonly ISettingsRepository _settings;
    private readonly ITaskTemplateRepository _templates;   // canonical template store (reconciliation 2026-06-21)
    private readonly IDefaultTaskSyncService _sync;

    public SettingsViewModel(
        IAppConfig config,
        ISettingsRepository settings,
        ITaskTemplateRepository templates,
        IDefaultTaskSyncService sync)
    {
        _config = config;
        _settings = settings;
        _templates = templates;
        _sync = sync;
    }

    [ObservableProperty] private string _dbPath = "";
    [ObservableProperty] private int _warningDays = DefaultWarningDays;
    [ObservableProperty] private string _newTemplateName = "";
    [ObservableProperty] private string _newTemplateTaskName = "";
    [ObservableProperty] private TaskTemplate? _selectedTemplate;

    public ObservableCollection<TaskTemplate> Templates { get; } = new();

    public async Task LoadAsync()
    {
        DbPath = _config.DbPath;

        var raw = await _settings.GetAsync(WarningDaysKey);
        WarningDays = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : DefaultWarningDays;

        await ReloadTemplatesAsync();
    }

    // SET-01: app-local config only, never the shared Settings table.
    [RelayCommand]
    private Task ApplyDbPathAsync()
    {
        _config.SetDbPath(DbPath);   // IAppConfig persists path to %APPDATA% (P1 contract: get + SetDbPath)
        return Task.CompletedTask;
    }

    // SET-02: shared Settings table.
    [RelayCommand]
    private Task SaveWarningDaysAsync() =>
        _settings.SetAsync(WarningDaysKey, WarningDays.ToString(CultureInfo.InvariantCulture));

    // SET-03: template CRUD (via ITaskTemplateRepository — canonical store).
    [RelayCommand]
    private async Task AddTemplateAsync()
    {
        await _templates.InsertAsync(new TaskTemplate(0, NewTemplateName, NewTemplateTaskName, 0));
        NewTemplateName = "";
        NewTemplateTaskName = "";
        await ReloadTemplatesAsync();
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync()
    {
        if (SelectedTemplate is null) return;
        await _templates.DeleteAsync(SelectedTemplate.Id);
        await ReloadTemplatesAsync();
    }

    // SET-04: reconcile DefaultTasks -> DEFAULT request's Tasks (rename = soft-delete + insert).
    [RelayCommand]
    private Task SaveDefaultTasksAsync() => _sync.SyncAsync();

    private async Task ReloadTemplatesAsync()
    {
        var items = await _templates.GetAllAsync();
        Templates.Clear();
        foreach (var t in items) Templates.Add(t);
    }
}
```

> `ITaskTemplateRepository` already exists from P4 Task 1 (interface + Dapper impl + DI). No new repository code is needed in P6 — only consume it.

- [ ] **Step 2.4: Run tests to verify they pass**

Run: `dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~SettingsViewModelTests"`
Expected: PASS — all 8 tests green. (<60s.)

- [ ] **Step 2.5: Commit**

```bash
git add src/TimesheetApp/ViewModels/SettingsViewModel.cs src/TimesheetApp.Tests/ViewModels/SettingsViewModelTests.cs
git commit -m "feat(P6): SettingsViewModel db-path/n-days/templates/sync (SET-01..04)"
```

**Done (grep-verifiable):**
- `grep -q "class SettingsViewModel : ObservableObject" src/TimesheetApp/ViewModels/SettingsViewModel.cs`
- `grep -q "_config.DbPath = DbPath" src/TimesheetApp/ViewModels/SettingsViewModel.cs` (SET-01: app-local write)
- `grep -q 'SetAsync(WarningDaysKey' src/TimesheetApp/ViewModels/SettingsViewModel.cs` (SET-02)
- `grep -q "AddTemplateAsync" src/TimesheetApp/ViewModels/SettingsViewModel.cs` (SET-03)
- `grep -q "_sync.SyncAsync()" src/TimesheetApp/ViewModels/SettingsViewModel.cs` (SET-04)
- 8 tests pass.

---

## Task 3: SettingsTab.xaml (UI binding) — manual-verify

**Model:** haiku (declarative XAML, no logic).

**Files:**
- Create: `src/TimesheetApp/Views/Tabs/SettingsTab.xaml`
- Create: `src/TimesheetApp/Views/Tabs/SettingsTab.xaml.cs`

**Read first:**
- `src/TimesheetApp/ViewModels/SettingsViewModel.cs` (Task 2 output — bind to exact member names: `DbPath`, `WarningDays`, `Templates`, `SelectedTemplate`, `NewTemplateName`, `NewTemplateTaskName`, and the 5 commands `ApplyDbPathCommand`, `SaveWarningDaysCommand`, `AddTemplateCommand`, `DeleteTemplateCommand`, `SaveDefaultTasksCommand`).
- `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md` §5 (SettingsViewModel responsibilities) + §1.4 folder layout (`Views/Tabs/`).
- `skills/implementer-dotnet-csharp/SKILL.md`.

**Note:** `SettingsViewModel.LoadAsync()` is invoked by `MainViewModel`/host when the tab activates (P6 does not own MainViewModel wiring — that lives in the P3/host plan). The Browse button raises `OpenFileDialog` in code-behind (dialog ownership is a View concern; the VM stays WPF-free per spec §4 line 268), sets `vm.DbPath`, then the `ApplyDbPath` button binds the command. Do NOT call `SaveFileDialog` from any service.

- [ ] **Step 3.1: Write the XAML**

`src/TimesheetApp/Views/Tabs/SettingsTab.xaml`:

```xml
<UserControl x:Class="TimesheetApp.Views.Tabs.SettingsTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <ScrollViewer>
        <StackPanel Margin="16">

            <!-- SET-01: DB path + Browse -->
            <TextBlock Text="Database file" FontWeight="Bold"/>
            <StackPanel Orientation="Horizontal" Margin="0,4,0,12">
                <TextBox Width="420" Text="{Binding DbPath, UpdateSourceTrigger=PropertyChanged}"/>
                <Button Content="Browse…" Margin="8,0,0,0" Click="OnBrowse"/>
                <Button Content="Apply" Margin="8,0,0,0" Command="{Binding ApplyDbPathCommand}"/>
            </StackPanel>

            <!-- SET-02: N-days warning (default 3) -->
            <TextBlock Text="“Chưa log” warning window (working days)" FontWeight="Bold"/>
            <StackPanel Orientation="Horizontal" Margin="0,4,0,12">
                <TextBox Width="60" Text="{Binding WarningDays, UpdateSourceTrigger=PropertyChanged}"/>
                <Button Content="Save" Margin="8,0,0,0" Command="{Binding SaveWarningDaysCommand}"/>
            </StackPanel>

            <!-- SET-03: TaskTemplates CRUD -->
            <TextBlock Text="Task templates" FontWeight="Bold"/>
            <ListBox ItemsSource="{Binding Templates}" SelectedItem="{Binding SelectedTemplate}"
                     Height="120" DisplayMemberPath="TemplateName" Margin="0,4,0,4"/>
            <StackPanel Orientation="Horizontal" Margin="0,0,0,12">
                <TextBox Width="160" Text="{Binding NewTemplateName, UpdateSourceTrigger=PropertyChanged}"/>
                <TextBox Width="160" Margin="8,0,0,0"
                         Text="{Binding NewTemplateTaskName, UpdateSourceTrigger=PropertyChanged}"/>
                <Button Content="Add" Margin="8,0,0,0" Command="{Binding AddTemplateCommand}"/>
                <Button Content="Delete" Margin="8,0,0,0" Command="{Binding DeleteTemplateCommand}"/>
            </StackPanel>

            <!-- SET-04: DefaultTasks sync -->
            <TextBlock Text="Default tasks" FontWeight="Bold"/>
            <Button Content="Sync default tasks" HorizontalAlignment="Left"
                    Margin="0,4,0,0" Command="{Binding SaveDefaultTasksCommand}"/>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

`src/TimesheetApp/Views/Tabs/SettingsTab.xaml.cs`:

```csharp
using Microsoft.Win32;
using System.Windows.Controls;
using TimesheetApp.ViewModels;

namespace TimesheetApp.Views.Tabs;

public partial class SettingsTab : UserControl
{
    public SettingsTab() => InitializeComponent();

    // Dialog ownership is a View concern (services stay WPF-free, spec §4).
    private void OnBrowse(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new OpenFileDialog
        {
            Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            CheckFileExists = false
        };
        if (dlg.ShowDialog() == true) vm.DbPath = dlg.FileName;
    }
}
```

- [ ] **Step 3.2: Build to verify XAML compiles**

Run: `dotnet build src/TimesheetApp/TimesheetApp.csproj`
Expected: BUILD SUCCEEDED — XAML codegen resolves all bindings to existing `SettingsViewModel` members. (<60s; if the host project build pulls more, scope the build to the single csproj.)

- [ ] **Step 3.3: Commit**

```bash
git add src/TimesheetApp/Views/Tabs/SettingsTab.xaml src/TimesheetApp/Views/Tabs/SettingsTab.xaml.cs
git commit -m "feat(P6): SettingsTab.xaml binding SettingsViewModel (SET-01..04)"
```

**Done (grep-verifiable):**
- `grep -q 'Command="{Binding ApplyDbPathCommand}"' src/TimesheetApp/Views/Tabs/SettingsTab.xaml` (SET-01)
- `grep -q 'Binding WarningDays' src/TimesheetApp/Views/Tabs/SettingsTab.xaml` (SET-02)
- `grep -q 'ItemsSource="{Binding Templates}"' src/TimesheetApp/Views/Tabs/SettingsTab.xaml` (SET-03)
- `grep -q 'Command="{Binding SaveDefaultTasksCommand}"' src/TimesheetApp/Views/Tabs/SettingsTab.xaml` (SET-04)
- `grep -q "OpenFileDialog" src/TimesheetApp/Views/Tabs/SettingsTab.xaml.cs` (Browse dialog in View, not service)

**Manual-verify checklist (Step 8a UAT — no automated coverage):**
1. Launch app → Settings tab. Browse picks a `.db` path; Apply writes it to `%APPDATA%\TimesheetApp\appsettings.json` (open file, confirm path; SET-01).
2. Change N-days to 5 → Save → reopen app → field shows 5 and Reports banner uses 5 (SET-02).
3. Add a template "Bugfix/Triage" → appears in list → selectable in Requests create dialog (SET-03).
4. Rename a DefaultTask → Sync → old name's Task soft-deleted, new inserted, prior TimeLogs still export (SET-04, decision 7).

---

## REQ Coverage Check (all 8 P6 REQs)

| REQ | Task | Automated evidence |
|---|---|---|
| SET-01 | Task 2 (logic), Task 3 (UI) | `ApplyDbPath_WritesToAppConfig_NotSettingsRepo` |
| SET-02 | Task 2 (logic), Task 3 (UI) | `WarningDays_DefaultsTo3_WhenSettingMissing`, `SaveWarningDays_PersistsToSharedSettingsTable` |
| SET-03 | Task 2 (logic), Task 3 (UI) | `AddTemplate_InsertsAndReloads`, `DeleteTemplate_RemovesAndReloads` |
| SET-04 | Task 2 (logic), Task 3 (UI) | `SaveDefaultTasks_CallsSync` (+ manual rename UAT for decision 7) |
| EXP-01 | Task 1 | `Excel_ProducesReopenableWorkbook_WithHeaderAndRows`, `Excel_PassesMonthRangeAndProjectFilterToRepo` |
| EXP-02 | Task 1 | `Markdown_RealRequest_RendersHeaderAndTable` |
| EXP-03 | Task 1 | `Markdown_DefaultRequest_HeaderUsesTaskNameNotProject` |
| EXP-04 | Task 1 | `Markdown_Hours_IntegerWhenWhole...`, `Markdown_TaskNameWithPipe_IsEscaped` |

No orphan REQ; every task cites ≥1 REQ.

## Open reconciliation items for executor (do not invent — STOP and confirm against P1/P2 output)
1. `IAppConfig` member name (`DbPath` assumed) — bind to the actual property.
2. Template persistence surface (BLOCKING for Task 2) — spec §5 line 314 routes templates through `ITaskRepository`, but spec §3 lists no template methods on it. Confirm P1's actual `ITaskRepository.cs`: bind to its real template method names if present; if absent, escalate to the architecture lead and add the three additive `*Template*` methods to `ITaskRepository` (NOT a new `ITemplateRepository`). Resolve before writing Task 2 tests — the entire Task 2 fixture mocks this surface.
3. `ITimeLogRepository.GetExportRowsAsync` user filtering happens in `ExportService` (repo has no user param per spec §3 line 204) — confirmed against spec; no change expected.
