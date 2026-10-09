using Connapse.Core.Interfaces;
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
    public async Task ReadFile_ResponseWithoutAStorageValue_ThrowsRetryableAndAnEmptyValueIsAllowed()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));
        await _connector.GetChangesAsync(null);

        foreach (Func<object?> shape in new Func<object?>[]
        {
            () => null,
            () => new { },
            () => new { storage = (object?)null },
            () => new { storage = new { representation = "storage" } },
            () => new { storage = new { value = (string?)null } },
        })
        {
            _api.Confluence.BodyShape = shape;
            var ex = await FluentActions.Awaiting(() => _connector.ReadFileAsync("/pages/1.md"))
                .Should().ThrowAsync<IOException>("an incomplete response is transient, so the ingest is retried");
            ex.Which.Should().NotBeOfType<FileNotFoundException>();
            ex.Which.Should().NotBeAssignableTo<PermanentIngestionException>();
        }

        _api.Confluence.BodyShape = () => new { storage = new { value = "", representation = "storage" } };
        (await ReadAsync("/pages/1.md")).Should().StartWith("# Engineering > Page");
    }

    [Fact]
    public async Task ReadFile_NeverRequestsExportView()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));
        _api.Confluence.Upsert(new FakeConfluencePage("50", SpaceId, "News", Kind: "blogpost"));

        await _connector.GetChangesAsync(null);
        await ReadAsync("/pages/1.md");
        await ReadAsync("/blogposts/50.md");

        // Two bodies, then footer and inline comments for the page and for the blog post.
        _api.Confluence.BodyFormats.Should().HaveCount(6).And.OnlyContain(f => f == "storage");
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

    [Fact]
    public async Task CommentsAppearInRenderedDocument()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));
        _api.Confluence.Upsert(new FakeConfluencePage("50", SpaceId, "News", Kind: "blogpost"));
        _api.Confluence.AddUser("acc-ada", "Ada Lovelace");
        _api.Confluence.AddUser("acc-bob", "Bob Ross");
        _api.Confluence.AddComment("1", "acc-bob", "<p>Second, inline.</p>", new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero), inline: true);
        _api.Confluence.AddComment("1", "acc-ada", "<p>First, thanks <ac:link><ri:user ri:account-id=\"acc-bob\" /></ac:link>.</p>",
            new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));
        _api.Confluence.AddComment("50", "acc-ada", "<p>On the blog.</p>", new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));

        await _connector.GetChangesAsync(null);
        string page = await ReadAsync("/pages/1.md");
        string blog = await ReadAsync("/blogposts/50.md");

        page.Should().Contain("## Comments");
        int first = page.IndexOf("--- Comment by Ada Lovelace, 2026-09-20 ---", StringComparison.Ordinal);
        int second = page.IndexOf("--- Comment by Bob Ross, 2026-09-21 ---", StringComparison.Ordinal);
        first.Should().BePositive();
        second.Should().BeGreaterThan(first, "comments are oldest first, footer and inline together");
        page.Should().Contain("First, thanks Bob Ross.").And.Contain("Second, inline.");
        blog.Should().Contain("--- Comment by Ada Lovelace, 2026-09-22 ---").And.Contain("On the blog.");
    }

    [Fact]
    public async Task BlogPostInlineComment_IsRenderedAndItsEditReingestsOnlyThatBlogPost()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));
        _api.Confluence.Upsert(new FakeConfluencePage("50", SpaceId, "News", Kind: "blogpost"));
        _api.Confluence.AddUser("acc-ada", "Ada Lovelace");
        string id = _api.Confluence.AddComment("50", "acc-ada", "<p>Inline on the blog.</p>",
            new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero), inline: true);
        var first = await _connector.GetChangesAsync(null);
        string cursor = first.NextCursor!;

        (await ReadAsync("/blogposts/50.md")).Should().Contain("Inline on the blog.");
        _api.Requests.Should().Contain(r => r.AbsolutePath.EndsWith("/blogposts/50/inline-comments", StringComparison.Ordinal));

        _clock.Advance(TimeSpan.FromMinutes(5));
        _api.Confluence.EditComment(id, "<p>Edited inline.</p>", new DateTimeOffset(2026, 10, 1, 12, 5, 0, TimeSpan.Zero));
        var delta = await _connector.GetChangesAsync(cursor);

        var before = first.Upserted.ToDictionary(f => f.Path);
        var changed = delta.Upserted.Where(f => f.LastModified != before[f.Path].LastModified).ToList();
        changed.Should().ContainSingle().Which.Path.Should().Be("/blogposts/50.md", "only the blog post's comment changed");
        (await ReadAsync("/blogposts/50.md")).Should().Contain("Edited inline.");
    }

    [Fact]
    public async Task GetChanges_SecondCycle_AsksCqlForCommentsSinceADayBeforeTheWatermark()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));

        string cursor = (await _connector.GetChangesAsync(null)).NextCursor!;
        _api.Confluence.SearchQueries.Should().BeEmpty("a first sync reads every page's comments anyway");

        _clock.Advance(TimeSpan.FromMinutes(5));
        await _connector.GetChangesAsync(cursor);

        _api.Confluence.SearchQueries.Should().Equal(
            "space=\"ENG\" AND type=comment AND lastmodified >= \"2026-09-30 12:00\"");
        _api.Requests.Last(r => r.AbsolutePath.EndsWith("/rest/api/search", StringComparison.Ordinal)).Query
            .Should().Contain("limit=100").And.Contain("expand=content.container");
    }

    [Fact]
    public async Task GetChanges_CommentHitsAcrossSearchPages_KeepTheNewestPerPage()
    {
        _api.Confluence.MaxPageSize = 1;
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));
        string cursor = (await _connector.GetChangesAsync(null)).NextCursor!;

        var older = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var newer = older.AddHours(2);
        _api.Confluence.AddComment("1", "acc", "<p>a</p>", newer);
        _api.Confluence.AddComment("1", "acc", "<p>b</p>", older);
        var delta = await _connector.GetChangesAsync(cursor);

        delta.Upserted.Single().LastModified.Should().Be(newer.UtcDateTime);
        _api.Confluence.SearchQueries.Should().HaveCount(2, "the second hit is on the next search page");
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
