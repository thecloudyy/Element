using System.Windows.Controls;
using ElementGui.ViewModels;

namespace ElementGui.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView(DownloadsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
