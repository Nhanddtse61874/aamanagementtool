using Xunit;
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

    private async Task<int> GetBacklogIdForTaskAsync(int taskId)
    {
        using var c = _db.Create();
        return await Dapper.SqlMapper.QuerySingleAsync<int>(
            c, "SELECT backlog_id FROM Tasks WHERE id = @id;", new { id = taskId });
    }
}
