using Xunit;
using TimesheetApp.Models;

namespace TimesheetApp.Tests.Models;

public class EntitiesTests
{
    [Fact]
    public void TimeLog_Carries_DateOnly_WorkDate_And_Decimal_Hours()
    {
        var log = new TimeLog(
            Id: 1, UserId: 2, TaskId: 3,
            WorkDate: new DateOnly(2026, 6, 21),
            Hours: 7.5m,
            CreatedAt: new DateTimeOffset(2026, 6, 21, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 6, 21), log.WorkDate);
        Assert.Equal(7.5m, log.Hours);
    }

    [Fact]
    public void User_WindowsUsername_Is_Nullable_And_IsActive_Tracked()
    {
        var user = new User(Id: 1, Name: "Anh", WindowsUsername: null, IsActive: true);
        Assert.Null(user.WindowsUsername);
        Assert.True(user.IsActive);
    }

    [Fact]
    public void TaskItem_Has_BacklogId_OrderIndex_IsActive()
    {
        var task = new TaskItem(Id: 1, BacklogId: 9, TaskName: "Annual Leave", OrderIndex: 0, IsActive: true);
        Assert.Equal(9, task.BacklogId);
        Assert.Equal("Annual Leave", task.TaskName);
        Assert.Equal(0, task.OrderIndex);
    }

    [Fact]
    public void Request_Has_Code_Project_CreatedAt_No_IsActive_Member()
    {
        var request = new Backlog(Id: 1, BacklogCode: "DEFAULT", Project: "DEFAULT",
            CreatedAt: DateTimeOffset.UnixEpoch);
        Assert.Equal("DEFAULT", request.BacklogCode);
        // Backlogs are NOT soft-deletable in v1 (DATA-02 decision 4) -> no IsActive property.
        Assert.Null(typeof(Backlog).GetProperty("IsActive"));
    }

    [Fact]
    public void DefaultTask_And_TaskTemplate_Shapes()
    {
        var dt = new DefaultTask(Id: 1, TaskName: "Meeting", OrderIndex: 1, IsActive: true);
        var tpl = new TaskTemplate(Id: 1, TemplateName: "Std", TaskName: "Dev", OrderIndex: 0);
        Assert.Equal("Meeting", dt.TaskName);
        Assert.Equal("Std", tpl.TemplateName);
    }
}
