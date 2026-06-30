namespace TimesheetApp.Views.Controls;

using System.Windows;

/// Lets a DataGridColumn (which lives outside the visual tree, so it can't inherit DataContext) bind to
/// the owning VM. Place one in Resources with Data="{Binding}", then bind a column's property to
/// "Data.X" via Source={StaticResource ...}. Used to hide the TEAM column when only one team is selected.
public sealed class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy),
            new UIPropertyMetadata(null));
}
