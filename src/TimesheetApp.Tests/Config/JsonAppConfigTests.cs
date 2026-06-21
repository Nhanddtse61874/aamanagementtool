using Xunit;
using TimesheetApp.Config;

namespace TimesheetApp.Tests.Config;

public class JsonAppConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public JsonAppConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "appsettings.json");
    }

    [Fact]
    public void First_Run_Returns_Default_Path_When_No_File_Exists()
    {
        var cfg = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"C:\shared\timesheet.db", cfg.DbPath);
    }

    [Fact]
    public void SetDbPath_Persists_To_Json_And_Survives_Reload()
    {
        var cfg = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        cfg.SetDbPath(@"D:\OneDrive\team\timesheet.db");

        Assert.True(File.Exists(_configPath));

        var reloaded = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"D:\OneDrive\team\timesheet.db", reloaded.DbPath);
    }

    [Fact]
    public void SetDbPath_Creates_Parent_Directory_If_Missing()
    {
        var nested = Path.Combine(_dir, "sub", "deep", "appsettings.json");
        var cfg = new JsonAppConfig(nested, defaultDbPath: @"C:\shared\timesheet.db");
        cfg.SetDbPath(@"E:\x\timesheet.db");
        Assert.True(File.Exists(nested));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
