namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Models;

public sealed partial class RequestEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<TaskTemplate> _templates;

    private RequestEditorViewModel(IReadOnlyList<TaskTemplate> templates)
    {
        _templates = templates;
        Templates = new ObservableCollection<TaskTemplate>(templates);
        TemplateNames = templates.Select(t => t.TemplateName).Distinct().OrderBy(n => n).ToList();
    }

    public bool IsEditMode { get; private init; }
    public int EditingRequestId { get; private init; }

    [ObservableProperty] private string _requestCode = string.Empty;
    [ObservableProperty] private string _project = string.Empty;
    [ObservableProperty] private string? _selectedTemplateName;

    // v2 ticket fields. PeriodMonthDate is a first-of-month DateOnly?; PeriodMonth projects it to "yyyy-MM".
    [ObservableProperty] private DateOnly? _startDate;
    [ObservableProperty] private DateOnly? _endDate;
    [ObservableProperty] private DateOnly? _periodMonthDate;
    [ObservableProperty] private string? _status;

    public string? PeriodMonth => PeriodMonthDate?.ToString("yyyy-MM");

    public IReadOnlyList<string> Statuses { get; } = RequestStatus.All;

    // v2 change history for this request (read-only; populated in ForEdit).
    public ObservableCollection<RequestAuditEntry> AuditEntries { get; } = new();

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

    public static RequestEditorViewModel ForCreate(IReadOnlyList<TaskTemplate> templates) =>
        // New tickets default to the current month ("mỗi ticket phải được add vào một tháng cố định").
        new(templates)
        {
            IsEditMode = false,
            EditingRequestId = 0,
            PeriodMonthDate = FirstOfThisMonth(),
        };

    public static RequestEditorViewModel ForEdit(
        Request request, IReadOnlyList<TaskItem> existingTasks, IReadOnlyList<TaskTemplate> templates,
        IReadOnlyList<RequestAuditEntry>? audit = null)
    {
        var vm = new RequestEditorViewModel(templates)
        {
            IsEditMode = true,
            EditingRequestId = request.Id,
            RequestCode = request.RequestCode,
            Project = request.Project,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            PeriodMonthDate = ParsePeriodMonth(request.PeriodMonth),
            Status = request.Status,
        };
        foreach (var t in existingTasks.OrderBy(t => t.OrderIndex))
            vm.Tasks.Add(EditableTaskRowVm.Existing(t.Id, t.TaskName, t.OrderIndex));
        if (audit is not null)
            foreach (var a in audit) vm.AuditEntries.Add(a);
        return vm;
    }

    private static DateOnly FirstOfThisMonth()
    {
        var t = DateTime.Today;
        return new DateOnly(t.Year, t.Month, 1);
    }

    private static DateOnly? ParsePeriodMonth(string? yyyymm) =>
        DateOnly.TryParseExact(yyyymm + "-01", "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;

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
