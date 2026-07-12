using System.IO;
using System.Linq;
using TimesheetApp.Config;
using TimesheetApp.Data;

namespace TimesheetApp.Services;

/// <summary>Makes a timestamped one-shot snapshot of the configured .db before a bulk write
/// (smart-input apply, DefaultTasks seed/sync). No-op when the .db is absent. (XC-10)
/// After each backup it prunes old .bak siblings down to <see cref="KeepBackups"/> so the DB
/// folder does not grow unbounded.
///
/// M8.2: the snapshot is an ONLINE backup (<see cref="SqliteOnlineBackup"/>), not a <c>File.Copy</c>.
/// This runs immediately before a bulk write on a database the app has open — under WAL the committed
/// rows are in the <c>-wal</c>, so a file copy would hand back a .bak missing exactly the data the
/// caller is about to overwrite.</summary>
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

    public async Task<string?> BackupAsync()
    {
        var dbPath = _config.DbPath;
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return null;

        var stamp = _clock.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupPath = $"{dbPath}.{stamp}.bak";
        // Off the caller's thread — this runs before every bulk write (smart-input apply / sync).
        await Task.Run(() =>
        {
            SqliteOnlineBackup.Copy(dbPath, backupPath); // online: the .db is live and may be in WAL
            PruneOldBackups(dbPath);
        });
        return backupPath;
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
