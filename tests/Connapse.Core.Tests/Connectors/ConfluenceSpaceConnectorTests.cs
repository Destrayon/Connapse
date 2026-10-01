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

    public ConfluenceSpaceConnectorTests()
    {
        var site = new AtlassianSite("https://acme.atlassian.net", CloudId, "client-1");
        var tokens = new AtlassianTokenSource(new FakeFactory(_api), TimeProvider.System);
        _connector = new ConfluenceSpaceConnector(
            new ConfluenceSpaceConfig(site, SpaceId, "ENG", _root),
            new AtlassianApiClient(_api.CreateClient(), tokens, site, "secret"));
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

    private sealed class FakeFactory(FakeAtlassianApi api) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => api.CreateClient();
    }
}
