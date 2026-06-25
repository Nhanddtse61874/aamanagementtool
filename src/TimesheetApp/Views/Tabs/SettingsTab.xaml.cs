namespace TimesheetApp.Views.Tabs;

using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.ViewModels;
using TimesheetApp.Views.Dialogs;

public partial class SettingsTab : UserControl
{
    public SettingsTab()
    {
        InitializeComponent();

        // AddTask reads from the NewTaskBox TextBox (AddTask(string) is not a RelayCommand).
        AddTaskFromBoxCommand = new RelayCommand(AddTaskFromBox);

        // Task row action commands: RemoveTask/MoveUp/MoveDown on TemplateEditorViewModel are plain
        // methods (not [RelayCommand]-decorated), so they are wrapped here for XAML binding
        // (mirrors RequestsTab.xaml.cs).
        RemoveTaskCommand = new RelayCommand<TemplateTaskRowVm>(row =>
        {
            if (row is not null) Vm?.TemplateEditor?.RemoveTask(row);
        });
        MoveUpCommand = new RelayCommand<TemplateTaskRowVm>(row =>
        {
            if (row is not null) Vm?.TemplateEditor?.MoveUp(row);
        });
        MoveDownCommand = new RelayCommand<TemplateTaskRowVm>(row =>
        {
            if (row is not null) Vm?.TemplateEditor?.MoveDown(row);
        });
    }

    // Commands exposed to XAML via RelativeSource AncestorType=UserControl.
    public IRelayCommand AddTaskFromBoxCommand { get; }
    public IRelayCommand<TemplateTaskRowVm> RemoveTaskCommand { get; }
    public IRelayCommand<TemplateTaskRowVm> MoveUpCommand { get; }
    public IRelayCommand<TemplateTaskRowVm> MoveDownCommand { get; }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    // Dialog ownership is a View concern — services stay WPF-free (spec §4).
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new OpenFileDialog
        {
            Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            CheckFileExists = false
        };
        if (dlg.ShowDialog() == true)
            vm.DbPath = dlg.FileName;
    }

    // "Add task" opens a dedicated input dialog instead of an inline text box.
    private void AddTaskFromBox()
    {
        if (Vm?.TemplateEditor is null) return;
        var dlg = new TaskInputDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.TaskName is { } name)
            Vm.TemplateEditor.AddTask(name);
    }
}
