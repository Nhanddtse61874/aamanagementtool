namespace TimesheetApp.Views.Tabs;

using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.ViewModels;

public partial class RequestsTab : UserControl
{
    public RequestsTab()
    {
        InitializeComponent();

        // Save routes create vs edit based on Editor.IsEditMode (glue: VM is dialog-free per spec §5).
        SaveCommand = new AsyncRelayCommand(SaveAsync);

        // AddTask reads from the NewTaskBox TextBox (AddTask(string) is not a RelayCommand).
        AddTaskFromBoxCommand = new RelayCommand(AddTaskFromBox);

        // Task row action commands: RemoveTask/MoveUp/MoveDown on RequestEditorViewModel are plain
        // methods (not [RelayCommand]-decorated), so they are wrapped here for XAML binding.
        RemoveTaskCommand = new RelayCommand<EditableTaskRowVm>(row =>
        {
            if (row is not null) Vm?.Editor?.RemoveTask(row);
        });
        MoveUpCommand = new RelayCommand<EditableTaskRowVm>(row =>
        {
            if (row is not null) Vm?.Editor?.MoveUp(row);
        });
        MoveDownCommand = new RelayCommand<EditableTaskRowVm>(row =>
        {
            if (row is not null) Vm?.Editor?.MoveDown(row);
        });
    }

    // Commands exposed to XAML via RelativeSource AncestorType=UserControl.
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand AddTaskFromBoxCommand { get; }
    public IRelayCommand<EditableTaskRowVm> RemoveTaskCommand { get; }
    public IRelayCommand<EditableTaskRowVm> MoveUpCommand { get; }
    public IRelayCommand<EditableTaskRowVm> MoveDownCommand { get; }

    private RequestsViewModel? Vm => DataContext as RequestsViewModel;

    private async System.Threading.Tasks.Task SaveAsync()
    {
        if (Vm?.Editor is null) return;
        if (Vm.Editor.IsEditMode)
            await Vm.SaveEditAsync();
        else
            await Vm.SaveNewAsync();
    }

    private void AddTaskFromBox()
    {
        if (Vm?.Editor is null) return;
        Vm.Editor.AddTask(NewTaskBox.Text);
        NewTaskBox.Clear();
    }
}
