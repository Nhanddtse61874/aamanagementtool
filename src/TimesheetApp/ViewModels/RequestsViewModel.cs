namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

public sealed partial class BacklogsViewModel : ObservableObject
{
    private readonly IBacklogRepository _backlogs;
    private readonly ITaskRepository _tasks;
    private readonly ITaskTemplateRepository _templates;
    private readonly IMessenger _messenger;
    private readonly ICurrentUserService? _currentUser;
    private readonly IUserRepository? _users;   // v4: assignee pick list + name resolution
    private readonly IPcaContactRepository? _pcaContacts;  // v7: PCA pick list
    private readonly ITagRepository? _tagsRepo;            // v7: tag multi-select
    private readonly ICurrentTeamService? _currentTeam;    // v8 (P10): active team + multi-team filter

    public BacklogsViewModel(
        IBacklogRepository backlogs, ITaskRepository tasks, ITaskTemplateRepository templates,
        IMessenger? messenger = null, ICurrentUserService? currentUser = null,
        IUserRepository? users = null,
        IPcaContactRepository? pcaContacts = null, ITagRepository? tagsRepo = null,
        ICurrentTeamService? currentTeam = null)
    {
        _backlogs = backlogs;
        _tasks = tasks;
        _templates = templates;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _currentUser = currentUser;
        _users = users;
        _pcaContacts = pcaContacts;
        _tagsRepo = tagsRepo;
        _currentTeam = currentTeam;

        // P10 (TM-07): the multi-team checkbox filter. Reloads the list on a selection/active-team change.
        if (_currentTeam is not null)
        {
            TeamFilter = new TeamFilterViewModel(_currentTeam);
            TeamFilter.SelectionChanged += (_, _) => _ = RefreshAsync();
        }
    }

    // P10: the shared multi-team filter (null when no current-team service is wired — legacy ctor / tests).
    public TeamFilterViewModel? TeamFilter { get; }

    // List row = the Backlog plus its active-task count + v2 period/type + v4 assignee (grid columns).
    // v8 (P10): TeamName + ShowTeam drive a team chip shown only when >1 team is checked.
    public sealed record BacklogListItem(
        int Id, string BacklogCode, string Project, int TaskCount, string? PeriodMonth, string? Type,
        string? AssigneeName, string? TeamName = null, bool ShowTeam = false);

    private const string AllOption = "All";

    // The full loaded set; Backlogs is the filtered view shown in the grid.
    private List<BacklogListItem> _all = new();
    public ObservableCollection<BacklogListItem> Backlogs { get; } = new();

    [ObservableProperty] private string? _searchTerm;
    [ObservableProperty] private BacklogEditorViewModel? _editor;

    // Structured filters (live, in-memory). "All" = no filter. Option lists are rebuilt from the data.
    [ObservableProperty] private string _filterProject = AllOption;
    [ObservableProperty] private string _filterType = AllOption;
    [ObservableProperty] private string _filterAssignee = AllOption;
    [ObservableProperty] private string _filterMonth = AllOption;

    public ObservableCollection<string> ProjectOptions { get; } = new() { AllOption };
    public ObservableCollection<string> TypeOptions { get; } = new() { AllOption };
    public ObservableCollection<string> AssigneeOptions { get; } = new() { AllOption };
    public ObservableCollection<string> MonthOptions { get; } = new() { AllOption };

    // All filters (incl. the search box) re-filter the loaded list live — no DB round-trip per keystroke.
    partial void OnSearchTermChanged(string? value) => ApplyFilters();
    partial void OnFilterProjectChanged(string value) => ApplyFilters();
    partial void OnFilterTypeChanged(string value) => ApplyFilters();
    partial void OnFilterAssigneeChanged(string value) => ApplyFilters();
    partial void OnFilterMonthChanged(string value) => ApplyFilters();

    public async Task LoadAsync() => await RefreshAsync();

    // Reloads the full set from the DB, rebuilds the filter option lists, then applies the live filters.
    [RelayCommand]
    public async Task RefreshAsync()
    {
        // P10 (TM-07): scope the list to the checked teams (null = all teams = legacy/no-filter behavior).
        var teamIds = TeamFilter?.CheckedTeamIds;
        var rows = await _backlogs.SearchAsync(null, teamIds);

        // v4: id -> name map (GetAll so a deactivated assignee still resolves) for the Assignee column/filter.
        var names = _users is null
            ? new Dictionary<int, string>()
            : (await _users.GetAllAsync()).ToDictionary(u => u.Id, u => u.Name);

        // P10: id -> team name (from the user's available teams) + chip visibility when >1 team is checked.
        var teamNames = _currentTeam?.AvailableTeams.ToDictionary(t => t.Id, t => t.Name)
                        ?? new Dictionary<int, string>();
        var showTeam = TeamFilter?.ShowTeamColumn ?? false;

        var backlogIds = rows.Select(r => r.Id).ToList();
        var tasksByBacklog = await _tasks.GetActiveByBacklogsAsync(backlogIds);

        var items = new List<BacklogListItem>();
        foreach (var r in rows)
        {
            tasksByBacklog.TryGetValue(r.Id, out var tasks);
            var assignee = r.AssigneeUserId is { } uid && names.TryGetValue(uid, out var n) ? n : null;
            var teamName = r.TeamId is { } tid && teamNames.TryGetValue(tid, out var tn) ? tn : null;
            items.Add(new BacklogListItem(
                r.Id, r.BacklogCode, r.Project, tasks?.Count ?? 0, r.PeriodMonth, r.Type, assignee,
                teamName, showTeam));
        }
        _all = items;
        RebuildFilterOptions();
        ApplyFilters();
    }

    private void RebuildFilterOptions()
    {
        static void Fill(ObservableCollection<string> col, IEnumerable<string?> values)
        {
            col.Clear();
            col.Add(AllOption);
            foreach (var v in values.Where(v => !string.IsNullOrEmpty(v)).Distinct().OrderBy(v => v))
                col.Add(v!);
        }
        Fill(ProjectOptions, _all.Select(i => i.Project));
        Fill(TypeOptions, _all.Select(i => i.Type));
        Fill(AssigneeOptions, _all.Select(i => i.AssigneeName));
        Fill(MonthOptions, _all.Select(i => i.PeriodMonth));

        // Drop any selection that no longer exists in the data (back to "All").
        if (!ProjectOptions.Contains(FilterProject)) FilterProject = AllOption;
        if (!TypeOptions.Contains(FilterType)) FilterType = AllOption;
        if (!AssigneeOptions.Contains(FilterAssignee)) FilterAssignee = AllOption;
        if (!MonthOptions.Contains(FilterMonth)) FilterMonth = AllOption;
    }

    private void ApplyFilters()
    {
        IEnumerable<BacklogListItem> q = _all;
        var term = SearchTerm?.Trim();
        if (!string.IsNullOrEmpty(term))
            q = q.Where(i => i.BacklogCode.Contains(term, StringComparison.OrdinalIgnoreCase)
                          || (i.Project ?? "").Contains(term, StringComparison.OrdinalIgnoreCase));
        if (FilterProject != AllOption) q = q.Where(i => i.Project == FilterProject);
        if (FilterType != AllOption) q = q.Where(i => i.Type == FilterType);
        if (FilterAssignee != AllOption) q = q.Where(i => i.AssigneeName == FilterAssignee);
        if (FilterMonth != AllOption) q = q.Where(i => i.PeriodMonth == FilterMonth);

        Backlogs.Clear();
        foreach (var i in q) Backlogs.Add(i);
    }

    [RelayCommand]
    public async Task BeginCreateAsync()
    {
        var templates = await _templates.GetAllAsync();
        var users = _users is null ? null : await _users.GetActiveAsync();
        var pca = _pcaContacts is null ? null : await _pcaContacts.GetActiveAsync();
        var tags = _tagsRepo is null ? null : await _tagsRepo.GetAllAsync();
        Editor = BacklogEditorViewModel.ForCreate(templates, users, pca, tags);
    }

    [RelayCommand]
    public async Task BeginEditAsync(int backlogId)
    {
        var backlog = await _backlogs.GetByIdAsync(backlogId);
        if (backlog is null) return;
        var existing = await _tasks.GetActiveByBacklogAsync(backlogId);
        var templates = await _templates.GetAllAsync();
        var audit = await _backlogs.GetAuditAsync(backlogId);
        var users = _users is null ? null : await _users.GetActiveAsync();
        var pca = _pcaContacts is null ? null : await _pcaContacts.GetActiveAsync();
        var tags = _tagsRepo is null ? null : await _tagsRepo.GetAllAsync();
        var checkedTagIds = await _backlogs.GetTagIdsAsync(backlogId);
        Editor = BacklogEditorViewModel.ForEdit(
            backlog, existing, templates, audit, users, pca, tags, checkedTagIds);
    }

    [RelayCommand]
    public async Task SaveNewAsync()
    {
        if (Editor is null) return;

        // A new backlog must have at least one task (kept editor open with a message otherwise).
        if (Editor.ActiveTasks.Count == 0)
        {
            Editor.ErrorMessage = "A backlog must have at least one task.";
            return;
        }
        Editor.ErrorMessage = null;

        // FIX-2 (TM-06): a NEW backlog is stamped with the active team so it shows up in team grids.
        // Editing keeps the existing team (SaveEditAsync below preserves Editor.TeamId).
        var newId = await _backlogs.InsertAsync(
            new Backlog(0, Editor.BacklogCode.Trim(), (Editor.Project ?? string.Empty).Trim(), DateTimeOffset.UtcNow,
                Editor.StartDate, Editor.EndDate, Editor.PeriodMonth, Editor.Type, Editor.AssigneeUserId,
                Editor.DeadlineInternal, Editor.DeadlineExternal,
                Editor.RoughEstimateHours, Editor.OfficialEstimateHours,
                Editor.ProgressPercent, Editor.Note, Editor.PcaContactId,
                TeamId: _currentTeam?.ActiveTeamId is { } tid and > 0 ? tid : null));

        foreach (var row in Editor.ActiveTasks)
            await _tasks.InsertAsync(new TaskItem(0, newId, row.TaskName.Trim(), row.OrderIndex, true));

        // v7: persist the tag multi-select (replace-all). Skipped when no tag repo wired (legacy ctor).
        if (_tagsRepo is not null)
            await _backlogs.SetTagsAsync(newId, Editor.CheckedTagIds);

        Editor = null;
        await RefreshAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Backlogs)); // live-sync: Timesheet + Task List reload
    }

    [RelayCommand]
    public async Task SaveEditAsync()
    {
        if (Editor is null || !Editor.IsEditMode) return;
        var id = Editor.EditingBacklogId;

        await _backlogs.UpdateAsync(
            new Backlog(id, Editor.BacklogCode.Trim(), (Editor.Project ?? string.Empty).Trim(), DateTimeOffset.UtcNow,
                Editor.StartDate, Editor.EndDate, Editor.PeriodMonth, Editor.Type, Editor.AssigneeUserId,
                Editor.DeadlineInternal, Editor.DeadlineExternal,
                Editor.RoughEstimateHours, Editor.OfficialEstimateHours,
                Editor.ProgressPercent, Editor.Note, Editor.PcaContactId,
                TeamId: Editor.TeamId),   // FIX-2: editing preserves the existing team_id
            _currentUser?.Current?.Id, _currentUser?.Current?.Name);

        // v7: replace the tag set for this backlog. Skipped when no tag repo wired (legacy ctor).
        if (_tagsRepo is not null)
            await _backlogs.SetTagsAsync(id, Editor.CheckedTagIds);

        // Soft-delete existing tasks flagged removed (REQ-04 — task only, never the backlog).
        foreach (var removed in Editor.Tasks.Where(t => t.IsRemoved && t.ExistingTaskId > 0))
            await _tasks.SetActiveAsync(removed.ExistingTaskId, false);

        // Insert brand-new tasks (ExistingTaskId == 0) from the active set.
        foreach (var row in Editor.ActiveTasks.Where(t => t.ExistingTaskId == 0))
            await _tasks.InsertAsync(new TaskItem(0, id, row.TaskName.Trim(), row.OrderIndex, true));

        Editor = null;
        await RefreshAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Tasks)); // live-sync: Timesheet reloads rows
    }

    [RelayCommand]
    public void CancelEditor() => Editor = null;
}
