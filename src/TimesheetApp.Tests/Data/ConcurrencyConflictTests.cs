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
// W3.5: the version is now read OFF THE ENTITY (repo.GetByIdAsync(id)).RowVersion — there is no
// GetRowVersionAsync any more. That is not cosmetic. A separate round-trip to fetch the version is a
// second read of the same row, and it can disagree with the first: between the read that gave you the
// data and the read that gave you the version, someone else can write. You would then hold THEIR
// version alongside YOUR data — and the next save would pass the check and silently clobber them.
// Projecting row_version in the same SELECT that returns the row closes that by construction.
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

    // ---- W3.5 MANDATORY: the mechanism is REACHABLE END TO END --------------------------------
    // The whole point of C-1. Before it, a client could not obtain the expectedVersion a checked write
    // requires: Backlog/TaskItem/TimeLog carried no RowVersion at all, and the SELECTs did not project
    // row_version. The API demanded a number the API refused to hand out. This is the round trip:
    //   read the entity -> get a NON-ZERO RowVersion -> pass it to the checked write -> it succeeds and
    //   RETURNS version+1 -> a second write at the now-stale version throws.

    [Fact]
    public async Task Read_entity_then_checked_write_round_trips_the_version_end_to_end()
    {
        var backlogs = new BacklogRepository(_db);
        var tasks = new TaskRepository(_db);
        var id = await _db.SeedRequestAsync("REQ-E2E", "ARCS");
        var taskId = await _db.SeedTaskAsync(id, "Implement");

        // 1. READ. The version rides in on the entity, from the same SELECT that returned the data.
        var backlog = (await backlogs.GetByIdAsync(id))!;
        var task = (await tasks.GetByIdAsync(taskId))!;

        // 2. It is NOT the record's default. A 0 here would mean the SELECT never projected
        //    row_version — every checked write in the app would then conflict on its first attempt.
        Assert.NotEqual(0, backlog.RowVersion);
        Assert.NotEqual(0, task.RowVersion);
        Assert.Equal(1, backlog.RowVersion);   // schema default for a fresh row
        Assert.Equal(1, task.RowVersion);

        // 3. WRITE with it. The checked write returns the NEW version, so the caller never has to
        //    re-read it — the read-back this shape exists to eliminate.
        var nextB = await backlogs.UpdateCheckedAsync(
            backlog with { ProgressPercent = 30 }, backlog.RowVersion, 1, "Ann");
        var nextT = await tasks.UpdateCheckedAsync(task with { TaskName = "Implement v2" }, task.RowVersion);
        Assert.Equal(backlog.RowVersion + 1, nextB);
        Assert.Equal(task.RowVersion + 1, nextT);

        // 4. The returned version is usable IMMEDIATELY as the next expectedVersion — no re-read.
        var afterB = await backlogs.UpdateCheckedAsync(
            backlog with { ProgressPercent = 60 }, nextB, 1, "Ann");
        Assert.Equal(nextB + 1, afterB);

        // 5. And the STALE version — the one we opened with — is now rejected on both.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            backlogs.UpdateCheckedAsync(backlog with { ProgressPercent = 99 }, backlog.RowVersion, 2, "Bob"));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            tasks.UpdateCheckedAsync(task with { TaskName = "clobbered" }, task.RowVersion));

        // Ann's data survived; Bob never landed.
        Assert.Equal(60, (await backlogs.GetByIdAsync(id))!.ProgressPercent);
        Assert.Equal("Implement v2", (await tasks.GetByIdAsync(taskId))!.TaskName);
    }

    // The same round trip on TimeLogs, whose key is the natural (user_id, task_id, work_date) triple
    // rather than an id — so it reads its version back through GetByUserAndRangeAsync, not GetById.
    [Fact]
    public async Task TimeLog_read_then_checked_upsert_round_trips_the_version()
    {
        var logs = new TimeLogRepository(_db);
        var (userId, taskId) = await _db.SeedUserAndTaskAsync();
        var day = new DateOnly(2026, 7, 6);

        // First write into an empty cell: expectedVersion null == "I expect no row here".
        var v1 = await logs.UpsertCheckedAsync(
            new TimeLog(0, userId, taskId, day, 4m, DateTimeOffset.UtcNow), null);
        Assert.Equal(1, v1);

        // READ the cell back — the version must arrive on the entity.
        var cell = (await logs.GetByUserAndRangeAsync(userId, day, day)).Single();
        Assert.Equal(4m, cell.Hours);
        Assert.Equal(v1, cell.RowVersion);      // 0 here => GetByUserAndRangeAsync forgot row_version

        // WRITE with the version we read, and get the next one back.
        var v2 = await logs.UpsertCheckedAsync(cell with { Hours = 6m }, cell.RowVersion);
        Assert.Equal(v1 + 1, v2);

        // The stale version is rejected, and 6h — not 4h — survives.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            logs.UpsertCheckedAsync(cell with { Hours = 8m }, cell.RowVersion));
        Assert.Equal(6m, (await logs.GetByUserAndRangeAsync(userId, day, day)).Single().Hours);
    }

    // C-2: a 409 on a timesheet cell must SAY WHICH CELL. TimeLogs.Id is 0 (natural key), so without
    // `detail` the message was just "TimeLogs was changed by someone else" — naming nothing.
    [Fact]
    public async Task A_timelog_conflict_names_the_cell_it_happened_on()
    {
        var logs = new TimeLogRepository(_db);
        var (userId, taskId) = await _db.SeedUserAndTaskAsync();
        var day = new DateOnly(2026, 7, 7);

        await logs.UpsertCheckedAsync(new TimeLog(0, userId, taskId, day, 4m, DateTimeOffset.UtcNow), null);

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            logs.UpsertCheckedAsync(new TimeLog(0, userId, taskId, day, 8m, DateTimeOffset.UtcNow), 99));

        Assert.Equal(0, conflict.Id);                       // natural key: there is no id to report
        Assert.NotNull(conflict.Detail);
        Assert.Contains($"user {userId}", conflict.Message);
        Assert.Contains($"task {taskId}", conflict.Message);
        Assert.Contains("2026-07-07", conflict.Message);
    }

    // ---- MANDATORY 1: two people edit one backlog; exactly one wins ---------------------------

    [Fact]
    public async Task Two_editors_on_one_backlog_one_wins_and_the_other_is_told()
    {
        var repo = new BacklogRepository(_db);
        var id = await _db.SeedRequestAsync("REQ-CONC", "ARCS");

        // Both clients load the same card and see the same version — which now travels ON the card.
        var seen = (await repo.GetByIdAsync(id))!;
        var v = seen.RowVersion;
        Assert.Equal(1, v);

        // Ann commits her inline edit first, and is handed her next version in the same breath.
        var next = await repo.UpdateCheckedAsync(seen with { ProgressPercent = 40, Type = "Implement" },
            v, changedByUserId: 1, changedByName: "Ann");
        Assert.Equal(2, next);

        // Bob commits from the card he loaded BEFORE Ann's write — he still carries version 1.
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            repo.UpdateCheckedAsync(seen with { ProgressPercent = 95, Type = "Bug" },
                v, changedByUserId: 2, changedByName: "Bob"));

        Assert.Equal("Backlogs", conflict.Table);
        Assert.Equal(id, conflict.Id);
        Assert.Equal(v, conflict.ExpectedVersion);
        Assert.False(conflict.Deleted);            // edited by someone else, not deleted

        // The winner's data is intact — Bob did not silently overwrite it.
        var after = (await repo.GetByIdAsync(id))!;
        Assert.Equal(40, after.ProgressPercent);
        Assert.Equal("Implement", after.Type);

        // Exactly one write landed, so the version advanced exactly once.
        Assert.Equal(2, after.RowVersion);
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
        var v = seen.RowVersion;

        await repo.UpdateCheckedAsync(seen with { ProgressPercent = 10 },
            v, changedByUserId: 1, changedByName: "Ann");
        var auditAfterAnn = (await repo.GetAuditAsync(id)).Count;
        Assert.Equal(1, auditAfterAnn);                       // progress: null -> 10

        // Bob's write carries the stale version and must be rejected...
        var ex = await Record.ExceptionAsync(() => repo.UpdateCheckedAsync(
            seen with { ProgressPercent = 95, Note = "bob-was-here" },
            v, changedByUserId: 2, changedByName: "Bob"));

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
        Assert.Equal(2, after.RowVersion);   // Ann's bump only — the rollback held

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

        var v = (await tasks.GetByIdAsync(taskId))!.RowVersion;

        await tasks.UpdateExtendedCheckedAsync(taskId, "Implement", ann, v, ann, "Ann");
        var auditAfterAnn = (await tasks.GetAuditAsync(taskId)).Count;

        var ex = await Record.ExceptionAsync(() =>
            tasks.UpdateExtendedCheckedAsync(taskId, "Bug", bob, v, bob, "Bob"));

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
    // carries no version from a client — it is a system write. It has NO checked sibling, by design.

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
            tasks.UpdateStatusCheckedAsync(taskId, "Done", 1, 1, "Ann"));
        Assert.Equal(1, conflict.ExpectedVersion);
        Assert.False(conflict.Deleted);
    }

    // Every existing caller (the WPF ViewModels, BacklogContinuationService, DefaultTaskSyncService)
    // uses the bump-only method. Those writes must keep landing, and must still bump.
    [Fact]
    public async Task A_write_that_carries_no_version_never_throws_and_still_bumps()
    {
        var backlogs = new BacklogRepository(_db);
        var tasks = new TaskRepository(_db);
        var id = await _db.SeedRequestAsync("REQ-LEGACY", "ARCS");
        var taskId = await _db.SeedTaskAsync(id, "Implement");

        var seen = (await backlogs.GetByIdAsync(id))!;

        // Deliberately stale-looking: the bump-only method compares nothing. Note `seen` carries
        // RowVersion 1 on it now — and it is still ignored, because the version travels only as an
        // explicit argument, never off the record. RequestsViewModel.SaveEditAsync depends on that:
        // it builds a FRESH Backlog from editor fields, whose RowVersion is the default 0.
        await backlogs.UpdateAsync(seen with { ProgressPercent = 10 }, 1, "Ann");
        await backlogs.UpdateAsync(seen with { ProgressPercent = 20 }, 2, "Bob");   // last write wins
        var after = (await backlogs.GetByIdAsync(id))!;
        Assert.Equal(20, after.ProgressPercent);
        Assert.Equal(3, after.RowVersion);                     // 1 -> 3, both bumped

        await tasks.UpdateStatusAsync(taskId, "Done", 1, "Ann");
        await tasks.UpdateExtendedAsync(taskId, "Bug", null, 2, "Bob");
        Assert.Equal(3, (await tasks.GetByIdAsync(taskId))!.RowVersion);
    }

    // The load-bearing case from C-1.3: a record built from editor fields (NOT from a read) carries
    // RowVersion 0. The bump-only write must not care — if any write path read the record's version,
    // every Backlog edit in the WPF app would 409 on the spot.
    [Fact]
    public async Task A_bump_only_write_ignores_the_records_own_RowVersion()
    {
        var backlogs = new BacklogRepository(_db);
        var id = await _db.SeedRequestAsync("REQ-FRESH", "ARCS");

        // Exactly what RequestsViewModel.SaveEditAsync constructs: a brand-new Backlog from the
        // editor's fields. RowVersion is the default 0 — a value no row in the database ever has.
        var fromEditor = new Backlog(id, "REQ-FRESH", "ARCS", DateTimeOffset.UtcNow, ProgressPercent: 55);
        Assert.Equal(0, fromEditor.RowVersion);

        await backlogs.UpdateAsync(fromEditor, 1, "Ann");       // must NOT throw

        var after = (await backlogs.GetByIdAsync(id))!;
        Assert.Equal(55, after.ProgressPercent);
        Assert.Equal(2, after.RowVersion);                      // bumped from 1, not from 0
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
        var bv = (await backlogs.GetByIdAsync(id))!.RowVersion;
        var tv = (await tasks.GetByIdAsync(taskId))!.RowVersion;

        Assert.Equal(bv + 1, await backlogs.SetTagsCheckedAsync(id, new[] { urgent }, bv, 1, "Ann"));
        Assert.Equal(tv + 1, await tasks.SetTaskTagsCheckedAsync(taskId, new[] { urgent }, tv, 1, "Ann"));

        // Bob ticked his chips against the card as it looked before Ann committed.
        var bc = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            backlogs.SetTagsCheckedAsync(id, new[] { later }, bv, 2, "Bob"));
        Assert.Equal("Backlogs", bc.Table);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            tasks.SetTaskTagsCheckedAsync(taskId, new[] { later }, tv, 2, "Bob"));

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
        var v = (await tasks.GetByIdAsync(taskId))!.RowVersion;

        // Hard-delete underneath the editor (a soft-delete leaves the row present, so it would — and
        // should — report Deleted = false: the row still exists, it just moved on).
        using (var c = _db.Create())
            await c.ExecuteAsync("DELETE FROM Tasks WHERE id = @id;", new { id = taskId });

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            tasks.UpdateStatusCheckedAsync(taskId, "Done", v, 1, "Ann"));

        Assert.True(conflict.Deleted);
        Assert.Contains("no longer exists", conflict.Message);

        // The same distinction on the single-statement path, which has no pre-read to lean on.
        var conflict2 = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            tasks.UpdateCheckedAsync(new TaskItem(taskId, backlogId, "Implement", 0, true), v));
        Assert.True(conflict2.Deleted);

        // A row that still exists but has moved on is a DIFFERENT thing, and says so.
        var live = await _db.SeedTaskAsync(backlogId, "Live");
        await tasks.SetOrderAsync(live, 3);                    // bump-only: version is now 2
        var moved = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            tasks.UpdateStatusCheckedAsync(live, "Done", 1, 1, "Ann"));
        Assert.False(moved.Deleted);
        Assert.Contains("was changed by someone else", moved.Message);
    }
}
