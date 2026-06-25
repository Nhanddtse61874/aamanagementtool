using System.Globalization;
using System.IO;
using System.Text;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

// DR-09: writes one markdown file per week, accumulating continuously. Backfills any completed week
// that has data but no file yet on every startup (desktop app has no background scheduler).
public sealed class StandupArchiveService : IStandupArchiveService
{
    private readonly IStandupRepository _repo;
    private readonly IUserRepository _users;
    private readonly IAppConfig _config;
    private readonly IClock _clock;

    public StandupArchiveService(IStandupRepository repo, IUserRepository users, IAppConfig config, IClock clock)
    {
        _repo = repo;
        _users = users;
        _config = config;
        _clock = clock;
    }

    public string FileNameFor(DateOnly anyDayInWeek) =>
        MondayOf(anyDayInWeek).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "_daily.md";

    public async Task<string?> ExportWeekAsync(DateOnly anyDayInWeek)
    {
        var monday = MondayOf(anyDayInWeek);
        var friday = monday.AddDays(4);

        var entries = await _repo.GetEntriesForRangeAsync(monday, friday);
        if (entries.Count == 0) return null; // no data -> no file (DR-09)

        var issues = (await _repo.GetIssuesForEntriesAsync(entries.Select(e => e.Id).ToList()))
            .ToLookup(i => i.EntryId);
        var names = (await _users.GetAllAsync() ?? Array.Empty<User>())
            .ToDictionary(u => u.Id, u => u.Name);

        var md = BuildMarkdown(monday, friday, entries, issues, names);

        var dir = ArchiveDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileNameFor(monday));
        await File.WriteAllTextAsync(path, md, Encoding.UTF8);
        return path;
    }

    public async Task BackfillMissingWeeksAsync()
    {
        var currentMonday = MondayOf(_clock.Today);
        // Everything strictly before the current week is a "completed" week.
        var entries = await _repo.GetEntriesForRangeAsync(new DateOnly(2000, 1, 1), currentMonday.AddDays(-1));
        if (entries.Count == 0) return;

        var weekMondays = entries.Select(e => MondayOf(e.WorkDate)).Distinct().ToList();
        var dir = ArchiveDir();
        foreach (var monday in weekMondays)
        {
            var path = Path.Combine(dir, FileNameFor(monday));
            if (!File.Exists(path)) await ExportWeekAsync(monday);
        }
    }

    private string ArchiveDir()
    {
        var dbDir = Path.GetDirectoryName(_config.DbPath);
        return Path.Combine(string.IsNullOrEmpty(dbDir) ? "." : dbDir, "StandupArchives");
    }

    private static string BuildMarkdown(
        DateOnly monday, DateOnly friday, IReadOnlyList<StandupEntry> entries,
        ILookup<int, StandupIssue> issues, IReadOnlyDictionary<int, string> names)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Daily Standup — Week of {monday:yyyy-MM-dd}");
        sb.AppendLine();

        var byDate = entries.GroupBy(e => e.WorkDate).OrderBy(g => g.Key);
        foreach (var dateGroup in byDate)
        {
            sb.AppendLine($"## {dateGroup.Key:yyyy-MM-dd} ({dateGroup.Key.DayOfWeek})");
            sb.AppendLine();

            var byUser = dateGroup
                .GroupBy(e => e.UserId)
                .OrderBy(g => names.TryGetValue(g.Key, out var n) ? n : "", StringComparer.OrdinalIgnoreCase);
            foreach (var userGroup in byUser)
            {
                var name = names.TryGetValue(userGroup.Key, out var n) ? n : $"User {userGroup.Key}";
                sb.AppendLine($"### {name}");
                AppendSection(sb, "Yesterday", userGroup.Where(e => e.Section == StandupSection.Yesterday), issues);
                AppendSection(sb, "Today", userGroup.Where(e => e.Section == StandupSection.Today), issues);
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static void AppendSection(
        StringBuilder sb, string title, IEnumerable<StandupEntry> rows, ILookup<int, StandupIssue> issues)
    {
        var list = rows.OrderBy(e => e.OrderIndex).ThenBy(e => e.Id).ToList();
        if (list.Count == 0) return;

        sb.AppendLine($"**{title}**");
        sb.AppendLine();
        sb.AppendLine("| Code | Task | Detail | Deadline | Status |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var e in list)
        {
            var deadline = e.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            sb.AppendLine($"| {Esc(e.RequestCode)} | {Esc(e.TaskText)} | {Esc(e.Description)} | {deadline} | {Esc(e.Status)} |");
        }
        foreach (var e in list)
        {
            foreach (var i in issues[e.Id].OrderBy(x => x.OrderIndex).ThenBy(x => x.Id))
            {
                var solution = string.IsNullOrWhiteSpace(i.SolutionText) ? "(pending)" : i.SolutionText;
                sb.AppendLine($"- ⚠ [{i.Status}] {Esc(e.TaskText)} — issue: {i.IssueText} · solution: {solution}");
            }
        }
        sb.AppendLine();
    }

    private static string Esc(string s) => s.Replace("|", "\\|");

    // Monday-start, culture-independent (matches the app's week convention).
    private static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
