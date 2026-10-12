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

    // ── Enforcement: what each account may see, its groups and the projects' roles ──────────

    private readonly Dictionary<(string Account, string Permission), HashSet<long>> _granted = [];
    private readonly Dictionary<string, List<(string Name, string GroupId)>> _groups = [];
    private readonly Dictionary<string, List<(string Name, string RoleId)>> _roles = [];

    /// <summary>
    /// Maps <c>permissions/check</c> and <c>user/groups</c> to answer from the tables below. An
    /// account with no entry is one Jira does not know: a 200 with empty <c>issues</c>, no groups.
    /// </summary>
    public FakeJira MapEnforcement()
    {
        api.Map(Root + "/permissions/check", PermissionCheck);
        api.Map(Root + "/user/groups", request =>
        {
            string account = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["accountId"] ?? "";
            return FakeAtlassianApi.Json(_groups.GetValueOrDefault(account, [])
                .Select(g => new { name = g.Name, groupId = g.GroupId, self = "https://acme.atlassian.net/group" }));
        });
        return this;
    }

    /// <summary>Lets <paramref name="account"/> browse the issues.</summary>
    public void Browse(string account, params string[] issueIds) => Grant(account, "BROWSE_PROJECTS", issueIds);

    /// <summary>Makes <paramref name="account"/> a service-desk agent on the issues.</summary>
    public void Agent(string account, params string[] issueIds) => Grant(account, "SERVICEDESK_AGENT", issueIds);

    private void Grant(string account, string permission, string[] issueIds)
    {
        if (!_granted.TryGetValue((account, permission), out var issues))
            _granted[(account, permission)] = issues = [];
        issues.UnionWith(issueIds.Select(long.Parse));
    }

    /// <summary>Puts <paramref name="account"/> in groups, each a (name, groupId).</summary>
    public void Groups(string account, params (string Name, string GroupId)[] groups) =>
        _groups[account] = [.. groups];

    /// <summary>
    /// Adds a role to a project's role list and serves its actors. Two roles may share a name, as
    /// a duplicated key in Jira's answer.
    /// </summary>
    public void Role(string projectId, string roleId, string name, params object[] actors)
    {
        if (!_roles.TryGetValue(projectId, out var roles))
        {
            _roles[projectId] = roles = [];
            // Written by hand: a dictionary cannot hold the duplicated name an ambiguous answer has.
            api.Map(Root + $"/project/{projectId}/role", _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{" + string.Join(",", _roles[projectId].Select(r =>
                        System.Text.Json.JsonSerializer.Serialize(r.Name) + ":" + System.Text.Json.JsonSerializer.Serialize(
                            $"https://api.atlassian.com/ex/jira/{cloudId}/rest/api/3/project/{projectId}/role/{r.RoleId}"))) + "}",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
        roles.Add((name, roleId));
        api.MapJson(Root + $"/project/{projectId}/role/{roleId}", new { id = long.TryParse(roleId, out long n) ? n : 0, name, actors });
    }

    public static object UserActor(string accountId) =>
        new { type = "atlassian-user-role-actor", actorUser = new { accountId } };

    public static object GroupActor(string groupId, string name = "some-group") =>
        new { type = "atlassian-group-role-actor", actorGroup = new { groupId, name } };

    private HttpResponseMessage PermissionCheck(HttpRequestMessage request)
    {
        string text = Body(request);
        PermissionChecks.Add(text);
        using var body = System.Text.Json.JsonDocument.Parse(text);
        string account = body.RootElement.GetProperty("accountId").GetString()!;
        var answer = new List<object>();
        foreach (var entry in body.RootElement.GetProperty("projectPermissions").EnumerateArray())
        {
            var asked = entry.GetProperty("issues").EnumerateArray().Select(e => e.GetInt64()).ToList();
            foreach (var permission in entry.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!))
            {
                var granted = _granted.GetValueOrDefault((account, permission), []);
                answer.Add(new { permission, issues = asked.Where(granted.Contains).ToArray(), projects = Array.Empty<long>() });
            }
        }
        return FakeAtlassianApi.Json(new { projectPermissions = answer, globalPermissions = Array.Empty<string>() });
    }

    // ── Sync: a stateful project with issues, comments and fields ──────────

    private readonly Dictionary<string, (string Key, string Name)> _projects = [];
    private readonly Dictionary<string, FakeJiraIssue> _issues = [];
    private readonly List<object> _fields = [];

    /// <summary>The <c>jql</c> of each <c>search/jql</c> GET.</summary>
    public List<string> Jqls { get; } = [];

    /// <summary>The ids each <c>bulkfetch</c> asked for, one list per call.</summary>
    public List<List<string>> BulkFetches { get; } = [];

    /// <summary>The most issues one <c>search/jql</c> page holds, whatever <c>maxResults</c> asks.</summary>
    public int SearchPageSize { get; set; } = 5000;

    /// <summary>The most comments one comment page holds, whatever <c>maxResults</c> asks.</summary>
    public int CommentPageSize { get; set; } = 100;

    /// <summary>Maps the routes a sync reads: fields, <c>search/jql</c> (GET) and <c>bulkfetch</c>.</summary>
    public FakeJira MapSync()
    {
        api.Map(Root + "/field", _ => FakeAtlassianApi.Json(_fields));
        api.Map(Root + "/search/jql", Search);
        api.Map(Root + "/issue/bulkfetch", BulkFetch);
        return this;
    }

    public void AddProject(string id, string key, string name)
    {
        _projects[id] = (key, name);
        api.Map(Root + $"/project/{id}", _ => _projects.TryGetValue(id, out var p)
            ? FakeAtlassianApi.Json(new { id, key = p.Key, name = p.Name })
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public void AddField(string id, string name, bool textarea) => _fields.Add(new
    {
        id,
        name,
        custom = id.StartsWith("customfield_", StringComparison.Ordinal),
        schema = textarea
            ? new { type = "string", custom = "com.atlassian.jira.plugin.system.customfieldtypes:textarea" }
            : new { type = "string", custom = "com.atlassian.jira.plugin.system.customfieldtypes:textfield" },
    });

    public FakeJiraIssue AddIssue(FakeJiraIssue issue)
    {
        _issues[issue.Id] = issue;
        api.Map(Root + $"/issue/{issue.Id}", _ => _issues.TryGetValue(issue.Id, out var i)
            ? FakeAtlassianApi.Json(new { id = i.Id, key = i.Key, fields = FullFields(i) })
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        api.Map(Root + $"/issue/{issue.Id}/comment", request => _issues.TryGetValue(issue.Id, out var i)
            ? CommentPage(i, request.RequestUri!)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        return issue;
    }

    public void RemoveIssue(string id) => _issues.Remove(id);

    /// <summary>
    /// Maps <c>GET issue/{id}/comment/{commentId}</c> for each comment the issue has now. Each answers
    /// with the comment's current entry, so a test can replace it; one removed answers 404.
    /// </summary>
    public void ServeComments(FakeJiraIssue issue)
    {
        foreach (string commentId in issue.Comments.Select(c => c.Id).ToList())
        {
            api.Map(Root + $"/issue/{issue.Id}/comment/{commentId}", request =>
            {
                if ((ChangedSincePageRead.GetValueOrDefault(commentId) ?? issue.Comments.FirstOrDefault(c => c.Id == commentId)) is not { } live)
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                bool rendered = request.RequestUri!.Query.Contains("expand=renderedBody", StringComparison.Ordinal);
                if (rendered)
                    RenderedReads.Add(commentId);
                return FakeAtlassianApi.Json(Comment(live, rendered && !WithoutRenderedBody.Contains(commentId)));
            });
        }
    }

    /// <summary>The comment ids read with <c>expand=renderedBody</c>, in order.</summary>
    public List<string> RenderedReads { get; } = [];

    /// <summary>What a comment's own read answers instead of the page's entry, as a comment changed in between.</summary>
    public Dictionary<string, FakeJiraComment> ChangedSincePageRead { get; } = [];

    /// <summary>Comments whose own read leaves out <c>renderedBody</c> even when asked for it.</summary>
    public HashSet<string> WithoutRenderedBody { get; } = [];

    // ── Attachments: listed by bulkfetch, downloaded through a redirect to the media host ──────

    /// <summary>Where <c>attachment/content/{id}</c> redirects, as Jira sends downloads to Atlassian's media service.</summary>
    public const string MediaHost = FakeConfluence.MediaHost;

    /// <summary>Overrides where a download redirects, for the client's host checks.</summary>
    public string? DownloadRedirect { get; set; }

    /// <summary>When set, every attachment download answers this status, as a credential without the attachment scope would.</summary>
    public HttpStatusCode? RefuseDownloadsWith { get; set; }

    private readonly Dictionary<string, (FakeJiraIssue Issue, FakeJiraAttachment Attachment)> _attachments = [];

    /// <summary>Attaches a file to <paramref name="issue"/> and serves its download.</summary>
    public FakeJiraAttachment Attach(FakeJiraIssue issue, FakeJiraAttachment attachment)
    {
        issue.Attachments.Add(attachment);
        _attachments[attachment.Id] = (issue, attachment);
        api.Media = AnswerMedia;
        api.Map(Root + $"/attachment/content/{attachment.Id}", _ =>
        {
            if (RefuseDownloadsWith is { } refused)
                return new HttpResponseMessage(refused);
            if (!issue.Attachments.Contains(attachment))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var redirect = new HttpResponseMessage(HttpStatusCode.SeeOther);
            redirect.Headers.Location = new Uri(DownloadRedirect ?? $"https://{MediaHost}/file/{attachment.Id}/binary?token=media-{attachment.Id}");
            return redirect;
        });
        return attachment;
    }

    /// <summary>The media host's answer for an attachment of this fake; null leaves it to Confluence's.</summary>
    private HttpResponseMessage? AnswerMedia(Uri uri)
    {
        var match = MediaRoute().Match(uri.AbsolutePath);
        if (!match.Success || !_attachments.TryGetValue(match.Groups["att"].Value, out var entry))
            return null;
        return uri.Query.Contains($"token=media-{entry.Attachment.Id}", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(entry.Attachment.Content) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^/file/(?<att>\d+)/binary$")]
    private static partial System.Text.RegularExpressions.Regex MediaRoute();

    /// <summary>Jira's own timestamp shape, with a colon-less offset.</summary>
    public static string Time(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "+0000";

    /// <summary>An ADF document of one paragraph.</summary>
    public static object Adf(string text) => new
    {
        type = "doc",
        version = 1,
        content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text } } } },
    };

    private HttpResponseMessage Search(HttpRequestMessage request)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        string jql = query["jql"] ?? "";
        Jqls.Add(jql);
        var match = JqlPattern().Match(jql);
        if (!match.Success)
            return new HttpResponseMessage(HttpStatusCode.BadRequest);

        string project = match.Groups["project"].Value;
        long? since = match.Groups["since"].Success ? long.Parse(match.Groups["since"].Value) : null;
        var hits = _issues.Values
            .Where(i => i.ProjectId == project && (since is null || i.Updated.ToUnixTimeMilliseconds() >= since))
            .OrderBy(i => match.Groups["order"].Success ? i.Updated.ToUnixTimeMilliseconds() : 0)
            .ThenBy(i => long.Parse(i.Id))
            .ToList();

        int pageSize = Math.Min(SearchPageSize, int.Parse(query["maxResults"] ?? "50"));
        int offset = query["nextPageToken"] is { } token ? int.Parse(token[1..]) : 0;
        var page = hits.Skip(offset).Take(pageSize).Select(i => new { id = i.Id, fields = new { updated = Time(i.Updated) } }).ToList();
        bool last = offset + pageSize >= hits.Count;
        return last
            ? FakeAtlassianApi.Json(new { issues = page, isLast = true })
            : FakeAtlassianApi.Json(new { issues = page, nextPageToken = "t" + (offset + pageSize), isLast = false });
    }

    private HttpResponseMessage BulkFetch(HttpRequestMessage request)
    {
        using var body = System.Text.Json.JsonDocument.Parse(Body(request));
        var ids = body.RootElement.GetProperty("issueIdsOrKeys").EnumerateArray().Select(e => e.GetString()!).ToList();
        BulkFetches.Add(ids);
        var found = ids.Where(_issues.ContainsKey).Select(id => _issues[id]).ToList();
        return FakeAtlassianApi.Json(new
        {
            issues = found.Select(i => new
            {
                id = i.Id,
                key = i.Key,
                fields = new
                {
                    summary = i.Summary,
                    updated = Time(i.Updated),
                    attachment = i.Attachments.Select(a => new
                    {
                        id = a.Id,
                        filename = a.FileName,
                        mimeType = a.MimeType,
                        size = a.DeclaredSize ?? a.Content.Length,
                        created = Time(a.Created),
                        content = $"https://acme.atlassian.net/rest/api/3/attachment/content/{a.Id}",
                    }),
                    comment = new { total = i.Comments.Count, maxResults = 20, startAt = 0, comments = i.Comments.Take(20).Select(Comment) },
                },
            }),
            issueErrors = ids.Where(id => !_issues.ContainsKey(id)).Select(id => new { issueIdsOrKeys = new[] { id }, status = 404 }),
        });
    }

    private static Dictionary<string, object?> FullFields(FakeJiraIssue i)
    {
        var fields = new Dictionary<string, object?>
        {
            ["summary"] = i.Summary,
            ["updated"] = Time(i.Updated),
            ["created"] = Time(i.Created ?? i.Updated),
            ["project"] = new { id = i.ProjectId, key = i.Key.Split('-')[0] },
            ["issuetype"] = new { name = "Task" },
            ["status"] = new { name = "To Do" },
            ["description"] = i.Description,
        };
        foreach (var (name, value) in i.Fields)
            fields[name] = value;
        return fields;
    }

    private HttpResponseMessage CommentPage(FakeJiraIssue issue, Uri uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        int startAt = int.Parse(query["startAt"] ?? "0");
        int maxResults = Math.Min(CommentPageSize, int.Parse(query["maxResults"] ?? "50"));
        var page = issue.Comments.Skip(startAt).Take(maxResults).Select(Comment).ToList();
        return FakeAtlassianApi.Json(new { startAt, maxResults, total = issue.Comments.Count, comments = page });
    }

    private static Dictionary<string, object?> Comment(FakeJiraComment c) => Comment(c, rendered: false);

    private static Dictionary<string, object?> Comment(FakeJiraComment c, bool rendered)
    {
        var comment = new Dictionary<string, object?>
        {
            ["id"] = c.Id,
            ["author"] = new { accountId = "acc-" + c.Author, displayName = c.Author },
            ["body"] = Adf(c.Text),
            ["created"] = Time(c.Created),
            ["updated"] = Time(c.Updated ?? c.Created),
        };
        if (c.Visibility is { } visibility)
            comment["visibility"] = visibility;
        if (c.JsdPublic is { } jsdPublic)
            comment["jsdPublic"] = jsdPublic;
        if (rendered)
            comment["renderedBody"] = c.RenderedBody ?? $"<p>{System.Net.WebUtility.HtmlEncode(c.Text)}</p>";
        return comment;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^project = (?<project>[0-9]+)(?: AND updated >= (?<since>-?[0-9]+))?(?<order> ORDER BY updated ASC)?$")]
    private static partial System.Text.RegularExpressions.Regex JqlPattern();

    // Routes answer synchronously, so the body is read through the content's synchronous stream.
    private static string Body(HttpRequestMessage request)
    {
        if (request.Content is null)
            return "";
        using var reader = new StreamReader(request.Content.ReadAsStream());
        return reader.ReadToEnd();
    }
}

/// <summary>
/// A comment on a <see cref="FakeJiraIssue"/>. <paramref name="Visibility"/> is sent as Jira's visibility object;
/// <paramref name="RenderedBody"/> is the HTML a read with <c>expand=renderedBody</c> answers.
/// </summary>
public sealed record FakeJiraComment(
    string Id, string Author, string Text, DateTimeOffset Created,
    DateTimeOffset? Updated = null, object? Visibility = null, bool? JsdPublic = null, string? RenderedBody = null);

/// <summary>A file attached to a <see cref="FakeJiraIssue"/>. <paramref name="DeclaredSize"/> overrides the size Jira lists.</summary>
public sealed record FakeJiraAttachment(
    string Id, string FileName, byte[] Content, DateTimeOffset Created, long? DeclaredSize = null, string MimeType = "text/plain");

/// <summary>An issue the fake serves; tests edit it in place between syncs.</summary>
public sealed class FakeJiraIssue(string id, string key, string summary, DateTimeOffset updated)
{
    public string Id { get; } = id;
    public string Key { get; set; } = key;
    public string Summary { get; set; } = summary;
    public DateTimeOffset Updated { get; set; } = updated;
    public DateTimeOffset? Created { get; set; }
    public string ProjectId { get; set; } = FakeJira.ProjectId;
    public object? Description { get; set; }
    public List<FakeJiraComment> Comments { get; } = [];
    public List<FakeJiraAttachment> Attachments { get; } = [];

    /// <summary>Extra fields in the full issue read, such as a rich-text custom field's ADF.</summary>
    public Dictionary<string, object?> Fields { get; } = [];
}
