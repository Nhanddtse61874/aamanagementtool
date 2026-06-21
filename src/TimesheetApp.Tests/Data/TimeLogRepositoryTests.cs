using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using Xunit;

namespace TimesheetApp.Tests.Data;

public class TimeLogRepositoryTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private TimeLogRepository _repo = null!;
    private int _userId, _taskId;

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _repo = new TimeLogRepository(_db);
        (_userId, _taskId) = await _db.SeedUserAndTaskAsync();
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UpsertAsync_inserts_then_updates_same_natural_key_no_duplicate()  // TS-07
    {
        var d = new DateOnly(2026, 6, 16);
        await _repo.UpsertAsync(new TimeLog(0, _userId, _taskId, d, 3m, DateTimeOffset.UtcNow));
        await _repo.UpsertAsync(new TimeLog(0, _userId, _taskId, d, 5m, DateTimeOffset.UtcNow));

        var rows = await _repo.GetByUserAndRangeAsync(_userId, d, d);
        Assert.Single(rows);
        Assert.Equal(5m, rows[0].Hours);   // updated, not duplicated
    }

    [Fact]
    public async Task UpsertAsync_round_trips_dateonly_and_decimal()  // XC-04 / XC-05 boundary mapping
    {
        var d = new DateOnly(2026, 6, 18);
        await _repo.UpsertAsync(new TimeLog(0, _userId, _taskId, d, 3.4m, DateTimeOffset.UtcNow));

        var rows = await _repo.GetByUserAndRangeAsync(_userId, d, d);
        Assert.Single(rows);
        Assert.Equal(d, rows[0].WorkDate);
        Assert.Equal(3.4m, rows[0].Hours);
        Assert.Equal(_userId, rows[0].UserId);
        Assert.Equal(_taskId, rows[0].TaskId);
    }

    [Fact]
    public async Task DeleteAsync_removes_the_row()  // TS-03
    {
        var d = new DateOnly(2026, 6, 16);
        await _repo.UpsertAsync(new TimeLog(0, _userId, _taskId, d, 4m, DateTimeOffset.UtcNow));
        await _repo.DeleteAsync(_userId, _taskId, d);

        Assert.Empty(await _repo.GetByUserAndRangeAsync(_userId, d, d));
    }

    [Fact]
    public async Task GetByUserAndRangeAsync_filters_by_user_and_inclusive_range()  // XC-03
    {
        await _repo.UpsertAsync(new TimeLog(0, _userId, _taskId, new DateOnly(2026, 6, 15), 1m, DateTimeOffset.UtcNow));
        await _repo.UpsertAsync(new TimeLog(0, _userId, _taskId, new DateOnly(2026, 6, 19), 2m, DateTimeOffset.UtcNow));
        // out of range
        await _repo.UpsertAsync(new TimeLog(0, _userId, _taskId, new DateOnly(2026, 6, 22), 3m, DateTimeOffset.UtcNow));

        var rows = await _repo.GetByUserAndRangeAsync(_userId, new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 19));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.InRange(r.WorkDate, new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 19)));
    }

    [Fact]
    public async Task GetReportRowsAsync_resolves_name_even_when_task_soft_deleted()  // XC-06
    {
        var d = new DateOnly(2026, 6, 16);
        await _repo.UpsertAsync(new TimeLog(0, _userId, _taskId, d, 8m, DateTimeOffset.UtcNow));
        await _db.SetTaskActiveAsync(_taskId, false);  // soft-delete the task

        var rows = await _repo.GetReportRowsAsync(_userId, d, d);

        Assert.Single(rows);
        Assert.False(string.IsNullOrEmpty(rows[0].TaskName)); // name still resolves despite is_active=0
        Assert.Equal(8m, rows[0].Hours);
    }

    [Fact]
    public async Task GetExportRowsAsync_resolves_name_when_soft_deleted_and_honours_project_filter()  // XC-06 / EXP
    {
        var d = new DateOnly(2026, 6, 16);
        await _repo.UpsertAsync(new TimeLog(0, _userId, _taskId, d, 6m, DateTimeOffset.UtcNow));
        await _db.SetTaskActiveAsync(_taskId, false);  // soft-deleted task must still export

        var all = await _repo.GetExportRowsAsync(d, d, projectFilter: null);
        Assert.Single(all);
        Assert.False(string.IsNullOrEmpty(all[0].TaskName));
        Assert.Equal("ProjectX", all[0].Project);

        var matched = await _repo.GetExportRowsAsync(d, d, projectFilter: "ProjectX");
        Assert.Single(matched);

        var unmatched = await _repo.GetExportRowsAsync(d, d, projectFilter: "NoSuchProject");
        Assert.Empty(unmatched);
    }

    [Fact]
    public async Task UpsertBatchAsync_writes_all_rows_in_one_call()  // SI-05
    {
        var logs = new[]
        {
            new TimeLog(0, _userId, _taskId, new DateOnly(2026, 6, 16), 3.3m, DateTimeOffset.UtcNow),
            new TimeLog(0, _userId, _taskId, new DateOnly(2026, 6, 17), 3.4m, DateTimeOffset.UtcNow),
        };
        await _repo.UpsertBatchAsync(logs);

        var rows = await _repo.GetByUserAndRangeAsync(_userId, new DateOnly(2026, 6, 16), new DateOnly(2026, 6, 17));
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { 3.3m, 3.4m }, rows.Select(r => r.Hours).ToArray());
    }

    [Fact]
    public async Task UpsertBatchAsync_rolls_back_all_rows_when_one_violates_fk()  // SI-05 atomicity
    {
        var good = new DateOnly(2026, 6, 16);
        var logs = new[]
        {
            new TimeLog(0, _userId, _taskId, good, 2m, DateTimeOffset.UtcNow),
            new TimeLog(0, _userId, 999999, good, 2m, DateTimeOffset.UtcNow), // bad FK -> whole batch must abort
        };

        await Assert.ThrowsAnyAsync<Exception>(() => _repo.UpsertBatchAsync(logs));

        // The good row must NOT have been committed (all-or-nothing).
        var rows = await _repo.GetByUserAndRangeAsync(_userId, good, good);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetUserIdsWithLogsInRangeAsync_returns_users_with_logs()  // RPT-04
    {
        await _repo.UpsertAsync(new TimeLog(0, _userId, _taskId, new DateOnly(2026, 6, 16), 2m, DateTimeOffset.UtcNow));
        var ids = await _repo.GetUserIdsWithLogsInRangeAsync(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 19));
        Assert.Contains(_userId, ids);
    }
}
