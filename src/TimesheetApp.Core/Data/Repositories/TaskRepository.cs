using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Task data access (TS-02, REQ-02..04, SET-04, DATA-03). SQL + Dapper only.
// Soft-delete only (SetActiveAsync) — never hard-delete a task that may own TimeLogs (XC-06).
// Template methods are deliberately absent — they live on ITaskTemplateRepository (P4).
public sealed class TaskRepository : ITaskRepository
{
    private readonly IConnectionFactory _factory;

    public TaskRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<TaskItem>> GetActiveByBacklogAsync(int backlogId)
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<TaskRaw>(
            @"SELECT id, backlog_id, task_name, order_index, is_active, status, type, assignee_user_id
              FROM Tasks
              WHERE backlog_id = @b AND is_active = 1
              ORDER BY order_index;",
            new { b = backlogId });
        return rows.Select(MapTask).ToList();
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<TaskItem>>> GetActiveByBacklogsAsync(IReadOnlyList<int> backlogIds)
    {
        using var c = _factory.Create();
        // One IN-query for all backlogs (avoids N+1 vs. calling GetActiveByBacklogAsync per id),
        // grouped by backlog_id. ORDER BY backlog_id, order_index keeps each group ordered like
        // the single-backlog query. Mirrors the column list + MapTask of GetActiveByBacklogAsync.
        var rows = await c.QueryAsync<TaskRaw>(
            @"SELECT id, backlog_id, task_name, order_index, is_active, status, type, assignee_user_id
              FROM Tasks
              WHERE backlog_id IN @ids AND is_active = 1
              ORDER BY backlog_id, order_index;",
            new { ids = backlogIds });
        return rows
            .GroupBy(r => (int)r.backlog_id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TaskItem>)g.Select(MapTask).ToList());
    }

    public async Task<IReadOnlyList<TaskItem>> GetActiveForTimesheetAsync(int? teamId = null)
    {
        using var c = _factory.Create();
        // Active tasks for the team's backlogs (incl. that team's hidden DEFAULT), ordered (TS-02).
        // Backlogs have no is_active column (decision 4) so every backlog is selectable; the
        // DEFAULT backlog's tasks are included because we only filter on the task's is_active.
        // @noTeam short-circuits the team filter when teamId is null (preserves existing behavior, R6);
        // a teamId filters via b.team_id (teamId 0 yields no rows — empty, R6).
        var noTeam = teamId is null;
        var rows = await c.QueryAsync<TaskRaw>(
            @"SELECT t.id, t.backlog_id, t.task_name, t.order_index, t.is_active, t.status, t.type, t.assignee_user_id
              FROM Tasks t
              JOIN Backlogs b ON b.id = t.backlog_id
              WHERE t.is_active = 1
                AND (@noTeam OR b.team_id = @teamId)
              ORDER BY b.backlog_code, t.order_index;",
            new { noTeam, teamId = teamId ?? 0 });
        return rows.Select(MapTask).ToList();
    }

    public async Task<TaskItem?> GetByIdAsync(int id)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<TaskRaw>(
            "SELECT id, backlog_id, task_name, order_index, is_active, status, type, assignee_user_id FROM Tasks WHERE id = @id;",
            new { id });
        return row is null ? null : MapTask(row);
    }

    public async Task<TaskItem?> GetByNameInBacklogAsync(int backlogId, string taskName)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<TaskRaw>(
            @"SELECT id, backlog_id, task_name, order_index, is_active, status, type, assignee_user_id
              FROM Tasks WHERE backlog_id = @b AND task_name = @n;",
            new { b = backlogId, n = taskName });
        return row is null ? null : MapTask(row);
    }

    public async Task<int> InsertAsync(TaskItem task)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Tasks(backlog_id, task_name, order_index, is_active, status)
              VALUES(@BacklogId, @TaskName, @OrderIndex, @IsActive, @Status);
              SELECT last_insert_rowid();",
            new { task.BacklogId, task.TaskName, task.OrderIndex, IsActive = task.IsActive ? 1 : 0, task.Status });
    }

    // M8.2: the number a caller reads, holds while the user edits, then hands back as expectedVersion.
    public async Task<long?> GetRowVersionAsync(int taskId)
    {
        using var c = _factory.Create();
        return await c.QuerySingleOrDefaultAsync<long?>(
            "SELECT row_version FROM Tasks WHERE id = @id;", new { id = taskId });
    }

    // check-and-bump: a user edit of the task's own fields. One statement, so it is already atomic —
    // no audit rows hang off it, hence no transaction.
    public async Task UpdateAsync(TaskItem task, long? expectedVersion = null)
    {
        using var c = _factory.Create();
        var rows = await c.ExecuteAsync(
            @"UPDATE Tasks SET task_name = @TaskName, order_index = @OrderIndex, status = @Status,
                row_version = row_version + 1
              WHERE id = @Id AND (@ExpectedVersion IS NULL OR row_version = @ExpectedVersion);",
            new { task.TaskName, task.OrderIndex, task.Status, task.Id, ExpectedVersion = expectedVersion });

        if (rows == 0 && expectedVersion is not null)
        {
            throw new ConcurrencyConflictException("Tasks", task.Id, expectedVersion,
                deleted: await NotFoundAsync(c, null, task.Id));
        }
    }

    // bump-only: a soft-delete carries no version from a client. It must still bump, so that anyone
    // holding a stale read of this task is told when they next try to write.
    public async Task SetActiveAsync(int taskId, bool isActive)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Tasks SET is_active = @a, row_version = row_version + 1 WHERE id = @id;",
            new { a = isActive ? 1 : 0, id = taskId });
    }

    // bump-only, and it MUST stay that way. TimesheetViewModel.ReorderAsync calls this once per row
    // over the whole list; under check-and-bump the first row would bump the version and every row
    // after it would arrive stale, so an ordinary drag-and-drop would 409-storm and the reorder would
    // fall apart halfway through. A reorder carries no version from a client — it is a system write.
    public async Task SetOrderAsync(int taskId, int orderIndex)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Tasks SET order_index = @o, row_version = row_version + 1 WHERE id = @id;",
            new { o = orderIndex, id = taskId });
    }

    // ---- v9 (P13-B3) task-level type/assignee/status edits + audit ----------------------

    public async Task UpdateExtendedAsync(int taskId, string? type, int? assigneeUserId,
        int? changedByUserId = null, string? changedByName = null, long? expectedVersion = null)
    {
        using var c = _factory.Create();
        // M8.2: read -> UPDATE -> N audit INSERTs is ONE unit of work, so it gets ONE transaction.
        // Untransacted, a rejected write would still leave its audit rows behind — history describing
        // a change that never happened. Default BeginTransaction() is BEGIN IMMEDIATE (NOT deferred,
        // which would reintroduce SQLITE_BUSY_SNAPSHOT).
        using var tx = c.BeginTransaction();

        // Pre-read so audited field changes can be diffed (mirrors BacklogRepository.UpdateAsync).
        var before = await c.QuerySingleOrDefaultAsync<TaskRaw>(
            "SELECT id, backlog_id, task_name, order_index, is_active, status, type, assignee_user_id FROM Tasks WHERE id = @id;",
            new { id = taskId }, tx);
        if (before is null)
        {
            // This read IS the existence check the conflict path owes the caller — no second query.
            if (expectedVersion is not null)
                throw new ConcurrencyConflictException("Tasks", taskId, expectedVersion, deleted: true);
            return;   // bump-only against a row that is gone: unchanged no-op semantics
        }

        var rows = await c.ExecuteAsync(
            @"UPDATE Tasks SET type = @type, assignee_user_id = @uid, row_version = row_version + 1
              WHERE id = @id AND (@expected IS NULL OR row_version = @expected);",
            new { type, uid = assigneeUserId, id = taskId, expected = expectedVersion }, tx);

        // We hold an IMMEDIATE transaction and `before` proved the row exists, so nobody can have
        // deleted it underneath us — a zero here can only mean the version moved on.
        if (rows == 0 && expectedVersion is not null)
            throw new ConcurrencyConflictException("Tasks", taskId, expectedVersion, deleted: false);

        var now = Iso(DateTimeOffset.UtcNow);

        async Task LogAsync(string field, string? oldV, string? newV)
        {
            if (string.Equals(oldV ?? "", newV ?? "", StringComparison.Ordinal)) return;
            await c.ExecuteAsync(
                @"INSERT INTO TaskAudit(task_id, field, old_value, new_value,
                    changed_by_user_id, changed_by_name, changed_at)
                  VALUES(@tid, @field, @old, @new, @uid, @uname, @at);",
                new { tid = taskId, field, old = oldV, @new = newV,
                      uid = changedByUserId, uname = changedByName, at = now }, tx);
        }

        await LogAsync("type", before.type, type);

        // Audit the assignee by NAME (readable history), gated on the user-id actually changing.
        if (before.assignee_user_id != (assigneeUserId.HasValue ? (long?)assigneeUserId.Value : null))
        {
            async Task<string?> NameOfAsync(int? uid) => uid is null ? null
                : await c.QuerySingleOrDefaultAsync<string?>("SELECT name FROM Users WHERE id = @id;", new { id = uid }, tx);
            await LogAsync("assignee",
                await NameOfAsync(before.assignee_user_id is { } b ? (int)b : null),
                await NameOfAsync(assigneeUserId));
        }

        tx.Commit();
    }

    public async Task UpdateStatusAsync(int taskId, string status,
        int? changedByUserId = null, string? changedByName = null, long? expectedVersion = null)
    {
        using var c = _factory.Create();
        // Same read -> UPDATE -> audit INSERT unit of work as UpdateExtendedAsync; same transaction.
        using var tx = c.BeginTransaction();

        var before = await c.QuerySingleOrDefaultAsync<string?>(
            "SELECT status FROM Tasks WHERE id = @id;", new { id = taskId }, tx);
        if (before is null)
        {
            if (expectedVersion is not null)
                throw new ConcurrencyConflictException("Tasks", taskId, expectedVersion, deleted: true);
            return;
        }

        var rows = await c.ExecuteAsync(
            @"UPDATE Tasks SET status = @status, row_version = row_version + 1
              WHERE id = @id AND (@expected IS NULL OR row_version = @expected);",
            new { status, id = taskId, expected = expectedVersion }, tx);

        if (rows == 0 && expectedVersion is not null)
            throw new ConcurrencyConflictException("Tasks", taskId, expectedVersion, deleted: false);

        if (!string.Equals(before, status, StringComparison.Ordinal))
        {
            await c.ExecuteAsync(
                @"INSERT INTO TaskAudit(task_id, field, old_value, new_value,
                    changed_by_user_id, changed_by_name, changed_at)
                  VALUES(@tid, 'status', @old, @new, @uid, @uname, @at);",
                new { tid = taskId, old = before, @new = status,
                      uid = changedByUserId, uname = changedByName, at = Iso(DateTimeOffset.UtcNow) }, tx);
        }

        tx.Commit();
    }

    public async Task<IReadOnlyList<int>> GetTagIdsAsync(int taskId)
    {
        using var c = _factory.Create();
        var ids = await c.QueryAsync<long>(
            "SELECT tag_id FROM TaskTags WHERE task_id = @tid ORDER BY tag_id;", new { tid = taskId });
        return ids.Select(i => (int)i).ToList();
    }

    public async Task SetTaskTagsAsync(int taskId, IReadOnlyList<int> tagIds,
        int? changedByUserId = null, string? changedByName = null, long? expectedVersion = null)
    {
        // Replace-all in one tx: capture the old set, clear, re-insert the new set (dedup),
        // then write ONE 'tags' audit row when the set actually changed (B3; mirrors BacklogRepository).
        using var c = _factory.Create();
        using var tx = c.BeginTransaction();

        // M8.2: TaskTags carries no row_version of its own, so a tag replace-all checks and bumps the
        // PARENT task's version (mirrors BacklogRepository.SetTagsAsync). Runs first so a conflict
        // aborts the tx before any TaskTags row is touched.
        var bumped = await c.ExecuteAsync(
            @"UPDATE Tasks SET row_version = row_version + 1
              WHERE id = @tid AND (@expected IS NULL OR row_version = @expected);",
            new { tid = taskId, expected = expectedVersion }, tx);

        if (bumped == 0 && expectedVersion is not null)
        {
            throw new ConcurrencyConflictException("Tasks", taskId, expectedVersion,
                deleted: await NotFoundAsync(c, tx, taskId));
        }

        var oldIds = (await c.QueryAsync<long>(
            "SELECT tag_id FROM TaskTags WHERE task_id = @tid ORDER BY tag_id;",
            new { tid = taskId }, tx)).Select(i => (int)i).ToHashSet();
        var newIds = tagIds.Distinct().ToHashSet();

        await c.ExecuteAsync("DELETE FROM TaskTags WHERE task_id = @tid;", new { tid = taskId }, tx);
        foreach (var tagId in newIds)
        {
            await c.ExecuteAsync(
                "INSERT INTO TaskTags(task_id, tag_id) VALUES(@tid, @tagId);",
                new { tid = taskId, tagId }, tx);
        }

        if (!oldIds.SetEquals(newIds))
        {
            await c.ExecuteAsync(
                @"INSERT INTO TaskAudit(task_id, field, old_value, new_value,
                    changed_by_user_id, changed_by_name, changed_at)
                  VALUES(@tid, 'tags', @old, @new, @uid, @uname, @at);",
                new { tid = taskId,
                      old = string.Join(",", oldIds.OrderBy(x => x)),
                      @new = string.Join(",", newIds.OrderBy(x => x)),
                      uid = changedByUserId, uname = changedByName, at = Iso(DateTimeOffset.UtcNow) }, tx);
        }

        tx.Commit();
    }

    public async Task<IReadOnlyList<TaskAuditEntry>> GetAuditAsync(int taskId)
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<AuditRaw>(
            @"SELECT id, task_id, field, old_value, new_value,
                     changed_by_user_id, changed_by_name, changed_at
              FROM TaskAudit WHERE task_id = @tid ORDER BY id DESC;",
            new { tid = taskId });
        return rows.Select(a => new TaskAuditEntry(
            (int)a.id, (int)a.task_id, a.field, a.old_value, a.new_value,
            a.changed_by_user_id is { } uid ? (int)uid : null, a.changed_by_name,
            DateTimeOffset.Parse(a.changed_at, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))).ToList();
    }

    // ONE existence check, run only on the conflict path, and only where no pre-read already answered
    // it. Separates "someone else edited this" from "someone else deleted this" — the two things
    // rowsAffected == 0 conflates, and the two things the user needs different words for.
    private static async Task<bool> NotFoundAsync(System.Data.IDbConnection c, System.Data.IDbTransaction? tx, int id) =>
        await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Tasks WHERE id = @id;", new { id }, tx) == 0;

    private static string Iso(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static TaskItem MapTask(TaskRaw r) =>
        new((int)r.id, (int)r.backlog_id, r.task_name, (int)r.order_index, r.is_active != 0,
            r.status ?? "Todo", r.type, r.assignee_user_id is { } a ? (int)a : null);

    // SQLite-native shape (long/string) — narrowed at the boundary above.
    private sealed class TaskRaw
    {
        public long id { get; set; }
        public long backlog_id { get; set; }
        public string task_name { get; set; } = "";
        public long order_index { get; set; }
        public long is_active { get; set; }
        public string? status { get; set; }
        // v9
        public string? type { get; set; }
        public long? assignee_user_id { get; set; }
    }

    private sealed class AuditRaw
    {
        public long id { get; set; }
        public long task_id { get; set; }
        public string field { get; set; } = "";
        public string? old_value { get; set; }
        public string? new_value { get; set; }
        public long? changed_by_user_id { get; set; }
        public string? changed_by_name { get; set; }
        public string changed_at { get; set; } = "";
    }
}
