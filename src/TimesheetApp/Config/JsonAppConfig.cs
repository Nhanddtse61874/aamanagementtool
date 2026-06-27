using System.IO;
using System.Text.Json;

namespace TimesheetApp.Config;

// Reads/writes %APPDATA%\TimesheetApp\appsettings.json. The default ctor resolves the
// canonical app-local path; the (path, default) ctor is the test/DI seam.
public sealed class JsonAppConfig : IAppConfig
{
    private sealed record Model(string DbPath, string? ArchivePath = null);

    private readonly string _configPath;
    private string _dbPath;
    private string _archivePath;

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
    }

    public string DbPath => _dbPath;
    public string ArchivePath => _archivePath;

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

    private void Save()
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var model = new Model(_dbPath, string.IsNullOrWhiteSpace(_archivePath) ? null : _archivePath);
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
