using System.Text.Json;
using System.Text.RegularExpressions;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>An Atlassian Cloud site and the service account that reads it.</summary>
/// <param name="SiteUrl">The site's own address, <c>https://{name}.atlassian.net</c>.</param>
/// <param name="CloudId">The site's cloud id (a GUID), which pins every API call to it.</param>
/// <param name="ClientId">The service account's OAuth client id.</param>
/// <param name="Product">
/// What the connection reads. A site's Confluence and its Jira are separate connections, each with
/// its own credential; one saved before Jira names no product and is Confluence's.
/// </param>
public sealed partial record AtlassianSite(
    string SiteUrl, string CloudId, string ClientId, AtlassianProduct Product = AtlassianProduct.Confluence)
{
    /// <summary>A product as a connection's config JSON stores it.</summary>
    public static string ProductValue(AtlassianProduct product) => product == AtlassianProduct.Jira ? "jira" : "confluence";

    /// <summary>The product a stored or requested value names, or null for anything else.</summary>
    public static AtlassianProduct? ProductOf(string? value) =>
        string.Equals(value, "confluence", StringComparison.OrdinalIgnoreCase) ? AtlassianProduct.Confluence
        : string.Equals(value, "jira", StringComparison.OrdinalIgnoreCase) ? AtlassianProduct.Jira
        : null;

    [GeneratedRegex(@"^https://[a-z0-9][a-z0-9-]*\.atlassian\.net$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SiteUrlPattern();

    /// <summary>
    /// Reads a connection's config JSON. Returns null for anything that is not a well-formed
    /// Atlassian Cloud site: a host other than <c>*.atlassian.net</c>, a cloud id that is not a
    /// GUID, a missing client id, or a product other than Confluence or Jira. The values end up in
    /// request URLs, so they are validated here.
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

            // Left out means Confluence; anything present must name a product, or the site reads nothing.
            AtlassianProduct? product = doc.RootElement.TryGetProperty("product", out _)
                ? ProductOf(Read(doc.RootElement, "product"))
                : AtlassianProduct.Confluence;
            if (product is null)
                return null;

            return new AtlassianSite(siteUrl, id.ToString("D"), clientId.Trim(), product.Value);
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
