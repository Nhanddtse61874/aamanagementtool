using System.IO;
using System.Text;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>
/// P12 (RT-03) — concrete <see cref="IPruneArchiver"/> (W2). Before RetentionService prunes a month it
/// calls this to (a) write per-team markdown for that month to every configured export root, reusing the
/// P11 builders scoped to a single team, and (b) BLOCKER-1: copy the full live <c>.db</c> into a dedicated
/// <c>{root}/db/prune-snapshots</c> folder that is NEVER auto-pruned (unlike <see cref="IBackupService"/>
/// / <see cref="IDbBackupHelper"/>, which prune to a keep-count and would delete the recovery artifact).
/// Returns the verified snapshot path (<c>File.Exists &amp;&amp; Length&gt;0</c>) on the first root that
/// succeeded, or <c>null</c> when no root is usable or no snapshot could be written/verified — in which
/// case RetentionService does NOT prune that month.
/// </summary>
public sealed class PruneArchiver : IPruneArchiver
{
    // Dedicated, never-auto-pruned snapshot folder under each root's db dir. Distinct from the
    // export's {root}/db backup folder (which BackupService.BackupToFolderAsync prunes to a keep-count),
    // and from the snapshot filename containing "_pre-prune_" so it never matches the "timesheet_*.db"
    // prune glob even if it shared a folder.
    private const string SnapshotSubdir = "prune-snapshots";

    private readonly IAppConfig _config;
    private readonly ITeamRepository _teams;
    private readonly IPathSanitizer _sanitizer;
    private readonly IStandupArchiveService _standup;
    private readonly ITaskListArchiveService _tasklist;
    private readonly IExportService _export;
    private readonly IClock _clock;

    public PruneArchiver(
        IAppConfig config,
        ITeamRepository teams,
        IPathSanitizer sanitizer,
        IStandupArchiveService standup,
        ITaskListArchiveService tasklist,
        IExportService export,
        IClock clock)
    {
        _config = config;
        _teams = teams;
        _sanitizer = sanitizer;
        _standup = standup;
        _tasklist = tasklist;
        _export = export;
        _clock = clock;
    }

    public async Task<string?> ArchiveMonthForPruneAsync(int year, int month)
    {
        var roots = new[] { _config.ExportRoot1Path, _config.ExportRoot2Path }
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToList();
        if (roots.Count == 0) return null; // no root -> can't archive -> caller won't prune.

        var teams = await _teams.GetAllAsync() ?? Array.Empty<Team>(); // incl. inactive
        var yyyyMM = $"{year:D4}{month:D2}";
        var weekMondays = WeekMondaysInMonth(year, month);

        // First-root-that-succeeds wins for the returned snapshot path. Best-effort per root: a failing
        // root (unwritable, etc.) must not fail the run if another root succeeds.
        string? verifiedSnapshot = null;
        foreach (var root in roots)
        {
            try
            {
                await WriteTeamMarkdownAsync(root, teams, year, month, yyyyMM, weekMondays);
                var snap = WriteSnapshot(root, yyyyMM);
                if (snap is not null && verifiedSnapshot is null)
                    verifiedSnapshot = snap; // keep the first verified snapshot as the recovery artifact
            }
            catch
            {
                // Best-effort per root — try the next one.
            }
        }

        return verifiedSnapshot;
    }

    // Per active+inactive team: concatenate the team's tasklist + daily + timesheet markdown for the month
    // into one {root}/{Team}/db/{yyyyMM}_pruned.md. Skip teams with no data (all sections null/empty).
    private async Task WriteTeamMarkdownAsync(
        string root, IReadOnlyList<Team> teams, int year, int month, string yyyyMM,
        IReadOnlyList<DateOnly> weekMondays)
    {
        // Mirror ExportHubService dedupe: distinct names can sanitize to the same segment.
        var usedSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var team in teams)
        {
            var segment = _sanitizer.SanitizeSegment(team.Name, team.Id);
            if (!usedSegments.Add(segment))
            {
                segment = $"{segment}-{team.Id}";
                usedSegments.Add(segment);
            }

            var teamIds = new[] { team.Id };
            var sb = new StringBuilder();

            // Task List (monthly).
            var taskMd = await _tasklist.BuildMonthMarkdownAsync(teamIds, year, month);
            if (!string.IsNullOrWhiteSpace(taskMd))
            {
                sb.AppendLine("# Task List");
                sb.AppendLine();
                sb.AppendLine(taskMd.TrimEnd());
                sb.AppendLine();
            }

            // Daily (each week whose Monday falls in the month).
            var dailyParts = new List<string>();
            foreach (var monday in weekMondays)
            {
                var weekMd = await _standup.BuildWeekMarkdownAsync(teamIds, monday);
                if (!string.IsNullOrWhiteSpace(weekMd))
                    dailyParts.Add(weekMd.TrimEnd());
            }
            if (dailyParts.Count > 0)
            {
                sb.AppendLine("# Daily Report");
                sb.AppendLine();
                sb.AppendLine(string.Join(Environment.NewLine + Environment.NewLine, dailyParts));
                sb.AppendLine();
            }

            // Timesheet (monthly). ExportService always emits a header; only include when it has data rows.
            var timesheetMd = await _export.ExportMarkdownAsync(new ExportFilter(null, year, month, null, teamIds));
            if (HasTimesheetData(timesheetMd))
            {
                sb.AppendLine("# Timesheet");
                sb.AppendLine();
                sb.AppendLine(timesheetMd.TrimEnd());
                sb.AppendLine();
            }

            if (sb.Length == 0) continue; // no data for this team this month -> no file.

            var dir = Path.Combine(root, segment, "db");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{yyyyMM}_pruned.md");
            await File.WriteAllTextAsync(path, sb.ToString().TrimEnd() + Environment.NewLine, Encoding.UTF8);
        }
    }

    // BLOCKER-1: plain File.Copy the live .db to {root}/db/prune-snapshots/timesheet_{yyyyMM}_pre-prune_{stamp}.db.
    // Returns the verified path (exists + non-zero), or null on any failure (so this root won't count).
    private string? WriteSnapshot(string root, string yyyyMM)
    {
        var dbPath = _config.DbPath;
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return null;

        var dir = Path.Combine(root, "db", SnapshotSubdir);
        Directory.CreateDirectory(dir);

        var stamp = _clock.UtcNow.LocalDateTime.ToString("yyyyMMddHHmmssfff");
        var snapPath = Path.Combine(dir, $"timesheet_{yyyyMM}_pre-prune_{stamp}.db");

        File.Copy(dbPath, snapPath, overwrite: false);

        return File.Exists(snapPath) && new FileInfo(snapPath).Length > 0 ? snapPath : null;
    }

    // The set of Mondays whose Mon–Fri week intersects [year-month]. The standup builder is week-scoped
    // (Monday stamp); a week that straddles a month boundary is included so no in-month day is missed.
    private static IReadOnlyList<DateOnly> WeekMondaysInMonth(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var monday = MondayOf(first);
        var result = new List<DateOnly>();
        while (monday <= last)
        {
            result.Add(monday);
            monday = monday.AddDays(7);
        }
        return result;
    }

    private static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    // Same has-data marker ExportHubService uses: the per-row table header only appears when there are logs.
    private static bool HasTimesheetData(string md) =>
        md.Contains("| Date | Task | Hours |", StringComparison.Ordinal);
}
