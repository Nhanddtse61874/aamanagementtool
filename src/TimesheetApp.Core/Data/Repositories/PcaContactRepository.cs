using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// PCA contact data access (TL-11). Soft-delete only (SetActiveAsync), mirroring UserRepository —
// never hard-delete a contact that historical backlogs may reference. One short connection per method.
public sealed class PcaContactRepository : IPcaContactRepository
{
    private readonly IConnectionFactory _factory;

    public PcaContactRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<PcaContact>> GetAllAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<PcaContactRaw>(
            "SELECT id, name, is_active FROM PcaContacts ORDER BY is_active DESC, name;");
        return rows.Select(MapContact).ToList();
    }

    public async Task<IReadOnlyList<PcaContact>> GetActiveAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<PcaContactRaw>(
            "SELECT id, name, is_active FROM PcaContacts WHERE is_active = 1 ORDER BY name;");
        return rows.Select(MapContact).ToList();
    }

    public async Task<PcaContact?> GetByIdAsync(int id)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<PcaContactRaw>(
            "SELECT id, name, is_active FROM PcaContacts WHERE id = @id;", new { id });
        return row is null ? null : MapContact(row);
    }

    public async Task<int> InsertAsync(PcaContact contact)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO PcaContacts(name, is_active)
              VALUES(@Name, @IsActive);
              SELECT last_insert_rowid();",
            new { contact.Name, IsActive = contact.IsActive ? 1 : 0 });
    }

    public async Task UpdateNameAsync(int id, string name)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE PcaContacts SET name = @n WHERE id = @id;", new { n = name, id });
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE PcaContacts SET is_active = @a WHERE id = @id;",
            new { a = isActive ? 1 : 0, id });
    }

    private static PcaContact MapContact(PcaContactRaw r) =>
        new((int)r.id, r.name, r.is_active != 0);

    private sealed class PcaContactRaw
    {
        public long id { get; set; }
        public string name { get; set; } = "";
        public long is_active { get; set; }
    }
}
