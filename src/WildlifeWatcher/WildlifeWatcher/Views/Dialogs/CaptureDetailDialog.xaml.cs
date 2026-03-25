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
    private string? _initialImagePath; // deferred until Loaded so the dialog opens without freezing

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

        // Determine which image to show, but DON'T decode it here — that blocks the UI thread
        // before ShowDialog returns. Instead, load it async after the window is visible.
        if (!string.IsNullOrEmpty(record.AnnotatedImageFilePath) &&
            File.Exists(record.AnnotatedImageFilePath))
        {
            ToggleAnnotatedButton.Visibility = Visibility.Visible;
            _showingAnnotated = true;
            _initialImagePath = record.AnnotatedImageFilePath;
            ToggleAnnotatedButton.Content = "View Original";
        }
        else if (File.Exists(record.ImageFilePath))
        {
            _initialImagePath = record.ImageFilePath;
        }

        Loaded += async (_, _) =>
        {
            if (_initialImagePath != null)
                await LoadImageAsync(_initialImagePath);
        };
    }

    // Decodes the image on a background thread so the UI thread is never blocked.
    private async Task LoadImageAsync(string path)
    {
        try
        {
            var bmp = await Task.Run(() =>
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource        = new Uri(path, UriKind.Absolute);
                img.CacheOption      = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 800;
                img.EndInit();
                img.Freeze();
                return img;
            });
            CaptureImage.Source = bmp;
        }
        catch { /* image missing or corrupt — leave placeholder empty */ }
    }

    private void FullSize_Click(object sender, RoutedEventArgs e)
    {
        var path = _showingAnnotated && !string.IsNullOrEmpty(_record.AnnotatedImageFilePath)
            ? _record.AnnotatedImageFilePath
            : _record.ImageFilePath;

        if (File.Exists(path))
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private async void ToggleAnnotated_Click(object sender, RoutedEventArgs e)
    {
        _showingAnnotated = !_showingAnnotated;

        if (_showingAnnotated && !string.IsNullOrEmpty(_record.AnnotatedImageFilePath) &&
            File.Exists(_record.AnnotatedImageFilePath))
        {
            await LoadImageAsync(_record.AnnotatedImageFilePath);
            ToggleAnnotatedButton.Content = "View Original";
        }
        else
        {
            await LoadImageAsync(_record.ImageFilePath);
            ToggleAnnotatedButton.Content = "View Annotated";
        }
    }

    private async void SaveNotes_Click(object sender, RoutedEventArgs e)
    {
        await _storage.UpdateCaptureNotesAsync(_record.Id, NotesBox.Text);
        Title = _record.Species.CommonName + " ✓";
    }

    private async void Reassign_Click(object sender, RoutedEventArgs e)
    {
        var allSpecies = await _storage.GetAllSpeciesAsync();
        var dialog = new ReassignSpeciesDialog(allSpecies) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedSpeciesId.HasValue)
        {
            await _storage.ReassignCaptureAsync(_record.Id, dialog.SelectedSpeciesId.Value);
            DialogResult = true;
            Close();
        }
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
