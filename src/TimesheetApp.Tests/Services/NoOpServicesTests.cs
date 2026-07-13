using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

/// <summary>
/// M8.2 (spec §9): the two no-op implementations the API host will register in place of the desktop
/// ones (M8.3 does the registering — nothing is wired here).
///
/// Left alone on a server, the real <see cref="DbBackupHelper"/> would copy the ENTIRE database on
/// every Smart Fill apply — it runs before every bulk write — and the real journal check would report
/// "clean" forever, because it looks for a <c>-journal</c> file that WAL never creates.
/// </summary>
public sealed class NoOpServicesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noop-" + Guid.NewGuid().ToString("N"));

    public NoOpServicesTests() => Directory.CreateDirectory(_dir);

    [Fact] // The contract that matters on a server: no path, and — the point — no copy of the database.
    public async Task NoOpDbBackupHelper_returns_no_path_and_writes_nothing()
    {
        IDbBackupHelper helper = new NoOpDbBackupHelper();

        Assert.Null(await helper.BackupAsync());
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact] // Swallows the warning without touching System.Windows.* — it is injected into services.
    public void NoOpJournalWarningSink_swallows_the_warning()
    {
        IJournalWarningSink sink = new NoOpJournalWarningSink();

        var ex = Record.Exception(() => sink.Warn("rollback journal still present after commit"));

        Assert.Null(ex);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
