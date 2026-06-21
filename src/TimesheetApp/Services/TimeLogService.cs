using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Business rules for time logging: precision/weekday/8h validation, day-total reads,
/// rounding, and atomic smart-input apply (with pre-bulk backup). No System.Windows.* / no Dapper.
/// (XC-02/03/04/05, XC-10, RPT-04)</summary>
public sealed class TimeLogService : ITimeLogService
{
    private const decimal DayCap = 8m;

    private readonly ITimeLogRepository _logs;
    private readonly IUserRepository _users;
    private readonly ITaskRepository _tasks;
    private readonly IDbBackupHelper _backup;
    private readonly IClock _clock;

    public TimeLogService(
        ITimeLogRepository logs, IUserRepository users, ITaskRepository tasks,
        IDbBackupHelper backup, IClock clock)
    {
        _logs = logs; _users = users; _tasks = tasks; _backup = backup; _clock = clock;
    }

    public async Task<SaveResult> SaveCellAsync(int userId, int taskId, DateOnly date, decimal hours)
    {
        if (hours <= 0m) return Err("Hours must be greater than 0.");
        if (HasMoreThanOneDecimal(hours)) return Err("Hours may have at most 1 decimal place.");
        if (IsWeekend(date)) return Err("Time can only be logged Monday–Friday.");

        var rounded = Round1(hours);
        if (rounded > DayCap) return Err($"A single cell cannot exceed {DayCap}h."); // XC-02

        // XC-03: read the day's OTHER logs from storage and check the whole-day total after merge.
        var sameDay = await _logs.GetByUserAndRangeAsync(userId, date, date);
        var otherTasksTotal = sameDay.Where(l => l.TaskId != taskId).Sum(l => l.Hours);
        if (otherTasksTotal + rounded > DayCap)
            return Err($"Total for {date:yyyy-MM-dd} would be {otherTasksTotal + rounded}h (> {DayCap}h).");

        await _logs.UpsertAsync(new TimeLog(0, userId, taskId, date, rounded, _clock.UtcNow));
        return Ok();
    }

    public Task ClearCellAsync(int userId, int taskId, DateOnly date)
        => _logs.DeleteAsync(userId, taskId, date); // TS-03 empty=0 => delete

    public async Task<SaveResult> ValidateDayTotalsAsync(int userId, IReadOnlyList<CellAssignment> cells, int taskId)
    {
        foreach (var dayGroup in cells.GroupBy(c => c.Date))
        {
            if (IsWeekend(dayGroup.Key)) return Err($"{dayGroup.Key:yyyy-MM-dd} is a weekend.");

            var sameDay = await _logs.GetByUserAndRangeAsync(userId, dayGroup.Key, dayGroup.Key);
            var otherTasksTotal = sameDay.Where(l => l.TaskId != taskId).Sum(l => l.Hours);
            var proposed = dayGroup.Sum(c => Round1(c.Hours));
            if (otherTasksTotal + proposed > DayCap)
                return Err($"{dayGroup.Key:yyyy-MM-dd}: {otherTasksTotal + proposed}h exceeds {DayCap}h.");
        }
        return Ok();
    }

    public async Task<SaveResult> ApplySmartInputAsync(int userId, int taskId, IReadOnlyList<CellAssignment> cells)
    {
        var validation = await ValidateDayTotalsAsync(userId, cells, taskId);
        if (!validation.Ok) return validation;

        await _backup.BackupAsync(); // XC-10: backup BEFORE the bulk write

        var logs = cells
            .Select(c => new TimeLog(0, userId, taskId, c.Date, Round1(c.Hours), _clock.UtcNow))
            .ToList();
        await _logs.UpsertBatchAsync(logs); // SI-05 atomic batch
        return Ok();
    }

    public async Task<WeekGrid> GetWeekAsync(int userId, DateOnly mondayOfWeek)
    {
        var monday = MondayOf(mondayOfWeek);
        var friday = monday.AddDays(4);
        var logs = await _logs.GetByUserAndRangeAsync(userId, monday, friday);
        var tasks = await _tasks.GetActiveForTimesheetAsync();

        var byKey = logs.ToLookup(l => l.TaskId);
        var rows = tasks.Select(t =>
        {
            decimal? On(DateOnly d) => byKey[t.Id].Where(l => l.WorkDate == d).Select(l => (decimal?)l.Hours).FirstOrDefault();
            // [ASSUMED] RequestCode left empty here. GetWeekAsync backs TS-01/02/05 (P3 scope);
            // ITaskRepository.GetActiveForTimesheetAsync (spec §3) returns TaskItem which has no
            // request_code. P3 either (a) extends that query to project request_code, or (b) the
            // VM joins it from IRequestRepository when shaping rows. P2 ships the structurally
            // correct WeekGrid with hours populated; RequestCode population is a P3 concern.
            return new WeekRow(t.Id, RequestCode: "", t.TaskName, t.OrderIndex,
                On(monday), On(monday.AddDays(1)), On(monday.AddDays(2)), On(monday.AddDays(3)), On(monday.AddDays(4)));
        }).ToList();

        return new WeekGrid(monday, rows);
    }

    public async Task<IReadOnlyList<User>> GetUsersMissingLogsAsync(int workdayWindowN)
    {
        var window = LastNWorkingDays(_clock.Today, workdayWindowN); // includes today (RPT-04)
        var earliest = window.Min();
        var withLogs = (await _logs.GetUserIdsWithLogsInRangeAsync(earliest, _clock.Today)).ToHashSet();
        var active = await _users.GetActiveAsync();
        return active.Where(u => !withLogs.Contains(u.Id)).ToList();
    }

    // ---- helpers ----
    private static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    private static decimal Round1(decimal v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);
    private static bool HasMoreThanOneDecimal(decimal v) => v != Round1(v);
    private static DateOnly MondayOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private static List<DateOnly> LastNWorkingDays(DateOnly today, int n)
    {
        var days = new List<DateOnly>();
        var d = today;
        while (days.Count < n)
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Add(d);
            d = d.AddDays(-1);
        }
        return days;
    }

    private static SaveResult Ok() => new(true, null);
    private static SaveResult Err(string message) => new(false, message);
}
