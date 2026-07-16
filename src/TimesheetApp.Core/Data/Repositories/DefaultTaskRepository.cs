using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// DefaultTasks data access (SET-04, DATA-04). SQL + Dapper only; one short connection per method.
// Soft-delete only (SetActiveAsync) — a hidden default's materialized Task may own TimeLogs (XC-06).
public sealed class DefaultTaskRepository : IDefaultTaskRepository
{
    private readonly IConnectionFactory _factory;

    public DefaultTaskRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<DefaultTask>> GetActiveAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<DefaultTaskRaw>(
            @"SELECT id, task_name, order_index, is_active
              FROM DefaultTasks
              WHERE is_active = 1
              ORDER BY order_index;");
        return rows.Select(MapDefaultTask).ToList();
    }

    public async Task<IReadOnlyList<DefaultTask>> GetAllAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<DefaultTaskRaw>(
            @"SELECT id, task_name, order_index, is_active
              FROM DefaultTasks
              ORDER BY order_index;");
        return rows.Select(MapDefaultTask).ToList();
    }

    public async Task<int> InsertAsync(DefaultTask defaultTask)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO DefaultTasks(task_name, order_index, is_active)
              VALUES(@TaskName, @OrderIndex, @IsActive);
              SELECT last_insert_rowid();",
            new { defaultTask.TaskName, defaultTask.OrderIndex, IsActive = defaultTask.IsActive ? 1 : 0 });
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE DefaultTasks SET is_active = @a WHERE id = @id;",
            new { a = isActive ? 1 : 0, id });
    }

    private static DefaultTask MapDefaultTask(DefaultTaskRaw r) =>
        new((int)r.id, r.task_name, (int)r.order_index, r.is_active != 0);

    // SQLite-native shape (long/string) — narrowed at the boundary above (matches sibling repos).
    private sealed class DefaultTaskRaw
    {
        public long id { get; set; }
        public string task_name { get; set; } = "";
        public long order_index { get; set; }
        public long is_active { get; set; }
    }
}
