using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public sealed class BacklogsViewModelTests
{
    private readonly Mock<IBacklogRepository> _requests = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<ITaskTemplateRepository> _templates = new();

    private BacklogsViewModel CreateVm() =>
        new(_requests.Object, _tasks.Object, _templates.Object);

    private static Backlog R(int id, string code, string proj) =>
        new(id, code, proj, DateTimeOffset.UtcNow);

    public BacklogsViewModelTests()
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

        Assert.Equal(2, vm.Backlogs.Count);
    }

    [Fact] // REQ-01: search filters the loaded list live (in-memory), by code or project
    public async Task SearchTerm_filters_loaded_list_live()
    {
        _requests.Setup(r => r.SearchAsync(null))
            .ReturnsAsync(new[] { R(1, "RQ-1", "Alpha"), R(2, "RQ-2", "Beta") });
        var vm = CreateVm();
        await vm.LoadAsync();
        Assert.Equal(2, vm.Backlogs.Count);

        vm.SearchTerm = "alp"; // live — no second DB query

        Assert.Single(vm.Backlogs);
        Assert.Equal("RQ-1", vm.Backlogs[0].BacklogCode);
        _requests.Verify(r => r.SearchAsync(It.IsAny<string?>()), Times.Once); // loaded once
    }

    [Fact] // Structured filters (project/status/assignee/month) combine with the search, all in-memory.
    public async Task Filters_combine_in_memory()
    {
        _requests.Setup(r => r.SearchAsync(null)).ReturnsAsync(new[]
        {
            new Backlog(1, "RQ-1", "ARCS", DateTimeOffset.UtcNow, Type: "Implement"),
            new Backlog(2, "RQ-2", "ARMS", DateTimeOffset.UtcNow, Type: "Implement"),
            new Backlog(3, "RQ-3", "ARCS", DateTimeOffset.UtcNow, Type: "Estimate"),
        });
        var vm = CreateVm();
        await vm.LoadAsync();

        vm.FilterProject = "ARCS";          // RQ-1, RQ-3
        Assert.Equal(2, vm.Backlogs.Count);
        vm.FilterType = "Implement";      // RQ-1 only
        Assert.Single(vm.Backlogs);
        Assert.Equal("RQ-1", vm.Backlogs[0].BacklogCode);
    }

    [Fact] // REQ-02: SaveNewAsync inserts request then inserts each active task with order_index
    public async Task SaveNewAsync_inserts_request_and_ordered_tasks()
    {
        _requests.Setup(r => r.InsertAsync(It.IsAny<Backlog>())).ReturnsAsync(42);
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>())).ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVm();
        await vm.BeginCreateAsync();
        vm.Editor!.BacklogCode = "RQ-9";
        vm.Editor.Project = "Gamma";
        vm.Editor.AddTask("First");
        vm.Editor.AddTask("Second");

        await vm.SaveNewAsync();

        _requests.Verify(r => r.InsertAsync(
            It.Is<Backlog>(x => x.BacklogCode == "RQ-9" && x.Project == "Gamma")), Times.Once);
        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.BacklogId == 42 && x.TaskName == "First" && x.OrderIndex == 0)), Times.Once);
        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.BacklogId == 42 && x.TaskName == "Second" && x.OrderIndex == 1)), Times.Once);
        Assert.Null(vm.Editor); // editor closed after save
    }

    [Fact] // A new request with no tasks is rejected: editor stays open with an error, nothing inserted.
    public async Task SaveNewAsync_requires_at_least_one_task()
    {
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>())).ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVm();
        await vm.BeginCreateAsync();
        vm.Editor!.BacklogCode = "RQ-EMPTY";
        vm.Editor.Project = "ARCS";
        // no tasks added

        await vm.SaveNewAsync();

        _requests.Verify(r => r.InsertAsync(It.IsAny<Backlog>()), Times.Never);
        Assert.NotNull(vm.Editor);                       // editor stays open
        Assert.False(string.IsNullOrEmpty(vm.Editor!.ErrorMessage));
    }

    [Fact] // REQ-02: applying a template before save inserts the template's tasks
    public async Task SaveNewAsync_with_template_inserts_template_tasks()
    {
        _requests.Setup(r => r.InsertAsync(It.IsAny<Backlog>())).ReturnsAsync(7);
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>())).ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVm();
        await vm.BeginCreateAsync();
        vm.Editor!.BacklogCode = "RQ-T";
        vm.Editor.Project = "Tmpl";
        vm.Editor.SelectedTemplateName = "Web";
        vm.Editor.ApplyTemplate();

        await vm.SaveNewAsync();

        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.BacklogId == 7 && x.TaskName == "Setup" && x.OrderIndex == 0)), Times.Once);
        // template-only: exactly one task inserted, no stray rows from other templates
        _tasks.Verify(t => t.InsertAsync(It.IsAny<TaskItem>()), Times.Once);
    }

    [Fact] // REQ-03: SaveEditAsync updates the request and inserts only new tasks
    public async Task SaveEditAsync_updates_request_and_inserts_new_tasks()
    {
        _requests.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(R(5, "RQ-5", "Old"));
        _tasks.Setup(t => t.GetActiveByBacklogAsync(5))
            .ReturnsAsync(new[] { new TaskItem(50, 5, "Existing", 0, true) });
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>())).ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVm();
        await vm.BeginEditAsync(5);
        vm.Editor!.Project = "New";
        vm.Editor.AddTask("Added");

        await vm.SaveEditAsync();

        _requests.Verify(r => r.UpdateAsync(
            It.Is<Backlog>(x => x.Id == 5 && x.Project == "New"),
            It.IsAny<int?>(), It.IsAny<string?>()), Times.Once);
        _tasks.Verify(t => t.InsertAsync(
            It.Is<TaskItem>(x => x.BacklogId == 5 && x.TaskName == "Added")), Times.Once);
        // existing task NOT re-inserted
        _tasks.Verify(t => t.InsertAsync(It.Is<TaskItem>(x => x.TaskName == "Existing")), Times.Never);
    }

    [Fact] // REQ-04: removing an existing task in edit calls SetActiveAsync(false), not request delete
    public async Task SaveEditAsync_soft_deletes_removed_existing_task()
    {
        _requests.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(R(5, "RQ-5", "Old"));
        _tasks.Setup(t => t.GetActiveByBacklogAsync(5))
            .ReturnsAsync(new[] { new TaskItem(50, 5, "Existing", 0, true) });
        _requests.Setup(r => r.SearchAsync(It.IsAny<string?>())).ReturnsAsync(Array.Empty<Backlog>());
        var vm = CreateVm();
        await vm.BeginEditAsync(5);
        vm.Editor!.RemoveTask(vm.Editor.Tasks[0]); // flag existing task removed

        await vm.SaveEditAsync();

        _tasks.Verify(t => t.SetActiveAsync(50, false), Times.Once);
    }

    [Fact] // REQ-04: IBacklogRepository has no SetActiveAsync — assert the type contract
    public void BacklogRepository_has_no_SetActiveAsync()
    {
        Assert.Null(typeof(IBacklogRepository).GetMethod("SetActiveAsync"));
    }
}
