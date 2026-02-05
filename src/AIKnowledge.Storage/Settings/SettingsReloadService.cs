using Microsoft.Extensions.Configuration;

namespace AIKnowledge.Storage.Settings;

/// <summary>
/// Service for triggering configuration reload after settings are updated.
/// This ensures IOptionsMonitor subscribers receive change notifications.
/// </summary>
public class SettingsReloadService
{
    private readonly IConfiguration _configuration;

    public SettingsReloadService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Reloads settings from the database and triggers change tokens.
    /// Call this after updating settings via ISettingsStore.
    /// </summary>
    public void ReloadSettings()
    {
        // Find the DatabaseSettingsProvider in the configuration
        var configRoot = _configuration as IConfigurationRoot;
        if (configRoot is null)
            return;

        foreach (var provider in configRoot.Providers)
        {
            if (provider is DatabaseSettingsProvider dbProvider)
            {
                dbProvider.Reload();
                break;
            }
        }
    }
}
