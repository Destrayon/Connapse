using System.Text.Json;
using System.Text.RegularExpressions;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>An Atlassian Cloud site and the service account that reads it.</summary>
/// <param name="SiteUrl">The site's own address, <c>https://{name}.atlassian.net</c>.</param>
/// <param name="CloudId">The site's cloud id (a GUID), which pins every API call to it.</param>
/// <param name="ClientId">The service account's OAuth client id.</param>
public sealed partial record AtlassianSite(string SiteUrl, string CloudId, string ClientId)
{
    [GeneratedRegex(@"^https://[a-z0-9][a-z0-9-]*\.atlassian\.net$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SiteUrlPattern();

    /// <summary>
    /// Reads a connection's config JSON. Returns null for anything that is not a well-formed
    /// Atlassian Cloud site: a host other than <c>*.atlassian.net</c>, a cloud id that is not a
    /// GUID, or a missing client id. The values end up in request URLs, so they are validated here.
    /// </summary>
    public static AtlassianSite? FromConfigJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            string? siteUrl = Read(doc.RootElement, "siteUrl")?.TrimEnd('/');
            string? cloudId = Read(doc.RootElement, "cloudId");
            string? clientId = Read(doc.RootElement, "clientId");

            if (siteUrl is null || cloudId is null || string.IsNullOrWhiteSpace(clientId))
                return null;
            if (!SiteUrlPattern().IsMatch(siteUrl) || !Guid.TryParse(cloudId, out Guid id))
                return null;

            return new AtlassianSite(siteUrl, id.ToString("D"), clientId.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Read(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
