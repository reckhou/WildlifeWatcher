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
        RtspPasswordBox.PasswordChanged  += (_, _) => viewModel.RtspPassword    = RtspPasswordBox.Password;
        ApiKeyBox.PasswordChanged        += (_, _) => viewModel.AnthropicApiKey = ApiKeyBox.Password;
        GeminiApiKeyBox.PasswordChanged  += (_, _) => viewModel.GeminiApiKey    = GeminiApiKeyBox.Password;

        // Pre-populate from loaded credentials
        RtspPasswordBox.Password  = viewModel.RtspPassword;
        ApiKeyBox.Password        = viewModel.AnthropicApiKey;
        GeminiApiKeyBox.Password  = viewModel.GeminiApiKey;
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
            var zone = new Models.MotionZone(
                Canvas.GetLeft(_rubberBand) / canvas.ActualWidth,
                Canvas.GetTop (_rubberBand) / canvas.ActualHeight,
                w / canvas.ActualWidth,
                h / canvas.ActualHeight);
            ((SettingsViewModel)DataContext).AddZone(zone);
        }
        canvas.Children.Remove(_rubberBand);
        _rubberBand = null;
    }
}
