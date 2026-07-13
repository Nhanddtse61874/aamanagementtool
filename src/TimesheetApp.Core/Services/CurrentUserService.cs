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
        // BUMP-ONLY (M8.2). This runs after ResolveAsync returned NeedsSelection, i.e. when Current is
        // null, so this service holds no version it could legitimately check against. W3-C fetched the
        // row first purely to synthesize one — but a version invented immediately before the write
        // checks nothing: it cannot fail, and the fetch is exactly the racy read-back the checked
        // methods exist to avoid. Claiming your own Windows identity is a system write, not a
        // contested user edit — there is no second person racing to claim your account.
        await _users.SetUsernameAsync(userId, windowsUsername);
        Current = await _users.GetByIdAsync(userId);
    }
}
