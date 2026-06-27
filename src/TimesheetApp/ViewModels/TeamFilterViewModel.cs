using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

// P10 W7 (TM-07, architecture §5b). One checkable team in the shared multi-team filter.
public sealed partial class TeamCheckVm : ObservableObject
{
    private readonly Action _onChanged;

    public TeamCheckVm(Team team, bool isChecked, Action onChanged)
    {
        Team = team;
        _isChecked = isChecked;
        _onChanged = onChanged;
    }

    public Team Team { get; }
    public string Name => Team.Name;

    [ObservableProperty] private bool _isChecked;

    // Each checkbox toggle bubbles up so the owner VM can reload its data with the new CheckedTeamIds.
    partial void OnIsCheckedChanged(bool value) => _onChanged();
}

/// <summary>
/// Shared multi-team checkbox filter (TM-07). Seeded from <see cref="ICurrentTeamService.AvailableTeams"/>,
/// default = the active team only checked. Each of the four view screens (Backlog, Task List, Reports,
/// Daily Board) owns one instance and reloads its data on <see cref="SelectionChanged"/>. Subscribes to
/// <see cref="ICurrentTeamService.ActiveTeamChanged"/> → rebuilds the list and RESETS the selection to
/// {active team} (resolved decision F-Q3). Session persistence = lives with the owner VM instance.
/// Hidden in the UI when the user has ≤1 team (<see cref="ShowFilter"/>).
/// </summary>
public sealed partial class TeamFilterViewModel : ObservableObject
{
    private readonly ICurrentTeamService _currentTeam;

    // Guards the bulk rebuild/reset so the per-checkbox change handler does not fire SelectionChanged
    // once per team while we are re-seeding the list.
    private bool _suppressChange;

    public TeamFilterViewModel(ICurrentTeamService currentTeam)
    {
        _currentTeam = currentTeam;
        _currentTeam.ActiveTeamChanged += OnActiveTeamChanged;
        Reload();
    }

    public ObservableCollection<TeamCheckVm> Teams { get; } = new();

    /// Raised whenever the checked set changes (a checkbox toggle) so the owner VM reloads its data.
    public event EventHandler? SelectionChanged;

    /// The checked team ids. Empty list = no teams (teamId 0 == empty, R6 — never "all").
    public IReadOnlyList<int> CheckedTeamIds =>
        Teams.Where(t => t.IsChecked).Select(t => t.Team.Id).ToList();

    /// True when >1 team is checked — owners show a per-team chip/column only then.
    public bool ShowTeamColumn => CheckedTeamIds.Count > 1;

    /// Hide the whole control for single-team users (nothing to filter).
    public bool ShowFilter => _currentTeam.AvailableTeams.Count > 1;

    /// Compact "Teams" label for the dropdown header, e.g. "Teams (1)" / "Teams (2)".
    public string HeaderText => $"Teams ({CheckedTeamIds.Count})";

    // Rebuild the checkbox list from the current AvailableTeams, with only the active team checked.
    public void Reload()
    {
        _suppressChange = true;
        Teams.Clear();
        foreach (var t in _currentTeam.AvailableTeams)
            Teams.Add(new TeamCheckVm(t, t.Id == _currentTeam.ActiveTeamId, OnTeamToggled));
        _suppressChange = false;

        OnPropertyChanged(nameof(ShowFilter));
        OnPropertyChanged(nameof(ShowTeamColumn));
        OnPropertyChanged(nameof(HeaderText));
    }

    private void OnActiveTeamChanged(object? sender, EventArgs e)
    {
        // Switching the active team resets each screen's filter to {new active team} (spec §6 / F-Q3).
        Reload();
        // The owner reloads its data for the new {active team} set.
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTeamToggled()
    {
        if (_suppressChange) return;
        OnPropertyChanged(nameof(ShowTeamColumn));
        OnPropertyChanged(nameof(HeaderText));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
