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
    private readonly ManualClock _clock = new();

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

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
    /// <summary>What the source's scope would say about attachments; tests flip it between cycles.</summary>
    private sealed class AttachmentOptions
    {
        public bool Include { get; set; } = true;
        public int MaxMb { get; set; } = 25;
    }

    private readonly AttachmentOptions _attachments = new();

    private sealed class FakeApiConnectorFactory(
        FakeAtlassianApi api, string root, TimeProvider clock, AttachmentOptions options, IFileTypeValidator fileTypes)
        : IConnectorFactory
    {
        private readonly AtlassianTokenSource _tokens = new(new FakeHttpClients(api), TimeProvider.System);

        public IConnector Create(Source source, Connection connection, string? secret = null)
        {
            var site = AtlassianSite.FromConfigJson(connection.ConfigJson)!;
            return new ConfluenceSpaceConnector(
                new ConfluenceSpaceConfig(
                    site, SpaceId, "ENG", Path.Combine(root, source.Id.ToString("N")), options.Include, options.MaxMb),
                new AtlassianApiClient(api.CreateClient(), _tokens, site, secret!),
                clock: clock,
                fileTypes: fileTypes);
        }

        public IConnector Create(Source source) => throw new InvalidOperationException("Confluence sources have a connection");
    }

    private (SourceSyncService Service, RecordingIngestionQueue Queue) BuildService(IServiceProvider sp)
    {
        var queue = new RecordingIngestionQueue();
        var service = new SourceSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new FakeApiConnectorFactory(_api, _root, _clock, _attachments, sp.GetRequiredService<IFileTypeValidator>()),
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

    /// <summary>
    /// Space Engineering: Root (101) > Child (102); Guides folder (900) > Guide (103); Other (104)
    /// at the top; and a blog post. Every page's breadcrumb depends on something above it.
    /// </summary>
    private void SeedTree()
    {
        _api.Confluence.AddSpace(SpaceId, "ENG", "Engineering");
        _api.Confluence.Upsert(new FakeConfluencePage("101", SpaceId, "Root"));
        _api.Confluence.Upsert(new FakeConfluencePage("102", SpaceId, "Child", ParentId: "101", ParentType: "page"));
        _api.Confluence.AddFolder("900", "Guides");
        _api.Confluence.Upsert(new FakeConfluencePage("103", SpaceId, "Guide", ParentId: "900", ParentType: "folder"));
        _api.Confluence.Upsert(new FakeConfluencePage("104", SpaceId, "Other"));
        _api.Confluence.Upsert(new FakeConfluencePage("201", SpaceId, "News", Kind: "blogpost"));
    }

    private async Task<(SourceSyncService Service, RecordingIngestionQueue Queue, Source Synced, Connection Connection)>
        SyncTreeAsync(IServiceProvider sp)
    {
        SeedTree();
        var (source, connection) = await SeedAsync(sp);
        var (service, queue) = BuildService(sp);
        var synced = await SyncAndSettleAsync(sp, service, queue, source, connection);
        return (service, queue, synced, connection);
    }

    [Fact]
    public async Task SecondSync_UnchangedTreeAfterFolderRefresh_EnqueuesNothing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (service, queue, synced, connection) = await SyncTreeAsync(scope.ServiceProvider);

        _clock.Advance(ConfluenceSpaceConnector.FolderRefreshInterval + TimeSpan.FromMinutes(1));
        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Error.Should().BeNull();
        queue.Jobs.Should().BeEmpty("re-reading an unchanged folder must not move any signature");
    }

    [Fact]
    public async Task ParentPageRenamed_ReEnqueuesItsChildButNotAnUnrelatedPage()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (service, queue, synced, connection) = await SyncTreeAsync(scope.ServiceProvider);

        _api.Confluence.Rename("101", "Start Here");
        (await service.SyncSourceAsync(synced, connection, CancellationToken.None)).Error.Should().BeNull();

        queue.Jobs.Select(j => j.Options.Path).Should().BeEquivalentTo("/pages/101.md", "/pages/102.md");
    }

    [Fact]
    public async Task FolderRenamed_ReEnqueuesPagesUnderItOnceTheFolderIsReadAgain()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (service, queue, synced, connection) = await SyncTreeAsync(scope.ServiceProvider);

        _api.Confluence.RenameFolder("900", "Handbooks");
        _clock.Advance(ConfluenceSpaceConnector.FolderRefreshInterval + TimeSpan.FromMinutes(1));
        (await service.SyncSourceAsync(synced, connection, CancellationToken.None)).Error.Should().BeNull();

        queue.Jobs.Select(j => j.Options.Path).Should().Equal("/pages/103.md");
    }

    [Fact]
    public async Task SpaceRenamed_ReEnqueuesEveryPage()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (service, queue, synced, connection) = await SyncTreeAsync(scope.ServiceProvider);

        _api.Confluence.RenameSpace(SpaceId, "Platform Engineering");
        (await service.SyncSourceAsync(synced, connection, CancellationToken.None)).Error.Should().BeNull();

        queue.Jobs.Select(j => j.Options.Path).Should().BeEquivalentTo(
            "/pages/101.md", "/pages/102.md", "/pages/103.md", "/pages/104.md", "/blogposts/201.md");
    }

    [Fact]
    public async Task PageMovedUnderAnotherParent_ReEnqueuedWithoutAVersionBump()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (service, queue, synced, connection) = await SyncTreeAsync(scope.ServiceProvider);

        _api.Confluence.Reparent("104", "101", "page");
        (await service.SyncSourceAsync(synced, connection, CancellationToken.None)).Error.Should().BeNull();

        queue.Jobs.Select(j => j.Options.Path).Should().Equal("/pages/104.md");
    }

    [Fact]
    public async Task RateLimitedPartwayThroughListing_DeletesNothingAndKeepsTheCursor()
    {
        SeedSpace(pages: 3);
        _api.Confluence.MaxPageSize = 1;

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        // Page 101 is gone, but the listing stops after its first page of results: what it saw
        // would read as 102 and 103 having vanished too.
        _api.Confluence.Remove("101");
        _api.Confluence.RateLimitNextContinuation = true;
        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Error.Should().BeNull();
        second.Deleted.Should().Be(0);
        second.WithheldDeletions.Should().Be(0);
        second.Notice.Should().Contain("rate limiting");
        queue.Jobs.Should().BeEmpty();
        (await sources.GetAsync(source.Id))!.SyncCursor.Should().Be(synced.SyncCursor);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().HaveCount(4);
    }

    [Fact]
    public async Task SpaceForbidden_MarksSourceAccessRevoked()
    {
        SeedSpace();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        _api.Confluence.ForbidSpace(SpaceId);
        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Error.Should().Contain("HTTP 403");
        (await sources.GetAsync(source.Id))!.AccessRevokedAt.Should().NotBeNull();
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().HaveCount(3);
    }

    [Fact]
    public async Task NewComment_ReEnqueuesItsPage()
    {
        SeedSpace();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        _clock.Advance(TimeSpan.FromMinutes(5));
        _api.Confluence.AddComment("102", "acc-1", "<p>Looks good.</p>", _clock.GetUtcNow().AddMinutes(-1));
        (await service.SyncSourceAsync(synced, connection, CancellationToken.None)).Error.Should().BeNull();

        queue.Jobs.Select(j => j.Options.Path).Should().Equal("/pages/102.md");
    }

    [Fact]
    public async Task RepeatedOverlapHits_DoNotReEnqueue()
    {
        SeedSpace();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        _clock.Advance(TimeSpan.FromMinutes(5));
        _api.Confluence.AddComment("102", "acc-1", "<p>Looks good.</p>", _clock.GetUtcNow().AddMinutes(-1));
        synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, synced, connection);

        // The comment is still inside the next query's one-day overlap, so CQL reports it again.
        _clock.Advance(TimeSpan.FromMinutes(5));
        var third = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        third.Error.Should().BeNull();
        _api.Confluence.SearchQueries.Should().HaveCount(2);
        queue.Jobs.Should().BeEmpty();
        (await sources.GetAsync(source.Id))!.SyncCursor.Should().NotBe(synced.SyncCursor, "the watermark still moves on");
    }

    [Fact]
    public async Task RateLimitedDuringChangeQuery_DeletesNothingAndKeepsTheCursor()
    {
        SeedSpace();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        _api.Confluence.Remove("101");
        _api.Confluence.AddComment("102", "acc-1", "<p>Missed this cycle.</p>", _clock.GetUtcNow());
        _api.Confluence.RateLimitNextSearch = true;
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Error.Should().BeNull();
        second.Deleted.Should().Be(0);
        second.Notice.Should().Contain("rate limiting");
        queue.Jobs.Should().BeEmpty();
        (await sources.GetAsync(source.Id))!.SyncCursor.Should().Be(synced.SyncCursor);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().HaveCount(3);

        // The next cycle counts back from the old watermark, so the comment is still caught.
        var third = await service.SyncSourceAsync((await sources.GetAsync(source.Id))!, connection, CancellationToken.None);
        third.Deleted.Should().Be(1);
        queue.Jobs.Select(j => j.Options.Path).Should().Equal("/pages/102.md");
    }

    private FakeConfluenceAttachment AttachmentOn(string pageId, string id, string name, long? size = null) =>
        new(id, pageId, name, "attached runbook text"u8.ToArray(), _clock.GetUtcNow().AddMinutes(-1), DeclaredSize: size);

    [Fact]
    public async Task NewAttachment_Ingested()
    {
        SeedSpace();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        _clock.Advance(TimeSpan.FromMinutes(5));
        _api.Confluence.AddAttachment(AttachmentOn("102", "601", "runbook.txt"));
        (await service.SyncSourceAsync(synced, connection, CancellationToken.None)).Error.Should().BeNull();

        var job = queue.Jobs.Should().ContainSingle().Subject;
        job.Options.Path.Should().Be("/attachments/601/runbook.txt");
        job.Options.Metadata!["confluence:breadcrumb"].Should().Be("Engineering > Page 2 > runbook.txt");
        job.Options.Metadata[SourceSyncService.RemoteSizeKey].Should().Be("21", "an attachment's signature is its real size");
    }

    [Fact]
    public async Task AttachmentDeleted_RemovedAfterDailySweep()
    {
        SeedSpace();
        _api.Confluence.AddAttachment(AttachmentOn("102", "601", "runbook.txt"));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().Contain("/attachments/601/runbook.txt");

        // A deletion is invisible to the change query, so it waits for the sweep.
        _api.Confluence.RemoveAttachment("601");
        _clock.Advance(TimeSpan.FromMinutes(5));
        synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, synced, connection);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().Contain("/attachments/601/runbook.txt");

        _clock.Advance(ConfluenceSpaceConnector.AttachmentSweepInterval);
        var swept = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        swept.Deleted.Should().Be(1);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id)).Should().NotContain("/attachments/601/runbook.txt");
    }

    [Fact]
    public async Task OversizeAttachment_SkippedAndCounted()
    {
        SeedSpace();
        _api.Confluence.AddAttachment(AttachmentOn("102", "601", "huge.txt", size: 30L * 1024 * 1024));
        _api.Confluence.AddAttachment(AttachmentOn("102", "602", "small.txt"));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);

        var result = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        result.Notice.Should().Be("1 attachment skipped: over 25 MB or unsupported type");
        queue.Jobs.Select(j => j.Options.Path).Should().Contain("/attachments/602/small.txt")
            .And.NotContain(p => p.StartsWith("/attachments/601/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsupportedAttachment_SkippedAndCounted()
    {
        SeedSpace();
        _api.Confluence.AddAttachment(AttachmentOn("101", "601", "setup.exe"));
        _api.Confluence.AddAttachment(AttachmentOn("102", "602", "archive.bin"));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);

        var result = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        result.Notice.Should().Be("2 attachments skipped: over 25 MB or unsupported type");
        queue.Jobs.Should().NotContain(j => j.Options.Path.StartsWith("/attachments/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncludeAttachmentsOff_RemovesExisting()
    {
        SeedSpace();
        _api.Confluence.AddAttachment(AttachmentOn("102", "601", "runbook.txt"));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, queue) = BuildService(scope.ServiceProvider);
        var synced = await SyncAndSettleAsync(scope.ServiceProvider, service, queue, source, connection);

        _attachments.Include = false;
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await service.SyncSourceAsync(synced, connection, CancellationToken.None);

        second.Deleted.Should().Be(1);
        (await IndexedPathsAsync(scope.ServiceProvider, source.Id))
            .Should().BeEquivalentTo("/pages/101.md", "/pages/102.md", "/blogposts/201.md");
    }

    [Fact]
    public async Task AttachmentResourceUri_PointsAtPage()
    {
        SeedSpace();
        _api.Confluence.AddAttachment(AttachmentOn("102", "601", "runbook.txt"));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedAsync(scope.ServiceProvider);
        var (service, _) = BuildService(scope.ServiceProvider);
        await service.SyncSourceAsync(source, connection, CancellationToken.None);

        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        string? uri = await db.Documents
            .Where(d => d.SourceId == source.Id && d.Path == "/attachments/601/runbook.txt")
            .Select(d => d.ResourceUri).SingleAsync();

        uri.Should().Be(AtlassianUri.ForAttachment(_cloudId.ToLowerInvariant(), "102", "601"));
        AtlassianUri.TryParse(uri, out string cloudId, out string contentId).Should().BeTrue();
        contentId.Should().Be("102", "a hit on the attachment is checked against its page");
        cloudId.Should().Be(_cloudId.ToLowerInvariant());
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
