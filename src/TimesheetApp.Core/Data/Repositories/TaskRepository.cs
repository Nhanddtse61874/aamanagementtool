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

    public async Task UpdateAsync(TaskItem task)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Tasks SET task_name = @TaskName, order_index = @OrderIndex, status = @Status WHERE id = @Id;",
            new { task.TaskName, task.OrderIndex, task.Status, task.Id });
    }

    public async Task SetActiveAsync(int taskId, bool isActive)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Tasks SET is_active = @a WHERE id = @id;",
            new { a = isActive ? 1 : 0, id = taskId });
    }

    public async Task SetOrderAsync(int taskId, int orderIndex)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Tasks SET order_index = @o WHERE id = @id;",
            new { o = orderIndex, id = taskId });
    }

    // ---- v9 (P13-B3) task-level type/assignee/status edits + audit ----------------------

    public async Task UpdateExtendedAsync(int taskId, string? type, int? assigneeUserId,
        int? changedByUserId = null, string? changedByName = null)
    {
        using var c = _factory.Create();

        // Pre-read so audited field changes can be diffed (mirrors BacklogRepository.UpdateAsync).
        var before = await c.QuerySingleOrDefaultAsync<TaskRaw>(
            "SELECT id, backlog_id, task_name, order_index, is_active, status, type, assignee_user_id FROM Tasks WHERE id = @id;",
            new { id = taskId });
        if (before is null) return;

        await c.ExecuteAsync(
            "UPDATE Tasks SET type = @type, assignee_user_id = @uid WHERE id = @id;",
            new { type, uid = assigneeUserId, id = taskId });

        var now = Iso(DateTimeOffset.UtcNow);

        async Task LogAsync(string field, string? oldV, string? newV)
        {
            if (string.Equals(oldV ?? "", newV ?? "", StringComparison.Ordinal)) return;
            await c.ExecuteAsync(
                @"INSERT INTO TaskAudit(task_id, field, old_value, new_value,
                    changed_by_user_id, changed_by_name, changed_at)
                  VALUES(@tid, @field, @old, @new, @uid, @uname, @at);",
                new { tid = taskId, field, old = oldV, @new = newV,
                      uid = changedByUserId, uname = changedByName, at = now });
        }

        await LogAsync("type", before.type, type);

        // Audit the assignee by NAME (readable history), gated on the user-id actually changing.
        if (before.assignee_user_id != (assigneeUserId.HasValue ? (long?)assigneeUserId.Value : null))
        {
            async Task<string?> NameOfAsync(int? uid) => uid is null ? null
                : await c.QuerySingleOrDefaultAsync<string?>("SELECT name FROM Users WHERE id = @id;", new { id = uid });
            await LogAsync("assignee",
                await NameOfAsync(before.assignee_user_id is { } b ? (int)b : null),
                await NameOfAsync(assigneeUserId));
        }
    }

    public async Task UpdateStatusAsync(int taskId, string status,
        int? changedByUserId = null, string? changedByName = null)
    {
        using var c = _factory.Create();

        var before = await c.QuerySingleOrDefaultAsync<string?>(
            "SELECT status FROM Tasks WHERE id = @id;", new { id = taskId });
        if (before is null) return;

        await c.ExecuteAsync(
            "UPDATE Tasks SET status = @status WHERE id = @id;", new { status, id = taskId });

        if (!string.Equals(before, status, StringComparison.Ordinal))
        {
            await c.ExecuteAsync(
                @"INSERT INTO TaskAudit(task_id, field, old_value, new_value,
                    changed_by_user_id, changed_by_name, changed_at)
                  VALUES(@tid, 'status', @old, @new, @uid, @uname, @at);",
                new { tid = taskId, old = before, @new = status,
                      uid = changedByUserId, uname = changedByName, at = Iso(DateTimeOffset.UtcNow) });
        }
    }

    public async Task<IReadOnlyList<int>> GetTagIdsAsync(int taskId)
    {
        using var c = _factory.Create();
        var ids = await c.QueryAsync<long>(
            "SELECT tag_id FROM TaskTags WHERE task_id = @tid ORDER BY tag_id;", new { tid = taskId });
        return ids.Select(i => (int)i).ToList();
    }

    public async Task SetTaskTagsAsync(int taskId, IReadOnlyList<int> tagIds,
        int? changedByUserId = null, string? changedByName = null)
    {
        // Replace-all in one tx: capture the old set, clear, re-insert the new set (dedup),
        // then write ONE 'tags' audit row when the set actually changed (B3; mirrors BacklogRepository).
        using var c = _factory.Create();
        using var tx = c.BeginTransaction();

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
