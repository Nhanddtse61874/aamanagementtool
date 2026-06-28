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

    // P10 (TM-05): the active team is an app-local, per-machine/user UI preference (DATA-07 locality
    // split — never in the shared OneDrive DB, else two users fight over one active team). 0 = unset
    // (resolved to first available membership on startup). Backward-compatible: old config files
    // missing the key default to 0.
    int ActiveTeamId { get; }
    void SetActiveTeamId(int teamId);

    // P11 (EX-01): two structured-export roots — a shared/SharePoint folder + a local folder.
    // Persisted app-locally (DATA-07) alongside DbPath/ArchivePath; default "" = that root is
    // skipped. Both empty = legacy flat archive fallback. Old config files missing the keys default
    // to "".
    string ExportRoot1Path { get; }
    void SetExportRoot1Path(string path);

    string ExportRoot2Path { get; }
    void SetExportRoot2Path(string path);
}
