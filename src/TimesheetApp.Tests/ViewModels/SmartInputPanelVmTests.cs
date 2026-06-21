// src/TimesheetApp.Tests/ViewModels/SmartInputPanelVmTests.cs
using Moq;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public class SmartInputPanelVmTests
{
    private static readonly DateOnly From = new(2026, 6, 15); // Mon
    private static readonly DateOnly To = new(2026, 6, 17); // Wed

    private static (SmartInputPanelVm vm, Mock<ISmartInputService> si, Mock<ITimeLogService> tl) Make(int userId = 1)
    {
        var si = new Mock<ISmartInputService>();
        var tl = new Mock<ITimeLogService>();
        var vm = new SmartInputPanelVm(si.Object, tl.Object, () => userId)
        {
            TaskId = 7,
            From = From,
            To = To,
            TotalHours = 9m,
            Mode = SmartInputMode.DistributeEven
        };
        return (vm, si, tl);
    }

    private static IReadOnlyList<CellAssignment> ThreeCells() => new[]
    {
        new CellAssignment(From, 3m),
        new CellAssignment(From.AddDays(1), 3m),
        new CellAssignment(To, 3m),
    };

    [Fact]
    public async Task BuildPreview_DistributeEven_PopulatesPreviewCells()
    {
        var (vm, si, tl) = Make();
        si.Setup(s => s.DistributeEven(From, To, 9m))
          .Returns(new SmartInputResult(true, ThreeCells(), null));
        tl.Setup(t => t.ValidateDayTotalsAsync(1, It.IsAny<IReadOnlyList<CellAssignment>>(), 7))
          .ReturnsAsync(new SaveResult(true, null));

        await vm.BuildPreviewCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.PreviewCells.Count);
        Assert.Null(vm.PreviewError);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task BuildPreview_Full8h_UsesFillFull8h()
    {
        var (vm, si, tl) = Make();
        vm.Mode = SmartInputMode.FillFull8h;
        si.Setup(s => s.FillFull8h(From, To))
          .Returns(new SmartInputResult(true, ThreeCells(), null));
        tl.Setup(t => t.ValidateDayTotalsAsync(1, It.IsAny<IReadOnlyList<CellAssignment>>(), 7))
          .ReturnsAsync(new SaveResult(true, null));

        await vm.BuildPreviewCommand.ExecuteAsync(null);

        si.Verify(s => s.FillFull8h(From, To), Times.Once);
        si.Verify(s => s.DistributeEven(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task BuildPreview_MathNoOp_SurfacesErrorAndBlocksApply()
    {
        var (vm, si, tl) = Make();
        si.Setup(s => s.DistributeEven(From, To, 9m))
          .Returns(new SmartInputResult(false, Array.Empty<CellAssignment>(), "no working days"));

        await vm.BuildPreviewCommand.ExecuteAsync(null);

        Assert.Equal("no working days", vm.PreviewError);
        Assert.False(vm.CanApply);
        tl.Verify(t => t.ValidateDayTotalsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<CellAssignment>>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task BuildPreview_DayTotalOverEight_BlocksApplyWithMessage()
    {
        var (vm, si, tl) = Make();
        si.Setup(s => s.DistributeEven(From, To, 9m))
          .Returns(new SmartInputResult(true, ThreeCells(), null));
        tl.Setup(t => t.ValidateDayTotalsAsync(1, It.IsAny<IReadOnlyList<CellAssignment>>(), 7))
          .ReturnsAsync(new SaveResult(false, "Mon exceeds 8h"));

        await vm.BuildPreviewCommand.ExecuteAsync(null);

        Assert.Equal("Mon exceeds 8h", vm.PreviewError);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task Apply_CommitsValidatedCellsAtomically_ThenRaisesApplied()
    {
        var (vm, si, tl) = Make();
        si.Setup(s => s.DistributeEven(From, To, 9m))
          .Returns(new SmartInputResult(true, ThreeCells(), null));
        tl.Setup(t => t.ValidateDayTotalsAsync(1, It.IsAny<IReadOnlyList<CellAssignment>>(), 7))
          .ReturnsAsync(new SaveResult(true, null));
        tl.Setup(t => t.ApplySmartInputAsync(1, 7, It.IsAny<IReadOnlyList<CellAssignment>>()))
          .ReturnsAsync(new SaveResult(true, null));
        var applied = 0;
        vm.Applied += () => applied++;

        await vm.BuildPreviewCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        tl.Verify(t => t.ApplySmartInputAsync(1, 7, It.Is<IReadOnlyList<CellAssignment>>(c => c.Count == 3)), Times.Once);
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task Apply_WithoutValidatedPreview_DoesNothing()
    {
        var (vm, si, tl) = Make();
        // No BuildPreview run -> CanApply false -> ApplyCommand guarded.
        await vm.ApplyCommand.ExecuteAsync(null);
        tl.Verify(t => t.ApplySmartInputAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<CellAssignment>>()), Times.Never);
    }

    [Fact]
    public void ModeCommands_SwitchMode()
    {
        var (vm, _, _) = Make();
        vm.SetFull8hCommand.Execute(null);
        Assert.Equal(SmartInputMode.FillFull8h, vm.Mode);
        vm.SetDistributeEvenCommand.Execute(null);
        Assert.Equal(SmartInputMode.DistributeEven, vm.Mode);
    }
}
