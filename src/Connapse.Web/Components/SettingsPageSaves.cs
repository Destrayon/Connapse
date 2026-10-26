using Connapse.Application.Ports;
using Connapse.Application.Settings;
using Microsoft.AspNetCore.Components.Authorization;

namespace Connapse.Web.Components;

/// <summary>
/// The Settings page's saves. It remembers each category as the page opened it, and as last saved, so a save
/// sends only the keys changed on the page since: a key changed elsewhere meanwhile is kept, and one the page
/// emptied is cleared, as on a provider switch. The page never holds a saved key, only which ones are saved,
/// since whatever it holds is rendered into the browser (#951).
/// </summary>
public sealed class SettingsPageSaves(UpdateSettings update, IConfiguredSettings configured, AuthenticationStateProvider authentication)
{
    /// <summary>What a key field shows while it is blank and a key is saved for it.</summary>
    public const string KeptPlaceholder = "A key is saved. Type a new one to replace it.";

    private readonly Dictionary<string, object> saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> savedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The category as configuration and the stored settings give it now, with its keys left out, remembered
    /// as opened; <see cref="SavedKeys"/> says which keys are saved.
    /// </summary>
    public T Open<T>(string category) where T : class, new()
    {
        var found = SettingsCategories.Find(category)!;
        var current = configured.Current<T>();
        savedKeys[category] = found.Secrets.Where(s => !string.IsNullOrEmpty((string?)s.GetValue(current))).Select(s => s.Name).ToHashSet();
        var opened = (T)found.WithoutSecrets(current);
        saved[category] = opened;
        return opened;
    }

    /// <summary>The keys saved for a category the page opened, by property name.</summary>
    public IReadOnlySet<string> SavedKeys(string category) => savedKeys[category];

    /// <summary>
    /// What a key field holding <paramref name="value"/> shows: that a key is saved while it is left blank and
    /// any of <paramref name="keys"/> is saved (a provider's own key, or the shared one it falls back on), or
    /// <paramref name="otherwise"/>.
    /// </summary>
    public static string? Placeholder(IReadOnlySet<string> savedKeys, string? value, string? otherwise, params string[] keys) =>
        value is null && keys.Any(savedKeys.Contains) ? KeptPlaceholder : otherwise;

    /// <summary>
    /// Saves a category through settings.update, as PUT /api/settings/{category} does: the category's rules, its
    /// stored keys and the embedding index all apply. Whether it saved, what the page says, and the settings the
    /// page keeps: once saved, without the keys typed into it.
    /// </summary>
    public async Task<(bool Saved, string Message, T Kept)> SaveAsync<T>(string category, T settings) where T : class
    {
        var found = SettingsCategories.Find(category)!;
        var body = found.BodyFrom(settings, saved[category]);
        var result = await update.RunAsync(await authentication.OperationContextAsync(), category, body, UnsentSecrets.Keep);
        if (!result.Succeeded)
            return (false, $"Failed to save {category} settings: {result.Detail}", settings);

        // A key typed is saved, one emptied is cleared, and one left blank stays as it was.
        foreach (var secret in found.Secrets)
        {
            if (secret.GetValue(settings) is not string value)
                continue;
            if (value.Length == 0)
                savedKeys[category].Remove(secret.Name);
            else
                savedKeys[category].Add(secret.Name);
        }
        var kept = (T)found.WithoutSecrets(settings);
        saved[category] = kept;
        return (true, $"{category} settings saved successfully!", kept);
    }
}
