namespace TimesheetApp.Services;

// P20: "Continue on next month" — copies a backlog into targetPeriod ("yyyy-MM") with Type="Continue",
// including its tags and its not-Done tasks (with their type/assignee + tags), keeping the source's
// progress. The source is left untouched. Blocked (returns 0) when a backlog with the same code already
// exists in targetPeriod for that backlog's team.
public interface IBacklogContinuationService
{
    Task<int> ContinueAsync(int backlogId, string targetPeriod);
}
