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
            It.Is<Request>(x => x.Id == 5 && x.Project == "New"),
            It.IsAny<int?>(), It.IsAny<string?>()), Times.Once);
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
