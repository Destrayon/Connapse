using System.Text.Json;
using System.Text.RegularExpressions;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>
/// Turns what an administrator typed into an Atlassian Cloud site address and its cloud id. The
/// address is checked before anything is requested, so only <c>*.atlassian.net</c> is ever dialled.
/// </summary>
public static partial class AtlassianSiteResolver
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*\.atlassian\.net$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    /// <summary>
    /// Accepts <c>acme.atlassian.net</c> or <c>https://acme.atlassian.net[/]</c> and returns
    /// <c>https://acme.atlassian.net</c>; anything else (another host, a port, a path, user info,
    /// plain http) is refused with null.
    /// </summary>
    public static string? NormalizeSiteUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string text = input.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
            return null;

        string host = uri.Host.ToLowerInvariant();
        return HostPattern().IsMatch(host) ? $"https://{host}" : null;
    }

    /// <summary>
    /// Asks the site (unauthenticated) for its cloud id. Returns null when the site does not answer
    /// or answers something that is not a GUID. <paramref name="siteUrl"/> must come from
    /// <see cref="NormalizeSiteUrl"/>.
    /// </summary>
    public static async Task<string?> ResolveCloudIdAsync(HttpClient http, string siteUrl, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{siteUrl}/_edge/tenant_info", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("cloudId", out var id)
                && id.ValueKind == JsonValueKind.String
                && Guid.TryParse(id.GetString(), out Guid cloudId)
                    ? cloudId.ToString("D")
                    : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Includes the HttpClient's own timeout, which surfaces as a cancellation the caller did not ask for.
            return null;
        }
    }
}
