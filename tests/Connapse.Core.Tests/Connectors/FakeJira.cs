using System.Net;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// Jira Cloud's REST v3 routes on a <see cref="FakeAtlassianApi"/>, answered for one site. So far it
/// holds what the site test probes: one project, one issue, the service account and one other
/// person, their groups and the project's roles. A route mapped on the fake afterwards replaces
/// the one here.
/// </summary>
public sealed partial class FakeJira(FakeAtlassianApi api, string cloudId)
{
    public const string OwnAccount = "svc-jira";
    public const string OtherAccount = "jira-person";
    public const string ProjectId = "10000";
    public const string IssueId = "20000";
    public const string RoleId = "10002";

    /// <summary>The site's REST v3 root, such as <c>/ex/jira/{cloudId}/rest/api/3</c>.</summary>
    public string Root => $"/ex/jira/{cloudId}/rest/api/3";

    /// <summary>The body of each <c>search/jql</c> request.</summary>
    public List<string> Searches { get; } = [];

    /// <summary>The body of each <c>permissions/check</c> request.</summary>
    public List<string> PermissionChecks { get; } = [];

    /// <summary>Maps every route the site test reads, each answering as a working site would.</summary>
    public FakeJira MapSiteTest()
    {
        api.MapJson(Root + "/myself", new { accountId = OwnAccount, accountType = "app" });
        api.MapJson(Root + "/project/search", new { values = new[] { new { id = ProjectId, key = "ENG" } }, total = 1, isLast = true });
        api.Map(Root + "/search/jql", request =>
        {
            Searches.Add(Body(request));
            return FakeAtlassianApi.Json(new { issues = new[] { new { id = IssueId } } });
        });
        api.MapJson(Root + "/issue/bulkfetch", new { issues = new[] { new { id = IssueId } }, issueErrors = Array.Empty<object>() });
        api.MapJson(Root + $"/issue/{IssueId}/comment", new { comments = Array.Empty<object>(), startAt = 0, maxResults = 1, total = 0 });
        api.MapJson(Root + "/attachment/meta", new { enabled = true, uploadLimit = 10485760 });
        Users(
            new { accountId = OwnAccount, accountType = "app" },
            new { accountId = "customer-1", accountType = "customer" },
            new { accountId = OtherAccount, accountType = "atlassian" });
        api.Map(Root + "/permissions/check", request =>
        {
            PermissionChecks.Add(Body(request));
            return FakeAtlassianApi.Json(new
            {
                projectPermissions = new[] { new { permission = "BROWSE_PROJECTS", projects = new[] { long.Parse(ProjectId) }, issues = Array.Empty<long>() } },
                globalPermissions = Array.Empty<string>(),
            });
        });
        api.MapJson(Root + "/user/groups", new[] { new { name = "jira-software-users", groupId = "6e87dc72-4f1f-421f-9382-2fee8b652487" } });
        api.MapJson(Root + $"/project/{ProjectId}/role", new Dictionary<string, string>
        {
            ["Developers"] = $"https://api.atlassian.com/ex/jira/{cloudId}/rest/api/3/project/{ProjectId}/role/{RoleId}",
        });
        api.MapJson(Root + $"/project/{ProjectId}/role/{RoleId}", new { id = long.Parse(RoleId), name = "Developers", actors = Array.Empty<object>() });
        return this;
    }

    /// <summary>Replaces the people <c>users/search</c> lists.</summary>
    public void Users(params object[] users) => api.MapJson(Root + "/users/search", users);

    /// <summary>Answers a path under <see cref="Root"/> with a bare status.</summary>
    public void Answer(string relative, HttpStatusCode status) =>
        api.Map(Root + relative, _ => new HttpResponseMessage(status));

    private static string Body(HttpRequestMessage request) =>
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
}
