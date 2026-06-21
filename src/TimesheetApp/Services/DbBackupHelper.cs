using System.IO;
using TimesheetApp.Config;

namespace TimesheetApp.Services;

/// <summary>Makes a timestamped one-shot <c>File.Copy</c> of the configured .db before a
/// bulk write (smart-input apply, DefaultTasks seed/sync). No-op when the .db is absent. (XC-10)</summary>
public sealed class DbBackupHelper : IDbBackupHelper
{
    private readonly IAppConfig _config;
    private readonly IClock _clock;

    public DbBackupHelper(IAppConfig config, IClock clock)
    {
        _config = config;
        _clock = clock;
    }

    public Task<string?> BackupAsync()
    {
        var dbPath = _config.DbPath;
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return Task.FromResult<string?>(null);

        var stamp = _clock.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupPath = $"{dbPath}.{stamp}.bak";
        File.Copy(dbPath, backupPath, overwrite: false);
        return Task.FromResult<string?>(backupPath);
    }
}
