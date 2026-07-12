using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Maps Environment.UserName -> Users.windows_username. Returns an outcome enum;
/// never opens SelectUserDialog (the VM owns the dialog). (XC-07)</summary>
public sealed class CurrentUserService : ICurrentUserService
{
    private readonly IUserRepository _users;
    private readonly Func<string> _windowsUserName;

    public CurrentUserService(IUserRepository users)
        : this(users, () => Environment.UserName) { }

    // Test seam: inject the username provider.
    public CurrentUserService(IUserRepository users, Func<string> windowsUserName)
    {
        _users = users;
        _windowsUserName = windowsUserName;
    }

    public User? Current { get; private set; }

    public async Task<CurrentUserResult> ResolveAsync()
    {
        var name = _windowsUserName();
        var user = await _users.GetByUsernameAsync(name);
        if (user is null)
        {
            Current = null;
            return new CurrentUserResult(CurrentUserOutcome.NeedsSelection, null);
        }

        Current = user;
        return new CurrentUserResult(CurrentUserOutcome.Resolved, user);
    }

    public async Task SetWindowsUsernameAsync(int userId, string windowsUsername)
    {
        // IUserRepository.SetUsernameAsync is check-and-bump (v10/M8.2), so it needs the version
        // this caller last saw. Current is typically null here (this runs after ResolveAsync
        // returned NeedsSelection), so it cannot supply it -- fetch userId's row fresh instead.
        var before = await _users.GetByIdAsync(userId);
        await _users.SetUsernameAsync(userId, windowsUsername, before?.RowVersion ?? 0);
        Current = await _users.GetByIdAsync(userId);
    }
}
