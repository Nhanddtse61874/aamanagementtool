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

    public async Task<IReadOnlyList<TaskItem>> GetActiveByRequestAsync(int requestId)
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<TaskRaw>(
            @"SELECT id, request_id, task_name, order_index, is_active
              FROM Tasks
              WHERE request_id = @r AND is_active = 1
              ORDER BY order_index;",
            new { r = requestId });
        return rows.Select(MapTask).ToList();
    }

    public async Task<IReadOnlyList<TaskItem>> GetActiveForTimesheetAsync()
    {
        using var c = _factory.Create();
        // Active tasks across all requests (incl. the hidden DEFAULT request), ordered (TS-02).
        // Requests have no is_active column (decision 4) so every request is selectable; the
        // DEFAULT request's tasks are included because we only filter on the task's is_active.
        var rows = await c.QueryAsync<TaskRaw>(
            @"SELECT t.id, t.request_id, t.task_name, t.order_index, t.is_active
              FROM Tasks t
              JOIN Requests r ON r.id = t.request_id
              WHERE t.is_active = 1
              ORDER BY r.request_code, t.order_index;");
        return rows.Select(MapTask).ToList();
    }

    public async Task<TaskItem?> GetByIdAsync(int id)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<TaskRaw>(
            "SELECT id, request_id, task_name, order_index, is_active FROM Tasks WHERE id = @id;",
            new { id });
        return row is null ? null : MapTask(row);
    }

    public async Task<TaskItem?> GetByNameInRequestAsync(int requestId, string taskName)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<TaskRaw>(
            @"SELECT id, request_id, task_name, order_index, is_active
              FROM Tasks WHERE request_id = @r AND task_name = @n;",
            new { r = requestId, n = taskName });
        return row is null ? null : MapTask(row);
    }

    public async Task<int> InsertAsync(TaskItem task)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Tasks(request_id, task_name, order_index, is_active)
              VALUES(@RequestId, @TaskName, @OrderIndex, @IsActive);
              SELECT last_insert_rowid();",
            new { task.RequestId, task.TaskName, task.OrderIndex, IsActive = task.IsActive ? 1 : 0 });
    }

    public async Task UpdateAsync(TaskItem task)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Tasks SET task_name = @TaskName, order_index = @OrderIndex WHERE id = @Id;",
            new { task.TaskName, task.OrderIndex, task.Id });
    }

    public async Task SetActiveAsync(int taskId, bool isActive)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Tasks SET is_active = @a WHERE id = @id;",
            new { a = isActive ? 1 : 0, id = taskId });
    }

    private static TaskItem MapTask(TaskRaw r) =>
        new((int)r.id, (int)r.request_id, r.task_name, (int)r.order_index, r.is_active != 0);

    // SQLite-native shape (long/string) — narrowed at the boundary above (see Task 4 mapping note).
    private sealed class TaskRaw
    {
        public long id { get; set; }
        public long request_id { get; set; }
        public string task_name { get; set; } = "";
        public long order_index { get; set; }
        public long is_active { get; set; }
    }
}
