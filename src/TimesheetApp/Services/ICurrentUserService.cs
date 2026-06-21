using TimesheetApp.Models;

namespace TimesheetApp.Services;

// Current-user resolution contract is VERBATIM from architecture spec §4.
// ResolveAsync returns an outcome enum — it NEVER opens SelectUserDialog (the VM owns the
// dialog). (XC-07)
public interface ICurrentUserService
{
    Task<CurrentUserResult> ResolveAsync();     // Environment.UserName -> lookup -> Resolved|NeedsSelection (XC-07)
    Task SetWindowsUsernameAsync(int userId, string windowsUsername); // persist after dialog pick (XC-07)
    User? Current { get; }
}
