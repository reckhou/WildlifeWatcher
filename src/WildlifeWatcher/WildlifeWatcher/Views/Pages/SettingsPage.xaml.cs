using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WildlifeWatcher.ViewModels;

namespace WildlifeWatcher.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // PasswordBox doesn't support data binding — wire manually
        RtspPasswordBox.PasswordChanged += (_, _) => viewModel.RtspPassword = RtspPasswordBox.Password;

        // Pre-populate from loaded credentials
        RtspPasswordBox.Password = viewModel.RtspPassword;
    }
}
