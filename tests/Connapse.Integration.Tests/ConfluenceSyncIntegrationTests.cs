using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Storage.Data;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// A Confluence space source synced end to end through <see cref="SourceSyncService"/> against
/// real PostgreSQL, with Confluence itself the in-memory <see cref="FakeAtlassianApi"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class ConfluenceSyncIntegrationTests(SharedWebAppFixture fixture) : IDisposable
{
    private const string SpaceId = "4001";
    private const string OtherSpaceId = "4002";

    // Upper case on purpose: every address must still come out in the lower-case form search matches.
    private readonly string _cloudId = Guid.NewGuid().ToString("D").ToUpperInvariant();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "confluence-sync-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAtlassianApi _api = new();

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeHttpClients(FakeAtlassianApi api) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => api.CreateClient();
    }

    /// <summary>Builds the real connector over the fake, the way the factory branch will.</summary>
    private sealed class FakeApiConnectorFactory(FakeAtlassianApi api, string root) : IConnectorFactory
    {
        private readonly AtlassianTokenSource _tokens = new(new FakeHttpClients(api), TimeProvider.System);

        public IConnector Create(Source source, Connection connection, string? secret = null)
        {
            var site = AtlassianSite.FromConfigJson(connection.ConfigJson)!;
            return new ConfluenceSpaceConnector(
                new ConfluenceSpaceConfig(site, SpaceId, "ENG", Path.Combine(root, source.Id.ToString("N"))),
                new AtlassianApiClient(api.CreateClient(), _tokens, site, secret!));
        }

        public IConnector Create(Source source) => throw new InvalidOperationException("Confluence sources have a connection");
    }

    private (SourceSyncService Service, RecordingIngestionQueue Queue) BuildService(IServiceProvider sp)
    {
        var queue = new RecordingIngestionQueue();
        var service = new SourceSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new FakeApiConnectorFactory(_api, _root),
            queue,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());
        return (service, queue);
    }

    private async Task<(Source Source, Connection Connection)> SeedAsync(IServiceProvider sp)
    {
        var connection = await sp.GetRequiredService<IConnectionStore>().CreateAsync(new CreateConnectionRequest(
            $"atl-{Guid.NewGuid():N}"[..20], ConnectionProvider.Atlassian,
            $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{_cloudId}}","clientId":"client-1"}""",
            "service-secret"), null);

        var source = await sp.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            $"cf-{Guid.NewGuid():N}"[..20], connection.Id,
            $$"""{"kind":"confluence-space","spaceId":"{{SpaceId}}","spaceKey":"ENG"}"""));

        return (source, connection);
    }

    private void SeedSpace(int pages = 2)
    {
        _api.Confluence.AddSpace(SpaceId, "ENG", "Engineering");
        _api.Confluence.AddSpace(OtherSpaceId, "OPS", "Operations");
        for (int i = 1; i <= pages; i++)
            _api.Confluence.Upsert(new FakeConfluencePage((100 + i).ToString(), SpaceId, $"Page {i}"));
        _api.Confluence.Upsert(new FakeConfluencePage("201", SpaceId, "News", Kind: "blogpost"));
    }

    /// <summary>
    /// Stands in for the ingestion worker, which does not run here: marks each queued document
    /// ingested with the signature its job carried, which is what the next cycle compares against.
    /// </summary>
    private static async Task SettleAsync(IServiceProvider sp, RecordingIngestionQueue queue)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        foreach (var job in queue.Jobs)
        {
            var document = await db.Documents.SingleAsync(d => d.Id == Guid.Parse(job.DocumentId));
            document.IngestionStatus = DocumentStatus.Ready;
            document.Metadata = new Dictionary<string, string>
            {
                [SourceSyncService.RemoteLastModifiedKey] = job.Options.Metadata![SourceSyncService.RemoteLastModifiedKey],
                [SourceSyncService.RemoteSizeKey] = job.Options.Metadata[SourceSyncService.RemoteSizeKey],
            };
        }

        await db.SaveChangesAsync();
        queue.Jobs.Clear();
    }

    private static async Task<List<string>> IndexedPathsAsync(IServiceProvider sp, Guid sourceId)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        return await db.Documents.Where(d => d.SourceId == sourceId).Select(d => d.Path).ToListAsync();
    }

    /// <summary>Syncs once and settles, then hands back the source as stored, cursor and all.</summary>
    private async Task<Source> SyncAndSettleAsync(
        IServiceProvider sp, SourceSyncService service, RecordingIngestionQueue queue, Source source, Connection connection)
    {
        var result = await service.SyncSourceAsync(source, connection, CancellationToken.None);
        result.Error.Should().BeNull();
        await SettleAsync(sp, queue);
        return (await sp.GetRequiredService<ISourceStore>().GetAsync(source.Id))!;
    }

    [Fact]
    public async Task FirstSync_EnqueuesEveryPageAndBlogpost()
    {
        SeedSpace(pages: 3);
        _api.Confluence.MaxPageSize = 2; // the listing has to follow next links to see them all

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);

        var result = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        result.Error.Should().BeNull();
        result.UsedDeltaPath.Should().BeTrue();
        queue.Jobs.Select(j => j.Options.Path).Should().BeEquivalentTo(
            "/pages/101.md", "/pages/102.md", "/pages/103.md", "/blogposts/201.md");

        var metadata = queue.Jobs.Single(j => j.Options.Path == "/pages/102.md").Options.Metadata!;
        metadata["confluence:spaceKey"].Should().Be("ENG");
        metadata["confluence:title"].Should().Be("Page 2");
        metadata["confluence:url"].Should().Be("https://acme.atlassian.net/wiki/pages/viewpage.action?pageId=102");
    }

    [Fact]
    public async Task SecondSync_Unchanged_EnqueuesNothing()
    {
        SeedSpace();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Error.Should().BeNull();
        second.Upserted.Should().Be(0);
        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task PageEdited_VersionBump_ReEnqueuesOnlyThatPage()
    {
        SeedSpace();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        _api.Confluence.Edit("102");
        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Error.Should().BeNull();
        queue.Jobs.Select(j => j.Options.Path).Should().Equal("/pages/102.md");
    }

    [Fact]
    public async Task PageDeleted_RemovedFromIndex()
    {
        SeedSpace();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        _api.Confluence.Remove("102");
        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Error.Should().BeNull();
        second.Deleted.Should().Be(1);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id))
            .Should().BeEquivalentTo("/pages/101.md", "/blogposts/201.md");
    }

    [Fact]
    public async Task PageMovedToOtherSpace_RemovedFromThisSource()
    {
        SeedSpace();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        _api.Confluence.Move("101", OtherSpaceId);
        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Error.Should().BeNull();
        second.Deleted.Should().Be(1);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id))
            .Should().BeEquivalentTo("/pages/102.md", "/blogposts/201.md");
    }

    [Fact]
    public async Task MassDisappearance_DeletionGuardWithholds()
    {
        SeedSpace(pages: 30);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        for (int i = 1; i <= 20; i++)
            _api.Confluence.Remove((100 + i).ToString());
        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Error.Should().BeNull();
        second.WithheldDeletions.Should().Be(20);
        second.Deleted.Should().Be(0);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().HaveCount(31);
    }

    [Fact]
    public async Task SpaceGone_MarksSourceAccessRevoked()
    {
        SeedSpace();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        _api.Confluence.RemoveSpace(SpaceId);
        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Error.Should().Contain("can no longer be read");
        var after = (await sources.GetAsync(source.Id))!;
        after.AccessRevokedAt.Should().NotBeNull();
        after.SyncCursor.Should().Be(synced.SyncCursor);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().HaveCount(3, "a refusal hides, it does not delete");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedListing_FailsCycle_KeepsStoreCursorAndDocuments(bool laterPage)
    {
        SeedSpace(pages: 3);
        _api.Confluence.MaxPageSize = 1; // every listing follows next links

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        string statePath = Path.Combine(_root, source.Id.ToString("N"));
        var storeBefore = Directory.EnumerateFiles(statePath, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText);

        _api.Intercept = uri =>
            uri.AbsolutePath.EndsWith($"/spaces/{SpaceId}/pages", StringComparison.Ordinal)
            && uri.Query.Contains("cursor=") == laterPage
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"results":[null],"_links":{}}""", System.Text.Encoding.UTF8, "application/json"),
                }
                : null;

        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Error.Should().NotBeNull();
        second.Deleted.Should().Be(0);
        var after = (await sources.GetAsync(source.Id))!;
        after.SyncCursor.Should().Be(synced.SyncCursor);
        after.LastSyncStatus.Should().Be(SyncStatus.Failed);
        Directory.EnumerateFiles(statePath, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText).Should().BeEquivalentTo(storeBefore);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().BeEquivalentTo(
            "/pages/101.md", "/pages/102.md", "/pages/103.md", "/blogposts/201.md");
    }

    /// <summary>
    /// The whole path with nothing faked but Confluence: the app's own connector factory and DI
    /// build the connector from the stored connection and secret, the sync enqueues the page, and
    /// the real ingestion pipeline chunks what the connector renders.
    /// </summary>
    [Fact]
    public async Task EndToEnd_IngestedPage_EveryChunkStartsWithBreadcrumb()
    {
        var confluence = fixture.Atlassian.Confluence;
        string spaceId = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();
        string parentId = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();
        string pageId = (long.Parse(parentId) + 1).ToString();
        confluence.AddSpace(spaceId, "ENG", "Engineering");
        confluence.Upsert(new FakeConfluencePage(parentId, spaceId, "Parent"));
        confluence.Upsert(new FakeConfluencePage(pageId, spaceId, "Title", ParentId: parentId, ParentType: "page",
            Body: "<h1>Install</h1><p>" + string.Join(" ", Enumerable.Repeat("Run the installer and follow each prompt.", 20))
                + "</p><h1>Upgrade</h1><p>Stop the service, then upgrade it in place.</p>"));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var connections = sp.GetRequiredService<IConnectionStore>();
        var connection = await connections.CreateAsync(new CreateConnectionRequest(
            $"atl-{Guid.NewGuid():N}"[..20], ConnectionProvider.Atlassian,
            $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{_cloudId}}","clientId":"client-{{_cloudId}}"}""",
            "service-secret"), null);
        var source = await sp.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            $"cf-{Guid.NewGuid():N}"[..20], connection.Id,
            $$"""{"kind":"confluence-space","spaceId":"{{spaceId}}","spaceKey":"ENG"}"""));

        var factory = sp.GetRequiredService<IConnectorFactory>();
        var queue = new RecordingIngestionQueue();
        var service = new SourceSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(), factory, queue,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());

        (await service.SyncSourceAsync(source, connection, CancellationToken.None)).Error.Should().BeNull();
        var job = queue.Jobs.Single(j => j.Options.Path == $"/pages/{pageId}.md");

        string? secret = await connections.GetSecretAsync(connection.Id);
        var connector = factory.Create(source, connection, secret);
        await using (var content = await connector.ReadFileAsync(job.Options.Path))
            await sp.GetRequiredService<IKnowledgeIngester>().IngestAsync(content, job.Options, CancellationToken.None);

        await using var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var chunks = await db.Chunks.Where(c => c.DocumentId == Guid.Parse(job.DocumentId)).Select(c => c.Content).ToListAsync();
        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(c => c.StartsWith("Engineering > Parent > Title", StringComparison.Ordinal));
        confluence.BodyFormats.Should().OnlyContain(f => f == "storage");
    }

    [Fact]
    public async Task EveryDocument_HasAtlassianResourceUri()
    {
        SeedSpace();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, _) = BuildService(scope.ServiceProvider);

        await service.SyncSourceAsync(source, connection, CancellationToken.None);

        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var documents = await db.Documents.Where(d => d.SourceId == source.Id).ToListAsync();
        documents.Should().HaveCount(3);
        string cloudId = _cloudId.ToLowerInvariant();
        documents.Should().OnlyContain(d =>
            d.ResourceUri == AtlassianUri.ForPage(cloudId, Path.GetFileNameWithoutExtension(d.Path))
            && d.ResourceUri!.StartsWith($"atlassian://{cloudId}/", StringComparison.Ordinal));
    }
}
