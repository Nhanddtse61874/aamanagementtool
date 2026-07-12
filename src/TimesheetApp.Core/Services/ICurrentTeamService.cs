using TimesheetApp.Models;

namespace TimesheetApp.Services;

// P10 Multi-Team (TM-05). The active-team context, mirroring ICurrentUserService but a superset:
// it also exposes the current user's AvailableTeams + an ActiveTeamChanged event + InitializeAsync.
// Services that need the team id inject this directly (NOT a Func<int> — that would clobber the
// existing user-id provider). Singleton.
public interface ICurrentTeamService
{
    int ActiveTeamId { get; }                    // 0 until resolved (and when the user has no teams)
    Team? ActiveTeam { get; }                    // null until resolved / when in zero teams
    IReadOnlyList<Team> AvailableTeams { get; }  // the current user's ACTIVE memberships (switcher source)

    event EventHandler? ActiveTeamChanged;       // working-scope VMs reload on change

    // Load AvailableTeams for the current user and resolve the active team. Runs AFTER current-user
    // resolution (needs the user id). Never throws on a stale/deleted persisted id (R5).
    Task InitializeAsync(int currentUserId);

    // Persist + raise ActiveTeamChanged + broadcast DataChangedMessage(DataKind.Teams).
    Task SetActiveTeamAsync(int teamId);
}
