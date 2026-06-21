using System.Collections.Generic;
using System.Windows;
using TimesheetApp.Models;

namespace TimesheetApp.Views.Dialogs;

/// <summary>
/// Modal picker for the current user when the Windows account is not mapped (XC-07). Given a list
/// of active users, returns the chosen one via <see cref="SelectedUser"/>; <c>DialogResult==true</c>
/// only when a user was actually selected. WPF lives here, not in any service or VM.
/// </summary>
public partial class SelectUserDialog : Window
{
    public SelectUserDialog(IReadOnlyList<User> activeUsers)
    {
        InitializeComponent();
        UsersList.ItemsSource = activeUsers;
        DataContext = this;
    }

    public User? SelectedUser { get; set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUser is null) return; // require a selection before closing OK
        DialogResult = true;
    }

    private void UsersList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedUser is null) return;
        DialogResult = true;
    }
}
