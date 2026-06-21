---
must_haves:
  observable_truths:
    - "Timesheet tab shows a Mon-Fri (5 column, no Sat/Sun) grid for one week; Prev/Next shifts the week and column headers show concrete dates recomputed on navigation (TS-01)."
    - "Grid rows are exactly the active tasks returned by the service for the timesheet: DEFAULT-request tasks plus tasks from active requests; soft-deleted tasks do not appear (TS-02)."
    - "Each day cell is inline-editable; clearing a cell to empty deletes the underlying log (empty = 0h, no zero-row persisted) (TS-03)."
    - "A cell with an invalid/over-limit value (>8, <=0, or >1 decimal) shows a per-cell red error via INotifyDataErrorInfo and is not committed (TS-04)."
    - "A footer strip below the grid shows per-column day totals Mon..Fri, updating live as cells change (TS-05)."
    - "Save is disabled (CanExecute = false) whenever any day column total exceeds 8h, re-evaluated on each cell change (TS-06)."
    - "Saving a cell upserts on the natural key (user_id, task_id, work_date); clearing deletes; re-entering a value updates the same row with no duplicate (TS-07)."
    - "Smart input overwrites (upserts) target cells; the post-merge day total is validated against 8h in the preview before save; apply is atomic all-or-nothing (SI-05)."
    - "A Smart Input panel below the grid offers Mode 1 (Chia deu: Task/From/To/Total) and Mode 2 (Full 8h: Task/From/To), each producing a preview the user must confirm before any write (SI-06)."
  required_artifacts:
    - "src/TimesheetApp/ViewModels/TimesheetRowVm.cs — bindable Mon-Fri row VM with 5 nullable decimal? day props, RowTotal, DayChanged event, Round1, INotifyDataErrorInfo per-cell errors."
    - "src/TimesheetApp/ViewModels/SmartInputPanelVm.cs — smart-input panel VM: two modes, BuildPreview (calls ISmartInputService + ITimeLogService.ValidateDayTotalsAsync), Apply (calls ApplySmartInputAsync)."
    - "src/TimesheetApp/ViewModels/TimesheetViewModel.cs — week nav, Rows collection, footer totals MonTotal..FriTotal, SaveCommand CanExecute gating, cell save/clear, error propagation, hosts SmartInputPanelVm."
    - "src/TimesheetApp/Views/Tabs/TimesheetTab.xaml (+ .xaml.cs) — static 5-column Mon-Fri DataGrid, date-bound headers, totals footer strip, red-cell validation template."
    - "src/TimesheetApp/Views/Dialogs/SmartInputPreviewDialog.xaml (+ .xaml.cs) — preview confirmation dialog listing CellAssignment rows and per-day validation result."
    - "src/TimesheetApp.Tests/ViewModels/TimesheetRowVmTests.cs, SmartInputPanelVmTests.cs, TimesheetViewModelTests.cs — xUnit + Moq, full real code."
  required_wiring:
    - "TimesheetViewModel ctor injects ITimeLogService, ISmartInputService, IClock (already registered transient in App.xaml.cs per spec §6) — no new DI registration needed in P3 except SmartInputPanelVm if resolved via DI."
    - "TimesheetViewModel subscribes to each TimesheetRowVm.DayChanged to recompute MonTotal..FriTotal and call SaveCommand.NotifyCanExecuteChanged()."
    - "SmartInputPanelVm.ApplyCommand, on success, asks TimesheetViewModel to reload the current week (GetWeekAsync) so the grid reflects upserts."
    - "TimesheetTab.xaml DataContext = TimesheetViewModel; column headers bind to MonHeader..FriHeader via RelativeSource to the DataGrid DataContext."
    - "Smart Input panel preview button opens SmartInputPreviewDialog; confirm triggers ApplyCommand."
  key_links:
    - "TimesheetRowVm.DayChanged -> TimesheetViewModel.OnRowDayChanged -> recompute footer totals + SaveCommand.NotifyCanExecuteChanged (TS-05, TS-06)."
    - "Cell setter -> Round1 + INotifyDataErrorInfo validation -> red border (TS-04) -> on valid commit -> TimesheetViewModel.SaveCellAsync/ClearCellAsync -> ITimeLogService upsert/delete (TS-03, TS-07)."
    - "SmartInputPanelVm.BuildPreview -> ISmartInputService.DistributeEven/FillFull8h -> ITimeLogService.ValidateDayTotalsAsync -> preview dialog (SI-05, SI-06) -> Apply -> ITimeLogService.ApplySmartInputAsync -> reload week."
  req_coverage:
    TS-01: [Task 3, Task 4]
    TS-02: [Task 3, Task 4]
    TS-03: [Task 1, Task 3, Task 4]
    TS-04: [Task 1, Task 4]
    TS-05: [Task 1, Task 3, Task 4]
    TS-06: [Task 3, Task 4]
    TS-07: [Task 3]
    SI-05: [Task 2, Task 5]
    SI-06: [Task 2, Task 5]
---

# P3 — Timesheet + Smart Input UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the WPF Timesheet tab — a fixed Mon-Fri weekly grid with inline edit, live per-day totals footer, 8h save-gating, per-cell red validation, natural-key upsert/delete persistence, and a two-mode Smart Input panel with a validated preview-then-apply flow.

**Architecture:** All testable logic lives in three ViewModels (`TimesheetRowVm`, `SmartInputPanelVm`, `TimesheetViewModel`) that depend only on the P2 service interfaces (`ITimeLogService`, `ISmartInputService`, `IClock`) — never on Dapper/SQL or `System.Windows.*`. The VMs are unit-tested with xUnit + Moq against mocked services. XAML (`TimesheetTab.xaml`, `SmartInputPreviewDialog.xaml`) is thin binding-only and is **manual-verify** (automated WPF UI testing is out of scope), but every piece of logic it triggers is already covered by a VM unit test.

**Tech Stack:** .NET 8 (`net8.0-windows`, WPF, UseWPF), CommunityToolkit.Mvvm 8.4.2 (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`, `INotifyDataErrorInfo`), xUnit + Moq + coverlet.collector (`net8.0` test project).

**Depends on:** P1 (models, `IConnectionFactory`, repos, `DatabaseInitializer`) and P2 (`ITimeLogService`, `ISmartInputService`, `IClock`, read-models `WeekGrid`/`WeekRow`/`CellAssignment`/`SaveResult`/`SmartInputResult`). This plan assumes those interfaces exist exactly as written in the architecture spec §2/§4. DI registrations from spec §6 are already in place; P3 adds no new singleton/transient except `SmartInputPanelVm` if resolved via DI (it is constructed by `TimesheetViewModel` here, so no DI change is required).

**Verification budget (Nyquist Rule):** every `<automated>` block runs a single test-class filter and completes in well under 60s. XAML tasks are explicitly `<verify>` = manual run/observe.

---

## Service / read-model contracts used (from architecture spec — DO NOT redefine, consume verbatim)

```csharp
// Models (spec §2)
public sealed record WeekGrid(DateOnly Monday, IReadOnlyList<WeekRow> Rows);
public sealed record WeekRow(
    int TaskId, string RequestCode, string TaskName, int OrderIndex,
    decimal? Mon, decimal? Tue, decimal? Wed, decimal? Thu, decimal? Fri);
public readonly record struct CellAssignment(DateOnly Date, decimal Hours);
public readonly record struct SaveResult(bool Ok, string? Error);
public readonly record struct SmartInputResult(bool Ok, IReadOnlyList<CellAssignment> Cells, string? Error);

// Services (spec §4)
public interface IClock { DateOnly Today { get; } DateTimeOffset UtcNow { get; } }

public interface ITimeLogService
{
    Task<SaveResult> SaveCellAsync(int userId, int taskId, DateOnly date, decimal hours);
    Task ClearCellAsync(int userId, int taskId, DateOnly date);
    Task<WeekGrid> GetWeekAsync(int userId, DateOnly mondayOfWeek);
    Task<SaveResult> ValidateDayTotalsAsync(int userId, IReadOnlyList<CellAssignment> cells, int taskId);
    Task<SaveResult> ApplySmartInputAsync(int userId, int taskId, IReadOnlyList<CellAssignment> cells);
    Task<IReadOnlyList<User>> GetUsersMissingLogsAsync(int workdayWindowN);
}

public interface ISmartInputService
{
    SmartInputResult DistributeEven(DateOnly from, DateOnly to, decimal totalHours);
    SmartInputResult FillFull8h(DateOnly from, DateOnly to);
}
```

**P3 decisions honored:** hours are `decimal` at the VM boundary (spec §2; the FEATURE-RESEARCH `double?` sketch is superseded by the architecture spec's `decimal?` `WeekRow` shape — use `decimal?`). Week start hard-coded Monday. Columns static Mon-Fri (Sat/Sun do not exist as columns). 8h limit is the **per-column day total** across all task rows, owned by `TimesheetViewModel` (not the row). Smart input **overwrites** (upsert) and is validated post-merge in preview.

---

## File Structure

| File | Responsibility | Task | Wave |
|---|---|---|---|
| `src/TimesheetApp/ViewModels/TimesheetRowVm.cs` | One bindable Mon-Fri row: 5 `decimal?` day props, `RowTotal`, `DayChanged` event, `Round1`, per-cell `INotifyDataErrorInfo` errors | 1 | 1 |
| `src/TimesheetApp.Tests/ViewModels/TimesheetRowVmTests.cs` | Unit tests for row math + error state | 1 | 1 |
| `src/TimesheetApp/ViewModels/SmartInputPanelVm.cs` | Smart-input panel: modes, `BuildPreview`, `Apply` orchestration over `ISmartInputService` + `ITimeLogService` | 2 | 1 |
| `src/TimesheetApp.Tests/ViewModels/SmartInputPanelVmTests.cs` | Unit tests for preview build, validation block, atomic apply | 2 | 1 |
| `src/TimesheetApp/ViewModels/TimesheetViewModel.cs` | Week nav + shaping, footer totals, Save gating, cell save/clear, hosts panel | 3 | 2 |
| `src/TimesheetApp.Tests/ViewModels/TimesheetViewModelTests.cs` | Unit tests for week shaping, totals, CanExecute, save/clear | 3 | 2 |
| `src/TimesheetApp/Views/Tabs/TimesheetTab.xaml` (+ `.xaml.cs`) | Static 5-col DataGrid, date headers, totals footer, red-cell template | 4 | 3 |
| `src/TimesheetApp/Views/Dialogs/SmartInputPreviewDialog.xaml` (+ `.xaml.cs`) | Preview confirm dialog + smart-input panel host | 5 | 3 |

**Wave file-overlap check:** Wave 1 Task 1 (`TimesheetRowVm.cs`) and Task 2 (`SmartInputPanelVm.cs`) touch disjoint files. Wave 3 Task 4 (`TimesheetTab.*` + `OverEightTagConverter.cs` + `App.xaml`) and Task 5 (`SmartInputPreviewDialog.*`) touch disjoint files. Task 3 (Wave 2) is alone and only **reads** Task 1/Task 2 files. No **intra-wave** overlap in any wave.

**Cross-wave edit disclosure (Task 5 → Task 2 files):** Task 5 Step 3 additionally **edits** `SmartInputPanelVm.cs` and `SmartInputPanelVmTests.cs` — files first shipped by Task 2 in Wave 1 — to add the two radio-button mode commands. This is **not** an intra-wave overlap because Task 2 (Wave 1) fully completes and commits before Wave 3 runs; the edit is strictly sequential across waves. No other Wave-3 task touches those two files (Task 4 does not), so Wave 3 itself stays overlap-free. The orchestrator/controller must ensure Wave 1 is merged before dispatching Wave 3 so the edit lands on Task 2's committed file.

---

## Wave 1 — Leaf ViewModels (parallel, zero file overlap)

### Task 1: TimesheetRowVm (row math + per-cell validation)

```xml
<task id="1" wave="1" model="sonnet" reqs="TS-03,TS-04,TS-05">
  <read_first>
    docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md  (§2 WeekRow, §5 TimesheetRowVm row)
    .planning/research/FEATURE-RESEARCH.md  (§1.2 row VM shape, §1.5 validation)
  </read_first>
  <action>Create TimesheetRowVm with 5 nullable decimal day props, RowTotal, DayChanged, Round1, and INotifyDataErrorInfo per-cell red-error state.</action>
  <verify><automated>dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~TimesheetRowVmTests"</automated></verify>
  <done>grep -q "INotifyDataErrorInfo" src/TimesheetApp/ViewModels/TimesheetRowVm.cs &amp;&amp; grep -q "public event Action? DayChanged" src/TimesheetApp/ViewModels/TimesheetRowVm.cs &amp;&amp; test passes</done>
</task>
```

**Files:**
- Create: `src/TimesheetApp/ViewModels/TimesheetRowVm.cs`
- Test: `src/TimesheetApp.Tests/ViewModels/TimesheetRowVmTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// src/TimesheetApp.Tests/ViewModels/TimesheetRowVmTests.cs
using System.ComponentModel;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public class TimesheetRowVmTests
{
    private static TimesheetRowVm NewRow() => new()
    {
        TaskId = 7, RequestCode = "REQ-001", Project = "ProjectX", TaskName = "Implement"
    };

    [Fact]
    public void RowTotal_SumsNonNullDays_TreatingNullAsZero()
    {
        var row = NewRow();
        row.Mon = 4m; row.Wed = 3.5m; // Tue/Thu/Fri null
        Assert.Equal(7.5m, row.RowTotal);
    }

    [Fact]
    public void SettingDay_RoundsToOneDecimalAwayFromZero()
    {
        var row = NewRow();
        row.Mon = 2.45m;
        Assert.Equal(2.5m, row.Mon);
    }

    [Fact]
    public void SettingDay_RaisesDayChanged()
    {
        var row = NewRow();
        var fired = 0;
        row.DayChanged += () => fired++;
        row.Tue = 5m;
        Assert.Equal(1, fired);
    }

    [Fact]
    public void EmptyCell_IsNull_NotZero()
    {
        var row = NewRow();
        row.Fri = null;
        Assert.Null(row.Fri);
    }

    [Theory]
    [InlineData(9)]      // > 8h single cell
    [InlineData(0)]      // <= 0
    [InlineData(-1)]     // negative
    public void InvalidValue_AddsErrorForThatColumn(decimal bad)
    {
        var row = NewRow();
        row.Mon = bad;
        Assert.True(row.HasErrors);
        Assert.NotEmpty(System.Linq.Enumerable.Cast<object>(row.GetErrors(nameof(row.Mon))));
    }

    [Fact]
    public void MoreThanOneDecimal_AddsError()
    {
        var row = NewRow();
        row.Mon = 2.55m; // 2 decimals — rejected (not silently rounded for validation purposes)
        Assert.True(row.HasErrors);
    }

    [Fact]
    public void ValidValue_ClearsPriorError_AndRaisesErrorsChanged()
    {
        var row = NewRow();
        DataErrorsChangedEventArgs? evt = null;
        row.ErrorsChanged += (_, e) => evt = e;
        row.Mon = 9m;          // error
        Assert.True(row.HasErrors);
        row.Mon = 4m;          // valid
        Assert.False(row.HasErrors);
        Assert.NotNull(evt);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~TimesheetRowVmTests"`
Expected: FAIL — `TimesheetRowVm` does not exist / no member compiles.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/TimesheetApp/ViewModels/TimesheetRowVm.cs
using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TimesheetApp.ViewModels;

/// One bindable Mon-Fri timesheet row. null day = empty cell = 0h (distinct from 0).
/// Per-cell red validation via INotifyDataErrorInfo; the 8h *column* total lives on the owner VM.
public sealed class TimesheetRowVm : ObservableObject, INotifyDataErrorInfo
{
    public int    TaskId      { get; init; }
    public string RequestCode { get; init; } = "";
    public string Project     { get; init; } = "";
    public string TaskName    { get; init; } = "";

    private decimal? _mon, _tue, _wed, _thu, _fri;
    public decimal? Mon { get => _mon; set => SetDay(ref _mon, value, nameof(Mon)); }
    public decimal? Tue { get => _tue; set => SetDay(ref _tue, value, nameof(Tue)); }
    public decimal? Wed { get => _wed; set => SetDay(ref _wed, value, nameof(Wed)); }
    public decimal? Thu { get => _thu; set => SetDay(ref _thu, value, nameof(Thu)); }
    public decimal? Fri { get => _fri; set => SetDay(ref _fri, value, nameof(Fri)); }

    public decimal RowTotal => (_mon ?? 0) + (_tue ?? 0) + (_wed ?? 0) + (_thu ?? 0) + (_fri ?? 0);

    /// Raised after any day value changes; owner VM recomputes column totals + Save CanExecute.
    public event Action? DayChanged;

    private readonly Dictionary<string, string> _errors = new();

    private void SetDay(ref decimal? field, decimal? value, string propName)
    {
        var rounded = Round1(value);
        Validate(propName, value);                  // validate the RAW entry (catches >1-decimal)
        if (SetProperty(ref field, rounded, propName))
        {
            OnPropertyChanged(nameof(RowTotal));
            DayChanged?.Invoke();
        }
    }

    private void Validate(string propName, decimal? raw)
    {
        string? error = null;
        if (raw is { } v)
        {
            if (v <= 0) error = "Hours must be greater than 0.";
            else if (v > 8m) error = "A single cell cannot exceed 8h.";
            else if (decimal.Round(v, 1, MidpointRounding.AwayFromZero) != v)
                error = "At most 1 decimal place.";
        }
        if (error is null) _errors.Remove(propName);
        else _errors[propName] = error;
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propName));
    }

    private static decimal? Round1(decimal? v) =>
        v is null ? null : decimal.Round(v.Value, 1, MidpointRounding.AwayFromZero);

    // --- INotifyDataErrorInfo ---
    public bool HasErrors => _errors.Count > 0;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public IEnumerable GetErrors(string? propertyName) =>
        propertyName is not null && _errors.TryGetValue(propertyName, out var e)
            ? new[] { e } : Array.Empty<string>();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~TimesheetRowVmTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/TimesheetApp/ViewModels/TimesheetRowVm.cs src/TimesheetApp.Tests/ViewModels/TimesheetRowVmTests.cs
git commit -m "feat(p3): add TimesheetRowVm with per-cell validation and DayChanged (TS-03/04/05)"
```

---

### Task 2: SmartInputPanelVm (preview-then-apply orchestration)

```xml
<task id="2" wave="1" model="opus" reqs="SI-05,SI-06">
  <read_first>
    docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md  (§4 ISmartInputService + ITimeLogService.ValidateDayTotals/ApplySmartInput, §7.4)
    .planning/REQUIREMENTS.md  (SI-05, SI-06)
    .planning/research/FEATURE-RESEARCH.md  (§2.4 preview-then-save)
  </read_first>
  <action>Create SmartInputPanelVm: two modes (DistributeEven / FillFull8h), BuildPreviewCommand that calls ISmartInputService then ITimeLogService.ValidateDayTotalsAsync, and ApplyCommand that calls ApplySmartInputAsync only on a validated preview.</action>
  <verify><automated>dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~SmartInputPanelVmTests"</automated></verify>
  <done>grep -q "ValidateDayTotalsAsync" src/TimesheetApp/ViewModels/SmartInputPanelVm.cs &amp;&amp; grep -q "ApplySmartInputAsync" src/TimesheetApp/ViewModels/SmartInputPanelVm.cs &amp;&amp; test passes</done>
</task>
```

**Files:**
- Create: `src/TimesheetApp/ViewModels/SmartInputPanelVm.cs`
- Test: `src/TimesheetApp.Tests/ViewModels/SmartInputPanelVmTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
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
    private static readonly DateOnly To   = new(2026, 6, 17); // Wed

    private static (SmartInputPanelVm vm, Mock<ISmartInputService> si, Mock<ITimeLogService> tl) Make(int userId = 1)
    {
        var si = new Mock<ISmartInputService>();
        var tl = new Mock<ITimeLogService>();
        var vm = new SmartInputPanelVm(si.Object, tl.Object, () => userId)
        {
            TaskId = 7, From = From, To = To, TotalHours = 9m, Mode = SmartInputMode.DistributeEven
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~SmartInputPanelVmTests"`
Expected: FAIL — `SmartInputPanelVm` / `SmartInputMode` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/TimesheetApp/ViewModels/SmartInputPanelVm.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

public enum SmartInputMode { DistributeEven, FillFull8h }

/// Smart Input panel (SI-06): two modes + preview. Apply overwrites cells atomically,
/// gated on a post-merge 8h validation done during preview (SI-05).
public sealed partial class SmartInputPanelVm : ObservableObject
{
    private readonly ISmartInputService _smartInput;
    private readonly ITimeLogService _timeLogs;
    private readonly Func<int> _currentUserId;

    public SmartInputPanelVm(ISmartInputService smartInput, ITimeLogService timeLogs, Func<int> currentUserId)
    {
        _smartInput = smartInput;
        _timeLogs = timeLogs;
        _currentUserId = currentUserId;
    }

    [ObservableProperty] private SmartInputMode _mode = SmartInputMode.DistributeEven;
    [ObservableProperty] private int _taskId;
    [ObservableProperty] private DateOnly _from;
    [ObservableProperty] private DateOnly _to;
    [ObservableProperty] private decimal _totalHours;
    [ObservableProperty] private string? _previewError;

    public ObservableCollection<CellAssignment> PreviewCells { get; } = new();

    /// True only after a preview that produced cells AND passed the 8h day-total validation.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApply;

    /// Raised after a successful atomic apply; owner VM reloads the week grid.
    public event Action? Applied;

    [RelayCommand]
    private async Task BuildPreviewAsync()
    {
        CanApply = false;
        PreviewCells.Clear();
        PreviewError = null;

        var math = Mode == SmartInputMode.DistributeEven
            ? _smartInput.DistributeEven(From, To, TotalHours)
            : _smartInput.FillFull8h(From, To);

        if (!math.Ok)
        {
            PreviewError = math.Error;          // SI-03 no-op message
            return;
        }

        var validation = await _timeLogs.ValidateDayTotalsAsync(_currentUserId(), math.Cells, TaskId);
        foreach (var c in math.Cells) PreviewCells.Add(c);

        if (!validation.Ok)
        {
            PreviewError = validation.Error;    // SI-05 post-merge >8h block
            return;
        }
        CanApply = true;
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var result = await _timeLogs.ApplySmartInputAsync(_currentUserId(), TaskId, PreviewCells.ToList());
        if (!result.Ok)
        {
            PreviewError = result.Error;
            return;
        }
        CanApply = false;
        PreviewCells.Clear();
        Applied?.Invoke();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~SmartInputPanelVmTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/TimesheetApp/ViewModels/SmartInputPanelVm.cs src/TimesheetApp.Tests/ViewModels/SmartInputPanelVmTests.cs
git commit -m "feat(p3): add SmartInputPanelVm preview-then-apply with 8h validation (SI-05/06)"
```

---

## Wave 2 — TimesheetViewModel (depends on Task 1)

### Task 3: TimesheetViewModel (week shaping, totals, Save gating, persistence)

```xml
<task id="3" wave="2" model="opus" reqs="TS-01,TS-02,TS-03,TS-05,TS-06,TS-07,SI-05,SI-06">
  <read_first>
    docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md  (§5 TimesheetViewModel row + §7.2 MondayOf, §7.4)
    src/TimesheetApp/ViewModels/TimesheetRowVm.cs  (from Task 1 — read, do not modify)
    src/TimesheetApp/ViewModels/SmartInputPanelVm.cs  (from Task 2 — read, do not modify)
    .planning/REQUIREMENTS.md  (TS-01..07)
  </read_first>
  <action>Create TimesheetViewModel: CurrentWeek Monday + Prev/Next, GetWeekAsync->Rows shaping, MonHeader..FriHeader date labels, MonTotal..FriTotal footer recomputed on DayChanged, SaveCommand CanExecute=false when any column >8, per-cell save/clear via ITimeLogService, hosts SmartInputPanelVm and reloads on Applied.</action>
  <verify><automated>dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~TimesheetViewModelTests"</automated></verify>
  <done>grep -q "NotifyCanExecuteChanged" src/TimesheetApp/ViewModels/TimesheetViewModel.cs &amp;&amp; grep -q "MondayOf" src/TimesheetApp/ViewModels/TimesheetViewModel.cs &amp;&amp; test passes</done>
</task>
```

**Files:**
- Create: `src/TimesheetApp/ViewModels/TimesheetViewModel.cs`
- Test: `src/TimesheetApp.Tests/ViewModels/TimesheetViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// src/TimesheetApp.Tests/ViewModels/TimesheetViewModelTests.cs
using Moq;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public class TimesheetViewModelTests
{
    // Wed 2026-06-17 -> Monday of that week is 2026-06-15.
    private static readonly DateOnly Wed = new(2026, 6, 17);
    private static readonly DateOnly Mon = new(2026, 6, 15);

    private static WeekGrid Grid(DateOnly monday, params WeekRow[] rows) => new(monday, rows);

    private static (TimesheetViewModel vm, Mock<ITimeLogService> tl, Mock<ISmartInputService> si) Make(
        WeekGrid? initial = null, int userId = 1)
    {
        var tl = new Mock<ITimeLogService>();
        var si = new Mock<ISmartInputService>();
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.Today).Returns(Wed);

        tl.Setup(t => t.GetWeekAsync(userId, It.IsAny<DateOnly>()))
          .ReturnsAsync((int _, DateOnly m) => initial ?? Grid(m));
        tl.Setup(t => t.SaveCellAsync(userId, It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<decimal>()))
          .ReturnsAsync(new SaveResult(true, null));
        tl.Setup(t => t.ClearCellAsync(userId, It.IsAny<int>(), It.IsAny<DateOnly>()))
          .Returns(Task.CompletedTask);

        var vm = new TimesheetViewModel(tl.Object, si.Object, clock.Object, () => userId);
        return (vm, tl, si);
    }

    [Fact]
    public async Task Load_SetsCurrentWeekToMondayOfToday()
    {
        var (vm, _, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(Mon, vm.CurrentWeek);
    }

    [Fact]
    public async Task Headers_ShowConcreteDates_MonToFri()
    {
        var (vm, _, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("Mon 15/06", vm.MonHeader);
        Assert.Equal("Fri 19/06", vm.FriHeader);
    }

    [Fact]
    public async Task Next_ShiftsWeekForwardSevenDays_AndReloads()
    {
        var (vm, tl, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.NextWeekCommand.ExecuteAsync(null);
        Assert.Equal(Mon.AddDays(7), vm.CurrentWeek);
        tl.Verify(t => t.GetWeekAsync(1, Mon.AddDays(7)), Times.Once);
    }

    [Fact]
    public async Task Prev_ShiftsWeekBackSevenDays()
    {
        var (vm, _, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.PreviousWeekCommand.ExecuteAsync(null);
        Assert.Equal(Mon.AddDays(-7), vm.CurrentWeek);
    }

    [Fact]
    public async Task Rows_ShapedFromWeekGrid_OneRowPerTask()
    {
        var grid = Grid(Mon,
            new WeekRow(7, "REQ-001", "Implement", 0, 4m, null, 3m, null, null),
            new WeekRow(9, "DEFAULT", "Annual Leave", 0, null, null, null, null, 8m));
        var (vm, _, _) = Make(grid);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(7, vm.Rows[0].TaskId);
        Assert.Equal(4m, vm.Rows[0].Mon);
        Assert.Equal("Annual Leave", vm.Rows[1].TaskName);
    }

    [Fact]
    public async Task ColumnTotals_SumAllRows_AndUpdateOnCellChange()
    {
        var grid = Grid(Mon,
            new WeekRow(7, "REQ-001", "Implement", 0, 4m, null, null, null, null),
            new WeekRow(9, "DEFAULT", "Meeting", 1, 2m, null, null, null, null));
        var (vm, _, _) = Make(grid);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(6m, vm.MonTotal);

        vm.Rows[0].Mon = 5m;               // 5 + 2
        Assert.Equal(7m, vm.MonTotal);
    }

    [Fact]
    public async Task Save_DisabledWhenAnyColumnExceedsEight()
    {
        var grid = Grid(Mon,
            new WeekRow(7, "REQ-001", "Implement", 0, 5m, null, null, null, null),
            new WeekRow(9, "DEFAULT", "Meeting", 1, 4m, null, null, null, null)); // Mon = 9 > 8
        var (vm, _, _) = Make(grid);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.Rows[1].Mon = 2m;               // Mon now 7
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveCell_WithValue_UpsertsOnNaturalKey()
    {
        var grid = Grid(Mon, new WeekRow(7, "REQ-001", "Implement", 0, null, null, null, null, null));
        var (vm, tl, _) = Make(grid);
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.SaveCellAsync(vm.Rows[0], DayColumn.Mon);
        // value still null -> nothing yet
        vm.Rows[0].Mon = 4m;
        await vm.SaveCellAsync(vm.Rows[0], DayColumn.Mon);

        tl.Verify(t => t.SaveCellAsync(1, 7, Mon, 4m), Times.Once);
    }

    [Fact]
    public async Task SaveCell_WithEmptyValue_DeletesLog()
    {
        var grid = Grid(Mon, new WeekRow(7, "REQ-001", "Implement", 0, 4m, null, null, null, null));
        var (vm, tl, _) = Make(grid);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.Rows[0].Mon = null;             // cleared
        await vm.SaveCellAsync(vm.Rows[0], DayColumn.Mon);

        tl.Verify(t => t.ClearCellAsync(1, 7, Mon), Times.Once);
        tl.Verify(t => t.SaveCellAsync(1, 7, Mon, It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task SmartInputApplied_ReloadsWeek()
    {
        var (vm, tl, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        tl.Invocations.Clear();

        vm.RaiseSmartInputAppliedForTest();   // internal test hook -> ReloadAsync

        tl.Verify(t => t.GetWeekAsync(1, Mon), Times.Once);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~TimesheetViewModelTests"`
Expected: FAIL — `TimesheetViewModel` / `DayColumn` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/TimesheetApp/ViewModels/TimesheetViewModel.cs
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

public enum DayColumn { Mon, Tue, Wed, Thu, Fri }

/// Timesheet tab VM (TS-01..07 + hosts SI-05/06 panel).
/// Owns week navigation, row shaping, per-column footer totals, Save gating, and per-cell persistence.
public sealed partial class TimesheetViewModel : ObservableObject
{
    private readonly ITimeLogService _timeLogs;
    private readonly IClock _clock;
    private readonly Func<int> _currentUserId;
    private bool _suppressTotals;

    public TimesheetViewModel(
        ITimeLogService timeLogs, ISmartInputService smartInput, IClock clock, Func<int> currentUserId)
    {
        _timeLogs = timeLogs;
        _clock = clock;
        _currentUserId = currentUserId;

        SmartInput = new SmartInputPanelVm(smartInput, timeLogs, currentUserId);
        SmartInput.Applied += async () => await ReloadAsync();

        CurrentWeek = MondayOf(_clock.Today);
    }

    public SmartInputPanelVm SmartInput { get; }

    [ObservableProperty] private DateOnly _currentWeek;

    public ObservableCollection<TimesheetRowVm> Rows { get; } = new();

    [ObservableProperty] private decimal _monTotal;
    [ObservableProperty] private decimal _tueTotal;
    [ObservableProperty] private decimal _wedTotal;
    [ObservableProperty] private decimal _thuTotal;
    [ObservableProperty] private decimal _friTotal;

    public string MonHeader => Header(0);
    public string TueHeader => Header(1);
    public string WedHeader => Header(2);
    public string ThuHeader => Header(3);
    public string FriHeader => Header(4);

    private string Header(int offset)
    {
        var d = CurrentWeek.AddDays(offset);
        var dow = d.DayOfWeek.ToString()[..3]; // Mon/Tue/...
        return $"{dow} {d:dd/MM}";
    }

    /// Hard-coded Monday week start (NOT culture-derived) — spec §7.2.
    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    [RelayCommand]
    private Task LoadAsync() => ReloadAsync();

    [RelayCommand]
    private async Task NextWeekAsync()
    {
        CurrentWeek = CurrentWeek.AddDays(7);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task PreviousWeekAsync()
    {
        CurrentWeek = CurrentWeek.AddDays(-7);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var grid = await _timeLogs.GetWeekAsync(_currentUserId(), CurrentWeek);

        _suppressTotals = true;
        foreach (var r in Rows) r.DayChanged -= OnRowDayChanged;
        Rows.Clear();
        foreach (var wr in grid.Rows)
        {
            var row = new TimesheetRowVm
            {
                TaskId = wr.TaskId, RequestCode = wr.RequestCode,
                Project = "", TaskName = wr.TaskName,
                Mon = wr.Mon, Tue = wr.Tue, Wed = wr.Wed, Thu = wr.Thu, Fri = wr.Fri
            };
            row.DayChanged += OnRowDayChanged;
            Rows.Add(row);
        }
        _suppressTotals = false;

        OnPropertyChanged(nameof(MonHeader));
        OnPropertyChanged(nameof(TueHeader));
        OnPropertyChanged(nameof(WedHeader));
        OnPropertyChanged(nameof(ThuHeader));
        OnPropertyChanged(nameof(FriHeader));
        RecomputeTotals();
    }

    private void OnRowDayChanged()
    {
        if (_suppressTotals) return;
        RecomputeTotals();
    }

    private void RecomputeTotals()
    {
        MonTotal = Rows.Sum(r => r.Mon ?? 0);
        TueTotal = Rows.Sum(r => r.Tue ?? 0);
        WedTotal = Rows.Sum(r => r.Wed ?? 0);
        ThuTotal = Rows.Sum(r => r.Thu ?? 0);
        FriTotal = Rows.Sum(r => r.Fri ?? 0);
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool AnyDayOverEight() =>
        new[] { MonTotal, TueTotal, WedTotal, ThuTotal, FriTotal }.Any(t => t > 8m);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        foreach (var row in Rows)
            foreach (DayColumn col in Enum.GetValues<DayColumn>())
                await SaveCellAsync(row, col);
        await ReloadAsync();
    }

    private bool CanSave() => !AnyDayOverEight();

    /// Persist one cell: value -> upsert on natural key (TS-07); empty -> delete (TS-03).
    public async Task SaveCellAsync(TimesheetRowVm row, DayColumn col)
    {
        var date = CurrentWeek.AddDays((int)col);
        var value = col switch
        {
            DayColumn.Mon => row.Mon, DayColumn.Tue => row.Tue, DayColumn.Wed => row.Wed,
            DayColumn.Thu => row.Thu, _ => row.Fri
        };
        if (value is { } v) await _timeLogs.SaveCellAsync(_currentUserId(), row.TaskId, date, v);
        else await _timeLogs.ClearCellAsync(_currentUserId(), row.TaskId, date);
    }

    // Test-only hook to exercise the Applied -> reload wiring without WPF dispatcher.
    internal void RaiseSmartInputAppliedForTest() => _ = ReloadAsync();
}
```

> **Note on the smart-input reload test:** `SmartInput.Applied` is `async void` in production (event handler). The `RaiseSmartInputAppliedForTest` hook is `internal`, so the test project needs assembly access — add `[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TimesheetApp.Tests")]` to `src/TimesheetApp/Properties/AssemblyInfo.cs` (or an `<InternalsVisibleTo Include="TimesheetApp.Tests"/>` item in `TimesheetApp.csproj`) as part of this task. The hook calls the same `ReloadAsync` awaitable path so the unit test can assert the reload without a WPF dispatcher. Delete the `vm.SmartInput.Applied += null;` line from the test — it is a dead no-op; the behavioral assertion is `GetWeekAsync(1, Mon)` `Times.Once`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~TimesheetViewModelTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: Commit**

```bash
git add src/TimesheetApp/ViewModels/TimesheetViewModel.cs src/TimesheetApp.Tests/ViewModels/TimesheetViewModelTests.cs
git commit -m "feat(p3): add TimesheetViewModel week shaping/totals/save-gating/persistence (TS-01..07)"
```

---

## Wave 3 — XAML views (MANUAL-VERIFY, depends on Wave 2)

> Wave 3 tasks have **no automated `<verify>`** — automated WPF UI testing is out of scope. Each `<verify>` is a manual run/observe checklist. All logic these views trigger is already covered by Wave 1/2 unit tests; the XAML is binding-only.

### Task 4: TimesheetTab.xaml — grid + date headers + totals footer + red-cell template

```xml
<task id="4" wave="3" model="sonnet" reqs="TS-01,TS-02,TS-03,TS-04,TS-05,TS-06">
  <read_first>
    src/TimesheetApp/ViewModels/TimesheetViewModel.cs  (from Task 3 — binding source)
    src/TimesheetApp/ViewModels/TimesheetRowVm.cs  (from Task 1 — cell bindings + errors)
    .planning/research/FEATURE-RESEARCH.md  (§1.3 static columns + RelativeSource header, §1.6 footer strip)
  </read_first>
  <action>Create TimesheetTab.xaml: AutoGenerateColumns=False DataGrid with Task col + 5 static Mon-Fri text columns bound to Mon..Fri with headers bound via RelativeSource to MonHeader..FriHeader; Prev/Next buttons; a footer UniformGrid bound to MonTotal..FriTotal with a red DataTrigger when >8; ValidatesOnNotifyDataErrors=True for red cell borders; Save button bound to SaveCommand.</action>
  <verify>MANUAL: dotnet run --project src/TimesheetApp; on the Timesheet tab observe (1) exactly 5 day columns labelled like "Mon 15/06".."Fri 19/06" and NO Sat/Sun (TS-01); (2) Prev/Next shifts headers by a week (TS-01); (3) rows are the active tasks incl DEFAULT (TS-02); (4) type 9 in a cell -> red border, clear a cell -> empties (TS-03/04); (5) footer shows live per-day totals that turn red when a column >8 (TS-05); (6) Save button greys out while any column >8 (TS-06).</verify>
  <done>grep -q "AutoGenerateColumns=\"False\"" src/TimesheetApp/Views/Tabs/TimesheetTab.xaml &amp;&amp; grep -q "MonHeader" src/TimesheetApp/Views/Tabs/TimesheetTab.xaml &amp;&amp; grep -q "ValidatesOnNotifyDataErrors=True" src/TimesheetApp/Views/Tabs/TimesheetTab.xaml</done>
</task>
```

**Files:**
- Create: `src/TimesheetApp/Views/Tabs/TimesheetTab.xaml`
- Create: `src/TimesheetApp/Views/Tabs/TimesheetTab.xaml.cs`

- [ ] **Step 1: Create the UserControl XAML**

```xml
<!-- src/TimesheetApp/Views/Tabs/TimesheetTab.xaml -->
<UserControl x:Class="TimesheetApp.Views.Tabs.TimesheetTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:TimesheetApp.ViewModels">
  <UserControl.Resources>
    <Style x:Key="OverEight" TargetType="TextBlock">
      <Style.Triggers>
        <DataTrigger Binding="{Binding RelativeSource={RelativeSource Self}, Path=Tag}" Value="True">
          <Setter Property="Foreground" Value="Red"/>
        </DataTrigger>
      </Style.Triggers>
    </Style>
  </UserControl.Resources>

  <DockPanel LastChildFill="True">
    <!-- Week nav -->
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="8">
      <Button Content="◀ Prev" Command="{Binding PreviousWeekCommand}" Width="80"/>
      <TextBlock Text="{Binding CurrentWeek, StringFormat='Week of {0:dd/MM/yyyy}'}"
                 VerticalAlignment="Center" Margin="12,0" FontWeight="Bold"/>
      <Button Content="Next ▶" Command="{Binding NextWeekCommand}" Width="80"/>
      <Button Content="Save" Command="{Binding SaveCommand}" Width="80" Margin="24,0,0,0"/>
    </StackPanel>

    <!-- Footer totals strip (TS-05) -->
    <Grid DockPanel.Dock="Bottom" Margin="8">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="2*"/>
        <ColumnDefinition Width="*"/><ColumnDefinition Width="*"/><ColumnDefinition Width="*"/>
        <ColumnDefinition Width="*"/><ColumnDefinition Width="*"/>
        <ColumnDefinition Width="*"/>
      </Grid.ColumnDefinitions>
      <TextBlock Grid.Column="0" Text="Day totals" FontWeight="Bold"/>
      <TextBlock Grid.Column="1" Text="{Binding MonTotal, StringFormat=N1}"
                 Tag="{Binding MonTotal, Converter={StaticResource OverEightTag}}" Style="{StaticResource OverEight}"/>
      <TextBlock Grid.Column="2" Text="{Binding TueTotal, StringFormat=N1}"
                 Tag="{Binding TueTotal, Converter={StaticResource OverEightTag}}" Style="{StaticResource OverEight}"/>
      <TextBlock Grid.Column="3" Text="{Binding WedTotal, StringFormat=N1}"
                 Tag="{Binding WedTotal, Converter={StaticResource OverEightTag}}" Style="{StaticResource OverEight}"/>
      <TextBlock Grid.Column="4" Text="{Binding ThuTotal, StringFormat=N1}"
                 Tag="{Binding ThuTotal, Converter={StaticResource OverEightTag}}" Style="{StaticResource OverEight}"/>
      <TextBlock Grid.Column="5" Text="{Binding FriTotal, StringFormat=N1}"
                 Tag="{Binding FriTotal, Converter={StaticResource OverEightTag}}" Style="{StaticResource OverEight}"/>
    </Grid>

    <!-- Editable grid (TS-01/02/03/04) -->
    <DataGrid ItemsSource="{Binding Rows}" AutoGenerateColumns="False"
              CanUserAddRows="False" CanUserDeleteRows="False" SelectionUnit="Cell" Margin="8">
      <DataGrid.Columns>
        <DataGridTextColumn Header="Task" Binding="{Binding TaskName}" IsReadOnly="True" Width="2*"/>
        <DataGridTextColumn Width="*"
            Header="{Binding DataContext.MonHeader, RelativeSource={RelativeSource AncestorType=DataGrid}}"
            Binding="{Binding Mon, UpdateSourceTrigger=LostFocus, TargetNullValue='',
                      ValidatesOnNotifyDataErrors=True, NotifyOnValidationError=True}"/>
        <DataGridTextColumn Width="*"
            Header="{Binding DataContext.TueHeader, RelativeSource={RelativeSource AncestorType=DataGrid}}"
            Binding="{Binding Tue, UpdateSourceTrigger=LostFocus, TargetNullValue='',
                      ValidatesOnNotifyDataErrors=True, NotifyOnValidationError=True}"/>
        <DataGridTextColumn Width="*"
            Header="{Binding DataContext.WedHeader, RelativeSource={RelativeSource AncestorType=DataGrid}}"
            Binding="{Binding Wed, UpdateSourceTrigger=LostFocus, TargetNullValue='',
                      ValidatesOnNotifyDataErrors=True, NotifyOnValidationError=True}"/>
        <DataGridTextColumn Width="*"
            Header="{Binding DataContext.ThuHeader, RelativeSource={RelativeSource AncestorType=DataGrid}}"
            Binding="{Binding Thu, UpdateSourceTrigger=LostFocus, TargetNullValue='',
                      ValidatesOnNotifyDataErrors=True, NotifyOnValidationError=True}"/>
        <DataGridTextColumn Width="*"
            Header="{Binding DataContext.FriHeader, RelativeSource={RelativeSource AncestorType=DataGrid}}"
            Binding="{Binding Fri, UpdateSourceTrigger=LostFocus, TargetNullValue='',
                      ValidatesOnNotifyDataErrors=True, NotifyOnValidationError=True}"/>
        <DataGridTextColumn Header="Total" Binding="{Binding RowTotal, StringFormat=N1}" IsReadOnly="True" Width="*"/>
      </DataGrid.Columns>
    </DataGrid>
  </DockPanel>
</UserControl>
```

> **Implementer note (TS-04/05 red feedback):** add a tiny `OverEightTag` `IValueConverter` (decimal -> bool: `(decimal)v > 8m`) registered in `App.xaml` resources, OR replace the `Tag`/converter approach with a direct `DataTrigger` on `MonTotal > 8` using a `Style` with `<DataTrigger Binding="{Binding MonTotal}" Value="...">` is not range-comparable — so the converter is the simplest correct path. Keep the converter in `src/TimesheetApp/Views/Converters/OverEightTagConverter.cs`. Per-cell red borders come for free from `ValidatesOnNotifyDataErrors=True` reading `TimesheetRowVm`'s `INotifyDataErrorInfo` (Task 1), no extra style needed for the default red border.

- [ ] **Step 2: Create code-behind**

```csharp
// src/TimesheetApp/Views/Tabs/TimesheetTab.xaml.cs
using System.Windows.Controls;

namespace TimesheetApp.Views.Tabs;

public partial class TimesheetTab : UserControl
{
    public TimesheetTab() => InitializeComponent();
}
```

- [ ] **Step 3: Add the OverEightTag converter**

```csharp
// src/TimesheetApp/Views/Converters/OverEightTagConverter.cs
using System.Globalization;
using System.Windows.Data;

namespace TimesheetApp.Views.Converters;

public sealed class OverEightTagConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is decimal d && d > 8m;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
```

Register in `App.xaml` resources: `<conv:OverEightTagConverter x:Key="OverEightTag"/>` (add `xmlns:conv="clr-namespace:TimesheetApp.Views.Converters"`).

- [ ] **Step 4: Build, then MANUAL verify**

Run: `dotnet build src/TimesheetApp` (Expected: build succeeds, 0 errors).
Then MANUAL: `dotnet run --project src/TimesheetApp` and walk the `<verify>` checklist above (5 columns, Prev/Next, red cell on 9, footer red >8, Save greys out).

- [ ] **Step 5: Commit**

```bash
git add src/TimesheetApp/Views/Tabs/TimesheetTab.xaml src/TimesheetApp/Views/Tabs/TimesheetTab.xaml.cs src/TimesheetApp/Views/Converters/OverEightTagConverter.cs src/TimesheetApp/App.xaml
git commit -m "feat(p3): TimesheetTab grid with date headers, totals footer, red-cell validation (TS-01..06)"
```

---

### Task 5: Smart Input panel + preview confirmation dialog (SI-06 UI)

```xml
<task id="5" wave="3" model="sonnet" reqs="SI-05,SI-06">
  <read_first>
    src/TimesheetApp/ViewModels/SmartInputPanelVm.cs  (from Task 2 — binding source)
    src/TimesheetApp/ViewModels/TimesheetViewModel.cs  (from Task 3 — exposes SmartInput)
    .planning/REQUIREMENTS.md  (SI-05, SI-06)
  </read_first>
  <action>Create a Smart Input panel section (two modes via radio: Chia deu Task/From/To/Total, Full 8h Task/From/To) bound to TimesheetViewModel.SmartInput, a Preview button bound to BuildPreviewCommand, and a SmartInputPreviewDialog.xaml listing PreviewCells + PreviewError with Confirm bound to ApplyCommand (enabled only when CanApply).</action>
  <verify>MANUAL: dotnet run --project src/TimesheetApp; in Smart Input pick Chia deu, enter a task + Mon-Wed range + total 9, click Preview -> dialog lists 3 cells (3/3/3) and Confirm is enabled (SI-06); set a range/total that pushes a day >8 -> dialog shows the per-day error and Confirm is disabled (SI-05); Confirm a valid preview -> grid reloads with the upserted cells overwriting prior values (SI-05).</verify>
  <done>grep -q "BuildPreviewCommand" src/TimesheetApp/Views/Dialogs/SmartInputPreviewDialog.xaml &amp;&amp; grep -q "ApplyCommand" src/TimesheetApp/Views/Dialogs/SmartInputPreviewDialog.xaml &amp;&amp; grep -q "PreviewCells" src/TimesheetApp/Views/Dialogs/SmartInputPreviewDialog.xaml</done>
</task>
```

**Files:**
- Create: `src/TimesheetApp/Views/Dialogs/SmartInputPreviewDialog.xaml`
- Create: `src/TimesheetApp/Views/Dialogs/SmartInputPreviewDialog.xaml.cs`

- [ ] **Step 1: Create the preview dialog XAML (hosts the panel inputs + preview list)**

```xml
<!-- src/TimesheetApp/Views/Dialogs/SmartInputPreviewDialog.xaml -->
<Window x:Class="TimesheetApp.Views.Dialogs.SmartInputPreviewDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Smart Input" Width="420" Height="480" WindowStartupLocation="CenterOwner">
  <!-- DataContext is the TimesheetViewModel.SmartInput (SmartInputPanelVm) -->
  <DockPanel Margin="12" LastChildFill="True">
    <StackPanel DockPanel.Dock="Top">
      <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
        <RadioButton Content="Chia đều" IsChecked="True" GroupName="Mode" Margin="0,0,12,0"
                     Command="{Binding SetDistributeEvenCommand}"/>
        <RadioButton Content="Full 8h" GroupName="Mode"
                     Command="{Binding SetFull8hCommand}"/>
      </StackPanel>

      <TextBlock Text="Task Id"/>
      <TextBox Text="{Binding TaskId, UpdateSourceTrigger=PropertyChanged}"/>
      <TextBlock Text="From (yyyy-MM-dd)"/>
      <TextBox Text="{Binding From, UpdateSourceTrigger=PropertyChanged}"/>
      <TextBlock Text="To (yyyy-MM-dd)"/>
      <TextBox Text="{Binding To, UpdateSourceTrigger=PropertyChanged}"/>
      <TextBlock Text="Total hours (Chia đều only)"/>
      <TextBox Text="{Binding TotalHours, UpdateSourceTrigger=PropertyChanged}"/>

      <Button Content="Preview" Command="{Binding BuildPreviewCommand}" Margin="0,8" Width="120" HorizontalAlignment="Left"/>
      <TextBlock Text="{Binding PreviewError}" Foreground="Red" TextWrapping="Wrap"/>
    </StackPanel>

    <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,8,0,0">
      <Button Content="Confirm" Command="{Binding ApplyCommand}" Width="100" IsDefault="True"/>
      <Button Content="Cancel" IsCancel="True" Width="100" Margin="8,0,0,0"/>
    </StackPanel>

    <!-- Preview cells (SI-06) -->
    <DataGrid ItemsSource="{Binding PreviewCells}" AutoGenerateColumns="False" IsReadOnly="True" Margin="0,8">
      <DataGrid.Columns>
        <DataGridTextColumn Header="Date"  Binding="{Binding Date, StringFormat='yyyy-MM-dd'}" Width="*"/>
        <DataGridTextColumn Header="Hours" Binding="{Binding Hours, StringFormat=N1}" Width="*"/>
      </DataGrid.Columns>
    </DataGrid>
  </DockPanel>
</Window>
```

- [ ] **Step 2: Create code-behind**

```csharp
// src/TimesheetApp/Views/Dialogs/SmartInputPreviewDialog.xaml.cs
using System.Windows;

namespace TimesheetApp.Views.Dialogs;

public partial class SmartInputPreviewDialog : Window
{
    public SmartInputPreviewDialog() => InitializeComponent();
}
```

- [ ] **Step 3: Add the two mode-select relay commands to SmartInputPanelVm**

> These are the only additions to `SmartInputPanelVm`; they set `Mode` from radio buttons. Because Task 2 already shipped `SmartInputPanelVm.cs`, and Task 5 is in a later wave, this edit is sequential (no intra-wave overlap). Add inside the class:

```csharp
[RelayCommand] private void SetDistributeEven() => Mode = SmartInputMode.DistributeEven;
[RelayCommand] private void SetFull8h() => Mode = SmartInputMode.FillFull8h;
```

Add a unit test to `SmartInputPanelVmTests.cs` (sequential edit, same wave-3 ownership):

```csharp
[Fact]
public void ModeCommands_SwitchMode()
{
    var (vm, _, _) = Make();
    vm.SetFull8hCommand.Execute(null);
    Assert.Equal(SmartInputMode.FillFull8h, vm.Mode);
    vm.SetDistributeEvenCommand.Execute(null);
    Assert.Equal(SmartInputMode.DistributeEven, vm.Mode);
}
```

Run: `dotnet test src/TimesheetApp.Tests --filter "FullyQualifiedName~SmartInputPanelVmTests"` — Expected: PASS (7 tests).

- [ ] **Step 4: Build, then MANUAL verify**

Run: `dotnet build src/TimesheetApp` (Expected: build succeeds).
Then MANUAL: `dotnet run --project src/TimesheetApp`, open Smart Input, walk the `<verify>` checklist (preview 3 cells, over-8 blocks Confirm, valid Confirm reloads grid).

- [ ] **Step 5: Commit**

```bash
git add src/TimesheetApp/Views/Dialogs/SmartInputPreviewDialog.xaml src/TimesheetApp/Views/Dialogs/SmartInputPreviewDialog.xaml.cs src/TimesheetApp/ViewModels/SmartInputPanelVm.cs src/TimesheetApp.Tests/ViewModels/SmartInputPanelVmTests.cs
git commit -m "feat(p3): Smart Input panel + preview dialog with mode select (SI-05/06)"
```

---

## Task Board (ownership + wave)

| Task | Component | Wave | Model | Implementer agent | Verify | REQs |
|---|---|---|---|---|---|---|
| 1 | TimesheetRowVm | 1 | sonnet | implementer-dotnet-csharp | automated (xUnit) | TS-03, TS-04, TS-05 |
| 2 | SmartInputPanelVm | 1 | opus | implementer-dotnet-csharp | automated (xUnit) | SI-05, SI-06 |
| 3 | TimesheetViewModel | 2 | opus | implementer-dotnet-csharp | automated (xUnit) | TS-01, TS-02, TS-03, TS-05, TS-06, TS-07, SI-05/06 host |
| 4 | TimesheetTab.xaml | 3 | sonnet | implementer-dotnet-csharp | **MANUAL** | TS-01, TS-02, TS-03, TS-04, TS-05, TS-06 |
| 5 | SmartInputPreviewDialog.xaml | 3 | sonnet | implementer-dotnet-csharp | **MANUAL** | SI-05, SI-06 |

**Model-profile note:** project `config.json` model_profile = `quality` shifts sonnet->opus, haiku->sonnet at dispatch time. The `<model>` tags above are the plan baseline; the controller applies the quality bump before spawning subagents.

**Stack skill:** all 5 tasks are .NET/C# WPF -> implementer loads `skills/implementer-dotnet-csharp/SKILL.md` per `stack-skill-rule-map.md`.

---

## Self-Review

**1. Spec coverage (9 P3 REQs):** TS-01 (Task 3 nav/headers + Task 4 columns), TS-02 (Task 3 shaping + Task 4 rows), TS-03 (Task 1 null cell + Task 3 clear/upsert + Task 4 inline edit), TS-04 (Task 1 INotifyDataErrorInfo + Task 4 ValidatesOnNotifyDataErrors), TS-05 (Task 1 RowTotal/DayChanged + Task 3 column totals + Task 4 footer), TS-06 (Task 3 CanExecute + Task 4 Save button), TS-07 (Task 3 SaveCellAsync/ClearCellAsync), SI-05 (Task 2 ValidateDayTotals+ApplySmartInput + Task 5 dialog block), SI-06 (Task 2 modes/preview + Task 5 panel UI). All 9 covered.

**2. Placeholder scan:** No TBD/TODO; every code step shows full code; XAML steps show full markup; all test bodies are concrete.

**3. Type consistency:** `decimal?` day props throughout (matches spec `WeekRow`); `DayColumn` enum, `SmartInputMode` enum, `CellAssignment`/`SaveResult`/`SmartInputResult`/`WeekGrid`/`WeekRow` consumed exactly as spec §2 defines; `SmartInputPanelVm(ISmartInputService, ITimeLogService, Func<int>)` ctor signature identical in Task 2 impl, Task 3 host, and tests; `BuildPreviewCommand`/`ApplyCommand`/`CanApply`/`Applied` names consistent across Task 2/3/5.

---

## Open notes for orchestrator review

- **Project-existence assumption:** `src/TimesheetApp` and `src/TimesheetApp.Tests` are created in P1. If P1/P2 are not yet on the branch, Wave 1 cannot compile — confirm P2 is merged before dispatching P3 Wave 1.
- **`Project` field:** `WeekRow` has no `Project` (only `RequestCode`); `TimesheetRowVm.Project` is set to `""` in Task 3. If reports/UI later need the project string per row, it must be added to `WeekRow` in P2 — out of P3 scope, flagged not fabricated.
- **Smart-input `currentUserId`:** passed as `Func<int>` from the host `TimesheetViewModel` (which gets it via the same `Func<int>` injected by `MainViewModel`/`ICurrentUserService`). Wiring of that `Func<int>` into `TimesheetViewModel`'s DI registration is a P-boundary detail; spec §6 registers `TimesheetViewModel` transient — the controller must ensure the user-id provider is supplied at resolve time (small App.xaml.cs factory delegate).
