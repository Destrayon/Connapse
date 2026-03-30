using System.Text.Json;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Services;

public class OAuthClientService(
    ConnapseIdentityDbContext dbContext,
    HttpClient httpClient,
    ILogger<OAuthClientService> logger)
{
    // -- Dynamic Client Registration --

    public async Task<OAuthClientInfo> RegisterAsync(
        string clientName,
        List<string> redirectUris,
        string applicationType,
        CancellationToken ct = default)
    {
        ValidateRedirectUris(redirectUris, applicationType);

        string clientId = Guid.NewGuid().ToString();

        var entity = new OAuthClientEntity
        {
            ClientId = clientId,
            ClientName = clientName,
            RedirectUris = JsonSerializer.Serialize(redirectUris),
            ApplicationType = applicationType,
            CreatedAt = DateTime.UtcNow,
        };

        dbContext.OAuthClients.Add(entity);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("OAuth client registered: {ClientId} ({ClientName})", clientId, clientName);

        return new OAuthClientInfo(clientId, clientName, redirectUris, applicationType);
    }

    // -- Client Lookup --

    public async Task<OAuthClientEntity?> GetByClientIdAsync(string clientId, CancellationToken ct = default)
    {
        return await dbContext.OAuthClients
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
    }

    // -- Client ID Metadata Document --

    public async Task<OAuthClientInfo?> FetchMetadataDocumentAsync(string clientIdUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(clientIdUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https")
        {
            return null;
        }

        try
        {
            var response = await httpClient.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            string json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var docClientId = root.GetProperty("client_id").GetString();
            if (!string.Equals(docClientId, clientIdUrl, StringComparison.Ordinal))
            {
                logger.LogWarning("Client ID metadata document client_id mismatch: expected {Expected}, got {Actual}",
                    clientIdUrl, docClientId);
                return null;
            }

            string clientName = root.GetProperty("client_name").GetString() ?? "Unknown";
            var redirectUris = root.GetProperty("redirect_uris")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .ToList();

            return new OAuthClientInfo(clientIdUrl, clientName, redirectUris, "native");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch client ID metadata document from {Url}", clientIdUrl);
            return null;
        }
    }

    // -- Resolve client (metadata doc or DB lookup) --

    public async Task<OAuthClientInfo?> ResolveClientAsync(string clientId, CancellationToken ct = default)
    {
        // URL-formatted client_id = Client ID Metadata Document
        if (Uri.TryCreate(clientId, UriKind.Absolute, out var uri) && uri.Scheme == "https")
        {
            return await FetchMetadataDocumentAsync(clientId, ct);
        }

        // Otherwise, look up in registered clients
        var entity = await GetByClientIdAsync(clientId, ct);
        if (entity is null)
            return null;

        var redirectUris = JsonSerializer.Deserialize<List<string>>(entity.RedirectUris) ?? [];
        return new OAuthClientInfo(entity.ClientId, entity.ClientName, redirectUris, entity.ApplicationType);
    }

    // -- Redirect URI Validation --

    public bool ValidateRedirectUri(OAuthClientEntity client, string redirectUri)
    {
        var allowedUris = JsonSerializer.Deserialize<List<string>>(client.RedirectUris) ?? [];
        return allowedUris.Contains(redirectUri, StringComparer.Ordinal);
    }

    public static bool ValidateRedirectUri(OAuthClientInfo client, string redirectUri)
    {
        return client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal);
    }

    // -- Validation --

    private static void ValidateRedirectUris(List<string> redirectUris, string applicationType)
    {
        foreach (string uri in redirectUris)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                throw new ArgumentException($"Invalid redirect URI: {uri}");

            bool isLoopback = parsed.Host is "127.0.0.1" or "localhost";

            switch (applicationType)
            {
                case "native" when !isLoopback || parsed.Scheme != "http":
                    throw new ArgumentException(
                        $"Native clients must use http://127.0.0.1 or http://localhost redirect URIs, got: {uri}");
                case "web" when parsed.Scheme != "https":
                    throw new ArgumentException(
                        $"Web clients must use https redirect URIs, got: {uri}");
            }
        }
    }
}

public record OAuthClientInfo(
    string ClientId,
    string ClientName,
    List<string> RedirectUris,
    string ApplicationType);
