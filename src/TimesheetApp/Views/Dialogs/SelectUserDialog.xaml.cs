using System.Collections.Generic;
using System.Windows;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Views.Dialogs;

/// <summary>
/// Modal picker for the current user when the Windows account is not mapped (XC-07). Given a list
/// of active users, returns the chosen one via <see cref="SelectedUser"/>; <c>DialogResult==true</c>
/// only when a user was selected OR created inline. On a fresh DB the list is empty, so the dialog
/// also lets the user type a new name and creates that user via <see cref="IUserRepository"/> on OK
/// (first-run unblock). WPF lives here, not in any service or VM.
/// </summary>
public partial class SelectUserDialog : Window
{
    private readonly IUserRepository _users;

    public SelectUserDialog(IReadOnlyList<User> activeUsers, IUserRepository users)
    {
        InitializeComponent();
        UsersList.ItemsSource = activeUsers;
        _users = users;
        DataContext = this;
    }

    public User? SelectedUser { get; set; }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        // Prefer an explicit list selection.
        if (SelectedUser is not null)
        {
            DialogResult = true;
            return;
        }

        // First-run path: no selection, but a new name was typed -> create + select that user.
        var name = NewUserNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return; // nothing chosen and nothing typed -> keep the dialog open
        }

        var id = await _users.InsertAsync(new User(0, name, null, true));
        SelectedUser = new User(id, name, null, true);
        DialogResult = true;
    }

    private void UsersList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedUser is null) return;
        DialogResult = true;
    }
}
