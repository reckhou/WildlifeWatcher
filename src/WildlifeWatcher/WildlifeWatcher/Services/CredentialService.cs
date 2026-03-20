using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class CredentialService : ICredentialService
{
    private readonly ILogger<CredentialService> _logger;
    private readonly string _credentialsPath;

    public CredentialService(ILogger<CredentialService> logger)
    {
        _logger = logger;
        _credentialsPath = Path.Combine(AppDataDir, "credentials.bin");
    }

    public Credentials? LoadCredentials()
    {
        if (!File.Exists(_credentialsPath))
        {
            _logger.LogInformation("No saved credentials found");
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(_credentialsPath);
            var plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<Credentials>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load credentials; file may be corrupted or from a different user profile");
            return null;
        }
    }

    public void SaveCredentials(string rtspUsername, string rtspPassword, string anthropicApiKey, string geminiApiKey = "")
    {
        Directory.CreateDirectory(AppDataDir);
        var credentials = new Credentials
        {
            RtspUsername    = rtspUsername,
            RtspPassword    = rtspPassword,
            AnthropicApiKey = anthropicApiKey,
            GeminiApiKey    = geminiApiKey
        };
        var json = JsonSerializer.Serialize(credentials);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_credentialsPath, encrypted);
        _logger.LogInformation("Credentials saved (DPAPI-encrypted)");
    }

    public void ClearCredentials()
    {
        if (File.Exists(_credentialsPath))
        {
            File.Delete(_credentialsPath);
            _logger.LogInformation("Credentials cleared");
        }
    }

    private static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WildlifeWatcher");
}
