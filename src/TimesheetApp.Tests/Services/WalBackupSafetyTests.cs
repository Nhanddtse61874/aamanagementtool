using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

/// <summary>
/// M8.2 — why <c>File.Copy</c> had to go.
///
/// BackupService used to state its own safety precondition in its header, verbatim: "File-level
/// File.Copy is safe at idle — the app uses short connections + journal_mode=DELETE." The WAL +
/// connection-pooling profile deletes BOTH premises.
///
/// Under WAL, committed transactions live in the <c>-wal</c> sidecar until a checkpoint. Copying the
/// .db alone therefore means, per https://www.sqlite.org/wal.html, that "transactions that were
/// previously committed ... might be lost, or the database file might become corrupted".
///
/// Each test below holds a connection OPEN (so the WAL is never checkpointed) and takes the backup
/// while a second connection holds an OPEN write transaction — the state a pooled server sits in all
/// day. Every one of them FAILS against the old File.Copy implementation.
/// </summary>
public sealed class WalBackupSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wal-bk-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly string _backupFolder;

    // Enough rows to be unmistakably "data", small enough to stay in the WAL (SQLite auto-checkpoints
    // at 1000 pages; 50 short rows are one page).
    private static readonly string[] Committed =
        Enumerable.Range(1, 50).Select(i => $"row-{i:D2}").ToArray();

    private const string Uncommitted = "NEVER-COMMITTED";

    public WalBackupSafetyTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "db", "timesheet.db");
        _backupFolder = Path.Combine(_root, "backups");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    private BackupService MakeService()
    {
        var cfg = new Mock<IAppConfig>();
        cfg.SetupGet(c => c.DbPath).Returns(_dbPath);
        cfg.SetupGet(c => c.BackupFolderPath).Returns(_backupFolder);
        cfg.SetupGet(c => c.BackupKeepCount).Returns(30);
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(DateTimeOffset.UtcNow);
        return new BackupService(cfg.Object, clock.Object);
    }

    [Fact] // THE test: a backup taken mid-write-transaction on a WAL database must restore intact.
    public async Task Backup_during_an_open_write_transaction_in_WAL_survives_restore_intact()
    {
        string backupPath;

        using (var live = TinyDb.OpenWal(_dbPath))   // stays open -> the WAL is never checkpointed
        {
            TinyDb.Seed(live, Committed);            // committed rows: they are in "-wal", NOT in the .db

            using var writer = TinyDb.OpenWal(_dbPath);
            using var tx = writer.BeginTransaction();
            TinyDb.Insert(writer, Uncommitted, tx);  // an OPEN write transaction, never committed

            // Back up right here: WAL hot, write transaction open.
            backupPath = (await MakeService().BackupNowAsync())!;
            Assert.NotNull(backupPath);

            // The committed rows must be IN the artifact. File.Copy leaves every one of them behind
            // in the -wal it did not copy — this is the silent data loss.
            Assert.Equal(Committed, TinyDb.ReadAll(backupPath));

            // ...and the open transaction's row must NOT be: a backup is a snapshot of committed state.
            Assert.DoesNotContain(Uncommitted, TinyDb.ReadAll(backupPath));

            tx.Rollback();
        }

        // The artifact is a real database — SQLite's verdict, not "exists && length > 0".
        Assert.Equal("ok", TinyDb.IntegrityCheck(backupPath));

        // Restore it over the live db, as the Settings tab does (the app restarts afterwards).
        await MakeService().RestoreAsync(backupPath);

        Assert.Equal(Committed, TinyDb.ReadAll(_dbPath));
        Assert.DoesNotContain(Uncommitted, TinyDb.ReadAll(_dbPath));
        Assert.Equal("ok", TinyDb.IntegrityCheck(_dbPath));
    }

    [Fact] // A restore must not leave the REPLACED database's -wal next to the new file: on the next
           // open SQLite treats that sidecar as belonging to the restored db and replays it over the
           // top. File.Copy(backup, dbPath) leaves it exactly where it was.
    public async Task Restore_removes_the_stale_wal_sidecar_of_the_replaced_db()
    {
        TinyDb.Create(_dbPath, "LIVE");
        Directory.CreateDirectory(_backupFolder);
        var backupPath = Path.Combine(_backupFolder, "timesheet_20260620080000000.db");
        TinyDb.Create(backupPath, "RESTORED");

        // The sidecars a WAL/pooled process leaves next to the live db.
        File.WriteAllBytes(_dbPath + "-wal", new byte[32]);
        File.WriteAllBytes(_dbPath + "-shm", new byte[32]);

        await MakeService().RestoreAsync(backupPath);

        Assert.False(File.Exists(_dbPath + "-wal"), "a stale -wal would be replayed over the restored db");
        Assert.False(File.Exists(_dbPath + "-shm"));
        Assert.Equal(new[] { "RESTORED" }, TinyDb.ReadAll(_dbPath));
        Assert.Equal("ok", TinyDb.IntegrityCheck(_dbPath));
    }

    [Fact] // The check that gates permanent deletion. RetentionService deletes the originals once
           // PruneArchiver hands back a snapshot path, so "verified" cannot mean "the file is there".
    public void Snapshot_verification_rejects_a_file_that_is_not_a_database()
    {
        // The exact fixture the old PruneArchiver test used as a stand-in for a live database.
        var blob = Path.Combine(_root, "not-a-database.db");
        File.WriteAllBytes(blob, new byte[] { 9, 9, 9, 9, 9, 9 });

        // It sails through the old check...
        Assert.True(File.Exists(blob) && new FileInfo(blob).Length > 0);
        // ...and SQLite says it is not a database at all.
        Assert.False(SqliteOnlineBackup.IsIntact(blob));

        TinyDb.Create(_dbPath, "REAL");
        Assert.True(SqliteOnlineBackup.IsIntact(_dbPath));
    }

    [Fact] // A truncated database still "exists and is non-zero".
    public void Snapshot_verification_rejects_a_truncated_database()
    {
        TinyDb.Create(_dbPath, Committed);
        var truncated = Path.Combine(_root, "truncated.db");
        var bytes = File.ReadAllBytes(_dbPath);
        File.WriteAllBytes(truncated, bytes.Take(bytes.Length / 2).ToArray());

        Assert.True(new FileInfo(truncated).Length > 0);
        Assert.False(SqliteOnlineBackup.IsIntact(truncated));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
