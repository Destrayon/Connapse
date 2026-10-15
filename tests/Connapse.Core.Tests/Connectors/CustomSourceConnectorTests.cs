using System.Net;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Custom;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class CustomSourceConnectorTests
{
    private readonly FakeMiddleMan _remote = new();

    private CustomSourceConnector Connector(string? token = FakeMiddleMan.Token) => new(
        new CustomSourceClient(new HttpClient(_remote), FakeMiddleMan.BaseUrl, token),
        new CustomSourceScope(FakeMiddleMan.BaseUrl, FakeMiddleMan.Scope));

    [Fact]
    public async Task GetChanges_NullCursor_ListsEveryDocumentAcrossPagesWithTheChangeCursor()
    {
        _remote.Put("a", "README.md", "# Readme");
        _remote.Put("b", "design/arch.md", "# Arch");
        _remote.Put("c", "notes.txt", "notes", "text/plain");

        SyncDelta delta = await Connector().GetChangesAsync(null);

        delta.IsFullListing.Should().BeTrue();
        delta.RequiresFullResync.Should().BeFalse();
        delta.Upserted.Select(f => f.Path).Should().BeEquivalentTo("/a/README.md", "/b/arch.md", "/c/notes.txt");
        delta.NextCursor.Should().Be("c3");
        _remote.LastAuthorization.Should().Be($"Bearer {FakeMiddleMan.Token}");
    }

    [Fact]
    public async Task GetChanges_FileWithoutExtension_GetsOneFromItsContentType()
    {
        _remote.Put("C123/1697.0001", "general/deploy-freeze", "# Freeze");
        _remote.Put("p1", "report", "%PDF", "application/pdf");

        SyncDelta delta = await Connector().GetChangesAsync(null);

        delta.Upserted.Select(f => f.Path).Should().BeEquivalentTo(
            $"/{Uri.EscapeDataString("C123/1697.0001")}/deploy-freeze.md",
            "/p1/report.pdf");
    }

    [Fact]
    public async Task GetChanges_Files_CarrySizeTypeAndNoResourceUri()
    {
        _remote.Put("a", "README.md", "# Readme");

        ConnectorFile file = (await Connector().GetChangesAsync(null)).Upserted.Single();

        file.SizeBytes.Should().Be(8);
        file.ContentType.Should().Be("text/markdown");
        file.ResourceUri.Should().BeNull("content-only documents are visible to everyone who can search sources");
    }

    [Fact]
    public async Task GetChanges_NewVersionWithSameSizeAndTime_ChangesTheSignature()
    {
        _remote.Put("a", "README.md", "# Readme", version: "v1");
        DateTime first = (await Connector().GetChangesAsync(null)).Upserted.Single().LastModified;

        _remote.Put("a", "README.md", "# Readme", version: "v2");
        DateTime second = (await Connector().GetChangesAsync(null)).Upserted.Single().LastModified;

        second.Should().NotBe(first);
        second.Date.Should().Be(new DateTime(2026, 10, 13));
    }

    [Fact]
    public async Task GetChanges_WithCursor_MapsUpsertsToFilesAndDeletesToPrefixes()
    {
        _remote.Put("a", "README.md", "# Readme");
        _remote.Put("b", "old-name.md", "# B");
        string cursor = (await Connector().GetChangesAsync(null)).NextCursor!;
        _remote.PageSize = 10;

        _remote.Put("b", "new-name.md", "# B renamed");
        _remote.Remove("a");
        SyncDelta delta = await Connector().GetChangesAsync(cursor);

        delta.IsFullListing.Should().BeFalse();
        delta.Upserted.Select(f => f.Path).Should().Equal("/b/new-name.md");
        delta.DeletedPaths.Should().BeEmpty();
        delta.DeletedPrefixes.Should().BeEquivalentTo("/a/", "/b/");
        delta.NextCursor.Should().Be("c4");
    }

    [Fact]
    public async Task GetChanges_FollowsChangePagesToTheNextCursor()
    {
        _remote.Put("a", "a.md", "a");
        string cursor = (await Connector().GetChangesAsync(null)).NextCursor!;
        _remote.Put("b", "b.md", "b");
        _remote.Put("c", "c.md", "c");
        _remote.Put("d", "d.md", "d");

        SyncDelta delta = await Connector().GetChangesAsync(cursor);

        delta.Upserted.Should().HaveCount(3);
        delta.NextCursor.Should().Be("c4");
    }

    [Fact]
    public async Task GetChanges_CursorTheMiddleManNoLongerKnows_RequiresAFullResync()
    {
        _remote.Put("a", "a.md", "a");

        SyncDelta delta = await Connector().GetChangesAsync("c99");

        delta.RequiresFullResync.Should().BeTrue();
        delta.NextCursor.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetChanges_RateLimitedOrUnavailable_DefersAndKeepsTheCursor(HttpStatusCode status)
    {
        _remote.Put("a", "a.md", "a");
        _remote.FailNextWith = status;

        SyncDelta delta = await Connector().GetChangesAsync("c1");

        delta.Deferred.Should().BeTrue();
        delta.NextCursor.Should().Be("c1");
        delta.Upserted.Should().BeEmpty();
        delta.Notice.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetChanges_MiddleManWithoutAChangeFeed_ListsInFullAndKeepsNoCursor()
    {
        _remote.SupportsChanges = false;
        _remote.Put("a", "a.md", "a");

        SyncDelta delta = await Connector().GetChangesAsync("c1");

        delta.IsFullListing.Should().BeTrue();
        delta.NextCursor.Should().BeNull();
        _remote.Requests.Should().NotContain(r => r.Contains("/changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetChanges_WrongToken_Throws()
    {
        _remote.Put("a", "a.md", "a");

        Func<Task> act = () => Connector(token: "wrong").GetChangesAsync(null);

        await act.Should().ThrowAsync<CustomSourceException>().WithMessage("*401*");
    }

    [Fact]
    public async Task GetChanges_Redirect_IsNotFollowed()
    {
        _remote.FailNextWith = HttpStatusCode.Found;

        Func<Task> act = () => Connector().GetChangesAsync(null);

        await act.Should().ThrowAsync<CustomSourceException>();
    }

    [Fact]
    public async Task ReadFile_FetchesTheContentByDocumentId()
    {
        _remote.Put("C123/1697.0001", "deploy-freeze.md", "# Freeze");
        string path = (await Connector().GetChangesAsync(null)).Upserted.Single().Path;

        await using Stream stream = await Connector().ReadFileAsync(path);

        (await new StreamReader(stream).ReadToEndAsync()).Should().Be("# Freeze");
    }

    [Fact]
    public async Task ReadFile_DocumentGone_ThrowsFileNotFound()
    {
        Func<Task> act = () => Connector().ReadFileAsync("/gone/gone.md");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public void Connector_IsReadOnlyAndNotWatchable()
    {
        CustomSourceConnector connector = Connector();

        connector.Type.Should().Be(ConnectorType.Custom);
        connector.SupportsLiveWatch.Should().BeFalse();
        connector.ResolveJobPath("/a/a.md").Should().Be("/a/a.md");
    }

    [Fact]
    public async Task GetChanges_ChangeFeedWithoutTombstones_ListsInFullSoDeletionsAreFound()
    {
        _remote.SupportsTombstones = false;
        _remote.Put("a", "a.md", "a");

        SyncDelta delta = await Connector().GetChangesAsync("c1");

        delta.IsFullListing.Should().BeTrue("only a completed full listing reveals deletions without tombstones");
        _remote.Requests.Should().NotContain(r => r.Contains("/changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetChanges_DocumentUpdatedThenDeletedAcrossPages_EndsDeleted()
    {
        _remote.Put("a", "a.md", "a");
        _remote.Put("b", "b.md", "b");
        string cursor = (await Connector().GetChangesAsync(null)).NextCursor!;
        _remote.PageSize = 1;

        _remote.Put("a", "a.md", "a, edited");
        _remote.Remove("a");
        _remote.Put("b", "first-name.md", "b");
        _remote.Put("b", "second-name.md", "b");
        SyncDelta delta = await Connector().GetChangesAsync(cursor);

        delta.Upserted.Select(f => f.Path).Should().Equal("/b/second-name.md");
        delta.DeletedPrefixes.Should().BeEquivalentTo("/a/", "/b/");
    }

    [Fact]
    public async Task GetChanges_RepeatedPageToken_FailsInsteadOfLooping()
    {
        _remote.Put("a", "a.md", "a");
        _remote.RepeatPageToken = true;

        Func<Task> act = () => Connector().GetChangesAsync(null);

        await act.Should().ThrowAsync<CustomSourceException>().WithMessage("*repeated*");
    }

    [Fact]
    public async Task ListScopes_RepeatedPageToken_FailsInsteadOfLooping()
    {
        _remote.RepeatPageToken = true;
        CustomSourceClient client = new(new HttpClient(_remote), FakeMiddleMan.BaseUrl, FakeMiddleMan.Token);

        Func<Task> act = () => client.ListScopesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<CustomSourceException>().WithMessage("*repeated*");
    }
}
