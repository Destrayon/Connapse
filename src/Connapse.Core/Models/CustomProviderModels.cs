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

/// <summary>What checking a custom provider's middle man found, step by step.</summary>
/// <param name="EndpointOk">The address is acceptable, the middle man speaks protocol 1, accepts the token and refuses a wrong one.</param>
/// <param name="PermissionsOk">It advertises <c>permissions.check</c> and answered a real check call.</param>
/// <param name="Field">The form field an error belongs to: <c>baseUrl</c> or <c>secret</c>.</param>
/// <param name="Name">The name the middle man reports in its discovery document, once it was reached.</param>
public sealed record CustomProviderCheck(
    string? BaseUrl,
    bool EndpointOk,
    bool PermissionsOk,
    int MaxCheckBatch = 0,
    bool SupportsAccessKeys = false,
    string? LinkUrl = null,
    string? Error = null,
    string? Field = null,
    string? Name = null);

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

/// <summary>
/// A custom connection's configuration. Either it names the custom provider whose middle man it reads and checks
/// permissions with (<c>{ "providerId" }</c>; the address and token are the provider's), or it holds a middle man
/// itself for content only (<c>{ "baseUrl" }</c>; the token is the connection's own secret).
/// </summary>
public static class CustomConnectionConfig
{
    public static string ToJson(Guid providerId) => JsonSerializer.Serialize(new { providerId });

    /// <summary>A connection without a provider: its sources read this middle man, and nothing is checked per user.</summary>
    public static string ForMiddleMan(string baseUrl) => JsonSerializer.Serialize(new { baseUrl });

    /// <summary>The middle man a connection without a provider holds, or null.</summary>
    public static string? BaseUrlOf(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("baseUrl", out JsonElement url)
                && url.ValueKind == JsonValueKind.String
                && url.GetString() is { Length: > 0 } value
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
