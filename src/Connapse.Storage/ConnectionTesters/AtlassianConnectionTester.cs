using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>The site and service-account credential an administrator wants to save.</summary>
public sealed record AtlassianSiteTestRequest(string SiteUrl, string ClientId, string ClientSecret);

/// <summary>
/// Validates an Atlassian site's service account before it is saved. Each probe is one thing
/// Connapse needs later, and the failing one is named in <c>Details["step"]</c>: the token exchange,
/// reading Confluence, seeing a space, and being Confluence Administrator (needed to ask what
/// another user may read). A site that passes carries its <c>cloudId</c> in <c>Details</c>.
/// </summary>
/// <remarks>
/// Uses its own token cache, never the shared one: that cache is keyed by client id, so a candidate
/// secret could otherwise be "accepted" by a token an earlier, correct secret had minted.
/// </remarks>
public sealed class AtlassianConnectionTester(IHttpClientFactory httpClients, TimeProvider? time = null) : IConnectionTester
{
    public const string SiteStep = "site";
    public const string TokenStep = "token";
    public const string IdentityStep = "identity";
    public const string SpacesStep = "spaces";
    public const string AdminStep = "admin";

    private const string UnverifiedMessage =
        "Couldn't verify Confluence Administrator access because Atlassian didn't answer the check properly; try again.";

    // The probes read what Atlassian returned; any of these means this probe failed, not that the test crashed.
    private static bool IsProbeFailure(Exception ex) =>
        ex is HttpRequestException or AtlassianAuthException or JsonException or InvalidOperationException;

    private static bool IsRefusal(Exception ex) =>
        ex is AtlassianAuthException
        || ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized };

    private const string AdminMessage =
        "The service account isn't Confluence Administrator; Connapse can't check other users' access without it.";

    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (settings is not AtlassianSiteTestRequest request)
            return ConnectionTestResult.CreateFailure("Invalid settings: expected AtlassianSiteTestRequest.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        CancellationToken linked = cts.Token;

        var stopwatch = Stopwatch.StartNew();

        string? siteUrl = AtlassianSiteResolver.NormalizeSiteUrl(request.SiteUrl);
        if (siteUrl is null)
            return Failure(SiteStep, "The site address must look like https://your-site.atlassian.net.", stopwatch);

        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
            return Failure(TokenStep, "Atlassian rejected the client ID or secret: both are required.", stopwatch);

        try
        {
            using var lookup = httpClients.CreateClient(AtlassianSiteResolver.HttpClientName);
            string? cloudId = await AtlassianSiteResolver.ResolveCloudIdAsync(lookup, siteUrl, linked);
            if (cloudId is null)
                return Failure(SiteStep, $"Couldn't find an Atlassian Cloud site at {siteUrl}. Check the address.", stopwatch);

            var site = new AtlassianSite(siteUrl, cloudId, request.ClientId.Trim());
            var tokens = new AtlassianTokenSource(httpClients, time ?? TimeProvider.System);

            try
            {
                await tokens.GetTokenAsync(site, request.ClientSecret, linked);
            }
            catch (AtlassianAuthException)
            {
                return Failure(TokenStep, "Atlassian rejected the client ID or secret.", stopwatch);
            }

            using var http = httpClients.CreateClient(AtlassianApiClient.HttpClientName);
            var api = new AtlassianApiClient(http, tokens, site, request.ClientSecret, time);

            string? ownAccountId;
            try
            {
                using var current = await api.GetJsonAsync<JsonDocument>("rest/api/user/current", linked);
                ownAccountId = Text(current.RootElement, "accountId");
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return Failure(IdentityStep, "The credential can't read Confluence on this site.", stopwatch);
            }

            // Without the service account's own id the "other user" below could be the account itself,
            // and checking yourself needs no admin rights: fail closed.
            if (string.IsNullOrWhiteSpace(ownAccountId))
                return Failure(IdentityStep, "The credential can't read Confluence on this site: Atlassian didn't say who it is.", stopwatch);

            try
            {
                using var spaces = await api.GetJsonAsync<JsonDocument>("api/v2/spaces?limit=1", linked);
                if (FirstId(spaces.RootElement) is null)
                    return Failure(SpacesStep, "The service account has no Confluence access.", stopwatch);
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return Failure(SpacesStep, "The service account has no Confluence access.", stopwatch);
            }

            string? pageId;
            try
            {
                using var pages = await api.GetJsonAsync<JsonDocument>("api/v2/pages?limit=1", linked);
                pageId = FirstId(pages.RootElement);
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return Failure(SpacesStep,
                    "The service account can't list pages. Check that its app has the read:page:confluence scope.", stopwatch);
            }

            if (pageId is null)
                return Failure(AdminStep,
                    "This site has no pages yet, so Connapse can't confirm the service account is Confluence Administrator. "
                    + "Create a page and test again.",
                    stopwatch, warning: true);

            string? otherUser;
            try
            {
                using var users = await api.GetJsonAsync<JsonDocument>("rest/api/search/user?cql=type=user&limit=25", linked);
                otherUser = FirstOtherAccount(users.RootElement, ownAccountId);
            }
            catch (Exception ex) when (IsRefusal(ex))
            {
                return Failure(AdminStep, AdminMessage, stopwatch);
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return Failure(AdminStep, UnverifiedMessage, stopwatch);
            }

            if (otherUser is null)
                return Failure(AdminStep,
                    "The site has no user other than the service account, so Connapse can't confirm Confluence Administrator "
                    + "access. Add another user to the site and test again.",
                    stopwatch);

            HttpStatusCode checkStatus;
            bool checkAnswered = false;
            try
            {
                using var check = await api.PostAsync(
                    $"rest/api/content/{Uri.EscapeDataString(pageId)}/permission/check",
                    new { subject = new { type = "user", identifier = otherUser }, operation = "read" }, linked);
                checkStatus = check.StatusCode;

                // A 200 proves nothing by itself (a login page or proxy error can be a 200): only a
                // PermissionCheckResponse carrying a boolean hasPermission shows the check ran. Either
                // value will do, since answering at all for another user needs administrator rights.
                if (checkStatus == HttpStatusCode.OK)
                {
                    using var answer = JsonDocument.Parse(await check.Content.ReadAsStringAsync(linked));
                    checkAnswered = answer.RootElement.ValueKind == JsonValueKind.Object
                        && answer.RootElement.TryGetProperty("hasPermission", out var has)
                        && has.ValueKind is JsonValueKind.True or JsonValueKind.False;
                }
            }
            catch (AtlassianAuthException)
            {
                return Failure(AdminStep, AdminMessage, stopwatch);
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return Failure(AdminStep, UnverifiedMessage, stopwatch);
            }

            if (checkStatus is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return Failure(AdminStep, AdminMessage, stopwatch);
            if (checkStatus != HttpStatusCode.OK)
                return Failure(AdminStep, $"{UnverifiedMessage} Confluence answered HTTP {(int)checkStatus}.", stopwatch);
            if (!checkAnswered)
                return Failure(AdminStep, UnverifiedMessage, stopwatch);

            stopwatch.Stop();
            return ConnectionTestResult.CreateSuccess(
                $"Connected to {siteUrl} as a Confluence Administrator service account.",
                new Dictionary<string, object> { ["cloudId"] = cloudId, ["siteUrl"] = siteUrl },
                stopwatch.Elapsed);
        }
        catch (AtlassianRateLimitedException ex)
        {
            return Failure("rate-limit", ex.Message, stopwatch);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Failure("timeout", "Atlassian didn't answer in time.", stopwatch);
        }
        catch (HttpRequestException)
        {
            return Failure(SiteStep, "Couldn't reach Atlassian.", stopwatch);
        }
    }

    private static ConnectionTestResult Failure(string step, string message, Stopwatch stopwatch, bool warning = false)
    {
        var details = new Dictionary<string, object> { ["step"] = step };
        if (warning)
            details["warning"] = true;
        return ConnectionTestResult.CreateFailure(message, details, stopwatch.Elapsed);
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? FirstId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id))
            {
                string? text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    // search/user results look like { results: [ { user: { accountId } } ] }; a flat shape is accepted too.
    private static string? FirstOtherAccount(JsonElement root, string? ownAccountId)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in results.EnumerateArray())
        {
            string? accountId = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("user", out var user)
                ? Text(user, "accountId")
                : Text(item, "accountId");

            if (!string.IsNullOrWhiteSpace(accountId) && accountId != ownAccountId)
                return accountId;
        }

        return null;
    }
}
