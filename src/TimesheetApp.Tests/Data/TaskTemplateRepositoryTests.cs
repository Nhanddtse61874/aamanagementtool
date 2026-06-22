using Dapper;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using Xunit;

namespace TimesheetApp.Tests.Data;

// Reuses the TestDb fixture (real P1 schema -> TaskTemplates table present, no template seed rows).
// Per standing correction: the canonical SqliteConnectionFactory takes IAppConfig, so we build the
// schema via TestDb and seed the three rows the plan specifies through TestDb.Create().
public sealed class TaskTemplateRepositoryTests
{
    private static async Task SeedAsync(TestDb db)
    {
        using var c = db.Create();
        await c.ExecuteAsync(
            @"INSERT INTO TaskTemplates(template_name, task_name, order_index) VALUES
                ('Web','Setup',0),('Web','Build',1),('API','Design',0);");
    }

    [Fact]
    public async Task GetAllAsync_returns_all_rows_ordered_by_template_then_order()
    {
        using var db = await TestDb.CreateAsync();
        await SeedAsync(db);
        var repo = new TaskTemplateRepository(db);

        var rows = await repo.GetAllAsync();

        Assert.Equal(3, rows.Count);
        Assert.Equal("API", rows[0].TemplateName);   // API before Web
        Assert.Equal("Design", rows[0].TaskName);
        Assert.Equal("Web", rows[1].TemplateName);
        Assert.Equal("Setup", rows[1].TaskName);     // order_index 0 before 1
        Assert.Equal("Build", rows[2].TaskName);
    }

    [Fact] // SET-03: insert returns new id and the row is retrievable
    public async Task InsertAsync_adds_row_and_returns_id()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new TaskTemplateRepository(db);

        var id = await repo.InsertAsync(new TaskTemplate(0, "Bugfix", "Triage", 0));

        Assert.True(id > 0);
        var rows = await repo.GetAllAsync();
        Assert.Contains(rows, r => r.Id == id && r.TemplateName == "Bugfix" && r.TaskName == "Triage");
    }

    [Fact] // SET-03: delete removes the row
    public async Task DeleteAsync_removes_row()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new TaskTemplateRepository(db);
        var id = await repo.InsertAsync(new TaskTemplate(0, "Temp", "X", 0));

        await repo.DeleteAsync(id);

        var rows = await repo.GetAllAsync();
        Assert.DoesNotContain(rows, r => r.Id == id);
    }

    [Fact] // SET-03: delete-by-name removes every row of a template, leaving others intact
    public async Task DeleteByTemplateNameAsync_removes_all_rows_of_that_template_only()
    {
        using var db = await TestDb.CreateAsync();
        await SeedAsync(db); // Web(Setup,Build) + API(Design)
        var repo = new TaskTemplateRepository(db);

        await repo.DeleteByTemplateNameAsync("Web");

        var rows = await repo.GetAllAsync();
        Assert.DoesNotContain(rows, r => r.TemplateName == "Web");
        Assert.Single(rows);
        Assert.Equal("API", rows[0].TemplateName);
    }
}
