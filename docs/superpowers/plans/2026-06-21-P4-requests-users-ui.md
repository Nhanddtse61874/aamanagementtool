---
must_haves:
  observable_truths:
    - "User can type a code/project substring in the Requests tab and the visible request list filters to matching rows (REQ-01)."
    - "User can create a request with a code + project, optionally pick a template to auto-generate ordered tasks, and add/remove/reorder custom tasks before saving (REQ-02)."
    - "User can edit an existing request's name/project and add a task to it (REQ-03)."
    - "User can soft-delete a TASK (it disappears from the editable task list, its TimeLogs survive); there is NO affordance anywhere to delete a Request (REQ-04, decision 4)."
    - "User can see all users with an Active/Inactive indicator (USR-01)."
    - "User can add a user by name; the user is created active (USR-02)."
    - "User can soft-delete a user (set inactive); their TimeLogs are preserved (USR-03)."
  required_artifacts:
    - "src/TimesheetApp/ViewModels/RequestsViewModel.cs — observable request list + search + create/edit/soft-delete-task orchestration over repos."
    - "src/TimesheetApp/ViewModels/RequestEditorViewModel.cs — bindable working-set for the create/edit dialog (code/project + ordered editable task rows + template apply)."
    - "src/TimesheetApp/ViewModels/EditableTaskRowVm.cs — one bindable task row (name + order + new/existing + removed flag)."
    - "src/TimesheetApp/ViewModels/UsersViewModel.cs — observable user list + add + soft-delete."
    - "src/TimesheetApp/Data/Repositories/ITaskTemplateRepository.cs — read seam for templates feeding REQ-02 (GetAllAsync grouped by TemplateName)."
    - "src/TimesheetApp/Views/Tabs/RequestsTab.xaml (+.cs) — request list, search box, create/edit dialog host."
    - "src/TimesheetApp/Views/Tabs/UsersTab.xaml (+.cs) — user list with status + add + deactivate."
    - "src/TimesheetApp.Tests/ViewModels/RequestsViewModelTests.cs — xUnit + mocked repos."
    - "src/TimesheetApp.Tests/ViewModels/RequestEditorViewModelTests.cs — template task-generation + reorder + soft-delete logic."
    - "src/TimesheetApp.Tests/ViewModels/UsersViewModelTests.cs — list/add/soft-delete logic."
  required_wiring:
    - "RequestsViewModel ctor injects IRequestRepository, ITaskRepository, ITaskTemplateRepository (matches spec §5 'IRequestRepository, ITaskRepository, ISettingsRepository (templates)' — template read isolated behind ITaskTemplateRepository)."
    - "UsersViewModel ctor injects IUserRepository only (spec §5)."
    - "App.xaml.cs registers ITaskTemplateRepository -> TaskTemplateRepository as a singleton alongside the existing repo registrations (spec §6 pattern); RequestsViewModel/UsersViewModel already registered transient in §6."
    - "RequestsTab.xaml DataContext = RequestsViewModel; UsersTab.xaml DataContext = UsersViewModel; both hosted in MainWindow TabControl."
  key_links:
    - "REQ-01 -> IRequestRepository.SearchAsync(term) -> RequestsViewModel.SearchTerm setter -> Requests ObservableCollection."
    - "REQ-02 -> RequestEditorViewModel.ApplyTemplate + Add/Remove/MoveUp/MoveDown -> RequestsViewModel.SaveNewAsync -> IRequestRepository.InsertAsync + ITaskRepository.InsertAsync(order_index)."
    - "REQ-03 -> RequestEditorViewModel(edit mode) -> RequestsViewModel.SaveEditAsync -> IRequestRepository.UpdateAsync + ITaskRepository.InsertAsync."
    - "REQ-04 -> RequestEditorViewModel.RemoveTask(existing) sets IsRemoved=true -> RequestsViewModel.SaveEditAsync -> ITaskRepository.SetActiveAsync(taskId,false); NO IRequestRepository.SetActiveAsync exists (verified absent in spec §3)."
    - "USR-01 -> IUserRepository.GetAllAsync -> UsersViewModel.Users (IsActive shown)."
    - "USR-02 -> UsersViewModel.AddUserAsync -> IUserRepository.InsertAsync(new User(0,name,null,true))."
    - "USR-03 -> UsersViewModel.DeactivateAsync -> IUserRepository.SetActiveAsync(id,false)."
---

# P4 — Requests + Users UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Requests tab (list + search + create-with-template + edit + soft-delete-task) and the Users tab (list + add + soft-delete) as MVVM ViewModels with full xUnit coverage, plus their XAML views.

**Architecture:** CommunityToolkit.Mvvm ViewModels (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`) orchestrate the P1/P2 repository interfaces verbatim from the architecture spec. ViewModels hold all list-shaping, search-debounce-free filtering (delegated to `IRequestRepository.SearchAsync`), template-to-task generation, and `order_index` reorder logic — never SQL. XAML views are thin and manually verified. A read-only `ITaskTemplateRepository` seam supplies templates for REQ-02 (P6/SET-03 owns template CRUD; this phase only reads).

**Tech Stack:** .NET 8 (`net8.0-windows` app, `net8.0` test project), CommunityToolkit.Mvvm 8.4.2, xUnit, Moq, Dapper/Microsoft.Data.Sqlite (existing P1 repos, not touched here).

**Mode B input:** User-approved spec `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md` (§2 models, §3 repos, §5 VM responsibilities, §6 DI). Contracts below are copied VERBATIM from that spec — do not redesign them.

**Dependency note:** Depends on P1 (models, `IRequestRepository`, `ITaskRepository`, `IUserRepository`, `SqliteConnectionFactory`) and P2 (services — not used by P4 VMs). P4 VMs call repos directly per spec §5 (Requests/Users VMs depend on repos, not services).

**`[ASSUMED]` flag:** Spec §5 lists RequestsViewModel calling `ISettingsRepository (templates)`, but no template-read method is defined on `ISettingsRepository` in §3 (it is a string key-value store). To avoid fabricating a signature on a typed key-value interface, this plan introduces a thin `ITaskTemplateRepository` read seam returning the spec's `TaskTemplate` record. This is additive and consistent with SET-03 ("templates selectable when creating a Request"). Tag this `[ASSUMED]` in the commit message for Task 1.

---

## Contracts copied verbatim from the spec (DO NOT redesign)

```csharp
// Models (spec §2)
public sealed record User(int Id, string Name, string? WindowsUsername, bool IsActive);
public sealed record Request(int Id, string RequestCode, string Project, DateTimeOffset CreatedAt);
public sealed record TaskItem(int Id, int RequestId, string TaskName, int OrderIndex, bool IsActive);
public sealed record TaskTemplate(int Id, string TemplateName, string TaskName, int OrderIndex);

// Repo interfaces used by P4 (spec §3)
public interface IUserRepository
{
    Task<IReadOnlyList<User>> GetActiveAsync();
    Task<IReadOnlyList<User>> GetAllAsync();
    Task<User?> GetByIdAsync(int id);
    Task<User?> GetByWindowsUsernameAsync(string windowsUsername);
    Task<int>  InsertAsync(User user);
    Task SetWindowsUsernameAsync(int userId, string windowsUsername);
    Task SetActiveAsync(int userId, bool isActive);
    Task UpdateNameAsync(int userId, string name);
}

public interface IRequestRepository
{
    Task<IReadOnlyList<Request>> SearchAsync(string? term); // null => all; matches code OR project (REQ-01)
    Task<Request?> GetByIdAsync(int id);
    Task<Request?> GetByCodeAsync(string requestCode);
    Task<int>  InsertAsync(Request request);                // REQ-02
    Task UpdateAsync(Request request);                      // REQ-03
    // No SetActiveAsync — Requests are NOT soft-deletable in v1 (REQ-04, decision 4).
}

public interface ITaskRepository
{
    Task<IReadOnlyList<TaskItem>> GetActiveByRequestAsync(int requestId);
    Task<IReadOnlyList<TaskItem>> GetActiveForTimesheetAsync();
    Task<TaskItem?> GetByIdAsync(int id);
    Task<TaskItem?> GetByNameInRequestAsync(int requestId, string taskName);
    Task<int>  InsertAsync(TaskItem task);                  // REQ-02/REQ-03
    Task UpdateAsync(TaskItem task);                        // name + order_index
    Task SetActiveAsync(int taskId, bool isActive);         // soft delete (REQ-04)
}
```

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/TimesheetApp/Data/Repositories/ITaskTemplateRepository.cs` | read seam: list all `TaskTemplate`s (REQ-02 template source) | T1 |
| `src/TimesheetApp/Data/Repositories/TaskTemplateRepository.cs` | Dapper impl of the read seam | T1 |
| `src/TimesheetApp/ViewModels/EditableTaskRowVm.cs` | one bindable task row in the editor | T2 |
| `src/TimesheetApp/ViewModels/RequestEditorViewModel.cs` | create/edit working-set: code/project + template apply + add/remove/reorder | T2 |
| `src/TimesheetApp/ViewModels/RequestsViewModel.cs` | list + search + open editor + persist create/edit/soft-delete-task | T3 |
| `src/TimesheetApp/ViewModels/UsersViewModel.cs` | list + add + soft-delete user | T4 |
| `src/TimesheetApp/Views/Tabs/RequestsTab.xaml (+.cs)` | Requests UI | T5 |
| `src/TimesheetApp/Views/Tabs/UsersTab.xaml (+.cs)` | Users UI | T5 |
| `src/TimesheetApp/App.xaml.cs` | DI registration for `ITaskTemplateRepository` | T1 |
| `src/TimesheetApp.Tests/ViewModels/RequestEditorViewModelTests.cs` | editor logic tests | T2 |
| `src/TimesheetApp.Tests/ViewModels/RequestsViewModelTests.cs` | list/search/persist tests | T3 |
| `src/TimesheetApp.Tests/ViewModels/UsersViewModelTests.cs` | users tests | T4 |

---

## Wave Plan

| Wave | Tasks | Rationale / file-overlap check |
|---|---|---|
| **Wave 1** | T1 (template repo seam + DI) | T3 depends on `ITaskTemplateRepository`; must land first. Touches `App.xaml.cs` + two repo files. |
| **Wave 2** | T2 (RequestEditorViewModel + EditableTaskRowVm), **T4 (UsersViewModel)** | Independent — zero shared files. T2 owns `RequestEditorViewModel.cs`/`EditableTaskRowVm.cs`; T4 owns `UsersViewModel.cs`. Can run in parallel. |
| **Wave 3** | T3 (RequestsViewModel) | Depends on T1 (`ITaskTemplateRepository`) + T2 (`RequestEditorViewModel`). Touches only `RequestsViewModel.cs` + its test. |
| **Wave 4** | T5 (RequestsTab.xaml + UsersTab.xaml — MANUAL VERIFY) | Depends on all VMs (T2/T3/T4). XAML only; binds to finished VMs. |

> **Intra-wave file-overlap check:** Wave 2's two tasks share no file (RequestEditor* vs Users*). All other waves are single-task. No forced sequencing needed beyond the dependency ordering above.

> **Context budget:** 5 tasks across 4 waves. Each task is one VM (or one cohesive pair) + its test file — well under the ~50% per-plan budget.

---

## Model assignment (config: model_profile=quality → shift applied)

Base `model_defaults`: mechanical=haiku, standard=sonnet, complex=opus. `quality` profile bumps haiku→sonnet, sonnet→opus (opus clamped). The `<model>` tags below show the **base** tier; the controller applies the quality shift at dispatch.

| Task | Base `<model>` | Reason |
|---|---|---|
| T1 | sonnet | repo seam + DI, straightforward but typed SQL |
| T2 | sonnet | template-gen + reorder + soft-delete logic (VM logic) |
| T3 | sonnet | search + persist orchestration (VM logic) |
| T4 | sonnet | list/add/soft-delete (VM logic) |
| T5 | haiku | XAML binding only, manual-verify |

---

## Task 1: ITaskTemplateRepository read seam + DI

`<model>sonnet</model>`

**REQ-IDs:** REQ-02 (template source for create-request).

**read_first:**
- `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md` §2 (TaskTemplate model), §3 (repo style: short connection via `IConnectionFactory.Create()`, Dapper only), §6 (DI registration pattern).

**Files:**
- Create: `src/TimesheetApp/Data/Repositories/ITaskTemplateRepository.cs`
- Create: `src/TimesheetApp/Data/Repositories/TaskTemplateRepository.cs`
- Create: `src/TimesheetApp.Tests/Data/TaskTemplateRepositoryTests.cs`
- Modify: `src/TimesheetApp/App.xaml.cs` (add one registration line)

- [ ] **Step 1: Write the interface**

```csharp
// src/TimesheetApp/Data/Repositories/ITaskTemplateRepository.cs
namespace TimesheetApp.Data.Repositories;

using TimesheetApp.Models;

public interface ITaskTemplateRepository
{
    // All template rows across all templates, ordered by TemplateName then OrderIndex.
    // Source for REQ-02 (apply template -> auto tasks). P6/SET-03 owns CRUD.
    Task<IReadOnlyList<TaskTemplate>> GetAllAsync();
}
```

- [ ] **Step 2: Write the failing integration test (temp-file SQLite)**

```csharp
// src/TimesheetApp.Tests/Data/TaskTemplateRepositoryTests.cs
using Microsoft.Data.Sqlite;
using System.Data;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using Xunit;

namespace TimesheetApp.Tests.Data;

public sealed class TaskTemplateRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tmpl-{Guid.NewGuid():N}.db");
    private readonly IConnectionFactory _factory;

    public TaskTemplateRepositoryTests()
    {
        _factory = new SqliteConnectionFactory(_dbPath);
        using var c = _factory.Create();
        Exec(c, """
            CREATE TABLE TaskTemplates(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                template_name TEXT NOT NULL,
                task_name TEXT NOT NULL,
                order_index INTEGER NOT NULL);
            INSERT INTO TaskTemplates(template_name,task_name,order_index) VALUES
                ('Web','Setup',0),('Web','Build',1),('API','Design',0);
            """);
    }

    private static void Exec(IDbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task GetAllAsync_returns_all_rows_ordered_by_template_then_order()
    {
        var repo = new TaskTemplateRepository(_factory);

        var rows = await repo.GetAllAsync();

        Assert.Equal(3, rows.Count);
        Assert.Equal("API", rows[0].TemplateName);   // API before Web
        Assert.Equal("Design", rows[0].TaskName);
        Assert.Equal("Web", rows[1].TemplateName);
        Assert.Equal("Setup", rows[1].TaskName);     // order_index 0 before 1
        Assert.Equal("Build", rows[2].TaskName);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~TaskTemplateRepositoryTests`
Expected: FAIL — `TaskTemplateRepository` does not exist (compile error).

- [ ] **Step 4: Write the Dapper implementation**

```csharp
// src/TimesheetApp/Data/Repositories/TaskTemplateRepository.cs
namespace TimesheetApp.Data.Repositories;

using Dapper;
using TimesheetApp.Models;

public sealed class TaskTemplateRepository : ITaskTemplateRepository
{
    private readonly IConnectionFactory _factory;
    public TaskTemplateRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<TaskTemplate>> GetAllAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<TaskTemplate>("""
            SELECT id AS Id, template_name AS TemplateName,
                   task_name AS TaskName, order_index AS OrderIndex
            FROM TaskTemplates
            ORDER BY template_name, order_index;
            """);
        return rows.AsList();
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~TaskTemplateRepositoryTests`
Expected: PASS (1 test).

- [ ] **Step 6: Register in DI**

In `src/TimesheetApp/App.xaml.cs`, immediately after the existing `sc.AddSingleton<ISettingsRepository, SettingsRepository>();` line, add:

```csharp
        sc.AddSingleton<ITaskTemplateRepository, TaskTemplateRepository>();
```

- [ ] **Step 7: Build to verify wiring**

Run: `dotnet build src/TimesheetApp/TimesheetApp.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/TimesheetApp/Data/Repositories/ITaskTemplateRepository.cs src/TimesheetApp/Data/Repositories/TaskTemplateRepository.cs src/TimesheetApp.Tests/Data/TaskTemplateRepositoryTests.cs src/TimesheetApp/App.xaml.cs
git commit -m "feat(p4): add ITaskTemplateRepository read seam for REQ-02 templates [ASSUMED interface]"
```

**done (grep-verifiable):**
- `grep -q "interface ITaskTemplateRepository" src/TimesheetApp/Data/Repositories/ITaskTemplateRepository.cs`
- `grep -q "AddSingleton<ITaskTemplateRepository, TaskTemplateRepository>" src/TimesheetApp/App.xaml.cs`

**verify (automated, <60s):**
- `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~TaskTemplateRepositoryTests` → 1 passed.

---

## Task 2: RequestEditorViewModel + EditableTaskRowVm (create/edit working-set)

`<model>sonnet</model>`

**REQ-IDs:** REQ-02 (create w/ optional template + add/remove/reorder), REQ-03 (edit name + add task), REQ-04 (soft-delete TASK only — no request delete).

**read_first:**
- `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md` §2 (Request, TaskItem, TaskTemplate), §5 (RequestsViewModel row: "create dialog (code/project + optional template + add/remove/reorder tasks); edit (name + add task)").

**Files:**
- Create: `src/TimesheetApp/ViewModels/EditableTaskRowVm.cs`
- Create: `src/TimesheetApp/ViewModels/RequestEditorViewModel.cs`
- Create: `src/TimesheetApp.Tests/ViewModels/RequestEditorViewModelTests.cs`

**Design contract for this task (referenced by Task 3 — keep names exact):**

```csharp
// EditableTaskRowVm public surface
//   int    ExistingTaskId   // 0 for a brand-new task; >0 for an existing TaskItem
//   string TaskName         // [ObservableProperty]
//   int    OrderIndex       // [ObservableProperty]
//   bool   IsRemoved        // [ObservableProperty]; existing task flagged for soft-delete

// RequestEditorViewModel public surface (consumed by RequestsViewModel)
//   bool   IsEditMode               // true => editing existing request
//   int    EditingRequestId         // 0 in create mode
//   string RequestCode              // [ObservableProperty]
//   string Project                  // [ObservableProperty]
//   ObservableCollection<TaskTemplate>     Templates           // grouped source (all rows)
//   IReadOnlyList<string>                  TemplateNames       // distinct names for the picker
//   string?                                SelectedTemplateName// [ObservableProperty]
//   ObservableCollection<EditableTaskRowVm> Tasks
//   void ApplyTemplate()            // RelayCommand: append SelectedTemplateName's tasks
//   void AddTask(string name)       // RelayCommand-friendly; appends a new EditableTaskRowVm
//   void RemoveTask(EditableTaskRowVm row)  // new => drop from list; existing => IsRemoved=true
//   void MoveUp(EditableTaskRowVm row) / MoveDown(...)  // reorder + reindex OrderIndex
//   IReadOnlyList<EditableTaskRowVm> ActiveTasks   // Tasks where !IsRemoved, reindexed 0..n
```

- [ ] **Step 1: Write the failing tests**

```csharp
// src/TimesheetApp.Tests/ViewModels/RequestEditorViewModelTests.cs
using System.Collections.ObjectModel;
using TimesheetApp.Models;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public sealed class RequestEditorViewModelTests
{
    private static IReadOnlyList<TaskTemplate> WebTemplate() => new[]
    {
        new TaskTemplate(1, "Web", "Setup", 0),
        new TaskTemplate(2, "Web", "Build", 1),
        new TaskTemplate(3, "API", "Design", 0),
    };

    [Fact] // REQ-02: create mode starts empty
    public void Create_mode_starts_empty()
    {
        var vm = RequestEditorViewModel.ForCreate(WebTemplate());

        Assert.False(vm.IsEditMode);
        Assert.Equal(0, vm.EditingRequestId);
        Assert.Empty(vm.Tasks);
        Assert.Equal(new[] { "API", "Web" }, vm.TemplateNames); // distinct, ordered
    }

    [Fact] // REQ-02: applying a template appends only that template's tasks, ordered
    public void ApplyTemplate_appends_selected_template_tasks_in_order()
    {
        var vm = RequestEditorViewModel.ForCreate(WebTemplate());
        vm.SelectedTemplateName = "Web";

        vm.ApplyTemplate();

        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal("Setup", vm.Tasks[0].TaskName);
        Assert.Equal("Build", vm.Tasks[1].TaskName);
        Assert.Equal(0, vm.Tasks[0].OrderIndex);
        Assert.Equal(1, vm.Tasks[1].OrderIndex);
        Assert.All(vm.Tasks, t => Assert.Equal(0, t.ExistingTaskId)); // template tasks are new
    }

    [Fact] // REQ-02: custom add appends after template tasks with next order_index
    public void AddTask_appends_with_next_order_index()
    {
        var vm = RequestEditorViewModel.ForCreate(WebTemplate());
        vm.SelectedTemplateName = "Web";
        vm.ApplyTemplate();

        vm.AddTask("Custom");

        Assert.Equal(3, vm.Tasks.Count);
        Assert.Equal("Custom", vm.Tasks[2].TaskName);
        Assert.Equal(2, vm.Tasks[2].OrderIndex);
    }

    [Fact] // REQ-02: removing a NEW task drops it and reindexes
    public void RemoveTask_new_drops_and_reindexes()
    {
        var vm = RequestEditorViewModel.ForCreate(WebTemplate());
        vm.AddTask("A");
        vm.AddTask("B");
        vm.AddTask("C");

        vm.RemoveTask(vm.Tasks[1]); // remove B

        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal("A", vm.Tasks[0].TaskName);
        Assert.Equal("C", vm.Tasks[1].TaskName);
        Assert.Equal(0, vm.Tasks[0].OrderIndex);
        Assert.Equal(1, vm.Tasks[1].OrderIndex);
    }

    [Fact] // REQ-02: reorder via MoveUp swaps and reindexes order_index
    public void MoveUp_swaps_and_reindexes()
    {
        var vm = RequestEditorViewModel.ForCreate(WebTemplate());
        vm.AddTask("A");
        vm.AddTask("B");

        vm.MoveUp(vm.Tasks[1]); // B moves above A

        Assert.Equal("B", vm.Tasks[0].TaskName);
        Assert.Equal("A", vm.Tasks[1].TaskName);
        Assert.Equal(0, vm.Tasks[0].OrderIndex);
        Assert.Equal(1, vm.Tasks[1].OrderIndex);
    }

    [Fact] // REQ-03: edit mode preloads request + existing tasks
    public void Edit_mode_preloads_request_and_existing_tasks()
    {
        var req = new Request(7, "RQ-7", "Billing", DateTimeOffset.UtcNow);
        var existing = new[]
        {
            new TaskItem(11, 7, "Analyse", 0, true),
            new TaskItem(12, 7, "Implement", 1, true),
        };

        var vm = RequestEditorViewModel.ForEdit(req, existing, WebTemplate());

        Assert.True(vm.IsEditMode);
        Assert.Equal(7, vm.EditingRequestId);
        Assert.Equal("RQ-7", vm.RequestCode);
        Assert.Equal("Billing", vm.Project);
        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal(11, vm.Tasks[0].ExistingTaskId);
        Assert.Equal(12, vm.Tasks[1].ExistingTaskId);
    }

    [Fact] // REQ-04: removing an EXISTING task flags IsRemoved (soft-delete), keeps it in Tasks
    public void RemoveTask_existing_flags_removed_not_dropped()
    {
        var req = new Request(7, "RQ-7", "Billing", DateTimeOffset.UtcNow);
        var existing = new[] { new TaskItem(11, 7, "Analyse", 0, true) };
        var vm = RequestEditorViewModel.ForEdit(req, existing, WebTemplate());

        vm.RemoveTask(vm.Tasks[0]);

        Assert.Single(vm.Tasks);              // still present (so Save can SetActiveAsync(false))
        Assert.True(vm.Tasks[0].IsRemoved);
        Assert.Empty(vm.ActiveTasks);         // excluded from the active set
    }

    [Fact] // ActiveTasks excludes removed + reindexes 0..n
    public void ActiveTasks_excludes_removed_and_reindexes()
    {
        var vm = RequestEditorViewModel.ForCreate(WebTemplate());
        vm.AddTask("A");
        vm.AddTask("B");
        vm.AddTask("C");
        vm.RemoveTask(vm.Tasks[0]); // A is new -> dropped

        var active = vm.ActiveTasks;

        Assert.Equal(2, active.Count);
        Assert.Equal("B", active[0].TaskName);
        Assert.Equal(0, active[0].OrderIndex);
        Assert.Equal("C", active[1].TaskName);
        Assert.Equal(1, active[1].OrderIndex);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~RequestEditorViewModelTests`
Expected: FAIL — `RequestEditorViewModel` / `EditableTaskRowVm` do not exist (compile error).

- [ ] **Step 3: Write EditableTaskRowVm**

```csharp
// src/TimesheetApp/ViewModels/EditableTaskRowVm.cs
namespace TimesheetApp.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

public sealed partial class EditableTaskRowVm : ObservableObject
{
    // 0 => brand-new task (INSERT). >0 => existing TaskItem (used for soft-delete on remove).
    public int ExistingTaskId { get; init; }

    [ObservableProperty] private string _taskName = string.Empty;
    [ObservableProperty] private int _orderIndex;
    [ObservableProperty] private bool _isRemoved;

    public static EditableTaskRowVm New(string name, int orderIndex) =>
        new() { ExistingTaskId = 0, TaskName = name, OrderIndex = orderIndex, IsRemoved = false };

    public static EditableTaskRowVm Existing(int taskId, string name, int orderIndex) =>
        new() { ExistingTaskId = taskId, TaskName = name, OrderIndex = orderIndex, IsRemoved = false };
}
```

- [ ] **Step 4: Write RequestEditorViewModel**

```csharp
// src/TimesheetApp/ViewModels/RequestEditorViewModel.cs
namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Models;

public sealed partial class RequestEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<TaskTemplate> _templates;

    private RequestEditorViewModel(IReadOnlyList<TaskTemplate> templates)
    {
        _templates = templates;
        Templates = new ObservableCollection<TaskTemplate>(templates);
        TemplateNames = templates.Select(t => t.TemplateName).Distinct().OrderBy(n => n).ToList();
    }

    public bool IsEditMode { get; private init; }
    public int EditingRequestId { get; private init; }

    [ObservableProperty] private string _requestCode = string.Empty;
    [ObservableProperty] private string _project = string.Empty;
    [ObservableProperty] private string? _selectedTemplateName;

    public ObservableCollection<TaskTemplate> Templates { get; }
    public IReadOnlyList<string> TemplateNames { get; }
    public ObservableCollection<EditableTaskRowVm> Tasks { get; } = new();

    // Active (not-removed) tasks, reindexed 0..n in display order. This is the persist set.
    public IReadOnlyList<EditableTaskRowVm> ActiveTasks
    {
        get
        {
            var active = Tasks.Where(t => !t.IsRemoved).ToList();
            for (var i = 0; i < active.Count; i++) active[i].OrderIndex = i;
            return active;
        }
    }

    public static RequestEditorViewModel ForCreate(IReadOnlyList<TaskTemplate> templates) =>
        new(templates) { IsEditMode = false, EditingRequestId = 0 };

    public static RequestEditorViewModel ForEdit(
        Request request, IReadOnlyList<TaskItem> existingTasks, IReadOnlyList<TaskTemplate> templates)
    {
        var vm = new RequestEditorViewModel(templates)
        {
            IsEditMode = true,
            EditingRequestId = request.Id,
            RequestCode = request.RequestCode,
            Project = request.Project,
        };
        foreach (var t in existingTasks.OrderBy(t => t.OrderIndex))
            vm.Tasks.Add(EditableTaskRowVm.Existing(t.Id, t.TaskName, t.OrderIndex));
        return vm;
    }

    private int NextOrderIndex() => Tasks.Count == 0 ? 0 : Tasks.Max(t => t.OrderIndex) + 1;

    [RelayCommand]
    public void ApplyTemplate()
    {
        if (string.IsNullOrWhiteSpace(SelectedTemplateName)) return;
        var rows = _templates
            .Where(t => t.TemplateName == SelectedTemplateName)
            .OrderBy(t => t.OrderIndex);
        foreach (var r in rows)
            Tasks.Add(EditableTaskRowVm.New(r.TaskName, NextOrderIndex()));
    }

    public void AddTask(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Tasks.Add(EditableTaskRowVm.New(name.Trim(), NextOrderIndex()));
    }

    public void RemoveTask(EditableTaskRowVm row)
    {
        if (row.ExistingTaskId > 0)
        {
            row.IsRemoved = true;           // existing -> soft-delete on save (REQ-04)
        }
        else
        {
            Tasks.Remove(row);              // new -> just drop
            Reindex();
        }
    }

    public void MoveUp(EditableTaskRowVm row)
    {
        var i = Tasks.IndexOf(row);
        if (i <= 0) return;
        Tasks.Move(i, i - 1);
        Reindex();
    }

    public void MoveDown(EditableTaskRowVm row)
    {
        var i = Tasks.IndexOf(row);
        if (i < 0 || i >= Tasks.Count - 1) return;
        Tasks.Move(i, i + 1);
        Reindex();
    }

    private void Reindex()
    {
        for (var i = 0; i < Tasks.Count; i++) Tasks[i].OrderIndex = i;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~RequestEditorViewModelTests`
Expected: PASS (9 tests).

- [ ] **Step 6: Commit**

```bash
git add src/TimesheetApp/ViewModels/EditableTaskRowVm.cs src/TimesheetApp/ViewModels/RequestEditorViewModel.cs src/TimesheetApp.Tests/ViewModels/RequestEditorViewModelTests.cs
git commit -m "feat(p4): RequestEditorViewModel template-gen + reorder + soft-delete-task (REQ-02/03/04)"
```

**done (grep-verifiable):**
- `grep -q "public static RequestEditorViewModel ForCreate" src/TimesheetApp/ViewModels/RequestEditorViewModel.cs`
- `grep -q "public static RequestEditorViewModel ForEdit" src/TimesheetApp/ViewModels/RequestEditorViewModel.cs`
- `grep -q "row.IsRemoved = true" src/TimesheetApp/ViewModels/RequestEditorViewModel.cs`

**verify (automated, <60s):**
- `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~RequestEditorViewModelTests` → 9 passed.

---

## Task 3: RequestsViewModel (list + search + persist create/edit/soft-delete-task)

`<model>sonnet</model>`

**REQ-IDs:** REQ-01 (list + search), REQ-02 (persist create + tasks), REQ-03 (persist edit + add task), REQ-04 (persist task soft-delete; NO request delete).

**read_first:**
- `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md` §3 (IRequestRepository, ITaskRepository signatures), §5 (RequestsViewModel responsibilities).
- `src/TimesheetApp/ViewModels/RequestEditorViewModel.cs` (from Task 2 — `ForCreate`/`ForEdit`/`ActiveTasks`/`EditableTaskRowVm.ExistingTaskId`/`IsRemoved`).
- `src/TimesheetApp/Data/Repositories/ITaskTemplateRepository.cs` (from Task 1).

**Files:**
- Create: `src/TimesheetApp/ViewModels/RequestsViewModel.cs`
- Create: `src/TimesheetApp.Tests/ViewModels/RequestsViewModelTests.cs`

**Public surface (for Task 5 XAML binding — keep exact):**

```csharp
//   ObservableCollection<Request> Requests
//   string? SearchTerm            // [ObservableProperty]; setter triggers RefreshAsync
//   RequestEditorViewModel? Editor // current open editor (null when closed)
//   Task LoadAsync()             // initial load (all requests)
//   Task RefreshAsync()         // re-run SearchAsync(SearchTerm)
//   Task BeginCreateAsync()     // builds Editor in create mode (loads templates)
//   Task BeginEditAsync(int requestId)  // builds Editor in edit mode (loads request+tasks)
//   Task SaveNewAsync()         // insert request + ActiveTasks; refresh; close editor
//   Task SaveEditAsync()        // update request + insert new tasks + SetActiveAsync(false) removed; refresh
```

- [ ] **Step 1: Write the failing tests (mocked repos)**

```csharp
// src/TimesheetApp.Tests/ViewModels/RequestsViewModelTests.cs
using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public sealed class RequestsViewModelTests
{
    private readonly Mock<IRequestRepository> _requests = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<ITaskTemplateRepository> _templates = new();

    private RequestsViewModel CreateVm() =>
        new(_requests.Object, _tasks.Object, _templates.Object);

    private static Request R(int id, string code, string proj) =>
        new(id, code, proj, DateTimeOffset.UtcNow);

    public RequestsViewModelTests()
    {
        _templates.Setup(t => t.GetAllAsync())
            .ReturnsAsync(new[] { new TaskTemplate(1, "Web", "Setup", 0) });
    }

    [Fact] // REQ-01: LoadAsync populates the list with all requests
    public async Task LoadAsync_loads_all_requests()
    {
        _requests.Setup(r => r.SearchAsync(null))
            .ReturnsAsync(new[] { R(1, "RQ-1", "Alpha"), R(2, "RQ-2", "Beta") });
        var vm = CreateVm();

        await vm.LoadAsync();

        Assert.Equal(2, vm.Requests.Count);
    }

    [Fact] // REQ-01: setting SearchTerm re-queries via SearchAsync(term)
    public async Task SearchTerm_filters_via_repository()
    {
        _requests.Setup(r => r.SearchAsync(null)).ReturnsAsync(Array.Empty<Request>());
        _requests.Setup(r => r.SearchAsync("alp")).ReturnsAsync(new[] { R(1, "RQ-1", "Alpha") });
        var vm = CreateVm();
        await vm.LoadAsync();

        vm.SearchTerm = "alp";
        await vm.RefreshAsync();

        Assert.Single(vm.Requests);
        Assert.Equal("RQ-1", vm.Requests[0].RequestCode);
        _requests.Verify(r => r.SearchAsync("alp"), Times.Once);
    }

    [Fact] // REQ-02: SaveNewAsync inserts request then inserts each active task with order_index
    public async Task SaveNewAsync_inserts_request_and_ordered_tasks()
    {
        _requests.Setup(r => r.InsertAsync(It.IsAny<Request>())).ReturnsAsync(42);
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>())).ReturnsAsync(Array.Empty<Request>());
        var vm = CreateVm();
        await vm.BeginCreateAsync();
        vm.Editor!.RequestCode = "RQ-9";
        vm.Editor.Project = "Gamma";
        vm.Editor.AddTask("First");
        vm.Editor.AddTask("Second");

        await vm.SaveNewAsync();

        _requests.Verify(r => r.InsertAsync(
            It.Is<Request>(x => x.RequestCode == "RQ-9" && x.Project == "Gamma")), Times.Once);
        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.RequestId == 42 && x.TaskName == "First" && x.OrderIndex == 0)), Times.Once);
        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.RequestId == 42 && x.TaskName == "Second" && x.OrderIndex == 1)), Times.Once);
        Assert.Null(vm.Editor); // editor closed after save
    }

    [Fact] // REQ-02: applying a template before save inserts the template's tasks
    public async Task SaveNewAsync_with_template_inserts_template_tasks()
    {
        _requests.Setup(r => r.InsertAsync(It.IsAny<Request>())).ReturnsAsync(7);
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>())).ReturnsAsync(Array.Empty<Request>());
        var vm = CreateVm();
        await vm.BeginCreateAsync();
        vm.Editor!.RequestCode = "RQ-T";
        vm.Editor.Project = "Tmpl";
        vm.Editor.SelectedTemplateName = "Web";
        vm.Editor.ApplyTemplate();

        await vm.SaveNewAsync();

        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.RequestId == 7 && x.TaskName == "Setup" && x.OrderIndex == 0)), Times.Once);
        // template-only: exactly one task inserted, no stray rows from other templates
        _tasks.Verify(t => t.InsertAsync(It.IsAny<TaskItem>()), Times.Once);
    }

    [Fact] // REQ-03: SaveEditAsync updates the request and inserts only new tasks
    public async Task SaveEditAsync_updates_request_and_inserts_new_tasks()
    {
        _requests.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(R(5, "RQ-5", "Old"));
        _tasks.Setup(t => t.GetActiveByRequestAsync(5))
            .ReturnsAsync(new[] { new TaskItem(50, 5, "Existing", 0, true) });
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>())).ReturnsAsync(Array.Empty<Request>());
        var vm = CreateVm();
        await vm.BeginEditAsync(5);
        vm.Editor!.Project = "New";
        vm.Editor.AddTask("Added");

        await vm.SaveEditAsync();

        _requests.Verify(r => r.UpdateAsync(
            It.Is<Request>(x => x.Id == 5 && x.Project == "New")), Times.Once);
        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.RequestId == 5 && x.TaskName == "Added")), Times.Once);
        // existing task NOT re-inserted
        _tasks.Verify(t => t.InsertAsync(It.Is<TaskItem>(x => x.TaskName == "Existing")), Times.Never);
    }

    [Fact] // REQ-04: removing an existing task in edit calls SetActiveAsync(false), not request delete
    public async Task SaveEditAsync_soft_deletes_removed_existing_task()
    {
        _requests.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(R(5, "RQ-5", "Old"));
        _tasks.Setup(t => t.GetActiveByRequestAsync(5))
            .ReturnsAsync(new[] { new TaskItem(50, 5, "Existing", 0, true) });
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>())).ReturnsAsync(Array.Empty<Request>());
        var vm = CreateVm();
        await vm.BeginEditAsync(5);
        vm.Editor!.RemoveTask(vm.Editor.Tasks[0]); // flag existing task removed

        await vm.SaveEditAsync();

        _tasks.Verify(t => t.SetActiveAsync(50, false), Times.Once);
    }

    [Fact] // REQ-04: IRequestRepository has no SetActiveAsync — assert the type contract
    public void RequestRepository_has_no_SetActiveAsync()
    {
        Assert.Null(typeof(IRequestRepository).GetMethod("SetActiveAsync"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~RequestsViewModelTests`
Expected: FAIL — `RequestsViewModel` does not exist (compile error).

- [ ] **Step 3: Write RequestsViewModel**

```csharp
// src/TimesheetApp/ViewModels/RequestsViewModel.cs
namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

public sealed partial class RequestsViewModel : ObservableObject
{
    private readonly IRequestRepository _requests;
    private readonly ITaskRepository _tasks;
    private readonly ITaskTemplateRepository _templates;

    public RequestsViewModel(
        IRequestRepository requests, ITaskRepository tasks, ITaskTemplateRepository templates)
    {
        _requests = requests;
        _tasks = tasks;
        _templates = templates;
    }

    public ObservableCollection<Request> Requests { get; } = new();

    [ObservableProperty] private string? _searchTerm;
    [ObservableProperty] private RequestEditorViewModel? _editor;

    // Re-query whenever the search box changes.
    partial void OnSearchTermChanged(string? value) => _ = RefreshAsync();

    public async Task LoadAsync() => await RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var term = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim();
        var rows = await _requests.SearchAsync(term);
        Requests.Clear();
        foreach (var r in rows) Requests.Add(r);
    }

    [RelayCommand]
    public async Task BeginCreateAsync()
    {
        var templates = await _templates.GetAllAsync();
        Editor = RequestEditorViewModel.ForCreate(templates);
    }

    [RelayCommand]
    public async Task BeginEditAsync(int requestId)
    {
        var request = await _requests.GetByIdAsync(requestId);
        if (request is null) return;
        var existing = await _tasks.GetActiveByRequestAsync(requestId);
        var templates = await _templates.GetAllAsync();
        Editor = RequestEditorViewModel.ForEdit(request, existing, templates);
    }

    [RelayCommand]
    public async Task SaveNewAsync()
    {
        if (Editor is null) return;
        var newId = await _requests.InsertAsync(
            new Request(0, Editor.RequestCode.Trim(), Editor.Project.Trim(), DateTimeOffset.UtcNow));

        foreach (var row in Editor.ActiveTasks)
            await _tasks.InsertAsync(new TaskItem(0, newId, row.TaskName.Trim(), row.OrderIndex, true));

        Editor = null;
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task SaveEditAsync()
    {
        if (Editor is null || !Editor.IsEditMode) return;
        var id = Editor.EditingRequestId;

        await _requests.UpdateAsync(
            new Request(id, Editor.RequestCode.Trim(), Editor.Project.Trim(), DateTimeOffset.UtcNow));

        // Soft-delete existing tasks flagged removed (REQ-04 — task only, never the request).
        foreach (var removed in Editor.Tasks.Where(t => t.IsRemoved && t.ExistingTaskId > 0))
            await _tasks.SetActiveAsync(removed.ExistingTaskId, false);

        // Insert brand-new tasks (ExistingTaskId == 0) from the active set.
        foreach (var row in Editor.ActiveTasks.Where(t => t.ExistingTaskId == 0))
            await _tasks.InsertAsync(new TaskItem(0, id, row.TaskName.Trim(), row.OrderIndex, true));

        Editor = null;
        await RefreshAsync();
    }

    [RelayCommand]
    public void CancelEditor() => Editor = null;
}
```

> **Note on `CreatedAt` in `SaveEditAsync`:** `IRequestRepository.UpdateAsync` edits name/project only (spec §3 comment "edit name/project"); the `CreatedAt` passed is ignored by the UPDATE SQL (P1 repo updates `request_code`/`project` columns). Passing `DateTimeOffset.UtcNow` is harmless because the repo's UPDATE does not write `created_at`. `[ASSUMED]` — if the P1 `UpdateAsync` SQL is found to write `created_at`, change this call to fetch-then-preserve. Flag in commit.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~RequestsViewModelTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/TimesheetApp/ViewModels/RequestsViewModel.cs src/TimesheetApp.Tests/ViewModels/RequestsViewModelTests.cs
git commit -m "feat(p4): RequestsViewModel list+search+create+edit+soft-delete-task (REQ-01/02/03/04)"
```

**done (grep-verifiable):**
- `grep -q "await _requests.SearchAsync(term)" src/TimesheetApp/ViewModels/RequestsViewModel.cs`
- `grep -q "_tasks.SetActiveAsync(removed.ExistingTaskId, false)" src/TimesheetApp/ViewModels/RequestsViewModel.cs`
- `! grep -q "SetActiveAsync.*request" src/TimesheetApp/ViewModels/RequestsViewModel.cs` (no request soft-delete)

**verify (automated, <60s):**
- `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~RequestsViewModelTests` → 7 passed.

---

## Task 4: UsersViewModel (list + add + soft-delete)

`<model>sonnet</model>`

**REQ-IDs:** USR-01 (list w/ Active/Inactive), USR-02 (add user), USR-03 (soft-delete user, preserve TimeLogs).

**read_first:**
- `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md` §3 (IUserRepository signatures: `GetAllAsync`, `InsertAsync`, `SetActiveAsync`), §5 (UsersViewModel responsibilities).

**Files:**
- Create: `src/TimesheetApp/ViewModels/UsersViewModel.cs`
- Create: `src/TimesheetApp.Tests/ViewModels/UsersViewModelTests.cs`

**Public surface (for Task 5 XAML binding — keep exact):**

```csharp
//   ObservableCollection<User> Users
//   string NewUserName             // [ObservableProperty]
//   Task LoadAsync()              // GetAllAsync -> Users (active + inactive)
//   Task AddUserAsync()          // insert active user from NewUserName; refresh; clear box
//   Task DeactivateAsync(int userId)  // SetActiveAsync(false); refresh
```

- [ ] **Step 1: Write the failing tests (mocked repo)**

```csharp
// src/TimesheetApp.Tests/ViewModels/UsersViewModelTests.cs
using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public sealed class UsersViewModelTests
{
    private readonly Mock<IUserRepository> _users = new();
    private UsersViewModel CreateVm() => new(_users.Object);

    private static User U(int id, string name, bool active) => new(id, name, null, active);

    [Fact] // USR-01: list shows BOTH active and inactive users
    public async Task LoadAsync_shows_active_and_inactive()
    {
        _users.Setup(u => u.GetAllAsync())
            .ReturnsAsync(new[] { U(1, "Ann", true), U(2, "Bob", false) });
        var vm = CreateVm();

        await vm.LoadAsync();

        Assert.Equal(2, vm.Users.Count);
        Assert.Contains(vm.Users, u => u.Name == "Bob" && !u.IsActive);
    }

    [Fact] // USR-02: adding a user inserts an ACTIVE user and clears the input
    public async Task AddUserAsync_inserts_active_user()
    {
        _users.Setup(u => u.InsertAsync(It.IsAny<User>())).ReturnsAsync(3);
        _users.Setup(u => u.GetAllAsync()).ReturnsAsync(Array.Empty<User>());
        var vm = CreateVm();
        vm.NewUserName = "  Cara  ";

        await vm.AddUserAsync();

        _users.Verify(u => u.InsertAsync(
            It.Is<User>(x => x.Name == "Cara" && x.IsActive && x.WindowsUsername == null)), Times.Once);
        Assert.Equal(string.Empty, vm.NewUserName);
    }

    [Fact] // USR-02: blank name is a no-op (no insert)
    public async Task AddUserAsync_ignores_blank_name()
    {
        var vm = CreateVm();
        vm.NewUserName = "   ";

        await vm.AddUserAsync();

        _users.Verify(u => u.InsertAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact] // USR-03: deactivate calls SetActiveAsync(id,false) and refreshes
    public async Task DeactivateAsync_sets_inactive()
    {
        _users.Setup(u => u.GetAllAsync()).ReturnsAsync(Array.Empty<User>());
        var vm = CreateVm();

        await vm.DeactivateAsync(7);

        _users.Verify(u => u.SetActiveAsync(7, false), Times.Once);
        _users.Verify(u => u.GetAllAsync(), Times.Once); // refreshed after
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~UsersViewModelTests`
Expected: FAIL — `UsersViewModel` does not exist (compile error).

- [ ] **Step 3: Write UsersViewModel**

```csharp
// src/TimesheetApp/ViewModels/UsersViewModel.cs
namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

public sealed partial class UsersViewModel : ObservableObject
{
    private readonly IUserRepository _users;

    public UsersViewModel(IUserRepository users) => _users = users;

    public ObservableCollection<User> Users { get; } = new();

    [ObservableProperty] private string _newUserName = string.Empty;

    public async Task LoadAsync()
    {
        var rows = await _users.GetAllAsync();   // includes inactive (USR-01)
        Users.Clear();
        foreach (var u in rows) Users.Add(u);
    }

    [RelayCommand]
    public async Task AddUserAsync()
    {
        var name = NewUserName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        await _users.InsertAsync(new User(0, name, null, true)); // active (USR-02)
        NewUserName = string.Empty;
        await LoadAsync();
    }

    [RelayCommand]
    public async Task DeactivateAsync(int userId)
    {
        await _users.SetActiveAsync(userId, false);  // soft-delete, TimeLogs preserved (USR-03)
        await LoadAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~UsersViewModelTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/TimesheetApp/ViewModels/UsersViewModel.cs src/TimesheetApp.Tests/ViewModels/UsersViewModelTests.cs
git commit -m "feat(p4): UsersViewModel list+add+soft-delete (USR-01/02/03)"
```

**done (grep-verifiable):**
- `grep -q "await _users.GetAllAsync()" src/TimesheetApp/ViewModels/UsersViewModel.cs`
- `grep -q "_users.SetActiveAsync(userId, false)" src/TimesheetApp/ViewModels/UsersViewModel.cs`
- `grep -q "new User(0, name, null, true)" src/TimesheetApp/ViewModels/UsersViewModel.cs`

**verify (automated, <60s):**
- `dotnet test src/TimesheetApp.Tests --filter FullyQualifiedName~UsersViewModelTests` → 4 passed.

---

## Task 5: RequestsTab.xaml + UsersTab.xaml (MANUAL VERIFY — XAML, no automated test)

`<model>haiku</model>`

> **MANUAL-VERIFY TASK.** XAML cannot be unit-tested headlessly in this project (Tests target bare `net8.0`, no WPF). Verification is by build + the documented manual steps below. No `dotnet test` step.

**REQ-IDs:** REQ-01 (search box + list), REQ-02/REQ-03 (create/edit dialog surface bound to `RequestEditorViewModel`), REQ-04 (remove-task button on rows; NO delete-request button), USR-01 (status column), USR-02 (add box + button), USR-03 (deactivate button).

**read_first:**
- `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md` §1.4 (Views/Tabs folder), §5 (VM surfaces).
- `src/TimesheetApp/ViewModels/RequestsViewModel.cs` (Task 3 surface).
- `src/TimesheetApp/ViewModels/UsersViewModel.cs` (Task 4 surface).
- `src/TimesheetApp/Views/MainWindow.xaml` (existing TabControl host — match its tab pattern).

**Files:**
- Create: `src/TimesheetApp/Views/Tabs/RequestsTab.xaml`
- Create: `src/TimesheetApp/Views/Tabs/RequestsTab.xaml.cs`
- Create: `src/TimesheetApp/Views/Tabs/UsersTab.xaml`
- Create: `src/TimesheetApp/Views/Tabs/UsersTab.xaml.cs`

- [ ] **Step 1: Write RequestsTab.xaml**

```xml
<!-- src/TimesheetApp/Views/Tabs/RequestsTab.xaml -->
<UserControl x:Class="TimesheetApp.Views.Tabs.RequestsTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- REQ-01: search box -->
        <DockPanel Grid.Row="0" Margin="0,0,0,8">
            <Button Content="New Request" Command="{Binding BeginCreateCommand}"
                    DockPanel.Dock="Right" Padding="10,4"/>
            <TextBlock Text="Search:" VerticalAlignment="Center" Margin="0,0,6,0"/>
            <TextBox Text="{Binding SearchTerm, UpdateSourceTrigger=PropertyChanged}"/>
        </DockPanel>

        <!-- REQ-01: request list. NOTE: no delete-request button anywhere (REQ-04). -->
        <DataGrid Grid.Row="1" ItemsSource="{Binding Requests}" AutoGenerateColumns="False"
                  IsReadOnly="True" SelectionMode="Single">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Code" Binding="{Binding RequestCode}" Width="Auto"/>
                <DataGridTextColumn Header="Project" Binding="{Binding Project}" Width="*"/>
                <DataGridTemplateColumn Header="">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <!-- REQ-03: edit; no Request delete affordance (REQ-04) -->
                            <Button Content="Edit"
                                    Command="{Binding DataContext.BeginEditCommand,
                                              RelativeSource={RelativeSource AncestorType=DataGrid}}"
                                    CommandParameter="{Binding Id}"/>
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>
            </DataGrid.Columns>
        </DataGrid>

        <!-- REQ-02/03: editor overlay shown when Editor != null -->
        <Border Grid.RowSpan="2" Background="#80000000"
                Visibility="{Binding Editor, Converter={StaticResource NullToCollapsedConverter}}">
            <Border Background="White" Width="460" Padding="16"
                    HorizontalAlignment="Center" VerticalAlignment="Center">
                <StackPanel DataContext="{Binding Editor}">
                    <TextBlock Text="Request" FontWeight="Bold" FontSize="16"/>
                    <TextBlock Text="Code:" Margin="0,8,0,0"/>
                    <TextBox Text="{Binding RequestCode, UpdateSourceTrigger=PropertyChanged}"/>
                    <TextBlock Text="Project:" Margin="0,6,0,0"/>
                    <TextBox Text="{Binding Project, UpdateSourceTrigger=PropertyChanged}"/>

                    <!-- REQ-02: template picker -->
                    <DockPanel Margin="0,8,0,0">
                        <Button Content="Apply Template" Command="{Binding ApplyTemplateCommand}"
                                DockPanel.Dock="Right"/>
                        <ComboBox ItemsSource="{Binding TemplateNames}"
                                  SelectedItem="{Binding SelectedTemplateName}"/>
                    </DockPanel>

                    <!-- REQ-02: ordered editable task list with reorder + remove -->
                    <ItemsControl ItemsSource="{Binding Tasks}" Margin="0,8,0,0">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <DockPanel Margin="0,2">
                                    <TextBlock Text="(removed)" DockPanel.Dock="Right" Foreground="Red"
                                               Visibility="{Binding IsRemoved,
                                                   Converter={StaticResource BoolToVisibleConverter}}"/>
                                    <Button Content="Remove" DockPanel.Dock="Right"
                                            Command="{Binding DataContext.Editor.RemoveTaskCommand,
                                                RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                            CommandParameter="{Binding}"/>
                                    <Button Content="Down" DockPanel.Dock="Right"
                                            Command="{Binding DataContext.Editor.MoveDownCommand,
                                                RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                            CommandParameter="{Binding}"/>
                                    <Button Content="Up" DockPanel.Dock="Right"
                                            Command="{Binding DataContext.Editor.MoveUpCommand,
                                                RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                            CommandParameter="{Binding}"/>
                                    <TextBox Text="{Binding TaskName, UpdateSourceTrigger=PropertyChanged}"/>
                                </DockPanel>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>

                    <DockPanel Margin="0,8,0,0">
                        <Button Content="Add Task" DockPanel.Dock="Right"
                                Command="{Binding DataContext.AddTaskFromBoxCommand,
                                    RelativeSource={RelativeSource AncestorType=UserControl}}"/>
                        <TextBox x:Name="NewTaskBox"/>
                    </DockPanel>

                    <DockPanel Margin="0,12,0,0">
                        <Button Content="Cancel" DockPanel.Dock="Right"
                                Command="{Binding DataContext.CancelEditorCommand,
                                    RelativeSource={RelativeSource AncestorType=UserControl}}"/>
                        <Button Content="Save" HorizontalAlignment="Left" Padding="16,4"
                                Command="{Binding DataContext.SaveCommand,
                                    RelativeSource={RelativeSource AncestorType=UserControl}}"/>
                    </DockPanel>
                </StackPanel>
            </Border>
        </Border>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Write RequestsTab.xaml.cs**

> The Save button routes to create vs edit based on `Editor.IsEditMode`; AddTask reads the `NewTaskBox` text (the `AddTask(string)` method is not a `[RelayCommand]`). These two glue commands live in code-behind to keep the VM dialog-free per spec §5.

```csharp
// src/TimesheetApp/Views/Tabs/RequestsTab.xaml.cs
namespace TimesheetApp.Views.Tabs;

using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.ViewModels;

public partial class RequestsTab : UserControl
{
    public RequestsTab()
    {
        InitializeComponent();
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        AddTaskFromBoxCommand = new RelayCommand(AddTaskFromBox);
    }

    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand AddTaskFromBoxCommand { get; }

    private RequestsViewModel? Vm => DataContext as RequestsViewModel;

    private async System.Threading.Tasks.Task SaveAsync()
    {
        if (Vm?.Editor is null) return;
        if (Vm.Editor.IsEditMode) await Vm.SaveEditAsync();
        else await Vm.SaveNewAsync();
    }

    private void AddTaskFromBox()
    {
        if (Vm?.Editor is null) return;
        Vm.Editor.AddTask(NewTaskBox.Text);
        NewTaskBox.Clear();
    }
}
```

> **`[ASSUMED]` binding note:** `SaveCommand`/`AddTaskFromBoxCommand` are on the code-behind (the `UserControl`), reached via `RelativeSource AncestorType=UserControl`. The converters `NullToCollapsedConverter`, `BoolToVisibleConverter` are expected to exist as app-level resources (added in an earlier phase or as a trivial converter file). If they are absent, the implementer adds a `Converters/` file with the two `IValueConverter`s and registers them in `App.xaml` `<Application.Resources>`. Flag in commit.

- [ ] **Step 3: Write UsersTab.xaml**

```xml
<!-- src/TimesheetApp/Views/Tabs/UsersTab.xaml -->
<UserControl x:Class="TimesheetApp.Views.Tabs.UsersTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- USR-02: add user -->
        <DockPanel Grid.Row="0" Margin="0,0,0,8">
            <Button Content="Add User" Command="{Binding AddUserCommand}"
                    DockPanel.Dock="Right" Padding="10,4"/>
            <TextBox Text="{Binding NewUserName, UpdateSourceTrigger=PropertyChanged}"/>
        </DockPanel>

        <!-- USR-01: list with Active/Inactive status -->
        <DataGrid Grid.Row="1" ItemsSource="{Binding Users}" AutoGenerateColumns="False"
                  IsReadOnly="True" SelectionMode="Single">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Name" Binding="{Binding Name}" Width="*"/>
                <DataGridTextColumn Header="Status"
                    Binding="{Binding IsActive, Converter={StaticResource ActiveStatusConverter}}"
                    Width="Auto"/>
                <DataGridTemplateColumn Header="">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <!-- USR-03: deactivate (only shown for active users) -->
                            <Button Content="Deactivate"
                                    IsEnabled="{Binding IsActive}"
                                    Command="{Binding DataContext.DeactivateCommand,
                                              RelativeSource={RelativeSource AncestorType=DataGrid}}"
                                    CommandParameter="{Binding Id}"/>
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>
            </DataGrid.Columns>
        </DataGrid>
    </Grid>
</UserControl>
```

- [ ] **Step 4: Write UsersTab.xaml.cs**

```csharp
// src/TimesheetApp/Views/Tabs/UsersTab.xaml.cs
namespace TimesheetApp.Views.Tabs;

using System.Windows.Controls;

public partial class UsersTab : UserControl
{
    public UsersTab() => InitializeComponent();
}
```

> **`[ASSUMED]`:** `ActiveStatusConverter` (bool → "Active"/"Inactive") is an app-level resource; add it alongside the other converters if absent.

- [ ] **Step 5: Build to verify XAML compiles + bindings resolve**

Run: `dotnet build src/TimesheetApp/TimesheetApp.csproj`
Expected: Build succeeded, 0 errors. (XAML binding typos that break compilation surface here.)

- [ ] **Step 6: MANUAL VERIFY — run the app**

Run: `dotnet run --project src/TimesheetApp/TimesheetApp.csproj`
Then confirm by hand:
1. **REQ-01:** Requests tab lists requests; typing in Search narrows the list live (matches code or project substring).
2. **REQ-02:** "New Request" opens the editor; pick a template → tasks appear ordered; "Add Task" appends; Up/Down reorder; "Remove" on a new task drops it; Save → request + tasks appear in the list.
3. **REQ-03:** "Edit" preloads code/project + existing tasks; change project, add a task, Save → persists.
4. **REQ-04:** In edit, "Remove" on an existing task marks it `(removed)`; Save → it vanishes from the grid next open (soft-deleted). Confirm there is **NO** delete/remove affordance on a Request row itself.
5. **USR-01:** Users tab shows all users with Active/Inactive status.
6. **USR-02:** Type a name, "Add User" → user appears as Active; input clears.
7. **USR-03:** "Deactivate" on an active user → status flips to Inactive; the Deactivate button disables for inactive rows.

- [ ] **Step 7: Commit**

```bash
git add src/TimesheetApp/Views/Tabs/RequestsTab.xaml src/TimesheetApp/Views/Tabs/RequestsTab.xaml.cs src/TimesheetApp/Views/Tabs/UsersTab.xaml src/TimesheetApp/Views/Tabs/UsersTab.xaml.cs
git commit -m "feat(p4): RequestsTab + UsersTab XAML views (REQ-01..04, USR-01..03) [manual-verify]"
```

**done (grep-verifiable):**
- `grep -q "Binding SearchTerm" src/TimesheetApp/Views/Tabs/RequestsTab.xaml`
- `grep -q "Binding NewUserName" src/TimesheetApp/Views/Tabs/UsersTab.xaml`
- `! grep -qi "delete.*request\|remove.*request" src/TimesheetApp/Views/Tabs/RequestsTab.xaml` (no request-delete affordance — REQ-04)

**verify (build only — XAML is manual-verify, no <60s test exists):**
- `dotnet build src/TimesheetApp/TimesheetApp.csproj` → Build succeeded.

---

## REQ Coverage Check

| REQ | Statement | Task(s) | Test evidence |
|---|---|---|---|
| REQ-01 | request list + search by code/project | T3, T5 | `LoadAsync_loads_all_requests`, `SearchTerm_filters_via_repository` |
| REQ-02 | create w/ optional template + add/remove/reorder | T2, T3, T5 | `ApplyTemplate_*`, `AddTask_*`, `MoveUp_*`, `SaveNewAsync_*` |
| REQ-03 | edit name + add task | T2, T3, T5 | `Edit_mode_preloads_*`, `SaveEditAsync_updates_*` |
| REQ-04 | soft-delete TASK only; Request NOT deletable | T2, T3, T5 | `RemoveTask_existing_flags_removed_*`, `SaveEditAsync_soft_deletes_*`, `RequestRepository_has_no_SetActiveAsync` |
| USR-01 | user list + Active/Inactive | T4, T5 | `LoadAsync_shows_active_and_inactive` |
| USR-02 | add user (active) | T4, T5 | `AddUserAsync_inserts_active_user`, `AddUserAsync_ignores_blank_name` |
| USR-03 | soft-delete user, preserve TimeLogs | T4, T5 | `DeactivateAsync_sets_inactive` |

All 7 P4 REQs covered. No orphan REQ; no fabricated REQ outside scope.

---

## Self-Review

1. **Spec coverage:** Every P4 REQ maps to ≥1 task (table above). Repo signatures used (`SearchAsync`, `InsertAsync`, `UpdateAsync`, `SetActiveAsync`, `GetActiveByRequestAsync`, `GetAllAsync`) all exist verbatim in spec §3. `IRequestRepository.SetActiveAsync` is asserted absent (REQ-04). ✔
2. **Placeholder scan:** No TBD/TODO; every code step shows full code; converters/glue flagged `[ASSUMED]` with the exact fallback action. ✔
3. **Type consistency:** `RequestEditorViewModel.ForCreate/ForEdit/ActiveTasks/Tasks`, `EditableTaskRowVm.ExistingTaskId/IsRemoved/OrderIndex`, `RequestsViewModel.Editor/SaveNewAsync/SaveEditAsync` are used identically in Tasks 2→3→5. `User`/`Request`/`TaskItem`/`TaskTemplate` ctor shapes match spec §2 verbatim. ✔

---

## Task Board

| Task | Wave | Owner agent | Model (base→quality-shift) | Files | REQ |
|---|---|---|---|---|---|
| T1 ITaskTemplateRepository + DI | 1 | implementer-dotnet-csharp | sonnet→opus | ITaskTemplateRepository.cs, TaskTemplateRepository.cs, App.xaml.cs, +test | REQ-02 |
| T2 RequestEditorViewModel | 2 | implementer-dotnet-csharp | sonnet→opus | RequestEditorViewModel.cs, EditableTaskRowVm.cs, +test | REQ-02/03/04 |
| T4 UsersViewModel | 2 | implementer-dotnet-csharp | sonnet→opus | UsersViewModel.cs, +test | USR-01/02/03 |
| T3 RequestsViewModel | 3 | implementer-dotnet-csharp | sonnet→opus | RequestsViewModel.cs, +test | REQ-01/02/03/04 |
| T5 RequestsTab + UsersTab XAML | 4 | implementer-dotnet-csharp | haiku→sonnet | RequestsTab.xaml(.cs), UsersTab.xaml(.cs) | all 7 (UI) |

> Quality-profile shift (config.json `model_profile: "quality"`) applied at dispatch: sonnet→opus, haiku→sonnet.
