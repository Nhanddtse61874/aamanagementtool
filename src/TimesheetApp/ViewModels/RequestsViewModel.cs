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

    public ObservableCollection<RequestListItem> Requests { get; } = new();

    [ObservableProperty] private string? _searchTerm;
    [ObservableProperty] private RequestEditorViewModel? _editor;

    // Note: SearchTerm setter does NOT auto-refresh. The view triggers RefreshAsync explicitly
    // (RefreshCommand) so the query runs exactly once per search (REQ-01); auto-firing here would
    // double-query when a caller also invokes RefreshAsync.

    public async Task LoadAsync() => await RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var term = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim();
        var rows = await _requests.SearchAsync(term);

        // v4: id -> name map (GetAll so a deactivated assignee still resolves) for the Assignee column.
        var names = _users is null
            ? new Dictionary<int, string>()
            : (await _users.GetAllAsync()).ToDictionary(u => u.Id, u => u.Name);

        Requests.Clear();
        foreach (var r in rows)
        {
            var tasks = await _tasks.GetActiveByRequestAsync(r.Id);
            var assignee = r.AssigneeUserId is { } uid && names.TryGetValue(uid, out var n) ? n : null;
            Requests.Add(new RequestListItem(
                r.Id, r.RequestCode, r.Project, tasks?.Count ?? 0, r.PeriodMonth, r.Status, assignee));
        }
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
