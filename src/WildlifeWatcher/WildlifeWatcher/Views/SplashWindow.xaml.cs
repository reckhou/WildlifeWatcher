using System.Windows;

namespace WildlifeWatcher.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string text)
    {
        Dispatcher.Invoke(() => StatusText.Text = text);
    }
}
