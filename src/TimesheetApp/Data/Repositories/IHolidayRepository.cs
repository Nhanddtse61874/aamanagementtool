using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Manually-marked non-working days (HOL-01). Date is the natural key; upsert toggles description.
public interface IHolidayRepository
{
    Task<IReadOnlyList<Holiday>> GetAllAsync();              // whole calendar (for the working-day set)
    Task<IReadOnlyList<Holiday>> GetForMonthAsync(int year, int month);
    Task UpsertAsync(DateOnly date, string? description);   // mark / re-describe a holiday
    Task DeleteAsync(DateOnly date);                         // unmark a holiday
}
