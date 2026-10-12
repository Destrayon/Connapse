using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;

namespace Connapse.Web.Services;

public enum AtlassianResolveOutcome { Resolved, InvalidAddress, NotFound }

public sealed record AtlassianResolveResult(AtlassianResolveOutcome Outcome, string? SiteUrl = null, string? CloudId = null);

public enum AtlassianAddOutcome { Created, MissingFields, TestFailed, Conflict }

public enum AtlassianReplaceOutcome { Replaced, NotFound, MissingFields, TestFailed, SiteChanged }

public enum AtlassianSpacesOutcome { Listed, NotFound, NotAtlassian, Unavailable }

/// <summary>A Confluence space an admin can turn into a source.</summary>
public sealed record ConfluenceSpaceInfo(string Id, string Key, string Name, string Type);

/// <param name="Error">What went wrong, for <see cref="AtlassianSpacesOutcome.Unavailable"/>. Never carries a secret.</param>
/// <param name="TroubleshootingUrl">The setup guide's entry for <paramref name="Error"/>.</param>
public sealed record AtlassianSpacesResult(
    AtlassianSpacesOutcome Outcome,
    IReadOnlyList<ConfluenceSpaceInfo>? Spaces = null,
    string? Error = null,
    string? TroubleshootingUrl = null);

/// <param name="Test">The test that ran: the failed probe for <see cref="AtlassianAddOutcome.TestFailed"/>, or a warning saved anyway.</param>
public sealed record AtlassianAddResult(
    AtlassianAddOutcome Outcome,
    Connection? Connection = null,
    ConnectionTestResult? Test = null,
    string? SiteUrl = null,
    string? CloudId = null,
    string? ClientId = null);

/// <param name="Test">The test of the new credential, when one ran.</param>
public sealed record AtlassianReplaceResult(
    AtlassianReplaceOutcome Outcome,
    Connection? Connection = null,
    ConnectionTestResult? Test = null);

/// <summary>
/// What a site test reports, in the words the page and the REST API both use: the step that failed,
/// its label, the troubleshooting entry for it, and Atlassian's own answer when it gave one.
/// </summary>
public sealed record AtlassianTestReport(
    bool Success,
    bool Warning,
    string Message,
    string? Step,
    string? StepLabel,
    string? TroubleshootingUrl,
    string? Detail,
    IReadOnlyList<string> Scopes)
{
    public static AtlassianTestReport From(ConnectionTestResult result)
    {
        string? step = result.Success ? null : result.Details?.GetValueOrDefault("step") as string;
        string? anchor = result.Success ? null : result.Details?.GetValueOrDefault("troubleshoot") as string;
        return new AtlassianTestReport(
            result.Success,
            result.Details?.ContainsKey("warning") ?? false,
            result.Message,
            step,
            step is null ? null : AtlassianConnectionTester.StepLabel(step),
            anchor is null ? null : AtlassianSiteTroubleshooting.UrlOf(anchor),
            result.Details?.GetValueOrDefault("detail") as string,
            result.Details?.GetValueOrDefault("scopes") as string[] ?? []);
    }
}

/// <summary>
/// What adding, resolving, re-testing and replacing the credentials of an Atlassian site mean,
/// shared by the REST endpoints and the Connections page so the two cannot disagree about what may
/// be saved. A site is stored only when every probe of <see cref="AtlassianConnectionTester"/>
/// passes, or when the only thing missing is something to check administrator access against and
/// the admin chose to save anyway. Its secret is encrypted by the connection store and never
/// returned.
/// </summary>
/// <remarks>
/// Every test writes audit events (<c>connection.test_started</c>, then
/// <c>connection.test_step_failed</c> or <c>connection.test_verified</c>) carrying the attempt id,
/// the step and how long it took, so time-to-verified and drop-off per step can be measured. They
/// carry the site host and client ID only, never a secret.
/// </remarks>
public sealed class AtlassianSiteService(
    IHttpClientFactory httpClients,
    AtlassianConnectionTester tester,
    IConnectionStore connections,
    IAuditLogger audit,
    AtlassianTokenSource tokens,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

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

    /// <param name="saveAnyway">
    /// Save a site whose only failure is a warning (no page or no other user yet to check
    /// administrator access against). Any other failure is never saved.
    /// </param>
    public async Task<AtlassianAddResult> AddAsync(
        string? siteUrl, string? clientId, string? clientSecret, Guid? userId, bool saveAnyway = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteUrl)
            || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret))
            return new AtlassianAddResult(AtlassianAddOutcome.MissingFields);

        var result = await TestAsync("add", null, new AtlassianSiteTestRequest(siteUrl, clientId, clientSecret), ct);

        if (!Saveable(result, saveAnyway)
            || !result.Details!.TryGetValue("cloudId", out object? cloudIdValue)
            || !result.Details.TryGetValue("siteUrl", out object? siteUrlValue))
            return new AtlassianAddResult(AtlassianAddOutcome.TestFailed, Test: result);

        string normalizedSite = (string)siteUrlValue;
        string cloudId = (string)cloudIdValue;
        string trimmedClientId = clientId.Trim();
        string name = new Uri(normalizedSite).Host;

        string configJson = JsonSerializer.Serialize(new { siteUrl = normalizedSite, cloudId, clientId = trimmedClientId });

        // Cancelled while the check ran: stop before anything is saved.
        ct.ThrowIfCancellationRequested();

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
            new { Provider = "Atlassian", SiteUrl = normalizedSite, CloudId = cloudId, ClientId = trimmedClientId, SavedWithWarning = !result.Success }, ct);

        if (await connections.RecordHealthAsync(connection.Id, HealthOf(result), ct) is { } health)
            connection = connection with { Health = health };

        return new AtlassianAddResult(AtlassianAddOutcome.Created, connection, result, normalizedSite, cloudId, trimmedClientId);
    }

    /// <summary>
    /// Tests a new client ID and secret against the site's stored address and, only when they pass,
    /// stores them in place of the old ones. The connection keeps its id, so its sources keep syncing.
    /// </summary>
    public async Task<AtlassianReplaceResult> ReplaceCredentialsAsync(
        Guid connectionId, string? clientId, string? clientSecret, Guid? userId, bool saveAnyway = false,
        CancellationToken ct = default)
    {
        Connection? connection = await connections.GetAsync(connectionId, ct);
        if (connection is not { Provider: ConnectionProvider.Atlassian }
            || AtlassianSite.FromConfigJson(connection.ConfigJson) is not { } site)
            return new AtlassianReplaceResult(AtlassianReplaceOutcome.NotFound);

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return new AtlassianReplaceResult(AtlassianReplaceOutcome.MissingFields);

        var result = await TestAsync("replace", connectionId, new AtlassianSiteTestRequest(site.SiteUrl, clientId, clientSecret), ct);
        if (!Saveable(result, saveAnyway))
            return new AtlassianReplaceResult(AtlassianReplaceOutcome.TestFailed, Test: result);

        // The address is the stored one, so a different cloud id means the site itself was replaced
        // (deleted and recreated under the same name): its sources point at content that is gone.
        if (!string.Equals(result.Details!.GetValueOrDefault("cloudId") as string, site.CloudId, StringComparison.OrdinalIgnoreCase))
            return new AtlassianReplaceResult(AtlassianReplaceOutcome.SiteChanged, Test: result);

        string trimmedClientId = clientId.Trim();
        JsonObject config = (JsonNode.Parse(connection.ConfigJson!) as JsonObject) ?? [];
        config["clientId"] = trimmedClientId;

        Connection? updated = await connections.UpdateAsync(connectionId,
            new UpdateConnectionRequest(ConfigJson: config.ToJsonString(), Secret: clientSecret), ct);
        if (updated is null)
            return new AtlassianReplaceResult(AtlassianReplaceOutcome.NotFound);

        await audit.LogAsync("connection.credentials_replaced", "connection", connectionId.ToString(),
            new { Provider = "Atlassian", site.SiteUrl, PreviousClientId = site.ClientId, ClientId = trimmedClientId, SavedWithWarning = !result.Success }, ct);

        if (await connections.RecordHealthAsync(connectionId, HealthOf(result), ct) is { } health)
            updated = updated with { Health = health };

        return new AtlassianReplaceResult(AtlassianReplaceOutcome.Replaced, updated, result);
    }

    /// <summary>
    /// Lists the site's current Confluence spaces as its service account, personal ones only when
    /// asked. Failures are reduced to a message safe to show an admin: the service account's secret
    /// never appears in it.
    /// </summary>
    public async Task<AtlassianSpacesResult> ListSpacesAsync(
        Guid connectionId, bool includePersonal, CancellationToken ct = default)
    {
        Connection? connection = await connections.GetAsync(connectionId, ct);
        if (connection is null)
            return new AtlassianSpacesResult(AtlassianSpacesOutcome.NotFound);
        if (connection.Provider != ConnectionProvider.Atlassian)
            return new AtlassianSpacesResult(AtlassianSpacesOutcome.NotAtlassian);

        AtlassianSite? site = AtlassianSite.FromConfigJson(connection.ConfigJson);
        string? secret = site is null ? null : await connections.GetSecretAsync(connectionId, ct);
        if (site is null || string.IsNullOrEmpty(secret))
            return new AtlassianSpacesResult(AtlassianSpacesOutcome.Unavailable,
                Error: $"Connection '{connection.Name}' is missing its site details or credential. Open it on the Connections page and choose Replace credentials.");

        try
        {
            using var http = httpClients.CreateClient(AtlassianApiClient.HttpClientName);
            var api = new AtlassianApiClient(http, tokens, site, secret);

            var spaces = new List<ConfluenceSpaceInfo>();
            await foreach (ConfluenceSpaceInfo space in api.PageAsync(
                "api/v2/spaces?status=current&limit=250", ReadSpaces, ct))
            {
                if (includePersonal || !string.Equals(space.Type, "personal", StringComparison.OrdinalIgnoreCase))
                    spaces.Add(space);
            }

            return new AtlassianSpacesResult(AtlassianSpacesOutcome.Listed,
                spaces.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (AtlassianRateLimitedException ex)
        {
            return new AtlassianSpacesResult(AtlassianSpacesOutcome.Unavailable, Error: ex.Message,
                TroubleshootingUrl: AtlassianSiteTroubleshooting.UrlOf(AtlassianSiteTroubleshooting.General));
        }
        catch (AtlassianAuthException ex)
        {
            return new AtlassianSpacesResult(AtlassianSpacesOutcome.Unavailable, Error: ex.Message,
                TroubleshootingUrl: AtlassianSyncErrors.TroubleshootingUrl(ex.Message));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            // A refusal is the service account's access; anything else says nothing about it.
            bool refused = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound };
            return new AtlassianSpacesResult(AtlassianSpacesOutcome.Unavailable,
                Error: "Atlassian could not list this site's spaces. Check the service account's access and try again.",
                TroubleshootingUrl: AtlassianSiteTroubleshooting.UrlOf(
                    refused ? AtlassianSiteTroubleshooting.NoSpaces : AtlassianSiteTroubleshooting.Unreachable));
        }
    }

    private static IEnumerable<ConfluenceSpaceInfo> ReadSpaces(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in results.EnumerateArray())
        {
            string? id = Text(item, "id"), key = Text(item, "key");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key))
                continue;
            yield return new ConfluenceSpaceInfo(id, key, Text(item, "name") ?? key, Text(item, "type") ?? "");
        }
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Runs every probe again with the stored secret and records the outcome as the site's health.
    /// Null when the connection is gone or unreadable.
    /// </summary>
    public async Task<ConnectionTestResult?> RetestAsync(Guid connectionId, CancellationToken ct = default)
    {
        Connection? connection = await connections.GetAsync(connectionId, ct);
        if (connection is not { Provider: ConnectionProvider.Atlassian }
            || AtlassianSite.FromConfigJson(connection.ConfigJson) is not { } site)
            return null;

        string? secret;
        try
        {
            secret = await connections.GetSecretAsync(connectionId, ct);
        }
        catch (ConnectionSecretUnavailableException)
        {
            secret = null;
        }
        if (secret is null)
            return null;

        var result = await TestAsync("retest", connectionId, new AtlassianSiteTestRequest(site.SiteUrl, site.ClientId, secret), ct);
        await connections.RecordHealthAsync(connectionId, HealthOf(result), ct);
        return result;
    }

    /// <summary>A passing test, or one whose only problem is a warning the admin chose to save past.</summary>
    private static bool Saveable(ConnectionTestResult result, bool saveAnyway) =>
        result.Details is not null
        && (result.Success || (saveAnyway && result.Details.ContainsKey("warning")));

    private ConnectionHealth HealthOf(ConnectionTestResult result) => new(
        CheckedAt: _time.GetUtcNow().UtcDateTime,
        Succeeded: result.Success,
        Step: result.Success ? null : result.Details?.GetValueOrDefault("step") as string,
        Message: result.Message,
        Origin: "test",
        CredentialRejected: result.Details?.ContainsKey("rejected") ?? false,
        Warning: result.Details?.ContainsKey("warning") ?? false,
        ConfirmedScopes: result.Success ? result.Details?.GetValueOrDefault("scopes") as string[] : null);

    /// <summary>Runs the tester between the audit events that make setup measurable.</summary>
    private async Task<ConnectionTestResult> TestAsync(
        string mode, Guid? connectionId, AtlassianSiteTestRequest request, CancellationToken ct)
    {
        Guid attempt = Guid.NewGuid();
        string? host = AtlassianSiteResolver.NormalizeSiteUrl(request.SiteUrl) is { } normalized ? new Uri(normalized).Host : null;
        string? resourceId = connectionId?.ToString();

        await audit.LogAsync("connection.test_started", "connection", resourceId,
            new { Provider = "Atlassian", Mode = mode, Attempt = attempt, SiteHost = host, ClientId = request.ClientId?.Trim() }, ct);

        var result = await tester.TestConnectionAsync(request, ct: ct);
        long totalMs = (long)(result.Duration ?? TimeSpan.Zero).TotalMilliseconds;

        if (result.Success)
        {
            await audit.LogAsync("connection.test_verified", "connection", resourceId,
                new { Provider = "Atlassian", Mode = mode, Attempt = attempt, SiteHost = host, TotalMs = totalMs }, ct);
        }
        else
        {
            await audit.LogAsync("connection.test_step_failed", "connection", resourceId, new
            {
                Provider = "Atlassian",
                Mode = mode,
                Attempt = attempt,
                SiteHost = host,
                Step = result.Details?.GetValueOrDefault("step") as string,
                StepMs = result.Details?.GetValueOrDefault("stepMs") as long?,
                TotalMs = totalMs,
                Troubleshoot = result.Details?.GetValueOrDefault("troubleshoot") as string,
                Warning = result.Details?.ContainsKey("warning") ?? false,
            }, ct);
        }

        return result;
    }
}
