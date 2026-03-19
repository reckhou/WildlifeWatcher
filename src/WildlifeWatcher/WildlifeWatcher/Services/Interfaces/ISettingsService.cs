using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface ISettingsService
{
    AppConfiguration CurrentSettings { get; }
    void Save(AppConfiguration settings);
}
