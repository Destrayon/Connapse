namespace Connapse.Storage.Settings;

/// <summary>
/// Implementation of ISettingsReloader that reloads database-backed configuration.
/// This triggers IOptionsMonitor change notifications for all settings.
/// </summary>
public class SettingsReloader : ISettingsReloader
{
    private readonly DatabaseSettingsProvider _provider;

    public SettingsReloader(DatabaseSettingsProvider provider)
    {
        _provider = provider;
    }

    public bool Reload()
    {
        // Reload database settings and trigger change token
        // This will notify IOptionsMonitor subscribers
        return _provider.Reload();
    }
}
