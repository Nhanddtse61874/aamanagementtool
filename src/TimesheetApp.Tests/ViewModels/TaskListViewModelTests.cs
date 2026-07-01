using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

// P8 / W5 (TL-04..08, §6, §7): sqlite-backed Task List grid build. Mirrors the repository-test setup
// (real repos over TestDb) + a FakeClock so schedule chips are deterministic.
public sealed class TaskListViewModelTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private BacklogRepository _backlogs = null!;
    private TaskRepository _tasks = null!;
    private TimeLogRepository _timeLogs = null!;
    private TagRepository _tags = null!;
    private PcaContactRepository _pca = null!;
    private UserRepository _users = null!;
    private HolidayRepository _holidays = null!;
    private readonly IWorkingDayCalculator _calc = new WorkingDayCalculator();
    private readonly IScheduleStateService _schedule = new ScheduleStateService();

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _backlogs = new BacklogRepository(_db);
        _tasks = new TaskRepository(_db);
        _timeLogs = new TimeLogRepository(_db);
        _tags = new TagRepository(_db);
        _pca = new PcaContactRepository(_db);
        _users = new UserRepository(_db);
        _holidays = new HolidayRepository(_db);
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; }
        public DateTimeOffset UtcNow => new(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    // Build a VM whose month selector points at `today` (so the default month = the seeded month) and
    // whose schedule math is evaluated as-of `today`. Archive is unused here → a throwing stub is fine.
    private TaskListViewModel CreateVm(DateOnly today) =>
        new(_backlogs, _tasks, _timeLogs, _tags, _pca, _users, _holidays, _calc, _schedule,
            new StubArchive(), new FakeClock { Today = today }, messenger: null);

    // P10: a VM wired with a team filter over the given service.
    private TaskListViewModel CreateVm(DateOnly today, ICurrentTeamService currentTeam) =>
        new(_backlogs, _tasks, _timeLogs, _tags, _pca, _users, _holidays, _calc, _schedule,
            new StubArchive(), new FakeClock { Today = today }, messenger: null, currentTeam: currentTeam);

    // Minimal in-memory team context: two teams, raises ActiveTeamChanged on Switch.
    private sealed class FakeTeam : ICurrentTeamService
    {
        public int ActiveTeamId { get; private set; }
        public Team? ActiveTeam => AvailableTeams.FirstOrDefault(t => t.Id == ActiveTeamId);
        public IReadOnlyList<Team> AvailableTeams { get; }
        public event EventHandler? ActiveTeamChanged;
        public FakeTeam(int activeId, params Team[] teams) { AvailableTeams = teams; ActiveTeamId = activeId; }
        public Task InitializeAsync(int currentUserId) => Task.CompletedTask;
        public Task SetActiveTeamAsync(int teamId)
        {
            ActiveTeamId = teamId;
            ActiveTeamChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private static FakeTeam TwoTeams(int activeId = 20) =>
        new(activeId,
            new Team(10, "Alpha", true, DateTimeOffset.UtcNow),
            new Team(20, "Beta", true, DateTimeOffset.UtcNow));

    private sealed class StubArchive : ITaskListArchiveService
    {
        public string FileNameFor(int year, int month) => $"{year:0000}{month:00}_tasklist.md";
        public Task<string?> ExportMonthAsync(int year, int month) => Task.FromResult<string?>(null);
        public Task<string?> BuildMonthMarkdownAsync(IReadOnlyList<int>? teamIds, int year, int month) => Task.FromResult<string?>(null);
        public Task BackfillMissingMonthsAsync() => Task.CompletedTask;
    }

    private async Task<int> SeedBacklogAsync(
        string code, string month, DateOnly? start = null, DateOnly? internalDeadline = null,
        decimal? rough = null, decimal? official = null, int? progress = null,
        int? assigneeUserId = null, int? pcaContactId = null, int? teamId = null) =>
        await _backlogs.InsertAsync(new Backlog(
            0, code, "ARCS", DateTimeOffset.UtcNow,
            StartDate: start, PeriodMonth: month,
            AssigneeUserId: assigneeUserId,
            DeadlineInternal: internalDeadline,
            RoughEstimateHours: rough, OfficialEstimateHours: official,
            ProgressPercent: progress, PcaContactId: pcaContactId, TeamId: teamId));

    private TaskListRowVm Row(TaskListViewModel vm, string code) =>
        vm.Rows.Single(r => r.BacklogCode == code);

    [Fact] // Only the selected month's non-DEFAULT backlogs become rows; DEFAULT is always excluded.
    public async Task LoadAsync_builds_rows_for_selected_month_only_excluding_default()
    {
        await SeedBacklogAsync("JUN-1", "2026-06");
        await SeedBacklogAsync("JUN-2", "2026-06");
        await SeedBacklogAsync("JUL-1", "2026-07");

        var vm = CreateVm(new DateOnly(2026, 6, 15));
        await vm.LoadAsync();

        Assert.Equal(new[] { "JUN-1", "JUN-2" }, vm.Rows.Select(r => r.BacklogCode).ToArray());
        Assert.DoesNotContain(vm.Rows, r => r.BacklogCode == "DEFAULT");   // DEFAULT never listed
        Assert.DoesNotContain(vm.Rows, r => r.BacklogCode == "JUL-1");      // other month excluded
    }

    [Fact] // Switching the month combo reloads with that month's rows (preserves the selection).
    public async Task Changing_month_switches_the_rows()
    {
        await SeedBacklogAsync("JUN-1", "2026-06");
        await SeedBacklogAsync("JUL-1", "2026-07");

        var vm = CreateVm(new DateOnly(2026, 6, 15));
        await vm.LoadAsync();
        Assert.Equal(new[] { "JUN-1" }, vm.Rows.Select(r => r.BacklogCode).ToArray());

        vm.SelectedMonth = 7;            // fires a reload via the change handler
        await vm.LoadAsync();            // also await directly so the assertion never races the fire-and-forget
        Assert.Equal(new[] { "JUL-1" }, vm.Rows.Select(r => r.BacklogCode).ToArray());
    }

    [Fact] // ISSUE 3: "All months" (SelectedMonth == 0) lists every backlog across months incl. null-period.
    public async Task AllMonths_lists_every_backlog_including_null_period()
    {
        await SeedBacklogAsync("JUN-1", "2026-06");
        await SeedBacklogAsync("JUL-1", "2026-07");
        // A null-period backlog (visible in the Backlog tab) must not be silently lost in Task List.
        await _backlogs.InsertAsync(new Backlog(
            0, "NOPER", "ARCS", DateTimeOffset.UtcNow, PeriodMonth: null));

        var vm = CreateVm(new DateOnly(2026, 6, 15));
        vm.SelectedMonth = TaskListViewModel.AllMonths;
        await vm.LoadAsync();

        Assert.Equal(new[] { "JUL-1", "JUN-1", "NOPER" },
            vm.Rows.Select(r => r.BacklogCode).OrderBy(c => c).ToArray());
        Assert.DoesNotContain(vm.Rows, r => r.BacklogCode == "DEFAULT");   // DEFAULT still excluded
    }

    [Fact] // QA: the progress cell inline-edit — text is seeded from the stored %, and Escape (ResetProgressEdit)
           // restores that committed value even after invalid input, without persisting a bad value.
    public async Task ProgressEdit_seeds_from_percent_and_ResetProgressEdit_recovers_invalid_input()
    {
        await SeedBacklogAsync("PRG-1", "2026-06", progress: 50);
        var vm = CreateVm(new DateOnly(2026, 6, 15));
        await vm.LoadAsync();
        var row = Row(vm, "PRG-1");

        Assert.Equal("50", row.EditProgressText);   // seeded from the stored ProgressPercent
        Assert.Equal(50, row.EditProgress);

        row.EditProgressText = "abc";                // invalid → parser ignores it, EditProgress unchanged
        Assert.Equal(50, row.EditProgress);

        row.ResetProgressEdit();                     // Escape restores the committed value + clears the bad text
        Assert.Equal("50", row.EditProgressText);
        Assert.Equal(50, row.EditProgress);
    }

    [Fact] // ISSUE 3: a specific month still filters (null-period + other months excluded).
    public async Task SpecificMonth_filters_and_excludes_null_period()
    {
        await SeedBacklogAsync("JUN-1", "2026-06");
        await SeedBacklogAsync("JUL-1", "2026-07");
        await _backlogs.InsertAsync(new Backlog(
            0, "NOPER", "ARCS", DateTimeOffset.UtcNow, PeriodMonth: null));

        var vm = CreateVm(new DateOnly(2026, 6, 15));   // defaults to month 6
        await vm.LoadAsync();

        Assert.Equal(new[] { "JUN-1" }, vm.Rows.Select(r => r.BacklogCode).ToArray());
    }

    [Fact] // Logged hours are all-time per backlog (XC-06/A1) and include soft-deleted-task hours.
    public async Task Logged_hours_are_alltime_including_softdeleted_tasks()
    {
        var bid = await SeedBacklogAsync("LOG-1", "2026-06", official: 20m);
        var taskId = await _tasks.InsertAsync(new TaskItem(0, bid, "Do", 0, true));
        var (userId, _) = await _db.SeedUserAndTaskAsync();   // a throwaway user to own the logs

        await _timeLogs.UpsertAsync(new TimeLog(0, userId, taskId, new DateOnly(2026, 6, 2), 3m, DateTimeOffset.UtcNow));
        await _timeLogs.UpsertAsync(new TimeLog(0, userId, taskId, new DateOnly(2026, 6, 3), 5m, DateTimeOffset.UtcNow));
        await _db.SetTaskActiveAsync(taskId, false);          // soft-delete: hours still roll up

        var vm = CreateVm(new DateOnly(2026, 6, 15));
        await vm.LoadAsync();

        Assert.Equal(8m, Row(vm, "LOG-1").Row.LoggedHours);
        // Absent logs → 0.
        var bid2 = await SeedBacklogAsync("LOG-0", "2026-06");
        await vm.LoadAsync();
        Assert.Equal(0m, Row(vm, "LOG-0").Row.LoggedHours);
    }

    [Fact] // Estimate precedence (§7): official wins over rough; rough used when no official; null when neither.
    public async Task Estimate_uses_official_then_rough()
    {
        await SeedBacklogAsync("EST-OFF", "2026-06", rough: 10m, official: 16m);
        await SeedBacklogAsync("EST-ROUGH", "2026-06", rough: 10m);
        await SeedBacklogAsync("EST-NONE", "2026-06");

        var vm = CreateVm(new DateOnly(2026, 6, 15));
        await vm.LoadAsync();

        Assert.Equal(16m, Row(vm, "EST-OFF").Row.EstimateHours);
        Assert.Equal(10m, Row(vm, "EST-ROUGH").Row.EstimateHours);
        Assert.Null(Row(vm, "EST-NONE").Row.EstimateHours);
        Assert.Equal("—", Row(vm, "EST-NONE").EstimateText);
    }

    [Fact] // §6.2: Done (≥1 active task, all Done) suppresses chips even when past deadline + behind.
    public async Task Done_backlog_suppresses_chips()
    {
        // Past internal deadline + zero logged + estimate>0 → would be Late, but every task is Done.
        var bid = await SeedBacklogAsync("DONE-1", "2026-06",
            start: new DateOnly(2026, 6, 1), internalDeadline: new DateOnly(2026, 6, 5), official: 10m);
        await _tasks.InsertAsync(new TaskItem(0, bid, "T1", 0, true, "Done"));
        await _tasks.InsertAsync(new TaskItem(0, bid, "T2", 1, true, "Done"));

        var vm = CreateVm(new DateOnly(2026, 6, 15));   // today is well past the 06-05 deadline
        await vm.LoadAsync();

        Assert.Equal(ScheduleState.Normal, Row(vm, "DONE-1").Row.ScheduleState);
        Assert.Empty(Row(vm, "DONE-1").Chips);          // no system chip
    }

    [Fact] // Past internal deadline + not Done → Late (red system chip), first in the chip list.
    public async Task Past_deadline_not_done_is_late()
    {
        var bid = await SeedBacklogAsync("LATE-1", "2026-06",
            start: new DateOnly(2026, 6, 1), internalDeadline: new DateOnly(2026, 6, 5), official: 10m);
        await _tasks.InsertAsync(new TaskItem(0, bid, "T1", 0, true, "In-process"));

        var vm = CreateVm(new DateOnly(2026, 6, 15));
        await vm.LoadAsync();

        var row = Row(vm, "LATE-1");
        Assert.Equal(ScheduleState.Late, row.Row.ScheduleState);
        Assert.True(row.Chips[0].IsLate);
    }

    [Fact] // Within ≤2 working days of the internal deadline + behind → Warning (amber system chip).
    public async Task Within_window_and_behind_is_warning()
    {
        // today=Wed 06-17; start Mon 06-15; deadline Fri 06-19 → (today,deadline] = 2 wd, in window.
        // total=5, elapsed=3, logged 0/10 < 3/5 → behind.
        var bid = await SeedBacklogAsync("WARN-1", "2026-06",
            start: new DateOnly(2026, 6, 15), internalDeadline: new DateOnly(2026, 6, 19), official: 10m);
        await _tasks.InsertAsync(new TaskItem(0, bid, "T1", 0, true, "In-process"));

        var vm = CreateVm(new DateOnly(2026, 6, 17));
        await vm.LoadAsync();

        var row = Row(vm, "WARN-1");
        Assert.Equal(ScheduleState.Warning, row.Row.ScheduleState);
        Assert.True(row.Chips[0].IsSystem);
        Assert.False(row.Chips[0].IsLate);
    }

    [Fact] // Chip ordering (TAG-02/Q4): the system chip comes before custom tags, which sort by Tag.Id.
    public async Task Chip_order_is_system_then_custom_tags_by_id()
    {
        var bid = await SeedBacklogAsync("CHIP-1", "2026-06",
            start: new DateOnly(2026, 6, 1), internalDeadline: new DateOnly(2026, 6, 5), official: 10m);
        await _tasks.InsertAsync(new TaskItem(0, bid, "T1", 0, true, "Todo"));     // past deadline → Late

        // Insert two tags out of id order in the link set; expect them ordered by id after the system chip.
        var tagA = await _tags.InsertAsync(new Tag(0, "Alpha", "🅰", "#111111", DateTimeOffset.UtcNow));
        var tagB = await _tags.InsertAsync(new Tag(0, "Beta", "🅱", "#222222", DateTimeOffset.UtcNow));
        await _backlogs.SetTagsAsync(bid, new[] { tagB, tagA });

        var vm = CreateVm(new DateOnly(2026, 6, 15));
        await vm.LoadAsync();

        var chips = Row(vm, "CHIP-1").Chips;
        Assert.Equal(3, chips.Count);
        Assert.True(chips[0].IsSystem && chips[0].IsLate);   // system first
        Assert.Equal("Alpha", chips[1].Text);                // then custom tags by id (tagA < tagB)
        Assert.Equal("Beta", chips[2].Text);
    }

    [Fact] // PCT (User, via GetAll) + PCA (PcaContact, via GetAll) names resolve, incl. deactivated.
    public async Task Resolves_pct_and_pca_names_including_deactivated()
    {
        var userId = await _users.InsertAsync(new User(0, "Lan", null, true));
        await _users.SetActiveAsync(userId, false);                       // deactivated PCT still resolves
        var pcaId = await _pca.InsertAsync(new PcaContact(0, "Acme", true));
        await _pca.SetActiveAsync(pcaId, false);                          // deactivated PCA still resolves

        await SeedBacklogAsync("NAME-1", "2026-06", assigneeUserId: userId, pcaContactId: pcaId);

        var vm = CreateVm(new DateOnly(2026, 6, 15));
        await vm.LoadAsync();

        var row = Row(vm, "NAME-1");
        Assert.Equal("Lan", row.PctAssigneeName);
        Assert.Equal("Acme", row.PcaContactName);
    }

    [Fact] // null progress → "—" + no bar; a set progress renders its whole-number percent.
    public async Task Progress_formats_null_as_dash()
    {
        await SeedBacklogAsync("PROG-NULL", "2026-06");
        await SeedBacklogAsync("PROG-60", "2026-06", progress: 60);

        var vm = CreateVm(new DateOnly(2026, 6, 15));
        await vm.LoadAsync();

        Assert.False(Row(vm, "PROG-NULL").HasProgress);
        Assert.Equal("—", Row(vm, "PROG-NULL").ProgressText);
        Assert.True(Row(vm, "PROG-60").HasProgress);
        Assert.Equal("60%", Row(vm, "PROG-60").ProgressText);
    }

    // ===== P10 W7 (TM-07) =====

    // Default filter = active team only → only the active team's backlogs become rows.
    [Fact]
    public async Task LoadAsync_scopes_rows_to_active_team_by_default()
    {
        await SeedBacklogAsync("ALPHA-1", "2026-06", teamId: 10);
        await SeedBacklogAsync("BETA-1", "2026-06", teamId: 20);

        var vm = CreateVm(new DateOnly(2026, 6, 15), TwoTeams(activeId: 20));
        Assert.Equal(new[] { 20 }, vm.TeamFilter!.CheckedTeamIds);
        await vm.LoadAsync();

        Assert.Equal(new[] { "BETA-1" }, vm.Rows.Select(r => r.BacklogCode).ToArray());
    }

    // Checking a second team aggregates both teams' backlogs and shows the team label.
    [Fact]
    public async Task Checking_second_team_aggregates_and_shows_team_label()
    {
        await SeedBacklogAsync("ALPHA-1", "2026-06", teamId: 10);
        await SeedBacklogAsync("BETA-1", "2026-06", teamId: 20);

        var vm = CreateVm(new DateOnly(2026, 6, 15), TwoTeams(activeId: 20));
        await vm.LoadAsync();

        vm.TeamFilter!.Teams.First(t => t.Team.Id == 10).IsChecked = true; // triggers reload
        await vm.LoadAsync();

        Assert.Equal(new[] { "ALPHA-1", "BETA-1" }, vm.Rows.Select(r => r.BacklogCode).OrderBy(c => c).ToArray());
        var alpha = Row(vm, "ALPHA-1");
        Assert.True(alpha.ShowTeam);
        Assert.Equal("Alpha", alpha.TeamName);
    }

    // Switching the active team resets the filter to {new active team} and reloads.
    [Fact]
    public async Task Active_team_switch_resets_filter_and_reloads()
    {
        await SeedBacklogAsync("ALPHA-1", "2026-06", teamId: 10);
        await SeedBacklogAsync("BETA-1", "2026-06", teamId: 20);

        var team = TwoTeams(activeId: 20);
        var vm = CreateVm(new DateOnly(2026, 6, 15), team);
        await vm.LoadAsync();
        Assert.Equal(new[] { "BETA-1" }, vm.Rows.Select(r => r.BacklogCode).ToArray());

        await team.SetActiveTeamAsync(10);   // raises ActiveTeamChanged → filter resets + reload
        await Task.Yield();

        Assert.Equal(new[] { 10 }, vm.TeamFilter!.CheckedTeamIds);
        Assert.Equal(new[] { "ALPHA-1" }, vm.Rows.Select(r => r.BacklogCode).ToArray());
    }
}
