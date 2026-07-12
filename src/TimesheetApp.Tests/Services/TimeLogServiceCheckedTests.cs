using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Services;

// M8.3 W0-T2. M8.2 built optimistic concurrency across eight repositories and stopped one layer short:
// no service method ever passed an expectedVersion. So the API had two broken choices — go THROUGH the
// service and never conflict (409 unreachable, every rowVersion on the wire pure decoration), or go
// AROUND it to the repository and bypass the 8h cap, the holiday guard, the weekend guard and the
// rounding rule. These tests pin the third option: conflicts AND rules, together, through the service.
//
// The conflict tests run against a REAL TimeLogRepository on a REAL temp-file DB (TestDb). Never
// :memory: — each connection would get its own private database, so two "clients" could never collide
// and the test would pass while asserting nothing.
public class TimeLogServiceCheckedTests
{
    private static readonly DateOnly Tue = new(2026, 6, 16);   // weekday
    private static readonly DateOnly Wed = new(2026, 6, 17);   // weekday, used as the holiday
    private static readonly DateOnly Sat = new(2026, 6, 20);   // weekend

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; }
        public DateTimeOffset UtcNow { get; init; } = new(2026, 6, 16, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeCurrentTeam : ICurrentTeamService
    {
        public int ActiveTeamId { get; set; } = 1;
        public Team? ActiveTeam => null;
        public IReadOnlyList<Team> AvailableTeams => Array.Empty<Team>();
        public event EventHandler? ActiveTeamChanged { add { } remove { } }
        public Task InitializeAsync(int currentUserId) => Task.CompletedTask;
        public Task SetActiveTeamAsync(int teamId) { ActiveTeamId = teamId; return Task.CompletedTask; }
    }

    // Real repository + real service over a real file DB: the only way a version conflict can actually occur.
    private sealed class Harness : IDisposable
    {
        public TestDb Db = null!;
        public TimeLogRepository Logs = null!;
        public Mock<IHolidayRepository> Holidays = new();
        public TimeLogService Svc = null!;
        public int UserId;
        public int TaskId;

        public static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            h.Db = await TestDb.CreateAsync();
            h.Logs = new TimeLogRepository(h.Db);
            (h.UserId, h.TaskId) = await h.Db.SeedUserAndTaskAsync();

            h.Holidays.Setup(x => x.IsHolidayAsync(It.IsAny<DateOnly>())).ReturnsAsync(false);
            h.Holidays.Setup(x => x.GetAllAsync()).ReturnsAsync(Array.Empty<Holiday>());

            h.Svc = new TimeLogService(
                h.Logs, new Mock<IUserRepository>().Object, new Mock<ITaskRepository>().Object,
                new Mock<IBacklogRepository>().Object, new Mock<ITeamRepository>().Object,
                new FakeCurrentTeam(), new Mock<IDbBackupHelper>().Object,
                new FakeClock { Today = Tue }, new Mock<IAppConfig>().Object,
                new NoOpJournalWarningSink(), h.Holidays.Object);
            return h;
        }

        public async Task<TimeLog?> CellAsync() =>
            (await Logs.GetByUserAndRangeAsync(UserId, Tue, Tue)).FirstOrDefault(l => l.TaskId == TaskId);

        public void Dispose() => Db.Dispose();
    }

    // ================= THE WAVE'S OBSERVABLE TRUTH =================

    // Two clients read the same cell at the same version; A saves; B's save at the stale version must
    // LOSE — through the service, not merely at the repository.
    [Fact]
    public async Task Two_clients_at_the_same_version__the_second_save_conflicts_THROUGH_the_service()
    {
        using var h = await Harness.CreateAsync();

        var seed = await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 2m, expectedVersion: null);
        Assert.True(seed.Ok);

        // Both clients read the cell and now hold the SAME version.
        var v = (await h.CellAsync())!.RowVersion;

        // Client A saves at v -> wins; the version moves on.
        var a = await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 3m, v);
        Assert.True(a.Ok);
        Assert.NotEqual(v, a.RowVersion);

        // Client B saves at the SAME v -> loses. Silently overwriting A here is the lost update that
        // this entire mechanism exists to prevent.
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 4m, v));
        Assert.Equal("TimeLogs", ex.Table);

        // A's write stands; B's was not applied.
        Assert.Equal(3m, (await h.CellAsync())!.Hours);
    }

    // The other half, and it matters as much: the rules must still BITE on the checked path. If they did
    // not, the API would be the one caller in the product that can log 40 hours on a Sunday holiday.
    [Fact]
    public async Task A_checked_save_of_40h_on_a_holiday_is_a_RULE_failure_not_a_conflict__and_writes_nothing()
    {
        using var h = await Harness.CreateAsync();
        h.Holidays.Setup(x => x.IsHolidayAsync(Wed)).ReturnsAsync(true);

        var r = await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Wed, 40m, expectedVersion: null);

        Assert.False(r.Ok);                                  // a RESULT (-> HTTP 400)...
        Assert.Contains("holiday", r.Error!);                // ...naming the rule that was broken...
        Assert.Equal(0, r.RowVersion);                       // ...with no version, because nothing was written.
        Assert.Empty(await h.Logs.GetByUserAndRangeAsync(h.UserId, Wed, Wed));
    }

    // ================= the rest of the rule set, on the checked path =================

    [Fact]
    public async Task The_8h_day_cap_still_bites_on_the_checked_path__and_writes_nothing()
    {
        using var h = await Harness.CreateAsync();
        var otherTaskId = await h.Db.SeedTaskAsync(await h.Db.SeedRequestAsync("REQ-002"), "Other task");

        // 7h already logged that day on a DIFFERENT task.
        Assert.True((await h.Svc.SaveCellCheckedAsync(h.UserId, otherTaskId, Tue, 7m, null)).Ok);

        // 2h more would make the day 9h (XC-03).
        var r = await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 2m, null);

        Assert.False(r.Ok);
        Assert.Contains("9", r.Error!);
        Assert.Null(await h.CellAsync());                    // this task's cell was never created
        Assert.Equal(7m, (await h.Logs.GetByUserAndRangeAsync(h.UserId, Tue, Tue)).Single().Hours);
    }

    [Fact]
    public async Task The_weekend_guard_still_bites_on_the_checked_path()
    {
        using var h = await Harness.CreateAsync();

        var r = await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Sat, 2m, null);

        Assert.False(r.Ok);
        Assert.Empty(await h.Logs.GetByUserAndRangeAsync(h.UserId, Sat, Sat));
    }

    [Fact]
    public async Task The_checked_path_rounds_to_one_decimal_AwayFromZero_like_the_unchecked_one()
    {
        using var h = await Harness.CreateAsync();

        Assert.True((await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 2.5m, null)).Ok);
        Assert.Equal(2.5m, (await h.CellAsync())!.Hours);

        // 1.55 has two decimals -> rejected outright, not silently rounded.
        Assert.False((await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 1.55m, null)).Ok);
    }

    // DRIFT GUARD. SaveCellAsync keeps its own copy of the rule set (it is left byte-for-byte because WPF
    // calls it until M8.10 deletes it), so the rules currently live in two places. This test pins them
    // together: if a future change teaches one path a rule and forgets the other, the suite fails loudly
    // instead of silently exempting the web from a business rule.
    [Fact]
    public async Task Checked_and_unchecked_saves_reject_identical_inputs_with_identical_messages()
    {
        using var h = await Harness.CreateAsync();
        h.Holidays.Setup(x => x.IsHolidayAsync(Wed)).ReturnsAsync(true);

        // 7h on another task, so the whole-day cap can trip for a 2h save on Tue.
        var otherTaskId = await h.Db.SeedTaskAsync(await h.Db.SeedRequestAsync("REQ-002"), "Other task");
        Assert.True((await h.Svc.SaveCellCheckedAsync(h.UserId, otherTaskId, Tue, 7m, null)).Ok);

        var cases = new (decimal Hours, DateOnly Date, string Rule)[]
        {
            (0m,    Tue, "zero"),
            (-2m,   Tue, "negative"),
            (1.55m, Tue, "two decimals"),
            (9m,    Tue, "per-cell 8h cap"),
            (2m,    Sat, "weekend"),
            (2m,    Wed, "holiday"),
            (2m,    Tue, "whole-day 8h cap (7h already on another task)"),
        };

        foreach (var (hours, date, rule) in cases)
        {
            var plain = await h.Svc.SaveCellAsync(h.UserId, h.TaskId, date, hours);
            var chk = await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, date, hours, null);

            Assert.False(plain.Ok, rule);
            Assert.False(chk.Ok, rule);
            Assert.Equal(plain.Error, chk.Error);
        }

        // Neither path wrote anything for this task, on any of those days.
        Assert.Null(await h.CellAsync());
    }

    // ================= the five outcomes of a checked UPSERT, seen from the service =================

    // expectedVersion: null asserts "I believe this cell is empty". If it is NOT empty, that belief is
    // wrong and somebody else filled the cell -> conflict, not a silent overwrite.
    [Fact]
    public async Task Believing_an_occupied_cell_is_empty_is_a_conflict()
    {
        using var h = await Harness.CreateAsync();
        Assert.True((await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 2m, null)).Ok);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 3m, expectedVersion: null));

        Assert.Equal(2m, (await h.CellAsync())!.Hours);
    }

    // Saving at a version whose row was DELETED must conflict, not resurrect the row at version 1.
    [Fact]
    public async Task Saving_at_a_version_whose_row_was_deleted_is_a_conflict_not_a_resurrection()
    {
        using var h = await Harness.CreateAsync();
        var v = (await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 2m, null)).RowVersion;

        await h.Svc.ClearCellCheckedAsync(h.UserId, h.TaskId, Tue, v);   // another client deletes it

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 3m, v));
        Assert.True(ex.Deleted);                                          // the client is told WHY
        Assert.Null(await h.CellAsync());
    }

    [Fact]
    public async Task ClearCellChecked_clears_at_the_current_version_and_conflicts_on_a_stale_one()
    {
        using var h = await Harness.CreateAsync();
        var v = (await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 2m, null)).RowVersion;

        // A stale version must not delete.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Svc.ClearCellCheckedAsync(h.UserId, h.TaskId, Tue, v + 99));
        Assert.NotNull(await h.CellAsync());

        // The current version does.
        await h.Svc.ClearCellCheckedAsync(h.UserId, h.TaskId, Tue, v);
        Assert.Null(await h.CellAsync());

        // Clearing a cell that is already gone is itself a conflict: the caller holds a version that no
        // longer exists, and it must find that out rather than be told "success".
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Svc.ClearCellCheckedAsync(h.UserId, h.TaskId, Tue, v));
    }

    // The returned version is the caller's next expectedVersion — a client never has to re-read (and a
    // read-back would be racy anyway).
    [Fact]
    public async Task The_returned_RowVersion_chains_straight_into_the_next_save()
    {
        using var h = await Harness.CreateAsync();

        var r1 = await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 1m, null);
        var r2 = await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 2m, r1.RowVersion);
        var r3 = await h.Svc.SaveCellCheckedAsync(h.UserId, h.TaskId, Tue, 3m, r2.RowVersion);

        Assert.True(r3.Ok);
        Assert.Equal(3m, (await h.CellAsync())!.Hours);
        Assert.True(r3.RowVersion > r1.RowVersion);
    }

    // ================= the wiring, pinned precisely (mocked repository) =================

    private static TimeLogService MakeMocked(Mock<ITimeLogRepository> logs, Mock<IHolidayRepository> holidays)
        => new(logs.Object, new Mock<IUserRepository>().Object, new Mock<ITaskRepository>().Object,
               new Mock<IBacklogRepository>().Object, new Mock<ITeamRepository>().Object,
               new FakeCurrentTeam(), new Mock<IDbBackupHelper>().Object,
               new FakeClock { Today = Tue }, new Mock<IAppConfig>().Object,
               new NoOpJournalWarningSink(), holidays.Object);

    private static (Mock<ITimeLogRepository> Logs, Mock<IHolidayRepository> Holidays) EmptyDay()
    {
        var logs = new Mock<ITimeLogRepository>();
        logs.Setup(r => r.GetByUserAndRangeAsync(It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>()))
            .ReturnsAsync(Array.Empty<TimeLog>());
        var holidays = new Mock<IHolidayRepository>();
        holidays.Setup(x => x.IsHolidayAsync(It.IsAny<DateOnly>())).ReturnsAsync(false);
        return (logs, holidays);
    }

    // The bump-only UpsertAsync "silently wins every race" (its own XML doc says so, and names the web API
    // as the caller that must not use it). The checked path must never reach it.
    [Fact]
    public async Task Checked_save_never_touches_the_bump_only_write_and_passes_the_version_through_unnormalized()
    {
        var (logs, holidays) = EmptyDay();
        logs.Setup(r => r.UpsertCheckedAsync(It.IsAny<TimeLog>(), It.IsAny<long?>())).ReturnsAsync(42L);
        var svc = MakeMocked(logs, holidays);

        var r = await svc.SaveCellCheckedAsync(1, 2, Tue, 3m, expectedVersion: null);

        Assert.True(r.Ok);
        Assert.Equal(42L, r.RowVersion);

        // null must arrive as null: it MEANS "I believe this cell is empty" and must not be normalized to 0.
        logs.Verify(x => x.UpsertCheckedAsync(
            It.Is<TimeLog>(l => l.UserId == 1 && l.TaskId == 2 && l.WorkDate == Tue && l.Hours == 3m),
            It.Is<long?>(v => v == null)), Times.Once);

        logs.Verify(x => x.UpsertAsync(It.IsAny<TimeLog>()), Times.Never);
        logs.Verify(x => x.UpsertBatchAsync(It.IsAny<IReadOnlyList<TimeLog>>()), Times.Never);
    }

    [Fact]
    public async Task A_concrete_expectedVersion_reaches_the_repository_unchanged()
    {
        var (logs, holidays) = EmptyDay();
        logs.Setup(r => r.UpsertCheckedAsync(It.IsAny<TimeLog>(), It.IsAny<long?>())).ReturnsAsync(8L);
        var svc = MakeMocked(logs, holidays);

        await svc.SaveCellCheckedAsync(1, 2, Tue, 3m, expectedVersion: 7L);

        logs.Verify(x => x.UpsertCheckedAsync(It.IsAny<TimeLog>(), It.Is<long?>(v => v == 7L)), Times.Once);
    }

    // Validation runs FIRST: a broken rule must reach NO repository write of any kind.
    [Fact]
    public async Task A_broken_rule_reaches_no_repository_write_at_all()
    {
        var (logs, holidays) = EmptyDay();
        var svc = MakeMocked(logs, holidays);

        var r = await svc.SaveCellCheckedAsync(1, 2, Sat, 3m, expectedVersion: 5L);   // weekend

        Assert.False(r.Ok);
        Assert.Equal(0, r.RowVersion);
        logs.Verify(x => x.UpsertCheckedAsync(It.IsAny<TimeLog>(), It.IsAny<long?>()), Times.Never);
        logs.Verify(x => x.UpsertAsync(It.IsAny<TimeLog>()), Times.Never);
    }

    [Fact]
    public async Task ClearCellChecked_uses_the_checked_delete_not_the_bump_only_one()
    {
        var (logs, holidays) = EmptyDay();
        var svc = MakeMocked(logs, holidays);

        await svc.ClearCellCheckedAsync(1, 2, Tue, 4L);

        logs.Verify(x => x.DeleteCheckedAsync(1, 2, Tue, 4L), Times.Once);
        logs.Verify(x => x.DeleteAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateOnly>()), Times.Never);
    }
}
