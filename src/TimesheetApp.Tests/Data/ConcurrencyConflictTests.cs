using Dapper;
using Xunit;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Tests.Data;

// M8.2 wave 3 (B): optimistic concurrency on the two highest-risk tables — Backlogs and Tasks.
// The Task List lets several people edit one card inline (PCT, Type, PCA, both deadlines, progress,
// tags) and every one of those was a bare UPDATE, so they overwrote each other in silence.
//
// These tests need NO THREADS. The conflict window IS the stale version number, so it reproduces
// sequentially: two "clients" read v=1, one applies, the other applies carrying the now-stale 1.
//
// The fixture is TestDb — a temp FILE database. Not :memory:, which hands every connection its own
// private database, so a conflict could never occur and the tests would be green while asserting
// nothing at all.
public class ConcurrencyConflictTests : IAsyncLifetime
{
    private TestDb _db = null!;
    public async Task InitializeAsync() => _db = await TestDb.CreateAsync();
    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    // Seeded with raw SQL rather than TagRepository so this file depends on no repository another
    // agent owns.
    private async Task<int> SeedTagAsync(string text)
    {
        using var c = _db.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Tags(text, icon, color, created_at) VALUES(@t, '#', '#FF0000', @now);
              SELECT last_insert_rowid();",
            new { t = text, now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") });
    }

    private async Task<long> RowVersionAsync(string table, int id)
    {
        using var c = _db.Create();
        return await c.ExecuteScalarAsync<long>(
            $"SELECT row_version FROM {table} WHERE id = @id;", new { id });
    }

    // ---- MANDATORY 1: two people edit one backlog; exactly one wins ---------------------------

    [Fact]
    public async Task Two_editors_on_one_backlog_one_wins_and_the_other_is_told()
    {
        var repo = new BacklogRepository(_db);
        var id = await _db.SeedRequestAsync("REQ-CONC", "ARCS");

        // Both clients load the same card and see the same version.
        var seen = (await repo.GetByIdAsync(id))!;
        var v = await repo.GetRowVersionAsync(id);
        Assert.Equal(1, v);

        // Ann commits her inline edit first.
        await repo.UpdateAsync(seen with { ProgressPercent = 40, Type = "Implement" },
            changedByUserId: 1, changedByName: "Ann", expectedVersion: v);

        // Bob commits from the card he loaded BEFORE Ann's write — he still carries version 1.
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            repo.UpdateAsync(seen with { ProgressPercent = 95, Type = "Bug" },
                changedByUserId: 2, changedByName: "Bob", expectedVersion: v));

        Assert.Equal("Backlogs", conflict.Table);
        Assert.Equal(id, conflict.Id);
        Assert.Equal(v, conflict.ExpectedVersion);
        Assert.False(conflict.Deleted);            // edited by someone else, not deleted

        // The winner's data is intact — Bob did not silently overwrite it.
        var after = (await repo.GetByIdAsync(id))!;
        Assert.Equal(40, after.ProgressPercent);
        Assert.Equal("Implement", after.Type);

        // Exactly one write landed, so the version advanced exactly once.
        Assert.Equal(2, await repo.GetRowVersionAsync(id));
    }

    // ---- MANDATORY 2: a rejected write leaves NO trace in history -----------------------------
    // This is the test that forced BacklogRepository.UpdateAsync into a transaction. The method does
    // read -> UPDATE -> N audit INSERTs. Bolt a version check onto the UPDATE's WHERE clause without
    // wrapping the whole thing and the UPDATE quietly matches zero rows while the audit INSERTs run
    // anyway (the existing code discards ExecuteAsync's row count) — leaving history that describes a
    // change which never happened.

    [Fact]
    public async Task A_rejected_backlog_edit_writes_no_audit_row()
    {
        var repo = new BacklogRepository(_db);
        var id = await _db.SeedRequestAsync("REQ-AUDIT", "ARCS");

        var seen = (await repo.GetByIdAsync(id))!;
        var v = await repo.GetRowVersionAsync(id);

        await repo.UpdateAsync(seen with { ProgressPercent = 10 },
            changedByUserId: 1, changedByName: "Ann", expectedVersion: v);
        var auditAfterAnn = (await repo.GetAuditAsync(id)).Count;
        Assert.Equal(1, auditAfterAnn);                       // progress: null -> 10

        // Bob's write carries the stale version and must be rejected...
        var ex = await Record.ExceptionAsync(() => repo.UpdateAsync(
            seen with { ProgressPercent = 95, Note = "bob-was-here" },
            changedByUserId: 2, changedByName: "Bob", expectedVersion: v));

        // ...and must leave nothing behind. Assert the SIDE EFFECT before the exception type, so a
        // regression that writes phantom history fails here and names the real problem.
        var audit = await repo.GetAuditAsync(id);
        Assert.Equal(auditAfterAnn, audit.Count);
        Assert.DoesNotContain(audit, a => a.NewValue == "bob-was-here");
        Assert.DoesNotContain(audit, a => a.ChangedByName == "Bob");

        // The row itself never moved.
        var after = (await repo.GetByIdAsync(id))!;
        Assert.Equal(10, after.ProgressPercent);
        Assert.Null(after.Note);
        Assert.Equal(2, await repo.GetRowVersionAsync(id));   // Ann's bump only — the rollback held

        Assert.IsType<ConcurrencyConflictException>(ex);
    }

    // A task inline edit has the same read -> UPDATE -> audit shape, and needed the same transaction.
    [Fact]
    public async Task A_rejected_task_edit_writes_no_audit_row()
    {
        var tasks = new TaskRepository(_db);
        var backlogId = await _db.SeedRequestAsync("REQ-TAUD", "ARCS");
        var taskId = await _db.SeedTaskAsync(backlogId, "Implement");
        var ann = await _db.SeedUserAsync("Ann");
        var bob = await _db.SeedUserAsync("Bob");

        var v = await tasks.GetRowVersionAsync(taskId);

        await tasks.UpdateExtendedAsync(taskId, "Implement", ann, ann, "Ann", expectedVersion: v);
        var auditAfterAnn = (await tasks.GetAuditAsync(taskId)).Count;

        var ex = await Record.ExceptionAsync(() =>
            tasks.UpdateExtendedAsync(taskId, "Bug", bob, bob, "Bob", expectedVersion: v));

        var audit = await tasks.GetAuditAsync(taskId);
        Assert.Equal(auditAfterAnn, audit.Count);
        Assert.DoesNotContain(audit, a => a.ChangedByName == "Bob");

        var after = (await tasks.GetByIdAsync(taskId))!;
        Assert.Equal("Implement", after.Type);                // Ann's value survived
        Assert.Equal(ann, after.AssigneeUserId);

        Assert.IsType<ConcurrencyConflictException>(ex);
    }

    // ---- MANDATORY 3: a drag-and-drop reorder must not 409-storm ------------------------------
    // SetOrderAsync is called ONCE PER ROW (TimesheetViewModel.ReorderAsync loops it over the list).
    // If it were check-and-bump, the first row would bump the version and every row after it would
    // arrive stale, so an ordinary drag would blow up halfway through. It is bump-only: a reorder
    // carries no version from a client — it is a system write.

    [Fact]
    public async Task Reordering_a_whole_list_does_not_409_storm()
    {
        var tasks = new TaskRepository(_db);
        var backlogId = await _db.SeedRequestAsync("REQ-ORDER", "ARCS");

        var ids = new List<int>();
        for (var i = 0; i < 5; i++)
            ids.Add(await _db.SeedTaskAsync(backlogId, $"Task {i}", orderIndex: i));

        // Someone edits one of the rows while the drag is in flight, so its version has already moved
        // on. A reorder must not care: it is not carrying a version to compare against.
        await tasks.UpdateStatusAsync(ids[2], "Done", changedByUserId: 1, changedByName: "Ann");

        // Drag the last task to the top: rewrite EVERY row's order_index, one call each — exactly
        // what the ViewModel does.
        var dragged = new List<int> { ids[4], ids[0], ids[1], ids[2], ids[3] };
        for (var i = 0; i < dragged.Count; i++)
            await tasks.SetOrderAsync(dragged[i], i);          // must not throw, not once

        var ordered = (await tasks.GetActiveByBacklogAsync(backlogId)).Select(t => t.Id).ToList();
        Assert.Equal(dragged, ordered);                        // the whole reorder landed
    }

    // ---- The classification itself: bump-only writes bump, and never throw --------------------

    [Fact]
    public async Task Reorder_and_soft_delete_bump_the_version_without_ever_checking_it()
    {
        var tasks = new TaskRepository(_db);
        var backlogId = await _db.SeedRequestAsync("REQ-BUMP", "ARCS");
        var taskId = await _db.SeedTaskAsync(backlogId, "Implement");

        Assert.Equal(1, await RowVersionAsync("Tasks", taskId));

        // Bump-only writes take no expectedVersion at all, so they cannot conflict...
        await tasks.SetOrderAsync(taskId, 7);
        Assert.Equal(2, await RowVersionAsync("Tasks", taskId));

        await tasks.SetActiveAsync(taskId, false);
        Assert.Equal(3, await RowVersionAsync("Tasks", taskId));

        // ...but they DO bump, which is the whole point: a client still holding version 1 is now told
        // its read is stale instead of silently clobbering the reorder and the soft-delete.
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            tasks.UpdateStatusAsync(taskId, "Done", 1, "Ann", expectedVersion: 1));
        Assert.Equal(1, conflict.ExpectedVersion);
        Assert.False(conflict.Deleted);
    }

    // Every existing caller (the WPF ViewModels, BacklogContinuationService, DefaultTaskSyncService)
    // passes no version. Those writes must keep landing, and must still bump.
    [Fact]
    public async Task A_write_that_carries_no_version_never_throws_and_still_bumps()
    {
        var backlogs = new BacklogRepository(_db);
        var tasks = new TaskRepository(_db);
        var id = await _db.SeedRequestAsync("REQ-LEGACY", "ARCS");
        var taskId = await _db.SeedTaskAsync(id, "Implement");

        var seen = (await backlogs.GetByIdAsync(id))!;

        // Deliberately stale-looking: no expectedVersion is supplied, so nothing is compared.
        await backlogs.UpdateAsync(seen with { ProgressPercent = 10 }, 1, "Ann");
        await backlogs.UpdateAsync(seen with { ProgressPercent = 20 }, 2, "Bob");   // last write wins
        Assert.Equal(20, (await backlogs.GetByIdAsync(id))!.ProgressPercent);
        Assert.Equal(3, await backlogs.GetRowVersionAsync(id));                     // 1 -> 3, both bumped

        await tasks.UpdateStatusAsync(taskId, "Done", 1, "Ann");
        await tasks.UpdateExtendedAsync(taskId, "Bug", null, 2, "Bob");
        Assert.Equal(3, await tasks.GetRowVersionAsync(taskId));
    }

    // ---- Tags: the chips are part of the card, so they ride the parent row's version -----------

    [Fact]
    public async Task Tag_replace_all_is_checked_against_the_parent_row_version()
    {
        var backlogs = new BacklogRepository(_db);
        var tasks = new TaskRepository(_db);
        var id = await _db.SeedRequestAsync("REQ-TAGS", "ARCS");
        var taskId = await _db.SeedTaskAsync(id, "Implement");
        var urgent = await SeedTagAsync("Urgent");
        var later = await SeedTagAsync("Later");

        // BacklogTags / TaskTags have no row_version of their own — the version is the parent's.
        var bv = await backlogs.GetRowVersionAsync(id);
        var tv = await tasks.GetRowVersionAsync(taskId);

        await backlogs.SetTagsAsync(id, new[] { urgent }, 1, "Ann", expectedVersion: bv);
        await tasks.SetTaskTagsAsync(taskId, new[] { urgent }, 1, "Ann", expectedVersion: tv);

        // Bob ticked his chips against the card as it looked before Ann committed.
        var bc = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            backlogs.SetTagsAsync(id, new[] { later }, 2, "Bob", expectedVersion: bv));
        Assert.Equal("Backlogs", bc.Table);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            tasks.SetTaskTagsAsync(taskId, new[] { later }, 2, "Bob", expectedVersion: tv));

        // Ann's tags survived, and Bob's rejected replace-all left no links and no audit row behind.
        Assert.Equal(new[] { urgent }, await backlogs.GetTagIdsAsync(id));
        Assert.Equal(new[] { urgent }, await tasks.GetTagIdsAsync(taskId));
        Assert.DoesNotContain(await backlogs.GetAuditAsync(id), a => a.ChangedByName == "Bob");
        Assert.DoesNotContain(await tasks.GetAuditAsync(taskId), a => a.ChangedByName == "Bob");
    }

    // ---- Deleted is not decoration ------------------------------------------------------------

    [Fact]
    public async Task A_conflict_on_a_row_someone_else_deleted_reports_it_as_deleted()
    {
        var tasks = new TaskRepository(_db);
        var backlogId = await _db.SeedRequestAsync("REQ-GONE", "ARCS");
        var taskId = await _db.SeedTaskAsync(backlogId, "Implement");
        var v = await tasks.GetRowVersionAsync(taskId);

        // Hard-delete underneath the editor (a soft-delete leaves the row present, so it would — and
        // should — report Deleted = false: the row still exists, it just moved on).
        using (var c = _db.Create())
            await c.ExecuteAsync("DELETE FROM Tasks WHERE id = @id;", new { id = taskId });

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            tasks.UpdateStatusAsync(taskId, "Done", 1, "Ann", expectedVersion: v));

        Assert.True(conflict.Deleted);
        Assert.Contains("no longer exists", conflict.Message);

        // The same distinction on the single-statement path, which has no pre-read to lean on.
        var conflict2 = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            tasks.UpdateAsync(new TaskItem(taskId, backlogId, "Implement", 0, true), expectedVersion: v));
        Assert.True(conflict2.Deleted);

        // A row that still exists but has moved on is a DIFFERENT thing, and says so.
        var live = await _db.SeedTaskAsync(backlogId, "Live");
        await tasks.SetOrderAsync(live, 3);                    // bump-only: version is now 2
        var moved = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            tasks.UpdateStatusAsync(live, "Done", 1, "Ann", expectedVersion: 1));
        Assert.False(moved.Deleted);
        Assert.Contains("was changed by someone else", moved.Message);
    }
}
