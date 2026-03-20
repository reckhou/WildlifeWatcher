using System.Windows.Controls;
using WildlifeWatcher.ViewModels;

namespace WildlifeWatcher.Views.Pages;

public partial class LiveViewPage : UserControl
{
    public LiveViewPage(LiveViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
