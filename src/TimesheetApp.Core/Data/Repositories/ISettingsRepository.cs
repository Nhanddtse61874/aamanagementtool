namespace TimesheetApp.Data.Repositories;

// Repository contract is VERBATIM from architecture spec §3. Implementation (SettingsRepository)
// is owned by P2 Task 5 (Wave 2).
public interface ISettingsRepository
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);                      // INSERT OR REPLACE (SET-02)
    Task<IReadOnlyDictionary<string, string>> GetAllAsync();
}
