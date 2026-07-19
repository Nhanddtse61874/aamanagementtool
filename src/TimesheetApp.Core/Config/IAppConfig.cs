namespace TimesheetApp.Config;

// DATA-07 / SET-01: the .db PATH is stored app-locally, never inside the shared DB (avoids the
// chicken-and-egg of reading the path from the DB it points to).
//
// M11: DbPath is now sourced from IConfiguration (TimesheetApp:DbPath), REQUIRED, with no fallback --
// Program.cs refuses to start if it is missing (F1) -- and it WINS over whatever JsonAppConfig's own
// persisted store has on disk (F2). SetDbPath below still exists (no production caller uses it; kept for
// the writable-store mechanics and existing tests), but a value it persists no longer outranks
// IConfiguration on the NEXT start -- IConfiguration is authoritative every time. Do not read DbPath as
// "the current runtime database" and assume SetDbPath can repoint it for a future restart; it cannot.
public interface IAppConfig
{
    string DbPath { get; }
    void SetDbPath(string dbPath);

    // ArchivePath is also reachable from IConfiguration (TimesheetApp:ArchivePath), but -- unlike DbPath --
    // NOT required: it has a working default (a folder next to DbPath; see StandupArchiveService /
    // TaskListArchiveService) and M11 does not change that. When IConfiguration supplies it, Program.cs
    // passes it into JsonAppConfig's ctor as an override; when absent, the persisted store / default apply
    // exactly as before.
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

    // M8.2 (Wave 4): ActiveTeamId USED TO LIVE HERE. It now lives in Users.active_team_id, reached via
    // IUserRepository.Get/SetActiveTeamIdAsync. The original note ("never in the shared DB, else two
    // users fight over one active team") was right about WPF and backwards for a server: IAppConfig is
    // per-PROCESS, and on a desktop one process serves one user, so per-process == per-user. In an API
    // one process serves EVERYONE — user A switching team would re-scope user B's next request to team
    // A. That is a cross-user data leak (violates R6), not a UI preference. The real defect was that
    // IAppConfig mixed per-APP state (DbPath, BackupFolderPath, ExportRoot*) with per-USER state; the
    // two only coincide on a desktop. Per-user state belongs on the user row. Do not re-add it here.

    // P11 (EX-01): two structured-export roots — a shared/SharePoint folder + a local folder.
    // Persisted app-locally (DATA-07) alongside DbPath/ArchivePath; default "" = that root is
    // skipped. Both empty = legacy flat archive fallback. Old config files missing the keys default
    // to "".
    string ExportRoot1Path { get; }
    void SetExportRoot1Path(string path);

    string ExportRoot2Path { get; }
    void SetExportRoot2Path(string path);

    // P12 (RT-01): 3-month retention/prune. App-local (DATA-07) — a per-machine opt-in, NOT a
    // shared DB setting (the shared marker retention.pruned_through lives in the DB). DESTRUCTIVE,
    // so default OFF. Old config files missing the keys default to off / 3 months (backward-compat).
    bool RetentionEnabled { get; }
    void SetRetentionEnabled(bool enabled);

    int RetentionMonths { get; }
    void SetRetentionMonths(int months);
}
