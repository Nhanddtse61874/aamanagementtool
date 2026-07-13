namespace TimesheetApp.Services;

/// <summary>One-shot .db backup before a bulk (multi-row) write. Safe only when no
/// transaction is open. Single-cell edits do NOT call this (avoids OneDrive churn). (XC-10)</summary>
public interface IDbBackupHelper
{
    /// <summary>Copies the configured .db to a timestamped sibling. Returns the backup
    /// path, or null if the .db file does not yet exist.</summary>
    Task<string?> BackupAsync();
}
