namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

public sealed partial class RequestsViewModel : ObservableObject
{
    private readonly IRequestRepository _requests;
    private readonly ITaskRepository _tasks;
    private readonly ITaskTemplateRepository _templates;
    private readonly IMessenger _messenger;
    private readonly ICurrentUserService? _currentUser;
    private readonly IUserRepository? _users;   // v4: assignee pick list + name resolution

    public RequestsViewModel(
        IRequestRepository requests, ITaskRepository tasks, ITaskTemplateRepository templates,
        IMessenger? messenger = null, ICurrentUserService? currentUser = null,
        IUserRepository? users = null)
    {
        _requests = requests;
        _tasks = tasks;
        _templates = templates;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _currentUser = currentUser;
        _users = users;
    }

    // List row = the Request plus its active-task count + v2 period/status + v4 assignee (grid columns).
    public sealed record RequestListItem(
        int Id, string RequestCode, string Project, int TaskCount, string? PeriodMonth, string? Status,
        string? AssigneeName);

    private const string AllOption = "All";

    // The full loaded set; Requests is the filtered view shown in the grid.
    private List<RequestListItem> _all = new();
    public ObservableCollection<RequestListItem> Requests { get; } = new();

    [ObservableProperty] private string? _searchTerm;
    [ObservableProperty] private RequestEditorViewModel? _editor;

    // Structured filters (live, in-memory). "All" = no filter. Option lists are rebuilt from the data.
    [ObservableProperty] private string _filterProject = AllOption;
    [ObservableProperty] private string _filterStatus = AllOption;
    [ObservableProperty] private string _filterAssignee = AllOption;
    [ObservableProperty] private string _filterMonth = AllOption;

    public ObservableCollection<string> ProjectOptions { get; } = new() { AllOption };
    public ObservableCollection<string> StatusOptions { get; } = new() { AllOption };
    public ObservableCollection<string> AssigneeOptions { get; } = new() { AllOption };
    public ObservableCollection<string> MonthOptions { get; } = new() { AllOption };

    // All filters (incl. the search box) re-filter the loaded list live — no DB round-trip per keystroke.
    partial void OnSearchTermChanged(string? value) => ApplyFilters();
    partial void OnFilterProjectChanged(string value) => ApplyFilters();
    partial void OnFilterStatusChanged(string value) => ApplyFilters();
    partial void OnFilterAssigneeChanged(string value) => ApplyFilters();
    partial void OnFilterMonthChanged(string value) => ApplyFilters();

    public async Task LoadAsync() => await RefreshAsync();

    // Reloads the full set from the DB, rebuilds the filter option lists, then applies the live filters.
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var rows = await _requests.SearchAsync(null);

        // v4: id -> name map (GetAll so a deactivated assignee still resolves) for the Assignee column/filter.
        var names = _users is null
            ? new Dictionary<int, string>()
            : (await _users.GetAllAsync()).ToDictionary(u => u.Id, u => u.Name);

        var items = new List<RequestListItem>();
        foreach (var r in rows)
        {
            var tasks = await _tasks.GetActiveByRequestAsync(r.Id);
            var assignee = r.AssigneeUserId is { } uid && names.TryGetValue(uid, out var n) ? n : null;
            items.Add(new RequestListItem(
                r.Id, r.RequestCode, r.Project, tasks?.Count ?? 0, r.PeriodMonth, r.Status, assignee));
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
        Fill(StatusOptions, _all.Select(i => i.Status));
        Fill(AssigneeOptions, _all.Select(i => i.AssigneeName));
        Fill(MonthOptions, _all.Select(i => i.PeriodMonth));

        // Drop any selection that no longer exists in the data (back to "All").
        if (!ProjectOptions.Contains(FilterProject)) FilterProject = AllOption;
        if (!StatusOptions.Contains(FilterStatus)) FilterStatus = AllOption;
        if (!AssigneeOptions.Contains(FilterAssignee)) FilterAssignee = AllOption;
        if (!MonthOptions.Contains(FilterMonth)) FilterMonth = AllOption;
    }

    private void ApplyFilters()
    {
        IEnumerable<RequestListItem> q = _all;
        var term = SearchTerm?.Trim();
        if (!string.IsNullOrEmpty(term))
            q = q.Where(i => i.RequestCode.Contains(term, StringComparison.OrdinalIgnoreCase)
                          || (i.Project ?? "").Contains(term, StringComparison.OrdinalIgnoreCase));
        if (FilterProject != AllOption) q = q.Where(i => i.Project == FilterProject);
        if (FilterStatus != AllOption) q = q.Where(i => i.Status == FilterStatus);
        if (FilterAssignee != AllOption) q = q.Where(i => i.AssigneeName == FilterAssignee);
        if (FilterMonth != AllOption) q = q.Where(i => i.PeriodMonth == FilterMonth);

        Requests.Clear();
        foreach (var i in q) Requests.Add(i);
    }

    [RelayCommand]
    public async Task BeginCreateAsync()
    {
        var templates = await _templates.GetAllAsync();
        var users = _users is null ? null : await _users.GetActiveAsync();
        Editor = RequestEditorViewModel.ForCreate(templates, users);
    }

    [RelayCommand]
    public async Task BeginEditAsync(int requestId)
    {
        var request = await _requests.GetByIdAsync(requestId);
        if (request is null) return;
        var existing = await _tasks.GetActiveByRequestAsync(requestId);
        var templates = await _templates.GetAllAsync();
        var audit = await _requests.GetAuditAsync(requestId);
        var users = _users is null ? null : await _users.GetActiveAsync();
        Editor = RequestEditorViewModel.ForEdit(request, existing, templates, audit, users);
    }

    [RelayCommand]
    public async Task SaveNewAsync()
    {
        if (Editor is null) return;

        // A new request must have at least one task (kept editor open with a message otherwise).
        if (Editor.ActiveTasks.Count == 0)
        {
            Editor.ErrorMessage = "A request must have at least one task.";
            return;
        }
        Editor.ErrorMessage = null;

        var newId = await _requests.InsertAsync(
            new Request(0, Editor.RequestCode.Trim(), (Editor.Project ?? string.Empty).Trim(), DateTimeOffset.UtcNow,
                Editor.StartDate, Editor.EndDate, Editor.PeriodMonth, Editor.Status, Editor.AssigneeUserId));

        foreach (var row in Editor.ActiveTasks)
            await _tasks.InsertAsync(new TaskItem(0, newId, row.TaskName.Trim(), row.OrderIndex, true));

        Editor = null;
        await RefreshAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Tasks)); // live-sync: Timesheet reloads rows
    }

    [RelayCommand]
    public async Task SaveEditAsync()
    {
        if (Editor is null || !Editor.IsEditMode) return;
        var id = Editor.EditingRequestId;

        await _requests.UpdateAsync(
            new Request(id, Editor.RequestCode.Trim(), (Editor.Project ?? string.Empty).Trim(), DateTimeOffset.UtcNow,
                Editor.StartDate, Editor.EndDate, Editor.PeriodMonth, Editor.Status, Editor.AssigneeUserId),
            _currentUser?.Current?.Id, _currentUser?.Current?.Name);

        // Soft-delete existing tasks flagged removed (REQ-04 — task only, never the request).
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
