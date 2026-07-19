using System.IO;
using System.Text.Json;

namespace TimesheetApp.Config;

// M11: reads/writes %APPDATA%\TimesheetApp\local-settings.json -- renamed from "appsettings.json" (F5,
// fast-lane-settings-appsettings.json) because that name collided with TimesheetApp.Api's OWN
// appsettings.json (real ASP.NET Core configuration, a completely different file/format). This class is
// the WRITABLE POLICY STORE: BackupFolderPath/AutoBackupEnabled/BackupKeepCount, ExportRoot1/2Path,
// RetentionEnabled/RetentionMonths. It is never read by builder.Configuration.
//
// DbPath is NO LONGER read from this store (F2, "appsettings.json WINS over the persisted store" --
// docs/superpowers/specs/2026-07-19-m11-configuration-design.md). The `dbPath` ctor argument is
// AUTHORITATIVE, not a default: Program.cs resolves it from IConfiguration (required, fail-fast if
// missing) and it always wins over whatever this file has on disk. ArchivePath keeps its old
// persisted-store behaviour UNLESS `archivePathOverride` is supplied (Program.cs passes
// TimesheetApp:ArchivePath when set; it is optional, unlike DbPath).
public sealed class JsonAppConfig : IAppConfig
{
    // Nullable backup keys default to null in old config files -> tolerated (defaults applied on load).
    //
    // M8.2 (Wave 4): ActiveTeamId was REMOVED from this record (it moved to Users.active_team_id).
    // Every already-installed appsettings.json still carries an "ActiveTeamId" key. System.Text.Json
    // SKIPS unmapped members by default (UnmappedMemberHandling.Skip), so the stale key deserializes
    // harmlessly and the next Save() drops it. This is load-bearing: LoadModel swallows JsonException
    // and returns null, which would silently reset every persisted policy key to its default — e.g. an
    // upgrading user's BackupFolderPath reverting to unset. Locked down by
    // Legacy_ActiveTeamId_Key_Is_Ignored_And_Persisted_Policy_Keys_Survive.
    private sealed record Model(
        string DbPath,
        string? ArchivePath = null,
        string? BackupFolderPath = null,
        bool? AutoBackupEnabled = null,
        int? BackupKeepCount = null,
        string? ExportRoot1Path = null,
        string? ExportRoot2Path = null,
        bool? RetentionEnabled = null,
        int? RetentionMonths = null);

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

    // M11: the parameterless ctor (implicit %APPDATA% defaults, used when Program.cs had no explicit
    // config) is GONE. ConfigPath/DbPath/KeyRingPath are now required IConfiguration keys with no fallback
    // chain (F1) -- Program.cs refuses to start rather than construct this with a guessed default. Every
    // caller now supplies both paths explicitly.
    public JsonAppConfig(string configPath, string dbPath, string? archivePathOverride = null)
    {
        _configPath = configPath;

        // F5 migration: on the CANONICAL app-local location only (never a test's temp directory, never an
        // operator's custom ConfigPath -- both simply never equal DefaultConfigPath()), transparently carry
        // a pre-M11 file's policy settings forward to the new name so nobody loses BackupFolderPath /
        // ExportRoot* / RetentionEnabled on upgrade.
        if (string.Equals(configPath, DefaultConfigPath(), StringComparison.OrdinalIgnoreCase))
            MigrateLegacyStoreIfNeeded(LegacyConfigPath(), configPath);

        var model = LoadModel(configPath);

        // F2 fix: `dbPath` is AUTHORITATIVE. The old `model?.DbPath ?? dbPath` let a persisted file outrank
        // the caller-supplied value -- on any machine that had run the app before, a DbPath passed in here
        // (i.e. from appsettings.json) was dead weight. Never again: this is the one line that fix is.
        _dbPath = dbPath;

        _archivePath = !string.IsNullOrWhiteSpace(archivePathOverride) ? archivePathOverride! : (model?.ArchivePath ?? "");
        _backupFolderPath = model?.BackupFolderPath ?? "";
        _autoBackupEnabled = model?.AutoBackupEnabled ?? false;
        _backupKeepCount = model?.BackupKeepCount ?? DefaultBackupKeepCount;
        _exportRoot1Path = model?.ExportRoot1Path ?? "";
        _exportRoot2Path = model?.ExportRoot2Path ?? "";
        _retentionEnabled = model?.RetentionEnabled ?? false;
        _retentionMonths = model?.RetentionMonths ?? DefaultRetentionMonths;
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
            _retentionMonths);
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

    // Public: Program.cs prints this as the suggested/legacy value in its fail-fast message when
    // TimesheetApp:ConfigPath is missing (F1), and the ctor above uses it to gate the F5 migration.
    public static string DefaultConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "TimesheetApp", "local-settings.json");
    }

    // Public: Program.cs prints this as the suggested/legacy value in its fail-fast message when
    // TimesheetApp:DbPath is missing (F1). Unchanged by the F5 rename -- only the CONFIG file's name
    // collided with ASP.NET Core's appsettings.json; the database file did not.
    public static string DefaultDbPath()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "TimesheetApp", "timesheet.db");
    }

    // F5: the pre-M11 name, at the same %APPDATA% location DefaultConfigPath() used to resolve to before
    // the rename. Private -- only the migration below needs it. (Program.cs's fail-fast message computes
    // the same literal value itself for the operator-facing hint; duplicating one Path.Combine is cheaper
    // than growing this class's public surface for a one-time historical constant.)
    private static string LegacyConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "TimesheetApp", "appsettings.json");
    }

    // F5: "migrate an existing file to the new name on first start so nobody loses their policy settings."
    // COPY, not move/rename -- the legacy file is left in place as a safety net; this must never be
    // destructive. Internal (not private) so JsonAppConfigTests can pin the mechanism directly with fully
    // controlled temp paths, without ever touching the real %APPDATA%.
    internal static void MigrateLegacyStoreIfNeeded(string legacyPath, string newConfigPath)
    {
        if (File.Exists(newConfigPath)) return;   // already migrated, or created fresh -- never overwrite.
        if (!File.Exists(legacyPath)) return;     // nothing to migrate.
        if (string.Equals(Path.GetFullPath(legacyPath), Path.GetFullPath(newConfigPath),
                StringComparison.OrdinalIgnoreCase))
            return;   // same file under both names (e.g. an operator kept the legacy filename) -- no-op.

        var dir = Path.GetDirectoryName(newConfigPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        try { File.Copy(legacyPath, newConfigPath); }
        catch (IOException) { /* best-effort; a fresh store with defaults is not data loss of anything real */ }
    }
}
