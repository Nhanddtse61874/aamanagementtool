using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

public class TimeLogServiceTests
{
    private readonly Mock<ITimeLogRepository> _logs = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IBacklogRepository> _requests = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IDbBackupHelper> _backup = new();
    private readonly Mock<IAppConfig> _config = new();
    private readonly SpyJournalWarningSink _journal = new();
    private readonly Mock<IHolidayRepository> _holidays = new();

    // P10 (TM-06): TimeLogService scopes the Log Work grid + RPT-04 banner to the active team.
    // ActiveTeamId defaults to 1 so the grid/group tests see a non-empty team scope.
    private readonly FakeCurrentTeam _currentTeam = new() { ActiveTeamId = 1 };

    private sealed class FakeCurrentTeam : ICurrentTeamService
    {
        public int ActiveTeamId { get; set; }
        public Team? ActiveTeam => null;
        public IReadOnlyList<Team> AvailableTeams => Array.Empty<Team>();
        public event EventHandler? ActiveTeamChanged { add { } remove { } }
        public Task InitializeAsync(int currentUserId) => Task.CompletedTask;
        public Task SetActiveTeamAsync(int teamId) { ActiveTeamId = teamId; return Task.CompletedTask; }
    }

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; }
        public DateTimeOffset UtcNow { get; init; } = new(2026, 6, 21, 0, 0, 0, TimeSpan.Zero);
    }

    // Records every warning so a test can assert IsJournalGone IS consulted on the bulk path (XC-09).
    private sealed class SpyJournalWarningSink : IJournalWarningSink
    {
        public List<string> Warnings { get; } = new();
        public void Warn(string message) => Warnings.Add(message);
    }

    public TimeLogServiceTests()
    {
        // Default: no holidays. A test that needs a holiday re-sets the relevant method (last wins).
        _holidays.Setup(h => h.GetAllAsync()).ReturnsAsync(Array.Empty<Holiday>());
        _holidays.Setup(h => h.IsHolidayAsync(It.IsAny<DateOnly>())).ReturnsAsync(false);
    }

    private TimeLogService Make(DateOnly today)
        => new(_logs.Object, _users.Object, _tasks.Object, _requests.Object, _teams.Object,
               _currentTeam, _backup.Object,
               new FakeClock { Today = today }, _config.Object, _journal, _holidays.Object);

    private static readonly DateOnly Tue = new(2026, 6, 16); // weekday
    private static readonly DateOnly Sat = new(2026, 6, 20); // weekend

    [Fact]
    public async Task SaveCell_rejects_zero_and_negative()  // XC-04
    {
        var svc = Make(Tue);
        Assert.False((await svc.SaveCellAsync(1, 1, Tue, 0m)).Ok);
        Assert.False((await svc.SaveCellAsync(1, 1, Tue, -2m)).Ok);
        _logs.Verify(r => r.UpsertAsync(It.IsAny<TimeLog>()), Times.Never);
    }

    [Fact]
    public async Task SaveCell_rejects_more_than_one_decimal()  // XC-04
    {
        var svc = Make(Tue);
        Assert.False((await svc.SaveCellAsync(1, 1, Tue, 2.55m)).Ok);
        _logs.Verify(r => r.UpsertAsync(It.IsAny<TimeLog>()), Times.Never);
    }

    [Fact]
    public async Task SaveCell_rejects_weekend_date()  // XC-05
    {
        var svc = Make(Tue);
        Assert.False((await svc.SaveCellAsync(1, 1, Sat, 4m)).Ok);
        _logs.Verify(r => r.UpsertAsync(It.IsAny<TimeLog>()), Times.Never);
    }

    [Fact]
    public async Task SaveCell_rejects_holiday_date()  // HOL-02 (ISSUE 5): a marked holiday is non-working
    {
        // Tue is a weekday but marked as a holiday → must be rejected like a weekend.
        // SaveCellAsync checks the date via the single-row IsHolidayAsync (N+1 fix), not GetAllAsync.
        _holidays.Setup(h => h.IsHolidayAsync(Tue)).ReturnsAsync(true);
        var svc = Make(Tue);

        var result = await svc.SaveCellAsync(1, 1, Tue, 4m);

        Assert.False(result.Ok);
        Assert.Contains("holiday", result.Error, StringComparison.OrdinalIgnoreCase);
        _logs.Verify(r => r.UpsertAsync(It.IsAny<TimeLog>()), Times.Never);
    }

    [Fact]
    public async Task SaveCell_rejects_per_cell_over_8h()  // XC-02
    {
        var svc = Make(Tue);
        Assert.False((await svc.SaveCellAsync(1, 1, Tue, 8.5m)).Ok);
        _logs.Verify(r => r.UpsertAsync(It.IsAny<TimeLog>()), Times.Never);
    }

    [Fact]
    public async Task SaveCell_rejects_when_whole_day_total_exceeds_8h()  // XC-03 (reads storage)
    {
        // task 1 already has 6h on Tue; adding 3h to task 2 => 9h total => reject.
        _logs.Setup(r => r.GetByUserAndRangeAsync(1, Tue, Tue)).ReturnsAsync(new[]
        {
            new TimeLog(0, 1, 1, Tue, 6m, DateTimeOffset.UtcNow)
        });
        var svc = Make(Tue);

        var result = await svc.SaveCellAsync(1, taskId: 2, Tue, 3m);

        Assert.False(result.Ok);
        _logs.Verify(r => r.UpsertAsync(It.IsAny<TimeLog>()), Times.Never);
    }

    [Fact]
    public async Task SaveCell_rounds_away_from_zero_then_upserts()  // XC-04
    {
        _logs.Setup(r => r.GetByUserAndRangeAsync(1, Tue, Tue)).ReturnsAsync(Array.Empty<TimeLog>());
        var svc = Make(Tue);

        var result = await svc.SaveCellAsync(1, 1, Tue, 2.5m);

        Assert.True(result.Ok);
        _logs.Verify(r => r.UpsertAsync(It.Is<TimeLog>(l => l.Hours == 2.5m && l.WorkDate == Tue)), Times.Once);
    }

    [Fact]
    public async Task ClearCell_deletes_the_row()  // TS-03
    {
        var svc = Make(Tue);
        await svc.ClearCellAsync(1, 1, Tue);
        _logs.Verify(r => r.DeleteAsync(1, 1, Tue), Times.Once);
    }

    [Fact]
    public async Task ValidateDayTotals_subtracts_target_task_existing_hours()  // SI-05 post-merge check exposed
    {
        // Existing: task 5 has 5h on Tue. Proposing task 5 -> 8h on Tue.
        // existingDayTotal(5) - existingForThisTask(5) + new(8) = 5 - 5 + 8 = 8 <= 8 => OK.
        _logs.Setup(r => r.GetByUserAndRangeAsync(1, Tue, Tue)).ReturnsAsync(new[]
        {
            new TimeLog(0, 1, 5, Tue, 5m, DateTimeOffset.UtcNow)
        });
        var svc = Make(Tue);

        var ok = await svc.ValidateDayTotalsAsync(1, new[] { new CellAssignment(Tue, 8m) }, taskId: 5);
        Assert.True(ok.Ok);
    }

    [Fact]
    public async Task ValidateDayTotals_rejects_when_other_task_pushes_day_over_8()  // SI-05
    {
        // task 9 has 4h on Tue. Proposing task 5 -> 5h => 4 + 5 = 9 > 8 => reject.
        _logs.Setup(r => r.GetByUserAndRangeAsync(1, Tue, Tue)).ReturnsAsync(new[]
        {
            new TimeLog(0, 1, 9, Tue, 4m, DateTimeOffset.UtcNow)
        });
        var svc = Make(Tue);

        var result = await svc.ValidateDayTotalsAsync(1, new[] { new CellAssignment(Tue, 5m) }, taskId: 5);
        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task ValidateDayTotals_rejects_holiday_date()  // HOL-02: bulk path matches SaveCell's guard
    {
        _holidays.Setup(h => h.IsHolidayAsync(Tue)).ReturnsAsync(true);   // weekday marked as holiday
        var svc = Make(Tue);

        var result = await svc.ValidateDayTotalsAsync(1, new[] { new CellAssignment(Tue, 3m) }, taskId: 5);

        Assert.False(result.Ok);
        Assert.Contains("holiday", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateSmartFill_rejects_holiday_date()  // HOL-02: multi-task fill path
    {
        _holidays.Setup(h => h.IsHolidayAsync(Tue)).ReturnsAsync(true);
        var svc = Make(Tue);
        var tasks = new[] { new SmartFillTask(5, new[] { new CellAssignment(Tue, 3m) }) };

        var result = await svc.ValidateSmartFillAsync(1, tasks);

        Assert.False(result.Ok);
        Assert.Contains("holiday", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplySmartFill_rejects_holiday_without_backup_or_write()  // HOL-02 end-to-end
    {
        _holidays.Setup(h => h.IsHolidayAsync(Tue)).ReturnsAsync(true);
        var svc = Make(Tue);
        var tasks = new[] { new SmartFillTask(5, new[] { new CellAssignment(Tue, 3m) }) };

        var result = await svc.ApplySmartFillAsync(1, tasks);

        Assert.False(result.Ok);
        _backup.Verify(b => b.BackupAsync(), Times.Never);
        _logs.Verify(r => r.UpsertBatchAsync(It.IsAny<IReadOnlyList<TimeLog>>()), Times.Never);
    }

    [Fact]
    public async Task ApplySmartInput_backs_up_then_batch_upserts_when_valid()  // XC-10 + SI-05
    {
        _logs.Setup(r => r.GetByUserAndRangeAsync(1, It.IsAny<DateOnly>(), It.IsAny<DateOnly>()))
             .ReturnsAsync(Array.Empty<TimeLog>());
        var svc = Make(Tue);
        var cells = new[] { new CellAssignment(Tue, 3.3m), new CellAssignment(new DateOnly(2026, 6, 17), 3.4m) };

        var result = await svc.ApplySmartInputAsync(1, taskId: 5, cells);

        Assert.True(result.Ok);
        _backup.Verify(b => b.BackupAsync(), Times.Once);          // XC-10 before bulk
        _logs.Verify(r => r.UpsertBatchAsync(It.Is<IReadOnlyList<TimeLog>>(l => l.Count == 2)), Times.Once);
    }

    [Fact]
    public async Task ApplySmartInput_rejects_without_backup_or_write_when_invalid()  // XC-10 not triggered on reject
    {
        _logs.Setup(r => r.GetByUserAndRangeAsync(1, Tue, Tue)).ReturnsAsync(new[]
        {
            new TimeLog(0, 1, 9, Tue, 6m, DateTimeOffset.UtcNow)   // other task already 6h
        });
        var svc = Make(Tue);
        var cells = new[] { new CellAssignment(Tue, 5m) };          // 6 + 5 = 11 > 8

        var result = await svc.ApplySmartInputAsync(1, taskId: 5, cells);

        Assert.False(result.Ok);
        _backup.Verify(b => b.BackupAsync(), Times.Never);
        _logs.Verify(r => r.UpsertBatchAsync(It.IsAny<IReadOnlyList<TimeLog>>()), Times.Never);
    }

    [Fact]
    public async Task ApplySmartInput_rejects_distribution_whose_remainder_day_exceeds_8h()  // PITFALL §2 HIGH edge
    {
        // DistributeEven can dump the remainder on the last day. If that day already holds 7.5h
        // from another task and the distributed share is 1h, the merged day total is 8.5h > 8h.
        // The pure math (SmartInputService) does NOT guard this; the apply path MUST reject it.
        _logs.Setup(r => r.GetByUserAndRangeAsync(1, Tue, Tue)).ReturnsAsync(new[]
        {
            new TimeLog(0, 1, 9, Tue, 7.5m, DateTimeOffset.UtcNow)   // other task already 7.5h on Tue
        });
        var svc = Make(Tue);
        var cells = new[] { new CellAssignment(Tue, 1m) };           // 7.5 + 1 = 8.5 > 8 => reject

        var result = await svc.ApplySmartInputAsync(1, taskId: 5, cells);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));       // per-day breakdown message
        _backup.Verify(b => b.BackupAsync(), Times.Never);           // no backup on reject
        _logs.Verify(r => r.UpsertBatchAsync(It.IsAny<IReadOnlyList<TimeLog>>()), Times.Never);
    }

    [Fact]
    public async Task GetUsersMissingLogs_flags_active_team_members_with_no_logs_in_window()  // RPT-04
    {
        var today = new DateOnly(2026, 6, 22); // Monday; LastNWorkingDays(_,3)=[Mon22,Fri19,Thu18]
        _users.Setup(r => r.GetActiveAsync()).ReturnsAsync(new[]
        {
            new User(1, "HasLogs", null, true),
            new User(2, "NoLogs", null, true)
        });
        _teams.Setup(r => r.GetUserIdsForTeamAsync(1)).ReturnsAsync(new[] { 1, 2 }); // both on active team
        _logs.Setup(r => r.GetUserIdsWithLogsInRangeAsync(new DateOnly(2026, 6, 18), today))
             .ReturnsAsync(new[] { 1 });
        var svc = Make(today);

        var missing = await svc.GetUsersMissingLogsAsync(3);

        Assert.Single(missing);
        Assert.Equal(2, missing[0].Id);
    }

    // M8.2 bug 3: the window used to exclude weekends but NOT holidays, contradicting WorkingDayCalculator
    // — so the "hasn't logged in N days" banner counted public holidays against people. Today is Wed 17th
    // and Tue 16th is a holiday, so the 2-working-day window is [Wed 17, Mon 15] and must reach back to
    // Mon 15. The old code stopped at Tue 16 (the holiday) and treated it as a day you failed to log.
    [Fact]
    public async Task GetUsersMissingLogs_window_skips_holidays_not_just_weekends()  // RPT-04 + HOL-02
    {
        var today = new DateOnly(2026, 6, 17);   // Wednesday
        var tue = new DateOnly(2026, 6, 16);
        var mon = new DateOnly(2026, 6, 15);
        _holidays.Setup(h => h.GetAllAsync()).ReturnsAsync(new[] { new Holiday(tue, "Public holiday") });
        _users.Setup(r => r.GetActiveAsync()).ReturnsAsync(new[] { new User(1, "NoLogs", null, true) });
        _teams.Setup(r => r.GetUserIdsForTeamAsync(1)).ReturnsAsync(new[] { 1 });
        _logs.Setup(r => r.GetUserIdsWithLogsInRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>()))
             .ReturnsAsync(Array.Empty<int>());
        var svc = Make(today);

        await svc.GetUsersMissingLogsAsync(2);

        // The 2-working-day window skips the holiday and reaches Mon 15 — it does not stop at Tue 16.
        _logs.Verify(r => r.GetUserIdsWithLogsInRangeAsync(mon, today), Times.Once);
        _logs.Verify(r => r.GetUserIdsWithLogsInRangeAsync(tue, today), Times.Never);
    }

    [Fact]
    public async Task GetUsersMissingLogs_excludes_users_outside_the_active_team()  // RPT-04 team scope
    {
        var today = new DateOnly(2026, 6, 22);
        _users.Setup(r => r.GetActiveAsync()).ReturnsAsync(new[]
        {
            new User(1, "OnTeamNoLogs", null, true),
            new User(2, "OffTeamNoLogs", null, true)
        });
        _teams.Setup(r => r.GetUserIdsForTeamAsync(1)).ReturnsAsync(new[] { 1 }); // only user 1 is a member
        _logs.Setup(r => r.GetUserIdsWithLogsInRangeAsync(new DateOnly(2026, 6, 18), today))
             .ReturnsAsync(Array.Empty<int>());
        var svc = Make(today);

        var missing = await svc.GetUsersMissingLogsAsync(3);

        // Only the active-team member with no logs is flagged; the off-team user is ignored.
        Assert.Single(missing);
        Assert.Equal(1, missing[0].Id);
    }

    [Fact]
    public async Task GetUsersMissingLogs_returns_empty_when_no_team_resolved()  // RPT-04 teamId 0 == empty
    {
        _currentTeam.ActiveTeamId = 0;
        var svc = Make(new DateOnly(2026, 6, 22));

        var missing = await svc.GetUsersMissingLogsAsync(3);

        Assert.Empty(missing);
        _users.Verify(r => r.GetActiveAsync(), Times.Never); // short-circuits before touching repos
    }

    [Fact]
    public async Task ApplySmartInput_warns_when_rollback_journal_persists_after_bulk_write()  // XC-09
    {
        // Point DbPath at a temp file and leave a lingering "<db>-journal" so IsJournalGone is false.
        // Proves the bulk path CONSULTS SqliteMaintenance.IsJournalGone and surfaces (not swallows) it.
        var dir = Path.Combine(Path.GetTempPath(), "tslog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "timesheet.db");
        await File.WriteAllTextAsync(dbPath + "-journal", "interrupted");
        try
        {
            _config.SetupGet(c => c.DbPath).Returns(dbPath);
            _logs.Setup(r => r.GetByUserAndRangeAsync(1, It.IsAny<DateOnly>(), It.IsAny<DateOnly>()))
                 .ReturnsAsync(Array.Empty<TimeLog>());
            var svc = Make(Tue);

            var result = await svc.ApplySmartInputAsync(1, taskId: 5, new[] { new CellAssignment(Tue, 3m) });

            Assert.True(result.Ok);                                   // warning is non-fatal
            _logs.Verify(r => r.UpsertBatchAsync(It.IsAny<IReadOnlyList<TimeLog>>()), Times.Once);
            Assert.Single(_journal.Warnings);                         // XC-09 consulted + surfaced
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ApplySmartInput_does_not_warn_when_no_journal_after_bulk_write()  // XC-09 (clean path)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tslog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "timesheet.db");          // no "-journal" sibling
        try
        {
            _config.SetupGet(c => c.DbPath).Returns(dbPath);
            _logs.Setup(r => r.GetByUserAndRangeAsync(1, It.IsAny<DateOnly>(), It.IsAny<DateOnly>()))
                 .ReturnsAsync(Array.Empty<TimeLog>());
            var svc = Make(Tue);

            var result = await svc.ApplySmartInputAsync(1, taskId: 5, new[] { new CellAssignment(Tue, 3m) });

            Assert.True(result.Ok);
            Assert.Empty(_journal.Warnings);                         // journal gone => no warning
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- GetWeekGroupedAsync: every request becomes a group, incl. DEFAULT + empty ones (TS-01/02) ----

    private static Backlog Req(int id, string code, string project = "P") =>
        new(id, code, project, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task GetWeekGrouped_returns_one_group_per_request_with_tasks_under_correct_request()
    {
        var monday = new DateOnly(2026, 6, 15);
        _requests.Setup(r => r.SearchAsync(null, It.IsAny<IReadOnlyList<int>?>())).ReturnsAsync(new[]
        {
            Req(1, "DEFAULT"), Req(2, "REQ-001"), Req(3, "REQ-002")
        });
        _tasks.Setup(t => t.GetActiveForTimesheetAsync(It.IsAny<int?>())).ReturnsAsync(new[]
        {
            new TaskItem(10, 2, "Implement", 0, true),
            new TaskItem(11, 2, "Review", 1, true),
            new TaskItem(12, 1, "Annual Leave", 0, true)
        });
        _logs.Setup(l => l.GetByUserAndRangeAsync(1, monday, monday.AddDays(4)))
             .ReturnsAsync(new[] { new TimeLog(0, 1, 10, monday, 4m, DateTimeOffset.UtcNow) });
        var svc = Make(monday);

        var groups = await svc.GetWeekGroupedAsync(1, monday);

        // Ordered by backlog_code (Ordinal): DEFAULT, REQ-001, REQ-002.
        Assert.Equal(3, groups.Count);
        Assert.Equal("DEFAULT", groups[0].BacklogCode);
        Assert.Equal("REQ-001", groups[1].BacklogCode);
        Assert.Equal("REQ-002", groups[2].BacklogCode);

        // REQ-001 has both its tasks, with the logged 4h landing on Monday for task 10.
        Assert.Equal(2, groups[1].Tasks.Count);
        Assert.Equal("REQ-001", groups[1].Tasks[0].BacklogCode); // BacklogCode now populated (no longer "")
        Assert.Equal(4m, groups[1].Tasks[0].Mon);

        // DEFAULT carries its one task.
        Assert.Single(groups[0].Tasks);
        Assert.Equal("Annual Leave", groups[0].Tasks[0].TaskName);
    }

    [Fact]
    public async Task GetWeekGrouped_includes_request_with_no_tasks_as_empty_group()
    {
        var monday = new DateOnly(2026, 6, 15);
        _requests.Setup(r => r.SearchAsync(null, It.IsAny<IReadOnlyList<int>?>())).ReturnsAsync(new[]
        {
            Req(1, "DEFAULT"), Req(7, "REQ-EMPTY")
        });
        _tasks.Setup(t => t.GetActiveForTimesheetAsync(It.IsAny<int?>())).ReturnsAsync(Array.Empty<TaskItem>());
        _logs.Setup(l => l.GetByUserAndRangeAsync(1, monday, monday.AddDays(4)))
             .ReturnsAsync(Array.Empty<TimeLog>());
        var svc = Make(monday);

        var groups = await svc.GetWeekGroupedAsync(1, monday);

        var empty = Assert.Single(groups, g => g.BacklogCode == "REQ-EMPTY");
        Assert.Empty(empty.Tasks);
        Assert.Contains(groups, g => g.BacklogCode == "DEFAULT"); // DEFAULT still present
    }

    // ---- TM-06: the Log Work grid passes the ACTIVE team to the repo so only that team's tasks
    //      + backlogs are read (a team B task is never fetched when the active team is B vs A). ----
    [Fact]
    public async Task GetWeekGrouped_scopes_tasks_and_backlogs_to_active_team()
    {
        var monday = new DateOnly(2026, 6, 15);
        _currentTeam.ActiveTeamId = 7;
        _requests.Setup(r => r.SearchAsync(null, It.IsAny<IReadOnlyList<int>?>()))
                 .ReturnsAsync(Array.Empty<Backlog>());
        _tasks.Setup(t => t.GetActiveForTimesheetAsync(It.IsAny<int?>()))
              .ReturnsAsync(Array.Empty<TaskItem>());
        _logs.Setup(l => l.GetByUserAndRangeAsync(1, monday, monday.AddDays(4)))
             .ReturnsAsync(Array.Empty<TimeLog>());
        var svc = Make(monday);

        await svc.GetWeekGroupedAsync(1, monday);

        // Active team (7) is the only team passed to both the task and backlog reads.
        _tasks.Verify(t => t.GetActiveForTimesheetAsync(7), Times.Once);
        _requests.Verify(r => r.SearchAsync(null, It.Is<IReadOnlyList<int>?>(ts => ts != null && ts.Single() == 7)), Times.Once);
    }

    [Fact]
    public async Task GetWeek_passes_active_team_to_GetActiveForTimesheet()  // TM-06
    {
        var monday = new DateOnly(2026, 6, 15);
        _currentTeam.ActiveTeamId = 7;
        _logs.Setup(l => l.GetByUserAndRangeAsync(1, monday, monday.AddDays(4)))
             .ReturnsAsync(Array.Empty<TimeLog>());
        _tasks.Setup(t => t.GetActiveForTimesheetAsync(It.IsAny<int?>()))
              .ReturnsAsync(Array.Empty<TaskItem>());
        var svc = Make(monday);

        await svc.GetWeekAsync(1, monday);

        _tasks.Verify(t => t.GetActiveForTimesheetAsync(7), Times.Once);
    }

    [Fact]
    public async Task GetWeekGroupedAllUsers_scopes_export_rows_to_active_team()  // TM-06
    {
        var monday = new DateOnly(2026, 6, 15);
        _currentTeam.ActiveTeamId = 7;
        _requests.Setup(r => r.SearchAsync(null, It.IsAny<IReadOnlyList<int>?>()))
                 .ReturnsAsync(Array.Empty<Backlog>());
        _tasks.Setup(t => t.GetActiveForTimesheetAsync(It.IsAny<int?>()))
              .ReturnsAsync(Array.Empty<TaskItem>());
        _logs.Setup(l => l.GetExportRowsAsync(monday, monday.AddDays(4), null, It.IsAny<IReadOnlyList<int>?>()))
             .ReturnsAsync(Array.Empty<TimeLogReportRow>());
        var svc = Make(monday);

        await svc.GetWeekGroupedAllUsersAsync(monday);

        _logs.Verify(l => l.GetExportRowsAsync(monday, monday.AddDays(4), null,
            It.Is<IReadOnlyList<int>?>(ts => ts != null && ts.Single() == 7)), Times.Once);
    }
}
