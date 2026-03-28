using System.Windows;
using WildlifeWatcher.ViewModels;
using WildlifeWatcher.Views.Pages;

namespace WildlifeWatcher.Views;

public partial class MainWindow : Window
{
    private readonly LiveViewPage             _liveViewPage;
    private readonly SettingsPage             _settingsPage;
    private readonly GalleryPage              _galleryPage;
    private readonly DetectionSettingsWindow  _detectionSettingsWindow;

    public MainWindow(
        MainViewModel viewModel,
        LiveViewPage liveViewPage,
        SettingsPage settingsPage,
        GalleryPage galleryPage,
        DetectionSettingsWindow detectionSettingsWindow)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Set window icon from embedded resource (avoids WPF pack-URI case-sensitivity crash)
        try
        {
            var iconUri = new Uri("pack://application:,,,/Resources/AppIcon.ico");
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
        }
        catch { /* non-fatal — app still runs without custom icon */ }

        _liveViewPage            = liveViewPage;
        _settingsPage            = settingsPage;
        _galleryPage             = galleryPage;
        _detectionSettingsWindow = detectionSettingsWindow;

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage))
                UpdateContentArea(viewModel.CurrentPage);
        };

        viewModel.OpenDetectionSettingsRequested += OpenDetectionSettings;

        UpdateContentArea(viewModel.CurrentPage);

        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            MaxWidth  = work.Width;
            MaxHeight = work.Height;
            if (Width  > work.Width)  Width  = work.Width;
            if (Height > work.Height) Height = work.Height;
        };
    }

    private void UpdateContentArea(AppPage page)
    {
        PageContentControl.Content = page switch
        {
            AppPage.LiveView => (object)_liveViewPage,
            AppPage.Settings => _settingsPage,
            _ => _galleryPage
        };
    }

    private void OpenDetectionSettings()
    {
        _detectionSettingsWindow.Owner = this;
        _detectionSettingsWindow.Show();
        _detectionSettingsWindow.Activate();
    }
}
