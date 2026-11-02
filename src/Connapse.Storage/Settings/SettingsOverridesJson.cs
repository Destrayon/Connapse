using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;

namespace Connapse.Storage.Settings;

/// <summary>
/// Settings overrides as they are stored. Link search lays an owner's search choices over the deployment's one
/// setting at a time, so a search setting stored only because the overrides object carried its default would replace
/// the deployment's choice (CON-157). A writer that knows which search settings it chose names them, and the search
/// section keeps only those. Readers that deserialize the section still get the default for a setting left out.
/// </summary>
internal static class SettingsOverridesJson
{
    /// <summary>
    /// <paramref name="overrides"/> as JSON. With <paramref name="searchSettingsNamed"/>, the search section keeps only
    /// the settings named there (in any case); without it, every setting the object carries, since a typed object
    /// alone cannot say which of its values were chosen.
    /// </summary>
    public static JsonDocument Serialize(
        ContainerSettingsOverrides overrides, JsonSerializerOptions options,
        IEnumerable<string>? searchSettingsNamed = null)
    {
        JsonObject root = JsonSerializer.SerializeToNode(overrides, options)!.AsObject();
        if (searchSettingsNamed is not null
            && Find(root, nameof(ContainerSettingsOverrides.Search)) is { Value: JsonObject search })
        {
            HashSet<string> named = new(searchSettingsNamed, StringComparer.OrdinalIgnoreCase);
            // A secret is never named back, since no reader sees it, but a kept one stays while the destinations it was
            // saved for are named: pruning it would delete the key, and keeping it without them would send it elsewhere.
            if (SettingsSecrets.DestinationsOf(typeof(SearchSettings)).All(d => named.Contains(d.Name)))
            {
                named.UnionWith(SettingsSecrets.Of(typeof(SearchSettings))
                    .Where(s => s.GetValue(overrides.Search) is not null).Select(s => s.Name));
            }
            foreach (string name in search.Select(p => p.Key).Where(n => !named.Contains(n)).ToList())
                search.Remove(name);
        }
        return JsonDocument.Parse(root.ToJsonString(options));
    }

    private static KeyValuePair<string, JsonNode?>? Find(JsonObject root, string name)
    {
        foreach (KeyValuePair<string, JsonNode?> property in root)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                return property;
        }
        return null;
    }
}
