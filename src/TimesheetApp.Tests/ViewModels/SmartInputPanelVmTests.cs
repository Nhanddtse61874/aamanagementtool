// src/TimesheetApp.Tests/ViewModels/SmartInputPanelVmTests.cs
using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public class SmartInputPanelVmTests
{
    private static readonly DateOnly From = new(2026, 6, 15); // Mon
    private static readonly DateOnly To = new(2026, 6, 17);   // Wed (3 working days)

    private static (SmartInputPanelVm vm, Mock<ITimeLogService> tl,
                    Mock<IBacklogRepository> req, Mock<ITaskRepository> tasks) Make(int userId = 1)
    {
        var tl = new Mock<ITimeLogService>();
        var req = new Mock<IBacklogRepository>();
        var tasks = new Mock<ITaskRepository>();
        var vm = new SmartInputPanelVm(tl.Object, req.Object, tasks.Object, () => userId)
        {
            From = From,
            To = To,
            TotalHours = 9m,
            Mode = SmartInputMode.DistributeEven,
        };
        return (vm, tl, req, tasks);
    }

    // Find request "REQ-1" -> two tasks (ids 7, 8).
    private static async Task FindTwoTasks(SmartInputPanelVm vm, Mock<IBacklogRepository> req, Mock<ITaskRepository> tasks)
    {
        req.Setup(r => r.SearchAsync("REQ-1"))
           .ReturnsAsync(new[] { new Backlog(5, "REQ-1", "ARCS", DateTimeOffset.UtcNow) });
        tasks.Setup(t => t.GetActiveByBacklogAsync(5))
             .ReturnsAsync(new[] { new TaskItem(7, 5, "Design", 0, true), new TaskItem(8, 5, "Impl", 1, true) });
        vm.BacklogCode = "REQ-1";
        await vm.FindBacklogCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task FindRequest_loads_active_tasks_as_checkboxes()
    {
        var (vm, _, req, tasks) = Make();
        await FindTwoTasks(vm, req, tasks);

        Assert.Equal(2, vm.Tasks.Count);
        Assert.Equal("Design", vm.Tasks[0].TaskName);
        Assert.Null(vm.LoadError);
    }

    [Fact]
    public async Task FindRequest_unknown_code_surfaces_error_and_no_tasks()
    {
        var (vm, _, req, _) = Make();
        req.Setup(r => r.SearchAsync("NOPE")).ReturnsAsync(System.Array.Empty<Backlog>());
        vm.BacklogCode = "NOPE";

        await vm.FindBacklogCommand.ExecuteAsync(null);

        Assert.Empty(vm.Tasks);
        Assert.False(string.IsNullOrEmpty(vm.LoadError));
    }

    [Fact]
    public async Task BuildPreview_no_task_checked_blocks_with_message()
    {
        var (vm, tl, req, tasks) = Make();
        await FindTwoTasks(vm, req, tasks); // none checked

        await vm.BuildPreviewCommand.ExecuteAsync(null);

        Assert.False(vm.CanApply);
        Assert.False(string.IsNullOrEmpty(vm.PreviewError));
        tl.Verify(t => t.ValidateSmartFillAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<SmartFillTask>>()), Times.Never);
    }

    [Fact] // 1 task, 9h over Mon–Wed -> 3 cells of 3.0h; validation passes -> CanApply.
    public async Task BuildPreview_distributes_total_across_checked_task_and_days()
    {
        var (vm, tl, req, tasks) = Make();
        await FindTwoTasks(vm, req, tasks);
        vm.Tasks[0].IsChecked = true;
        tl.Setup(t => t.ValidateSmartFillAsync(1, It.IsAny<IReadOnlyList<SmartFillTask>>()))
          .ReturnsAsync(new SaveResult(true, null));

        await vm.BuildPreviewCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.PreviewCells.Count);
        Assert.All(vm.PreviewCells, c => Assert.Equal(3.0m, c.Hours));
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task BuildPreview_validation_failure_blocks_apply()
    {
        var (vm, tl, req, tasks) = Make();
        await FindTwoTasks(vm, req, tasks);
        vm.Tasks[0].IsChecked = true;
        tl.Setup(t => t.ValidateSmartFillAsync(1, It.IsAny<IReadOnlyList<SmartFillTask>>()))
          .ReturnsAsync(new SaveResult(false, "Mon exceeds 8h"));

        await vm.BuildPreviewCommand.ExecuteAsync(null);

        Assert.Equal("Mon exceeds 8h", vm.PreviewError);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task Apply_commits_validated_plan_then_raises_Applied()
    {
        var (vm, tl, req, tasks) = Make();
        await FindTwoTasks(vm, req, tasks);
        vm.Tasks[0].IsChecked = true;
        tl.Setup(t => t.ValidateSmartFillAsync(1, It.IsAny<IReadOnlyList<SmartFillTask>>()))
          .ReturnsAsync(new SaveResult(true, null));
        tl.Setup(t => t.ApplySmartFillAsync(1, It.IsAny<IReadOnlyList<SmartFillTask>>()))
          .ReturnsAsync(new SaveResult(true, null));
        var applied = 0;
        vm.Applied += () => applied++;

        await vm.BuildPreviewCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        tl.Verify(t => t.ApplySmartFillAsync(1,
            It.Is<IReadOnlyList<SmartFillTask>>(p => p.Count == 1 && p[0].TaskId == 7)), Times.Once);
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task Apply_without_validated_preview_does_nothing()
    {
        var (vm, tl, _, _) = Make();
        await vm.ApplyCommand.ExecuteAsync(null); // CanApply false
        tl.Verify(t => t.ApplySmartFillAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<SmartFillTask>>()), Times.Never);
    }

    [Fact]
    public void ModeCommands_switch_mode()
    {
        var (vm, _, _, _) = Make();
        vm.SetFull8hCommand.Execute(null);
        Assert.Equal(SmartInputMode.FillFull8h, vm.Mode);
        vm.SetDistributeEvenCommand.Execute(null);
        Assert.Equal(SmartInputMode.DistributeEven, vm.Mode);
    }
}
