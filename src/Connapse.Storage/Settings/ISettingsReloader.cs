namespace Connapse.Storage.Settings;

/// <summary>
/// Service for triggering configuration reload after settings changes.
/// This notifies IOptionsMonitor subscribers that settings have been updated.
/// </summary>
public interface ISettingsReloader
{
    /// <summary>
    /// Reloads settings from the database and triggers change notifications.
    /// Call this after updating settings to propagate changes to IOptionsMonitor.
    /// </summary>
    /// <returns>
    /// True when the database was read. False when it could not be reached or the read failed, in
    /// which case the merged configuration still holds whatever was loaded before — possibly only
    /// appsettings defaults — and must not be trusted as the authoritative stored state.
    /// </returns>
    bool Reload();
}
