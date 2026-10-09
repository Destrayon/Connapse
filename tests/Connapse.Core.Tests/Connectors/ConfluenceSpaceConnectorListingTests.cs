using System.Net;
using System.Text;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// A listing response that answers 200 but is not a listing must fail the cycle, never read as an
/// empty or shorter space: the store, the cursor and what it reports stay as they were.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConfluenceSpaceConnectorListingTests : IDisposable
{
    private const string SpaceId = "4001";
    private const string CloudId = "11111111-2222-3333-4444-555555555555";

    private readonly FakeAtlassianApi _api = new();
    private readonly string _state = Path.Combine(Path.GetTempPath(), "confluence-listing-" + Guid.NewGuid().ToString("N"));
    private readonly AtlassianSite _site = new("https://acme.atlassian.net", CloudId, "client-1");

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_state))
            Directory.Delete(_state, recursive: true);
    }

    private ConfluenceSpaceConnector NewConnector() =>
        new(new ConfluenceSpaceConfig(_site, SpaceId, "ENG", _state),
            new AtlassianApiClient(_api.CreateClient(), new AtlassianTokenSource(new FakeFactory(_api), TimeProvider.System), _site, "secret"));

    private void SeedSpace()
    {
        _api.Confluence.AddSpace(SpaceId, "ENG", "Engineering");
        for (int i = 1; i <= 3; i++)
            _api.Confluence.Upsert(new FakeConfluencePage((100 + i).ToString(), SpaceId, $"Page {i}"));
        _api.Confluence.Upsert(new FakeConfluencePage("201", SpaceId, "News", Kind: "blogpost"));
    }

    private Dictionary<string, string> SnapshotStore() =>
        Directory.EnumerateFiles(_state, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(_state, f), File.ReadAllText);

    private static HttpResponseMessage Raw(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static TheoryData<string> MalformedBodies =>
    [
        "{}",
        "[]",
        """{"results":{}}""",
        """{"results":null}""",
        """{"results":[null]}""",
        """{"results":["101"]}""",
        """{"results":[{"title":"No id","version":{"number":1}}]}""",
        """{"results":[{"id":"abc","version":{"number":1}}]}""",
        """{"results":[{"id":101,"version":{"number":1}}]}""",
        """{"results":[{"id":"101"}]}""",
        """{"results":[{"id":"101","version":null}]}""",
        """{"results":[{"id":"101","version":{"number":0}}]}""",
    ];

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task GetChangesAsync_MalformedFirstListingPage_ThrowsAndKeepsStore(string body)
    {
        SeedSpace();
        var connector = NewConnector();
        var first = await connector.GetChangesAsync(null);
        var before = SnapshotStore();
        var listedBefore = await connector.ListFilesAsync();

        _api.Intercept = uri => uri.AbsolutePath.EndsWith($"/spaces/{SpaceId}/pages", StringComparison.Ordinal) ? Raw(body) : null;

        var act = () => connector.GetChangesAsync(first.NextCursor);

        await act.Should().ThrowAsync<AtlassianMalformedResponseException>();
        SnapshotStore().Should().BeEquivalentTo(before);
        (await connector.ListFilesAsync()).Should().BeEquivalentTo(listedBefore);
    }

    [Fact]
    public async Task GetChangesAsync_MalformedLaterListingPage_ThrowsAndKeepsStore()
    {
        SeedSpace();
        _api.Confluence.MaxPageSize = 1; // three pages of pages, so the bad answer is a later one
        var connector = NewConnector();
        var first = await connector.GetChangesAsync(null);
        var before = SnapshotStore();

        _api.Intercept = uri =>
            uri.AbsolutePath.EndsWith($"/spaces/{SpaceId}/pages", StringComparison.Ordinal) && uri.Query.Contains("cursor=")
                ? Raw("""{"results":[null],"_links":{}}""")
                : null;

        var act = () => connector.GetChangesAsync(first.NextCursor);

        await act.Should().ThrowAsync<AtlassianMalformedResponseException>();
        SnapshotStore().Should().BeEquivalentTo(before);
        (await connector.ListFilesAsync()).Select(f => f.Path).Should().BeEquivalentTo(
            "/pages/101.md", "/pages/102.md", "/pages/103.md", "/blogposts/201.md");
    }

    private sealed class FakeFactory(FakeAtlassianApi api) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => api.CreateClient();
    }
}
