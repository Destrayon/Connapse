namespace AIKnowledge.Storage.Settings;

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
    void Reload();
}
