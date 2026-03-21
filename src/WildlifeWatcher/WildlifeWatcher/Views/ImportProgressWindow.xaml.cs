using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Views;

public partial class ImportProgressWindow : Window
{
    private readonly IDataPortService _service;
    private readonly string _zipPath;
    private readonly string _preserveRtspUrl;
    private bool _importComplete;

    public ImportProgressWindow(IDataPortService service, string zipPath, string preserveRtspUrl)
    {
        InitializeComponent();
        _service = service;
        _zipPath = zipPath;
        _preserveRtspUrl = preserveRtspUrl;
        Loaded += (_, _) => RunImportAsync();
    }

    private async void RunImportAsync()
    {
        var progress = new Progress<(int percent, string message)>(p =>
        {
            ProgressBar.Value = p.percent;
            StatusText.Text = p.message;
        });

        try
        {
            await _service.ImportAsync(_zipPath, _preserveRtspUrl, progress, CancellationToken.None);

            _importComplete = true;
            var psi = new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false };
            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusText.Visibility = Visibility.Collapsed;
            ErrorText.Text = $"Import failed: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
            CloseButton.Visibility = Visibility.Visible;
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // Block closing while import is in progress
        if (!_importComplete && CloseButton.Visibility != Visibility.Visible)
            e.Cancel = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
