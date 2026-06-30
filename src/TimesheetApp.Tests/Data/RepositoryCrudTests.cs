using Dapper;
using Xunit;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Tests.Data;

public class RepositoryCrudTests : IAsyncLifetime
{
    private TestDb _db = null!;
    public async Task InitializeAsync() => _db = await TestDb.CreateAsync();
    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task User_insert_get_softdelete_windowsusername_roundtrip()
    {
        var repo = new UserRepository(_db);
        var id = await repo.InsertAsync(new User(0, "Alice", null, true));

        await repo.SetWindowsUsernameAsync(id, "DOMAIN\\alice");
        Assert.Equal(id, (await repo.GetByWindowsUsernameAsync("DOMAIN\\alice"))!.Id);

        await repo.SetActiveAsync(id, false);
        var all = await repo.GetAllAsync();
        var active = await repo.GetActiveAsync();
        Assert.Contains(all, u => u.Id == id && !u.IsActive);   // still present in GetAll
        Assert.DoesNotContain(active, u => u.Id == id);          // hidden from active
    }

    [Fact]
    public async Task Request_search_matches_code_or_project_and_null_returns_all()
    {
        var repo = new BacklogRepository(_db);
        await repo.InsertAsync(new Backlog(0, "REQ-100", "Apollo", DateTimeOffset.UtcNow));
        await repo.InsertAsync(new Backlog(0, "REQ-200", "Gemini", DateTimeOffset.UtcNow));

        Assert.Single(await repo.SearchAsync("Apollo"));   // by project
        Assert.Single(await repo.SearchAsync("REQ-200"));  // by code
        // null => all (plus the seeded DEFAULT request from initializer)
        Assert.True((await repo.SearchAsync(null)).Count >= 2);
    }

    [Fact] // v2: start/end/month/status round-trip + change history records who changed what.
    public async Task Request_v2_fields_roundtrip_and_changes_are_audited()
    {
        var repo = new BacklogRepository(_db);
        var id = await repo.InsertAsync(new Backlog(
            0, "REQ-V2", "Mercury", DateTimeOffset.UtcNow,
            StartDate: new DateOnly(2026, 6, 1), EndDate: new DateOnly(2026, 6, 30),
            PeriodMonth: "2026-06", Type: "Estimate"));

        var loaded = await repo.GetByIdAsync(id);
        Assert.Equal(new DateOnly(2026, 6, 1), loaded!.StartDate);
        Assert.Equal("2026-06", loaded.PeriodMonth);
        Assert.Equal("Estimate", loaded.Type);

        // Change status + start_date + period_month (end_date unchanged) as user 7 "Nhan".
        await repo.UpdateAsync(loaded with
        {
            Type = "Implement",
            StartDate = new DateOnly(2026, 6, 2),
            PeriodMonth = "2026-07",
        }, changedByUserId: 7, changedByName: "Nhan");

        var audit = await repo.GetAuditAsync(id);
        Assert.Equal(3, audit.Count); // status, start_date, period_month — NOT end_date
        Assert.All(audit, a => Assert.Equal("Nhan", a.ChangedByName));
        Assert.All(audit, a => Assert.Equal(7, a.ChangedByUserId));
        Assert.Contains(audit, a => a.Field == "type" && a.OldValue == "Estimate" && a.NewValue == "Implement");
        Assert.Contains(audit, a => a.Field == "period_month" && a.OldValue == "2026-06" && a.NewValue == "2026-07");
        Assert.DoesNotContain(audit, a => a.Field == "end_date");
    }

    [Fact]
    public async Task Task_softdelete_hides_from_active_and_GetByName_finds_match()
    {
        var requests = new BacklogRepository(_db);
        var tasks = new TaskRepository(_db);
        var rid = await requests.InsertAsync(new Backlog(0, "REQ-300", "Zeus", DateTimeOffset.UtcNow));
        var tid = await tasks.InsertAsync(new TaskItem(0, rid, "Design", 0, true));

        Assert.Equal(tid, (await tasks.GetByNameInBacklogAsync(rid, "Design"))!.Id);

        await tasks.SetActiveAsync(tid, false);
        Assert.DoesNotContain(await tasks.GetActiveByBacklogAsync(rid), t => t.Id == tid);
    }

    [Fact]
    public async Task Settings_set_then_get_is_insert_or_replace()
    {
        var repo = new SettingsRepository(_db);
        await repo.SetAsync("n_days", "3");
        Assert.Equal("3", await repo.GetAsync("n_days"));
        await repo.SetAsync("n_days", "5");                 // replace, not duplicate
        Assert.Equal("5", await repo.GetAsync("n_days"));
        Assert.Null(await repo.GetAsync("missing_key"));
    }

    // v9 (B2): auditNote is stored in BacklogAudit.note for deadline fields, null for all others.
    [Fact]
    public async Task UpdateAsync_auditNote_written_only_for_deadline_fields()
    {
        var repo = new BacklogRepository(_db);
        var id = await repo.InsertAsync(new Backlog(
            0, "REQ-AUDIT-NOTE", "NoteTest", DateTimeOffset.UtcNow,
            DeadlineInternal: new DateOnly(2026, 7, 1),
            Type: "Estimate"));

        var loaded = await repo.GetByIdAsync(id);

        // Change both a deadline field (deadline_internal) and a non-deadline field (type)
        // in the same UpdateAsync call, passing an auditNote.
        await repo.UpdateAsync(loaded! with
        {
            DeadlineInternal = new DateOnly(2026, 8, 1),
            Type = "Implement",
        }, changedByUserId: 1, changedByName: "Tester", auditNote: "Pushed due to scope change");

        // Read raw BacklogAudit rows to check the note column directly.
        using var c = _db.Create();
        var auditRows = (await c.QueryAsync<(string field, string? note)>(
            "SELECT field, note FROM BacklogAudit WHERE backlog_id = @bid ORDER BY id;",
            new { bid = id })).ToList();

        // deadline_internal changed → note must be "Pushed due to scope change"
        var deadlineRow = auditRows.Single(r => r.field == "deadline_internal");
        Assert.Equal("Pushed due to scope change", deadlineRow.note);

        // type changed → note must be NULL (non-deadline field)
        var typeRow = auditRows.Single(r => r.field == "type");
        Assert.Null(typeRow.note);

        // v9 (B-6): the note must also round-trip through GetAuditAsync (BacklogAuditEntry.Note),
        // so the editor's change-history panel can surface the deadline-change reason.
        var entries = await repo.GetAuditAsync(id);
        Assert.Equal("Pushed due to scope change", entries.Single(e => e.Field == "deadline_internal").Note);
        Assert.Null(entries.Single(e => e.Field == "type").Note);
    }

    // v9 (B2): when only a non-deadline field changes and auditNote is supplied, note stays null.
    [Fact]
    public async Task UpdateAsync_auditNote_is_null_when_no_deadline_changed()
    {
        var repo = new BacklogRepository(_db);
        var id = await repo.InsertAsync(new Backlog(
            0, "REQ-AUDIT-NODEADLINE", "NoteNullTest", DateTimeOffset.UtcNow,
            Type: "Estimate"));

        var loaded = await repo.GetByIdAsync(id);

        // Change only a non-deadline field; supply a note anyway — must NOT appear in audit.
        await repo.UpdateAsync(loaded! with { Type = "Implement" },
            changedByUserId: 1, changedByName: "Tester", auditNote: "should not appear");

        using var c = _db.Create();
        var auditRows = (await c.QueryAsync<(string field, string? note)>(
            "SELECT field, note FROM BacklogAudit WHERE backlog_id = @bid ORDER BY id;",
            new { bid = id })).ToList();

        // Only the type row should exist; its note must be null.
        var typeRow = auditRows.Single(r => r.field == "type");
        Assert.Null(typeRow.note);
    }

    // v9 (B1): SetTagsAsync writes exactly one 'tags' audit row when the tag set changes.
    [Fact]
    public async Task SetTagsAsync_writes_one_tags_audit_row_on_change()
    {
        var repo = new BacklogRepository(_db);
        var bid = await repo.InsertAsync(new Backlog(0, "REQ-TAGS-AUDIT", "TagAuditTest", DateTimeOffset.UtcNow));

        // Seed two tags directly (mirrors RetentionServiceTests pattern).
        using var c = _db.Create();
        var tag1 = await c.ExecuteScalarAsync<int>(
            "INSERT INTO Tags(text, icon, color, created_at) VALUES('Alpha','🔥','#FF0000','2026-01-01T00:00:00Z'); SELECT last_insert_rowid();");
        var tag2 = await c.ExecuteScalarAsync<int>(
            "INSERT INTO Tags(text, icon, color, created_at) VALUES('Beta','⚡','#FFAA00','2026-01-01T00:00:00Z'); SELECT last_insert_rowid();");

        // Set initial tags (tag1 only) — changedBy supplied so an audit row is written.
        await repo.SetTagsAsync(bid, new[] { tag1 }, changedByUserId: 5, changedByName: "Editor");

        var auditAfterFirst = (await c.QueryAsync<(string field, string? old, string? @new)>(
            "SELECT field, old_value, new_value FROM BacklogAudit WHERE backlog_id = @bid ORDER BY id;",
            new { bid })).ToList();

        Assert.Single(auditAfterFirst);
        Assert.Equal("tags", auditAfterFirst[0].field);
        // old set was empty → old_value is empty string (no ids)
        Assert.Equal("", auditAfterFirst[0].old);
        Assert.Equal(tag1.ToString(), auditAfterFirst[0].@new);

        // Change to tag2 — must produce a second 'tags' audit row.
        await repo.SetTagsAsync(bid, new[] { tag2 }, changedByUserId: 5, changedByName: "Editor");

        var auditAfterSecond = (await c.QueryAsync<(string field, string? old, string? @new)>(
            "SELECT field, old_value, new_value FROM BacklogAudit WHERE backlog_id = @bid ORDER BY id;",
            new { bid })).ToList();

        Assert.Equal(2, auditAfterSecond.Count);
        var secondRow = auditAfterSecond[1];
        Assert.Equal("tags", secondRow.field);
        Assert.Equal(tag1.ToString(), secondRow.old);
        Assert.Equal(tag2.ToString(), secondRow.@new);
    }

    // v9 (B1): SetTagsAsync writes NO audit row when the tag set is unchanged.
    [Fact]
    public async Task SetTagsAsync_writes_no_audit_row_when_set_unchanged()
    {
        var repo = new BacklogRepository(_db);
        var bid = await repo.InsertAsync(new Backlog(0, "REQ-TAGS-SAME", "TagNoAuditTest", DateTimeOffset.UtcNow));

        using var c = _db.Create();
        var tag1 = await c.ExecuteScalarAsync<int>(
            "INSERT INTO Tags(text, icon, color, created_at) VALUES('Gamma','✅','#00FF00','2026-01-01T00:00:00Z'); SELECT last_insert_rowid();");

        // Set {tag1}.
        await repo.SetTagsAsync(bid, new[] { tag1 }, changedByUserId: 5, changedByName: "Editor");

        // Set the same {tag1} again — must not write a second audit row.
        await repo.SetTagsAsync(bid, new[] { tag1 }, changedByUserId: 5, changedByName: "Editor");

        var auditRows = (await c.QueryAsync<string>(
            "SELECT field FROM BacklogAudit WHERE backlog_id = @bid AND field = 'tags';",
            new { bid })).ToList();

        Assert.Single(auditRows);  // only the first call (empty→tag1) produced an audit row
    }
}
