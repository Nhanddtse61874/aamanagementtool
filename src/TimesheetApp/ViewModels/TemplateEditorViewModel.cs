namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TimesheetApp.Models;

// Working-set for creating/editing a single template (the set of TaskTemplate rows sharing one
// template_name). Mirrors RequestEditorViewModel: ForCreate/ForEdit factories, an ObservableCollection
// of editable rows, and plain Add/Remove/MoveUp/MoveDown methods wrapped as commands in the view
// code-behind. SET-03: one template name + an ordered editable list of task names.
public sealed partial class TemplateEditorViewModel : ObservableObject
{
    private TemplateEditorViewModel() { }

    public bool IsEditMode { get; private init; }

    // The template name as it existed when editing began — used to delete the old rows before
    // reinserting (the user may rename the template). Empty in create mode.
    public string OriginalTemplateName { get; private init; } = string.Empty;

    [ObservableProperty] private string _templateName = string.Empty;

    public ObservableCollection<TemplateTaskRowVm> Tasks { get; } = new();

    // Ordered, non-blank task names in display order — the set to persist on Save.
    public IReadOnlyList<string> OrderedTaskNames =>
        Tasks.Select(t => t.TaskName.Trim())
             .Where(n => !string.IsNullOrWhiteSpace(n))
             .ToList();

    public static TemplateEditorViewModel ForCreate() =>
        new() { IsEditMode = false };

    public static TemplateEditorViewModel ForEdit(string templateName, IEnumerable<TaskTemplate> existingRows)
    {
        var vm = new TemplateEditorViewModel
        {
            IsEditMode = true,
            OriginalTemplateName = templateName,
            TemplateName = templateName,
        };
        foreach (var r in existingRows.OrderBy(r => r.OrderIndex))
            vm.Tasks.Add(TemplateTaskRowVm.New(r.TaskName, vm.NextOrderIndex()));
        return vm;
    }

    private int NextOrderIndex() => Tasks.Count == 0 ? 0 : Tasks.Max(t => t.OrderIndex) + 1;

    public void AddTask(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Tasks.Add(TemplateTaskRowVm.New(name.Trim(), NextOrderIndex()));
    }

    public void RemoveTask(TemplateTaskRowVm row)
    {
        Tasks.Remove(row);
        Reindex();
    }

    public void MoveUp(TemplateTaskRowVm row)
    {
        var i = Tasks.IndexOf(row);
        if (i <= 0) return;
        Tasks.Move(i, i - 1);
        Reindex();
    }

    public void MoveDown(TemplateTaskRowVm row)
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
