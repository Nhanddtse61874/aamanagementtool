using System.IO;

namespace TimesheetApp.Data;

// XC-08: scan the DB folder for OneDrive conflict-copy siblings ("<name>-<MACHINE>.db")
// so silent data divergence becomes a visible startup event.
// XC-09: after a committed write the rollback journal must be gone; a lingering
// "<db>-journal" means a transaction was interrupted -> caller surfaces a warning.
public static class SqliteMaintenance
{
    public static IReadOnlyList<string> FindConflictCopies(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return Array.Empty<string>();

        var stem = Path.GetFileNameWithoutExtension(dbPath); // "timesheet"
        var ext = Path.GetExtension(dbPath);                  // ".db"
        var canonicalFull = Path.GetFullPath(dbPath);

        var results = new List<string>();
        // Pattern "<stem>-*<ext>" catches "timesheet-DESKTOP-AB12.db" but not "timesheet.db".
        foreach (var file in Directory.EnumerateFiles(dir, $"{stem}-*{ext}"))
        {
            if (Path.GetFullPath(file) == canonicalFull) continue;
            results.Add(file);
        }
        return results;
    }

    public static bool IsJournalGone(string dbPath)
        => !File.Exists(dbPath + "-journal");
}
