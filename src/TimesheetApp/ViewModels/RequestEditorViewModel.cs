namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Models;

public sealed partial class BacklogEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<TaskTemplate> _templates;

    // v4: sentinel for "no assignee" (id 0) so the ComboBox can clear an assignment.
    public static readonly User Unassigned = new(0, "— Unassigned —", null, true);

    private BacklogEditorViewModel(IReadOnlyList<TaskTemplate> templates, IReadOnlyList<User>? users)
    {
        _templates = templates;
        Templates = new ObservableCollection<TaskTemplate>(templates);
        TemplateNames = templates.Select(t => t.TemplateName).Distinct().OrderBy(n => n).ToList();

        // Assignee pick list = "(unassigned)" + the active users; default selection is unassigned.
        Users = new[] { Unassigned }.Concat(users ?? Array.Empty<User>()).ToList();
        _selectedAssignee = Unassigned;
    }

    public bool IsEditMode { get; private init; }
    public int EditingBacklogId { get; private init; }

    [ObservableProperty] private string _backlogCode = string.Empty;
    [ObservableProperty] private string _project = string.Empty;
    [ObservableProperty] private string? _selectedTemplateName;

    // v2 ticket fields. Period is a REQUIRED month: month-number (1-12) + year combos -> "yyyy-MM".
    [ObservableProperty] private DateOnly? _startDate;
    [ObservableProperty] private DateOnly? _endDate;
    [ObservableProperty] private int _periodMonthNumber = DateTime.Today.Month;
    [ObservableProperty] private int _periodYear = DateTime.Today.Year;
    [ObservableProperty] private string? _type;

    // v4: assignee (the user responsible). Bound to a ComboBox of Users; Unassigned (id 0) => null.
    [ObservableProperty] private User _selectedAssignee = Unassigned;
    public IReadOnlyList<User> Users { get; }
    public int? AssigneeUserId => SelectedAssignee is { Id: > 0 } u ? u.Id : null;

    // Validation message shown in the editor (e.g. "must have at least one task" on create).
    [ObservableProperty] private string? _errorMessage;

    // Always set (month is required) — projected to the persisted "yyyy-MM".
    public string PeriodMonth => $"{PeriodYear:D4}-{PeriodMonthNumber:D2}";

    public IReadOnlyList<string> Types { get; } = BacklogType.All;
    public IReadOnlyList<string> Projects { get; } = BacklogProjects.All;
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public IReadOnlyList<int> Years { get; } =
        Enumerable.Range(DateTime.Today.Year - 2, 6).ToList(); // current-2 .. current+3

    // v2 change history for this backlog (read-only; populated in ForEdit).
    public ObservableCollection<BacklogAuditEntry> AuditEntries { get; } = new();

    public ObservableCollection<TaskTemplate> Templates { get; }
    public IReadOnlyList<string> TemplateNames { get; }
    public ObservableCollection<EditableTaskRowVm> Tasks { get; } = new();

    // Active (not-removed) tasks, reindexed 0..n in display order. This is the persist set.
    public IReadOnlyList<EditableTaskRowVm> ActiveTasks
    {
        get
        {
            var active = Tasks.Where(t => !t.IsRemoved).ToList();
            for (var i = 0; i < active.Count; i++) active[i].OrderIndex = i;
            return active;
        }
    }

    public static BacklogEditorViewModel ForCreate(
        IReadOnlyList<TaskTemplate> templates, IReadOnlyList<User>? users = null) =>
        // New tickets default to the current month (month/year fields default to today).
        new(templates, users) { IsEditMode = false, EditingBacklogId = 0 };

    public static BacklogEditorViewModel ForEdit(
        Backlog backlog, IReadOnlyList<TaskItem> existingTasks, IReadOnlyList<TaskTemplate> templates,
        IReadOnlyList<BacklogAuditEntry>? audit = null, IReadOnlyList<User>? users = null)
    {
        var (month, year) = ParsePeriodMonth(backlog.PeriodMonth);
        var vm = new BacklogEditorViewModel(templates, users)
        {
            IsEditMode = true,
            EditingBacklogId = backlog.Id,
            BacklogCode = backlog.BacklogCode,
            Project = backlog.Project,
            StartDate = backlog.StartDate,
            EndDate = backlog.EndDate,
            PeriodMonthNumber = month,
            PeriodYear = year,
            Type = backlog.Type,
        };
        // Preselect the saved assignee (falls back to Unassigned if not in the list / null).
        vm.SelectedAssignee = vm.Users.FirstOrDefault(u => u.Id == backlog.AssigneeUserId) ?? Unassigned;
        foreach (var t in existingTasks.OrderBy(t => t.OrderIndex))
            vm.Tasks.Add(EditableTaskRowVm.Existing(t.Id, t.TaskName, t.OrderIndex));
        if (audit is not null)
            foreach (var a in audit) vm.AuditEntries.Add(a);
        return vm;
    }

    // "yyyy-MM" -> (month, year); falls back to the current month when missing/unparseable.
    private static (int Month, int Year) ParsePeriodMonth(string? yyyymm)
    {
        if (DateOnly.TryParseExact(yyyymm + "-01", "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
            return (d.Month, d.Year);
        return (DateTime.Today.Month, DateTime.Today.Year);
    }

    private int NextOrderIndex() => Tasks.Count == 0 ? 0 : Tasks.Max(t => t.OrderIndex) + 1;

    // Tracks the template whose tasks were last appended, so picking it (or clicking Apply again)
    // does not duplicate. Picking a DIFFERENT template appends that one's tasks.
    private string? _lastAppliedTemplate;

    // UX: selecting a template in the dropdown auto-applies it — the user no longer has to click a
    // separate "Apply Template" button (the missing click was why templates appeared to do nothing).
    partial void OnSelectedTemplateNameChanged(string? value) => ApplyTemplate();

    [RelayCommand]
    public void ApplyTemplate()
    {
        if (string.IsNullOrWhiteSpace(SelectedTemplateName)) return;
        if (SelectedTemplateName == _lastAppliedTemplate) return; // already applied — no duplicate
        _lastAppliedTemplate = SelectedTemplateName;

        var rows = _templates
            .Where(t => t.TemplateName == SelectedTemplateName)
            .OrderBy(t => t.OrderIndex);
        foreach (var r in rows)
            Tasks.Add(EditableTaskRowVm.New(r.TaskName, NextOrderIndex()));
    }

    public void AddTask(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Tasks.Add(EditableTaskRowVm.New(name.Trim(), NextOrderIndex()));
    }

    public void RemoveTask(EditableTaskRowVm row)
    {
        if (row.ExistingTaskId > 0)
        {
            row.IsRemoved = true;           // existing -> soft-delete on save (REQ-04)
        }
        else
        {
            Tasks.Remove(row);              // new -> just drop
            Reindex();
        }
    }

    public void MoveUp(EditableTaskRowVm row)
    {
        var i = Tasks.IndexOf(row);
        if (i <= 0) return;
        Tasks.Move(i, i - 1);
        Reindex();
    }

    public void MoveDown(EditableTaskRowVm row)
    {
        var i = Tasks.IndexOf(row);
        if (i < 0 || i >= Tasks.Count - 1) return;
        Tasks.Move(i, i + 1);
        Reindex();
    }

    private void Reindex()
    {
        for (var i = 0; i < Tasks.Count; i++) Tasks[i].OrderIndex = i;
    }
}
