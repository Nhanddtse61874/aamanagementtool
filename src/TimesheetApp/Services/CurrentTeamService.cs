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
    private int _currentUserId;        // last-known user from InitializeAsync (for live re-resolve, I3)
    private bool _initialized;         // ignore broadcasts until the first InitializeAsync
    private bool _suppressReentry;     // SetActiveTeamAsync sends DataKind.Teams -> don't re-resolve self

    public CurrentTeamService(ITeamRepository teams, IAppConfig config, IMessenger messenger)
    {
        _teams = teams;
        _config = config;
        _messenger = messenger;

        // Live refresh: a team create/rename/deactivate/membership change elsewhere broadcasts
        // DataKind.Teams. Re-resolve AvailableTeams for the current user and fall the active team back
        // if it is no longer valid, so the switcher + the 4 TeamFilters update without a restart (I3).
        _messenger.Register<CurrentTeamService, DataChangedMessage>(this, static (s, m) =>
        {
            if (m.Kind == DataKind.Teams)
                _ = s.OnTeamsChangedAsync();
        });
    }

    public int ActiveTeamId { get; private set; }
    public Team? ActiveTeam => _available.FirstOrDefault(t => t.Id == ActiveTeamId);
    public IReadOnlyList<Team> AvailableTeams => _available;

    public event EventHandler? ActiveTeamChanged;

    public async Task InitializeAsync(int currentUserId)
    {
        _currentUserId = currentUserId;
        _initialized = true;

        await ResolveAvailableAsync(currentUserId);

        // Resolve: persisted id if still available, else first available, else 0 (zero-team edge).
        var persisted = _config.ActiveTeamId;
        ActiveTeamId = _available.Any(t => t.Id == persisted)
            ? persisted
            : (_available.Count > 0 ? _available[0].Id : 0);

        // The per-screen TeamFilters + the sidebar switcher were constructed BEFORE this async resolve
        // (AvailableTeams was empty then), so announce the now-resolved context so they rebuild and
        // default to {active team}. Without this they stay seeded from the empty set → "Teams (0)" →
        // every team-filtered grid (Task List, Backlog list, …) renders empty.
        ActiveTeamChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetActiveTeamAsync(int teamId)
    {
        if (!_available.Any(t => t.Id == teamId))
            throw new InvalidOperationException(
                $"Team {teamId} is not one of the current user's available teams.");

        ActiveTeamId = teamId;
        _config.SetActiveTeamId(teamId);
        ActiveTeamChanged?.Invoke(this, EventArgs.Empty);

        // Broadcast the change without re-resolving ourselves on the echo (feedback-loop guard, I3).
        _suppressReentry = true;
        try { _messenger.Send(new DataChangedMessage(DataKind.Teams)); }
        finally { _suppressReentry = false; }
        await Task.CompletedTask;
    }

    // Re-resolve AvailableTeams on a DataKind.Teams broadcast. Only changes the active team when the
    // current one is no longer valid (fall back to first available, else 0) — an unchanged active team
    // stays put and fires no event (avoids churning working-scope VMs). Idempotent. (I3)
    internal async Task OnTeamsChangedAsync()
    {
        if (!_initialized || _suppressReentry) return;

        await ResolveAvailableAsync(_currentUserId);

        if (_available.Any(t => t.Id == ActiveTeamId)) return;   // still valid -> nothing to do

        ActiveTeamId = _available.Count > 0 ? _available[0].Id : 0;
        _config.SetActiveTeamId(ActiveTeamId);
        ActiveTeamChanged?.Invoke(this, EventArgs.Empty);
    }

    // AvailableTeams = the user's memberships ∩ active teams (only active teams are selectable).
    private async Task ResolveAvailableAsync(int userId)
    {
        var memberOf = (await _teams.GetTeamIdsForUserAsync(userId)).ToHashSet();
        _available = (await _teams.GetActiveAsync())
            .Where(t => memberOf.Contains(t.Id))
            .ToList();
    }
}
