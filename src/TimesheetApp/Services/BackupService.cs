using System.Globalization;
using System.IO;
using System.Linq;
using TimesheetApp.Config;

namespace TimesheetApp.Services;

/// <summary>P9 (BK-01..06): user-controlled full-DB backup + restore to a chosen local folder.
/// File-level <c>File.Copy</c> is safe at idle — the app uses short connections + journal_mode=DELETE.
/// Distinct from <see cref="DbBackupHelper"/> (XC-10).</summary>
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

    public Task<string?> BackupNowAsync()
    {
        var folder = _config.BackupFolderPath;
        var dbPath = _config.DbPath;
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return Task.FromResult<string?>(null);

        Directory.CreateDirectory(folder);
        var stamp = _clock.UtcNow.LocalDateTime.ToString(Stamp, CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(folder, $"{Prefix}{stamp}{Extension}");
        File.Copy(dbPath, backupPath, overwrite: true);
        Prune(folder);
        return Task.FromResult<string?>(backupPath);
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

    public Task RestoreAsync(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            throw new FileNotFoundException("Backup file not found or unreadable.", backupPath);

        var dbPath = _config.DbPath;

        // Restoring the live DB onto itself would truncate it (safety copy made first, then File.Copy
        // source==dest). Reject it explicitly. Case-insensitive full-path compare (Windows file system).
        if (!string.IsNullOrWhiteSpace(dbPath) &&
            string.Equals(Path.GetFullPath(backupPath), Path.GetFullPath(dbPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot restore the live database file onto itself.");

        // Safety copy of the CURRENT db before overwrite, so a wrong restore is reversible (BK-05).
        if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
        {
            var stamp = _clock.UtcNow.LocalDateTime.ToString(Stamp, CultureInfo.InvariantCulture);
            File.Copy(dbPath, $"{dbPath}.pre-restore_{stamp}.bak", overwrite: false);
        }

        File.Copy(backupPath, dbPath, overwrite: true);
        return Task.CompletedTask;
    }

    // BK-06: keep only the newest BackupKeepCount "timesheet_*.db" files in the folder. Best-effort:
    // matches only this app's pattern (never unrelated files) and never fails the backup that succeeded.
    private void Prune(string folder)
    {
        var keep = _config.BackupKeepCount;
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
