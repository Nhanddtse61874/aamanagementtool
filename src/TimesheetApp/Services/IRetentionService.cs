namespace TimesheetApp.Services;

/// <summary>
/// One prunable month and the per-table row counts that WOULD be deleted for it.
/// </summary>
public sealed record RetentionMonthPreview(
    string Month,            // "yyyy-MM"
    int StandupIssues,
    int StandupEntries,
    int TimeLogs,
    int Tasks,
    int Backlogs);

/// <summary>
/// Read-only dry-run result: the cutoff that was used, and the prunable months (oldest first)
/// with the row counts that WOULD be removed. Writes nothing.
/// </summary>
public sealed record RetentionPreview(
    string Cutoff,                                       // "yyyy-MM" — months <= this are prunable
    IReadOnlyList<RetentionMonthPreview> Months);

/// <summary>
/// P12 (RT-01..07) 3-month retention/prune. DESTRUCTIVE: archives then deletes business data
/// older than the live window. Off by default; manual run + dry-run preview.
/// </summary>
public interface IRetentionService
{
    /// <summary>Dry-run: returns the prunable months + per-table counts that WOULD be deleted.
    /// Guaranteed write-free (read-only queries only).</summary>
    Task<RetentionPreview> PreviewAsync();

    /// <summary>Archive→verify-snapshot→delete the prunable months, then advance the marker.
    /// Aborts the whole run if an OneDrive conflict copy is present; skips any month whose
    /// archive failed or whose snapshot is missing/zero. Returns a human-readable status.</summary>
    Task<string> EnsureRetentionAsync();
}
