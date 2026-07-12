using Dapper;

namespace TimesheetApp.Data.Repositories;

// Key-value settings store (SET-02). SQL + Dapper only; one short connection per method.
public sealed class SettingsRepository : ISettingsRepository
{
    private readonly IConnectionFactory _factory;

    public SettingsRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<string?> GetAsync(string key)
    {
        using var c = _factory.Create();
        return await c.QuerySingleOrDefaultAsync<string?>(
            "SELECT value FROM Settings WHERE key = @k;", new { k = key });
    }

    public async Task SetAsync(string key, string value)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "INSERT OR REPLACE INTO Settings(key, value) VALUES(@k, @v);",
            new { k = key, v = value });
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<(string Key, string Value)>("SELECT key, value FROM Settings;");
        return rows.ToDictionary(r => r.Key, r => r.Value);
    }
}
