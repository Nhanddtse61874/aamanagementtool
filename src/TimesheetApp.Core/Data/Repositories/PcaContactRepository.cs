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
            "SELECT id, name, is_active, row_version FROM PcaContacts ORDER BY is_active DESC, name;");
        return rows.Select(MapContact).ToList();
    }

    public async Task<IReadOnlyList<PcaContact>> GetActiveAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<PcaContactRaw>(
            "SELECT id, name, is_active, row_version FROM PcaContacts WHERE is_active = 1 ORDER BY name;");
        return rows.Select(MapContact).ToList();
    }

    public async Task<PcaContact?> GetByIdAsync(int id)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<PcaContactRaw>(
            "SELECT id, name, is_active, row_version FROM PcaContacts WHERE id = @id;", new { id });
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

    // BUMP-ONLY: always lands, always bumps, never throws (the Settings rename path).
    public async Task UpdateNameAsync(int id, string name)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE PcaContacts SET name = @n, row_version = row_version + 1 WHERE id = @id;",
            new { n = name, id });
    }

    // CHECK-AND-BUMP; RETURNING supplies the caller's next expectedVersion from the same statement
    // that performed the write, so there is no racy read-back.
    public async Task<long> UpdateNameCheckedAsync(int id, string name, long expectedVersion)
    {
        using var c = _factory.Create();
        var newVersion = await c.QuerySingleOrDefaultAsync<long?>(
            @"UPDATE PcaContacts SET name = @n, row_version = row_version + 1
              WHERE id = @id AND row_version = @expected
              RETURNING row_version;",
            new { n = name, id, expected = expectedVersion });
        if (newVersion is not null) return newVersion.Value;

        var exists = await c.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM PcaContacts WHERE id = @id;", new { id });
        throw new ConcurrencyConflictException("PcaContacts", id, expectedVersion, deleted: exists == 0);
    }

    // Bump-only: a deactivation doesn't carry a version from the client, and always succeeds.
    public async Task SetActiveAsync(int id, bool isActive)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE PcaContacts SET is_active = @a, row_version = row_version + 1 WHERE id = @id;",
            new { a = isActive ? 1 : 0, id });
    }

    private static PcaContact MapContact(PcaContactRaw r) =>
        new((int)r.id, r.name, r.is_active != 0, r.row_version);

    private sealed class PcaContactRaw
    {
        public long id { get; set; }
        public string name { get; set; } = "";
        public long is_active { get; set; }
        public long row_version { get; set; }
    }
}
