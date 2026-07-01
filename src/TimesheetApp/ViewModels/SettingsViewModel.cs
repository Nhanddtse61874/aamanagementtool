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
    private readonly ITagRepository _tags;                  // P8 TAG-01
    private readonly IPcaContactRepository _pca;            // P8 TL-11
    private readonly IBackupService _backup;                // P9 BK-01..06
    private readonly ITeamRepository _teams;                // P10 TM-03
    private readonly IUserRepository _users;                // P10 TM-03 (membership editor)
    private readonly IExportHubService? _exportHub;         // P11 EX-06 (manual "Export now"; null in tests)
    private readonly IRetentionService? _retention;         // P12 RT-01..07 (Preview/Run now; null in tests)
    private readonly ISharePointDestinationValidator? _spValidator;  // P14 SP-01 (Verify; null in tests)
    private readonly IMessenger _messenger;

    public SettingsViewModel(
        IAppConfig config,
        ISettingsRepository settings,
        ITaskTemplateRepository templates,
        IDefaultTaskSyncService sync,
        ITagRepository tags,
        IPcaContactRepository pca,
        IHolidayRepository holidays,
        IBackupService backup,
        ITeamRepository teams,
        IUserRepository users,
        IMessenger? messenger = null,
        IExportHubService? exportHub = null,
        IRetentionService? retention = null,
        ISharePointDestinationValidator? spValidator = null)
    {
        _config = config;
        _settings = settings;
        _templates = templates;
        _sync = sync;
        _tags = tags;
        _pca = pca;
        _backup = backup;
        _teams = teams;
        _users = users;
        _exportHub = exportHub;
        _retention = retention;
        _spValidator = spValidator;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        HolidayCalendar = new HolidayCalendarViewModel(holidays, _messenger);
    }

    [ObservableProperty] private string _dbPath = "";
    [ObservableProperty] private string _archivePath = "";
    [ObservableProperty] private int _warningDays = DefaultWarningDays;

    // --- P11 (EX-01): structured-export roots (shared/SharePoint + local) + manual "Export now" ---
    [ObservableProperty] private string _exportRoot1Path = "";
    [ObservableProperty] private string _exportRoot2Path = "";
    [ObservableProperty] private string _exportStatus = "";

    // P14 (SP-01): the SharePoint-folder Verify result — message + level ("Ok"/"Warning"/"Error") that
    // colors the status line (green/amber/red) in Settings.
    [ObservableProperty] private string _exportRoot1VerifyStatus = "";
    [ObservableProperty] private string _exportRoot1VerifyLevel = "";

    // The template editor overlay; null = hidden (mirrors BacklogsViewModel.Editor).
    [ObservableProperty] private TemplateEditorViewModel? _templateEditor;

    // All raw template rows (kept so BeginEditTemplate can hand the matching rows to ForEdit).
    private IReadOnlyList<TaskTemplate> _allTemplateRows = Array.Empty<TaskTemplate>();

    // Templates grouped for the Settings list: one entry per template name + its task count (SET-03).
    public ObservableCollection<TemplateSummary> TemplateGroups { get; } = new();

    // --- P8: Tags (TAG-01) / PCA contacts (TL-11) / Holiday calendar (HOL-01) ---

    public ObservableCollection<Tag> Tags { get; } = new();

    // Editable row wrappers so the Name TextBox is two-way bindable (PcaContact is an immutable record).
    public ObservableCollection<PcaContactRowVm> PcaContacts { get; } = new();

    // The tag editor overlay; null = hidden (mirrors TemplateEditor).
    [ObservableProperty] private TagEditorViewModel? _tagEditor;

    // New-PCA-contact input box (mirrors UsersViewModel.NewUserName).
    [ObservableProperty] private string _newPcaName = string.Empty;

    // Owned month-grid holiday calendar.
    public HolidayCalendarViewModel HolidayCalendar { get; }

    // --- P10: Teams (TM-03) ---

    // Editable row wrappers so the Name TextBox is two-way bindable (Team is an immutable record).
    public ObservableCollection<TeamRowVm> Teams { get; } = new();

    // New-team input box (mirrors NewPcaName).
    [ObservableProperty] private string _newTeamName = string.Empty;

    // The membership editor overlay; null = hidden (mirrors TemplateEditor/TagEditor).
    [ObservableProperty] private TeamMembershipEditorViewModel? _membershipEditor;

    // --- P9: Backup & Restore (BK-01..07) ---

    [ObservableProperty] private string _backupFolder = "";
    [ObservableProperty] private bool _autoBackupEnabled;
    [ObservableProperty] private int _backupKeepCount = 30;
    [ObservableProperty] private string _backupStatus = "";

    // Existing backups in the chosen folder (newest first); drives the list + per-row Restore.
    public ObservableCollection<BackupInfo> Backups { get; } = new();

    // Backup actions are gated until a folder is chosen (BK-01); bound to IsEnabled in XAML.
    public bool HasBackupFolder => !string.IsNullOrWhiteSpace(BackupFolder);

    partial void OnBackupFolderChanged(string value) => OnPropertyChanged(nameof(HasBackupFolder));

    // --- P12: Data retention (RT-01..07) — off by default; manual run + dry-run preview ---

    [ObservableProperty] private bool _retentionEnabled;
    [ObservableProperty] private int _retentionMonths = 3;
    [ObservableProperty] private string _retentionStatus = "";

    // Human-readable dry-run result (cutoff + per-month counts) from the last Preview.
    [ObservableProperty] private string _retentionPreviewText = "";

    public async Task LoadAsync()
    {
        DbPath = _config.DbPath;
        ArchivePath = _config.ArchivePath;
        ExportRoot1Path = _config.ExportRoot1Path;
        ExportRoot2Path = _config.ExportRoot2Path;

        var raw = await _settings.GetAsync(WarningDaysKey);
        WarningDays = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : DefaultWarningDays;

        BackupFolder = _config.BackupFolderPath;
        AutoBackupEnabled = _config.AutoBackupEnabled;
        BackupKeepCount = _config.BackupKeepCount;
        RefreshBackups();

        RetentionEnabled = _config.RetentionEnabled;
        RetentionMonths = _config.RetentionMonths;

        await ReloadTemplatesAsync();
        await ReloadTagsAsync();
        await ReloadPcaAsync();
        await ReloadTeamsAsync();
        await HolidayCalendar.LoadAsync();
    }

    // SET-01: app-local config only, never the shared Settings table.
    [RelayCommand]
    private Task ApplyDbPathAsync()
    {
        _config.SetDbPath(DbPath);   // IAppConfig persists path to %APPDATA% (P1 contract: get + SetDbPath)
        return Task.CompletedTask;
    }

    // SET-05: daily report archive folder — app-local config.
    [RelayCommand]
    private Task ApplyArchivePathAsync()
    {
        _config.SetArchivePath(ArchivePath);
        return Task.CompletedTask;
    }

    // EX-01: persist the two structured-export roots app-local (mirror ArchivePath).
    [RelayCommand]
    private Task ApplyExportRoot1Async()
    {
        _config.SetExportRoot1Path(ExportRoot1Path);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task ApplyExportRoot2Async()
    {
        _config.SetExportRoot2Path(ExportRoot2Path);
        return Task.CompletedTask;
    }

    // SP-01: verify the SharePoint folder is a usable write destination before exporting. _spValidator is
    // null only in unit tests that don't inject it.
    [RelayCommand]
    private void VerifyExportRoot1()
    {
        if (_spValidator is null)
        {
            ExportRoot1VerifyLevel = "Error";
            ExportRoot1VerifyStatus = "Verification is not available.";
            return;
        }

        var result = _spValidator.Verify(ExportRoot1Path);
        ExportRoot1VerifyLevel = result.Level.ToString();
        ExportRoot1VerifyStatus = result.Message;
    }

    // EX-06: manual "Export now" — regenerate the structured per-team export into every configured root
    // via the hub (W3 wiring). _exportHub is null only in unit tests that don't inject it.
    [RelayCommand]
    private async Task ExportNowAsync()
    {
        if (_exportHub is null)
        {
            ExportStatus = "Export now is not available.";
            return;
        }

        ExportStatus = "Exporting...";
        try
        {
            ExportStatus = await _exportHub.ExportNowAsync();
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export failed: {ex.Message}";
        }
    }

    // ---------- P9: Backup & Restore (BK-01..06) ----------

    // BK-01: persist the 3 backup settings app-local + refresh the list against the new folder.
    [RelayCommand]
    private void ApplyBackupSettings()
    {
        var keep = BackupKeepCount < 1 ? 1 : BackupKeepCount;
        BackupKeepCount = keep;
        _config.SetBackupFolderPath(BackupFolder);
        _config.SetAutoBackupEnabled(AutoBackupEnabled);
        _config.SetBackupKeepCount(keep);
        RefreshBackups();
        BackupStatus = "Backup settings saved.";
    }

    // BK-02: manual backup of the live .db to the chosen folder.
    [RelayCommand]
    private async Task BackupNowAsync()
    {
        var path = await _backup.BackupNowAsync();
        BackupStatus = path is null
            ? "No backup made — choose a folder and make sure the database file exists."
            : $"Backup created: {System.IO.Path.GetFileName(path)}";
        RefreshBackups();
    }

    // BK-04: re-read the folder contents.
    [RelayCommand]
    private void RefreshBackups()
    {
        Backups.Clear();
        foreach (var b in _backup.ListBackups() ?? Array.Empty<BackupInfo>()) Backups.Add(b);
    }

    // BK-05: restore a chosen backup. Confirmation is owned by the View (WPF dialog); this method
    // performs the restore and reports the restart instruction. Safety copy is made by the service.
    [RelayCommand]
    private async Task RestoreAsync(BackupInfo? backup)
    {
        if (backup is null) return;
        try
        {
            await _backup.RestoreAsync(backup.Path);
            BackupStatus = "Restore done — please restart the app for it to take effect.";
        }
        catch (Exception ex)
        {
            BackupStatus = $"Restore failed: {ex.Message}";
        }
    }

    // ---------- P12: Data retention (RT-01..07) ----------

    // Persist enable + months app-local (mirrors ApplyBackupSettings). Months are clamped to >= 1.
    [RelayCommand]
    private void ApplyRetentionSettings()
    {
        var months = RetentionMonths < 1 ? 1 : RetentionMonths;
        RetentionMonths = months;
        _config.SetRetentionEnabled(RetentionEnabled);
        _config.SetRetentionMonths(months);
        RetentionStatus = "Retention settings saved.";
    }

    // Dry-run (SUGGESTION-4): calls ONLY PreviewAsync (write-free) and formats cutoff + per-month
    // counts. Never invokes EnsureRetentionAsync. _retention is null only in tests that don't inject it.
    [RelayCommand]
    private async Task PreviewRetentionAsync()
    {
        if (_retention is null)
        {
            RetentionStatus = "Retention is not available.";
            return;
        }

        RetentionStatus = "Previewing...";
        try
        {
            var preview = await _retention.PreviewAsync();
            if (preview.Months.Count == 0)
            {
                RetentionPreviewText = $"Nothing to prune (cutoff {preview.Cutoff}).";
                RetentionStatus = "Preview: nothing older than the live window.";
                return;
            }

            var lines = new System.Text.StringBuilder();
            lines.AppendLine($"Cutoff {preview.Cutoff} — months at or before this would be pruned:");
            foreach (var m in preview.Months)
                lines.AppendLine(
                    $"  {m.Month}: {m.StandupIssues} standup issues, {m.StandupEntries} standup entries, " +
                    $"{m.TimeLogs} time logs, {m.Tasks} tasks, {m.Backlogs} backlogs");

            RetentionPreviewText = lines.ToString().TrimEnd();
            RetentionStatus = $"Preview: {preview.Months.Count} month(s) would be pruned.";
        }
        catch (Exception ex)
        {
            RetentionPreviewText = "";
            RetentionStatus = $"Preview failed: {ex.Message}";
        }
    }

    // Destructive run. Confirmation is owned by the View (WPF dialog, mirrors Restore); this method
    // archives→verifies→deletes via the service and surfaces its status string.
    [RelayCommand]
    private async Task RunRetentionAsync()
    {
        if (_retention is null)
        {
            RetentionStatus = "Retention is not available.";
            return;
        }

        RetentionStatus = "Running retention...";
        try
        {
            RetentionStatus = await _retention.EnsureRetentionAsync();
        }
        catch (Exception ex)
        {
            RetentionStatus = $"Retention failed: {ex.Message}";
        }
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
        // Need a name + ≥1 task. Surface why (was a silent no-op) instead of just returning.
        if (string.IsNullOrWhiteSpace(name))
        {
            editor.ErrorMessage = "Template name is required.";
            return;
        }
        if (taskNames.Count == 0)
        {
            editor.ErrorMessage = "A template must have at least one task.";
            return;
        }
        editor.ErrorMessage = string.Empty;

        // Edit = delete-then-reinsert all rows (handles rename + reorder + add/remove in one shot).
        if (editor.IsEditMode)
            await _templates.DeleteByTemplateNameAsync(editor.OriginalTemplateName);

        for (var i = 0; i < taskNames.Count; i++)
            await _templates.InsertAsync(new TaskTemplate(0, name, taskNames[i], i));

        TemplateEditor = null;
        await ReloadTemplatesAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Templates)); // live-sync: Backlog template list
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

    // SET-04: reconcile DefaultTasks -> DEFAULT backlog's Tasks (rename = soft-delete + insert).
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

    // ---------- TAG-01: custom tag CRUD (hard-delete; repo cascades BacklogTags links) ----------

    [RelayCommand]
    private void BeginCreateTag() => TagEditor = TagEditorViewModel.ForCreate();

    [RelayCommand]
    private void BeginEditTag(Tag tag)
    {
        if (tag is null) return;
        TagEditor = TagEditorViewModel.ForEdit(tag);
    }

    [RelayCommand]
    private async Task SaveTagAsync()
    {
        var editor = TagEditor;
        if (editor is null) return;

        var text = editor.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;     // a tag needs a label

        var icon = editor.Icon.Trim();
        var color = string.IsNullOrWhiteSpace(editor.Color) ? "#64748B" : editor.Color.Trim();

        if (editor.IsEditMode)
            await _tags.UpdateAsync(new Tag(editor.TagId, text, icon, color, DateTimeOffset.UtcNow));
        else
            await _tags.InsertAsync(new Tag(0, text, icon, color, DateTimeOffset.UtcNow));

        TagEditor = null;
        await ReloadTagsAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Tags)); // live-sync: editor + Task List chips
    }

    [RelayCommand]
    private void CancelTag() => TagEditor = null;

    [RelayCommand]
    private async Task DeleteTagAsync(int tagId)
    {
        await _tags.DeleteAsync(tagId);   // cascades BacklogTags links in one tx
        await ReloadTagsAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Tags));
    }

    private async Task ReloadTagsAsync()
    {
        var rows = await _tags.GetAllAsync();
        Tags.Clear();
        foreach (var t in rows) Tags.Add(t);
    }

    // ---------- TL-11: PCA contacts (soft-delete, mirrors UsersViewModel) ----------

    [RelayCommand]
    private async Task AddPcaContactAsync()
    {
        var name = NewPcaName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        await _pca.InsertAsync(new PcaContact(0, name, true));
        NewPcaName = string.Empty;
        await ReloadPcaAsync();
        _messenger.Send(new DataChangedMessage(DataKind.PcaContacts));
    }

    [RelayCommand]
    private async Task RenamePcaContactAsync(int id)
    {
        var contact = PcaContacts.FirstOrDefault(c => c.Id == id);
        if (contact is null) return;
        var name = contact.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        await _pca.UpdateNameAsync(id, name);
        await ReloadPcaAsync();
        _messenger.Send(new DataChangedMessage(DataKind.PcaContacts));
    }

    [RelayCommand]
    private async Task DeactivatePcaContactAsync(int id)
    {
        await _pca.SetActiveAsync(id, false);   // soft-delete: historical backlogs keep the reference
        await ReloadPcaAsync();
        _messenger.Send(new DataChangedMessage(DataKind.PcaContacts));
    }

    private async Task ReloadPcaAsync()
    {
        var rows = await _pca.GetAllAsync();   // incl. inactive (Settings list)
        PcaContacts.Clear();
        foreach (var p in rows) PcaContacts.Add(new PcaContactRowVm(p.Id, p.Name, p.IsActive));
    }

    // ---------- TM-03: teams (soft-delete CRUD, mirrors PCA) + membership overlay ----------

    [RelayCommand]
    private async Task AddTeamAsync()
    {
        var name = NewTeamName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        var newId = await _teams.InsertAsync(new Team(0, name, true, DateTimeOffset.UtcNow));
        // TM-04: a new team gets its own DEFAULT backlog + the global default tasks materialized under it.
        await _sync.EnsureDefaultBacklogIdAsync(newId);
        await _sync.SyncAsync();

        NewTeamName = string.Empty;
        await ReloadTeamsAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Teams));
    }

    [RelayCommand]
    private async Task RenameTeamAsync(int id)
    {
        var team = Teams.FirstOrDefault(t => t.Id == id);
        if (team is null) return;
        var name = team.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        await _teams.UpdateNameAsync(id, name);
        await ReloadTeamsAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Teams));
    }

    [RelayCommand]
    private async Task DeactivateTeamAsync(int id)
    {
        await _teams.SetActiveAsync(id, false);   // soft-delete: historical backlogs/standup keep the reference
        await ReloadTeamsAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Teams));
    }

    // Membership editor: open an overlay seeded with all active users, pre-checking current members.
    [RelayCommand]
    private async Task BeginEditMembersAsync(int id)
    {
        var team = await _teams.GetByIdAsync(id);
        if (team is null) return;
        var users = await _users.GetActiveAsync();
        var members = await _teams.GetUserIdsForTeamAsync(id);
        MembershipEditor = TeamMembershipEditorViewModel.ForTeam(team, users, members);
    }

    [RelayCommand]
    private async Task SaveMembersAsync()
    {
        var editor = MembershipEditor;
        if (editor is null) return;

        await _teams.SetMembersAsync(editor.TeamId, editor.CheckedUserIds);   // replace-all in one tx
        MembershipEditor = null;
        _messenger.Send(new DataChangedMessage(DataKind.Teams));
    }

    [RelayCommand]
    private void CancelMembers() => MembershipEditor = null;

    private async Task ReloadTeamsAsync()
    {
        var rows = await _teams.GetAllAsync();   // incl. inactive (Settings list)
        Teams.Clear();
        foreach (var t in rows) Teams.Add(new TeamRowVm(t.Id, t.Name, t.IsActive));
    }
}

// Editable row for the Settings Teams list (TM-03): a mutable Name TextBox + the immutable id/active flag.
public sealed partial class TeamRowVm : ObservableObject
{
    public TeamRowVm(int id, string name, bool isActive)
    {
        Id = id;
        _name = name;
        IsActive = isActive;
    }

    public int Id { get; }
    public bool IsActive { get; }

    [ObservableProperty] private string _name;
}

// Editable row for the Settings PCA list (TL-11): a mutable Name TextBox + the immutable id/active flag.
public sealed partial class PcaContactRowVm : ObservableObject
{
    public PcaContactRowVm(int id, string name, bool isActive)
    {
        Id = id;
        _name = name;
        IsActive = isActive;
    }

    public int Id { get; }
    public bool IsActive { get; }

    [ObservableProperty] private string _name;
}
