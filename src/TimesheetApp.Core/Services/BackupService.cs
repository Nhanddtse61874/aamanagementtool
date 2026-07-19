using System.Globalization;
using System.IO;
using System.Linq;
using TimesheetApp.Config;
using TimesheetApp.Data;

namespace TimesheetApp.Services;

/// <summary>P9 (BK-01..06): user-controlled full-DB backup + restore to a chosen local folder.
/// Distinct from <see cref="DbBackupHelper"/> (XC-10).
///
/// M8.2: this used to read "File-level <c>File.Copy</c> is safe at idle — the app uses short connections
/// + journal_mode=DELETE", and it was true right up until WAL + connection pooling deleted both premises.
/// Under WAL the committed pages are in the <c>-wal</c> sidecar, so a copy of the .db alone can lose
/// committed transactions or be outright corrupt. Every copy of the live database now goes through
/// <see cref="SqliteOnlineBackup"/>.</summary>
public sealed class BackupService : IBackupService
{
    // Backup filename: timesheet_{yyyyMMddHHmmssfff}.db — millisecond precision (matches DbBackupHelper)
    // so two backups in the same second don't silently overwrite. Sortable -> ordinal sort is chronological.
    // The single Stamp constant drives BOTH the write (BackupNowAsync) and the parse (TryParseStamp).
    private const string Prefix = "timesheet_";
    private const string Extension = ".db";
    private const string Stamp = "yyyyMMddHHmmssfff";

    private readonly IAppConfig _config;
    private readonly IClock _clock;

    public BackupService(IAppConfig config, IClock clock)
    {
        _config = config;
        _clock = clock;
    }

    public Task<string?> BackupNowAsync() =>
        BackupToFolderAsync(_config.BackupFolderPath, _config.BackupKeepCount);

    // P11 (EX-05): copy the live .db into an arbitrary folder (the structured export's {root}/db), pruning
    // to `keep` newest. Same copy + stamp + pattern as BackupNowAsync (which now delegates here).
    public async Task<string?> BackupToFolderAsync(string folder, int keep)
    {
        var dbPath = _config.DbPath;
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return null;

        var stamp = _clock.UtcNow.LocalDateTime.ToString(Stamp, CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(folder, $"{Prefix}{stamp}{Extension}");
        // Off the caller's (UI) thread: a large DB or a slow network share (export root) would
        // otherwise freeze the window for the duration of the backup.
        await Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            SqliteOnlineBackup.Copy(dbPath, backupPath); // online: the .db is live and may be in WAL
            Prune(folder, keep);
        });
        return backupPath;
    }

    public async Task<bool> AutoBackupIfDueAsync()
    {
        if (!_config.AutoBackupEnabled) return false;
        if (string.IsNullOrWhiteSpace(_config.BackupFolderPath)) return false;

        var today = _clock.UtcNow.LocalDateTime.Date;
        if (ListBackups().Any(b => b.Timestamp.Date == today)) return false; // one/day already exists

        var path = await BackupNowAsync();
        return path is not null;
    }

    public IReadOnlyList<BackupInfo> ListBackups()
    {
        var folder = _config.BackupFolderPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Array.Empty<BackupInfo>();

        var list = new List<BackupInfo>();
        foreach (var file in Directory.EnumerateFiles(folder, $"{Prefix}*{Extension}"))
        {
            if (!TryParseStamp(file, out var ts)) continue;
            long size;
            try { size = new FileInfo(file).Length; }
            catch { continue; } // vanished/locked — skip it
            list.Add(new BackupInfo(file, ts, size));
        }
        return list.OrderByDescending(b => b.Timestamp).ToList();
    }

    /// <summary>BK-05. Restore is an OFFLINE operation: the pool is dropped first, then the live .db —
    /// and any <c>-wal</c>/<c>-shm</c> it left behind — is replaced, and the app must restart (which the
    /// Settings tab already tells the user to do).
    ///
    /// A plain <c>File.Copy</c> onto the live path cannot do this once pooling is on. It throws
    /// <see cref="IOException"/> while a pooled handle still holds the file; and when it does succeed it
    /// leaves the REPLACED database's <c>-wal</c> lying next to the new one, which SQLite then replays
    /// over the freshly restored file on the very next open.</summary>
    public async Task RestoreAsync(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            throw new FileNotFoundException("Backup file not found or unreadable.", backupPath);

        var dbPath = _config.DbPath;

        // Restoring the live DB onto itself would destroy it (it is both the safety-copy source and the
        // replaced destination). Reject it explicitly. Case-insensitive full-path compare (Windows).
        if (!string.IsNullOrWhiteSpace(dbPath) &&
            string.Equals(Path.GetFullPath(backupPath), Path.GetFullPath(dbPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot restore the live database file onto itself.");

        // "Exists" is not "usable": SqliteOnlineBackup.Copy DELETES the live .db before it opens the
        // source (Copy:41 runs before Copy:43). Reject a source that is not a real, intact SQLite
        // database HERE — before that delete — so an unusable backup fails loudly instead of destroying
        // production first and discovering the replacement is garbage only after it is gone.
        if (!SqliteOnlineBackup.IsIntact(backupPath))
            throw new InvalidOperationException(
                $"Backup file is not a usable SQLite database (failed integrity check): {backupPath}. Nothing was changed.");

        var stamp = _clock.UtcNow.LocalDateTime.ToString(Stamp, CultureInfo.InvariantCulture);
        // Off the UI thread (see BackupToFolderAsync). Validation above stays synchronous so a bad
        // path / self-restore / non-database surfaces immediately.
        await Task.Run(() =>
        {
            // Take the app's own handles off the live .db before replacing it.
            SqliteOnlineBackup.ClearPools();

            // Safety copy of the CURRENT db first, so a wrong restore is reversible (BK-05). That db is
            // live, so this is an online backup too: a safety net that is itself corrupt is worse than
            // no safety net, because it is the one file the user reaches for when the restore was wrong.
            if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
                SqliteOnlineBackup.Copy(dbPath, $"{dbPath}.pre-restore_{stamp}.bak");

            SqliteOnlineBackup.Copy(backupPath, dbPath);
        });
    }

    // BK-06: keep only the newest BackupKeepCount "timesheet_*.db" files in the folder. Best-effort:
    // matches only this app's pattern (never unrelated files) and never fails the backup that succeeded.
    private static void Prune(string folder, int keep)
    {
        if (keep <= 0) return;
        try
        {
            var stale = Directory.EnumerateFiles(folder, $"{Prefix}*{Extension}")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .Skip(keep);
            foreach (var old in stale)
            {
                try { File.Delete(old); } catch { /* locked/removed — skip it */ }
            }
        }
        catch { /* never let pruning break the backup that just succeeded */ }
    }

    // Parse the stamp embedded in "timesheet_{yyyyMMddHHmmssfff}.db" (same Stamp constant as the write).
    private static bool TryParseStamp(string filePath, out DateTime timestamp)
    {
        timestamp = default;
        var name = Path.GetFileNameWithoutExtension(filePath); // timesheet_20260627093015123
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var stampPart = name.Substring(Prefix.Length);
        return DateTime.TryParseExact(
            stampPart, Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }
}
