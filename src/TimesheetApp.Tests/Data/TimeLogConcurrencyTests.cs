using System.Globalization;
using Dapper;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using Xunit;

namespace TimesheetApp.Tests.Data;

// M8.2 optimistic concurrency on TimeLogs (schema v10 row_version).
//
// TimeLogs is written through an UPSERT, not a plain UPDATE, so the version rule has FIVE cases,
// not four -- the row can be absent on either side of the check. The fifth (version supplied, row
// already deleted) is the one that silently destroys data, so it gets the most attention below.
//
// No threads. The conflict window IS the stale version number, so two "simultaneous" clients
// reproduce perfectly in sequence: both read v=1, A writes, B writes with expected=1 -> conflict.
// Deterministic, fast, and it cannot flake. (TestDb is a temp FILE db -- ':memory:' would give
// every connection its own database, so a conflict could never happen and the test would pass
// while asserting nothing.)
public class TimeLogConcurrencyTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private TimeLogRepository _repo = null!;
    private int _userId, _taskId;

    private static readonly DateOnly D = new(2026, 6, 16);

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

    // ---- the five cases of a versioned upsert -------------------------------------------------

    [Fact]  // 1/5: no version supplied, no row => plain insert at v1.
    public async Task UpsertChecked_expected_null_and_no_row_inserts_at_version_1()
    {
        var version = await _repo.UpsertCheckedAsync(Log(3m), expectedVersion: null);

        Assert.Equal(1, version);
        Assert.Equal(1, await VersionAsync());
        Assert.Equal(3m, await HoursAsync());
    }

    [Fact]  // 2/5: caller believes the cell is empty, but someone else already filled it.
    public async Task UpsertChecked_expected_null_but_row_exists_conflicts()
    {
        await _repo.UpsertCheckedAsync(Log(3m), expectedVersion: null);

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _repo.UpsertCheckedAsync(Log(5m), expectedVersion: null));

        Assert.Equal("TimeLogs", ex.Table);
        Assert.Null(ex.ExpectedVersion);
        Assert.False(ex.Deleted);              // it exists -- someone else created it
        Assert.Equal(3m, await HoursAsync());  // the other writer's value survives
        Assert.Equal(1, await VersionAsync()); // and the failed write did NOT bump
    }

    [Fact]  // 3/5: the happy path -- version matches, row updates, version becomes N+1.
    public async Task UpsertChecked_matching_version_updates_and_bumps()
    {
        var v1 = await _repo.UpsertCheckedAsync(Log(3m), expectedVersion: null);
        var v2 = await _repo.UpsertCheckedAsync(Log(5m), expectedVersion: v1);

        Assert.Equal(2, v2);                    // RETURNING gives the POST-update version
        Assert.Equal(2, await VersionAsync());
        Assert.Equal(5m, await HoursAsync());   // and the write actually landed
    }

    [Fact]  // 4/5: THE LOST UPDATE. Two clients read v1; A writes; B writes with the stale v1.
    public async Task UpsertChecked_stale_version_conflicts_and_As_value_survives()
    {
        await _repo.UpsertCheckedAsync(Log(3m), expectedVersion: null);

        // Both "clients" read the same version -- this is the entire conflict window.
        var aRead = await VersionAsync();
        var bRead = await VersionAsync();
        Assert.Equal(aRead, bRead);

        await _repo.UpsertCheckedAsync(Log(6m), expectedVersion: aRead);  // A wins

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _repo.UpsertCheckedAsync(Log(2m), expectedVersion: bRead));  // B is stale

        Assert.Equal(bRead, ex.ExpectedVersion);
        Assert.False(ex.Deleted);               // it moved on, it was not deleted
        Assert.Equal(6m, await HoursAsync());   // B did NOT overwrite A -- the whole point
        Assert.Equal(2, await VersionAsync());  // B's rejected write did not bump either
    }

    [Fact]  // 5/5: the case the first spec MISSED. Alice deletes the cell; Bob, holding v3, writes.
    public async Task UpsertChecked_version_supplied_but_row_deleted_conflicts_and_does_not_resurrect()
    {
        // Get the row to v3 so Bob is holding a version that genuinely existed.
        var v1 = await _repo.UpsertCheckedAsync(Log(3m), expectedVersion: null);
        var v2 = await _repo.UpsertCheckedAsync(Log(4m), expectedVersion: v1);
        var v3 = await _repo.UpsertCheckedAsync(Log(5m), expectedVersion: v2);
        Assert.Equal(3, v3);

        await _repo.DeleteCheckedAsync(_userId, _taskId, D, expectedVersion: v3);  // Alice deletes
        Assert.Null(await VersionAsync());

        // Bob still holds v3 and types a number into the cell Alice just removed.
        // A naive `ON CONFLICT ... DO UPDATE ... WHERE row_version = @expected` INSERTS here:
        // no row => no conflict => the WHERE never runs => the row is resurrected at v1 and the
        // write is reported as success, and Alice's delete evaporates with no error and no trace.
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _repo.UpsertCheckedAsync(Log(8m), expectedVersion: v3));

        Assert.True(ex.Deleted);               // and the user is told WHY
        Assert.Equal(v3, ex.ExpectedVersion);
        Assert.Null(await VersionAsync());     // the row stayed deleted -- NOT resurrected at v1
        Assert.Empty(await _repo.GetByUserAndRangeAsync(_userId, D, D));
    }

    // ---- versioned delete ---------------------------------------------------------------------

    [Fact]
    public async Task DeleteChecked_matching_version_removes_the_row()
    {
        var v1 = await _repo.UpsertCheckedAsync(Log(3m), expectedVersion: null);

        await _repo.DeleteCheckedAsync(_userId, _taskId, D, expectedVersion: v1);

        Assert.Null(await VersionAsync());
    }

    [Fact]
    public async Task DeleteChecked_stale_version_conflicts_and_keeps_the_row()
    {
        var v1 = await _repo.UpsertCheckedAsync(Log(3m), expectedVersion: null);
        await _repo.UpsertCheckedAsync(Log(7m), expectedVersion: v1);  // someone else edits it

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _repo.DeleteCheckedAsync(_userId, _taskId, D, expectedVersion: v1));

        Assert.False(ex.Deleted);
        Assert.Equal(7m, await HoursAsync());   // their edit is NOT silently discarded by our delete
    }

    [Fact]
    public async Task DeleteChecked_row_already_gone_conflicts_as_deleted()
    {
        var v1 = await _repo.UpsertCheckedAsync(Log(3m), expectedVersion: null);
        await _repo.DeleteCheckedAsync(_userId, _taskId, D, expectedVersion: v1);

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _repo.DeleteCheckedAsync(_userId, _taskId, D, expectedVersion: v1));

        Assert.True(ex.Deleted);
    }

    // ---- bump-only paths: they move the version but never throw --------------------------------

    [Fact]  // The legacy/system write carries no client version, so it cannot check one -- but it BUMPS.
    public async Task UpsertAsync_bumps_row_version_and_never_conflicts()
    {
        await _repo.UpsertAsync(Log(3m));
        Assert.Equal(1, await VersionAsync());

        await _repo.UpsertAsync(Log(5m));       // would be a conflict for a checked caller; not here
        Assert.Equal(2, await VersionAsync());
        Assert.Equal(5m, await HoursAsync());
    }

    [Fact]  // Smart Fill BUMPS but does not CHECK. If it bumped nothing, a client holding the
            // pre-fill version would still match and would silently overwrite the fill's result.
    public async Task UpsertBatch_bumps_row_version_so_a_stale_client_cannot_overwrite_smart_fill()
    {
        var v1 = await _repo.UpsertCheckedAsync(Log(3m), expectedVersion: null);

        await _repo.UpsertBatchAsync(new[] { Log(6m) });   // Smart Fill overwrites the cell

        Assert.Equal(2, await VersionAsync());             // it bumped...
        Assert.Equal(6m, await HoursAsync());

        // ...so the client still holding the pre-fill version is now correctly rejected.
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _repo.UpsertCheckedAsync(Log(1m), expectedVersion: v1));
        Assert.False(ex.Deleted);
        Assert.Equal(6m, await HoursAsync());              // Smart Fill's result survives
    }

    // ---- helpers ------------------------------------------------------------------------------

    private TimeLog Log(decimal hours) => new(0, _userId, _taskId, D, hours, DateTimeOffset.UtcNow);

    private async Task<long?> VersionAsync()
    {
        using var c = _db.Create();
        return await c.ExecuteScalarAsync<long?>(
            "SELECT row_version FROM TimeLogs WHERE user_id = @u AND task_id = @t AND work_date = @d;",
            new { u = _userId, t = _taskId, d = D.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
    }

    private async Task<decimal?> HoursAsync()
    {
        using var c = _db.Create();
        var h = await c.ExecuteScalarAsync<double?>(
            "SELECT hours FROM TimeLogs WHERE user_id = @u AND task_id = @t AND work_date = @d;",
            new { u = _userId, t = _taskId, d = D.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        return h is null ? null : (decimal)h;
    }
}
