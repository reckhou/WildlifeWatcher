using System.IO;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WildlifeWatcher.Data;
using WildlifeWatcher.Services;
using WildlifeWatcher.Services.Interfaces;
using WildlifeWatcher.ViewModels;
using WildlifeWatcher.Views;
using WildlifeWatcher.Models;

namespace WildlifeWatcher;

public partial class App : Application
{
    private IHost _host = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WildlifeWatcher", "logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDir, "wildlife-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                var dbPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WildlifeWatcher", "wildlife.db");

                services.AddDbContext<WildlifeDbContext>(options =>
                    options.UseSqlite($"Data Source={dbPath}"));

                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<ICredentialService, CredentialService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        _host.Start();

        // Apply any pending EF Core migrations on startup
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WildlifeDbContext>();
        db.Database.Migrate();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

