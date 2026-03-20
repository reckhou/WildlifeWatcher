using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using WildlifeWatcher.Data;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services;
using WildlifeWatcher.Services.Interfaces;
using WildlifeWatcher.ViewModels;
using WildlifeWatcher.Views;
using WildlifeWatcher.Views.Pages;

namespace WildlifeWatcher;

public partial class App : Application
{
    private IHost _host = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WildlifeWatcher", "logs");

        var inMemorySink = new WildlifeWatcher.Services.InMemoryLogSink();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDir, "wildlife-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .WriteTo.Sink(inMemorySink)
            .CreateLogger();

        // Bootstrap settings to read the effective DB path before DI is configured
        var bootstrap = new SettingsService(NullLogger<SettingsService>.Instance);
        var dbPath    = bootstrap.CurrentSettings.GetEffectiveDatabasePath();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                services.AddDbContextFactory<WildlifeDbContext>(options =>
                    options.UseSqlite($"Data Source={dbPath}"));

                // Core services
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<ICredentialService, CredentialService>();

                // Camera (Phase 2)
                services.AddSingleton<ICameraService, RtspCameraService>();

                // AI recognition (Phase 3) — provider selected at startup
                if (bootstrap.CurrentSettings.AiProvider == AiProvider.Gemini)
                    services.AddSingleton<IAiRecognitionService, GeminiRecognitionService>();
                else
                    services.AddSingleton<IAiRecognitionService, ClaudeRecognitionService>();
                services.AddSingleton<IBackgroundModelService, BackgroundModelService>();
                services.AddSingleton<IMotionDetectionService, MotionDetectionService>();
                services.AddSingleton<IPointOfInterestService, PointOfInterestService>();
                services.AddSingleton<IRecognitionLoopService, RecognitionLoopService>();
                services.AddHostedService(sp =>
                    (RecognitionLoopService)sp.GetRequiredService<IRecognitionLoopService>());

                // Capture storage (Phase 4)
                services.AddSingleton<ICaptureStorageService, CaptureStorageService>();

                // Bird photo service (legacy singleton HttpClient — kept for BirdPhotoService)
                services.AddSingleton<HttpClient>();
                services.AddSingleton<IBirdPhotoService, BirdPhotoService>();

                // Named HTTP clients (Phase 6 — weather/geocoding; Phase 7 — update check)
                services.AddHttpClient("nominatim", c => {
                    c.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
                    c.DefaultRequestHeaders.UserAgent.ParseAdd("WildlifeWatcher/1.0");
                });
                services.AddHttpClient("openmeteo", c =>
                    c.BaseAddress = new Uri("https://api.open-meteo.com/"));
                services.AddHttpClient("github", c => {
                    c.BaseAddress = new Uri("https://api.github.com/");
                    c.DefaultRequestHeaders.UserAgent.ParseAdd("WildlifeWatcher");
                });

                // Weather & geocoding (Phase 6)
                services.AddSingleton<IGeocodingService, NominatimGeocodingService>();
                services.AddSingleton<IWeatherService, OpenMeteoWeatherService>();

                // Auto-update (Phase 7)
                services.AddSingleton<IUpdateService, UpdateService>();

                // ViewModels
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<LiveViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<GalleryViewModel>();

                // Pages (singletons — VideoView must not be recreated)
                services.AddSingleton<LiveViewPage>();
                services.AddSingleton<SettingsPage>();
                services.AddSingleton<GalleryPage>();

                // Main window
                services.AddSingleton<MainWindow>();
            })
            .Build();

        _host.Start();

        // Apply any pending EF Core migrations on startup
        var factory = _host.Services.GetRequiredService<IDbContextFactory<WildlifeDbContext>>();
        using var db = factory.CreateDbContext();
        db.Database.Migrate();

        // Merge any duplicate species that share the same ScientificName
        var captureStorage = _host.Services.GetRequiredService<ICaptureStorageService>();
        captureStorage.MergeSpeciesByScientificNameAsync().GetAwaiter().GetResult();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host.Services.GetRequiredService<IBackgroundModelService>().SaveState();
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
