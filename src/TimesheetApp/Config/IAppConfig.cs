namespace TimesheetApp.Config;

// DATA-07 / SET-01: the .db PATH is stored app-locally (%APPDATA%), never inside the
// shared DB (avoids the chicken-and-egg of reading the path from the DB it points to).
public interface IAppConfig
{
    string DbPath { get; }
    void SetDbPath(string dbPath);

    string ArchivePath { get; }
    void SetArchivePath(string archivePath);

    // P9 (BK-01/03/06): user-controlled local backup folder, auto-backup toggle, and retention count.
    // Persisted app-locally alongside DbPath/ArchivePath. "" folder = unset (backup actions gated).
    string BackupFolderPath { get; }
    void SetBackupFolderPath(string backupFolderPath);

    bool AutoBackupEnabled { get; }
    void SetAutoBackupEnabled(bool enabled);

    int BackupKeepCount { get; }
    void SetBackupKeepCount(int keepCount);
}
