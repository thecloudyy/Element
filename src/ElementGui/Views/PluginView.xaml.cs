using System.Windows.Controls;
using ElementGui.ViewModels;

namespace ElementGui.Views;

public partial class PluginView : UserControl
{
    public PluginView(PluginViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }
}
