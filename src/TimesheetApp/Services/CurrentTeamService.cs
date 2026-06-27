using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Holds the active-team context for the resolved current user (TM-05). Resolves the
/// persisted ActiveTeamId if it is still one of the user's active memberships, else falls back to
/// the first available team (or 0 when the user is in zero teams — R5/F-Q8). Never throws on a
/// stale/deleted id.</summary>
public sealed class CurrentTeamService : ICurrentTeamService
{
    private readonly ITeamRepository _teams;
    private readonly IAppConfig _config;
    private readonly IMessenger _messenger;

    private IReadOnlyList<Team> _available = Array.Empty<Team>();

    public CurrentTeamService(ITeamRepository teams, IAppConfig config, IMessenger messenger)
    {
        _teams = teams;
        _config = config;
        _messenger = messenger;
    }

    public int ActiveTeamId { get; private set; }
    public Team? ActiveTeam => _available.FirstOrDefault(t => t.Id == ActiveTeamId);
    public IReadOnlyList<Team> AvailableTeams => _available;

    public event EventHandler? ActiveTeamChanged;

    public async Task InitializeAsync(int currentUserId)
    {
        // AvailableTeams = the user's memberships ∩ active teams (only active teams are selectable).
        var memberOf = (await _teams.GetTeamIdsForUserAsync(currentUserId)).ToHashSet();
        _available = (await _teams.GetActiveAsync())
            .Where(t => memberOf.Contains(t.Id))
            .ToList();

        // Resolve: persisted id if still available, else first available, else 0 (zero-team edge).
        var persisted = _config.ActiveTeamId;
        ActiveTeamId = _available.Any(t => t.Id == persisted)
            ? persisted
            : (_available.Count > 0 ? _available[0].Id : 0);
    }

    public async Task SetActiveTeamAsync(int teamId)
    {
        if (!_available.Any(t => t.Id == teamId))
            throw new InvalidOperationException(
                $"Team {teamId} is not one of the current user's available teams.");

        ActiveTeamId = teamId;
        _config.SetActiveTeamId(teamId);
        ActiveTeamChanged?.Invoke(this, EventArgs.Empty);
        _messenger.Send(new DataChangedMessage(DataKind.Teams));
        await Task.CompletedTask;
    }
}
