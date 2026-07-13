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
        // M8.2: the panel no longer owns the fill math — it calls the real (holiday-aware) service, so
        // these tests now exercise the one implementation rather than a ViewModel-local copy of it.
        var vm = new SmartInputPanelVm(tl.Object, req.Object, tasks.Object, new SmartInputService(), () => userId)
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
        req.Setup(r => r.SearchAsync("REQ-1", It.IsAny<IReadOnlyList<int>?>()))
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
    public async Task FindBacklog_scopes_search_to_active_team()  // TM-06: no cross-team leak into smart-fill
    {
        var tl = new Mock<ITimeLogService>();
        var req = new Mock<IBacklogRepository>();
        var tasks = new Mock<ITaskRepository>();
        req.Setup(r => r.SearchAsync("REQ-1", It.IsAny<IReadOnlyList<int>?>()))
           .ReturnsAsync(new[] { new Backlog(5, "REQ-1", "ARCS", DateTimeOffset.UtcNow) });
        tasks.Setup(t => t.GetActiveByBacklogAsync(5))
             .ReturnsAsync(new[] { new TaskItem(7, 5, "Design", 0, true) });
        var vm = new SmartInputPanelVm(
            tl.Object, req.Object, tasks.Object, new SmartInputService(), () => 1, currentTeamId: () => 42);
        vm.BacklogCode = "REQ-1";

        await vm.FindBacklogCommand.ExecuteAsync(null);

        // The active team (42) — not null (all teams) — is the scope passed to the backlog search.
        req.Verify(r => r.SearchAsync("REQ-1",
            It.Is<IReadOnlyList<int>?>(ts => ts != null && ts.Single() == 42)), Times.Once);
    }

    [Fact]
    public async Task FindRequest_unknown_code_surfaces_error_and_no_tasks()
    {
        var (vm, _, req, _) = Make();
        req.Setup(r => r.SearchAsync("NOPE", It.IsAny<IReadOnlyList<int>?>())).ReturnsAsync(System.Array.Empty<Backlog>());
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

    // M8.2 bug 1: the preview and the validator used to disagree about holidays. BuildPlan enumerated
    // working days WITHOUT excluding them while ValidateSmartFillAsync excluded them, so a range spanning
    // a holiday rendered a cell on it, then failed validation and blocked Apply — with no way for the user
    // to act on the error, since they never typed in that cell. Both sides now use the same day set.
    [Fact]
    public async Task BuildPreview_over_a_holiday_previews_only_days_the_validator_accepts()
    {
        var tue = new DateOnly(2026, 6, 16);   // inside From..To (Mon 15 -> Wed 17)
        var tl = new Mock<ITimeLogService>();
        var req = new Mock<IBacklogRepository>();
        var tasks = new Mock<ITaskRepository>();
        var holidays = new Mock<IHolidayRepository>();
        holidays.Setup(h => h.GetAllAsync()).ReturnsAsync(new[] { new Holiday(tue, "Public holiday") });

        // Stands in for the real TimeLogService.ValidateSmartFillAsync, which rejects ANY cell landing on
        // a holiday (HOL-02). This is precisely the rule the old preview contradicted.
        tl.Setup(t => t.ValidateSmartFillAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<SmartFillTask>>()))
          .ReturnsAsync((int _, IReadOnlyList<SmartFillTask> plan) =>
              plan.SelectMany(p => p.Cells).Any(c => c.Date == tue)
                  ? new SaveResult(false, $"{tue:yyyy-MM-dd} is a holiday.")
                  : new SaveResult(true, null));

        var vm = new SmartInputPanelVm(
            tl.Object, req.Object, tasks.Object, new SmartInputService(), () => 1, holidays.Object)
        {
            From = From, To = To, TotalHours = 9m, Mode = SmartInputMode.DistributeEven,
        };
        await FindTwoTasks(vm, req, tasks);
        vm.Tasks[0].IsChecked = true;

        await vm.BuildPreviewCommand.ExecuteAsync(null);

        // The holiday is never previewed ...
        Assert.DoesNotContain(vm.PreviewCells, c => c.Date == tue);
        Assert.Equal(2, vm.PreviewCells.Count);                  // Mon + Wed
        Assert.Equal(9m, vm.PreviewCells.Sum(c => c.Hours));     // and all 9h are still placed
        // ... so validation passes and Apply is reachable. Before the fix this preview blocked Apply.
        Assert.Null(vm.PreviewError);
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
