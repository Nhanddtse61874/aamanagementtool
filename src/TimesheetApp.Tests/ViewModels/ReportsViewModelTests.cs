using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public class ReportsViewModelTests
{
    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; }
        public DateTimeOffset UtcNow => new(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    private static (ReportsViewModel vm,
                    Mock<ITimeLogRepository> repo,
                    Mock<ITimeLogService> svc,
                    Mock<ISettingsRepository> settings,
                    Mock<IUserRepository> users)
        Build(DateOnly today)
    {
        var repo = new Mock<ITimeLogRepository>();
        var svc = new Mock<ITimeLogService>();
        var settings = new Mock<ISettingsRepository>();
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetActiveAsync()).ReturnsAsync(System.Array.Empty<User>());
        var clock = new FakeClock { Today = today };
        var vm = new ReportsViewModel(repo.Object, svc.Object, settings.Object, users.Object, clock, new ReportAggregator());
        return (vm, repo, svc, settings, users);
    }

    private static TimeLogReportRow Row(string proj, string code, int taskId, string task, string date, decimal h) =>
        new(1, "A", code, proj, taskId, task, DateOnly.Parse(date), h);

    [Fact]
    public void Ctor_defaults_week_to_monday_of_today_and_month_to_first()
    {
        // 2026-06-18 is a Thursday -> Monday is 2026-06-15
        var (vm, _, _, _, _) = Build(new DateOnly(2026, 6, 18));
        Assert.Equal(new DateOnly(2026, 6, 15), vm.SelectedWeekMonday);
        Assert.Equal(new DateOnly(2026, 6, 1), vm.SelectedMonth);
    }

    // RPT-01
    [Fact]
    public async Task LoadWeekly_queries_monday_to_friday_and_fills_rows()
    {
        var (vm, repo, _, _, _) = Build(new DateOnly(2026, 6, 18));
        vm.SelectedTarget = new ReportsViewModel.ReportTarget(7, "Greg");
        vm.SelectedWeekMonday = new DateOnly(2026, 6, 15);
        repo.Setup(r => r.GetReportRowsAsync(7, new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 19)))
            .ReturnsAsync(new[] { Row("P", "R1", 1, "T", "2026-06-15", 4m), Row("P", "R1", 2, "U", "2026-06-15", 2m) });

        await vm.LoadWeeklyAsync();

        var day = Assert.Single(vm.WeeklyRows);
        Assert.Equal(new DateOnly(2026, 6, 15), day.Date);
        Assert.Equal(6m, day.TotalHours);
        repo.Verify(r => r.GetReportRowsAsync(7, new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 19)), Times.Once);
    }

    // RPT-02 + RPT-03
    [Fact]
    public async Task LoadMonthly_queries_full_month_and_fills_monthly_rows_and_tree()
    {
        var (vm, repo, _, _, _) = Build(new DateOnly(2026, 6, 18));
        vm.SelectedTarget = new ReportsViewModel.ReportTarget(7, "Greg");
        vm.SelectedMonth = new DateOnly(2026, 6, 1);
        repo.Setup(r => r.GetReportRowsAsync(7, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30)))
            .ReturnsAsync(new[]
            {
                Row("ProjX", "REQ-001", 10, "Build", "2026-06-16", 4m),
                Row("ProjX", "REQ-001", 11, "Audit", "2026-06-17", 2m),
            });

        await vm.LoadMonthlyAsync();

        Assert.Equal(2, vm.MonthlyRows.Count);
        var proj = Assert.Single(vm.ProjectTree);
        Assert.Equal("ProjX", proj.Project);
        Assert.Equal(6m, proj.TotalHours);
        repo.Verify(r => r.GetReportRowsAsync(7, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30)), Times.Once);
    }

    // RPT-04 — banner shows the CONFIGURED N (not the actual gap), and passes that N to the service.
    [Fact]
    public async Task LoadBanner_uses_configured_N_and_shows_it_in_text()
    {
        var (vm, _, svc, settings, _) = Build(new DateOnly(2026, 6, 18));
        settings.Setup(s => s.GetAsync(ReportsViewModel.NDaysKey)).ReturnsAsync("5");
        svc.Setup(s => s.GetUsersMissingLogsAsync(5))
           .ReturnsAsync(new[] { new User(2, "Bob", null, true) });

        await vm.LoadBannerAsync();

        var w = Assert.Single(vm.MissingBanner);
        Assert.Equal("Bob", w.UserName);
        Assert.Equal("Bob has not logged in 5 days", vm.BannerText); // shows configured N=5
        svc.Verify(s => s.GetUsersMissingLogsAsync(5), Times.Once);
    }

    // RPT-04 / SET-02 — missing/blank setting falls back to default N=3.
    [Fact]
    public async Task LoadBanner_defaults_N_to_3_when_setting_absent()
    {
        var (vm, _, svc, settings, _) = Build(new DateOnly(2026, 6, 18));
        settings.Setup(s => s.GetAsync(ReportsViewModel.NDaysKey)).ReturnsAsync((string?)null);
        svc.Setup(s => s.GetUsersMissingLogsAsync(3))
           .ReturnsAsync(new[] { new User(2, "Bob", null, true) });

        await vm.LoadBannerAsync();

        Assert.Equal("Bob has not logged in 3 days", vm.BannerText);
        svc.Verify(s => s.GetUsersMissingLogsAsync(3), Times.Once);
    }

    [Fact]
    public async Task LoadBanner_empty_when_no_user_missing()
    {
        var (vm, _, svc, settings, _) = Build(new DateOnly(2026, 6, 18));
        settings.Setup(s => s.GetAsync(ReportsViewModel.NDaysKey)).ReturnsAsync("3");
        svc.Setup(s => s.GetUsersMissingLogsAsync(3)).ReturnsAsync(System.Array.Empty<User>());

        await vm.LoadBannerAsync();

        Assert.Empty(vm.MissingBanner);
        Assert.Equal(string.Empty, vm.BannerText);
    }

    // Dropdown: a leading "whole team" option (UserId 0) followed by one entry per active user (by name).
    [Fact]
    public async Task LoadUsers_builds_targets_with_team_option_then_one_per_active_user()
    {
        var (vm, _, _, _, users) = Build(new DateOnly(2026, 6, 18));
        users.Setup(u => u.GetActiveAsync())
             .ReturnsAsync(new[] { new User(3, "Cara", null, true), new User(5, "Dan", null, true) });

        await vm.LoadUsersAsync();

        Assert.Equal(3, vm.Targets.Count);
        Assert.Equal(0, vm.Targets[0].UserId); // leading team option
        Assert.Equal(new[] { "Whole team (all)", "Cara", "Dan" }, vm.Targets.Select(t => t.Display).ToArray());
        Assert.Equal(new[] { 0, 3, 5 }, vm.Targets.Select(t => t.UserId).ToArray());
        Assert.Equal(0, vm.SelectedTarget!.UserId); // defaults to team
    }

    // Team selected (UserId 0) -> all-users export query is used, not the per-user report query.
    [Fact]
    public async Task LoadWeekly_team_uses_export_rows_for_all_users()
    {
        var (vm, repo, _, _, _) = Build(new DateOnly(2026, 6, 18));
        vm.SelectedTarget = new ReportsViewModel.ReportTarget(0, "Whole team (all)");
        vm.SelectedWeekMonday = new DateOnly(2026, 6, 15);
        repo.Setup(r => r.GetExportRowsAsync(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 19), null))
            .ReturnsAsync(new[] { Row("P", "R1", 1, "T", "2026-06-15", 4m) });

        await vm.LoadWeeklyAsync();

        Assert.Single(vm.WeeklyRows);
        repo.Verify(r => r.GetExportRowsAsync(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 19), null), Times.Once);
        repo.Verify(r => r.GetReportRowsAsync(It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>()), Times.Never);
    }

    [Fact]
    public async Task LoadMonthly_team_uses_export_rows_for_all_users()
    {
        var (vm, repo, _, _, _) = Build(new DateOnly(2026, 6, 18));
        vm.SelectedTarget = new ReportsViewModel.ReportTarget(0, "Whole team (all)");
        vm.SelectedMonth = new DateOnly(2026, 6, 1);
        repo.Setup(r => r.GetExportRowsAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), null))
            .ReturnsAsync(new[] { Row("ProjX", "REQ-001", 10, "Build", "2026-06-16", 4m) });

        await vm.LoadMonthlyAsync();

        Assert.Single(vm.MonthlyRows);
        repo.Verify(r => r.GetExportRowsAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), null), Times.Once);
        repo.Verify(r => r.GetReportRowsAsync(It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>()), Times.Never);
    }
}
