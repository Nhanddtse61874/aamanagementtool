using System.IO;
using System.Text.Json;

namespace TimesheetApp.Config;

// Reads/writes %APPDATA%\TimesheetApp\appsettings.json. The default ctor resolves the
// canonical app-local path; the (path, default) ctor is the test/DI seam.
public sealed class JsonAppConfig : IAppConfig
{
    private sealed record Model(string DbPath);

    private readonly string _configPath;
    private string _dbPath;

    public JsonAppConfig()
        : this(DefaultConfigPath(), DefaultDbPath())
    {
    }

    public JsonAppConfig(string configPath, string defaultDbPath)
    {
        _configPath = configPath;
        _dbPath = Load(configPath) ?? defaultDbPath;
    }

    public string DbPath => _dbPath;

    public void SetDbPath(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new Model(dbPath),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }

    private static string? Load(string configPath)
    {
        if (!File.Exists(configPath)) return null;
        try
        {
            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(configPath));
            return string.IsNullOrWhiteSpace(model?.DbPath) ? null : model!.DbPath;
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
