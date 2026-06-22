namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

public sealed partial class UsersViewModel : ObservableObject
{
    private readonly IUserRepository _users;
    private readonly IMessenger _messenger;

    public UsersViewModel(IUserRepository users, IMessenger? messenger = null)
    {
        _users = users;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

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
        _messenger.Send(new DataChangedMessage(DataKind.Users)); // live-sync: Reports refresh
    }

    [RelayCommand]
    public async Task DeactivateAsync(int userId)
    {
        await _users.SetActiveAsync(userId, false);  // soft-delete, TimeLogs preserved (USR-03)
        await LoadAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Users)); // live-sync: Reports refresh
    }
}
