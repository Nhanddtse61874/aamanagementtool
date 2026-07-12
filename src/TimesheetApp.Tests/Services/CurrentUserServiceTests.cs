using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

public class CurrentUserServiceTests
{
    private readonly Mock<IUserRepository> _users = new();

    private CurrentUserService Make(string windowsName)
        => new(_users.Object, () => windowsName);

    [Fact]
    public async Task ResolveAsync_match_returns_Resolved_and_sets_Current()
    {
        var u = new User(7, "Nguyen Van A", "DOMAIN\\nva", true);
        _users.Setup(r => r.GetByUsernameAsync("DOMAIN\\nva")).ReturnsAsync(u);
        var svc = Make("DOMAIN\\nva");

        var result = await svc.ResolveAsync();

        Assert.Equal(CurrentUserOutcome.Resolved, result.Outcome);
        Assert.Equal(u, result.User);
        Assert.Equal(u, svc.Current);
    }

    [Fact]
    public async Task ResolveAsync_no_match_returns_NeedsSelection_and_null_Current()
    {
        _users.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        var svc = Make("DOMAIN\\unknown");

        var result = await svc.ResolveAsync();

        Assert.Equal(CurrentUserOutcome.NeedsSelection, result.Outcome);
        Assert.Null(result.User);
        Assert.Null(svc.Current);
    }

    [Fact]
    public async Task SetWindowsUsernameAsync_persists_and_refreshes_Current()
    {
        var picked = new User(3, "Picked", null, true);
        _users.Setup(r => r.GetByIdAsync(3)).ReturnsAsync(picked with { WindowsUsername = "DOMAIN\\pk" });
        var svc = Make("DOMAIN\\pk");

        await svc.SetWindowsUsernameAsync(3, "DOMAIN\\pk");

        // BUMP-ONLY (W3.5). This runs when Current is null, so the service holds no version it could
        // legitimately check against. W3-C pre-fetched the row to synthesize one — but a version
        // invented immediately before the write checks nothing, and the fetch is the very read-back
        // the checked methods exist to avoid. Claiming your own identity is a system write.
        _users.Verify(r => r.SetUsernameAsync(3, "DOMAIN\\pk"), Times.Once);
        Assert.Equal(3, svc.Current!.Id);
        Assert.Equal("DOMAIN\\pk", svc.Current!.WindowsUsername);
    }
}
