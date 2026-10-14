using System.Net;
using System.Text.Json;
using Connapse.Storage.Connectors.Atlassian;
using static Connapse.Storage.ConnectionTesters.AtlassianConnectionTester;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// The site test's Jira probes: reading Jira, seeing a project, reading an issue and its comments
/// (one probe per scope a sync needs), being able to ask what another person may browse (Administer
/// Jira), and reading the group and project role membership that restricted comments name.
/// </summary>
internal sealed class JiraSiteProbe(IHttpClientFactory httpClients, TimeProvider? time = null) : IAtlassianProductProbe
{
    public string Product => AtlassianSiteScopes.JiraProduct;

    /// <summary>How many browsable projects the issue search spans when the first project has no issue.</summary>
    private const int MaxProjectsSearched = 50;

    internal const string CantReadJiraMessage =
        "The credential can't read Jira on this site. Check that the service account has Jira access and that its credential "
        + "has the Jira scopes listed in the setup steps, then test again.";

    internal const string NoProjectsMessage =
        "The service account can't see any Jira project yet, so Connapse can't check the rest of Jira. The site can be saved "
        + "with this warning, and the check runs again at each test. Or let it see a project (Viewer on a private team-managed space, or Browse Spaces "
        + "in a company-managed space's permission scheme) and test again.";

    internal const string NoIssuesMessage =
        "The service account can't see any Jira issue yet, so Connapse couldn't check that it can read issues and comments. "
        + "The site can be saved with this warning, and the check runs again at each test. Or let it see a project that has issues (Viewer on a "
        + "private team-managed space, or Browse Spaces in a company-managed space's permission scheme) and test again.";

    internal const string NoOtherUserMessage =
        "The site has no Jira user other than the service account, so Connapse can't confirm it may check what other people "
        + "can read in Jira. The site can be saved with this warning, and the check runs again at each test. Or add another user and test again.";

    internal const string NotJiraAdministratorMessage =
        "The service account doesn't have the Administer Jira permission, so Connapse can't check what other people may read "
        + "in Jira. In Atlassian Administration, go to Directory → Service accounts and set Jira to User and Jira "
        + "Administration to App admin, then test again. If it still fails, add the service account to the "
        + "jira-admins-<site> group under Directory → Groups, which holds Administer Jira, and test again.";

    internal const string CantListUsersMessage =
        "The service account can't list Jira users, so Connapse can't find another person to check access for. Check that "
        + "its credential has the " + AtlassianSiteScopes.JiraUser + " scope and that it has the Browse users and groups "
        + "global permission, then test again.";

    internal const string UnverifiedMessage =
        "Couldn't verify Administer Jira access because Atlassian didn't answer the check properly. Wait a minute and test again.";

    internal const string CantReadMembershipMessage =
        "The service account can't read Jira group or project role membership, so Connapse can't tell who may read a comment "
        + "restricted to a group or role. Check that it has the Administer Jira permission and that its credential has "
        + AtlassianSiteScopes.JiraGroup + " and " + AtlassianSiteScopes.JiraProjectRole + ", then test again.";

    public async Task<ProductProbeResult> RunAsync(
        AtlassianSite site, string secret, AtlassianTokenSource tokens, Run steps, CancellationToken linked)
    {
        var run = steps.For(Product);
        IReadOnlyList<string>? granted = tokens.GrantedScopes(site);

        using var http = httpClients.CreateClient(AtlassianApiClient.HttpClientName);
        var api = new AtlassianApiClient(http, tokens, site, secret, time, downloads: null, product: AtlassianProduct.Jira);

        run.Enter(JiraIdentityStep);
        string? ownAccountId;
        try
        {
            using var myself = await api.GetJsonAsync<JsonDocument>("rest/api/3/myself", linked);
            ownAccountId = Text(myself.RootElement, "accountId");
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return run.Fail(CantReadJiraMessage, detail: Describe(ex));
        }

        // Without the service account's own id the "other user" below could be the account itself.
        if (string.IsNullOrWhiteSpace(ownAccountId))
            return run.Fail(CantReadJiraMessage, detail: "Atlassian didn't say which account the credential belongs to.");

        run.Enter(JiraProjectsStep);
        string? projectId;
        List<long> browsable;
        try
        {
            // Up to 50, for the issue search below when the first project has none.
            using var projects = await api.GetJsonAsync<JsonDocument>(
                $"rest/api/3/project/search?maxResults={MaxProjectsSearched}&action=browse&status=live", linked);
            projectId = FirstIdIn(projects.RootElement, "values");
            browsable = NumericIdsIn(projects.RootElement, "values", MaxProjectsSearched);
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return run.Fail(MissingScopeMessage(AtlassianSiteScopes.JiraProject), AtlassianSiteTroubleshooting.MissingScope, Describe(ex));
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return run.Fail(CantReadJiraMessage, detail: Describe(ex));
        }

        if (projectId is null)
            return run.Warn(NoProjectsMessage);

        // The permission check names projects by their numeric id; anything else is not Jira's answer.
        if (!long.TryParse(projectId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long project))
            return run.Fail(CantReadJiraMessage, detail: "Jira listed a project without a numeric id.");

        // One read per call a sync makes, each the kind of call the connector makes, so a credential
        // missing a scope fails here and not at sync.
        run.Enter(JiraContentStep);
        var (issueId, searchFailure) = await FirstIssueAsync(api, $"project = {project}", run, linked);
        if (searchFailure is not null)
            return searchFailure;
        // The first project may have no issue the account can see; another it browses may.
        if (issueId is null && browsable.Any(id => id != project))
        {
            (issueId, searchFailure) = await FirstIssueAsync(
                api, $"project in ({string.Join(',', browsable.Prepend(project).Distinct().Take(MaxProjectsSearched))})", run, linked);
            if (searchFailure is not null)
                return searchFailure;
        }

        if (issueId is not null)
        {
            var (fetched, fetchFailure) = await ReadJsonAsync(
                () => api.PostAsync("rest/api/3/issue/bulkfetch", new { issueIdsOrKeys = new[] { issueId }, fields = new[] { "id" } }, linked),
                AtlassianSiteScopes.JiraIssue, run, linked);
            if (fetchFailure is not null)
                return fetchFailure;
            fetched!.Dispose();

            var (outcome, detail) = await ProbeAsync(api, $"rest/api/3/issue/{Uri.EscapeDataString(issueId)}/comment?maxResults=1", linked);
            if (outcome == Probe.Missing)
                return run.Fail(MissingScopeMessage(AtlassianSiteScopes.JiraComment), AtlassianSiteTroubleshooting.MissingScope, detail);
            if (outcome == Probe.Unanswered)
                return run.Fail(
                    $"Couldn't check the {AtlassianSiteScopes.JiraComment} scope because Atlassian didn't answer properly. "
                    + "Wait a minute and test again.",
                    AtlassianSiteTroubleshooting.General, detail);
        }

        // Optional: recorded as confirmed when it passes, never a failure without it.
        // Attachment 0 never exists: Atlassian checks the scope first, so "not found" proves it, and the
        // content endpoint needs read:attachment:jira alone (attachment/meta needs one more).
        bool attachmentsConfirmed = (await ProbeAsync(api, "rest/api/3/attachment/content/0", linked)).Outcome == Probe.Granted;

        run.Enter(JiraAdminStep);
        string? otherUser;
        try
        {
            using var users = await api.GetJsonAsync<JsonDocument>("rest/api/3/users/search?maxResults=50", linked);
            otherUser = FirstOtherPerson(users.RootElement, ownAccountId);
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return run.Fail(CantListUsersMessage, AtlassianSiteTroubleshooting.MissingScope, Describe(ex));
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return run.Fail(UnverifiedMessage, AtlassianSiteTroubleshooting.General, Describe(ex));
        }

        // Jira answers a check without an account for the caller: never send one without another person.
        if (otherUser is null)
            return run.Warn(NoOtherUserMessage);

        bool scopeMissing = granted is not null
            && !granted.Contains(AtlassianSiteScopes.JiraPermission, StringComparer.OrdinalIgnoreCase);
        try
        {
            using var check = await api.PostAsync("rest/api/3/permissions/check", new
            {
                accountId = otherUser,
                projectPermissions = new[] { new { permissions = new[] { "BROWSE_PROJECTS" }, projects = new[] { project } } },
            }, linked);
            string body = await check.Content.ReadAsStringAsync(linked);

            if (check.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                string checkDetail = DescribeAnswer(check.StatusCode, body);
                return scopeMissing || body.Contains("scope", StringComparison.OrdinalIgnoreCase)
                    ? run.Fail(MissingScopeMessage(AtlassianSiteScopes.JiraPermission), AtlassianSiteTroubleshooting.MissingScope, checkDetail)
                    : run.Fail(NotJiraAdministratorMessage, detail: checkDetail);
            }
            if (check.StatusCode != HttpStatusCode.OK)
                return run.Fail($"{UnverifiedMessage} Jira answered HTTP {(int)check.StatusCode}.", AtlassianSiteTroubleshooting.General,
                    DescribeAnswer(check.StatusCode, body));

            // A 200 proves nothing by itself (a login page can be a 200): only an answer carrying a
            // projectPermissions list shows the check ran for that person.
            using var answer = JsonDocument.Parse(body);
            if (!HasArray(answer.RootElement, "projectPermissions"))
                return run.Fail(UnverifiedMessage, AtlassianSiteTroubleshooting.General, "Jira answered the check without a list of project permissions.");
        }
        catch (AtlassianAuthException ex)
        {
            return run.Fail(MissingScopeMessage(AtlassianSiteScopes.JiraPermission), AtlassianSiteTroubleshooting.MissingScope,
                ex.Detail ?? ex.Message);
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return run.Fail(UnverifiedMessage, AtlassianSiteTroubleshooting.General, Describe(ex));
        }

        // Comments restricted to a group or project role are matched against membership at search time.
        run.Enter(JiraMembershipStep);
        try
        {
            using var groups = await api.GetJsonAsync<JsonDocument>(
                $"rest/api/3/user/groups?accountId={Uri.EscapeDataString(otherUser)}", linked);
            if (groups.RootElement.ValueKind != JsonValueKind.Array)
                return run.Fail(CantReadMembershipMessage, detail: "Jira answered the group lookup without a list.");

            using var roles = await api.GetJsonAsync<JsonDocument>($"rest/api/3/project/{project}/role", linked);
            if (roles.RootElement.ValueKind != JsonValueKind.Object)
                return run.Fail(CantReadMembershipMessage, detail: "Jira answered the role list without its roles.");

            // Only the trailing number of a role's address is used: the address itself is never followed.
            // Without a role to read, role membership is unproven, so the step fails.
            if (FirstRoleId(roles.RootElement) is not { } roleId)
                return run.Fail(CantReadMembershipMessage, detail: "Jira listed no project role Connapse could read.");

            using var role = await api.GetJsonAsync<JsonDocument>($"rest/api/3/project/{project}/role/{roleId}", linked);
            if (!HasArray(role.RootElement, "actors"))
                return run.Fail(CantReadMembershipMessage, detail: "Jira answered the role without its members.");
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return run.Fail(CantReadMembershipMessage, detail: Describe(ex));
        }

        // Nothing proves the issue and comment scopes: saved anyway, they are not counted as confirmed.
        if (issueId is null)
        {
            run.Enter(JiraContentStep);
            return run.Warn(NoIssuesMessage);
        }

        // What the probes proved, and anything else Atlassian says it granted.
        var proven = new HashSet<string>(AtlassianSiteScopes.JiraProvenByTest, StringComparer.OrdinalIgnoreCase);
        if (attachmentsConfirmed)
            proven.Add(AtlassianSiteScopes.JiraAttachment);
        IReadOnlyList<string> confirmed = AtlassianSiteScopes.Jira.Select(s => s.Name)
            .Where(n => proven.Contains(n) || (granted?.Contains(n, StringComparer.OrdinalIgnoreCase) ?? false))
            .ToList();

        return run.Pass(
            "read Jira, listed projects, read issues and comments, and confirmed it can check who may read each issue and comment",
            confirmed);
    }

    /// <summary>
    /// The most recently updated issue matching <paramref name="filter"/>, or null when there is none.
    /// A GET, as the sync searches: Atlassian requires more scopes for the POST form of this endpoint.
    /// </summary>
    private static async Task<(string? IssueId, ProductProbeResult? Failure)> FirstIssueAsync(
        AtlassianApiClient api, string filter, ProductSteps run, CancellationToken ct)
    {
        string jql = Uri.EscapeDataString($"{filter} ORDER BY updated DESC");
        var (searched, failure) = await ReadJsonAsync(
            () => api.GetAsync($"rest/api/3/search/jql?jql={jql}&maxResults=1&fields=id", ct),
            AtlassianSiteScopes.JiraIssueDetails, run, ct);
        if (failure is not null)
            return (null, failure);
        using JsonDocument answer = searched!;
        return (FirstIdIn(answer.RootElement, "issues"), null);
    }

    /// <summary>
    /// One request that must answer 200 with JSON. A refusal is a missing <paramref name="scope"/>; any
    /// other answer is reported as unanswered, never as a missing scope.
    /// </summary>
    private static async Task<(JsonDocument? Answer, ProductProbeResult? Failure)> ReadJsonAsync(
        Func<Task<HttpResponseMessage>> send, string scope, ProductSteps run, CancellationToken ct)
    {
        try
        {
            using var response = await send();
            string text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return (null, run.Fail(MissingScopeMessage(scope), AtlassianSiteTroubleshooting.MissingScope,
                    DescribeAnswer(response.StatusCode, text)));
            if (response.StatusCode != HttpStatusCode.OK)
                return (null, run.Fail(
                    $"Couldn't check the {scope} scope because Atlassian didn't answer properly. Wait a minute and test again.",
                    AtlassianSiteTroubleshooting.General, DescribeAnswer(response.StatusCode, text)));
            return (JsonDocument.Parse(text), null);
        }
        catch (AtlassianAuthException ex)
        {
            return (null, run.Fail(MissingScopeMessage(scope), AtlassianSiteTroubleshooting.MissingScope, ex.Detail ?? ex.Message));
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            return (null, run.Fail(
                $"Couldn't check the {scope} scope because Atlassian didn't answer properly. Wait a minute and test again.",
                AtlassianSiteTroubleshooting.General, Describe(ex)));
        }
    }

    private static bool HasArray(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array;

    /// <summary>The numeric ids of the first <paramref name="max"/> items of the <paramref name="list"/> array; others are skipped.</summary>
    private static List<long> NumericIdsIn(JsonElement root, string list, int max)
    {
        var ids = new List<long>();
        if (!HasArray(root, list))
            return ids;

        foreach (var item in root.GetProperty(list).EnumerateArray().Take(max))
        {
            string? text = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id)
                ? id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString()
                : null;
            if (long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value))
                ids.Add(value);
        }

        return ids;
    }

    /// <summary>The id of the first item of the <paramref name="list"/> array, as text.</summary>
    private static string? FirstIdIn(JsonElement root, string list)
    {
        if (!HasArray(root, list))
            return null;

        foreach (var item in root.GetProperty(list).EnumerateArray())
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

    /// <summary>
    /// A person other than the service account: an <c>atlassian</c> account, never an app, a customer
    /// or the anonymous user, since a check for any of those says nothing about another person.
    /// </summary>
    private static string? FirstOtherPerson(JsonElement root, string ownAccountId)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var user in root.EnumerateArray())
        {
            string? accountId = Text(user, "accountId");
            if (!string.IsNullOrWhiteSpace(accountId)
                && accountId != ownAccountId
                && !string.Equals(accountId, "anonymous", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Text(user, "accountType"), "atlassian", StringComparison.Ordinal))
                return accountId;
        }

        return null;
    }

    // project/{id}/role answers { "Developers": "https://…/project/10000/role/10002" }.
    private static string? FirstRoleId(JsonElement roles)
    {
        foreach (var role in roles.EnumerateObject())
        {
            if (role.Value.ValueKind != JsonValueKind.String || role.Value.GetString() is not { } address)
                continue;

            if (Connapse.Storage.CloudScope.JiraPermissionChecker.RoleIdOf(address) is { } id)
                return id;
        }

        return null;
    }
}
