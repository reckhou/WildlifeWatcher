using System.Windows;
using WildlifeWatcher.ViewModels;

namespace WildlifeWatcher.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}