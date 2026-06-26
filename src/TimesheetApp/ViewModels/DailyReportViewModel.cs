using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

// Daily Report (Standup) VM — DR-06..10. Input tab edits the signed-in user's standup for a day
// (add/delete entries, add/edit/delete issues); Board exposes the whole team for that day (read-only).
// Edits broadcast DataKind.Standup so the board refreshes live.
public sealed partial class DailyReportViewModel : ObservableObject
{
    private readonly IStandupService _service;
    private readonly IStandupArchiveService _archive;
    private readonly IClock _clock;
    private readonly IMessenger _messenger;

    public DailyReportViewModel(
        IStandupService service, IStandupArchiveService archive, IClock clock, IMessenger? messenger = null)
    {
        _service = service;
        _archive = archive;
        _clock = clock;
        _messenger = messenger ?? WeakReferenceMessenger.Default;

        _selectedDate = _clock.Today;
        NewYesterday = new StandupDraftVm(StandupSection.Yesterday, this);
        NewToday = new StandupDraftVm(StandupSection.Today, this);

        // Live refresh when standup/users/backlogs change anywhere.
        _messenger.Register<DailyReportViewModel, DataChangedMessage>(this, static (vm, m) =>
        {
            if (m.Kind is DataKind.Standup or DataKind.Users or DataKind.Backlogs)
                _ = vm.LoadAsync();
        });
    }

    [ObservableProperty] private DateOnly _selectedDate;
    [ObservableProperty] private string _myUserName = string.Empty;
    [ObservableProperty] private bool _canEditSelectedDay;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<StandupEntryRowVm> MyYesterday { get; } = new();
    public ObservableCollection<StandupEntryRowVm> MyToday { get; } = new();
    public ObservableCollection<UserStandup> Board { get; } = new();

    public StandupDraftVm NewYesterday { get; }
    public StandupDraftVm NewToday { get; }

    partial void OnSelectedDateChanged(DateOnly value) => _ = LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        CanEditSelectedDay = _service.CanEditDay(SelectedDate);
        StatusMessage = CanEditSelectedDay ? string.Empty : "This day is locked (only today and yesterday are editable).";

        var mine = await _service.GetMyStandupAsync(SelectedDate);
        MyUserName = mine.UserName;
        Fill(MyYesterday, mine.Yesterday);
        Fill(MyToday, mine.Today);

        var board = await _service.GetTeamStandupAsync(SelectedDate);
        Board.Clear();
        foreach (var u in board) Board.Add(u);

        // Refresh the backlog picker for the add-row forms.
        var backlogs = await _service.SearchBacklogsAsync(null);
        FillPicker(NewYesterday, backlogs);
        FillPicker(NewToday, backlogs);
    }

    [RelayCommand]
    public Task PrevDayAsync() { SelectedDate = SelectedDate.AddDays(-1); return Task.CompletedTask; }

    [RelayCommand]
    public Task NextDayAsync() { SelectedDate = SelectedDate.AddDays(1); return Task.CompletedTask; }

    [RelayCommand]
    public async Task ArchiveWeekAsync()
    {
        var path = await _archive.ExportWeekAsync(SelectedDate);
        StatusMessage = path is null ? "No standup data this week to archive." : $"Archived {Path.GetFileName(path)}";
    }

    // ---- called by child VMs ----

    internal async Task AddEntryAsync(StandupDraftVm draft)
    {
        var d = new StandupEntryDraft(
            draft.Section,
            draft.BacklogId,
            (draft.BacklogCode ?? "").Trim(),
            (draft.TaskText ?? "").Trim(),
            draft.Description ?? "",
            draft.Deadline is { } dt ? DateOnly.FromDateTime(dt) : null,
            draft.Status);
        try
        {
            var id = await _service.AddEntryAsync(SelectedDate, d);
            if (id <= 0) { StatusMessage = "Day is locked — cannot add."; return; }
            draft.Reset();
            await ReloadAndBroadcastAsync();
        }
        catch (ArgumentException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    internal async Task DeleteEntryAsync(int entryId)
    {
        if (await _service.DeleteEntryAsync(entryId)) await ReloadAndBroadcastAsync();
    }

    internal async Task AddIssueAsync(int entryId, string issueText, string? solutionText, string status)
    {
        await _service.AddIssueAsync(entryId, issueText, solutionText, status);
        await ReloadAndBroadcastAsync();
    }

    internal async Task SaveIssueAsync(StandupIssue issue)
    {
        await _service.UpdateIssueAsync(issue);
        await ReloadAndBroadcastAsync();
    }

    internal async Task DeleteIssueAsync(int issueId)
    {
        await _service.DeleteIssueAsync(issueId);
        await ReloadAndBroadcastAsync();
    }

    internal async Task LoadTasksForDraftAsync(StandupDraftVm draft, int backlogId)
    {
        var tasks = await _service.GetTasksForBacklogAsync(backlogId);
        draft.Tasks.Clear();
        foreach (var t in tasks) draft.Tasks.Add(t);
    }

    private async Task ReloadAndBroadcastAsync()
    {
        await LoadAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Standup));
    }

    private void Fill(ObservableCollection<StandupEntryRowVm> col, IEnumerable<StandupEntryView> views)
    {
        col.Clear();
        foreach (var v in views) col.Add(new StandupEntryRowVm(v, this));
    }

    private static void FillPicker(StandupDraftVm draft, IReadOnlyList<Backlog> backlogs)
    {
        draft.Backlogs.Clear();
        // Hide the hidden DEFAULT backlog from the standup picker.
        foreach (var r in backlogs.Where(r => r.BacklogCode != "DEFAULT")) draft.Backlogs.Add(r);
    }
}

// One existing standup row in the Input tab: read-only fields + its (collaboratively editable) issues
// + an add-issue box + a delete button (enabled only when the parent day is editable & owned).
public sealed partial class StandupEntryRowVm : ObservableObject
{
    private readonly DailyReportViewModel _parent;
    public StandupEntry Model { get; }
    public bool Editable { get; }
    public ObservableCollection<StandupIssueRowVm> Issues { get; } = new();

    public StandupEntryRowVm(StandupEntryView v, DailyReportViewModel parent)
    {
        Model = v.Entry;
        Editable = v.Editable;
        _parent = parent;
        foreach (var i in v.Issues) Issues.Add(new StandupIssueRowVm(i, parent));
    }

    public string BacklogCode => Model.BacklogCode;
    public string TaskText => Model.TaskText;
    public string Description => Model.Description;
    public string DeadlineText => Model.Deadline?.ToString("yyyy-MM-dd") ?? "";
    public string Status => Model.Status;

    // Adding an issue is done via a popup dialog (opened from the tab); the dialog calls
    // DailyReportViewModel.AddIssueAsync directly. Existing issues stay inline-editable below.
    [RelayCommand]
    private Task DeleteAsync() => _parent.DeleteEntryAsync(Model.Id);
}

// One issue row: text is read-only after creation; solution + status are editable by anyone (DR-04).
public sealed partial class StandupIssueRowVm : ObservableObject
{
    private readonly DailyReportViewModel _parent;
    public StandupIssue Model { get; }

    public StandupIssueRowVm(StandupIssue model, DailyReportViewModel parent)
    {
        _parent = parent;
        Model = model;
        _solutionText = model.SolutionText;
        _status = model.Status;
    }

    public int Id => Model.Id;
    public string IssueText => Model.IssueText;
    public IReadOnlyList<string> StatusOptions => StandupIssueStatus.All;

    [ObservableProperty] private string? _solutionText;
    [ObservableProperty] private string _status;

    [RelayCommand]
    private Task SaveAsync() =>
        _parent.SaveIssueAsync(Model with { SolutionText = SolutionText, Status = Status });

    [RelayCommand]
    private Task DeleteAsync() => _parent.DeleteIssueAsync(Id);
}

// The add-new-row form for one section (Yesterday/Today). A backlog can be picked (its tasks become
// pickable, code prefilled) or typed ad-hoc; deadline/status are entered manually.
public sealed partial class StandupDraftVm : ObservableObject
{
    private readonly DailyReportViewModel _parent;
    public string Section { get; }

    public StandupDraftVm(string section, DailyReportViewModel parent)
    {
        Section = section;
        _parent = parent;
        _status = StandupStatus.All[0];
    }

    public ObservableCollection<Backlog> Backlogs { get; } = new();
    public ObservableCollection<TaskItem> Tasks { get; } = new();
    public IReadOnlyList<string> StatusOptions => StandupStatus.All;

    [ObservableProperty] private Backlog? _selectedBacklog;
    [ObservableProperty] private TaskItem? _selectedTask;
    [ObservableProperty] private string _backlogCode = string.Empty;
    [ObservableProperty] private string _taskText = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private DateTime? _deadline;
    [ObservableProperty] private string _status;

    // Null when typed ad-hoc (no existing backlog selected) — keeps backlog_id null (DR-03).
    public int? BacklogId => SelectedBacklog?.Id;

    // Picking an existing backlog fills the code box + loads its tasks (the boxes hold the saved values,
    // so ad-hoc typing still works with the themed non-editable combos).
    partial void OnSelectedBacklogChanged(Backlog? value)
    {
        if (value is null) return;
        BacklogCode = value.BacklogCode;
        SelectedTask = null;
        _ = _parent.LoadTasksForDraftAsync(this, value.Id);
    }

    // Picking a task fills the task text box.
    partial void OnSelectedTaskChanged(TaskItem? value)
    {
        if (value is not null) TaskText = value.TaskName;
    }

    [RelayCommand]
    private Task AddAsync() => _parent.AddEntryAsync(this);

    public void Reset()
    {
        SelectedBacklog = null;
        SelectedTask = null;
        BacklogCode = string.Empty;
        TaskText = string.Empty;
        Description = string.Empty;
        Deadline = null;
        Status = StandupStatus.All[0];
        Tasks.Clear();
    }
}
