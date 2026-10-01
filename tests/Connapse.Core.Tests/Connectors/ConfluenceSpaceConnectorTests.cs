using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public sealed class ConfluenceSpaceConnectorTests : IDisposable
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string SpaceId = "4001";

    private readonly FakeAtlassianApi _api = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "confluence-unit-" + Guid.NewGuid().ToString("N"));
    private readonly ConfluenceSpaceConnector _connector;
    private readonly ManualClock _clock = new();

    public ConfluenceSpaceConnectorTests()
    {
        var site = new AtlassianSite("https://acme.atlassian.net", CloudId, "client-1");
        var tokens = new AtlassianTokenSource(new FakeFactory(_api), TimeProvider.System);
        _connector = new ConfluenceSpaceConnector(
            new ConfluenceSpaceConfig(site, SpaceId, "ENG", _root),
            new AtlassianApiClient(_api.CreateClient(), tokens, site, "secret"),
            clock: _clock);
        _api.Confluence.AddSpace(SpaceId, "ENG", "Engineering");
    }

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<string> ReadAsync(string path)
    {
        using var reader = new StreamReader(await _connector.ReadFileAsync(path));
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task ReadFile_RendersBreadcrumbFromAncestorsAndFolders()
    {
        // Root page > Guides (folder) > Deep (folder) > Child page > Leaf page
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Root"));
        _api.Confluence.AddFolder("900", "Guides", parentId: "1", parentType: "page");
        _api.Confluence.AddFolder("901", "Deep", parentId: "900", parentType: "folder");
        _api.Confluence.Upsert(new FakeConfluencePage("2", SpaceId, "Child", ParentId: "901", ParentType: "folder"));
        _api.Confluence.Upsert(new FakeConfluencePage("3", SpaceId, "Leaf",
            Body: """<h1>Setup</h1><p>Ask <ac:link><ri:user ri:account-id="acc-1" /></ac:link>.</p>""",
            ParentId: "2", ParentType: "page"));
        _api.Confluence.Upsert(new FakeConfluencePage("50", SpaceId, "News", Kind: "blogpost"));
        _api.Confluence.AddUser("acc-1", "Ada Lovelace");

        await _connector.GetChangesAsync(null);
        string leaf = await ReadAsync("/pages/3.md");
        string blog = await ReadAsync("/blogposts/50.md");

        leaf.Should().StartWith("# Engineering > Root > Guides > Deep > Child > Leaf\n");
        leaf.Should().Contain("## Setup", "the page's own headings sit one level under the breadcrumb");
        leaf.Should().Contain("Ada Lovelace");
        blog.Should().StartWith("# Engineering > Blog > News\n");
        _api.Confluence.FolderFetches.Should().BeEquivalentTo(new Dictionary<string, int> { ["900"] = 1, ["901"] = 1 });

        await _connector.GetChangesAsync(null);
        _api.Confluence.FolderFetches.Values.Should().AllBeEquivalentTo(2, "a fresh start forgets the cache, and only that");
    }

    [Fact]
    public async Task ReadFile_NeverRequestsExportView()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));
        _api.Confluence.Upsert(new FakeConfluencePage("50", SpaceId, "News", Kind: "blogpost"));

        await _connector.GetChangesAsync(null);
        await ReadAsync("/pages/1.md");
        await ReadAsync("/blogposts/50.md");

        _api.Confluence.BodyFormats.Should().HaveCount(2).And.OnlyContain(f => f == "storage");
        _api.Requests.Select(r => r.Query).Should().NotContain(q =>
            q.Contains("view", StringComparison.OrdinalIgnoreCase) || q.Contains("expand", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadFile_ParentCycle_Terminates()
    {
        // Two pages naming each other as parent, and two folders doing the same.
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Alpha", ParentId: "2", ParentType: "page"));
        _api.Confluence.Upsert(new FakeConfluencePage("2", SpaceId, "Beta", ParentId: "1", ParentType: "page"));
        _api.Confluence.AddFolder("900", "Left", parentId: "901", parentType: "folder");
        _api.Confluence.AddFolder("901", "Right", parentId: "900", parentType: "folder");
        _api.Confluence.Upsert(new FakeConfluencePage("3", SpaceId, "Gamma", ParentId: "900", ParentType: "folder"));

        var sync = _connector.GetChangesAsync(null);
        (await sync.WaitAsync(TimeSpan.FromSeconds(10))).Upserted.Should().HaveCount(3);

        (await ReadAsync("/pages/1.md").WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().StartWith("# Engineering > Beta > Alpha\n");
        (await ReadAsync("/pages/3.md").WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().StartWith("# Engineering > Right > Left > Gamma\n");
    }

    [Fact]
    public async Task ReadFile_PathNotInTheStore_ThrowsFileNotFound()
    {
        await _connector.GetChangesAsync(null);

        await FluentActions.Awaiting(() => _connector.ReadFileAsync("/pages/999.md"))
            .Should().ThrowAsync<FileNotFoundException>();
        await FluentActions.Awaiting(() => _connector.ReadFileAsync("/pages/../state.md"))
            .Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ReadFile_AncestorChainDeeperThanTheCap_StopsAtFiftyLevels()
    {
        // Page 1 is the top; each page n+1 sits under page n, sixty deep.
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "P1"));
        for (int n = 2; n <= 60; n++)
            _api.Confluence.Upsert(new FakeConfluencePage(n.ToString(), SpaceId, "P" + n, ParentId: (n - 1).ToString(), ParentType: "page"));

        await _connector.GetChangesAsync(null);
        string header = (await ReadAsync("/pages/60.md").WaitAsync(TimeSpan.FromSeconds(10))).Split('\n')[0];

        string[] crumbs = header["# ".Length..].Split(" > ");
        crumbs.Should().HaveCount(1 + ConfluenceSpaceConnector.MaxAncestors + 1);
        crumbs[0].Should().Be("Engineering");
        crumbs[1].Should().Be("P10", "the walk climbs fifty levels from P60 and stops at P10");
        crumbs[^1].Should().Be("P60");
    }

    [Fact]
    public async Task GetChanges_UnreadableCursor_RequiresFullResync()
    {
        var delta = await _connector.GetChangesAsync("not a cursor");

        delta.RequiresFullResync.Should().BeTrue();
        delta.NextCursor.Should().BeNull();
        _api.Requests.Should().BeEmpty("nothing is asked of Confluence before the cursor is understood");
    }

    [Fact]
    public async Task GetChanges_RateLimitedOnAFolder_AppliesNothingAndKeepsTheCursor()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Kept"));
        _api.Confluence.AddFolder("900", "Guides");
        string cursor = (await _connector.GetChangesAsync(null)).NextCursor!;

        // A page now in a folder whose lookup is rate limited, and the old page gone.
        _api.Confluence.Remove("1");
        _api.Confluence.Upsert(new FakeConfluencePage("2", SpaceId, "New", ParentId: "900", ParentType: "folder"));
        _api.Map($"/ex/confluence/{CloudId}/wiki/api/v2/folders/900", _ =>
        {
            var limited = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            limited.Headers.TryAddWithoutValidation("Retry-After", "30");
            return limited;
        });

        var delta = await _connector.GetChangesAsync(cursor);

        delta.IsFullListing.Should().BeFalse();
        delta.Upserted.Should().BeEmpty();
        delta.NextCursor.Should().Be(cursor);
        delta.Notice.Should().Contain("rate limiting");
        (await _connector.ListFilesAsync()).Select(f => f.Path).Should().Equal("/pages/1.md");
    }

    [Fact]
    public async Task GetChanges_FreshStartRateLimited_LeavesTheOldStoreReadable()
    {
        _api.Confluence.MaxPageSize = 1;
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "One"));
        _api.Confluence.Upsert(new FakeConfluencePage("2", SpaceId, "Two"));
        await _connector.GetChangesAsync(null);

        _api.Confluence.RateLimitNextContinuation = true;
        var delta = await _connector.GetChangesAsync(null);

        delta.Upserted.Should().BeEmpty();
        delta.IsFullListing.Should().BeFalse();
        (await _connector.ListFilesAsync()).Should().HaveCount(2, "jobs already queued must still find their pages");
        (await ReadAsync("/pages/2.md")).Should().StartWith("# Engineering > Two\n");
    }

    [Fact]
    public async Task GetChanges_UnavailableFolder_IsNotAskedAgainUntilTheHourlyRefresh()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Orphan", ParentId: "999", ParentType: "folder"));

        string cursor = (await _connector.GetChangesAsync(null)).NextCursor!;
        cursor = (await _connector.GetChangesAsync(cursor)).NextCursor!;
        _api.Confluence.FolderFetches["999"].Should().Be(1, "a 404 is remembered");

        _clock.Advance(ConfluenceSpaceConnector.FolderRefreshInterval);
        await _connector.GetChangesAsync(cursor);
        _api.Confluence.FolderFetches["999"].Should().Be(2);
    }

    [Fact]
    public async Task GetChanges_FolderNoLongerUsed_IsDroppedFromTheState()
    {
        _api.Confluence.AddFolder("900", "Guides");
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Guide", ParentId: "900", ParentType: "folder"));
        string cursor = (await _connector.GetChangesAsync(null)).NextCursor!;

        _api.Confluence.Reparent("1", null, null);
        await _connector.GetChangesAsync(cursor);

        string state = await File.ReadAllTextAsync(Path.Combine(_root, "state.json"));
        state.Should().NotContain("900");
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
