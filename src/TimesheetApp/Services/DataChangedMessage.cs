namespace TimesheetApp.Services;

/// <summary>What kind of shared data changed, so subscribers reload only what they need.</summary>
public enum DataKind
{
    Backlogs,
    Tasks,        // a task was added/edited/soft-deleted (affects the Timesheet grid rows)
    Users,        // a user was added/soft-deleted
    Logs,         // time logs changed (affects Reports)
    Templates,    // task templates changed
    DefaultTasks, // default tasks synced into the DEFAULT backlog (affects Timesheet rows)
    Standup,      // daily-report standup entries/issues changed (affects the Daily Report board)
    Tags,         // P8: user-defined tags changed (affects backlog editor + Task List chips)
    PcaContacts,  // P8: external (PCA) contacts changed (affects backlog editor combo)
    Holidays      // P8: holiday calendar changed (affects working-day math + Task List/Gantt)
}

/// <summary>
/// Broadcast over CommunityToolkit's WeakReferenceMessenger when shared data changes in one tab,
/// so other tabs refresh live without the user switching to them (cross-tab sync).
/// </summary>
public sealed record DataChangedMessage(DataKind Kind);
