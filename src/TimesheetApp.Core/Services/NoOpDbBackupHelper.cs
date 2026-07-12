namespace TimesheetApp.Services;

/// <summary>
/// M8.2 (spec §9) — the <see cref="IDbBackupHelper"/> the API host registers. Does nothing and returns
/// no path.
///
/// <see cref="DbBackupHelper"/> snapshots the ENTIRE database before every bulk write. On a single-user
/// desktop app sitting on OneDrive that is a sensible safety net (XC-10). On a server it is a disaster:
/// <see cref="TimeLogService"/> takes a bulk write on every Smart Fill apply, so each request would copy
/// the whole database — for every user, all day.
///
/// The server's durability story is the database itself (WAL + a real backup schedule), not a .bak file
/// per request. This is a DI swap: no service constructor changes, and the desktop app keeps the real
/// helper until M8.10.
/// </summary>
public sealed class NoOpDbBackupHelper : IDbBackupHelper
{
    public Task<string?> BackupAsync() => Task.FromResult<string?>(null);
}
