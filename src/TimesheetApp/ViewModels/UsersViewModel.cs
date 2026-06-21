namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

public sealed partial class UsersViewModel : ObservableObject
{
    private readonly IUserRepository _users;

    public UsersViewModel(IUserRepository users) => _users = users;

    public ObservableCollection<User> Users { get; } = new();

    [ObservableProperty] private string _newUserName = string.Empty;

    public async Task LoadAsync()
    {
        var rows = await _users.GetAllAsync();   // includes inactive (USR-01)
        Users.Clear();
        foreach (var u in rows) Users.Add(u);
    }

    [RelayCommand]
    public async Task AddUserAsync()
    {
        var name = NewUserName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        await _users.InsertAsync(new User(0, name, null, true)); // active (USR-02)
        NewUserName = string.Empty;
        await LoadAsync();
    }

    [RelayCommand]
    public async Task DeactivateAsync(int userId)
    {
        await _users.SetActiveAsync(userId, false);  // soft-delete, TimeLogs preserved (USR-03)
        await LoadAsync();
    }
}
