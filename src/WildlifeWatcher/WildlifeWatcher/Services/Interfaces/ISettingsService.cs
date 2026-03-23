using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface ISettingsService
{
    AppConfiguration CurrentSettings { get; }
    void Save(AppConfiguration settings);

    /// <summary>Fired after Save() is called, with the new settings.</summary>
    event EventHandler<AppConfiguration>? SettingsChanged;
}
