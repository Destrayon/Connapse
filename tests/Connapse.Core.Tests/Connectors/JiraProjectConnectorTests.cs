using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public sealed class JiraProjectConnectorTests : IDisposable
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string ProjectId = FakeJira.ProjectId;

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 11, 0, 0, TimeSpan.Zero);

    private readonly FakeAtlassianApi _api = new();
    private readonly FakeJira _jira;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jira-unit-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly JiraProjectConnector _connector;

    public JiraProjectConnectorTests()
    {
        _connector = Connector();
        _jira = new FakeJira(_api, CloudId).MapSync();
        _jira.AddProject(ProjectId, "ENG", "Engineering");
    }

    private JiraProjectConnector Connector(bool includeAttachments = true, int maxAttachmentMb = 25, int sweepSlice = 500)
    {
        var site = new AtlassianSite("https://acme.atlassian.net", CloudId, "client-1");
        var tokens = new AtlassianTokenSource(new FakeFactory(_api), TimeProvider.System);
        return new JiraProjectConnector(
            new JiraProjectConfig(site, ProjectId, "ENG", _root, includeAttachments, maxAttachmentMb),
            new AtlassianApiClient(_api.CreateClient(), tokens, site, "secret", product: AtlassianProduct.Jira),
            clock: _clock,
            fileTypes: new Extensions(".txt", ".pdf"))
        {
            SweepSliceSize = sweepSlice,
        };
    }

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private FakeJiraIssue Issue(string id, string key, string summary, DateTimeOffset? updated = null) =>
        _jira.AddIssue(new FakeJiraIssue(id, key, summary, updated ?? T0));

    private async Task<string> ReadAsync(string path)
    {
        using var reader = new StreamReader(await _connector.ReadFileAsync(path));
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Sync_FirstSync_ListsEveryIssue()
    {
        Issue("20001", "ENG-1", "First");
        Issue("20002", "ENG-2", "Second");
        Issue("20003", "ENG-3", "Third");
        _jira.SearchPageSize = 2;

        var delta = await _connector.GetChangesAsync(null);

        delta.IsFullListing.Should().BeTrue();
        delta.RequiresFullResync.Should().BeFalse();
        delta.NextCursor.Should().NotBeNull();
        delta.Upserted.Select(f => f.Path).Should().BeEquivalentTo("/issues/20001.md", "/issues/20002.md", "/issues/20003.md");
        var first = delta.Upserted.Single(f => f.Path == "/issues/20001.md");
        first.ResourceUri.Should().Be(AtlassianUri.ForJiraIssue(CloudId, "20001"));
        first.Strategy.Should().Be(ChunkingStrategy.Record);
        first.ContentType.Should().Be("text/markdown");
        first.LastModified.Should().Be(T0.UtcDateTime);
        first.Metadata.Should().Contain(new Dictionary<string, string>
        {
            ["jira:projectKey"] = "ENG",
            ["jira:issueKey"] = "ENG-1",
            ["jira:breadcrumb"] = "Engineering > ENG-1 First",
            ["jira:url"] = "https://acme.atlassian.net/browse/ENG-1",
        });
        _jira.Jqls.Should().OnlyContain(j => j == $"project = {ProjectId}", "the listing pages by token and a first sync has no change query");
        _jira.Jqls.Should().HaveCount(2);
        _jira.BulkFetches.SelectMany(b => b).Should().BeEquivalentTo("20001", "20002", "20003");
    }

    [Fact]
    public async Task Sync_EditedIssue_SignatureChanges()
    {
        var edited = Issue("20001", "ENG-1", "First");
        Issue("20002", "ENG-2", "Second");
        var first = await _connector.GetChangesAsync(null);
        _jira.BulkFetches.Clear();

        _clock.Advance(TimeSpan.FromMinutes(10));
        edited.Summary = "First, edited";
        edited.Updated = _clock.GetUtcNow().AddMinutes(-2);
        var second = await _connector.GetChangesAsync(first.NextCursor);

        Size(second, "/issues/20001.md").Should().NotBe(Size(first, "/issues/20001.md"));
        Size(second, "/issues/20002.md").Should().Be(Size(first, "/issues/20002.md"));
        _jira.BulkFetches.SelectMany(b => b).Should().Equal(new[] { "20001" }, "only the changed issue is fetched again");
    }

    [Fact]
    public async Task Sync_DeletedIssue_DropsOutOfListing()
    {
        Issue("20001", "ENG-1", "First");
        Issue("20002", "ENG-2", "Second");
        var first = await _connector.GetChangesAsync(null);

        _jira.RemoveIssue("20002");
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await _connector.GetChangesAsync(first.NextCursor);

        second.Upserted.Select(f => f.Path).Should().Equal("/issues/20001.md");
        (await _connector.ExistsAsync("/issues/20002.md")).Should().BeFalse();
    }

    [Fact]
    public async Task Sync_IssueMovedToOtherProject_DropsOut()
    {
        var moved = Issue("20001", "ENG-1", "First");
        Issue("20002", "ENG-2", "Second");
        var first = await _connector.GetChangesAsync(null);

        moved.ProjectId = "10001";
        moved.Key = "OPS-7";
        moved.Updated = T0.AddMinutes(1);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await _connector.GetChangesAsync(first.NextCursor);

        second.Upserted.Select(f => f.Path).Should().Equal("/issues/20002.md");
    }

    [Fact]
    public async Task Sync_ChangeQuery_UsesUnquotedEpochMsMinusFiveMinutes()
    {
        Issue("20001", "ENG-1", "First");
        long startedMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var first = await _connector.GetChangesAsync(null);
        _jira.Jqls.Clear();

        _clock.Advance(TimeSpan.FromMinutes(5));
        await _connector.GetChangesAsync(first.NextCursor);

        _jira.Jqls.Should().Contain($"project = {ProjectId} AND updated >= {startedMs - 300_000} ORDER BY updated ASC");
        _jira.Jqls.Should().NotContain(j => j.Contains('"') || j.Contains('\''), "a quoted date is read in the account's timezone");
    }

    [Fact]
    public async Task Sync_RateLimited_DeferredKeepsCursor()
    {
        Issue("20001", "ENG-1", "First");
        var first = await _connector.GetChangesAsync(null);

        _api.RateLimitNext = true;
        var second = await _connector.GetChangesAsync(first.NextCursor);

        second.Deferred.Should().BeTrue();
        second.NextCursor.Should().Be(first.NextCursor);
        second.Upserted.Should().BeEmpty();
        second.IsFullListing.Should().BeFalse("an empty deferred delta must not read as deletions");
        (await _connector.ListFilesAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Sync_RateLimitedOnSecondListingPage_DeferredKeepsCursorAndStore()
    {
        Issue("20001", "ENG-1", "First");
        Issue("20002", "ENG-2", "Second");
        Issue("20003", "ENG-3", "Third");
        _jira.SearchPageSize = 2;
        var first = await _connector.GetChangesAsync(null);

        _jira.RemoveIssue("20003");
        _jira.SearchPageSize = 1;
        _api.Intercept = uri => uri.AbsolutePath.EndsWith("/search/jql", StringComparison.Ordinal)
            && uri.Query.Contains("nextPageToken", StringComparison.Ordinal) ? RateLimited() : null;
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await _connector.GetChangesAsync(first.NextCursor);

        second.Deferred.Should().BeTrue();
        second.NextCursor.Should().Be(first.NextCursor);
        _jira.Jqls.Should().Contain(j => j == $"project = {ProjectId}", "the listing's first page was read before the limit");
        (await _connector.ListFilesAsync()).Select(f => f.Path)
            .Should().BeEquivalentTo("/issues/20001.md", "/issues/20002.md", "/issues/20003.md");
    }

    [Fact]
    public async Task Sync_RateLimitedDuringBulkFetchOnFreshStart_DeferredAndStoreUntouched()
    {
        var edited = Issue("20001", "ENG-1", "First");
        Issue("20002", "ENG-2", "Second");
        await _connector.GetChangesAsync(null);

        _jira.RemoveIssue("20002");
        edited.Summary = "First, edited";
        _api.Intercept = uri => uri.AbsolutePath.EndsWith("/issue/bulkfetch", StringComparison.Ordinal) ? RateLimited() : null;
        var second = await _connector.GetChangesAsync(null);

        second.Deferred.Should().BeTrue();
        second.NextCursor.Should().BeNull("a fresh start's cursor is null, and it is kept");
        var files = await _connector.ListFilesAsync();
        files.Select(f => f.Path).Should().BeEquivalentTo("/issues/20001.md", "/issues/20002.md");
        files.Single(f => f.Path == "/issues/20001.md").Metadata!["jira:breadcrumb"].Should().Be("Engineering > ENG-1 First");
    }

    private void BulkFetchAnswersWithIssueError(object issueError) =>
        _api.Map(_jira.Root + "/issue/bulkfetch", _ => FakeAtlassianApi.Json(new
        {
            issues = new[] { new { id = "20001", key = "ENG-1", fields = new { summary = "First", updated = FakeJira.Time(T0), attachment = Array.Empty<object>(), comment = new { total = 0 } } } },
            issueErrors = new[] { issueError },
        }));

    [Fact]
    public async Task Sync_FreshStart_IssueBulkFetchDidNotReturn_HasNoRecord()
    {
        Issue("20001", "ENG-1", "First");
        Issue("20002", "ENG-2", "Second");
        await _connector.GetChangesAsync(null);

        // Listed, but bulkfetch reports it gone (404): its record from before the resync is not kept.
        BulkFetchAnswersWithIssueError(new { issueIdsOrKeys = new[] { "20002" }, status = 404 });
        var second = await _connector.GetChangesAsync(null);

        second.Upserted.Select(f => f.Path).Should().Equal("/issues/20001.md");
        (await _connector.ExistsAsync("/issues/20002.md")).Should().BeFalse();
    }

    [Fact]
    public async Task Sync_IssueBulkFetchError404_IssueDropsOut()
    {
        Issue("20001", "ENG-1", "First");
        var second = Issue("20002", "ENG-2", "Second");
        var first = await _connector.GetChangesAsync(null);

        _clock.Advance(TimeSpan.FromMinutes(10));
        second.Updated = _clock.GetUtcNow().AddMinutes(-1);
        BulkFetchAnswersWithIssueError(new { issueIdsOrKeys = new[] { "20002" }, status = 404 });
        var delta = await _connector.GetChangesAsync(first.NextCursor);

        delta.Upserted.Select(f => f.Path).Should().Equal("/issues/20001.md");
    }

    public static TheoryData<object> FailedIssueErrors => new()
    {
        new { issueIdsOrKeys = new[] { "20002" }, status = 500 },
        new { issueIdsOrKeys = new[] { "20002" }, status = 403 },
        new { issueIdsOrKeys = new[] { "20002" } },
    };

    [Theory]
    [MemberData(nameof(FailedIssueErrors))]
    public async Task Sync_FreshStart_IssueBulkFetchErrorNot404_UnavailableAndStoreUntouched(object issueError)
    {
        var edited = Issue("20001", "ENG-1", "First");
        Issue("20002", "ENG-2", "Second");
        await _connector.GetChangesAsync(null);

        edited.Summary = "First, edited";
        BulkFetchAnswersWithIssueError(issueError);
        await FluentActions.Awaiting(() => _connector.GetChangesAsync(null))
            .Should().ThrowAsync<AtlassianUnavailableException>();

        var files = await _connector.ListFilesAsync();
        files.Select(f => f.Path).Should().BeEquivalentTo("/issues/20001.md", "/issues/20002.md");
        files.Single(f => f.Path == "/issues/20001.md").Metadata!["jira:breadcrumb"].Should().Be("Engineering > ENG-1 First");
    }

    [Fact]
    public async Task Sync_IssueBulkFetchError500_UnavailableAndStoreUntouched()
    {
        Issue("20001", "ENG-1", "First");
        var second = Issue("20002", "ENG-2", "Second");
        var first = await _connector.GetChangesAsync(null);

        _clock.Advance(TimeSpan.FromMinutes(10));
        second.Updated = _clock.GetUtcNow().AddMinutes(-1);
        BulkFetchAnswersWithIssueError(new { issueIdsOrKeys = new[] { "20002" }, status = 500 });
        await FluentActions.Awaiting(() => _connector.GetChangesAsync(first.NextCursor))
            .Should().ThrowAsync<AtlassianUnavailableException>();

        (await _connector.ListFilesAsync()).Select(f => f.Path).Should().BeEquivalentTo("/issues/20001.md", "/issues/20002.md");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public async Task Sync_BadCursor_RequiresFullResync(string cursor)
    {
        var delta = await _connector.GetChangesAsync(cursor);

        delta.RequiresFullResync.Should().BeTrue();
        delta.Upserted.Should().BeEmpty();
        _jira.Jqls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    [InlineData(System.Net.HttpStatusCode.NotFound)]
    public async Task Sync_ProjectForbidden_ThrowsUnreadable(System.Net.HttpStatusCode status)
    {
        _jira.Answer($"/project/{ProjectId}", status);

        var ex = await FluentActions.Awaiting(() => _connector.GetChangesAsync(null))
            .Should().ThrowAsync<JiraProjectUnreadableException>();
        ex.Which.Should().BeAssignableTo<SourceAccessRevokedException>();
        ex.Which.Message.Should().Contain("ENG");
    }

    [Fact]
    public async Task Read_PublicCommentsInline_RestrictedAndInternalLeftOut()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Description = FakeJira.Adf("Steps to reproduce.");
        issue.Comments.Add(new FakeJiraComment("1", "Ada", "Public remark", T0));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Group secret", T0.AddMinutes(1),
            Visibility: new { type = "group", value = "admins", identifier = "6e87dc72-4f1f-421f-9382-2fee8b652487" }));
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "Role secret", T0.AddMinutes(2),
            Visibility: new { type = "role", value = "Developers", identifier = "Developers" }));
        issue.Comments.Add(new FakeJiraComment("4", "Di", "Internal note", T0.AddMinutes(3), JsdPublic: false));
        issue.Comments.Add(new FakeJiraComment("5", "Ed", "Customer reply", T0.AddMinutes(4), JsdPublic: true));
        await _connector.GetChangesAsync(null);

        string text = await ReadAsync("/issues/20001.md");

        text.Should().StartWith("# Engineering > ENG-1 Login fails\n");
        text.Should().Contain("## Description\n").And.Contain("Steps to reproduce.");
        text.Should().Contain("## Comments\n");
        text.Should().Contain("--- Comment by Ada, 2026-10-01 ---\nPublic remark");
        text.Should().Contain("--- Comment by Ed, 2026-10-01 ---\nCustomer reply");
        text.Should().NotContain("Group secret").And.NotContain("Role secret").And.NotContain("Internal note");
        text.Should().NotContain("Bob").And.NotContain("Cy,").And.NotContain("Di,");
    }

    [Fact]
    public async Task Read_VisibilityWithoutType_LeftOut()
    {
        var issue = Issue("20001", "ENG-1", "Odd");
        issue.Comments.Add(new FakeJiraComment("1", "Ada", "Shape unknown", T0, Visibility: new { value = "someone" }));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Public remark", T0.AddMinutes(1)));
        await _connector.GetChangesAsync(null);

        string text = await ReadAsync("/issues/20001.md");

        text.Should().Contain("Public remark").And.NotContain("Shape unknown");
    }

    [Fact]
    public async Task Read_LinksAndParent_ShowKeysNeverOtherIssuesSummaries()
    {
        var issue = Issue("20001", "ENG-1", "Child");
        issue.Fields["parent"] = new { id = "30000", key = "ENG-100", fields = new { summary = "Parent secret epic" } };
        issue.Fields["issuelinks"] = new object[]
        {
            new { type = new { inward = "is blocked by", outward = "blocks" }, outwardIssue = new { key = "SEC-9", fields = new { summary = "Secret merger plan" } } },
            new { type = new { inward = "is blocked by", outward = "blocks" }, inwardIssue = new { key = "OPS-3", fields = new { summary = "Hidden outage" } } },
        };
        await _connector.GetChangesAsync(null);

        string text = await ReadAsync("/issues/20001.md");

        text.Should().Contain("Parent: ENG-100").And.Contain("Link: blocks SEC-9").And.Contain("Link: is blocked by OPS-3");
        text.Should().NotContain("Parent secret epic").And.NotContain("Secret merger plan").And.NotContain("Hidden outage");
    }

    [Fact]
    public async Task Read_IssueWith250Comments_RendersAll()
    {
        var issue = Issue("20001", "ENG-1", "Busy");
        for (int i = 1; i <= 250; i++)
            issue.Comments.Add(new FakeJiraComment(i.ToString(), "Ada", $"Remark {i}.", T0.AddMinutes(i)));
        _jira.CommentPageSize = 40;
        await _connector.GetChangesAsync(null);

        string text = await ReadAsync("/issues/20001.md");

        text.Split('\n').Count(l => l.StartsWith("--- Comment by ", StringComparison.Ordinal)).Should().Be(250);
        text.Should().Contain("Remark 1.").And.Contain("Remark 250.");
    }

    [Fact]
    public async Task Read_RichTextCustomField_RenderedUnderItsName()
    {
        _jira.AddField("customfield_10050", "Acceptance criteria", textarea: true);
        _jira.AddField("customfield_10051", "Team code", textarea: false);
        var issue = Issue("20001", "ENG-1", "Feature");
        issue.Fields["customfield_10050"] = FakeJira.Adf("Users can sign in.");
        issue.Fields["customfield_10051"] = "T-9";
        await _connector.GetChangesAsync(null);

        string text = await ReadAsync("/issues/20001.md");

        text.Should().Contain("## Acceptance criteria\nUsers can sign in.");
        text.Should().NotContain("Team code");
        _api.Requests.Should().Contain(r => r.AbsolutePath.EndsWith("/issue/20001", StringComparison.Ordinal)
            && Uri.UnescapeDataString(r.Query).Contains("fields=*navigable,customfield_10050"));
    }

    [Fact]
    public async Task Read_IssueNowInOtherProject_FileNotFound()
    {
        var issue = Issue("20001", "ENG-1", "First");
        await _connector.GetChangesAsync(null);

        issue.ProjectId = "10001";

        await FluentActions.Awaiting(() => _connector.ReadFileAsync("/issues/20001.md"))
            .Should().ThrowAsync<FileNotFoundException>();
        await FluentActions.Awaiting(() => _connector.ReadFileAsync("/issues/99999.md"))
            .Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Breadcrumb_TitleWithGreaterThan_Escaped()
    {
        _jira.AddProject(ProjectId, "ENG", "R&D > Ops");
        Issue("20001", "ENG-1", "Compare a > b");

        var delta = await _connector.GetChangesAsync(null);
        string text = await ReadAsync("/issues/20001.md");

        delta.Upserted.Single().Metadata!["jira:breadcrumb"].Should().Be("R&D › Ops > ENG-1 Compare a › b");
        text.Should().StartWith("# R&D › Ops > ENG-1 Compare a › b\n");
    }

    // ── Restricted comments and internal notes ─────────────────────────────

    private const string GroupA = "6e87dc72-4f1f-421f-9382-2fee8b652487";
    private const string GroupB = "0b5c3a8e-1d2f-4e6a-9b7c-8d9e0f1a2b3c";

    private static object GroupVisibility(string groupId) => new { type = "group", value = "admins", identifier = groupId };

    private static string CommentPath(string issueId, string commentId) => $"/issues/{issueId}/comments/{commentId}.md";

    private static ConnectorFile? Doc(SyncDelta delta, string path) => delta.Upserted.SingleOrDefault(f => f.Path == path);

    [Fact]
    public void Route_NoVisibility_Inline()
    {
        var route = JiraProjectConnector.Route(new JiraStoredComment { Id = "1" }, ProjectId);

        route.Should().Be((false, false, (JiraRestriction?)null, false));
    }

    [Fact]
    public void Route_GroupVisibility_SeparateWithGroupId()
    {
        var comment = new JiraStoredComment { Id = "1", VisibilityType = "group", VisibilityIdentifier = GroupA.ToUpperInvariant() };

        var route = JiraProjectConnector.Route(comment, ProjectId);

        route.Should().Be((true, false, new JiraRestriction(JiraRestrictionType.Group, GroupA), false));
    }

    [Fact]
    public void Route_RoleVisibility_SeparateWithProjectRole()
    {
        var comment = new JiraStoredComment { Id = "1", VisibilityType = "role", VisibilityIdentifier = "Developers" };

        var route = JiraProjectConnector.Route(comment, ProjectId);

        route.Should().Be((true, false, new JiraRestriction(JiraRestrictionType.Role, "Developers", ProjectId), false));
    }

    [Fact]
    public void Route_InternalNote_SeparateInternal()
    {
        var route = JiraProjectConnector.Route(new JiraStoredComment { Id = "1", Internal = true }, ProjectId);

        route.Should().Be((true, true, (JiraRestriction?)null, false));
    }

    [Fact]
    public void Route_InternalNoteWithGroup_Both()
    {
        var comment = new JiraStoredComment { Id = "1", VisibilityType = "group", VisibilityIdentifier = GroupA, Internal = true };

        var route = JiraProjectConnector.Route(comment, ProjectId);

        route.Should().Be((true, true, new JiraRestriction(JiraRestrictionType.Group, GroupA), false));
    }

    [Fact]
    public async Task Sync_BulkFetchCountsNoComments_CommentsStillRead()
    {
        // Seen on a live site: bulkfetch's comment total was 0 for an issue whose comment list
        // returned a comment restricted to the role "QA/Ops Team".
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "Role secret", T0,
            Visibility: new { type = "role", value = "QA/Ops Team", identifier = "QA/Ops Team" }));
        _jira.BulkCommentTotal = 0;

        var delta = await _connector.GetChangesAsync(null);

        delta.Upserted.Select(f => f.Path).Should().Contain(CommentPath("20001", "3"));
        Doc(delta, CommentPath("20001", "3"))!.ResourceUri.Should().Be(
            AtlassianUri.ForJiraComment(CloudId, "20001", "3", false, new JiraRestriction(JiraRestrictionType.Role, "QA/Ops Team", ProjectId)));
    }

    [Fact]
    public async Task Sync_RestrictedAndInternalComments_EachTheirOwnDocument()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("1", "Ada", "Public remark", T0));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Group secret", T0.AddMinutes(1), Visibility: GroupVisibility(GroupA)));
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "Role secret", T0.AddMinutes(2),
            Visibility: new { type = "role", value = "Developers", identifier = "Developers" }));
        issue.Comments.Add(new FakeJiraComment("4", "Di", "Internal note", T0.AddMinutes(3), JsdPublic: false));

        var delta = await _connector.GetChangesAsync(null);

        delta.Upserted.Select(f => f.Path).Should().BeEquivalentTo(
            "/issues/20001.md", CommentPath("20001", "2"), CommentPath("20001", "3"), CommentPath("20001", "4"));
        Doc(delta, CommentPath("20001", "2"))!.ResourceUri.Should().Be(
            AtlassianUri.ForJiraComment(CloudId, "20001", "2", false, new JiraRestriction(JiraRestrictionType.Group, GroupA)));
        Doc(delta, CommentPath("20001", "3"))!.ResourceUri.Should().Be(
            AtlassianUri.ForJiraComment(CloudId, "20001", "3", false, new JiraRestriction(JiraRestrictionType.Role, "Developers", ProjectId)));
        Doc(delta, CommentPath("20001", "4"))!.ResourceUri.Should().Be(AtlassianUri.ForJiraComment(CloudId, "20001", "4", true, null));
        foreach (var file in delta.Upserted)
            AtlassianUri.TryParseJira(file.ResourceUri, out _).Should().BeTrue($"{file.Path} must carry a parseable URI");

        var group = Doc(delta, CommentPath("20001", "2"))!;
        group.SizeBytes.Should().Be(T0.AddMinutes(1).ToUnixTimeMilliseconds());
        group.LastModified.Should().Be(T0.AddMinutes(1).UtcDateTime);
        group.ContentType.Should().Be("text/markdown");
        group.Metadata.Should().Contain(new Dictionary<string, string>
        {
            ["jira:issueKey"] = "ENG-1",
            ["jira:breadcrumb"] = "Engineering > ENG-1 Login fails > Comment",
            ["jira:visibility"] = "group",
            ["jira:url"] = "https://acme.atlassian.net/browse/ENG-1?focusedCommentId=2",
        });
        Doc(delta, CommentPath("20001", "3"))!.Metadata!["jira:visibility"].Should().Be("role:Developers");
        Doc(delta, CommentPath("20001", "4"))!.Metadata!["jira:visibility"].Should().Be("internal");
    }

    [Fact]
    public async Task Sync_GroupVisibilityWithoutIdentifier_IndexedAsDenyAll()
    {
        var issue = Issue("20001", "ENG-1", "Old comment");
        issue.Comments.Add(new FakeJiraComment("7", "Ada", "Named group only", T0, Visibility: new { type = "group", value = "admins" }));

        var delta = await _connector.GetChangesAsync(null);

        string? uri = Doc(delta, CommentPath("20001", "7"))!.ResourceUri;
        uri.Should().Be(AtlassianUri.ForJiraComment(CloudId, "20001", "7", false, JiraRestriction.DenyAll));
        AtlassianUri.TryParseJira(uri, out var resource).Should().BeTrue();
        resource.Restriction.Should().Be(new JiraRestriction(JiraRestrictionType.Group, "00000000-0000-0000-0000-000000000000"));
        (await ReadAsync("/issues/20001.md")).Should().NotContain("Named group only");
    }

    [Fact]
    public async Task Sync_UnknownVisibilityType_IndexedAsDenyAll()
    {
        var issue = Issue("20001", "ENG-1", "Odd");
        issue.Comments.Add(new FakeJiraComment("7", "Ada", "Some new kind", T0, Visibility: new { type = "user", identifier = "acc-1" }));
        issue.Comments.Add(new FakeJiraComment("8", "Bob", "Group by name", T0, Visibility: new { type = "group", identifier = "admins" }));
        issue.Comments.Add(new FakeJiraComment("9", "Cy", "No type", T0, Visibility: new { value = "someone" }));

        var delta = await _connector.GetChangesAsync(null);

        foreach (string id in new[] { "7", "8", "9" })
            Doc(delta, CommentPath("20001", id))!.ResourceUri
                .Should().Be(AtlassianUri.ForJiraComment(CloudId, "20001", id, false, JiraRestriction.DenyAll));
        string text = await ReadAsync("/issues/20001.md");
        text.Should().NotContain("Some new kind").And.NotContain("Group by name").And.NotContain("No type");
    }

    [Fact]
    public async Task Sync_CommentBecomesRestricted_MovesOutAndIssueReRenders()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("1", "Ada", "Was public", T0));
        var first = await _connector.GetChangesAsync(null);
        (await ReadAsync("/issues/20001.md")).Should().Contain("Was public");

        _clock.Advance(TimeSpan.FromMinutes(10));
        DateTimeOffset edited = _clock.GetUtcNow().AddMinutes(-1);
        issue.Comments[0] = issue.Comments[0] with { Updated = edited, Visibility = GroupVisibility(GroupA) };
        issue.Updated = edited;
        var second = await _connector.GetChangesAsync(first.NextCursor);

        Size(second, "/issues/20001.md").Should().NotBe(Size(first, "/issues/20001.md"));
        Doc(second, CommentPath("20001", "1"))!.ResourceUri.Should().Be(
            AtlassianUri.ForJiraComment(CloudId, "20001", "1", false, new JiraRestriction(JiraRestrictionType.Group, GroupA)));
        (await ReadAsync("/issues/20001.md")).Should().NotContain("Was public");
    }

    [Fact]
    public async Task Sync_CommentBecomesPublic_DocumentDeleted()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("1", "Ada", "Was secret", T0, Visibility: GroupVisibility(GroupA)));
        var first = await _connector.GetChangesAsync(null);
        Doc(first, CommentPath("20001", "1")).Should().NotBeNull();

        _clock.Advance(TimeSpan.FromMinutes(10));
        DateTimeOffset edited = _clock.GetUtcNow().AddMinutes(-1);
        issue.Comments[0] = issue.Comments[0] with { Updated = edited, Visibility = null };
        issue.Updated = edited;
        var second = await _connector.GetChangesAsync(first.NextCursor);

        second.Upserted.Select(f => f.Path).Should().Equal("/issues/20001.md");
        Size(second, "/issues/20001.md").Should().NotBe(Size(first, "/issues/20001.md"));
        (await _connector.ExistsAsync(CommentPath("20001", "1"))).Should().BeFalse();
        (await ReadAsync("/issues/20001.md")).Should().Contain("Was secret");
    }

    [Fact]
    public async Task Sync_RestrictionChangesGroup_UriUpdated()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("1", "Ada", "Secret", T0, Visibility: GroupVisibility(GroupA)));
        var first = await _connector.GetChangesAsync(null);

        _clock.Advance(TimeSpan.FromMinutes(10));
        DateTimeOffset edited = _clock.GetUtcNow().AddMinutes(-1);
        issue.Comments[0] = issue.Comments[0] with { Updated = edited, Visibility = GroupVisibility(GroupB) };
        issue.Updated = edited;
        var second = await _connector.GetChangesAsync(first.NextCursor);

        Doc(second, CommentPath("20001", "1"))!.ResourceUri.Should().Be(
            AtlassianUri.ForJiraComment(CloudId, "20001", "1", false, new JiraRestriction(JiraRestrictionType.Group, GroupB)));
        Size(second, CommentPath("20001", "1")).Should().NotBe(Size(first, CommentPath("20001", "1")));
    }

    [Fact]
    public async Task Sync_DailySweep_RereadsUnchangedIssues()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("1", "Ada", "Secret", T0, Visibility: GroupVisibility(GroupA)));
        Issue("20002", "ENG-2", "Quiet");
        var first = await _connector.GetChangesAsync(null);

        // Restricted to another group without the issue's updated time moving: the change query misses it.
        issue.Comments[0] = issue.Comments[0] with { Visibility = GroupVisibility(GroupB) };
        _jira.BulkFetches.Clear();
        _clock.Advance(TimeSpan.FromMinutes(10));
        var second = await _connector.GetChangesAsync(first.NextCursor);

        _jira.BulkFetches.Should().BeEmpty("no day has passed since the fresh start read every comment");
        Doc(second, CommentPath("20001", "1"))!.ResourceUri.Should().EndWith($"/group/{GroupA}");

        _clock.Advance(TimeSpan.FromHours(24));
        var third = await _connector.GetChangesAsync(second.NextCursor);

        _jira.BulkFetches.SelectMany(b => b).Should().BeEquivalentTo("20001", "20002");
        Doc(third, CommentPath("20001", "1"))!.ResourceUri.Should().EndWith($"/group/{GroupB}");

        _jira.BulkFetches.Clear();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await _connector.GetChangesAsync(third.NextCursor);

        _jira.BulkFetches.Should().BeEmpty("the sweep's time was carried in the cursor");
    }

    [Fact]
    public async Task Sync_RateLimitedDuringSweep_CycleAppliesOrdinaryChangesAndNextCycleFinishesSweep()
    {
        var quiet = Issue("20001", "ENG-1", "Login fails");
        quiet.Comments.Add(new FakeJiraComment("1", "Ada", "Secret", T0, Visibility: GroupVisibility(GroupA)));
        var edited = Issue("20002", "ENG-2", "Second");
        var first = await _connector.GetChangesAsync(null);

        _clock.Advance(TimeSpan.FromHours(25));
        quiet.Comments[0] = quiet.Comments[0] with { Visibility = GroupVisibility(GroupB) };
        edited.Summary = "Second, edited";
        edited.Updated = _clock.GetUtcNow();
        // Every read of the quiet issue's comments is refused: the sweep cannot finish this cycle.
        _api.Intercept = uri => uri.AbsolutePath.EndsWith("/issue/20001/comment", StringComparison.Ordinal) ? RateLimited() : null;
        var second = await _connector.GetChangesAsync(first.NextCursor);

        second.Deferred.Should().BeFalse("a rate limit in the sweep must not hold back the cycle's ordinary changes");
        second.IsFullListing.Should().BeTrue();
        second.NextCursor.Should().NotBe(first.NextCursor);
        Doc(second, "/issues/20002.md")!.Metadata!["jira:breadcrumb"].Should().Be("Engineering > ENG-2 Second, edited");
        Doc(second, CommentPath("20001", "1"))!.ResourceUri
            .Should().EndWith($"/group/{GroupA}", "an issue the sweep could not read in full keeps its last record");

        _api.Intercept = null;
        _jira.BulkFetches.Clear();
        _clock.Advance(TimeSpan.FromMinutes(10));
        var third = await _connector.GetChangesAsync(second.NextCursor);

        _jira.BulkFetches.SelectMany(b => b).Should().Contain("20001", "the unfinished sweep carries on");
        Doc(third, CommentPath("20001", "1"))!.ResourceUri.Should().EndWith($"/group/{GroupB}");

        _jira.BulkFetches.Clear();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await _connector.GetChangesAsync(third.NextCursor);

        _jira.BulkFetches.Should().BeEmpty("the sweep finished, so the next one is a day away");
    }

    [Fact]
    public async Task Sync_RateLimitedDuringSweep_IssuesReadInFullAreKeptAndSweepResumesAfterThem()
    {
        var connector = Connector(sweepSlice: 10);
        var a = Issue("20001", "ENG-1", "A");
        a.Comments.Add(new FakeJiraComment("1", "Ada", "Secret", T0, Visibility: GroupVisibility(GroupA)));
        var b = Issue("20002", "ENG-2", "B");
        b.Comments.Add(new FakeJiraComment("2", "Bob", "Secret", T0, Visibility: GroupVisibility(GroupA)));
        var c = Issue("20003", "ENG-3", "C");
        c.Comments.Add(new FakeJiraComment("3", "Cy", "Secret", T0, Visibility: GroupVisibility(GroupA)));
        var first = await connector.GetChangesAsync(null);

        _clock.Advance(TimeSpan.FromHours(25));
        foreach (var issue in new[] { a, b, c })
            issue.Comments[0] = issue.Comments[0] with { Visibility = GroupVisibility(GroupB) };
        _api.Intercept = uri => uri.AbsolutePath.EndsWith("/issue/20002/comment", StringComparison.Ordinal) ? RateLimited() : null;
        var second = await connector.GetChangesAsync(first.NextCursor);

        second.Deferred.Should().BeFalse();
        Doc(second, CommentPath("20001", "1"))!.ResourceUri.Should().EndWith($"/group/{GroupB}", "it was read in full before the limit");
        Doc(second, CommentPath("20002", "2"))!.ResourceUri.Should().EndWith($"/group/{GroupA}", "its read was cut off");
        Doc(second, CommentPath("20003", "3"))!.ResourceUri.Should().EndWith($"/group/{GroupA}", "it was never reached");

        _api.Intercept = null;
        _jira.BulkFetches.Clear();
        _clock.Advance(TimeSpan.FromMinutes(10));
        var third = await connector.GetChangesAsync(second.NextCursor);

        _jira.BulkFetches.SelectMany(x => x).Should().BeEquivalentTo(new[] { "20002", "20003" }, "the sweep resumes after the last issue it read in full");
        Doc(third, CommentPath("20002", "2"))!.ResourceUri.Should().EndWith($"/group/{GroupB}");
        Doc(third, CommentPath("20003", "3"))!.ResourceUri.Should().EndWith($"/group/{GroupB}");
    }

    [Fact]
    public async Task Sync_LargeProject_SweepRunsInSlicesAcrossCycles()
    {
        var connector = Connector(sweepSlice: 2);
        foreach (string id in new[] { "20001", "20002", "20003" })
            Issue(id, "ENG-" + id[^1], "Issue " + id);
        var first = await connector.GetChangesAsync(null);

        _clock.Advance(TimeSpan.FromHours(25));
        _jira.BulkFetches.Clear();
        var second = await connector.GetChangesAsync(first.NextCursor);
        _jira.BulkFetches.SelectMany(x => x).Should().Equal("20001", "20002");

        _jira.BulkFetches.Clear();
        _clock.Advance(TimeSpan.FromMinutes(10));
        var third = await connector.GetChangesAsync(second.NextCursor);
        _jira.BulkFetches.SelectMany(x => x).Should().Equal("20003");

        _jira.BulkFetches.Clear();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await connector.GetChangesAsync(third.NextCursor);
        _jira.BulkFetches.Should().BeEmpty("the sweep is done until a day after it began");
    }

    [Fact]
    public async Task Sync_RateLimitedOnSweepBulkFetch_CycleCompletesAndSweepStaysDue()
    {
        Issue("20001", "ENG-1", "First");
        var first = await _connector.GetChangesAsync(null);

        _clock.Advance(TimeSpan.FromHours(25));
        _api.Intercept = uri => uri.AbsolutePath.EndsWith("/issue/bulkfetch", StringComparison.Ordinal) ? RateLimited() : null;
        var second = await _connector.GetChangesAsync(first.NextCursor);

        second.Deferred.Should().BeFalse();
        second.Upserted.Select(f => f.Path).Should().Equal("/issues/20001.md");

        _api.Intercept = null;
        _jira.BulkFetches.Clear();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await _connector.GetChangesAsync(second.NextCursor);
        _jira.BulkFetches.SelectMany(x => x).Should().Equal("20001");
    }

    [Fact]
    public async Task Read_RestrictedComment_RendersBreadcrumbAuthorAndBody()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Group secret", T0, Visibility: GroupVisibility(GroupA)));
        _jira.ServeComments(issue);
        await _connector.GetChangesAsync(null);

        string text = await ReadAsync(CommentPath("20001", "2"));

        text.Should().Be("# Engineering > ENG-1 Login fails > Comment\n\n--- Comment by Bob, 2026-10-01 ---\nGroup secret\n");
        (await _connector.ExistsAsync(CommentPath("20001", "2"))).Should().BeTrue();
    }

    [Fact]
    public async Task Read_CommentNoLongerRestricted_FileNotFound()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Was secret", T0, Visibility: GroupVisibility(GroupA)));
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "Moved group", T0, Visibility: GroupVisibility(GroupA)));
        issue.Comments.Add(new FakeJiraComment("4", "Di", "Deleted", T0, Visibility: GroupVisibility(GroupA)));
        _jira.ServeComments(issue);
        await _connector.GetChangesAsync(null);

        issue.Comments[0] = issue.Comments[0] with { Visibility = null };
        issue.Comments[1] = issue.Comments[1] with { Visibility = GroupVisibility(GroupB) };
        issue.Comments.RemoveAt(2);

        await FluentActions.Awaiting(() => _connector.ReadFileAsync(CommentPath("20001", "2")))
            .Should().ThrowAsync<FileNotFoundException>();
        await FluentActions.Awaiting(() => _connector.ReadFileAsync(CommentPath("20001", "3")))
            .Should().ThrowAsync<FileNotFoundException>("the listing that queued it carried the old group");
        await FluentActions.Awaiting(() => _connector.ReadFileAsync(CommentPath("20001", "4")))
            .Should().ThrowAsync<FileNotFoundException>();
        await FluentActions.Awaiting(() => _connector.ReadFileAsync(CommentPath("20001", "99")))
            .Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Read_RestrictedCommentIssueMovedToOtherProject_FileNotFound()
    {
        // The role name is the same in both projects, so the comment's own route still matches.
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "Role secret", T0,
            Visibility: new { type = "role", value = "Developers", identifier = "Developers" }));
        _jira.ServeComments(issue);
        await _connector.GetChangesAsync(null);

        issue.ProjectId = "10001";

        await FluentActions.Awaiting(() => _connector.ReadFileAsync(CommentPath("20001", "3")))
            .Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Read_RestrictedCommentIssueProjectUnknown_FailsWithoutContent()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Group secret", T0, Visibility: GroupVisibility(GroupA)));
        _jira.ServeComments(issue);
        await _connector.GetChangesAsync(null);

        _api.Map(_jira.Root + "/issue/20001", _ => FakeAtlassianApi.Json(new { id = "20001", fields = new { } }));
        var unknown = await FluentActions.Awaiting(() => _connector.ReadFileAsync(CommentPath("20001", "2")))
            .Should().ThrowAsync<IOException>();
        unknown.Which.Should().NotBeOfType<FileNotFoundException>("an unknown project is retried, not taken as gone");
    }

    // ── Vanished restricted documents ──────────────────────────────────────

    [Fact]
    public async Task Sync_RestrictedCommentVanishes_ListedOnceMoreAsDenyAllThenDropped()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Group secret", T0, Visibility: GroupVisibility(GroupA)));
        issue.Comments.Add(new FakeJiraComment("4", "Di", "Internal note", T0.AddMinutes(1), JsdPublic: false));
        var first = await _connector.GetChangesAsync(null);

        // Restricted to a group the service account is not in: Jira's answer leaves both out.
        _clock.Advance(TimeSpan.FromMinutes(10));
        issue.Comments.Clear();
        issue.Updated = _clock.GetUtcNow().AddMinutes(-1);
        var second = await _connector.GetChangesAsync(first.NextCursor);

        var group = Doc(second, CommentPath("20001", "2"));
        group.Should().NotBeNull("a vanished restricted comment is listed once more so its URI denies everyone");
        group!.ResourceUri.Should().Be(AtlassianUri.ForJiraComment(CloudId, "20001", "2", false, JiraRestriction.DenyAll));
        group.SizeBytes.Should().Be(Size(first, CommentPath("20001", "2")), "the same signature only rewrites the URI");
        group.LastModified.Should().Be(Doc(first, CommentPath("20001", "2"))!.LastModified);
        Doc(second, CommentPath("20001", "4"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraComment(CloudId, "20001", "4", true, JiraRestriction.DenyAll));
        (await _connector.ExistsAsync(CommentPath("20001", "2"))).Should().BeFalse();
        await FluentActions.Awaiting(() => _connector.ReadFileAsync(CommentPath("20001", "2")))
            .Should().ThrowAsync<FileNotFoundException>();

        _clock.Advance(TimeSpan.FromMinutes(10));
        var third = await _connector.GetChangesAsync(second.NextCursor);

        third.Upserted.Select(f => f.Path).Should().Equal("/issues/20001.md");
    }

    [Fact]
    public async Task Sync_TombstoneCycleNotCommitted_ListedAgain()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Group secret", T0, Visibility: GroupVisibility(GroupA)));
        var first = await _connector.GetChangesAsync(null);

        _clock.Advance(TimeSpan.FromMinutes(10));
        issue.Comments.Clear();
        issue.Updated = _clock.GetUtcNow().AddMinutes(-1);
        await _connector.GetChangesAsync(first.NextCursor);

        // The engine kept the old cursor (it withheld deletions): the tombstone's listing was not taken up.
        _clock.Advance(TimeSpan.FromMinutes(10));
        var again = await _connector.GetChangesAsync(first.NextCursor);

        Doc(again, CommentPath("20001", "2"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraComment(CloudId, "20001", "2", false, JiraRestriction.DenyAll));
        (await _connector.ListFilesAsync()).Select(f => f.Path).Should().Contain(CommentPath("20001", "2"));

        _clock.Advance(TimeSpan.FromMinutes(10));
        var later = await _connector.GetChangesAsync(again.NextCursor);
        later.Upserted.Select(f => f.Path).Should().Equal("/issues/20001.md");
    }

    [Fact]
    public async Task Sync_FreshStart_RestrictedAttachmentsVanish_ListedOnceMoreAsDenyAll()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "secret.txt"));
        _jira.Attach(issue, Attachment("30002", "logs.txt"));
        _jira.Attach(issue, Attachment("30003", "public.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Group secret", T0, Visibility: GroupVisibility(GroupA), RenderedBody: LinkTo("30001")));
        issue.Comments.Add(new FakeJiraComment("4", "Di", "Internal note", T0, JsdPublic: false, RenderedBody: LinkTo("30002")));
        _jira.ServeComments(issue);
        var first = await _connector.GetChangesAsync(null);

        issue.Comments.Clear();
        issue.Attachments.Clear();
        var second = await _connector.GetChangesAsync(null);

        var secret = Doc(second, AttachmentPath("20001", "30001", "secret.txt"));
        secret!.ResourceUri.Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, JiraRestriction.DenyAll));
        secret.SizeBytes.Should().Be(Size(first, AttachmentPath("20001", "30001", "secret.txt")));
        secret.LastModified.Should().Be(AttachedAt.UtcDateTime);
        Doc(second, AttachmentPath("20001", "30002", "logs.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30002", true, JiraRestriction.DenyAll));
        Doc(second, AttachmentPath("20001", "30003", "public.txt")).Should().BeNull("an unrestricted attachment needs no tombstone");
        await FluentActions.Awaiting(() => _connector.ReadFileAsync(AttachmentPath("20001", "30001", "secret.txt")))
            .Should().ThrowAsync<FileNotFoundException>();

        _clock.Advance(TimeSpan.FromMinutes(10));
        var third = await _connector.GetChangesAsync(second.NextCursor);
        third.Upserted.Select(f => f.Path).Should().Equal("/issues/20001.md");
    }

    // ── Attachments ────────────────────────────────────────────────────────

    private static readonly DateTimeOffset AttachedAt = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    private static FakeJiraAttachment Attachment(string id, string name, string text = "attached text", long? declaredSize = null) =>
        new(id, name, System.Text.Encoding.UTF8.GetBytes(text), AttachedAt, declaredSize);

    private static string AttachmentPath(string issueId, string attachmentId, string name) =>
        $"/issues/{issueId}/attachments/{attachmentId}/{name}";

    private static string LinkTo(string attachmentId) =>
        $"<p>See <a href=\"https://acme.atlassian.net/rest/api/3/attachment/content/{attachmentId}\">the file</a></p>";

    private const string SkippedNotice =
        "1 attachment was skipped: larger than {0} MB, or a file type Connapse can't read. "
        + "To include larger files, raise Max attachment size on the source.";

    [Fact]
    public async Task Sync_NewAttachment_Listed()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "notes.txt", "hello"));

        var delta = await _connector.GetChangesAsync(null);

        var file = Doc(delta, AttachmentPath("20001", "30001", "notes.txt"));
        file.Should().NotBeNull();
        file!.ResourceUri.Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, null));
        file.SizeBytes.Should().Be(5);
        file.LastModified.Should().Be(AttachedAt.UtcDateTime);
        file.ContentType.Should().Be("text/plain");
        file.Metadata.Should().Contain(new Dictionary<string, string>
        {
            ["jira:issueKey"] = "ENG-1",
            ["jira:breadcrumb"] = "Engineering > ENG-1 Login fails > notes.txt",
        });
        AtlassianUri.TryParseJira(file.ResourceUri, out _).Should().BeTrue();
        delta.Notice.Should().BeNull();
        _jira.RenderedReads.Should().BeEmpty("an issue with no restricted comments has nothing to tie its attachments to");
    }

    [Fact]
    public async Task Sync_AttachmentLinkedFromGroupComment_CarriesGroup()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "secret.txt"));
        _jira.Attach(issue, Attachment("30002", "diagram.txt"));
        _jira.Attach(issue, Attachment("30003", "public.txt"));
        issue.Comments.Add(new FakeJiraComment("1", "Ada", "Public remark", T0));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Group secret", T0, Visibility: GroupVisibility(GroupA),
            RenderedBody: LinkTo("30001") + "<img src=\"/secure/attachment/30002/diagram.txt\">"));
        _jira.ServeComments(issue);

        var delta = await _connector.GetChangesAsync(null);

        var group = new JiraRestriction(JiraRestrictionType.Group, GroupA);
        Doc(delta, AttachmentPath("20001", "30001", "secret.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, group));
        Doc(delta, AttachmentPath("20001", "30002", "diagram.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30002", false, group));
        Doc(delta, AttachmentPath("20001", "30003", "public.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30003", false, null));
        _jira.RenderedReads.Should().Equal(new[] { "2" }, "only restricted comments are read rendered");
    }

    [Fact]
    public async Task Sync_AttachmentLinkedFromInternalNote_CarriesInternal()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "logs.txt"));
        issue.Comments.Add(new FakeJiraComment("4", "Di", "Internal note", T0, JsdPublic: false, RenderedBody: LinkTo("30001")));
        _jira.ServeComments(issue);

        var delta = await _connector.GetChangesAsync(null);

        Doc(delta, AttachmentPath("20001", "30001", "logs.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", true, null));
    }

    /// <summary>
    /// The seam between the connector and the verifier: every file the listing emits carries a URI
    /// that the verifier's one parser reads back to exactly the restriction the connector meant.
    /// </summary>
    [Fact]
    public async Task Listing_EveryResourceUri_ParsesBackThroughTryParseJira()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "group.txt"));
        _jira.Attach(issue, Attachment("30002", "role.txt"));
        _jira.Attach(issue, Attachment("30003", "denied.txt"));
        _jira.Attach(issue, Attachment("30004", "public.txt"));
        issue.Comments.Add(new FakeJiraComment("1", "Ada", "Public remark", T0));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Group secret", T0, Visibility: GroupVisibility(GroupA),
            RenderedBody: LinkTo("30001")));
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "Role secret", T0,
            Visibility: new { type = "role", value = "QA/Ops Team", identifier = "QA/Ops Team" }, RenderedBody: LinkTo("30002")));
        issue.Comments.Add(new FakeJiraComment("4", "Di", "Internal note", T0, JsdPublic: false));
        issue.Comments.Add(new FakeJiraComment("5", "Ed", "Named group", T0, Visibility: new { type = "group", value = "admins" },
            RenderedBody: LinkTo("30003")));
        _jira.ServeComments(issue);

        var delta = await _connector.GetChangesAsync(null);

        var role = new JiraRestriction(JiraRestrictionType.Role, "QA/Ops Team", ProjectId);
        var group = new JiraRestriction(JiraRestrictionType.Group, GroupA);
        var expected = new Dictionary<string, JiraResource>
        {
            ["/issues/20001.md"] = new(CloudId, "20001", JiraResourceKind.Issue, null, false, null),
            [CommentPath("20001", "2")] = new(CloudId, "20001", JiraResourceKind.Comment, "2", false, group),
            [CommentPath("20001", "3")] = new(CloudId, "20001", JiraResourceKind.Comment, "3", false, role),
            [CommentPath("20001", "4")] = new(CloudId, "20001", JiraResourceKind.Comment, "4", true, null),
            [CommentPath("20001", "5")] = new(CloudId, "20001", JiraResourceKind.Comment, "5", false, JiraRestriction.DenyAll),
            [AttachmentPath("20001", "30001", "group.txt")] = new(CloudId, "20001", JiraResourceKind.Attachment, "30001", false, group),
            [AttachmentPath("20001", "30002", "role.txt")] = new(CloudId, "20001", JiraResourceKind.Attachment, "30002", false, role),
            [AttachmentPath("20001", "30003", "denied.txt")] =
                new(CloudId, "20001", JiraResourceKind.Attachment, "30003", false, JiraRestriction.DenyAll),
            [AttachmentPath("20001", "30004", "public.txt")] = new(CloudId, "20001", JiraResourceKind.Attachment, "30004", false, null),
        };

        delta.Upserted.Select(f => f.Path).Should().BeEquivalentTo(expected.Keys);
        foreach (var file in delta.Upserted)
        {
            AtlassianUri.TryParseJira(file.ResourceUri, out var parsed).Should().BeTrue($"{file.Path} must carry a parseable URI");
            parsed.Should().Be(expected[file.Path], file.Path);
        }
        Doc(delta, CommentPath("20001", "3"))!.ResourceUri.Should().Contain("QA%2FOps%20Team");
    }

    [Fact]
    public async Task Sync_AttachmentLinkedFromTwoDifferentRestrictions_DenyAll()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "shared.txt"));
        _jira.Attach(issue, Attachment("30002", "unreadable.txt"));
        _jira.Attach(issue, Attachment("30003", "same.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "A", T0, Visibility: GroupVisibility(GroupA),
            RenderedBody: LinkTo("30001") + LinkTo("30003")));
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "B", T0, Visibility: GroupVisibility(GroupB), RenderedBody: LinkTo("30001")));
        issue.Comments.Add(new FakeJiraComment("5", "Ed", "Also A", T0, Visibility: GroupVisibility(GroupA), RenderedBody: LinkTo("30003")));
        // A group named without its id: a deny-all comment, so whatever it links is deny-all too.
        issue.Comments.Add(new FakeJiraComment("7", "Fay", "Named group", T0, Visibility: new { type = "group", value = "admins" },
            RenderedBody: LinkTo("30002")));
        _jira.ServeComments(issue);

        var delta = await _connector.GetChangesAsync(null);

        Doc(delta, AttachmentPath("20001", "30001", "shared.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, JiraRestriction.DenyAll));
        Doc(delta, AttachmentPath("20001", "30002", "unreadable.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30002", false, JiraRestriction.DenyAll));
        Doc(delta, AttachmentPath("20001", "30003", "same.txt"))!.ResourceUri.Should().Be(
            AtlassianUri.ForJiraAttachment(CloudId, "20001", "30003", false, new JiraRestriction(JiraRestrictionType.Group, GroupA)),
            "two comments with the same restriction agree");
    }

    [Fact]
    public async Task Sync_OversizeAttachment_SkippedAndCounted()
    {
        var connector = Connector(maxAttachmentMb: 1);
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "big.txt", declaredSize: 2 * 1024 * 1024));
        _jira.Attach(issue, Attachment("30002", "small.txt"));
        _jira.Attach(issue, Attachment("30003", "empty.txt", ""));

        var delta = await connector.GetChangesAsync(null);

        delta.Upserted.Select(f => f.Path).Should().BeEquivalentTo("/issues/20001.md", AttachmentPath("20001", "30002", "small.txt"));
        delta.Notice.Should().Be(string.Format(SkippedNotice, 1), "an empty file is left out without being counted");
        (await connector.ExistsAsync(AttachmentPath("20001", "30001", "big.txt"))).Should().BeFalse();
    }

    [Fact]
    public async Task Sync_UnsupportedAttachment_SkippedAndCounted()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "setup.exe"));

        var delta = await _connector.GetChangesAsync(null);

        delta.Upserted.Select(f => f.Path).Should().Equal("/issues/20001.md");
        delta.Notice.Should().Be(string.Format(SkippedNotice, 25));
        delta.Notice.Should().Contain(ConfluenceSpaceConnector.SkippedAttachmentsPhrase);
    }

    [Fact]
    public async Task Sync_IncludeAttachmentsOff_NoneListed()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "notes.txt"));
        _jira.Attach(issue, Attachment("30002", "setup.exe"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Secret", T0, Visibility: GroupVisibility(GroupA), RenderedBody: LinkTo("30001")));
        _jira.ServeComments(issue);
        var first = await _connector.GetChangesAsync(null);
        Doc(first, AttachmentPath("20001", "30001", "notes.txt")).Should().NotBeNull();
        _jira.RenderedReads.Clear();

        var off = Connector(includeAttachments: false);
        var delta = await off.GetChangesAsync(null);

        // A full listing: the attachment indexed before is deleted through the diff.
        delta.IsFullListing.Should().BeTrue();
        delta.Upserted.Select(f => f.Path).Should().BeEquivalentTo("/issues/20001.md", CommentPath("20001", "2"));
        delta.Notice.Should().BeNull("nothing is skipped when attachments are off");
        _jira.RenderedReads.Should().BeEmpty();
        (await off.ExistsAsync(AttachmentPath("20001", "30001", "notes.txt"))).Should().BeFalse();
    }

    [Fact]
    public async Task Read_Attachment_StreamsCapped()
    {
        const int cap = 1024 * 1024;
        var connector = Connector(maxAttachmentMb: 1);
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "notes.txt", "hello from Jira"));
        // Listed as 100 bytes, served as three times the cap.
        _jira.Attach(issue, new FakeJiraAttachment("30002", "big.txt", new byte[3 * cap], AttachedAt, DeclaredSize: 100));
        await connector.GetChangesAsync(null);

        using (var reader = new StreamReader(await connector.ReadFileAsync(AttachmentPath("20001", "30001", "notes.txt"))))
            (await reader.ReadToEndAsync()).Should().Be("hello from Jira");
        _api.Requests.Should().Contain(r => r.AbsolutePath == $"/ex/jira/{CloudId}/rest/api/3/attachment/content/30001");
        _api.MediaAuthorization.Should().ContainSingle().Which.Should().BeNull("the media address carries its own token");

        var copy = new MemoryStream();
        await using (var download = await connector.ReadFileAsync(AttachmentPath("20001", "30002", "big.txt")))
        {
            await FluentActions.Awaiting(() => download.CopyToAsync(copy))
                .Should().ThrowAsync<PermanentIngestionException>().WithMessage("*1 MB cap*");
        }
        copy.Length.Should().BeLessThanOrEqualTo(cap);

        issue.Attachments.RemoveAt(0);
        await FluentActions.Awaiting(() => connector.ReadFileAsync(AttachmentPath("20001", "30001", "notes.txt")))
            .Should().ThrowAsync<FileNotFoundException>();
        await FluentActions.Awaiting(() => connector.ReadFileAsync(AttachmentPath("20001", "30001", "renamed.txt")))
            .Should().ThrowAsync<FileNotFoundException>("a path the listing did not emit is not read");
    }

    [Theory]
    [InlineData("https://evil.example.com/file/30001/binary")]
    [InlineData("http://api.media.atlassian.com/file/30001/binary?token=media-30001")]
    [InlineData("https://api.media.atlassian.com.evil.example/file/30001/binary")]
    public async Task Read_AttachmentRedirectOffAtlassian_Permanent(string target)
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "notes.txt"));
        await _connector.GetChangesAsync(null);
        _jira.DownloadRedirect = target;

        (await FluentActions.Awaiting(() => _connector.ReadFileAsync(AttachmentPath("20001", "30001", "notes.txt")))
            .Should().ThrowAsync<PermanentIngestionException>())
            .WithInnerException<AtlassianRedirectRefusedException>();
        _api.Requests.Should().NotContain(r => r.AbsoluteUri.StartsWith(target, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_AttachmentScopeMissing_PermanentWithNotice()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "notes.txt"));
        await _connector.GetChangesAsync(null);
        _jira.RefuseDownloadsWith = System.Net.HttpStatusCode.Forbidden;

        (await FluentActions.Awaiting(() => _connector.ReadFileAsync(AttachmentPath("20001", "30001", "notes.txt")))
            .Should().ThrowAsync<PermanentIngestionException>())
            .WithMessage($"*{JiraProjectConnector.AttachmentScopeNotice}*")
            .And.Message.Should().Contain("read:attachment:jira");
    }

    [Theory]
    [InlineData("<a href=\"/rest/api/3/attachment/content/30001\">f</a>")]
    [InlineData("<a href=\"https://acme.atlassian.net/rest/api/2/attachment/content/30001\">f</a>")]
    [InlineData("<a href=\"/rest/api/latest/attachment/content/30001\">f</a>")]
    [InlineData("<img src=\"/rest/api/3/attachment/thumbnail/30001\">")]
    [InlineData("<img src=\"/rest/api/2/attachment/thumbnail/30001\">")]
    [InlineData("<a href=\"/secure/attachment/30001/notes.txt\">f</a>")]
    [InlineData("<img src=\"/secure/thumbnail/30001/notes.txt\">")]
    public async Task Sync_AttachmentLinkForm_TiedToComment(string html)
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "notes.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Secret", T0, Visibility: GroupVisibility(GroupA), RenderedBody: html));
        _jira.ServeComments(issue);

        var delta = await _connector.GetChangesAsync(null);

        Doc(delta, AttachmentPath("20001", "30001", "notes.txt"))!.ResourceUri.Should().Be(
            AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, new JiraRestriction(JiraRestrictionType.Group, GroupA)));
    }

    [Theory]
    [InlineData("<img data-media-services-id=\"9f1c2d3e-aaaa-bbbb-cccc-111122223333\" src=\"blob:https://acme.atlassian.net/9f1c\">")]
    [InlineData("<span data-media-type=\"file\"></span>")]
    [InlineData("<img src=\"https://api.media.atlassian.com/file/9f1c2d3e-aaaa-bbbb-cccc-111122223333/image\">")]
    [InlineData("<a href=\"/media/9f1c2d3e-aaaa-bbbb-cccc-111122223333\">f</a>")]
    [InlineData("<img src='blob:https://acme.atlassian.net/9f1c'>")]
    [InlineData("<img src=\"/rest/api/3/attachment/preview/9f1c\">")]
    [InlineData("<span data-attachment-name=\"notes.txt\" data-attachment-type=\"file\"></span>")]
    public async Task Sync_UntieableMediaReference_UntiedAttachmentsDenyAll(string html)
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "notes.txt"));
        issue.Comments.Add(new FakeJiraComment("4", "Di", "Internal", T0, JsdPublic: false, RenderedBody: html));
        _jira.ServeComments(issue);
        // An issue whose restricted comment links nothing unusual keeps its attachment unrestricted.
        var plain = Issue("20002", "ENG-2", "Other");
        _jira.Attach(plain, Attachment("30002", "plain.txt"));
        plain.Comments.Add(new FakeJiraComment("5", "Bob", "Secret", T0, Visibility: GroupVisibility(GroupA),
            RenderedBody: "<p>See <a href=\"https://example.com/docs\">the docs</a></p>"));
        _jira.ServeComments(plain);

        var delta = await _connector.GetChangesAsync(null);

        Doc(delta, AttachmentPath("20001", "30001", "notes.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", true, JiraRestriction.DenyAll));
        Doc(delta, AttachmentPath("20002", "30002", "plain.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20002", "30002", false, null));
    }

    [Fact]
    public async Task Sync_UntieableMediaReference_TieUnderOtherRestrictionDenyAll()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "other.txt"));
        _jira.Attach(issue, Attachment("30002", "same.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "A", T0, Visibility: GroupVisibility(GroupA),
            RenderedBody: LinkTo("30002") + "<img data-media-services-id=\"9f1c\">"));
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "B", T0, Visibility: GroupVisibility(GroupB), RenderedBody: LinkTo("30001")));
        _jira.ServeComments(issue);

        var delta = await _connector.GetChangesAsync(null);

        // The media reference in group A's comment may be 30001, which group B's comment also links.
        Doc(delta, AttachmentPath("20001", "30001", "other.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, JiraRestriction.DenyAll));
        Doc(delta, AttachmentPath("20001", "30002", "same.txt"))!.ResourceUri.Should().Be(
            AtlassianUri.ForJiraAttachment(CloudId, "20001", "30002", false, new JiraRestriction(JiraRestrictionType.Group, GroupA)));
    }

    [Fact]
    public async Task Sync_TwoUntieableCommentsDifferentGroups_TiedAttachmentDenyAll()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "tied.txt"));
        // A (group X) ties 30001 by id and also shows an unnamed file; B (group Y) shows an unnamed
        // file too, which may be 30001 shown only to group Y.
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "A", T0, Visibility: GroupVisibility(GroupA),
            RenderedBody: LinkTo("30001") + "<img data-media-services-id=\"aaaa\">"));
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "B", T0, Visibility: GroupVisibility(GroupB),
            RenderedBody: "<img data-media-services-id=\"bbbb\">"));
        _jira.ServeComments(issue);

        var delta = await _connector.GetChangesAsync(null);

        Doc(delta, AttachmentPath("20001", "30001", "tied.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, JiraRestriction.DenyAll));
    }

    [Fact]
    public async Task Sync_UntieableCommentChangedSincePageRead_EveryAttachmentDenyAll()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "tied.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "A", T0, Visibility: GroupVisibility(GroupA), RenderedBody: LinkTo("30001")));
        // Group A on the page, so its stored route equals 30001's tie; group B by the time it is read.
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "Media", T0, Visibility: GroupVisibility(GroupA),
            RenderedBody: "<img data-media-services-id=\"bbbb\">"));
        _jira.ServeComments(issue);
        _jira.ChangedSincePageRead["3"] = issue.Comments[1] with { Visibility = GroupVisibility(GroupB) };

        var delta = await _connector.GetChangesAsync(null);

        Doc(delta, AttachmentPath("20001", "30001", "tied.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, JiraRestriction.DenyAll));
    }

    [Fact]
    public async Task Sync_DenyAllUntieableComment_EveryAttachmentDenyAll()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "tied.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "A", T0, Visibility: GroupVisibility(GroupA), RenderedBody: LinkTo("30001")));
        issue.Comments.Add(new FakeJiraComment("7", "Fay", "Named group", T0, Visibility: new { type = "group", value = "admins" },
            RenderedBody: "<img data-media-services-id=\"bbbb\">"));
        _jira.ServeComments(issue);

        var delta = await _connector.GetChangesAsync(null);

        Doc(delta, AttachmentPath("20001", "30001", "tied.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, JiraRestriction.DenyAll));
    }

    [Fact]
    public async Task Sync_LaterUntieableCommentIsInternal_UntiedAttachmentInternal()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "untied.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "A", T0, Visibility: GroupVisibility(GroupA),
            RenderedBody: "<img data-media-services-id=\"aaaa\">"));
        issue.Comments.Add(new FakeJiraComment("4", "Di", "Internal", T0, JsdPublic: false,
            RenderedBody: "<img data-media-services-id=\"bbbb\">"));
        _jira.ServeComments(issue);

        var delta = await _connector.GetChangesAsync(null);

        Doc(delta, AttachmentPath("20001", "30001", "untied.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", true, JiraRestriction.DenyAll));
    }

    [Fact]
    public async Task Sync_CommentRestrictionChangedSincePageRead_AttachmentDenyAll()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "notes.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Secret", T0, Visibility: GroupVisibility(GroupA), RenderedBody: LinkTo("30001")));
        _jira.ServeComments(issue);
        _jira.ChangedSincePageRead["2"] = issue.Comments[0] with { Visibility = GroupVisibility(GroupB) };

        var delta = await _connector.GetChangesAsync(null);

        Doc(delta, AttachmentPath("20001", "30001", "notes.txt"))!.ResourceUri
            .Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, JiraRestriction.DenyAll));
    }

    [Fact]
    public async Task Sync_RenderedBodyMissing_UnavailableAndNothingSaved()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "notes.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Secret", T0, Visibility: GroupVisibility(GroupA), RenderedBody: LinkTo("30001")));
        _jira.ServeComments(issue);
        _jira.WithoutRenderedBody.Add("2");

        await FluentActions.Awaiting(() => _connector.GetChangesAsync(null)).Should().ThrowAsync<AtlassianUnavailableException>();

        (await _connector.ListFilesAsync()).Should().BeEmpty("a cycle that stopped saves nothing");
        (await _connector.ExistsAsync(AttachmentPath("20001", "30001", "notes.txt"))).Should().BeFalse();
    }

    [Fact]
    public async Task Listing_TyingCommentNoLongerRestricted_AttachmentDenyAll()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "notes.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Secret", T0, Visibility: GroupVisibility(GroupA), RenderedBody: LinkTo("30001")));
        _jira.ServeComments(issue);
        await _connector.GetChangesAsync(null);

        // A record whose tying comment no longer reads as restricted: the tie is not trusted.
        var store = new JiraIssueStateStore(_root);
        var stored = store.Load("20001")!;
        stored.Comments.Single(c => c.Id == "2").VisibilityType = null;
        store.Save(stored);

        var file = (await _connector.ListFilesAsync()).Single(f => f.Path == AttachmentPath("20001", "30001", "notes.txt"));
        file.ResourceUri.Should().Be(AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, JiraRestriction.DenyAll));
    }

    [Fact]
    public async Task Sync_RenderedReadNotFound_AnotherCommentStillTies()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "notes.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "Deleted", T0, Visibility: GroupVisibility(GroupB), RenderedBody: LinkTo("30001")));
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "Kept", T0, Visibility: GroupVisibility(GroupA), RenderedBody: LinkTo("30001")));
        _jira.ServeComments(issue);
        _jira.Answer("/issue/20001/comment/2", System.Net.HttpStatusCode.NotFound);

        var delta = await _connector.GetChangesAsync(null);

        Doc(delta, AttachmentPath("20001", "30001", "notes.txt"))!.ResourceUri.Should().Be(
            AtlassianUri.ForJiraAttachment(CloudId, "20001", "30001", false, new JiraRestriction(JiraRestrictionType.Group, GroupA)));
    }

    [Fact]
    public async Task Sync_TwoSweepsOnUnchangedIssue_SameTie()
    {
        var issue = Issue("20001", "ENG-1", "Login fails");
        _jira.Attach(issue, Attachment("30001", "grouped.txt"));
        _jira.Attach(issue, Attachment("30002", "shared.txt"));
        _jira.Attach(issue, Attachment("30003", "public.txt"));
        issue.Comments.Add(new FakeJiraComment("2", "Bob", "A", T0, Visibility: GroupVisibility(GroupA),
            RenderedBody: LinkTo("30001") + LinkTo("30002")));
        issue.Comments.Add(new FakeJiraComment("3", "Cy", "B", T0, Visibility: GroupVisibility(GroupB), RenderedBody: LinkTo("30002")));
        _jira.ServeComments(issue);
        var first = await _connector.GetChangesAsync(null);

        _clock.Advance(TimeSpan.FromHours(25));
        var second = await _connector.GetChangesAsync(first.NextCursor);
        _clock.Advance(TimeSpan.FromHours(25));
        var third = await _connector.GetChangesAsync(second.NextCursor);

        _jira.RenderedReads.Should().Equal("2", "3", "2", "3", "2", "3");
        var uris = first.Upserted.ToDictionary(f => f.Path, f => f.ResourceUri);
        second.Upserted.ToDictionary(f => f.Path, f => f.ResourceUri).Should().Equal(uris);
        third.Upserted.ToDictionary(f => f.Path, f => f.ResourceUri).Should().Equal(uris);
        uris[AttachmentPath("20001", "30002", "shared.txt")].Should().Be(
            AtlassianUri.ForJiraAttachment(CloudId, "20001", "30002", false, JiraRestriction.DenyAll));
    }

    [Fact]
    public void Factory_JiraKind_CreatesJiraConnector()
    {
        var (factory, connection) = Factory();
        var source = Source(connection.Id, """{"kind":"jira-project","projectId":"10000","projectKey":"ENG"}""");

        var connector = factory.Create(source, connection, "service-secret");

        var config = connector.Should().BeOfType<JiraProjectConnector>().Subject.Config;
        connector.Should().BeAssignableTo<ISyncCursorConnector>();
        connector.Type.Should().Be(ConnectorType.Atlassian);
        config.ProjectId.Should().Be("10000");
        config.ProjectKey.Should().Be("ENG");
        config.Site.CloudId.Should().Be(CloudId);
        config.StatePath.Should().Be(Path.GetFullPath(Path.Combine("/var/atlassian", source.Id.ToString("N"))));
        config.IncludeAttachments.Should().BeTrue();
        config.MaxAttachmentMb.Should().Be(25);
    }

    [Theory]
    [InlineData("""{"kind":"jira-issues","projectId":"10000","projectKey":"ENG"}""")]
    [InlineData("""{"kind":"jira-project","projectId":"10/../1","projectKey":"ENG"}""")]
    [InlineData("""{"kind":"jira-project","projectId":"10000","projectKey":" "}""")]
    public void Factory_UnknownKind_Throws(string scope)
    {
        var (factory, connection) = Factory();
        var source = Source(connection.Id, scope);

        Action act = () => factory.Create(source, connection, "service-secret");

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{source.Name}'*");
    }

    private static HttpResponseMessage RateLimited()
    {
        var limited = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        limited.Headers.TryAddWithoutValidation("Retry-After", "30");
        return limited;
    }

    private static long Size(SyncDelta delta, string path) => delta.Upserted.Single(f => f.Path == path).SizeBytes;

    [Fact]
    public void Factory_JiraKindOnConfluenceConnection_Throws()
    {
        var (factory, connection) = Factory(product: null);
        var source = Source(connection.Id, """{"kind":"jira-project","projectId":"10000","projectKey":"ENG"}""");

        Action act = () => factory.Create(source, connection, "service-secret");

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{source.Name}'*Confluence*");

        var (jiraFactory, jiraConnection) = Factory();
        var space = Source(jiraConnection.Id, """{"kind":"confluence-space","spaceId":"4001","spaceKey":"ENG"}""");
        Action reverse = () => jiraFactory.Create(space, jiraConnection, "service-secret");
        reverse.Should().Throw<InvalidOperationException>().WithMessage($"*'{space.Name}'*Jira*");
    }

    /// <param name="product">The connection's product; null writes none, which is a Confluence site.</param>
    private static (ConnectorFactory Factory, Connection Connection) Factory(string? product = "jira")
    {
        var security = Substitute.For<IOptionsMonitor<SourceSecuritySettings>>();
        security.CurrentValue.Returns(new SourceSecuritySettings());
        var gitHub = Substitute.For<IOptionsMonitor<GitHubSourceSettings>>();
        gitHub.CurrentValue.Returns(new GitHubSourceSettings());
        var azure = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        azure.CurrentValue.Returns(new AzureProviderSettings());
        var atlassian = Substitute.For<IOptionsMonitor<AtlassianSourceSettings>>();
        atlassian.CurrentValue.Returns(new AtlassianSourceSettings { StateDirectory = "/var/atlassian" });
        var httpClients = Substitute.For<IHttpClientFactory>();
        httpClients.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IProviderCredentialStore>());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var factory = new ConnectorFactory(
            security, gitHub, Substitute.For<ISshHostKeyStore>(),
            new ConnapseAwsCredentials(scopeFactory, NullLogger<ConnapseAwsCredentials>.Instance),
            new ConnapseAzureCredentials(azure),
            httpClients, NullLogger<ConnectorFactory>.Instance,
            atlassianTokens: new AtlassianTokenSource(httpClients, TimeProvider.System),
            atlassianSettings: atlassian);
        var connection = new Connection(
            Id: Guid.NewGuid(), Name: "conn", Provider: ConnectionProvider.Atlassian,
            ConfigJson: product is null
                ? $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"client-1"}"""
                : $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"client-1","product":"{{product}}"}""",
            CreatedByUserId: null, CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);
        return (factory, connection);
    }

    private static Source Source(Guid connectionId, string scope) => new(
        Id: Guid.NewGuid(), Name: "jira-src", Description: null, ConnectionId: connectionId,
        ScopeJson: scope, CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

    private sealed class Extensions(params string[] supported) : IFileTypeValidator
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(supported, StringComparer.OrdinalIgnoreCase);

        public bool IsSupported(string fileName) => SupportedExtensions.Contains(Path.GetExtension(fileName));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeFactory(FakeAtlassianApi api) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => api.CreateClient();
    }
}
