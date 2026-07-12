using Xunit;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Tests.Data;

// P8 (schema v7) data-layer round-trips: Tag/Pca/Holiday CRUD, BacklogTags set/get, the 7 new Backlog
// tracking columns + their audit rows, and the all-time logged-hours roll-up (XC-06: includes hours of
// soft-deleted tasks).
public class TaskListRepositoryTests : IAsyncLifetime
{
    private TestDb _db = null!;
    public async Task InitializeAsync() => _db = await TestDb.CreateAsync();
    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Tag_insert_update_delete_roundtrip_and_links_removed_on_delete()
    {
        var tags = new TagRepository(_db);
        var backlogs = new BacklogRepository(_db);

        var id = await tags.InsertAsync(new Tag(0, "Urgent", "⚡", "#FF0000", DateTimeOffset.UtcNow));
        var loaded = (await tags.GetAllAsync()).Single(t => t.Id == id);
        Assert.Equal("Urgent", loaded.Text);
        Assert.Equal("#FF0000", loaded.Color);

        await tags.UpdateAsync(loaded with { Text = "Hot", Color = "#00FF00" });
        var updated = (await tags.GetAllAsync()).Single(t => t.Id == id);
        Assert.Equal("Hot", updated.Text);
        Assert.Equal("#00FF00", updated.Color);

        // Link the tag to a backlog, then deleting the tag must also remove the BacklogTags link.
        var bid = await backlogs.InsertAsync(new Backlog(0, "REQ-TAG", "ARCS", DateTimeOffset.UtcNow));
        await backlogs.SetTagsAsync(bid, new[] { id });
        Assert.Contains(id, await backlogs.GetTagIdsAsync(bid));

        await tags.DeleteAsync(id);
        Assert.DoesNotContain(await tags.GetAllAsync(), t => t.Id == id);
        Assert.Empty(await backlogs.GetTagIdsAsync(bid));   // link cascaded away
    }

    [Fact]
    public async Task PcaContact_insert_get_softdelete_rename_roundtrip()
    {
        var repo = new PcaContactRepository(_db);
        var id = await repo.InsertAsync(new PcaContact(0, "Acme Corp", true));

        Assert.Equal("Acme Corp", (await repo.GetByIdAsync(id))!.Name);
        Assert.Contains(await repo.GetActiveAsync(), p => p.Id == id);

        await repo.UpdateNameAsync(id, "Acme Ltd");
        Assert.Equal("Acme Ltd", (await repo.GetByIdAsync(id))!.Name);

        await repo.SetActiveAsync(id, false);
        Assert.Contains(await repo.GetAllAsync(), p => p.Id == id && !p.IsActive);  // still in GetAll
        Assert.DoesNotContain(await repo.GetActiveAsync(), p => p.Id == id);         // hidden from active
    }

    // ---- M8.2 optimistic concurrency (no threads needed: the stale version IS the race window) ----

    [Fact]
    public async Task Tag_UpdateAsync_TwoAdmins_SecondWithStaleVersionConflicts_FirstSurvives()
    {
        var tags = new TagRepository(_db);
        var id = await tags.InsertAsync(new Tag(0, "Urgent", "!", "#111111", DateTimeOffset.UtcNow));
        var loaded = (await tags.GetAllAsync()).Single(t => t.Id == id);
        Assert.Equal(1, loaded.RowVersion);

        // Both admins open the tag editor and read v1. Admin A saves first.
        await tags.UpdateAsync(loaded with { Text = "Hot" }, loaded.RowVersion);

        // Admin B, still holding stale v1, saves next -> conflict; A's edit is untouched.
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => tags.UpdateAsync(loaded with { Text = "Cold" }, loaded.RowVersion));
        Assert.Equal("Tags", ex.Table);
        Assert.False(ex.Deleted);

        var current = (await tags.GetAllAsync()).Single(t => t.Id == id);
        Assert.Equal("Hot", current.Text);
        Assert.Equal(2, current.RowVersion);
    }

    [Fact]
    public async Task Tag_UpdateAsync_NoExpectedVersion_IsBumpOnly_NeverThrows()
    {
        var tags = new TagRepository(_db);
        var id = await tags.InsertAsync(new Tag(0, "Urgent", "!", "#111111", DateTimeOffset.UtcNow));
        var loaded = (await tags.GetAllAsync()).Single(t => t.Id == id);

        await tags.UpdateAsync(loaded with { Text = "Hot" });   // existing 1-arg call shape

        var current = (await tags.GetAllAsync()).Single(t => t.Id == id);
        Assert.Equal("Hot", current.Text);
        Assert.Equal(2, current.RowVersion);
    }

    [Fact]
    public async Task Tag_UpdateAsync_RowDeletedByOther_ThrowsWithDeletedTrue()
    {
        var tags = new TagRepository(_db);
        var id = await tags.InsertAsync(new Tag(0, "Urgent", "!", "#111111", DateTimeOffset.UtcNow));
        var loaded = (await tags.GetAllAsync()).Single(t => t.Id == id);

        await tags.DeleteAsync(id);   // someone else hard-deletes it (Tags is hard-delete, per contract)

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => tags.UpdateAsync(loaded with { Text = "Hot" }, loaded.RowVersion));
        Assert.True(ex.Deleted);
        Assert.Equal("Tags", ex.Table);
    }

    [Fact]
    public async Task PcaContact_UpdateNameAsync_TwoAdmins_SecondWithStaleVersionConflicts_FirstSurvives()
    {
        var repo = new PcaContactRepository(_db);
        var id = await repo.InsertAsync(new PcaContact(0, "Acme Corp", true));
        var loaded = await repo.GetByIdAsync(id);
        Assert.Equal(1, loaded!.RowVersion);

        await repo.UpdateNameAsync(id, "Acme Ltd", loaded.RowVersion);   // Admin A saves first

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => repo.UpdateNameAsync(id, "Acme LLC", loaded.RowVersion));   // Admin B, stale v1
        Assert.Equal("PcaContacts", ex.Table);
        Assert.False(ex.Deleted);

        var current = await repo.GetByIdAsync(id);
        Assert.Equal("Acme Ltd", current!.Name);
        Assert.Equal(2, current.RowVersion);
    }

    [Fact]
    public async Task PcaContact_SetActive_IsBumpOnly_NeedsNoVersionAndNeverThrows()
    {
        var repo = new PcaContactRepository(_db);
        var id = await repo.InsertAsync(new PcaContact(0, "Acme Corp", true));
        Assert.Equal(1, (await repo.GetByIdAsync(id))!.RowVersion);

        await repo.SetActiveAsync(id, false);   // deactivation carries no version at all

        var after = await repo.GetByIdAsync(id);
        Assert.False(after!.IsActive);
        Assert.Equal(2, after.RowVersion);       // bumped even though nothing was checked
    }

    [Fact]
    public async Task Holiday_upsert_get_for_month_and_delete_roundtrip()
    {
        var repo = new HolidayRepository(_db);
        var d = new DateOnly(2026, 7, 1);

        await repo.UpsertAsync(d, "National Day");
        var month = await repo.GetForMonthAsync(2026, 7);
        Assert.Single(month);
        Assert.Equal("National Day", month[0].Description);
        Assert.Equal(d, month[0].Date);

        // A holiday in another month is excluded from the month query but present in GetAll.
        await repo.UpsertAsync(new DateOnly(2026, 8, 15), null);
        Assert.Single(await repo.GetForMonthAsync(2026, 7));
        Assert.Equal(2, (await repo.GetAllAsync()).Count);

        // Upsert overwrites the description; delete removes the row.
        await repo.UpsertAsync(d, "Renamed");
        Assert.Equal("Renamed", (await repo.GetForMonthAsync(2026, 7))[0].Description);
        await repo.DeleteAsync(d);
        Assert.Empty(await repo.GetForMonthAsync(2026, 7));
    }

    [Fact]
    public async Task Backlog_v7_columns_roundtrip_and_changes_are_audited()
    {
        var repo = new BacklogRepository(_db);
        var id = await repo.InsertAsync(new Backlog(
            0, "REQ-V7", "ARMS", DateTimeOffset.UtcNow,
            DeadlineInternal: new DateOnly(2026, 7, 10),
            DeadlineExternal: new DateOnly(2026, 7, 20),
            RoughEstimateHours: 12.5m, OfficialEstimateHours: 16m,
            ProgressPercent: 40, Note: "first cut", PcaContactId: null));

        var loaded = await repo.GetByIdAsync(id);
        Assert.Equal(new DateOnly(2026, 7, 10), loaded!.DeadlineInternal);
        Assert.Equal(new DateOnly(2026, 7, 20), loaded.DeadlineExternal);
        Assert.Equal(12.5m, loaded.RoughEstimateHours);
        Assert.Equal(16m, loaded.OfficialEstimateHours);
        Assert.Equal(40, loaded.ProgressPercent);
        Assert.Equal("first cut", loaded.Note);
        Assert.Null(loaded.PcaContactId);

        // Change progress + note + official estimate (deadlines unchanged) -> exactly 3 audit rows.
        await repo.UpdateAsync(loaded with
        {
            ProgressPercent = 75,
            Note = "second cut",
            OfficialEstimateHours = 20m,
        }, changedByUserId: 9, changedByName: "Lan");

        var audit = await repo.GetAuditAsync(id);
        Assert.Contains(audit, a => a.Field == "progress_percent" && a.OldValue == "40" && a.NewValue == "75");
        Assert.Contains(audit, a => a.Field == "note" && a.OldValue == "first cut" && a.NewValue == "second cut");
        Assert.Contains(audit, a => a.Field == "official_estimate_hours");
        Assert.DoesNotContain(audit, a => a.Field == "deadline_internal");
        Assert.All(audit, a => Assert.Equal("Lan", a.ChangedByName));
    }

    [Fact]
    public async Task Backlog_pca_contact_change_audited_by_name()
    {
        var pca = new PcaContactRepository(_db);
        var repo = new BacklogRepository(_db);
        var contactId = await pca.InsertAsync(new PcaContact(0, "Globex", true));
        var id = await repo.InsertAsync(new Backlog(0, "REQ-PCA", "ARCS", DateTimeOffset.UtcNow));

        await repo.UpdateAsync((await repo.GetByIdAsync(id))! with { PcaContactId = contactId },
            changedByUserId: 1, changedByName: "Admin");

        var audit = await repo.GetAuditAsync(id);
        Assert.Contains(audit, a => a.Field == "pca_contact" && a.OldValue == null && a.NewValue == "Globex");
    }

    [Fact]
    public async Task SetTagsAsync_replaces_the_whole_set()
    {
        var tags = new TagRepository(_db);
        var repo = new BacklogRepository(_db);
        var t1 = await tags.InsertAsync(new Tag(0, "A", "a", "#111111", DateTimeOffset.UtcNow));
        var t2 = await tags.InsertAsync(new Tag(0, "B", "b", "#222222", DateTimeOffset.UtcNow));
        var t3 = await tags.InsertAsync(new Tag(0, "C", "c", "#333333", DateTimeOffset.UtcNow));
        var bid = await repo.InsertAsync(new Backlog(0, "REQ-SET", "ARCS", DateTimeOffset.UtcNow));

        await repo.SetTagsAsync(bid, new[] { t1, t2 });
        Assert.Equal(new[] { t1, t2 }, (await repo.GetTagIdsAsync(bid)).OrderBy(x => x).ToArray());

        // Replace-all: {t1,t2} -> {t3}.
        await repo.SetTagsAsync(bid, new[] { t3 });
        Assert.Equal(new[] { t3 }, await repo.GetTagIdsAsync(bid));

        var all = await repo.GetTagIdsForAllAsync();
        Assert.Equal(new[] { t3 }, all[bid].ToArray());
    }

    [Fact]
    public async Task GetLoggedHoursByBacklogAsync_sums_alltime_including_softdeleted_tasks()  // XC-06 / A1
    {
        var logs = new TimeLogRepository(_db);
        var (userId, taskId) = await _db.SeedUserAndTaskAsync();

        // backlog_id of the seeded task: it was created under SeedRequestAsync's backlog.
        var backlogId = await GetBacklogIdForTaskAsync(taskId);

        await logs.UpsertAsync(new TimeLog(0, userId, taskId, new DateOnly(2026, 6, 16), 3m, DateTimeOffset.UtcNow));
        await logs.UpsertAsync(new TimeLog(0, userId, taskId, new DateOnly(2026, 6, 17), 5m, DateTimeOffset.UtcNow));

        // Soft-delete the task: its hours must STILL roll up (no is_active filter on the join).
        await _db.SetTaskActiveAsync(taskId, false);

        var byBacklog = await logs.GetLoggedHoursByBacklogAsync();
        Assert.Equal(8m, byBacklog[backlogId]);
    }

    [Fact]
    public async Task UpdateExtendedAsync_audits_one_row_per_changed_field()
    {
        var tasks = new TaskRepository(_db);
        var bid = await _db.SeedRequestAsync("REQ-EXT", "ARCS");
        var taskId = await _db.SeedTaskAsync(bid, "Build");
        var userId = await _db.SeedUserAsync("Mai");

        // type NULL -> "Bug" and assignee NULL -> Mai: exactly two audit rows (type + assignee-by-name).
        await tasks.UpdateExtendedAsync(taskId, "Bug", userId,
            changedByUserId: 3, changedByName: "Ed");

        var loaded = await tasks.GetByIdAsync(taskId);
        Assert.Equal("Bug", loaded!.Type);
        Assert.Equal(userId, loaded.AssigneeUserId);

        var audit = await tasks.GetAuditAsync(taskId);
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, a => a.Field == "type" && a.OldValue == null && a.NewValue == "Bug");
        Assert.Contains(audit, a => a.Field == "assignee" && a.OldValue == null && a.NewValue == "Mai");
        Assert.All(audit, a => Assert.Equal("Ed", a.ChangedByName));
    }

    [Fact]
    public async Task UpdateExtendedAsync_writes_no_audit_when_unchanged()
    {
        var tasks = new TaskRepository(_db);
        var bid = await _db.SeedRequestAsync("REQ-NOOP", "ARCS");
        var taskId = await _db.SeedTaskAsync(bid, "Build");

        // First set a value, then re-apply the SAME value -> the second call must add no audit rows.
        await tasks.UpdateExtendedAsync(taskId, "Task", null, changedByUserId: 1, changedByName: "A");
        var afterFirst = (await tasks.GetAuditAsync(taskId)).Count;

        await tasks.UpdateExtendedAsync(taskId, "Task", null, changedByUserId: 1, changedByName: "A");
        Assert.Equal(afterFirst, (await tasks.GetAuditAsync(taskId)).Count);
    }

    [Fact]
    public async Task SetTaskTagsAsync_replaces_links_and_audits_once_on_change()
    {
        var tags = new TagRepository(_db);
        var tasks = new TaskRepository(_db);
        var t1 = await tags.InsertAsync(new Tag(0, "A", "a", "#111111", DateTimeOffset.UtcNow));
        var t2 = await tags.InsertAsync(new Tag(0, "B", "b", "#222222", DateTimeOffset.UtcNow));
        var t3 = await tags.InsertAsync(new Tag(0, "C", "c", "#333333", DateTimeOffset.UtcNow));
        var bid = await _db.SeedRequestAsync("REQ-TTAG", "ARCS");
        var taskId = await _db.SeedTaskAsync(bid, "Build");

        await tasks.SetTaskTagsAsync(taskId, new[] { t1, t2 }, changedByUserId: 1, changedByName: "A");
        Assert.Equal(new[] { t1, t2 }, (await tasks.GetTagIdsAsync(taskId)).OrderBy(x => x).ToArray());

        // Replace-all {t1,t2} -> {t3}: links replaced, plus exactly one more 'tags' audit row.
        await tasks.SetTaskTagsAsync(taskId, new[] { t3 }, changedByUserId: 1, changedByName: "A");
        Assert.Equal(new[] { t3 }, await tasks.GetTagIdsAsync(taskId));

        var tagAudit = (await tasks.GetAuditAsync(taskId)).Where(a => a.Field == "tags").ToList();
        Assert.Equal(2, tagAudit.Count);   // one per change (NULL->{t1,t2}, {t1,t2}->{t3})

        // Re-applying the same set writes no further 'tags' audit row.
        await tasks.SetTaskTagsAsync(taskId, new[] { t3 }, changedByUserId: 1, changedByName: "A");
        Assert.Equal(2, (await tasks.GetAuditAsync(taskId)).Count(a => a.Field == "tags"));
    }

    [Fact]
    public async Task UpdateStatusAsync_audits_status_change_and_skips_when_unchanged()
    {
        var tasks = new TaskRepository(_db);
        var bid = await _db.SeedRequestAsync("REQ-ST", "ARCS");
        var taskId = await _db.SeedTaskAsync(bid, "Build");   // seeded status defaults to 'Todo'

        await tasks.UpdateStatusAsync(taskId, "Done", changedByUserId: 1, changedByName: "A");
        var statusAudit = (await tasks.GetAuditAsync(taskId)).Where(a => a.Field == "status").ToList();
        Assert.Single(statusAudit);
        Assert.Equal("Done", statusAudit[0].NewValue);

        // Re-applying the same status writes no further audit row.
        await tasks.UpdateStatusAsync(taskId, "Done", changedByUserId: 1, changedByName: "A");
        Assert.Single(await tasks.GetAuditAsync(taskId), a => a.Field == "status");
    }

    [Fact]
    public async Task Tag_delete_removes_task_tag_links()   // v9 B-5: cascade must also clear TaskTags
    {
        var tags = new TagRepository(_db);
        var tasks = new TaskRepository(_db);
        var tagId = await tags.InsertAsync(new Tag(0, "X", "x", "#abcdef", DateTimeOffset.UtcNow));
        var bid = await _db.SeedRequestAsync("REQ-TTDEL", "ARCS");
        var taskId = await _db.SeedTaskAsync(bid, "Build");

        await tasks.SetTaskTagsAsync(taskId, new[] { tagId });
        Assert.Contains(tagId, await tasks.GetTagIdsAsync(taskId));

        await tags.DeleteAsync(tagId);
        Assert.Empty(await tasks.GetTagIdsAsync(taskId));   // TaskTags link cascaded away (not just BacklogTags)
    }

    private async Task<int> GetBacklogIdForTaskAsync(int taskId)
    {
        using var c = _db.Create();
        return await Dapper.SqlMapper.QuerySingleAsync<int>(
            c, "SELECT backlog_id FROM Tasks WHERE id = @id;", new { id = taskId });
    }
}
