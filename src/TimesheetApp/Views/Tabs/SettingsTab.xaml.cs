namespace TimesheetApp.Views.Tabs;

using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Services;
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

        // TAG-01: icon/color quick-pick rows write into the open tag editor (plain VM setters).
        PickIconCommand = new RelayCommand<string>(glyph =>
        {
            if (Vm?.TagEditor is { } ed && glyph is not null) ed.Icon = glyph;
        });
        PickColorCommand = new RelayCommand<string>(hex =>
        {
            if (Vm?.TagEditor is { } ed && hex is not null) ed.Color = hex;
        });
    }

    // Commands exposed to XAML via RelativeSource AncestorType=UserControl.
    public IRelayCommand AddTaskFromBoxCommand { get; }
    public IRelayCommand<TemplateTaskRowVm> RemoveTaskCommand { get; }
    public IRelayCommand<TemplateTaskRowVm> MoveUpCommand { get; }
    public IRelayCommand<TemplateTaskRowVm> MoveDownCommand { get; }
    public IRelayCommand<string> PickIconCommand { get; }
    public IRelayCommand<string> PickColorCommand { get; }

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

    // Browse folder for daily report archive path.
    private void OnBrowseArchive(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new OpenFolderDialog { Title = "Select archive folder" };
        if (!string.IsNullOrWhiteSpace(vm.ArchivePath) && System.IO.Directory.Exists(vm.ArchivePath))
            dlg.InitialDirectory = vm.ArchivePath;
        if (dlg.ShowDialog() == true)
            vm.ArchivePath = dlg.FolderName;
    }

    // EX-01: pick the shared/SharePoint export root (View concern — service stays WPF-free).
    private void OnBrowseExportRoot1(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new OpenFolderDialog { Title = "Select shared/SharePoint export folder" };
        if (!string.IsNullOrWhiteSpace(vm.ExportRoot1Path) && System.IO.Directory.Exists(vm.ExportRoot1Path))
            dlg.InitialDirectory = vm.ExportRoot1Path;
        if (dlg.ShowDialog() == true)
            vm.ExportRoot1Path = dlg.FolderName;
    }

    // EX-01: pick the local export root.
    private void OnBrowseExportRoot2(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new OpenFolderDialog { Title = "Select local export folder" };
        if (!string.IsNullOrWhiteSpace(vm.ExportRoot2Path) && System.IO.Directory.Exists(vm.ExportRoot2Path))
            dlg.InitialDirectory = vm.ExportRoot2Path;
        if (dlg.ShowDialog() == true)
            vm.ExportRoot2Path = dlg.FolderName;
    }

    // BK-01: pick the backup folder (View concern — service stays WPF-free).
    private void OnBrowseBackupFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new OpenFolderDialog { Title = "Select backup folder" };
        if (!string.IsNullOrWhiteSpace(vm.BackupFolder) && System.IO.Directory.Exists(vm.BackupFolder))
            dlg.InitialDirectory = vm.BackupFolder;
        if (dlg.ShowDialog() == true)
            vm.BackupFolder = dlg.FolderName;
    }

    // BK-05: confirm before restoring, then offer to restart. Confirmation/restart are View concerns.
    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is not Button { CommandParameter: BackupInfo backup }) return;

        var confirm = MessageBox.Show(
            $"Restore the database from this backup?\n\n{System.IO.Path.GetFileName(backup.Path)}\n" +
            $"({backup.Timestamp:yyyy-MM-dd HH:mm:ss})\n\n" +
            "A safety copy of the current database is made first. The app must be restarted afterwards.",
            "Restore database", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        await Vm.RestoreCommand.ExecuteAsync(backup);

        var restart = MessageBox.Show(
            Vm.BackupStatus + "\n\nClose the app now?",
            "Restore database", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (restart == MessageBoxResult.Yes)
            Application.Current.Shutdown();
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
