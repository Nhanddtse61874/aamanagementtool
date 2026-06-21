using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public sealed class UsersViewModelTests
{
    private readonly Mock<IUserRepository> _users = new();
    private UsersViewModel CreateVm() => new(_users.Object);

    private static User U(int id, string name, bool active) => new(id, name, null, active);

    [Fact] // USR-01: list shows BOTH active and inactive users
    public async Task LoadAsync_shows_active_and_inactive()
    {
        _users.Setup(u => u.GetAllAsync())
            .ReturnsAsync(new[] { U(1, "Ann", true), U(2, "Bob", false) });
        var vm = CreateVm();

        await vm.LoadAsync();

        Assert.Equal(2, vm.Users.Count);
        Assert.Contains(vm.Users, u => u.Name == "Bob" && !u.IsActive);
    }

    [Fact] // USR-02: adding a user inserts an ACTIVE user and clears the input
    public async Task AddUserAsync_inserts_active_user()
    {
        _users.Setup(u => u.InsertAsync(It.IsAny<User>())).ReturnsAsync(3);
        _users.Setup(u => u.GetAllAsync()).ReturnsAsync(Array.Empty<User>());
        var vm = CreateVm();
        vm.NewUserName = "  Cara  ";

        await vm.AddUserAsync();

        _users.Verify(u => u.InsertAsync(
            It.Is<User>(x => x.Name == "Cara" && x.IsActive && x.WindowsUsername == null)), Times.Once);
        Assert.Equal(string.Empty, vm.NewUserName);
    }

    [Fact] // USR-02: blank name is a no-op (no insert)
    public async Task AddUserAsync_ignores_blank_name()
    {
        var vm = CreateVm();
        vm.NewUserName = "   ";

        await vm.AddUserAsync();

        _users.Verify(u => u.InsertAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact] // USR-03: deactivate calls SetActiveAsync(id,false) and refreshes
    public async Task DeactivateAsync_sets_inactive()
    {
        _users.Setup(u => u.GetAllAsync()).ReturnsAsync(Array.Empty<User>());
        var vm = CreateVm();

        await vm.DeactivateAsync(7);

        _users.Verify(u => u.SetActiveAsync(7, false), Times.Once);
        _users.Verify(u => u.GetAllAsync(), Times.Once); // refreshed after
    }
}
