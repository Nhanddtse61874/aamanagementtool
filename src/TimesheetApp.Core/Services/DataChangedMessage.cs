namespace TimesheetApp.Services;

/// <summary>What kind of shared data changed, so subscribers reload only what they need.</summary>
/// <summary>
/// 🔴 THESE ORDINALS ARE A WIRE CONTRACT. DO NOT REORDER. DO NOT INSERT IN THE MIDDLE. APPEND ONLY.
///
/// SignalR serialises this enum as an INTEGER, not a string. `Program.cs` calls `AddSignalR()` with no
/// `.AddJsonProtocol(...)`, there is no `[JsonConverter]` here, and `JsonStringEnumConverter` appears
/// nowhere in the repo -- so System.Text.Json emits the ordinal. The hub sends `DataChanged(3, 42)`,
/// never `DataChanged("Logs", 42)`.
///
/// The Angular client mirrors these numbers in `core/realtime.service.ts` (`DataKind`), and a test pins
/// them. But that test lives on the OTHER SIDE OF THE WIRE: nothing here can see it, and nothing there
/// can see this. Before the values were explicit, reordering this enum would have SILENTLY re-pointed
/// every TypeScript constant at the wrong event -- a filter that matches nothing, compiling clean, on a
/// feed whose entire purpose is filtering. The explicit values are what make that impossible.
///
/// (M9/P6 found this. The plan had told the agent `kind` was a STRING. Writing `if (e.kind === 'Logs')`
/// would have type-checked under `strict` and matched nothing, forever.)
/// </summary>
public enum DataKind
{
    Backlogs = 0,
    Tasks = 1,        // a task was added/edited/soft-deleted (affects the Timesheet grid rows)
    Users = 2,        // a user was added/soft-deleted
    Logs = 3,         // time logs changed (affects Reports)
    Templates = 4,    // task templates changed
    DefaultTasks = 5, // default tasks synced into the DEFAULT backlog (affects Timesheet rows)
    Standup = 6,      // daily-report standup entries/issues changed (affects the Daily Report board)
    Tags = 7,         // P8: user-defined tags changed (affects backlog editor + Task List chips)
    PcaContacts = 8,  // P8: external (PCA) contacts changed (affects backlog editor combo)
    Holidays = 9,     // P8: holiday calendar changed (affects working-day math + Task List/Gantt)
    Teams = 10        // P10: teams / membership / active-team changed (affects switcher, filters, working scope)
}

/// <summary>
/// Broadcast over CommunityToolkit's WeakReferenceMessenger when shared data changes in one tab,
/// so other tabs refresh live without the user switching to them (cross-tab sync).
/// </summary>
public sealed record DataChangedMessage(DataKind Kind);
