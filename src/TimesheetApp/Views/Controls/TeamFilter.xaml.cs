using System.Windows.Controls;

namespace TimesheetApp.Views.Controls;

// P10 W7: shared multi-team checkbox filter (TM-07). DataContext = TeamFilterViewModel; all behavior
// lives in the VM. Code-behind is the XAML partial only.
public partial class TeamFilter : UserControl
{
    public TeamFilter()
    {
        InitializeComponent();
    }
}
