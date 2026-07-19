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
        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"C:\shared\timesheet.db", cfg.DbPath);
    }

    // F2 fix (M11): the ctor argument is now AUTHORITATIVE. SetDbPath still WRITES the file (kept for the
    // writable-store mechanics; no production caller uses it any more), but reloading with the SAME ctor
    // argument as before returns THAT argument, not the persisted value -- this is the fix, not a
    // regression: appsettings.json (the ctor argument, in production) must win every time, or DbPath in
    // appsettings.json is dead weight on any machine that has run the app before (the exact F2 bug).
    [Fact]
    public void SetDbPath_Persists_To_Json_But_A_Later_Construction_Uses_Its_Own_Ctor_Argument()
    {
        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");
        cfg.SetDbPath(@"D:\OneDrive\team\timesheet.db");

        Assert.True(File.Exists(_configPath));
        Assert.Contains("OneDrive", File.ReadAllText(_configPath));

        var reloaded = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"C:\shared\timesheet.db", reloaded.DbPath);
    }

    // F2 (fast-lane-settings-appsettings.json): "on any machine that has run the app before, a DbPath in
    // appsettings.json is dead weight" was the bug -- the persisted store outranked the passed argument.
    // This pins the fix directly: a persisted store carrying a DIFFERENT DbPath must NOT win.
    [Fact]
    public void DbPath_Ctor_Argument_Wins_Over_A_Stale_Persisted_Store_Carrying_A_Different_DbPath()
    {
        File.WriteAllText(_configPath, "{\"DbPath\":\"D:\\\\OneDrive\\\\team\\\\timesheet.db\"}");

        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");

        Assert.Equal(@"C:\shared\timesheet.db", cfg.DbPath);
    }

    [Fact]
    public void SetDbPath_Creates_Parent_Directory_If_Missing()
    {
        var nested = Path.Combine(_dir, "sub", "deep", "appsettings.json");
        var cfg = new JsonAppConfig(nested, dbPath: @"C:\shared\timesheet.db");
        cfg.SetDbPath(@"E:\x\timesheet.db");
        Assert.True(File.Exists(nested));
    }

    // BK-01/03/06: an existing config file lacking the new backup keys loads with defaults applied.
    [Fact]
    public void Backup_Keys_Default_When_Missing_From_Existing_Config()
    {
        File.WriteAllText(_configPath, "{\"DbPath\":\"C:\\\\x\\\\timesheet.db\"}");
        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");

        // M11 (F2): DbPath no longer reads from the persisted store at all -- the ctor argument always wins.
        Assert.Equal(@"C:\shared\timesheet.db", cfg.DbPath);
        Assert.Equal("", cfg.BackupFolderPath);
        Assert.False(cfg.AutoBackupEnabled);
        Assert.Equal(30, cfg.BackupKeepCount);
    }

    // M8.2 (Wave 4) UPGRADE SAFETY. ActiveTeamId moved to Users.active_team_id and was deleted from the
    // config Model record — but EVERY already-installed store still contains the key. If an unmapped
    // member threw, LoadModel's `catch (JsonException) -> return null` would swallow it and fall back to
    // EVERY persisted policy key's default -- e.g. an upgrading user's BackupKeepCount silently reverting.
    // System.Text.Json skips unmapped members by default, so it does not throw — this test pins that
    // behaviour so nobody later "hardens" the deserializer into a data-loss bug.
    //
    // M11 update: the original version of this test also asserted DbPath survived FROM the persisted
    // store -- that was F2's bug (see the tests above). DbPath here now comes from the ctor argument only.
    [Fact]
    public void Legacy_ActiveTeamId_Key_Is_Ignored_And_Persisted_Policy_Keys_Survive()
    {
        File.WriteAllText(_configPath,
            "{\"DbPath\":\"D:\\\\OneDrive\\\\team\\\\timesheet.db\",\"ActiveTeamId\":7,\"BackupKeepCount\":15}");

        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");

        // M11 (F2): DbPath is the ctor argument, not the stale persisted value.
        Assert.Equal(@"C:\shared\timesheet.db", cfg.DbPath);
        // BackupKeepCount is still a persisted policy key; the unmapped stale key does not disturb it.
        Assert.Equal(15, cfg.BackupKeepCount);

        // And the next write drops the dead key rather than preserving it forever.
        cfg.SetBackupKeepCount(20);
        Assert.DoesNotContain("ActiveTeamId", File.ReadAllText(_configPath));
    }

    [Fact]
    public void Backup_Settings_Persist_And_Survive_Reload()
    {
        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");
        cfg.SetBackupFolderPath(@"E:\backups");
        cfg.SetAutoBackupEnabled(true);
        cfg.SetBackupKeepCount(15);

        var reloaded = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"E:\backups", reloaded.BackupFolderPath);
        Assert.True(reloaded.AutoBackupEnabled);
        Assert.Equal(15, reloaded.BackupKeepCount);
    }

    // P11 (EX-01): export roots persist app-local and survive a reload.
    [Fact]
    public void ExportRoots_Persist_And_Survive_Reload()
    {
        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");
        cfg.SetExportRoot1Path(@"D:\SharePoint\TimesheetApp");
        cfg.SetExportRoot2Path(@"E:\Local\TimesheetApp");

        var reloaded = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"D:\SharePoint\TimesheetApp", reloaded.ExportRoot1Path);
        Assert.Equal(@"E:\Local\TimesheetApp", reloaded.ExportRoot2Path);
    }

    // P11 (EX-01): an old config file lacking the export-root keys loads with "" defaults (backward-compat).
    [Fact]
    public void ExportRoots_Default_To_Empty_When_Missing_From_Existing_Config()
    {
        File.WriteAllText(_configPath, "{\"DbPath\":\"C:\\\\x\\\\timesheet.db\"}");
        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");

        Assert.Equal("", cfg.ExportRoot1Path);
        Assert.Equal("", cfg.ExportRoot2Path);
    }

    // P12 (RT-01): retention keys persist app-local and survive a reload.
    [Fact]
    public void Retention_Settings_Persist_And_Survive_Reload()
    {
        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");
        Assert.False(cfg.RetentionEnabled); // default OFF (destructive)
        Assert.Equal(3, cfg.RetentionMonths); // default 3-month window

        cfg.SetRetentionEnabled(true);
        cfg.SetRetentionMonths(6);

        var reloaded = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");
        Assert.True(reloaded.RetentionEnabled);
        Assert.Equal(6, reloaded.RetentionMonths);
    }

    // P12 (RT-01): an old config file lacking the retention keys loads with off / 3 (backward-compat).
    [Fact]
    public void Retention_Keys_Default_When_Missing_From_Existing_Config()
    {
        File.WriteAllText(_configPath, "{\"DbPath\":\"C:\\\\x\\\\timesheet.db\"}");
        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");

        Assert.False(cfg.RetentionEnabled);
        Assert.Equal(3, cfg.RetentionMonths);
    }

    // M11: ArchivePath is optionally sourced from IConfiguration too (unlike DbPath, NOT required -- see
    // IAppConfig.ArchivePath's doc comment). When Program.cs supplies a non-blank override, it wins over
    // whatever the persisted store has, matching the "locations win" pattern the rest of the milestone
    // establishes (2026-07-19-m11-configuration-design.md §3.1).
    [Fact]
    public void ArchivePath_Override_Wins_Over_The_Persisted_Store_When_Supplied()
    {
        File.WriteAllText(_configPath,
            "{\"DbPath\":\"C:\\\\x\\\\timesheet.db\",\"ArchivePath\":\"E:\\\\persisted\"}");

        var cfg = new JsonAppConfig(
            _configPath, dbPath: @"C:\shared\timesheet.db", archivePathOverride: @"F:\configured");

        Assert.Equal(@"F:\configured", cfg.ArchivePath);
    }

    // The converse: absent, the persisted store still applies exactly as before M11.
    [Fact]
    public void ArchivePath_Falls_Back_To_The_Persisted_Store_When_No_Override_Is_Supplied()
    {
        File.WriteAllText(_configPath,
            "{\"DbPath\":\"C:\\\\x\\\\timesheet.db\",\"ArchivePath\":\"E:\\\\persisted\"}");

        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db");

        Assert.Equal(@"E:\persisted", cfg.ArchivePath);
    }

    // A blank/whitespace override must NOT clobber the persisted value -- Program.cs passes
    // builder.Configuration["TimesheetApp:ArchivePath"] straight through, which is null/"" when unset.
    [Fact]
    public void ArchivePath_Ignores_A_Blank_Override()
    {
        File.WriteAllText(_configPath,
            "{\"DbPath\":\"C:\\\\x\\\\timesheet.db\",\"ArchivePath\":\"E:\\\\persisted\"}");

        var cfg = new JsonAppConfig(_configPath, dbPath: @"C:\shared\timesheet.db", archivePathOverride: "  ");

        Assert.Equal(@"E:\persisted", cfg.ArchivePath);
    }

    // ---- F5: the writable-store rename migration -------------------------------------------------------

    // F5 (fast-lane-settings-appsettings.json): "rename the writable store... migrate an existing file to
    // the new name on first start so nobody loses their policy settings." Pinned directly against the
    // extracted mechanism (internal, via InternalsVisibleTo) with fully controlled temp paths -- the
    // constructor only invokes this against the REAL %APPDATA% legacy path, which a test must never touch.
    [Fact]
    public void MigrateLegacyStoreIfNeeded_Copies_An_Existing_Legacy_File_To_The_New_Name()
    {
        var legacyPath = Path.Combine(_dir, "appsettings.json");
        var newPath = Path.Combine(_dir, "local-settings.json");
        File.WriteAllText(legacyPath,
            "{\"DbPath\":\"C:\\\\x\\\\timesheet.db\",\"BackupFolderPath\":\"E:\\\\backups\",\"AutoBackupEnabled\":true}");

        JsonAppConfig.MigrateLegacyStoreIfNeeded(legacyPath, newPath);

        Assert.True(File.Exists(newPath));
        Assert.True(File.Exists(legacyPath)); // COPY, not move/rename -- the legacy file is left in place.

        var cfg = new JsonAppConfig(newPath, dbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"E:\backups", cfg.BackupFolderPath);
        Assert.True(cfg.AutoBackupEnabled);
    }

    [Fact]
    public void MigrateLegacyStoreIfNeeded_Never_Overwrites_A_New_File_That_Already_Exists()
    {
        var legacyPath = Path.Combine(_dir, "appsettings.json");
        var newPath = Path.Combine(_dir, "local-settings.json");
        File.WriteAllText(legacyPath, "{\"DbPath\":\"C:\\\\x\\\\timesheet.db\",\"BackupFolderPath\":\"E:\\\\legacy\"}");
        File.WriteAllText(newPath, "{\"DbPath\":\"C:\\\\y\\\\timesheet.db\",\"BackupFolderPath\":\"F:\\\\already-here\"}");

        JsonAppConfig.MigrateLegacyStoreIfNeeded(legacyPath, newPath);

        var cfg = new JsonAppConfig(newPath, dbPath: @"C:\shared\timesheet.db");
        Assert.Equal(@"F:\already-here", cfg.BackupFolderPath);
    }

    [Fact]
    public void MigrateLegacyStoreIfNeeded_Is_A_NoOp_When_No_Legacy_File_Exists()
    {
        var legacyPath = Path.Combine(_dir, "appsettings.json");
        var newPath = Path.Combine(_dir, "local-settings.json");

        JsonAppConfig.MigrateLegacyStoreIfNeeded(legacyPath, newPath);

        Assert.False(File.Exists(newPath));
    }

    [Fact]
    public void MigrateLegacyStoreIfNeeded_Is_A_NoOp_When_Both_Names_Resolve_To_The_Same_File()
    {
        var path = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(path, "{\"DbPath\":\"C:\\\\x\\\\timesheet.db\"}");
        var before = File.GetLastWriteTimeUtc(path);

        JsonAppConfig.MigrateLegacyStoreIfNeeded(path, path);

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
