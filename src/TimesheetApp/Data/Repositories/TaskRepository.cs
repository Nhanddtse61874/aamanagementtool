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
            @"SELECT id, backlog_id, task_name, order_index, is_active, status
              FROM Tasks
              WHERE backlog_id = @b AND is_active = 1
              ORDER BY order_index;",
            new { b = backlogId });
        return rows.Select(MapTask).ToList();
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
            @"SELECT t.id, t.backlog_id, t.task_name, t.order_index, t.is_active, t.status
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
            "SELECT id, backlog_id, task_name, order_index, is_active, status FROM Tasks WHERE id = @id;",
            new { id });
        return row is null ? null : MapTask(row);
    }

    public async Task<TaskItem?> GetByNameInBacklogAsync(int backlogId, string taskName)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<TaskRaw>(
            @"SELECT id, backlog_id, task_name, order_index, is_active, status
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

    private static TaskItem MapTask(TaskRaw r) =>
        new((int)r.id, (int)r.backlog_id, r.task_name, (int)r.order_index, r.is_active != 0, r.status ?? "Todo");

    // SQLite-native shape (long/string) — narrowed at the boundary above.
    private sealed class TaskRaw
    {
        public long id { get; set; }
        public long backlog_id { get; set; }
        public string task_name { get; set; } = "";
        public long order_index { get; set; }
        public long is_active { get; set; }
        public string? status { get; set; }
    }
}
