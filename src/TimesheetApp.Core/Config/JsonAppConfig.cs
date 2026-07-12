using System.IO;
using System.Text.Json;

namespace TimesheetApp.Config;

// Reads/writes %APPDATA%\TimesheetApp\appsettings.json. The default ctor resolves the
// canonical app-local path; the (path, default) ctor is the test/DI seam.
public sealed class JsonAppConfig : IAppConfig
{
    // Nullable backup keys default to null in old config files -> tolerated (defaults applied on load).
    //
    // M8.2 (Wave 4): ActiveTeamId was REMOVED from this record (it moved to Users.active_team_id).
    // Every already-installed appsettings.json still carries an "ActiveTeamId" key. System.Text.Json
    // SKIPS unmapped members by default (UnmappedMemberHandling.Skip), so the stale key deserializes
    // harmlessly and the next Save() drops it. This is load-bearing: LoadModel swallows JsonException
    // and returns null, which would silently reset DbPath to the default — i.e. point every upgrading
    // user at an EMPTY database. Locked down by Legacy_ActiveTeamId_Key_Is_Ignored_And_DbPath_Survives.
    private sealed record Model(
        string DbPath,
        string? ArchivePath = null,
        string? BackupFolderPath = null,
        bool? AutoBackupEnabled = null,
        int? BackupKeepCount = null,
        string? ExportRoot1Path = null,
        string? ExportRoot2Path = null,
        bool? RetentionEnabled = null,
        int? RetentionMonths = null,
        bool? IsDarkMode = null);

    // P9 (BK-06): default retention when no value persisted yet.
    private const int DefaultBackupKeepCount = 30;

    // P12 (RT-01): default retention window when no value persisted yet.
    private const int DefaultRetentionMonths = 3;

    private readonly string _configPath;
    private string _dbPath;
    private string _archivePath;
    private string _backupFolderPath;
    private bool _autoBackupEnabled;
    private int _backupKeepCount;
    private string _exportRoot1Path;
    private string _exportRoot2Path;
    private bool _retentionEnabled;
    private int _retentionMonths;
    private bool _isDarkMode;

    public JsonAppConfig()
        : this(DefaultConfigPath(), DefaultDbPath())
    {
    }

    public JsonAppConfig(string configPath, string defaultDbPath)
    {
        _configPath = configPath;
        var model = LoadModel(configPath);
        _dbPath = model?.DbPath ?? defaultDbPath;
        _archivePath = model?.ArchivePath ?? "";
        _backupFolderPath = model?.BackupFolderPath ?? "";
        _autoBackupEnabled = model?.AutoBackupEnabled ?? false;
        _backupKeepCount = model?.BackupKeepCount ?? DefaultBackupKeepCount;
        _exportRoot1Path = model?.ExportRoot1Path ?? "";
        _exportRoot2Path = model?.ExportRoot2Path ?? "";
        _retentionEnabled = model?.RetentionEnabled ?? false;
        _retentionMonths = model?.RetentionMonths ?? DefaultRetentionMonths;
        _isDarkMode = model?.IsDarkMode ?? false;
    }

    public string DbPath => _dbPath;
    public string ArchivePath => _archivePath;
    public string BackupFolderPath => _backupFolderPath;
    public bool AutoBackupEnabled => _autoBackupEnabled;
    public int BackupKeepCount => _backupKeepCount;
    public string ExportRoot1Path => _exportRoot1Path;
    public string ExportRoot2Path => _exportRoot2Path;
    public bool RetentionEnabled => _retentionEnabled;
    public int RetentionMonths => _retentionMonths;
    public bool IsDarkMode => _isDarkMode;

    public void SetDbPath(string dbPath)
    {
        _dbPath = dbPath;
        Save();
    }

    public void SetArchivePath(string archivePath)
    {
        _archivePath = archivePath;
        Save();
    }

    public void SetBackupFolderPath(string backupFolderPath)
    {
        _backupFolderPath = backupFolderPath;
        Save();
    }

    public void SetAutoBackupEnabled(bool enabled)
    {
        _autoBackupEnabled = enabled;
        Save();
    }

    public void SetBackupKeepCount(int keepCount)
    {
        _backupKeepCount = keepCount;
        Save();
    }

    public void SetExportRoot1Path(string path)
    {
        _exportRoot1Path = path;
        Save();
    }

    public void SetExportRoot2Path(string path)
    {
        _exportRoot2Path = path;
        Save();
    }

    public void SetRetentionEnabled(bool enabled)
    {
        _retentionEnabled = enabled;
        Save();
    }

    public void SetRetentionMonths(int months)
    {
        _retentionMonths = months;
        Save();
    }

    public void SetIsDarkMode(bool dark)
    {
        _isDarkMode = dark;
        Save();
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var model = new Model(
            _dbPath,
            string.IsNullOrWhiteSpace(_archivePath) ? null : _archivePath,
            string.IsNullOrWhiteSpace(_backupFolderPath) ? null : _backupFolderPath,
            _autoBackupEnabled,
            _backupKeepCount,
            string.IsNullOrWhiteSpace(_exportRoot1Path) ? null : _exportRoot1Path,
            string.IsNullOrWhiteSpace(_exportRoot2Path) ? null : _exportRoot2Path,
            _retentionEnabled,
            _retentionMonths,
            _isDarkMode);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }

    private static Model? LoadModel(string configPath)
    {
        if (!File.Exists(configPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<Model>(File.ReadAllText(configPath));
        }
        catch (JsonException)
        {
            return null; // corrupt config -> fall back to default
        }
    }

    private static string DefaultConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "TimesheetApp", "appsettings.json");
    }

    private static string DefaultDbPath()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "TimesheetApp", "timesheet.db");
    }
}
