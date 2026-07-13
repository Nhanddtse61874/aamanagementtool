using System.Globalization;
using System.IO;
using System.Text;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

// TL-09: writes one markdown overview per month, accumulating continuously. Mirrors StandupArchiveService:
// no data => no file, idempotent overwrite, startup backfill of any completed month with data but no file.
// Month M content = current members (period_month==M, DEFAULT excluded) + moved-out backlogs
// (BacklogAudit field='period_month' old_value=M, not currently in M) under a "Moved to next month" section.
public sealed class TaskListArchiveService : ITaskListArchiveService
{
    private readonly IBacklogRepository _backlogs;
    private readonly ITaskRepository _tasks;
    private readonly ITimeLogRepository _logs;
    private readonly ITagRepository _tags;
    private readonly IPcaContactRepository _pcas;
    private readonly IUserRepository _users;
    private readonly IHolidayRepository _holidays;
    private readonly IScheduleStateService _schedule;
    private readonly IWorkingDayCalculator _calc;
    private readonly IAppConfig _config;
    private readonly IClock _clock;
    private readonly ITeamRepository? _teams;   // v8 (P10 TM-08): resolves the Team column (optional for legacy tests)

    public TaskListArchiveService(
        IBacklogRepository backlogs, ITaskRepository tasks, ITimeLogRepository logs, ITagRepository tags,
        IPcaContactRepository pcas, IUserRepository users, IHolidayRepository holidays,
        IScheduleStateService schedule, IWorkingDayCalculator calc, IAppConfig config, IClock clock,
        ITeamRepository? teams = null)
    {
        _backlogs = backlogs;
        _tasks = tasks;
        _logs = logs;
        _tags = tags;
        _pcas = pcas;
        _users = users;
        _holidays = holidays;
        _schedule = schedule;
        _calc = calc;
        _config = config;
        _clock = clock;
        _teams = teams;
    }

    public string FileNameFor(int year, int month) =>
        $"{year:D4}{month:D2}_tasklist.md";

    public async Task<string?> ExportMonthAsync(int year, int month)
    {
        var md = await BuildMonthMarkdownAsync(null, year, month);
        if (md is null) return null; // no data -> no file (TL-09)

        var dir = ArchiveDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileNameFor(year, month));
        await File.WriteAllTextAsync(path, md, Encoding.UTF8);
        return path;
    }

    // P11 (EX-04): per-team (or all-teams when teamIds==null) month markdown builder. Relocates the
    // inline build from ExportMonthAsync, scoping SearchAsync to teamIds. Returns null when the scoped
    // month has no current members and no moved-out members. ExportMonthAsync delegates with teamIds=null
    // (byte-identical legacy file).
    public async Task<string?> BuildMonthMarkdownAsync(IReadOnlyList<int>? teamIds, int year, int month)
    {
        var monthKey = MonthKey(year, month);

        var all = (await _backlogs.SearchAsync(null, teamIds) ?? Array.Empty<Backlog>())
            .Where(b => !string.Equals(b.BacklogCode, "DEFAULT", StringComparison.Ordinal))
            .ToList();

        // Current members: period_month == M.
        var current = all.Where(b => string.Equals(b.PeriodMonth, monthKey, StringComparison.Ordinal)).ToList();
        var currentIds = current.Select(b => b.Id).ToHashSet();

        // Moved-out members: had a period_month audit row with old_value == M, and are not currently in M.
        // Dedup current-wins; the latest audit row decides (GetAuditAsync returns DESC by id).
        var nonCurrentIds = all.Where(b => !currentIds.Contains(b.Id)).Select(b => b.Id).ToList();
        var auditByBacklog = await _backlogs.GetAuditForBacklogsAsync(nonCurrentIds);
        var movedOut = new List<Backlog>();
        foreach (var b in all)
        {
            if (currentIds.Contains(b.Id)) continue;
            var audit = auditByBacklog.TryGetValue(b.Id, out var a) ? a : Array.Empty<BacklogAuditEntry>();
            var latestPeriodMove = audit.FirstOrDefault(a => a.Field == "period_month");
            if (latestPeriodMove is { } move && string.Equals(move.OldValue, monthKey, StringComparison.Ordinal))
                movedOut.Add(b);
        }

        if (current.Count == 0 && movedOut.Count == 0) return null; // no data -> no file (TL-09)

        // Shared lookups (loaded once).
        var loggedByBacklog = await _logs.GetLoggedHoursByBacklogAsync();
        var tagsById = (await _tags.GetAllAsync() ?? Array.Empty<Tag>()).ToDictionary(t => t.Id);
        var tagLinks = await _backlogs.GetTagIdsForAllAsync();
        var userNames = (await _users.GetAllAsync() ?? Array.Empty<User>()).ToDictionary(u => u.Id, u => u.Name);
        var pcaNames = (await _pcas.GetAllAsync() ?? Array.Empty<PcaContact>()).ToDictionary(p => p.Id, p => p.Name);
        var holidaySet = (await _holidays.GetAllAsync() ?? Array.Empty<Holiday>()).Select(h => h.Date).ToHashSet();
        var teamNames = _teams is null
            ? new Dictionary<int, string>()
            : (await _teams.GetAllAsync() ?? Array.Empty<Team>()).ToDictionary(t => t.Id, t => t.Name);

        var sb = new StringBuilder();
        sb.AppendLine($"# Task List — {monthKey}");
        sb.AppendLine();

        await AppendSectionAsync(sb, "Members", current, loggedByBacklog, tagsById, tagLinks, userNames, pcaNames, holidaySet, teamNames);
        if (movedOut.Count > 0)
            await AppendSectionAsync(sb, "Moved to next month", movedOut, loggedByBacklog, tagsById, tagLinks, userNames, pcaNames, holidaySet, teamNames);

        return sb.ToString();
    }

    public async Task BackfillMissingMonthsAsync()
    {
        var today = _clock.Today;
        var currentMonthKey = MonthKey(today.Year, today.Month);

        var all = (await _backlogs.SearchAsync(null) ?? Array.Empty<Backlog>())
            .Where(b => !string.Equals(b.BacklogCode, "DEFAULT", StringComparison.Ordinal))
            .ToList();

        // Every month that has data: current period_month values + period_month audit old_values.
        var allIds = all.Select(b => b.Id).ToList();
        var auditByBacklog = await _backlogs.GetAuditForBacklogsAsync(allIds);
        var months = new HashSet<string>();
        foreach (var b in all)
        {
            if (!string.IsNullOrWhiteSpace(b.PeriodMonth)) months.Add(b.PeriodMonth!);
            var audit = auditByBacklog.TryGetValue(b.Id, out var a) ? a : Array.Empty<BacklogAuditEntry>();
            foreach (var entry in audit)
                if (entry.Field == "period_month" && !string.IsNullOrWhiteSpace(entry.OldValue))
                    months.Add(entry.OldValue!);
        }

        var dir = ArchiveDir();
        foreach (var key in months)
        {
            // Only months strictly before the current month are "completed".
            if (string.CompareOrdinal(key, currentMonthKey) >= 0) continue;
            if (!TryParseMonthKey(key, out var y, out var m)) continue;

            var path = Path.Combine(dir, FileNameFor(y, m));
            if (!File.Exists(path)) await ExportMonthAsync(y, m);
        }
    }

    private async Task AppendSectionAsync(
        StringBuilder sb, string title, IReadOnlyList<Backlog> backlogs,
        IReadOnlyDictionary<int, decimal> loggedByBacklog, IReadOnlyDictionary<int, Tag> tagsById,
        IReadOnlyDictionary<int, IReadOnlyList<int>> tagLinks,
        IReadOnlyDictionary<int, string> userNames, IReadOnlyDictionary<int, string> pcaNames,
        IReadOnlySet<DateOnly> holidaySet, IReadOnlyDictionary<int, string> teamNames)
    {
        sb.AppendLine($"## {title}");
        sb.AppendLine();

        if (backlogs.Count == 0)
        {
            sb.AppendLine("_(none)_");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Team | Code | Project | Type | PCT | PCA | Internal | External | Estimate | Logged | Progress | Tags | Schedule |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (var b in backlogs.OrderBy(b => b.BacklogCode, StringComparer.OrdinalIgnoreCase))
        {
            var tasks = await _tasks.GetActiveByBacklogAsync(b.Id);
            var isDone = IsDone(tasks);

            var logged = loggedByBacklog.TryGetValue(b.Id, out var h) ? h : 0m;
            var estimate = b.OfficialEstimateHours ?? b.RoughEstimateHours;

            var state = _schedule.Evaluate(
                _clock.Today, b.StartDate, b.DeadlineInternal, estimate, logged, isDone, holidaySet, _calc);

            var pctName = b.AssigneeUserId is { } uid && userNames.TryGetValue(uid, out var un) ? un : "";
            var pcaName = b.PcaContactId is { } pid && pcaNames.TryGetValue(pid, out var pn) ? pn : "";
            var teamName = b.TeamId is { } tid && teamNames.TryGetValue(tid, out var tn) ? tn : "";

            var tagText = tagLinks.TryGetValue(b.Id, out var ids)
                ? string.Join(", ", ids.Where(tagsById.ContainsKey).Select(i => tagsById[i].Text))
                : "";

            sb.AppendLine(string.Join(" | ", new[]
            {
                "",
                Esc(teamName),
                Esc(b.BacklogCode),
                Esc(b.Project),
                Esc(b.Type ?? ""),
                Esc(pctName),
                Esc(pcaName),
                Day(b.DeadlineInternal),
                Day(b.DeadlineExternal),
                FormatHelpers.FormatHoursNullable(estimate),
                FormatHelpers.FormatHoursNullable(logged),
                b.ProgressPercent is { } p ? $"{p}%" : "—",
                Esc(tagText),
                state.ToString(),
                "",
            }).Trim());
        }

        sb.AppendLine();
    }

    // Done = ≥1 active task AND every active task Status=="Done" (§6.2). Zero active tasks => not Done.
    private static bool IsDone(IReadOnlyList<TaskItem> tasks)
        => tasks.Count > 0 && tasks.All(t => string.Equals(t.Status, "Done", StringComparison.Ordinal));

    private string ArchiveDir()
    {
        if (!string.IsNullOrWhiteSpace(_config.ArchivePath))
            return _config.ArchivePath;

        var dbDir = Path.GetDirectoryName(_config.DbPath);
        return Path.Combine(string.IsNullOrEmpty(dbDir) ? "." : dbDir, "TaskListArchives");
    }

    private static string MonthKey(int year, int month) =>
        $"{year:D4}-{month:D2}";

    private static bool TryParseMonthKey(string key, out int year, out int month)
    {
        year = 0;
        month = 0;
        var parts = key.Split('-');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out year)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out month)
            && month is >= 1 and <= 12;
    }

    private static string Day(DateOnly? d) =>
        d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";

    private static string Esc(string s) => s.Replace("|", "\\|");
}
