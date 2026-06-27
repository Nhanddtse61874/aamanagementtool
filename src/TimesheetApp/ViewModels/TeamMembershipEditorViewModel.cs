namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TimesheetApp.Models;

// P10 Multi-Team (TM-03). Working-set for editing one team's membership, shown in the Settings overlay.
// Mirrors the BacklogEditorViewModel tag multi-select: a checkbox list of all active users whose
// checked set is replace-all-saved via ITeamRepository.SetMembersAsync. Built via the ForTeam factory.
public sealed partial class TeamMembershipEditorViewModel : ObservableObject
{
    private TeamMembershipEditorViewModel() { }

    // The team being edited (id used for SetMembersAsync; name shown in the overlay header).
    public int TeamId { get; private init; }
    public string TeamName { get; private init; } = string.Empty;

    public ObservableCollection<UserCheckVm> Users { get; } = new();

    // The checked user ids — the replace-all member set saved on Save.
    public IReadOnlyList<int> CheckedUserIds =>
        Users.Where(u => u.IsChecked).Select(u => u.User.Id).ToList();

    // Seed the checklist from all active users, pre-checking the team's current members.
    public static TeamMembershipEditorViewModel ForTeam(
        Team team, IReadOnlyList<User> activeUsers, IReadOnlyList<int> memberUserIds)
    {
        var members = new HashSet<int>(memberUserIds);
        var vm = new TeamMembershipEditorViewModel { TeamId = team.Id, TeamName = team.Name };
        foreach (var u in activeUsers)
            vm.Users.Add(new UserCheckVm(u, members.Contains(u.Id)));
        return vm;
    }
}

// One user in the membership checklist; IsChecked drives SetMembersAsync on save.
public sealed partial class UserCheckVm : ObservableObject
{
    public UserCheckVm(User user, bool isChecked)
    {
        User = user;
        _isChecked = isChecked;
    }

    public User User { get; }
    public string Name => User.Name;
    [ObservableProperty] private bool _isChecked;
}
