using System.Text.Json;

namespace Connapse.Core;

/// <summary>
/// Where a custom source reads from: the middle man's base URL and one scope it exposes. Stored as the
/// source's scope JSON. The middle man's bearer token is not part of it; it is kept encrypted beside the source.
/// </summary>
public sealed record CustomSourceScope(string BaseUrl, string ScopeId)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null when the JSON is missing, malformed, or lacks either member.</summary>
    public static CustomSourceScope? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            CustomSourceScope? scope = JsonSerializer.Deserialize<CustomSourceScope>(json, Json);
            return scope is { BaseUrl.Length: > 0, ScopeId.Length: > 0 } ? scope : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
