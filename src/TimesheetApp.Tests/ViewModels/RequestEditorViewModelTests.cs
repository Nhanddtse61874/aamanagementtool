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
