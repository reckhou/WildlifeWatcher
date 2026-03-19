using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface ICredentialService
{
    Credentials? LoadCredentials();
    void SaveCredentials(string rtspUsername, string rtspPassword, string anthropicApiKey);
    void ClearCredentials();
}
