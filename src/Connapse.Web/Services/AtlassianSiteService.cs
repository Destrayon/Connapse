using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;

namespace Connapse.Web.Services;

public enum AtlassianResolveOutcome { Resolved, InvalidAddress, NotFound }

public sealed record AtlassianResolveResult(AtlassianResolveOutcome Outcome, string? SiteUrl = null, string? CloudId = null);

public enum AtlassianAddOutcome { Created, MissingFields, TestFailed, Conflict, NameTaken }

public enum AtlassianReplaceOutcome { Replaced, NotFound, MissingFields, TestFailed, SiteChanged }

/// <summary>
/// How listing a site's spaces or projects went. <see cref="WrongProduct"/>: the connection is the
/// site's other product's, whose credential lists nothing here; its message says what to list instead.
/// </summary>
public enum AtlassianSpacesOutcome { Listed, NotFound, NotAtlassian, Unavailable, WrongProduct }

/// <summary>A space or project an admin can turn into a source: what the pickers share.</summary>
public interface IAtlassianSourceItem
{
    string Id { get; }
    string Name { get; }
}

/// <summary>A site's spaces or projects as one product's picker reads them, or why they could not be listed.</summary>
public sealed record AtlassianListing<TItem>(
    AtlassianSpacesOutcome Outcome, IReadOnlyList<TItem>? Items, string? Error, string? TroubleshootingUrl);

/// <summary>A Confluence space an admin can turn into a source.</summary>
public sealed record ConfluenceSpaceInfo(string Id, string Key, string Name, string Type) : IAtlassianSourceItem;

/// <param name="Error">What went wrong, for <see cref="AtlassianSpacesOutcome.Unavailable"/>. Never carries a secret.</param>
/// <param name="TroubleshootingUrl">The setup guide's entry for <paramref name="Error"/>.</param>
public sealed record AtlassianSpacesResult(
    AtlassianSpacesOutcome Outcome,
    IReadOnlyList<ConfluenceSpaceInfo>? Spaces = null,
    string? Error = null,
    string? TroubleshootingUrl = null);

/// <summary>A Jira project an admin can turn into a source.</summary>
public sealed record JiraProjectInfo(string Id, string Key, string Name, string ProjectTypeKey) : IAtlassianSourceItem;

/// <summary>The site's Jira projects, or why they could not be listed; the outcomes are those of <see cref="AtlassianSpacesResult"/>.</summary>
public sealed record AtlassianProjectsResult(
    AtlassianSpacesOutcome Outcome,
    IReadOnlyList<JiraProjectInfo>? Projects = null,
    string? Error = null,
    string? TroubleshootingUrl = null);

/// <param name="Test">The test that ran: the failed probe for <see cref="AtlassianAddOutcome.TestFailed"/>, or a warning saved anyway.</param>
/// <param name="Name">
/// For <see cref="AtlassianAddOutcome.Conflict"/>, the site's connection for the same product; for
/// <see cref="AtlassianAddOutcome.NameTaken"/>, the name this one would take, held by another connection.
/// </param>
public sealed record AtlassianAddResult(
    AtlassianAddOutcome Outcome,
    Connection? Connection = null,
    ConnectionTestResult? Test = null,
    string? SiteUrl = null,
    string? CloudId = null,
    string? ClientId = null,
    string? Name = null);

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
    /// <param name="product">
    /// What the connection reads. A site has at most one connection per product; its Jira connection
    /// is named apart from its Confluence one, which keeps the site's bare host name as before.
    /// </param>
    public async Task<AtlassianAddResult> AddAsync(
        string? siteUrl, string? clientId, string? clientSecret, Guid? userId, bool saveAnyway = false,
        AtlassianProduct product = AtlassianProduct.Confluence, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteUrl)
            || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret))
            return new AtlassianAddResult(AtlassianAddOutcome.MissingFields);

        var result = await TestAsync("add", null, new AtlassianSiteTestRequest(siteUrl, clientId, clientSecret, product), ct);

        if (!Saveable(result, saveAnyway)
            || !result.Details!.TryGetValue("cloudId", out object? cloudIdValue)
            || !result.Details.TryGetValue("siteUrl", out object? siteUrlValue))
            return new AtlassianAddResult(AtlassianAddOutcome.TestFailed, Test: result);

        string normalizedSite = (string)siteUrlValue;
        string cloudId = (string)cloudIdValue;
        string trimmedClientId = clientId.Trim();
        string host = new Uri(normalizedSite).Host;
        string name = product == AtlassianProduct.Jira ? $"{host} (Jira)" : host;

        string configJson = JsonSerializer.Serialize(new
        {
            siteUrl = normalizedSite, cloudId, clientId = trimmedClientId, product = AtlassianSite.ProductValue(product),
        });

        // Cancelled while the check ran: stop before anything is saved.
        ct.ThrowIfCancellationRequested();

        // One connection per site and product, whatever it has been renamed to since.
        if (await SiteConnectionAsync(cloudId, product, ct) is { } existing)
            return new AtlassianAddResult(AtlassianAddOutcome.Conflict, SiteUrl: normalizedSite, Name: existing.Name);

        Connection connection;
        try
        {
            connection = await connections.CreateAsync(
                new CreateConnectionRequest(name, ConnectionProvider.Atlassian, configJson, clientSecret),
                userId, ct);
        }
        catch (InvalidOperationException)
        {
            return new AtlassianAddResult(AtlassianAddOutcome.NameTaken, SiteUrl: normalizedSite, Name: name);
        }

        await audit.LogAsync("connection.created", "connection", connection.Id.ToString(),
            new
            {
                Provider = "Atlassian", Product = AtlassianSite.ProductValue(product), SiteUrl = normalizedSite, CloudId = cloudId,
                ClientId = trimmedClientId, SavedWithWarning = !result.Success,
            }, ct);

        if (await connections.RecordHealthAsync(connection.Id, HealthOf(result), ct) is { } health)
            connection = connection with { Health = health };

        return new AtlassianAddResult(AtlassianAddOutcome.Created, connection, result, normalizedSite, cloudId, trimmedClientId);
    }

    /// <summary>The site's saved connection for <paramref name="product"/>, if it has one.</summary>
    private async Task<Connection?> SiteConnectionAsync(string cloudId, AtlassianProduct product, CancellationToken ct) =>
        (await connections.ListAsync(take: int.MaxValue, ct: ct)).FirstOrDefault(c =>
            c.Provider == ConnectionProvider.Atlassian
            && AtlassianSite.FromConfigJson(c.ConfigJson) is { } site
            && site.Product == product
            && string.Equals(site.CloudId, cloudId, StringComparison.OrdinalIgnoreCase));

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

        var result = await TestAsync("replace", connectionId, new AtlassianSiteTestRequest(site.SiteUrl, clientId, clientSecret, site.Product), ct);
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
        var (outcome, spaces, error, url) = await ListAsync(connectionId, AtlassianProduct.Confluence,
            api => api.PageAsync("api/v2/spaces?status=current&limit=250", ReadSpaces, ct),
            space => includePersonal || !string.Equals(space.Type, "personal", StringComparison.OrdinalIgnoreCase),
            space => space.Name,
            "Atlassian could not list this site's spaces. Check the service account's access and try again.",
            AtlassianSiteTroubleshooting.NoSpaces, ct);
        return new AtlassianSpacesResult(outcome, spaces, error, url);
    }

    /// <summary>
    /// Lists the site's live Jira projects the service account can browse, as Jira itself answers
    /// for that account. Failures are reduced to a message safe to show an admin, as for spaces.
    /// </summary>
    public async Task<AtlassianProjectsResult> ListProjectsAsync(Guid connectionId, CancellationToken ct = default)
    {
        var (outcome, projects, error, url) = await ListAsync(connectionId, AtlassianProduct.Jira,
            api => api.PageByOffsetAsync("rest/api/3/project/search?action=browse&status=live", "values", ReadProject, 100, ct),
            _ => true,
            project => project.Name,
            "Atlassian could not list this site's Jira projects. Check the service account's access and try again.",
            AtlassianSiteTroubleshooting.CantReadJira, ct);
        return new AtlassianProjectsResult(outcome, projects, error, url);
    }

    /// <summary>
    /// What listing spaces and projects share: the connection is looked up and checked, the client
    /// is pinned to <paramref name="product"/>, and every failure becomes a message without the secret.
    /// </summary>
    private async Task<(AtlassianSpacesOutcome Outcome, IReadOnlyList<T>? Items, string? Error, string? TroubleshootingUrl)> ListAsync<T>(
        Guid connectionId, AtlassianProduct product, Func<AtlassianApiClient, IAsyncEnumerable<T>> list,
        Func<T, bool> keep, Func<T, string> name, string unlistable, string refusedEntry, CancellationToken ct)
    {
        Connection? connection = await connections.GetAsync(connectionId, ct);
        if (connection is null)
            return (AtlassianSpacesOutcome.NotFound, null, null, null);
        if (connection.Provider != ConnectionProvider.Atlassian)
            return (AtlassianSpacesOutcome.NotAtlassian, null, null, null);

        AtlassianSite? site = AtlassianSite.FromConfigJson(connection.ConfigJson);
        // Refused before the secret is read: the credential was granted for the connection's own product alone.
        if (site is not null && site.Product != product)
            return (AtlassianSpacesOutcome.WrongProduct, null, WrongProductMessage(site.Product), null);

        string? secret = site is null ? null : await connections.GetSecretAsync(connectionId, ct);
        if (site is null || string.IsNullOrEmpty(secret))
            return (AtlassianSpacesOutcome.Unavailable, null,
                $"Connection '{connection.Name}' is missing its site details or credential. Open it on the Connections page and choose Replace credentials.", null);

        try
        {
            using var http = httpClients.CreateClient(AtlassianApiClient.HttpClientName);
            var api = new AtlassianApiClient(http, tokens, site, secret, product: product);

            var items = new List<T>();
            await foreach (T item in list(api))
            {
                if (keep(item))
                    items.Add(item);
            }

            return (AtlassianSpacesOutcome.Listed, items.OrderBy(name, StringComparer.OrdinalIgnoreCase).ToList(), null, null);
        }
        catch (AtlassianRateLimitedException ex)
        {
            return (AtlassianSpacesOutcome.Unavailable, null, ex.Message,
                AtlassianSiteTroubleshooting.UrlOf(AtlassianSiteTroubleshooting.General));
        }
        catch (AtlassianAuthException ex)
        {
            return (AtlassianSpacesOutcome.Unavailable, null, ex.Message, AtlassianSyncErrors.TroubleshootingUrl(ex.Message));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException
            or AtlassianMalformedResponseException)
        {
            // A refusal is the service account's access; anything else says nothing about it.
            bool refused = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound };
            return (AtlassianSpacesOutcome.Unavailable, null, unlistable,
                AtlassianSiteTroubleshooting.UrlOf(refused ? refusedEntry : AtlassianSiteTroubleshooting.Unreachable));
        }
    }

    private static string WrongProductMessage(AtlassianProduct actual) => actual == AtlassianProduct.Jira
        ? "This is a Jira connection; list its projects instead."
        : "This is a Confluence connection; list its spaces instead.";

    private static JiraProjectInfo? ReadProject(JsonElement item)
    {
        // Ids are digits: the connector refuses any other project id, so none is offered.
        string? id = Text(item, "id"), key = Text(item, "key");
        if (id is not { Length: > 0 and <= 19 } || !id.All(char.IsAsciiDigit) || string.IsNullOrEmpty(key))
            return null;
        return new JiraProjectInfo(id, key, Text(item, "name") ?? key, Text(item, "projectTypeKey") ?? "");
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

        var result = await TestAsync("retest", connectionId, new AtlassianSiteTestRequest(site.SiteUrl, site.ClientId, secret, site.Product), ct);
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
