using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsPath;
    private AppConfiguration _current;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        _settingsPath = Path.Combine(AppDataDir, "settings.json");
        _current = Load();
    }

    public AppConfiguration CurrentSettings => _current;

    public event EventHandler<AppConfiguration>? SettingsChanged;

    public void Save(AppConfiguration settings)
    {
        _current = settings;
        Directory.CreateDirectory(AppDataDir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
        _logger.LogInformation("Settings saved to {Path}", _settingsPath);
        SettingsChanged?.Invoke(this, _current);
    }

    private AppConfiguration Load()
    {
        if (!File.Exists(_settingsPath))
        {
            _logger.LogInformation("No settings file found; using defaults");
            return new AppConfiguration();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppConfiguration>(json) ?? new AppConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings; using defaults");
            return new AppConfiguration();
        }
    }

    private static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WildlifeWatcher");
}
