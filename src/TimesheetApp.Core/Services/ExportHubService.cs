using System.IO;
using System.Text;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

// P11 (EX-02/03/05/06): structured per-team export, mirrored to the configured roots. See IExportHubService.
public sealed class ExportHubService : IExportHubService
{
    // How far back ExportNow / Backfill reach. Bounded + simple: no-data periods produce no file (the
    // builders return null), so an over-wide window is harmless beyond a few extra null builds.
    private const int BackfillMonths = 12;
    private const int BackfillWeeks = 12;

    private readonly IAppConfig _config;
    private readonly ITeamRepository _teams;
    private readonly IStandupArchiveService _standup;
    private readonly ITaskListArchiveService _tasklist;
    private readonly IExportService _export;
    private readonly IBackupService _backup;
    private readonly IClock _clock;
    private readonly IPathSanitizer _sanitizer;
    private readonly ISharePointDestinationValidator? _spValidator;   // P14 SP-03 (null in older tests)

    public ExportHubService(
        IAppConfig config, ITeamRepository teams, IStandupArchiveService standup,
        ITaskListArchiveService tasklist, IExportService export, IBackupService backup,
        IClock clock, IPathSanitizer sanitizer,
        ISharePointDestinationValidator? spValidator = null)
    {
        _config = config;
        _teams = teams;
        _standup = standup;
        _tasklist = tasklist;
        _export = export;
        _backup = backup;
        _clock = clock;
        _sanitizer = sanitizer;
        _spValidator = spValidator;
    }

    public Task<string> ExportNowAsync() => RunAsync(backfillOnly: false);

    public async Task BackfillAsync() => await RunAsync(backfillOnly: true);

    private async Task<string> RunAsync(bool backfillOnly)
    {
        var roots = new[] { _config.ExportRoot1Path, _config.ExportRoot2Path }
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToList();
        if (roots.Count == 0) return "no export root configured";

        var teams = await _teams.GetAllAsync() ?? Array.Empty<Team>();   // FIX-2: incl. inactive
        var months = MonthsFor(backfillOnly);
        var weekMondays = WeekMondaysFor(backfillOnly);

        var status = new StringBuilder();
        foreach (var root in roots)
        {
            // SP-03: hard-verify the destination before writing. A web-URL or unwritable root is skipped
            // with the same clear, actionable reason the Settings "Verify" gives (not an opaque OS error),
            // so files never land in the wrong place. Warning-level (writable but plain-local) still exports.
            var check = _spValidator?.Verify(root);
            if (check is { Level: DestinationLevel.Error })
            {
                status.AppendLine($"failed: {root} — {check.Message}");
                continue;
            }

            try
            {
                await ExportRootAsync(root, teams, months, weekMondays, backfillOnly);
                status.AppendLine($"ok: {root}");
            }
            catch (Exception ex)
            {
                // Best-effort per root — one failing root must not abort the other (EX-03).
                status.AppendLine($"failed: {root} — {ex.Message}");
            }
        }
        return status.ToString().TrimEnd();
    }

    private async Task ExportRootAsync(
        string root, IReadOnlyList<Team> teams, IReadOnlyList<(int Year, int Month)> months,
        IReadOnlyList<DateOnly> weekMondays, bool backfillOnly)
    {
        // I-1: distinct team names can sanitize to the SAME folder segment (e.g. "Team:A" / "Team_A"),
        // and per-team files share identical names — the second team would silently overwrite the first.
        // Dedupe per root: keep the clean segment when unique; on collision append the stable team id.
        // GetAllAsync order (is_active DESC, name) + fixed ids make this deterministic across runs.
        var usedSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var team in teams)
        {
            var segment = _sanitizer.SanitizeSegment(team.Name, team.Id);
            if (!usedSegments.Add(segment))
            {
                segment = $"{segment}-{team.Id}";
                usedSegments.Add(segment);
            }
            var teamDir = Path.Combine(root, segment);
            var teamIds = new[] { team.Id };

            // tasklist (monthly)
            foreach (var (y, m) in months)
            {
                var dir = Path.Combine(teamDir, "tasklist");
                var path = Path.Combine(dir, _tasklist.FileNameFor(y, m));
                if (backfillOnly && File.Exists(path)) continue;
                var md = await _tasklist.BuildMonthMarkdownAsync(teamIds, y, m);
                if (md is null) continue; // no-data -> no file
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(path, md, Encoding.UTF8);
            }

            // timesheet (monthly) — ExportService always emits a header, so skip when it has no data rows.
            foreach (var (y, m) in months)
            {
                var dir = Path.Combine(teamDir, "timesheet");
                var path = Path.Combine(dir, $"{y:D4}{m:D2}_timesheet.md");
                if (backfillOnly && File.Exists(path)) continue;
                var md = await _export.ExportMarkdownAsync(new ExportFilter(null, y, m, null, teamIds));
                if (!FormatHelpers.HasTimesheetData(md)) continue; // no-data -> no file
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(path, md, Encoding.UTF8);
            }

            // daily (weekly, Monday stamp)
            foreach (var monday in weekMondays)
            {
                var dir = Path.Combine(teamDir, "daily");
                var path = Path.Combine(dir, _standup.FileNameFor(monday));
                if (backfillOnly && File.Exists(path)) continue;
                var md = await _standup.BuildWeekMarkdownAsync(teamIds, monday);
                if (md is null) continue; // no-data -> no file
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(path, md, Encoding.UTF8);
            }
        }

        // One whole-DB copy per root (EX-05), not per team.
        await _backup.BackupToFolderAsync(Path.Combine(root, "db"), _config.BackupKeepCount);
    }

    // ExportNow = current + previous month; Backfill = the last BackfillMonths completed months
    // (strictly before the current month). Bounded; no-data months produce no file.
    private IReadOnlyList<(int Year, int Month)> MonthsFor(bool backfillOnly)
    {
        var today = _clock.Today;
        var list = new List<(int, int)>();
        if (backfillOnly)
        {
            for (var i = 1; i <= BackfillMonths; i++)
                list.Add(AddMonths(today.Year, today.Month, -i));
        }
        else
        {
            list.Add((today.Year, today.Month));
            list.Add(AddMonths(today.Year, today.Month, -1));
        }
        return list;
    }

    // ExportNow = current + previous week; Backfill = the last BackfillWeeks completed weeks
    // (strictly before the current week). Stamp = each week's Monday.
    private IReadOnlyList<DateOnly> WeekMondaysFor(bool backfillOnly)
    {
        var currentMonday = DateHelpers.MondayOf(_clock.Today);
        var list = new List<DateOnly>();
        if (backfillOnly)
        {
            for (var i = 1; i <= BackfillWeeks; i++)
                list.Add(currentMonday.AddDays(-7 * i));
        }
        else
        {
            list.Add(currentMonday);
            list.Add(currentMonday.AddDays(-7));
        }
        return list;
    }

    private static (int Year, int Month) AddMonths(int year, int month, int delta)
    {
        var dt = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(delta);
        return (dt.Year, dt.Month);
    }
}
