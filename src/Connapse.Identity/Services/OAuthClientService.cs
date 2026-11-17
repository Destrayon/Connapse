using System.Text.Json;
using Connapse.Core.Utilities;
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

    /// <summary>
    /// Registers a client (RFC 7591). With no <paramref name="applicationType"/>, a client whose redirect URIs are
    /// all http loopback is native and any other is web. A loopback redirect is a native app's (RFC 8252 §7.3), so a
    /// client declaring "web" whose redirects are all loopback is recorded as native rather than turned away.
    /// </summary>
    public async Task<OAuthClientInfo> RegisterAsync(
        string clientName,
        List<string> redirectUris,
        string? applicationType,
        string? registeredFromIp = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientName))
            throw new ArgumentException("client_name is required.");
        applicationType = ResolveApplicationType(redirectUris, applicationType);
        ValidateRedirectUris(redirectUris, applicationType);

        string clientId = Guid.NewGuid().ToString();

        var entity = new OAuthClientEntity
        {
            ClientId = clientId,
            ClientName = clientName,
            RedirectUris = JsonSerializer.Serialize(redirectUris),
            ApplicationType = applicationType,
            RegisteredFromIp = registeredFromIp,
            CreatedAt = DateTime.UtcNow,
        };

        dbContext.OAuthClients.Add(entity);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation(
            "OAuth client registered: {ClientId} ({ClientName}) from {Ip}",
            clientId,
            LogSanitizer.Sanitize(clientName),
            LogSanitizer.Sanitize(registeredFromIp ?? "unknown"));

        return new OAuthClientInfo(clientId, clientName, redirectUris, applicationType);
    }

    // -- Client Lookup --

    public async Task<OAuthClientEntity?> GetByClientIdAsync(string clientId, CancellationToken ct = default)
    {
        return await dbContext.OAuthClients
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
    }

    // -- Client ID Metadata Document --

    public async Task<OAuthClientInfo?> FetchMetadataDocumentAsync(string clientIdUrl, CancellationToken ct = default) =>
        (await FetchMetadataDocumentWithReasonAsync(clientIdUrl, ct)).Client;

    /// <summary>
    /// Fetches a Client ID Metadata Document and, when that fails, says why: the URL it fetched and the HTTP status
    /// that came back, or what was wrong with the document. A proxy that blocks the server's own request for its
    /// CLI document otherwise surfaces only as an unknown client.
    /// </summary>
    public async Task<ClientResolution> FetchMetadataDocumentWithReasonAsync(string clientIdUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(clientIdUrl, UriKind.Absolute, out var uri))
            return ClientResolution.Unknown;

        // HTTPS required, except HTTP allowed for loopback (RFC 8252 §8.3)
        if (!IsMetadataDocumentClientId(uri))
        {
            return ClientResolution.Unknown;
        }

        try
        {
            using var response = await httpClient.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                logger.LogWarning(
                    "Client ID metadata document fetch from {Url} returned HTTP {StatusCode} ({StatusName})",
                    LogSanitizer.Sanitize(clientIdUrl), status, response.StatusCode);
                return ClientResolution.FetchFailed(
                    $"Fetching the client file {clientIdUrl} returned HTTP {status} ({response.StatusCode}).");
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var docClientId = root.GetProperty("client_id").GetString();
            if (!string.Equals(docClientId, clientIdUrl, StringComparison.Ordinal))
            {
                logger.LogWarning("Client ID metadata document client_id mismatch: expected {Expected}, got {Actual}",
                    LogSanitizer.Sanitize(clientIdUrl), LogSanitizer.Sanitize(docClientId));
                return ClientResolution.FetchFailed(
                    $"The client file {clientIdUrl} names a different client_id than its own address.");
            }

            string clientName = root.GetProperty("client_name").GetString() ?? "Unknown";
            var redirectUris = root.GetProperty("redirect_uris")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .ToList();

            return new ClientResolution(new OAuthClientInfo(clientIdUrl, clientName, redirectUris, "native"), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to fetch client ID metadata document from {Url}", LogSanitizer.Sanitize(clientIdUrl));
            return ClientResolution.FetchFailed(ex is HttpRequestException or TaskCanceledException
                ? $"Fetching the client file {clientIdUrl} failed before any response came back."
                : $"The client file {clientIdUrl} is not a valid client description.");
        }
    }

    // -- Resolve client (metadata doc or DB lookup) --

    /// <param name="serverBaseUrl">This server's own scheme and host, as it publishes its CLI document under; the
    /// CLI's client_id for this server resolves from it without a fetch, over http too.</param>
    public async Task<OAuthClientInfo?> ResolveClientAsync(string clientId, string? serverBaseUrl = null, CancellationToken ct = default) =>
        (await ResolveClientWithReasonAsync(clientId, serverBaseUrl, ct)).Client;

    /// <summary>
    /// <see cref="ResolveClientAsync"/>, plus why a client whose metadata document was fetched couldn't be resolved.
    /// </summary>
    public async Task<ClientResolution> ResolveClientWithReasonAsync(string clientId, string? serverBaseUrl = null, CancellationToken ct = default)
    {
        if (IsFirstPartyCliClientId(clientId, serverBaseUrl))
            return new ClientResolution(FirstPartyCliClient(clientId), null);

        // URL-formatted client_id = Client ID Metadata Document
        if (Uri.TryCreate(clientId, UriKind.Absolute, out var uri) && IsMetadataDocumentClientId(uri))
        {
            return await FetchMetadataDocumentWithReasonAsync(clientId, ct);
        }

        // Otherwise, look up in registered clients
        var entity = await GetByClientIdAsync(clientId, ct);
        if (entity is null)
            return ClientResolution.Unknown;

        var redirectUris = JsonSerializer.Deserialize<List<string>>(entity.RedirectUris) ?? [];
        return new ClientResolution(
            new OAuthClientInfo(entity.ClientId, entity.ClientName, redirectUris, entity.ApplicationType), null);
    }

    // -- Redirect URI Validation --

    public bool ValidateRedirectUri(OAuthClientEntity client, string redirectUri)
    {
        var allowedUris = JsonSerializer.Deserialize<List<string>>(client.RedirectUris) ?? [];
        return allowedUris.Contains(redirectUri, StringComparer.Ordinal);
    }

    public static bool ValidateRedirectUri(OAuthClientInfo client, string redirectUri)
    {
        // Exact match first
        if (client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            return true;

        // RFC 8252 Section 7.3: loopback redirect URIs may use any port
        if (Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) && IsLoopbackHttp(uri))
        {
            // Check if any registered URI matches except for port
            return client.RedirectUris.Any(registered =>
            {
                if (!Uri.TryCreate(registered, UriKind.Absolute, out var regUri))
                    return false;
                return regUri.Host == uri.Host &&
                       regUri.Scheme == uri.Scheme &&
                       regUri.AbsolutePath == uri.AbsolutePath;
            });
        }

        return false;
    }

    // -- First-party CLI client --

    /// <summary>Where this server publishes the connapse CLI's client metadata document.</summary>
    public const string FirstPartyCliPath = "/oauth/clients/cli.json";

    public const string FirstPartyCliName = "Connapse CLI";

    public static readonly IReadOnlyList<string> FirstPartyCliRedirectUris = ["http://127.0.0.1/callback"];

    /// <summary>
    /// Whether <paramref name="clientId"/> is the CLI document this server itself publishes at
    /// <paramref name="serverBaseUrl"/>. Such a client_id is answered from this server's own definition and never
    /// fetched, so its scheme doesn't matter: the https rule for client ID URLs protects a document fetched from
    /// someone else, and nothing is fetched here. Only the exact URL of this server's document qualifies.
    /// </summary>
    public static bool IsFirstPartyCliClientId(string clientId, string? serverBaseUrl) =>
        !string.IsNullOrEmpty(serverBaseUrl)
        && string.Equals(clientId, serverBaseUrl + FirstPartyCliPath, StringComparison.Ordinal);

    /// <summary>
    /// The sign-in page's error for a client it couldn't resolve. When the client file was fetched, it adds why the
    /// fetch failed; when that file is a Connapse CLI document, it adds that a reverse proxy must let the server
    /// reach it, the usual cause behind a proxy with an IP allowlist.
    /// </summary>
    public static string UnknownClientMessage(string clientId, string? fetchFailure)
    {
        const string unknown = "Unknown or unregistered client.";
        if (fetchFailure is null)
            return unknown;

        string message = $"{unknown} {fetchFailure}";
        if (Uri.TryCreate(clientId, UriKind.Absolute, out var uri)
            && string.Equals(uri.AbsolutePath, FirstPartyCliPath, StringComparison.Ordinal))
        {
            message += $" If a reverse proxy sits in front of Connapse, it must let the Connapse server itself reach {FirstPartyCliPath}.";
        }

        return message;
    }

    private static OAuthClientInfo FirstPartyCliClient(string clientId) =>
        new(clientId, FirstPartyCliName, [.. FirstPartyCliRedirectUris], "native");

    // -- Validation --

    /// <summary>
    /// Whether a URL client_id names a Client ID Metadata Document this server will fetch: https, or http on a
    /// loopback host (RFC 8252 §8.3).
    /// </summary>
    public static bool IsMetadataDocumentClientId(Uri clientId) =>
        clientId.Scheme == Uri.UriSchemeHttps || IsLoopbackHttp(clientId);

    /// <summary>
    /// An http URL on a loopback host: localhost, 127.0.0.1 or [::1] (RFC 8252 §7.3, §8.3), on any port.
    /// </summary>
    public static bool IsLoopbackHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp && uri.Host is "127.0.0.1" or "localhost" or "[::1]";

    private static string ResolveApplicationType(List<string> redirectUris, string? applicationType)
    {
        if (applicationType is not (null or "native" or "web"))
            throw new ArgumentException($"application_type must be \"native\" or \"web\", got: {applicationType}");

        bool allLoopback = redirectUris is { Count: > 0 } && redirectUris.All(
            u => Uri.TryCreate(u, UriKind.Absolute, out var parsed) && IsLoopbackHttp(parsed));
        return allLoopback ? "native" : applicationType ?? "web";
    }

    private static void ValidateRedirectUris(List<string> redirectUris, string applicationType)
    {
        if (redirectUris is null || redirectUris.Count == 0)
            throw new ArgumentException("At least one redirect_uri is required.");

        foreach (string uri in redirectUris)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                throw new ArgumentException($"Invalid redirect URI: {uri}");

            switch (applicationType)
            {
                case "native" when !IsLoopbackHttp(parsed):
                    throw new ArgumentException(
                        $"Native clients must use http://127.0.0.1, http://[::1] or http://localhost redirect URIs, got: {uri}");
                case "web" when parsed.Scheme != Uri.UriSchemeHttps:
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

/// <summary>
/// A resolved client, or none. <see cref="FetchFailure"/> says why when the client's metadata document was fetched
/// and the fetch or the document failed: it names the URL and, for an error response, the HTTP status.
/// </summary>
public record ClientResolution(OAuthClientInfo? Client, string? FetchFailure)
{
    public static readonly ClientResolution Unknown = new(null, null);

    public static ClientResolution FetchFailed(string reason) => new(null, reason);
}
