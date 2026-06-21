using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public SettingsViewModel(
        IAppConfig config,
        ISettingsRepository settings,
        ITaskTemplateRepository templates,
        IDefaultTaskSyncService sync)
    {
        _config = config;
        _settings = settings;
        _templates = templates;
        _sync = sync;
    }

    [ObservableProperty] private string _dbPath = "";
    [ObservableProperty] private int _warningDays = DefaultWarningDays;
    [ObservableProperty] private string _newTemplateName = "";
    [ObservableProperty] private string _newTemplateTaskName = "";
    [ObservableProperty] private TaskTemplate? _selectedTemplate;

    public ObservableCollection<TaskTemplate> Templates { get; } = new();

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
    [RelayCommand]
    private async Task AddTemplateAsync()
    {
        await _templates.InsertAsync(new TaskTemplate(0, NewTemplateName, NewTemplateTaskName, 0));
        NewTemplateName = "";
        NewTemplateTaskName = "";
        await ReloadTemplatesAsync();
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync()
    {
        if (SelectedTemplate is null) return;
        await _templates.DeleteAsync(SelectedTemplate.Id);
        await ReloadTemplatesAsync();
    }

    // SET-04: reconcile DefaultTasks -> DEFAULT request's Tasks (rename = soft-delete + insert).
    [RelayCommand]
    private Task SaveDefaultTasksAsync() => _sync.SyncAsync();

    private async Task ReloadTemplatesAsync()
    {
        var items = await _templates.GetAllAsync();
        Templates.Clear();
        foreach (var t in items) Templates.Add(t);
    }
}
