using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Repository contract is VERBATIM from architecture spec §3. Implementation (UserRepository)
// is owned by P2 Task 5 (Wave 2) — this file provides only the interface so Wave-1 services
// (CurrentUserService, XC-07) can depend on it and be unit-tested with a mock.
public interface IUserRepository
{
    Task<IReadOnlyList<User>> GetActiveAsync();
    Task<IReadOnlyList<User>> GetAllAsync();                       // Users tab shows inactive too (USR-01)
    Task<User?> GetByIdAsync(int id);
    Task<User?> GetByWindowsUsernameAsync(string windowsUsername); // XC-07 lookup
    Task<int> InsertAsync(User user);                              // returns new id (USR-02)
    Task SetWindowsUsernameAsync(int userId, string windowsUsername); // XC-07 persist
    Task SetActiveAsync(int userId, bool isActive);                // soft delete (USR-03)
    Task UpdateNameAsync(int userId, string name);
}
