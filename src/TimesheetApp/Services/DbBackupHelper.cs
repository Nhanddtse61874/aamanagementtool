using System.IO;
using System.Linq;
using TimesheetApp.Config;

namespace TimesheetApp.Services;

/// <summary>Makes a timestamped one-shot <c>File.Copy</c> of the configured .db before a
/// bulk write (smart-input apply, DefaultTasks seed/sync). No-op when the .db is absent. (XC-10)
/// After each backup it prunes old .bak siblings down to <see cref="KeepBackups"/> so the DB
/// folder does not grow unbounded.</summary>
public sealed class DbBackupHelper : IDbBackupHelper
{
    // How many timestamped .bak files to retain in the DB folder (newest kept, older deleted).
    public const int KeepBackups = 10;

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
        PruneOldBackups(dbPath);
        return Task.FromResult<string?>(backupPath);
    }

    /// XC-10: keep only the newest <see cref="KeepBackups"/> "<c>{db}.{stamp}.bak</c>" siblings.
    /// The stamp is a fixed-width sortable timestamp, so an ordinal sort is chronological.
    /// Best-effort: a locked or vanished file is skipped, and pruning never fails a backup.
    private static void PruneOldBackups(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrEmpty(dir)) return;
        var prefix = Path.GetFileName(dbPath); // e.g. "timesheet.db" -> match "timesheet.db.*.bak"
        try
        {
            var stale = Directory.EnumerateFiles(dir, prefix + ".*.bak")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(KeepBackups);
            foreach (var old in stale)
            {
                try { File.Delete(old); } catch { /* locked/removed — skip it */ }
            }
        }
        catch { /* never let pruning break the backup that just succeeded */ }
    }
}
