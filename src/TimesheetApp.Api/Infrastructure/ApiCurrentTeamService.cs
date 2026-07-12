using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.Api.Infrastructure;

/// <summary>The server-side <see cref="ICurrentTeamService"/>. SCOPED — one per request.
///
/// <para><b>Why this exists at all.</b> <c>ICurrentTeamService</c> lives in Core and is a hard dependency
/// of both <c>TimeLogService</c> and <c>StandupService</c>, but its only implementation
/// (<c>TimesheetApp/Services/CurrentTeamService.cs</c>) is in the WPF project — <c>net8.0-windows</c>, and
/// its constructor takes CommunityToolkit's <c>IMessenger</c>. A <c>net8.0</c> API can reference neither.
/// So the API needs its own, backed by <c>IUserRepository.GetActiveTeamIdAsync</c> /
/// <c>SetActiveTeamIdAsync</c> (the active team is per-USER, in <c>Users.active_team_id</c> — M8.2/W4 moved
/// it off <c>IAppConfig</c> precisely because a per-PROCESS active team lets one user's switch re-scope
/// another user's next request).</para>
///
/// <para><b>Resolution mirrors the WPF service exactly:</b> the persisted active team if it is still one of
/// the user's active memberships, else the first available, else 0 (a user in zero teams). Never throws on a
/// stale or deleted persisted id.</para>
///
/// <para><b><see cref="ActiveTeamChanged"/> is a deliberate no-op here.</b> On the desktop it tells the
/// working-scope view-models to reload. In the API a team switch is PER-USER state: raising it — or letting
/// it reach the SignalR team group — would push one user's private scope change at everyone else in the
/// team. The accessors are written out by hand rather than left as an auto-event so the compiler does not
/// warn (CS0067) about an event that is never raised: never raising it IS the contract.</para></summary>
public sealed class ApiCurrentTeamService : ICurrentTeamService
{
    private readonly ITeamRepository _teams;
    private readonly IUserRepository _users;

    private IReadOnlyList<Team> _available = Array.Empty<Team>();
    private int _currentUserId;

    public ApiCurrentTeamService(ITeamRepository teams, IUserRepository users)
    {
        _teams = teams;
        _users = users;
    }

    public int ActiveTeamId { get; private set; }
    public Team? ActiveTeam => _available.FirstOrDefault(t => t.Id == ActiveTeamId);
    public IReadOnlyList<Team> AvailableTeams => _available;

    /// <summary>Never raised — see the type remarks. A per-user team switch must not broadcast.</summary>
    public event EventHandler? ActiveTeamChanged
    {
        add { }
        remove { }
    }

    /// <summary>Called once per authenticated request by <see cref="ClientContextFilter"/>, immediately
    /// after the user resolves. This is NOT optional: <see cref="ActiveTeamId"/> is a synchronous property
    /// whose resolution needs an async database read, so it cannot resolve lazily. Skip this and it stays
    /// 0 — and <c>GetActiveForTimesheetAsync(0)</c> returns no tasks (R6), so every timesheet and standup
    /// endpoint silently returns empty while <c>StandupService.AddEntryAsync</c> writes <c>TeamId: 0</c>.</summary>
    public async Task InitializeAsync(int currentUserId)
    {
        _currentUserId = currentUserId;

        var memberOf = (await _teams.GetTeamIdsForUserAsync(currentUserId)).ToHashSet();
        _available = (await _teams.GetActiveAsync())
            .Where(t => memberOf.Contains(t.Id))
            .ToList();

        var persisted = await _users.GetActiveTeamIdAsync(currentUserId);
        ActiveTeamId = _available.Any(t => t.Id == persisted)
            ? persisted
            : (_available.Count > 0 ? _available[0].Id : 0);
    }

    /// <summary>Persists the switch to THIS user's row (bump-only — a system write carrying no
    /// client-supplied version). Rejects a team the user is not a member of, which is what stops a raw
    /// <c>teamId</c> on the wire from re-scoping the request to someone else's team.</summary>
    public async Task SetActiveTeamAsync(int teamId)
    {
        if (!_available.Any(t => t.Id == teamId))
            throw new InvalidOperationException(
                $"Team {teamId} is not one of the current user's available teams.");

        ActiveTeamId = teamId;
        await _users.SetActiveTeamIdAsync(_currentUserId, teamId);
        // No ActiveTeamChanged, and no DataChangedMessage: per-user state, not a team-wide event.
    }
}
