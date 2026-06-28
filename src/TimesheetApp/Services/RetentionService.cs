using System.Data;
using System.IO;
using Dapper;
using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;

namespace TimesheetApp.Services;

/// <summary>
/// P12 (RT-01..07) retention/prune core. Computes the live window from <see cref="IClock"/>,
/// derives the prunable months (≤ cutoff) each run, dry-runs (<see cref="PreviewAsync"/>) and
/// archives-then-deletes (<see cref="EnsureRetentionAsync"/>) in ONE transaction with the R5a
/// time-axis rule (children-first; spanning backlogs and per-team DEFAULT survive). The archive
/// is an injected <see cref="IPruneArchiver"/> seam so the deletion + safety guards live here and
/// W2 supplies the archiver as a new file. DESTRUCTIVE — correctness is paramount.
/// </summary>
public sealed class RetentionService : IRetentionService
{
    // Marker key in the shared Settings KV: the max month fully archived+pruned ("yyyy-MM").
    // Shared (not app-local) because the pruned data is shared — pruning once globally is correct.
    public const string MarkerKey = "retention.pruned_through";

    private readonly IAppConfig _config;
    private readonly IConnectionFactory _factory;
    private readonly IClock _clock;
    private readonly IDbBackupHelper _backup;
    private readonly ISettingsRepository _settings;
    private readonly IPruneArchiver _archiver;

    public RetentionService(
        IAppConfig config,
        IConnectionFactory factory,
        IClock clock,
        IDbBackupHelper backup,
        ISettingsRepository settings,
        IPruneArchiver archiver)
    {
        _config = config;
        _factory = factory;
        _clock = clock;
        _backup = backup;
        _settings = settings;
        _archiver = archiver;
    }

    // FIX-A: DUPLICATED 3-line helper. ExportHubService.AddMonths(int,int,int) at
    // ExportHubService.cs:178 is private static and not reusable; this DateOnly overload is the
    // P12 copy. Operates on the first-of-month so AddMonths handles year rollover.
    private static DateOnly AddMonths(DateOnly firstOfMonth, int delta)
        => firstOfMonth.AddMonths(delta);

    // BLOCKER-2 (PINNED): cutoff = first-of-this-month minus RetentionMonths, formatted "yyyy-MM".
    // A month key is prunable iff key <= cutoff (lexical string compare; both work_date "yyyy-MM-dd"
    // and period_month "yyyy-MM" are zero-padded ISO so the 7-char prefix is month-comparable).
    private string Cutoff()
    {
        var today = _clock.Today;
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        return AddMonths(firstOfMonth, -_config.RetentionMonths).ToString("yyyy-MM");
    }

    public async Task<RetentionPreview> PreviewAsync()
    {
        var cutoff = Cutoff();
        using var c = _factory.Create();

        // Re-derive the set of months with old business data each run (don't trust the marker).
        var months = (await c.QueryAsync<string>(MonthsWithOldDataSql, new { cutoff }))
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

        var perMonth = new List<RetentionMonthPreview>(months.Count);
        foreach (var m in months)
        {
            // Per-month counts (one month at a time) so the preview is itemized; the Tasks/Backlogs
            // counts mirror the actual delete predicates (NOT EXISTS guards + DEFAULT/NULL exclusion).
            var counts = await c.QuerySingleAsync<MonthCounts>(MonthCountsSql, new { m });
            perMonth.Add(new RetentionMonthPreview(
                m, counts.StandupIssues, counts.StandupEntries, counts.TimeLogs, counts.Tasks, counts.Backlogs));
        }

        return new RetentionPreview(cutoff, perMonth);
    }

    public async Task<string> EnsureRetentionAsync()
    {
        // BLOCKER-3 STEP (a): conflict-copy guard FIRST — abort the WHOLE run (no archive/backup/delete).
        var conflicts = SqliteMaintenance.FindConflictCopies(_config.DbPath);
        if (conflicts.Count > 0)
            return $"Retention aborted: {conflicts.Count} OneDrive conflict copy(ies) present next to the DB.";

        var cutoff = Cutoff();

        List<string> months;
        using (var c = _factory.Create())
        {
            months = (await c.QueryAsync<string>(MonthsWithOldDataSql, new { cutoff }))
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();
        }

        if (months.Count == 0)
            return "Retention: nothing to prune.";

        // Archive each prunable month oldest-first. We delete ONLY the contiguous, successfully
        // archived prefix from the oldest: the FIRST month whose archive fails (null) or whose
        // returned snapshot is absent/zero stops the prefix, so no month past a failed archive is
        // pruned. (Deletion approach: per-month-archive-then-single-pass delete of <= effectiveCutoff.)
        var warnings = new List<string>();
        string? effectiveCutoff = null;
        foreach (var m in months)
        {
            var (y, mo) = SplitMonth(m);
            var snapshot = await _archiver.ArchiveMonthForPruneAsync(y, mo);

            // BLOCKER-1: verify the returned snapshot exists + non-zero immediately. If not, SKIP
            // this month AND stop (no delete for it or any later month — keep the prefix contiguous).
            if (snapshot is null || !File.Exists(snapshot) || new FileInfo(snapshot).Length <= 0)
            {
                warnings.Add($"{m}: archive failed or snapshot missing/zero — not pruned.");
                break;
            }

            effectiveCutoff = m; // archived OK; extend the contiguous prunable prefix
        }

        if (effectiveCutoff is null)
            return Compose("Retention: no month archived; nothing pruned.", warnings);

        // Safety net before the destructive transaction: full timestamped .db backup (XC-10).
        await _backup.BackupAsync();

        // R5a deletion — ONE transaction, children-first. effectiveCutoff bounds every predicate, so
        // all archived months (<= effectiveCutoff) are pruned together. Crash mid-tx -> DELETE-journal
        // rollback leaves a consistent DB (no half-pruned month).
        using (var c = _factory.Create())
        using (var tx = c.BeginTransaction())
        {
            var p = new { cutoff = effectiveCutoff };
            await c.ExecuteAsync(DeleteStandupIssuesSql, p, tx);   // 1
            await c.ExecuteAsync(DeleteStandupEntriesSql, p, tx);  // 2
            await c.ExecuteAsync(DeleteTimeLogsSql, p, tx);        // 3
            await c.ExecuteAsync(DeleteTasksSql, p, tx);           // 4
            await c.ExecuteAsync(DeleteBacklogAuditForDeletedSql, p, tx); // 5a (see note below)
            await c.ExecuteAsync(DeleteBacklogsSql, p, tx);        // 5
            await c.ExecuteAsync(DeleteBacklogTagsSql, transaction: tx); // 6 orphan cleanup (no FK)
            // SUGGESTION-3 intent: BacklogAudit is retained for SURVIVING backlogs. DEVIATION: the
            // plan/research said "do NOT delete BacklogAudit", but BacklogAudit.backlog_id is a
            // NOT NULL RESTRICT FK -> Backlogs(id) (v6 RENAME of RequestAudit, DatabaseInitializer:190).
            // Keeping the audit rows of a backlog that is being deleted would raise SQLite error 19.
            // Since backlog_id is NOT NULL we cannot null it either. So step 5a deletes audit rows
            // ONLY for the exact set of backlogs that step 5 deletes; all surviving backlogs keep their
            // full audit history (the TaskList "moved-to-next-month" archive still works).
            tx.Commit();
        }

        // Post-commit: advance the marker to the max pruned month.
        await _settings.SetAsync(MarkerKey, effectiveCutoff);

        // Post-commit XC-09 journal-clean check.
        var journalClean = SqliteMaintenance.IsJournalGone(_config.DbPath);

        var status = $"Retention: pruned business data through {effectiveCutoff}." +
                     (journalClean ? "" : " WARNING: rollback journal still present after commit.");
        return Compose(status, warnings);
    }

    private static string Compose(string status, IReadOnlyList<string> warnings)
        => warnings.Count == 0 ? status : status + " " + string.Join(" ", warnings);

    private static (int Year, int Month) SplitMonth(string yyyyMM)
        => (int.Parse(yyyyMM[..4]), int.Parse(yyyyMM[5..7]));

    private sealed class MonthCounts
    {
        public int StandupIssues { get; init; }
        public int StandupEntries { get; init; }
        public int TimeLogs { get; init; }
        public int Tasks { get; init; }
        public int Backlogs { get; init; }
    }

    // ----- SQL ---------------------------------------------------------------------------------

    // The distinct month keys (<= cutoff) that still hold prunable business data: any old-dated
    // TimeLog/StandupEntry, or any non-DEFAULT backlog whose period_month is <= cutoff.
    private const string MonthsWithOldDataSql = @"
SELECT m FROM (
    SELECT DISTINCT substr(work_date,1,7) AS m FROM TimeLogs       WHERE substr(work_date,1,7) <= @cutoff
    UNION
    SELECT DISTINCT substr(work_date,1,7) AS m FROM StandupEntries WHERE substr(work_date,1,7) <= @cutoff
    UNION
    SELECT DISTINCT period_month AS m FROM Backlogs
        WHERE period_month IS NOT NULL AND period_month <> '' AND period_month <= @cutoff
          AND backlog_code <> 'DEFAULT'
);";

    // Per-month preview counts. TimeLogs/StandupEntries/StandupIssues by work_date == @m.
    // Tasks/Backlogs use the SAME predicates as the actual delete (period_month == @m here, since
    // a single month is previewed): childless after time-prune + non-DEFAULT + period_month set.
    private const string MonthCountsSql = @"
SELECT
  (SELECT COUNT(*) FROM StandupIssues
     WHERE entry_id IN (SELECT id FROM StandupEntries WHERE substr(work_date,1,7) = @m)) AS StandupIssues,
  (SELECT COUNT(*) FROM StandupEntries WHERE substr(work_date,1,7) = @m)                 AS StandupEntries,
  (SELECT COUNT(*) FROM TimeLogs       WHERE substr(work_date,1,7) = @m)                 AS TimeLogs,
  (SELECT COUNT(*) FROM Tasks
     WHERE backlog_id IN (SELECT id FROM Backlogs
                            WHERE period_month IS NOT NULL AND period_month = @m AND backlog_code <> 'DEFAULT')
       AND NOT EXISTS (SELECT 1 FROM TimeLogs tl WHERE tl.task_id = Tasks.id
                         AND substr(tl.work_date,1,7) > @m))                              AS Tasks,
  (SELECT COUNT(*) FROM Backlogs b
     WHERE b.period_month IS NOT NULL AND b.period_month = @m AND b.backlog_code <> 'DEFAULT'
       AND NOT EXISTS (SELECT 1 FROM Tasks t WHERE t.backlog_id = b.id
                         AND EXISTS (SELECT 1 FROM TimeLogs tl WHERE tl.task_id = t.id
                                       AND substr(tl.work_date,1,7) > @m)))               AS Backlogs;";

    // 1. StandupIssues for in-month entries (explicit — don't rely on cascade; SUGGESTION-2).
    private const string DeleteStandupIssuesSql = @"
DELETE FROM StandupIssues
 WHERE entry_id IN (SELECT id FROM StandupEntries WHERE substr(work_date,1,7) <= @cutoff);";

    // 2. StandupEntries by work_date month (no FK to Backlogs — safe any time).
    private const string DeleteStandupEntriesSql = @"
DELETE FROM StandupEntries WHERE substr(work_date,1,7) <= @cutoff;";

    // 3. TimeLogs by work_date month — frees childless Tasks/Backlogs for removal.
    private const string DeleteTimeLogsSql = @"
DELETE FROM TimeLogs WHERE substr(work_date,1,7) <= @cutoff;";

    // 4. Tasks: childless (no remaining TimeLog) under a pruned, non-DEFAULT backlog. A spanning
    //    backlog keeps any Task that still has an in-window TimeLog -> that backlog survives (step 5).
    private const string DeleteTasksSql = @"
DELETE FROM Tasks
 WHERE backlog_id IN (
        SELECT id FROM Backlogs
         WHERE period_month IS NOT NULL AND period_month <= @cutoff AND backlog_code <> 'DEFAULT')
   AND NOT EXISTS (SELECT 1 FROM TimeLogs tl WHERE tl.task_id = Tasks.id);";

    // 5a. BacklogAudit for the EXACT backlogs step 5 will delete (BacklogAudit.backlog_id is a
    //     NOT NULL RESTRICT FK -> Backlogs(id), so its rows must go before/with the parent). Runs
    //     before step 5 so the FK is clear when the backlog row is removed. Surviving backlogs keep
    //     their audit (the predicate matches step 5's deletion set verbatim).
    private const string DeleteBacklogAuditForDeletedSql = @"
DELETE FROM BacklogAudit
 WHERE backlog_id IN (
        SELECT id FROM Backlogs
         WHERE period_month IS NOT NULL AND period_month <= @cutoff AND backlog_code <> 'DEFAULT'
           AND NOT EXISTS (SELECT 1 FROM Tasks t WHERE t.backlog_id = Backlogs.id));";

    // 5. Backlogs: pruned, non-DEFAULT, now childless (NOT EXISTS double-checks against orphaning).
    private const string DeleteBacklogsSql = @"
DELETE FROM Backlogs
 WHERE period_month IS NOT NULL AND period_month <= @cutoff AND backlog_code <> 'DEFAULT'
   AND NOT EXISTS (SELECT 1 FROM Tasks t WHERE t.backlog_id = Backlogs.id);";

    // 6. Orphan BacklogTags (no FK — manual cleanup).
    private const string DeleteBacklogTagsSql = @"
DELETE FROM BacklogTags WHERE backlog_id NOT IN (SELECT id FROM Backlogs);";
}
