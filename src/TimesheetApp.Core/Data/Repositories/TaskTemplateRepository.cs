using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// TaskTemplate data access (REQ-02 source, SET-03 CRUD). SQL + Dapper only; one short connection
// per method. Reads go through a raw DTO (long/string) mapped to the positional record at the
// boundary — Dapper cannot bind columns onto a positional record's ctor params (see RequestRepository).
public sealed class TaskTemplateRepository : ITaskTemplateRepository
{
    private readonly IConnectionFactory _factory;

    public TaskTemplateRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<TaskTemplate>> GetAllAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<TaskTemplateRaw>(
            @"SELECT id, template_name, task_name, order_index
              FROM TaskTemplates
              ORDER BY template_name, order_index;");
        return rows.Select(MapTemplate).ToList();
    }

    public async Task<int> InsertAsync(TaskTemplate template)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO TaskTemplates(template_name, task_name, order_index)
              VALUES(@TemplateName, @TaskName, @OrderIndex);
              SELECT last_insert_rowid();",
            template);
    }

    public async Task DeleteAsync(int id)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync("DELETE FROM TaskTemplates WHERE id = @id;", new { id });
    }

    public async Task DeleteByTemplateNameAsync(string templateName)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync("DELETE FROM TaskTemplates WHERE template_name = @n;", new { n = templateName });
    }

    private static TaskTemplate MapTemplate(TaskTemplateRaw r) =>
        new((int)r.id, r.template_name, r.task_name, (int)r.order_index);

    // SQLite-native shape (long/string) — Dapper's positional-record path cannot bind onto the
    // TaskTemplate record's ctor params, so reads land here then map at the boundary above.
    private sealed class TaskTemplateRaw
    {
        public long id { get; set; }
        public string template_name { get; set; } = "";
        public string task_name { get; set; } = "";
        public long order_index { get; set; }
    }
}
