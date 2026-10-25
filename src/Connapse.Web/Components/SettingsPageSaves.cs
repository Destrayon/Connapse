using Connapse.Application.Ports;
using Connapse.Application.Settings;
using Microsoft.AspNetCore.Components.Authorization;

namespace Connapse.Web.Components;

/// <summary>
/// The Settings page's saves. It remembers each category as the page opened it, and as last saved, so a save
/// sends only the keys changed on the page since: a key changed elsewhere meanwhile is kept, and one the page
/// emptied is cleared, as on a provider switch.
/// </summary>
public sealed class SettingsPageSaves(UpdateSettings update, IConfiguredSettings configured, AuthenticationStateProvider authentication)
{
    private readonly Dictionary<string, object> saved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The category as configuration and the stored settings give it now, remembered as opened.</summary>
    public T Open<T>(string category) where T : class, new()
    {
        var current = configured.Current<T>();
        saved[category] = current;
        return current;
    }

    /// <summary>
    /// Saves a category through settings.update, as PUT /api/settings/{category} does: the category's rules, its
    /// stored keys and the embedding index all apply. Whether it saved, and what the page says.
    /// </summary>
    public async Task<(bool Saved, string Message)> SaveAsync(string category, object settings)
    {
        var body = SettingsCategories.Find(category)!.BodyFrom(settings, saved[category]);
        var result = await update.RunAsync(await authentication.OperationContextAsync(), category, body, UnsentSecrets.Keep);
        if (!result.Succeeded)
            return (false, $"Failed to save {category} settings: {result.Detail}");
        saved[category] = settings;
        return (true, $"{category} settings saved successfully!");
    }
}
