using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;

namespace Connapse.Web.Services;

public enum AtlassianResolveOutcome { Resolved, InvalidAddress, NotFound }

public sealed record AtlassianResolveResult(AtlassianResolveOutcome Outcome, string? SiteUrl = null, string? CloudId = null);

public enum AtlassianAddOutcome { Created, MissingFields, TestFailed, Conflict }

/// <param name="Test">The failed probe, when <paramref name="Outcome"/> is <see cref="AtlassianAddOutcome.TestFailed"/>.</param>
public sealed record AtlassianAddResult(
    AtlassianAddOutcome Outcome,
    Connection? Connection = null,
    ConnectionTestResult? Test = null,
    string? SiteUrl = null,
    string? CloudId = null,
    string? ClientId = null);

/// <summary>
/// What adding, resolving and re-testing an Atlassian site means, shared by the REST endpoints and
/// the Providers page so the two cannot disagree about what may be saved. A site is stored only when
/// every probe of <see cref="AtlassianConnectionTester"/> passes; its secret is encrypted by the
/// connection store and never returned.
/// </summary>
public sealed class AtlassianSiteService(
    IHttpClientFactory httpClients,
    AtlassianConnectionTester tester,
    IConnectionStore connections,
    IAuditLogger audit)
{
    public async Task<AtlassianResolveResult> ResolveAsync(string? siteUrl, CancellationToken ct = default)
    {
        string? normalized = AtlassianSiteResolver.NormalizeSiteUrl(siteUrl);
        if (normalized is null)
            return new AtlassianResolveResult(AtlassianResolveOutcome.InvalidAddress);

        using var http = httpClients.CreateClient(AtlassianSiteResolver.HttpClientName);
        string? cloudId = await AtlassianSiteResolver.ResolveCloudIdAsync(http, normalized, ct);
        return cloudId is null
            ? new AtlassianResolveResult(AtlassianResolveOutcome.NotFound, normalized)
            : new AtlassianResolveResult(AtlassianResolveOutcome.Resolved, normalized, cloudId);
    }

    public async Task<AtlassianAddResult> AddAsync(
        string? siteUrl, string? clientId, string? clientSecret, Guid? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteUrl)
            || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret))
            return new AtlassianAddResult(AtlassianAddOutcome.MissingFields);

        var result = await tester.TestConnectionAsync(
            new AtlassianSiteTestRequest(siteUrl, clientId, clientSecret), ct: ct);

        if (!result.Success || result.Details is null
            || !result.Details.TryGetValue("cloudId", out object? cloudIdValue)
            || !result.Details.TryGetValue("siteUrl", out object? siteUrlValue))
            return new AtlassianAddResult(AtlassianAddOutcome.TestFailed, Test: result);

        string normalizedSite = (string)siteUrlValue;
        string cloudId = (string)cloudIdValue;
        string trimmedClientId = clientId.Trim();
        string name = new Uri(normalizedSite).Host;

        string configJson = JsonSerializer.Serialize(new { siteUrl = normalizedSite, cloudId, clientId = trimmedClientId });

        Connection connection;
        try
        {
            connection = await connections.CreateAsync(
                new CreateConnectionRequest(name, ConnectionProvider.Atlassian, configJson, clientSecret),
                userId, ct);
        }
        catch (InvalidOperationException)
        {
            return new AtlassianAddResult(AtlassianAddOutcome.Conflict, SiteUrl: normalizedSite);
        }

        await audit.LogAsync("connection.created", "connection", connection.Id.ToString(),
            new { Provider = "Atlassian", SiteUrl = normalizedSite, CloudId = cloudId, ClientId = trimmedClientId }, ct);

        return new AtlassianAddResult(AtlassianAddOutcome.Created, connection, SiteUrl: normalizedSite,
            CloudId: cloudId, ClientId: trimmedClientId);
    }

    /// <summary>Runs every probe again with the stored secret. Null when the connection is gone or unreadable.</summary>
    public async Task<ConnectionTestResult?> RetestAsync(Guid connectionId, CancellationToken ct = default)
    {
        Connection? connection = await connections.GetAsync(connectionId, ct);
        if (connection is not { Provider: ConnectionProvider.Atlassian, ConfigJson: { } configJson })
            return null;

        string? secret = await connections.GetSecretAsync(connectionId, ct);
        if (secret is null)
            return null;

        string? siteUrl, clientId;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            siteUrl = doc.RootElement.TryGetProperty("siteUrl", out var s) ? s.GetString() : null;
            clientId = doc.RootElement.TryGetProperty("clientId", out var c) ? c.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (siteUrl is null || clientId is null)
            return null;

        return await tester.TestConnectionAsync(new AtlassianSiteTestRequest(siteUrl, clientId, secret), ct: ct);
    }
}
