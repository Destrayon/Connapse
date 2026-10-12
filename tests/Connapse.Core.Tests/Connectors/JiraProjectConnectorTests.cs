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
        var site = new AtlassianSite("https://acme.atlassian.net", CloudId, "client-1");
        var tokens = new AtlassianTokenSource(new FakeFactory(_api), TimeProvider.System);
        _connector = new JiraProjectConnector(
            new JiraProjectConfig(site, ProjectId, "ENG", _root),
            new AtlassianApiClient(_api.CreateClient(), tokens, site, "secret", product: AtlassianProduct.Jira),
            clock: _clock);
        _jira = new FakeJira(_api, CloudId).MapSync();
        _jira.AddProject(ProjectId, "ENG", "Engineering");
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

    private static long Size(SyncDelta delta, string path) => delta.Upserted.Single(f => f.Path == path).SizeBytes;

    private static (ConnectorFactory Factory, Connection Connection) Factory()
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
            ConfigJson: $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"client-1"}""",
            CreatedByUserId: null, CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);
        return (factory, connection);
    }

    private static Source Source(Guid connectionId, string scope) => new(
        Id: Guid.NewGuid(), Name: "jira-src", Description: null, ConnectionId: connectionId,
        ScopeJson: scope, CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

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
