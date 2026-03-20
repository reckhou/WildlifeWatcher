using System.Windows.Controls;
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
        ApiKeyBox.PasswordChanged += (_, _) => viewModel.AnthropicApiKey = ApiKeyBox.Password;

        // Pre-populate from loaded credentials
        RtspPasswordBox.Password = viewModel.RtspPassword;
        ApiKeyBox.Password = viewModel.AnthropicApiKey;
    }
}
