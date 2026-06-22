using CommunityToolkit.Mvvm.Messaging;
using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

/// <summary>
/// Cross-tab live sync (WeakReferenceMessenger): a change made in one tab broadcasts a
/// DataChangedMessage that makes the relevant other tab reload — without switching tabs.
/// </summary>
public sealed class CrossTabSyncTests
{
    [Fact] // Creating a request/task in the Requests tab reloads the Timesheet grid live.
    public async Task RequestSave_reloads_Timesheet_via_messenger()
    {
        var bus = new WeakReferenceMessenger();

        var svc = new Mock<ITimeLogService>();
        var empty = new WeekGrid(new DateOnly(2026, 6, 15), System.Array.Empty<WeekRow>());
        var withTask = new WeekGrid(new DateOnly(2026, 6, 15),
            new[] { new WeekRow(5, "R1", "New Task", 0, null, null, null, null, null) });
        svc.SetupSequence(s => s.GetWeekAsync(It.IsAny<int>(), It.IsAny<DateOnly>()))
            .ReturnsAsync(empty)     // initial load
            .ReturnsAsync(withTask); // reload triggered by the broadcast

        var timesheet = new TimesheetViewModel(
            svc.Object, Mock.Of<ISmartInputService>(), Mock.Of<IClock>(), () => 1, bus);
        await timesheet.LoadCommand.ExecuteAsync(null);
        Assert.Empty(timesheet.Rows);

        var reqRepo = new Mock<IRequestRepository>();
        reqRepo.Setup(r => r.InsertAsync(It.IsAny<Request>())).ReturnsAsync(10);
        reqRepo.Setup(r => r.SearchAsync(It.IsAny<string?>())).ReturnsAsync(System.Array.Empty<Request>());
        var requests = new RequestsViewModel(
            reqRepo.Object, Mock.Of<ITaskRepository>(), Mock.Of<ITaskTemplateRepository>(), bus);
        requests.Editor = RequestEditorViewModel.ForCreate(System.Array.Empty<TaskTemplate>());
        requests.Editor.RequestCode = "R1";
        requests.Editor.Project = "P";

        await requests.SaveNewCommand.ExecuteAsync(null);

        // The broadcast reloaded the timesheet grid live (no tab switch).
        Assert.Single(timesheet.Rows);
        Assert.Equal("New Task", timesheet.Rows[0].TaskName);
    }

    [Fact] // Adding a user broadcasts -> Reports "chưa log" banner reloads live.
    public async Task UserAdd_reloads_Reports_banner_via_messenger()
    {
        var bus = new WeakReferenceMessenger();

        var svc = new Mock<ITimeLogService>();
        svc.Setup(s => s.GetUsersMissingLogsAsync(It.IsAny<int>()))
            .ReturnsAsync(new[] { new User(1, "Ann", null, true) });
        var settings = new Mock<ISettingsRepository>();
        settings.Setup(s => s.GetAsync(ReportsViewModel.NDaysKey)).ReturnsAsync("3");

        var reports = new ReportsViewModel(
            Mock.Of<ITimeLogRepository>(), svc.Object, settings.Object, Mock.Of<IClock>(),
            Mock.Of<IReportAggregator>(), bus);
        Assert.Equal(string.Empty, reports.BannerText);

        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(u => u.InsertAsync(It.IsAny<User>())).ReturnsAsync(2);
        userRepo.Setup(u => u.GetAllAsync()).ReturnsAsync(System.Array.Empty<User>());
        var users = new UsersViewModel(userRepo.Object, bus) { NewUserName = "Bob" };

        await users.AddUserCommand.ExecuteAsync(null);

        Assert.Contains("Ann", reports.BannerText); // banner reloaded live
    }
}
