using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
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
            clock: _clock,
            fileTypes: new Extensions(".txt", ".pdf"));
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
            "space=\"ENG\" AND type in (comment, attachment) AND lastmodified >= \"2026-09-30 12:00\"");
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

    private static FakeConfluenceAttachment Attachment(string id, string pageId, string name, string text = "attached text") =>
        new(id, pageId, name, System.Text.Encoding.UTF8.GetBytes(text), new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ReadFile_Attachment_FollowsTheRedirectToMediaWithoutTheBearerToken()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));
        _api.Confluence.AddAttachment(Attachment("501", "1", "notes.txt", "hello from the attachment"));

        var files = (await _connector.GetChangesAsync(null)).Upserted;
        var file = files.Single(f => f.Path.StartsWith("/attachments/", StringComparison.Ordinal));

        file.Path.Should().Be("/attachments/501/notes.txt");
        (await ReadAsync(file.Path)).Should().Be("hello from the attachment");
        _api.MediaAuthorization.Should().ContainSingle().Which.Should().BeNull("the media address carries its own token");
        _api.Requests.Should().Contain(r => r.AbsolutePath.EndsWith("/rest/api/content/1/child/attachment/att501/download", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://evil.example.com/file/501/binary")]
    [InlineData("http://api.media.atlassian.com/file/501/binary?token=media-501")]
    [InlineData("https://api.media.atlassian.com.evil.example/file/501/binary")]
    [InlineData("https://api.atlassian.com/ex/confluence/99999999-2222-3333-4444-555555555555/wiki/x")]
    public async Task ReadFile_AttachmentRedirectLeavingAtlassian_IsRefused(string target)
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));
        _api.Confluence.AddAttachment(Attachment("501", "1", "notes.txt"));
        await _connector.GetChangesAsync(null);
        _api.Confluence.DownloadRedirect = target;

        // Permanent: the same redirect will come back on a retry.
        (await FluentActions.Awaiting(() => _connector.ReadFileAsync("/attachments/501/notes.txt"))
            .Should().ThrowAsync<Connapse.Core.Interfaces.PermanentIngestionException>())
            .WithInnerException<AtlassianRedirectRefusedException>();
        _api.Requests.Should().NotContain(r => r.AbsoluteUri.StartsWith(target, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ObjectDisposedException))]
    public async Task ReadFile_AttachmentDownloadFailsForAnotherReason_IsNotMarkedPermanent(Type failure)
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));
        _api.Confluence.AddAttachment(Attachment("501", "1", "notes.txt"));
        await _connector.GetChangesAsync(null);
        _api.Map($"/ex/confluence/{CloudId}/wiki/rest/api/content/1/child/attachment/att501/download",
            _ => throw (Exception)Activator.CreateInstance(failure, "transient")!);

        // A retry may well succeed, so it must not be filed as a failure no retry can fix.
        var thrown = await FluentActions.Awaiting(() => _connector.ReadFileAsync("/attachments/501/notes.txt"))
            .Should().ThrowAsync<Exception>();
        thrown.Which.Should().NotBeOfType<Connapse.Core.Interfaces.PermanentIngestionException>();
    }

    [Fact]
    public async Task GetChanges_RateLimitedMidSweep_KeepsThePreviousAttachments()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "One"));
        _api.Confluence.Upsert(new FakeConfluencePage("2", SpaceId, "Two"));
        _api.Confluence.AddAttachment(Attachment("501", "1", "a.txt"));
        _api.Confluence.AddAttachment(Attachment("502", "2", "b.txt"));
        string cursor = (await _connector.GetChangesAsync(null)).NextCursor!;

        // Both deleted; the sweep reaches page 1, then is rate limited on page 2.
        _api.Confluence.RemoveAttachment("501");
        _api.Confluence.RemoveAttachment("502");
        _clock.Advance(ConfluenceSpaceConnector.AttachmentSweepInterval);
        _api.Confluence.RateLimitAttachmentListing.Add("2");
        var delta = await _connector.GetChangesAsync(cursor);

        delta.IsFullListing.Should().BeTrue();
        delta.Notice.Should().Contain("rate limiting");
        delta.Upserted.Select(f => f.Path).Should().BeEquivalentTo("/pages/1.md", "/pages/2.md", "/attachments/502/b.txt");

        var next = await _connector.GetChangesAsync(delta.NextCursor);
        next.Upserted.Select(f => f.Path).Should().BeEquivalentTo("/pages/1.md", "/pages/2.md");
    }

    [Fact]
    public async Task GetChanges_AttachmentChangeHit_ReadsOnlyThatPagesList()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "One"));
        _api.Confluence.Upsert(new FakeConfluencePage("2", SpaceId, "Two"));
        string cursor = (await _connector.GetChangesAsync(null)).NextCursor!;

        _clock.Advance(TimeSpan.FromMinutes(5));
        _api.Confluence.AddAttachment(Attachment("503", "2", "new.pdf") with { ModifiedAt = _clock.GetUtcNow() });
        var delta = await _connector.GetChangesAsync(cursor);

        delta.Upserted.Should().Contain(f => f.Path == "/attachments/503/new.pdf");
        _api.Confluence.AttachmentListings.Should().BeEquivalentTo(new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });
    }

    [Fact]
    public async Task GetChanges_RepeatedRateLimitsWithAProcessedChangeStillInTheOverlap_SweepReachesEveryPage()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "One"));
        _api.Confluence.Upsert(new FakeConfluencePage("2", SpaceId, "Two"));
        _api.Confluence.Upsert(new FakeConfluencePage("3", SpaceId, "Three"));
        _api.Confluence.AddAttachment(Attachment("501", "1", "a.txt"));
        string? cursor = (await _connector.GetChangesAsync(null)).NextCursor;

        // A day on, every list is due. Page 1's attachment just changed, so the change query reports
        // it for the next day; pages 2 and 3 gained attachments only the sweep can find. Each cycle
        // Confluence answers one attachment listing before rate limiting.
        _clock.Advance(ConfluenceSpaceConnector.AttachmentSweepInterval);
        _api.Confluence.AddAttachment(Attachment("501", "1", "a.txt") with { ModifiedAt = _clock.GetUtcNow().AddMinutes(-1) });
        _api.Confluence.AddAttachment(Attachment("502", "2", "b.txt"));
        _api.Confluence.AddAttachment(Attachment("503", "3", "c.txt"));

        Connapse.Core.Interfaces.SyncDelta delta = null!;
        for (int cycle = 0; cycle < 4; cycle++)
        {
            _api.Confluence.AttachmentListingBudget = 1;
            delta = await _connector.GetChangesAsync(cursor);
            cursor = delta.NextCursor;
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _api.Confluence.SearchQueries.Last().Should().Contain("attachment");
        delta.Upserted.Select(f => f.Path).Should().Contain(["/attachments/502/b.txt", "/attachments/503/c.txt"]);
        _api.Confluence.AttachmentListings["1"].Should().Be(2, "a change already read is not read again");
    }

    [Fact]
    public async Task GetChanges_AttachmentMovedToAnotherPage_IsListedOnceUnderTheNewPageEveryCycle()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "One"));
        _api.Confluence.Upsert(new FakeConfluencePage("2", SpaceId, "Two"));
        _api.Confluence.AddAttachment(Attachment("501", "1", "notes.txt", "moved text"));
        string? cursor = (await _connector.GetChangesAsync(null)).NextCursor;

        // Moved from page 1 to page 2. The change query names page 2; page 1's list is not read again.
        _clock.Advance(TimeSpan.FromMinutes(5));
        _api.Confluence.AddAttachment(Attachment("501", "2", "notes.txt", "moved text") with { ModifiedAt = _clock.GetUtcNow() });

        for (int cycle = 0; cycle < 3; cycle++)
        {
            var delta = await _connector.GetChangesAsync(cursor);
            cursor = delta.NextCursor;

            delta.Upserted.Where(f => f.Path == "/attachments/501/notes.txt").Should().ContainSingle()
                .Which.ResourceUri.Should().Be(AtlassianUri.ForAttachment(CloudId, "2", "501"), "cycle {0}", cycle);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        (await ReadAsync("/attachments/501/notes.txt")).Should().Be("moved text");
        _api.Confluence.AttachmentListings["1"].Should().Be(1, "page 1's own list was never read again");
    }

    [Fact]
    public async Task GetChanges_AttachmentMovedAndOldPageNotReadAgain_SweepGivesItToTheNewPageOnly()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "One"));
        _api.Confluence.Upsert(new FakeConfluencePage("2", SpaceId, "Two"));
        _api.Confluence.AddAttachment(Attachment("501", "2", "notes.txt"));
        string cursor = (await _connector.GetChangesAsync(null)).NextCursor!;

        // Moved from page 2 to page 1 without a change hit; the sweep reads page 1, then is rate
        // limited on page 2, whose old list still holds it.
        _api.Confluence.AddAttachment(Attachment("501", "1", "notes.txt"));
        _clock.Advance(ConfluenceSpaceConnector.AttachmentSweepInterval);
        _api.Confluence.RateLimitAttachmentListing.Add("2");
        var delta = await _connector.GetChangesAsync(cursor);

        delta.Upserted.Where(f => f.Path == "/attachments/501/notes.txt").Should().ContainSingle()
            .Which.ResourceUri.Should().Be(AtlassianUri.ForAttachment(CloudId, "1", "501"));
    }

    [Fact]
    public async Task GetChanges_AttachmentClaimedByTwoFreshLists_IsNotListedUntilSettled()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "One"));
        _api.Confluence.Upsert(new FakeConfluencePage("2", SpaceId, "Two"));
        _api.Confluence.AddAttachment(Attachment("501", "1", "notes.txt"));
        // Confluence answering the same attachment on both pages: no way to tell which page guards it.
        _api.Confluence.AttachmentAlsoListedOn["501"] = "2";

        var delta = await _connector.GetChangesAsync(null);

        delta.Upserted.Should().NotContain(f => f.Path.StartsWith("/attachments/501/", StringComparison.Ordinal));
        (await _connector.ExistsAsync("/attachments/501/notes.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task GetChanges_AttachmentListedWithoutASize_IsSkippedAndCounted()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));
        _api.Confluence.AddAttachment(Attachment("501", "1", "unsized.txt") with { OmitSize = true });
        _api.Confluence.AddAttachment(Attachment("502", "1", "zero.txt") with { DeclaredSize = 0 });

        var delta = await _connector.GetChangesAsync(null);

        delta.Upserted.Select(f => f.Path).Should().Equal("/pages/1.md");
        delta.Notice.Should().Be("2 attachments skipped: over 25 MB or unsupported type");
        (await _connector.ExistsAsync("/attachments/501/unsized.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ReadFile_AttachmentLargerThanListed_FailsPermanentlyWithoutPassingTheCap()
    {
        const int cap = 1024 * 1024;
        var site = new AtlassianSite("https://acme.atlassian.net", CloudId, "client-1");
        var capped = new ConfluenceSpaceConnector(
            new ConfluenceSpaceConfig(site, SpaceId, "ENG", _root, MaxAttachmentMb: 1),
            new AtlassianApiClient(_api.CreateClient(), new AtlassianTokenSource(new FakeFactory(_api), TimeProvider.System), site, "secret"),
            fileTypes: new Extensions(".txt"));
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));

        // Listed as 100 bytes, served as three times the cap.
        _api.Confluence.AddAttachment(new FakeConfluenceAttachment(
            "501", "1", "big.txt", new byte[3 * cap], DateTimeOffset.UnixEpoch, DeclaredSize: 100));
        await capped.GetChangesAsync(null);

        var copy = new MemoryStream();
        await using var download = await capped.ReadFileAsync("/attachments/501/big.txt");
        await FluentActions.Awaiting(() => download.CopyToAsync(copy))
            .Should().ThrowAsync<Connapse.Core.Interfaces.PermanentIngestionException>().WithMessage("*1 MB cap*");
        copy.Length.Should().BeLessThanOrEqualTo(cap);
    }

    [Fact]
    public async Task ReadFile_AttachmentDeletedSinceTheListing_ThrowsFileNotFound()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", SpaceId, "Page"));
        _api.Confluence.AddAttachment(Attachment("501", "1", "notes.txt"));
        await _connector.GetChangesAsync(null);

        _api.Confluence.RemoveAttachment("501");

        await FluentActions.Awaiting(() => _connector.ReadFileAsync("/attachments/501/notes.txt"))
            .Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public void SafeFileName_LongNameEndingInASurrogatePair_IsNotSplit()
    {
        string name = new string('a', 195) + "\U0001F600" + new string('b', 20) + ".txt";

        string safe = ConfluenceSpaceConnector.SafeFileName(name);

        safe.Should().Be(new string('a', 195) + ".txt");
        safe.Any(char.IsSurrogate).Should().BeFalse();
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "_._etc_passwd")]
    [InlineData("a\\b/c.txt", "a_b_c.txt")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    [InlineData("  ..hidden..  ", "hidden")]
    [InlineData("...", "attachment")]
    [InlineData("what?:*.txt", "what___.txt")]
    public void SafeFileName_RemovesSeparatorsDotRunsAndControls(string name, string expected)
    {
        ConfluenceSpaceConnector.SafeFileName(name).Should().Be(expected);
        ConfluenceSpaceConnector.SafeFileName(name).Should().Be(ConfluenceSpaceConnector.SafeFileName(name));
    }

    private sealed class Extensions(params string[] supported) : Connapse.Core.Interfaces.IFileTypeValidator
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
