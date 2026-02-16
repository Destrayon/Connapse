namespace AIKnowledge.Storage.Settings;

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

    public void Reload()
    {
        // Reload database settings and trigger change token
        // This will notify IOptionsMonitor subscribers
        _provider.Reload();
    }
}
