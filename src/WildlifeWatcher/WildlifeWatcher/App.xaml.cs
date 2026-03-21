using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var msg = args.ExceptionObject?.ToString() ?? "Unknown error";
            Log.Fatal("Unhandled domain exception: {Error}", msg);
            Log.CloseAndFlush();
            MessageBox.Show(msg, "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled dispatcher exception");
            Log.CloseAndFlush();
            MessageBox.Show(args.Exception.Message, "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };

        // Import mode: launched by the running app after it exits, so files are no longer locked
        var importJobArg = e.Args.FirstOrDefault(a => a.StartsWith("--import-job="));
        if (importJobArg != null)
        {
            RunImportMode(importJobArg["--import-job=".Length..]);
            return; // Skip normal host startup
        }

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WildlifeWatcher", "logs");

        var inMemorySink = new WildlifeWatcher.Services.InMemoryLogSink();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(logDir, "wildlife-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .WriteTo.Sink(inMemorySink)
            .CreateLogger();

        // Show splash immediately before any heavy work
        var splash = new SplashWindow();
        splash.Show();

        base.OnStartup(e);

        // Run all heavy startup work on a background thread, then show main window
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                // Bootstrap settings to read the effective DB path before DI is configured
                splash.SetStatus("Initialising…");
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
                        services.AddSingleton<IDataPortService, DataPortService>();

                        // Camera (Phase 2)
                        services.AddSingleton<ICameraService, RtspCameraService>();

                        // AI recognition (Phase 3) — provider resolved at runtime via settings
                        services.AddSingleton<ClaudeRecognitionService>();
                        services.AddSingleton<GeminiRecognitionService>();
                        services.AddSingleton<IAiRecognitionService, AiRecognitionServiceResolver>();
                        services.AddSingleton<IBackgroundModelService, BackgroundModelService>();
                        services.AddSingleton<IPointOfInterestService, PointOfInterestService>();
                        services.AddSingleton<IRecognitionLoopService, RecognitionLoopService>();
                        services.AddHostedService(sp =>
                            (RecognitionLoopService)sp.GetRequiredService<IRecognitionLoopService>());

                        // Capture storage (Phase 4)
                        services.AddSingleton<ICaptureStorageService, CaptureStorageService>();

                        // Bird photo service
                        services.AddSingleton<IBirdPhotoService, BirdPhotoService>();

                        // Named HTTP clients
                        services.AddHttpClient("inaturalist", c => {
                            c.BaseAddress = new Uri("https://api.inaturalist.org/");
                            c.DefaultRequestHeaders.UserAgent.ParseAdd("WildlifeWatcher/1.0");
                        });
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

                splash.SetStatus("Starting services…");
                await Task.Run(() => _host.Start());

                // Apply any pending EF Core migrations on startup
                splash.SetStatus("Checking database…");
                await Task.Run(() =>
                {
                    var factory = _host.Services.GetRequiredService<IDbContextFactory<WildlifeDbContext>>();
                    using var db = factory.CreateDbContext();
                    db.Database.Migrate();
                });

                // Merge any duplicate species that share the same ScientificName
                splash.SetStatus("Preparing gallery…");
                var captureStorage = _host.Services.GetRequiredService<ICaptureStorageService>();
                await captureStorage.MergeSpeciesByScientificNameAsync();

                var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                mainWindow.Show();
                splash.Close();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Startup failed");
                Log.CloseAndFlush();
                MessageBox.Show(ex.Message, "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                splash.Close();
                Shutdown(1);
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // _host is null in import mode — guard before accessing it
        if (_host != null)
        {
            _host.Services.GetRequiredService<IBackgroundModelService>().SaveState();
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void RunImportMode(string jobPath)
    {
        try
        {
            var job = JsonSerializer.Deserialize<ImportJob>(File.ReadAllText(jobPath))
                ?? throw new InvalidOperationException("Invalid import job file.");
            try { File.Delete(jobPath); } catch { /* best effort */ }

            // Wait for the main process to fully exit so its DB lock is released
            if (job.MainProcessId > 0)
            {
                try
                {
                    using var mainProcess = Process.GetProcessById(job.MainProcessId);
                    mainProcess.WaitForExit(15_000);
                }
                catch (ArgumentException) { /* already exited */ }
            }

            // Build minimal services — no hosted services, no camera, no AI
            var settingsService = new SettingsService(NullLogger<SettingsService>.Instance);
            var dbPath = settingsService.CurrentSettings.GetEffectiveDatabasePath();
            var dbOptions = new DbContextOptionsBuilder<WildlifeDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            var dataPortService = new DataPortService(
                settingsService,
                new SimpleDbContextFactory(dbOptions),
                NullLogger<DataPortService>.Instance);

            var window = new ImportProgressWindow(dataPortService, job.ZipPath, job.PreserveRtspUrl);
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start import: {ex.Message}", "Import Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    // Minimal IDbContextFactory used only in import mode (export path is not called during import)
    private sealed class SimpleDbContextFactory : IDbContextFactory<WildlifeDbContext>
    {
        private readonly DbContextOptions<WildlifeDbContext> _opts;
        public SimpleDbContextFactory(DbContextOptions<WildlifeDbContext> opts) => _opts = opts;
        public WildlifeDbContext CreateDbContext() => new(_opts);
        public Task<WildlifeDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(new WildlifeDbContext(_opts));
    }
}
