using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Holiday data access (HOL-01). holiday_date is the PK (yyyy-MM-dd TEXT). One short connection per method.
public sealed class HolidayRepository : IHolidayRepository
{
    private readonly IConnectionFactory _factory;

    public HolidayRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Holiday>> GetAllAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<HolidayRaw>(
            "SELECT holiday_date, description FROM Holidays ORDER BY holiday_date;");
        return rows.Select(MapHoliday).ToList();
    }

    public async Task<IReadOnlyList<Holiday>> GetForMonthAsync(int year, int month)
    {
        using var c = _factory.Create();
        // Month bounds as inclusive yyyy-MM-dd strings; lexical compare is valid for the fixed format.
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var rows = await c.QueryAsync<HolidayRaw>(
            @"SELECT holiday_date, description FROM Holidays
              WHERE holiday_date >= @from AND holiday_date <= @to
              ORDER BY holiday_date;",
            new { from = Day(from), to = Day(to) });
        return rows.Select(MapHoliday).ToList();
    }

    public async Task<bool> IsHolidayAsync(DateOnly date)
    {
        using var c = _factory.Create();
        // Single-row existence probe (avoids loading the whole calendar just to test one date).
        // Same yyyy-MM-dd TEXT key format as every other query in this repo.
        return await c.ExecuteScalarAsync<long>(
            "SELECT EXISTS(SELECT 1 FROM Holidays WHERE holiday_date = @d);",
            new { d = Day(date) }) != 0;
    }

    public async Task UpsertAsync(DateOnly date, string? description)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            @"INSERT INTO Holidays(holiday_date, description)
              VALUES(@d, @desc)
              ON CONFLICT(holiday_date) DO UPDATE SET description = excluded.description;",
            new { d = Day(date), desc = description });
    }

    public async Task DeleteAsync(DateOnly date)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "DELETE FROM Holidays WHERE holiday_date = @d;", new { d = Day(date) });
    }

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Holiday MapHoliday(HolidayRaw r) => new(
        DateOnly.ParseExact(r.holiday_date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        r.description);

    private sealed class HolidayRaw
    {
        public string holiday_date { get; set; } = "";
        public string? description { get; set; }
    }
}
