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
        new(templates) { IsEditMode = false, EditingRequestId = 0 };

    public static RequestEditorViewModel ForEdit(
        Request request, IReadOnlyList<TaskItem> existingTasks, IReadOnlyList<TaskTemplate> templates)
    {
        var vm = new RequestEditorViewModel(templates)
        {
            IsEditMode = true,
            EditingRequestId = request.Id,
            RequestCode = request.RequestCode,
            Project = request.Project,
        };
        foreach (var t in existingTasks.OrderBy(t => t.OrderIndex))
            vm.Tasks.Add(EditableTaskRowVm.Existing(t.Id, t.TaskName, t.OrderIndex));
        return vm;
    }

    private int NextOrderIndex() => Tasks.Count == 0 ? 0 : Tasks.Max(t => t.OrderIndex) + 1;

    [RelayCommand]
    public void ApplyTemplate()
    {
        if (string.IsNullOrWhiteSpace(SelectedTemplateName)) return;
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
