using System.Net;
using System.Text.Json;
using Connapse.Storage.Connectors.Atlassian;
using static Connapse.Storage.ConnectionTesters.AtlassianConnectionTester;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// The site test's Confluence probes: reading Confluence, seeing a space, reading comments, folders
/// and changes (one probe per scope a sync needs), and being Confluence Administrator (needed to ask
/// what another user may read).
/// </summary>
internal sealed class ConfluenceSiteProbe(IHttpClientFactory httpClients, TimeProvider? time = null) : IAtlassianProductProbe
{
    public string Product => "confluence";

    internal const string UnverifiedMessage =
        "Couldn't verify Confluence Administrator access because Atlassian didn't answer the check properly. "
        + "Wait a minute and test again.";

    internal const string AdminMessage =
        "The service account isn't Confluence Administrator, so Connapse can't check what other people may read. "
        + "In Atlassian Administration, go to Directory → Service accounts, set its Confluence role to the admin role, then test again.";

    internal const string MissingPermissionScopeMessage =
        "The credential is missing the " + AtlassianSiteScopes.ContentPermission + " scope, so Connapse can't check what "
        + "other people may read. Create a new OAuth 2.0 credential on the service account with every scope listed in the "
        + "setup steps, then enter it here.";

    internal const string CantReadConfluenceMessage =
        "The credential can't read Confluence on this site. Check that the service account's Confluence role isn't None "
        + "and that its credential has every scope listed in the setup steps, then test again.";

    internal const string NoSpacesMessage =
        "The service account can't see any Confluence space. Give it the Viewer role in at least one space "
        + "(Space settings > Users), then test again.";

    public async Task<ProductProbeResult> RunAsync(
        AtlassianSite site, string secret, AtlassianTokenSource tokens, Run steps, CancellationToken linked)
    {
        var run = steps.For(Product);
        IReadOnlyList<string>? granted = tokens.GrantedScopes(site);

        using var http = httpClients.CreateClient(AtlassianApiClient.HttpClientName);
        var api = new AtlassianApiClient(http, tokens, site, secret, time);

        run.Enter(IdentityStep);
        string? ownAccountId;
        try
        {
            using var current = await api.GetJsonAsync<JsonDocument>("rest/api/user/current", linked);
            ownAccountId = Text(current.RootElement, "accountId");
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return run.Fail(CantReadConfluenceMessage, detail: Describe(ex));
        }

        // Without the service account's own id the "other user" below could be the account itself,
        // and checking yourself needs no admin rights: fail closed.
        if (string.IsNullOrWhiteSpace(ownAccountId))
            return run.Fail(CantReadConfluenceMessage, detail: "Atlassian didn't say which account the credential belongs to.");

        run.Enter(SpacesStep);
        try
        {
            using var spaces = await api.GetJsonAsync<JsonDocument>("api/v2/spaces?limit=1", linked);
            if (FirstId(spaces.RootElement) is null)
                return run.Fail(NoSpacesMessage, detail: "Atlassian listed no spaces for this account.");
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return run.Fail(NoSpacesMessage, detail: Describe(ex));
        }

        string? pageId;
        string? folderId;
        try
        {
            using var pages = await api.GetJsonAsync<JsonDocument>("api/v2/pages?limit=1", linked);
            pageId = FirstId(pages.RootElement);
            folderId = FirstParentFolder(pages.RootElement);
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return run.Fail(
                "The service account can't list pages. Check that its credential has the read:page:confluence scope, "
                + "then test again.",
                AtlassianSiteTroubleshooting.MissingScope, Describe(ex));
        }

        // One cheap read per scope a sync needs that the steps around this one don't use, each the
        // kind of call the connector makes, so a credential missing one fails here and not at sync.
        run.Enter(ContentStep);
        var probes = new List<(string Scope, string Path)>();
        if (pageId is not null)
        {
            string page = Uri.EscapeDataString(pageId);
            probes.Add((AtlassianSiteScopes.Comment, $"api/v2/pages/{page}/footer-comments?limit=1"));
            // A folder the page sits in when there is one. Otherwise the page's own id: Atlassian
            // checks the scope before it looks the folder up, so "not found" still proves the scope.
            probes.Add((AtlassianSiteScopes.Folder, $"api/v2/folders/{Uri.EscapeDataString(folderId ?? pageId)}"));
        }
        probes.Add((AtlassianSiteScopes.ContentDetails, $"rest/api/user/bulk?accountId={Uri.EscapeDataString(ownAccountId)}"));
        probes.Add((AtlassianSiteScopes.ContentDetails, $"rest/api/search?cql={Uri.EscapeDataString("type=comment")}&limit=1"));

        foreach (var (scope, path) in probes)
        {
            var (outcome, probeDetail) = await ProbeAsync(api, path, linked);
            if (outcome == Probe.Missing)
                return run.Fail(MissingScopeMessage(scope), AtlassianSiteTroubleshooting.MissingScope, probeDetail);
            if (outcome == Probe.Unanswered)
                return run.Fail(
                    $"Couldn't check the {scope} scope because Atlassian didn't answer properly. Wait a minute and test again.",
                    AtlassianSiteTroubleshooting.General, probeDetail);
        }

        // Optional: recorded as confirmed when it passes, never a failure without it.
        bool attachmentsConfirmed = pageId is not null
            && (await ProbeAsync(api, $"api/v2/pages/{Uri.EscapeDataString(pageId)}/attachments?limit=1", linked)).Outcome == Probe.Granted;

        run.Enter(AdminStep);
        if (pageId is null)
            return run.Warn(
                "This site has no pages yet, so Connapse can't confirm the service account is Confluence Administrator. "
                + "The site can be saved with this warning, and the check runs again at each test. Or create a page and test again.");

        string? otherUser;
        try
        {
            using var users = await api.GetJsonAsync<JsonDocument>("rest/api/search/user?cql=type=user&limit=25", linked);
            otherUser = FirstOtherAccount(users.RootElement, ownAccountId);
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return run.Fail(AdminMessage, detail: Describe(ex));
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return run.Fail(UnverifiedMessage, detail: Describe(ex));
        }

        if (otherUser is null)
            return run.Warn(
                "The site has no user other than the service account, so Connapse can't confirm Confluence Administrator "
                + "access. The site can be saved with this warning, and the check runs again at each test. Or add another user and test again.");

        // A missing scope and a missing role both stop the check; Atlassian names the scope when
        // it is the cause (and, when it lists the token's scopes, the scope can be seen missing).
        bool scopeMissing = granted is not null
            && !granted.Contains(AtlassianSiteScopes.ContentPermission, StringComparer.OrdinalIgnoreCase);

        HttpStatusCode checkStatus;
        string? checkDetail = null;
        bool checkAnswered = false;
        try
        {
            using var check = await api.PostAsync(
                $"rest/api/content/{Uri.EscapeDataString(pageId)}/permission/check",
                new { subject = new { type = "user", identifier = otherUser }, operation = "read" }, linked);
            checkStatus = check.StatusCode;
            string body = await check.Content.ReadAsStringAsync(linked);

            // A 200 proves nothing by itself (a login page or proxy error can be a 200): only a
            // PermissionCheckResponse carrying a boolean hasPermission shows the check ran. Either
            // value will do, since answering at all for another user needs administrator rights.
            if (checkStatus == HttpStatusCode.OK)
            {
                using var answer = JsonDocument.Parse(body);
                checkAnswered = answer.RootElement.ValueKind == JsonValueKind.Object
                    && answer.RootElement.TryGetProperty("hasPermission", out var has)
                    && has.ValueKind is JsonValueKind.True or JsonValueKind.False;
            }
            else
            {
                checkDetail = DescribeAnswer(checkStatus, body);
                if (body.Contains("scope", StringComparison.OrdinalIgnoreCase))
                    scopeMissing = true;
            }
        }
        catch (AtlassianAuthException ex)
        {
            // Two 401s in a row after the same token read Confluence above: Atlassian refuses a
            // token on one endpoint like this when its scopes don't cover that endpoint.
            return run.Fail(MissingPermissionScopeMessage, AtlassianSiteTroubleshooting.MissingScope, ex.Detail ?? ex.Message);
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return run.Fail(UnverifiedMessage, detail: Describe(ex));
        }

        if (checkStatus is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            return scopeMissing
                ? run.Fail(MissingPermissionScopeMessage, AtlassianSiteTroubleshooting.MissingScope, checkDetail)
                : run.Fail(AdminMessage, detail: checkDetail);
        if (checkStatus != HttpStatusCode.OK)
            return run.Fail($"{UnverifiedMessage} Confluence answered HTTP {(int)checkStatus}.", detail: checkDetail);
        if (!checkAnswered)
            return run.Fail(UnverifiedMessage, detail: "Confluence answered the check without saying whether the user may read the page.");

        // What the probes proved, and anything else Atlassian says it granted.
        var proven = new HashSet<string>(AtlassianSiteScopes.ProvenByTest, StringComparer.OrdinalIgnoreCase);
        if (attachmentsConfirmed)
            proven.Add(AtlassianSiteScopes.Attachment);
        IReadOnlyList<string> confirmed = AtlassianSiteScopes.Confluence.Select(s => s.Name)
            .Where(n => proven.Contains(n) || (granted?.Contains(n, StringComparer.OrdinalIgnoreCase) ?? false))
            .ToList();

        return run.Pass(
            "read Confluence, listed spaces, read comments, folders and changes, and confirmed it can check what other people may read",
            confirmed);
    }

    /// <summary>The folder the first listed page sits in, or null when it sits elsewhere.</summary>
    private static string? FirstParentFolder(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in results.EnumerateArray())
            return string.Equals(Text(item, "parentType"), "folder", StringComparison.Ordinal) ? Text(item, "parentId") : null;
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
