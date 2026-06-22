using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    // RECONCILED 2026-06-21: single source of truth — P5 ReportsViewModel already shipped
    // `public const string NDaysKey = "chua_log_n_days"`. Reference it so Settings(write) and
    // Reports(read) can never drift apart. (Both VMs are in namespace TimesheetApp.ViewModels.)
    private const string WarningDaysKey = ReportsViewModel.NDaysKey;
    private const int DefaultWarningDays = 3;

    private readonly IAppConfig _config;
    private readonly ISettingsRepository _settings;
    private readonly ITaskTemplateRepository _templates;   // canonical template store (reconciliation 2026-06-21)
    private readonly IDefaultTaskSyncService _sync;
    private readonly IMessenger _messenger;

    public SettingsViewModel(
        IAppConfig config,
        ISettingsRepository settings,
        ITaskTemplateRepository templates,
        IDefaultTaskSyncService sync,
        IMessenger? messenger = null)
    {
        _config = config;
        _settings = settings;
        _templates = templates;
        _sync = sync;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    [ObservableProperty] private string _dbPath = "";
    [ObservableProperty] private int _warningDays = DefaultWarningDays;

    // The template editor overlay; null = hidden (mirrors RequestsViewModel.Editor).
    [ObservableProperty] private TemplateEditorViewModel? _templateEditor;

    // All raw template rows (kept so BeginEditTemplate can hand the matching rows to ForEdit).
    private IReadOnlyList<TaskTemplate> _allTemplateRows = Array.Empty<TaskTemplate>();

    // Templates grouped for the Settings list: one entry per template name + its task count (SET-03).
    public ObservableCollection<TemplateSummary> TemplateGroups { get; } = new();

    public async Task LoadAsync()
    {
        DbPath = _config.DbPath;

        var raw = await _settings.GetAsync(WarningDaysKey);
        WarningDays = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : DefaultWarningDays;

        await ReloadTemplatesAsync();
    }

    // SET-01: app-local config only, never the shared Settings table.
    [RelayCommand]
    private Task ApplyDbPathAsync()
    {
        _config.SetDbPath(DbPath);   // IAppConfig persists path to %APPDATA% (P1 contract: get + SetDbPath)
        return Task.CompletedTask;
    }

    // SET-02: shared Settings table.
    [RelayCommand]
    private Task SaveWarningDaysAsync() =>
        _settings.SetAsync(WarningDaysKey, WarningDays.ToString(CultureInfo.InvariantCulture));

    // SET-03: template CRUD (via ITaskTemplateRepository — canonical store).
    // A template = the set of rows sharing one template_name. The editor lets the user name the
    // template once and manage an ordered list of task names, then Save persists all rows at once.

    [RelayCommand]
    private void BeginCreateTemplate() => TemplateEditor = TemplateEditorViewModel.ForCreate();

    [RelayCommand]
    private void BeginEditTemplate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var rows = _allTemplateRows.Where(t => t.TemplateName == name).ToList();
        TemplateEditor = TemplateEditorViewModel.ForEdit(name, rows);
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        var editor = TemplateEditor;
        if (editor is null) return;

        var name = editor.TemplateName.Trim();
        var taskNames = editor.OrderedTaskNames;
        if (string.IsNullOrWhiteSpace(name) || taskNames.Count == 0) return; // need a name + ≥1 task

        // Edit = delete-then-reinsert all rows (handles rename + reorder + add/remove in one shot).
        if (editor.IsEditMode)
            await _templates.DeleteByTemplateNameAsync(editor.OriginalTemplateName);

        for (var i = 0; i < taskNames.Count; i++)
            await _templates.InsertAsync(new TaskTemplate(0, name, taskNames[i], i));

        TemplateEditor = null;
        await ReloadTemplatesAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Templates)); // live-sync: Requests template list
    }

    [RelayCommand]
    private void CancelTemplate() => TemplateEditor = null;

    [RelayCommand]
    private async Task DeleteTemplateAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await _templates.DeleteByTemplateNameAsync(name);
        await ReloadTemplatesAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Templates));
    }

    // SET-04: reconcile DefaultTasks -> DEFAULT request's Tasks (rename = soft-delete + insert).
    [RelayCommand]
    private async Task SaveDefaultTasksAsync()
    {
        await _sync.SyncAsync();
        _messenger.Send(new DataChangedMessage(DataKind.DefaultTasks)); // live-sync: Timesheet rows
    }

    private async Task ReloadTemplatesAsync()
    {
        _allTemplateRows = await _templates.GetAllAsync();
        TemplateGroups.Clear();
        foreach (var g in _allTemplateRows
                     .GroupBy(t => t.TemplateName)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
            TemplateGroups.Add(new TemplateSummary(g.Key, g.Count()));
    }
}
