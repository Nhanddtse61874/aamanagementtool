using Xunit;
using TimesheetApp.Data;

namespace TimesheetApp.Tests.Data;

public class SqliteMaintenanceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SqliteMaintenanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-maint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        File.WriteAllText(_dbPath, "");
    }

    [Fact]
    public void FindConflictCopies_Returns_Empty_When_None_Exist()
    {
        var hits = SqliteMaintenance.FindConflictCopies(_dbPath);
        Assert.Empty(hits);
    }

    [Fact]
    public void FindConflictCopies_Detects_Machine_Suffixed_Sibling()
    {
        // OneDrive conflict-copy pattern: <name>-<MACHINE>.db next to the canonical file.
        var conflict = Path.Combine(_dir, "timesheet-DESKTOP-AB12.db");
        File.WriteAllText(conflict, "");

        var hits = SqliteMaintenance.FindConflictCopies(_dbPath);

        Assert.Contains(hits, p => Path.GetFileName(p) == "timesheet-DESKTOP-AB12.db");
    }

    [Fact]
    public void FindConflictCopies_Ignores_The_Canonical_File_Itself()
    {
        var hits = SqliteMaintenance.FindConflictCopies(_dbPath);
        Assert.DoesNotContain(hits, p => Path.GetFullPath(p) == Path.GetFullPath(_dbPath));
    }

    [Fact]
    public void IsJournalGone_True_When_No_Journal_Sidecar()
    {
        Assert.True(SqliteMaintenance.IsJournalGone(_dbPath));
    }

    [Fact]
    public void IsJournalGone_False_When_Journal_Sidecar_Persists()
    {
        File.WriteAllText(_dbPath + "-journal", "leftover");
        Assert.False(SqliteMaintenance.IsJournalGone(_dbPath)); // XC-09 warn condition
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
