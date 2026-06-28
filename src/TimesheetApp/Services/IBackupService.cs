namespace TimesheetApp.Services;

/// <summary>One backup file in the chosen backup folder: full path, the timestamp parsed from its
/// filename, and its size in bytes. (P9 / BK-04)</summary>
public sealed record BackupInfo(string Path, DateTime Timestamp, long SizeBytes);

/// <summary>P9 (BK-01..06): user-controlled local DB backup + restore to a folder the user picks
/// (ideally outside OneDrive). Distinct from <see cref="IDbBackupHelper"/> (XC-10, .bak next to the DB).
/// Backup = a full timestamped copy of the live .db; restore safety-copies first then overwrites.</summary>
public interface IBackupService
{
    /// <summary>Copy the live .db to <c>{folder}/timesheet_{yyyyMMddHHmmss}.db</c>, prune to N, return its
    /// path. Returns null (no-op) if no folder is set or the .db is missing. (BK-02)</summary>
    Task<string?> BackupNowAsync();

    /// <summary>Copy the live .db into <paramref name="folder"/> as <c>timesheet_{stamp}.db</c>, prune to
    /// <paramref name="keep"/> newest, return its path. Null (no-op) if the .db is missing or folder empty.
    /// Used by the structured export to drop one .db copy at <c>{root}/db</c>. (P11 / EX-05)</summary>
    Task<string?> BackupToFolderAsync(string folder, int keep);

    /// <summary>If auto-backup is on, a folder is set, and no backup dated today exists, do one backup.
    /// Returns true if a backup was made. Best-effort. (BK-03)</summary>
    Task<bool> AutoBackupIfDueAsync();

    /// <summary>Existing <c>timesheet_*.db</c> backups in the folder, newest first. Empty if no folder. (BK-04)</summary>
    IReadOnlyList<BackupInfo> ListBackups();

    /// <summary>Safety-copy the current .db to <c>{db}.pre-restore_{stamp}.bak</c>, then overwrite the live
    /// .db with the chosen backup. Throws if the backup file is missing/unreadable. (BK-05)</summary>
    Task RestoreAsync(string backupPath);
}
