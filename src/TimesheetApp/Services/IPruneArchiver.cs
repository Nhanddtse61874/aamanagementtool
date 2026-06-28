namespace TimesheetApp.Services;

/// <summary>
/// P12 (RT-03) archive-before-prune seam. Owned by W1 (RetentionService depends on it);
/// the concrete implementation is added by W2 as a NEW file (PruneArchiver).
/// </summary>
/// <remarks>
/// The contract: archive one calendar month's business data to the export structure
/// (per-team markdown to ≥1 root) AND take a full <c>.db</c> snapshot into a dedicated,
/// never-auto-pruned folder. Returns the verified retained <c>.db</c> snapshot path — the
/// true recovery artifact (markdown is a human summary, not byte-exact). Returns
/// <c>null</c> on failure (no root configured, markdown landed nowhere, or the snapshot
/// could not be written). RetentionService re-verifies the returned path exists and is
/// non-zero immediately before it deletes that month — if the archive failed or the
/// snapshot is absent/zero, the month is NOT pruned.
/// </remarks>
public interface IPruneArchiver
{
    /// <summary>Archive month (year, month) and return the verified pre-prune .db snapshot
    /// path, or null on failure.</summary>
    Task<string?> ArchiveMonthForPruneAsync(int year, int month);
}
