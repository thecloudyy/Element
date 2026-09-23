using System.Windows.Controls;
using ElementGui.ViewModels;

namespace ElementGui.Views;

public partial class CloudView : UserControl
{
    public CloudView(CloudViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
