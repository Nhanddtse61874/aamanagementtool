namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Models;

// v7: one tag in the editor's multi-select; IsChecked drives SetTagsAsync on save.
public sealed partial class TagPickVm : ObservableObject
{
    public TagPickVm(Tag tag, bool isChecked)
    {
        Tag = tag;
        _isChecked = isChecked;
    }

    public Tag Tag { get; }
    [ObservableProperty] private bool _isChecked;
}

public sealed partial class BacklogEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<TaskTemplate> _templates;

    // v4: sentinel for "no assignee" (id 0) so the ComboBox can clear an assignment.
    public static readonly User Unassigned = new(0, "— Unassigned —", null, true);

    // v7: sentinel for "no PCA contact" (id 0), mirroring the assignee Unassigned sentinel.
    public static readonly PcaContact NoPcaContact = new(0, "— Unassigned —", true);

    private BacklogEditorViewModel(
        IReadOnlyList<TaskTemplate> templates, IReadOnlyList<User>? users,
        IReadOnlyList<PcaContact>? pcaContacts, IReadOnlyList<Tag>? tags)
    {
        _templates = templates;
        Templates = new ObservableCollection<TaskTemplate>(templates);
        TemplateNames = templates.Select(t => t.TemplateName).Distinct().OrderBy(n => n).ToList();

        // Assignee pick list = "(unassigned)" + the active users; default selection is unassigned.
        Users = new[] { Unassigned }.Concat(users ?? Array.Empty<User>()).ToList();
        _selectedAssignee = Unassigned;

        // v7: PCA pick list = "(unassigned)" + active contacts; default selection is none.
        PcaContacts = new[] { NoPcaContact }.Concat(pcaContacts ?? Array.Empty<PcaContact>()).ToList();
        _selectedPcaContact = NoPcaContact;

        // v7: tag multi-select (all tags, none checked by default).
        foreach (var t in tags ?? Array.Empty<Tag>())
            TagPicks.Add(new TagPickVm(t, false));
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

    // v7 tracking (spec §5.2). DateOnly? deadlines bound to DatePickers via the DateOnly converter.
    [ObservableProperty] private DateOnly? _deadlineInternal;
    [ObservableProperty] private DateOnly? _deadlineExternal;

    // v7 estimates. Bound as TEXT — parsed to decimal? on change; non-numeric/negative => null + error.
    [ObservableProperty] private string? _roughEstimateText;
    [ObservableProperty] private string? _officialEstimateText;
    public decimal? RoughEstimateHours { get; private set; }
    public decimal? OfficialEstimateHours { get; private set; }

    [ObservableProperty] private string? _note;

    // v7 manual progress (0-100). Bound as TEXT; out-of-range / non-numeric => null + error.
    [ObservableProperty] private string? _progressText;
    public int? ProgressPercent { get; private set; }

    // v7: external (PCA) contact. Bound to a ComboBox; NoPcaContact (id 0) => null.
    [ObservableProperty] private PcaContact _selectedPcaContact = NoPcaContact;
    public IReadOnlyList<PcaContact> PcaContacts { get; }
    public int? PcaContactId => SelectedPcaContact is { Id: > 0 } p ? p.Id : null;

    // v7: tag multi-select; the ids of the checked tags form the SetTagsAsync replace-set.
    public ObservableCollection<TagPickVm> TagPicks { get; } = new();
    public IReadOnlyList<int> CheckedTagIds =>
        TagPicks.Where(t => t.IsChecked).Select(t => t.Tag.Id).ToList();

    // Validation message shown in the editor (e.g. "must have at least one task" on create).
    [ObservableProperty] private string? _errorMessage;

    // v7 parse handlers — keep the parsed value + text in sync, surfacing bad input via ErrorMessage.
    partial void OnRoughEstimateTextChanged(string? value) =>
        RoughEstimateHours = ParseEstimate(value, "Rough estimate");
    partial void OnOfficialEstimateTextChanged(string? value) =>
        OfficialEstimateHours = ParseEstimate(value, "Official estimate");
    partial void OnProgressTextChanged(string? value) => ProgressPercent = ParseProgress(value);

    // Empty => null (cleared, no error). Non-numeric / negative => null + an error message.
    private decimal? ParseEstimate(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (decimal.TryParse(value.Trim(),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0)
            return d;
        ErrorMessage = $"{label} must be a number ≥ 0.";
        return null;
    }

    // Empty => null (cleared). Non-integer / out of 0-100 => null + an error message.
    private int? ParseProgress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value.Trim(), out var p) && p is >= 0 and <= 100)
            return p;
        ErrorMessage = "Progress must be a whole number 0–100.";
        return null;
    }

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
        IReadOnlyList<TaskTemplate> templates, IReadOnlyList<User>? users = null,
        IReadOnlyList<PcaContact>? pcaContacts = null, IReadOnlyList<Tag>? tags = null) =>
        // New tickets default to the current month (month/year fields default to today).
        new(templates, users, pcaContacts, tags) { IsEditMode = false, EditingBacklogId = 0 };

    public static BacklogEditorViewModel ForEdit(
        Backlog backlog, IReadOnlyList<TaskItem> existingTasks, IReadOnlyList<TaskTemplate> templates,
        IReadOnlyList<BacklogAuditEntry>? audit = null, IReadOnlyList<User>? users = null,
        IReadOnlyList<PcaContact>? pcaContacts = null, IReadOnlyList<Tag>? tags = null,
        IReadOnlyList<int>? checkedTagIds = null)
    {
        var (month, year) = ParsePeriodMonth(backlog.PeriodMonth);
        var vm = new BacklogEditorViewModel(templates, users, pcaContacts, tags)
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
            // v7 tracking fields (text-bound estimates/progress projected back to text).
            DeadlineInternal = backlog.DeadlineInternal,
            DeadlineExternal = backlog.DeadlineExternal,
            Note = backlog.Note,
            RoughEstimateText = FormatHours(backlog.RoughEstimateHours),
            OfficialEstimateText = FormatHours(backlog.OfficialEstimateHours),
            ProgressText = backlog.ProgressPercent?.ToString(),
        };
        // Preselect the saved assignee (falls back to Unassigned if not in the list / null).
        vm.SelectedAssignee = vm.Users.FirstOrDefault(u => u.Id == backlog.AssigneeUserId) ?? Unassigned;
        // Preselect the saved PCA contact (falls back to NoPcaContact if not in the list / null).
        vm.SelectedPcaContact =
            vm.PcaContacts.FirstOrDefault(p => p.Id == backlog.PcaContactId) ?? NoPcaContact;
        // Check the tags currently linked to this backlog.
        if (checkedTagIds is not null)
        {
            var set = new HashSet<int>(checkedTagIds);
            foreach (var pick in vm.TagPicks)
                if (set.Contains(pick.Tag.Id)) pick.IsChecked = true;
        }
        foreach (var t in existingTasks.OrderBy(t => t.OrderIndex))
            vm.Tasks.Add(EditableTaskRowVm.Existing(t.Id, t.TaskName, t.OrderIndex));
        if (audit is not null)
            foreach (var a in audit) vm.AuditEntries.Add(a);
        return vm;
    }

    // Render a stored estimate back into the text box without trailing zeros (e.g. 12.5, 16).
    private static string? FormatHours(decimal? hours) =>
        hours?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

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
