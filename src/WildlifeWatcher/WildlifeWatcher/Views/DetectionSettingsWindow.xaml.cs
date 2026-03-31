using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WildlifeWatcher.Models;
using WildlifeWatcher.ViewModels;

namespace WildlifeWatcher.Views;

public partial class DetectionSettingsWindow : Window
{
    private bool _populatingPasswords;

    public DetectionSettingsWindow(DetectionSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // PasswordBox doesn't support data binding — wire manually.
        // Guard with _populatingPasswords so the initial population doesn't
        // fire saves (which would overwrite one key with empty string).
        ApiKeyBox.PasswordChanged += (_, _) =>
        {
            if (!_populatingPasswords)
                viewModel.SaveApiCredentials(ApiKeyBox.Password, GeminiApiKeyBox.Password);
        };
        GeminiApiKeyBox.PasswordChanged += (_, _) =>
        {
            if (!_populatingPasswords)
                viewModel.SaveApiCredentials(ApiKeyBox.Password, GeminiApiKeyBox.Password);
        };

        // Pre-populate from loaded credentials
        _populatingPasswords = true;
        ApiKeyBox.Password       = viewModel.AnthropicApiKey;
        GeminiApiKeyBox.Password = viewModel.GeminiApiKey;
        _populatingPasswords = false;

        Loaded += async (_, _) =>
        {
            var maxH = SystemParameters.WorkArea.Height - 40;
            MaxHeight = maxH;
            if (Height > maxH) Height = maxH;
            await viewModel.AutoCaptureZoneBackgroundAsync();
        };
    }

    // Hide instead of close so the window can be re-opened without re-creating
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    // ── Zone canvas rubber-band drawing ──────────────────────────────────

    private System.Windows.Point _dragStart;
    private System.Windows.Shapes.Rectangle? _rubberBand;

    private void ZoneCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var canvas = (Canvas)sender;
        _dragStart = e.GetPosition(canvas);
        _rubberBand = new System.Windows.Shapes.Rectangle
        {
            Stroke          = System.Windows.Media.Brushes.Yellow,
            StrokeThickness = 2,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 },
            Fill            = System.Windows.Media.Brushes.Transparent
        };
        Canvas.SetLeft(_rubberBand, _dragStart.X);
        Canvas.SetTop (_rubberBand, _dragStart.Y);
        canvas.Children.Add(_rubberBand);
        canvas.CaptureMouse();
        e.Handled = true;
    }

    private void ZoneCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_rubberBand == null) return;
        var canvas = (Canvas)sender;
        var pos = e.GetPosition(canvas);
        _rubberBand.Width  = Math.Abs(pos.X - _dragStart.X);
        _rubberBand.Height = Math.Abs(pos.Y - _dragStart.Y);
        Canvas.SetLeft(_rubberBand, Math.Min(_dragStart.X, pos.X));
        Canvas.SetTop (_rubberBand, Math.Min(_dragStart.Y, pos.Y));
    }

    private void ZoneCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_rubberBand == null) return;
        var canvas = (Canvas)sender;
        canvas.ReleaseMouseCapture();
        double w = _rubberBand.Width, h = _rubberBand.Height;
        if (w > 8 && h > 8)
        {
            var zone = new MotionZone(
                Canvas.GetLeft(_rubberBand) / canvas.ActualWidth,
                Canvas.GetTop (_rubberBand) / canvas.ActualHeight,
                w / canvas.ActualWidth,
                h / canvas.ActualHeight);
            ((DetectionSettingsViewModel)DataContext).AddZone(zone);
        }
        canvas.Children.Remove(_rubberBand);
        _rubberBand = null;
    }
}
