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

    // BK-01/03/06: an existing config file lacking the new backup keys loads with defaults applied.
    [Fact]
    public void Backup_Keys_Default_When_Missing_From_Existing_Config()
    {
        File.WriteAllText(_configPath, "{\"DbPath\":\"C:\\\\x\\\\timesheet.db\"}");
        var cfg = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");

        Assert.Equal(@"C:\x\timesheet.db", cfg.DbPath);
        Assert.Equal("", cfg.BackupFolderPath);
        Assert.False(cfg.AutoBackupEnabled);
        Assert.Equal(30, cfg.BackupKeepCount);
        Assert.Equal(0, cfg.ActiveTeamId); // P10: missing key -> 0 (unset)
    }

    // P10 (TM-05): the active team persists app-locally and survives a reload.
    [Fact]
    public void ActiveTeamId_Persists_And_Survives_Reload()
    {
        var cfg = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        Assert.Equal(0, cfg.ActiveTeamId); // default on a brand-new config

        cfg.SetActiveTeamId(7);

        var reloaded = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        Assert.Equal(7, reloaded.ActiveTeamId);
    }

    [Fact]
    public void Backup_Settings_Persist_And_Survive_Reload()
    {
        var cfg = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        cfg.SetBackupFolderPath(@"E:\backups");
        cfg.SetAutoBackupEnabled(true);
        cfg.SetBackupKeepCount(15);

        var reloaded = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"E:\backups", reloaded.BackupFolderPath);
        Assert.True(reloaded.AutoBackupEnabled);
        Assert.Equal(15, reloaded.BackupKeepCount);
    }

    // P11 (EX-01): export roots persist app-local and survive a reload.
    [Fact]
    public void ExportRoots_Persist_And_Survive_Reload()
    {
        var cfg = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        cfg.SetExportRoot1Path(@"D:\SharePoint\TimesheetApp");
        cfg.SetExportRoot2Path(@"E:\Local\TimesheetApp");

        var reloaded = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"D:\SharePoint\TimesheetApp", reloaded.ExportRoot1Path);
        Assert.Equal(@"E:\Local\TimesheetApp", reloaded.ExportRoot2Path);
    }

    // P11 (EX-01): an old config file lacking the export-root keys loads with "" defaults (backward-compat).
    [Fact]
    public void ExportRoots_Default_To_Empty_When_Missing_From_Existing_Config()
    {
        File.WriteAllText(_configPath, "{\"DbPath\":\"C:\\\\x\\\\timesheet.db\"}");
        var cfg = new JsonAppConfig(_configPath, defaultDbPath: @"C:\shared\timesheet.db");

        Assert.Equal("", cfg.ExportRoot1Path);
        Assert.Equal("", cfg.ExportRoot2Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
