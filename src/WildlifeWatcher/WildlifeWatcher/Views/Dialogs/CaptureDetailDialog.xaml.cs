using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Views.Dialogs;

public partial class CaptureDetailDialog : Window
{
    private readonly ICaptureStorageService _storage;
    private readonly CaptureRecord          _record;
    private bool _showingAnnotated;

    public CaptureDetailDialog(CaptureRecord record, ICaptureStorageService storage)
    {
        InitializeComponent();
        _storage = storage;
        _record  = record;

        Title                    = record.Species.CommonName;
        TitleTextBlock.Text      = $"{record.Species.CommonName} — {record.CapturedAt:d MMM yyyy  HH:mm:ss}";
        ConfidenceTextBlock.Text = $"Confidence: {record.ConfidenceScore:P0}";
        NotesBox.Text            = record.Notes ?? string.Empty;

        // Weather
        if (!string.IsNullOrEmpty(record.WeatherCondition))
        {
            WeatherPanel.Visibility = Visibility.Visible;
            var w = record.WeatherCondition;
            if (record.Temperature.HasValue)  w += $" • {record.Temperature:F1}°C";
            if (record.WindSpeed.HasValue)    w += $" • {record.WindSpeed:F0} km/h wind";
            if (record.Precipitation.HasValue) w += $" • {record.Precipitation:F1}mm rain";
            WeatherTextBlock.Text = w;
        }

        if (record.Sunrise.HasValue)
        {
            SunrisePanel.Visibility = Visibility.Visible;
            SunriseTextBlock.Text   = $"Sunrise: {record.Sunrise:HH:mm}   Sunset: {record.Sunset:HH:mm}";
        }

        // Alternatives text
        if (!string.IsNullOrEmpty(record.AlternativesJson))
        {
            try
            {
                var alts = JsonSerializer.Deserialize<List<SpeciesCandidate>>(record.AlternativesJson);
                if (alts is { Count: > 0 })
                {
                    AlternativesTextBlock.Text =
                        "Also considered: " +
                        string.Join(", ", alts.Select(a => $"{a.CommonName} ({a.Confidence:P0})"));
                }
            }
            catch { /* ignore malformed JSON */ }
        }

        // Show annotated toggle if annotated file exists
        if (!string.IsNullOrEmpty(record.AnnotatedImageFilePath) &&
            File.Exists(record.AnnotatedImageFilePath))
        {
            ToggleAnnotatedButton.Visibility = Visibility.Visible;
            _showingAnnotated = true;
            LoadImage(record.AnnotatedImageFilePath);
            ToggleAnnotatedButton.Content = "View Original";
        }
        else if (File.Exists(record.ImageFilePath))
        {
            LoadImage(record.ImageFilePath);
        }
    }

    private void LoadImage(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource        = new Uri(path, UriKind.Absolute);
        img.CacheOption      = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = 800;
        img.EndInit();
        img.Freeze();
        CaptureImage.Source = img;
    }

    private void FullSize_Click(object sender, RoutedEventArgs e)
    {
        var path = _showingAnnotated && !string.IsNullOrEmpty(_record.AnnotatedImageFilePath)
            ? _record.AnnotatedImageFilePath
            : _record.ImageFilePath;

        if (File.Exists(path))
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void ToggleAnnotated_Click(object sender, RoutedEventArgs e)
    {
        _showingAnnotated = !_showingAnnotated;

        if (_showingAnnotated && !string.IsNullOrEmpty(_record.AnnotatedImageFilePath) &&
            File.Exists(_record.AnnotatedImageFilePath))
        {
            LoadImage(_record.AnnotatedImageFilePath);
            ToggleAnnotatedButton.Content = "View Original";
        }
        else
        {
            LoadImage(_record.ImageFilePath);
            ToggleAnnotatedButton.Content = "View Annotated";
        }
    }

    private async void SaveNotes_Click(object sender, RoutedEventArgs e)
    {
        await _storage.UpdateCaptureNotesAsync(_record.Id, NotesBox.Text);
        Title = _record.Species.CommonName + " ✓";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Delete this capture permanently?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await _storage.DeleteCaptureAsync(_record.Id);
            DialogResult = true;
            Close();
        }
    }
}
