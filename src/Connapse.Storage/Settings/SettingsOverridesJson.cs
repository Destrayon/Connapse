using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;

namespace Connapse.Storage.Settings;

/// <summary>
/// Settings overrides as they are stored. The search section keeps only the settings its writer chose, because
/// link search lays an owner's search choices over the deployment's one setting at a time: a setting stored only
/// because the overrides object carried its default would replace the deployment's choice (CON-157). Readers that
/// deserialize the section still get that default for a setting left out, so nothing else changes.
/// </summary>
internal static class SettingsOverridesJson
{
    /// <summary>
    /// <paramref name="overrides"/> as JSON. With <paramref name="request"/>, the JSON the caller sent, the search
    /// section keeps the settings the request named; without it, the ones that differ from their defaults, since a
    /// typed object cannot say which of its defaults were chosen.
    /// </summary>
    public static JsonDocument Serialize(
        ContainerSettingsOverrides overrides, JsonSerializerOptions options, JsonElement? request = null)
    {
        JsonObject root = JsonSerializer.SerializeToNode(overrides, options)!.AsObject();
        if (Find(root, nameof(ContainerSettingsOverrides.Search)) is { Value: JsonObject search })
        {
            HashSet<string>? named = request is { } sent ? NamedSearchSettings(sent) : null;
            JsonObject defaults = JsonSerializer.SerializeToNode(new SearchSettings(), options)!.AsObject();
            foreach (string name in search.Select(p => p.Key).ToList())
            {
                bool keep = named?.Contains(name) ?? !JsonNode.DeepEquals(search[name], defaults[name]);
                if (!keep)
                    search.Remove(name);
            }
        }
        return JsonDocument.Parse(root.ToJsonString(options));
    }

    private static HashSet<string> NamedSearchSettings(JsonElement request)
    {
        HashSet<string> named = new(StringComparer.OrdinalIgnoreCase);
        if (request.ValueKind != JsonValueKind.Object)
            return named;
        foreach (JsonProperty section in request.EnumerateObject())
        {
            if (string.Equals(section.Name, nameof(ContainerSettingsOverrides.Search), StringComparison.OrdinalIgnoreCase)
                && section.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty setting in section.Value.EnumerateObject())
                    named.Add(setting.Name);
            }
        }
        return named;
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
