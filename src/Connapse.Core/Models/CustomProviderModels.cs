using System.Text.Json;

namespace Connapse.Core;

/// <summary>
/// A customer-run middle man registered for permissions: the address and token Connapse uses to ask it,
/// live, what each user may read. Several may exist. Its token and link secret never appear on this read model.
/// </summary>
/// <param name="HasLinkSecret">Whether a link secret was generated, so users can link their accounts.</param>
public sealed record CustomProvider(
    Guid Id,
    string Name,
    string BaseUrl,
    bool HasToken,
    int MaxCheckBatch,
    bool SupportsAccessKeys,
    string? LinkUrl,
    DateTime? VerifiedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool HasLinkSecret = false);

/// <summary>What a custom provider is saved with, after its middle man was checked.</summary>
/// <param name="Token">The bearer token; null on an update keeps the stored one.</param>
public sealed record SaveCustomProviderRequest(
    string Name,
    string BaseUrl,
    string? Token,
    int MaxCheckBatch,
    bool SupportsAccessKeys,
    string? LinkUrl)
{
    // The generated ToString would print the token into any log line or message it reached.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, BaseUrl = {BaseUrl}, Token = {(Token is null ? "null" : "***")}, ")
            .Append($"MaxCheckBatch = {MaxCheckBatch}, SupportsAccessKeys = {SupportsAccessKeys}, LinkUrl = {LinkUrl}");
        return true;
    }
}

/// <summary>A custom connection's configuration: the custom provider it checks permissions with, and nothing else.</summary>
public static class CustomConnectionConfig
{
    public static string ToJson(Guid providerId) => JsonSerializer.Serialize(new { providerId });

    public static Guid? ProviderIdOf(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("providerId", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && Guid.TryParse(id.GetString(), out Guid parsed)
                    ? parsed
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
