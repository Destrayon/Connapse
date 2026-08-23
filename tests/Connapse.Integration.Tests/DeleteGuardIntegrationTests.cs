using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DeleteGuardIntegrationTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<Source> SeedSourceAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        var connection = await connections.CreateAsync(
            new CreateConnectionRequest(ShortName("conn"), ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);

        return await sources.CreateAsync(
            new CreateSourceRequest(ShortName("src"), connection.Id, """{"bucketName":"b"}"""));
    }

    /// <summary>
    /// Inserts <paramref name="count"/> source-owned documents directly via SQL, following the
    /// raw-insert pattern in OwnerBridgeSchemaTests: <c>IDocumentStore.StoreAsync</c> always
    /// populates <c>container_id</c> and so cannot express a source-owned row.
    /// </summary>
    private async Task SeedDocumentsAsync(Guid sourceId, int count)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync();

        for (int i = 0; i < count; i++)
        {
            string path = $"/doc-{i}.md";

            // status = 'Ready': these rows represent already-indexed documents, not ones
            // mid-ingestion. Left at the column's 'Pending' default, HasRemoteChanged's
            // in-flight check would treat every one of them as already queued and skip it
            // regardless of remote signature, which would make a reconcile's upsert count
            // always zero here.
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, status, created_at) "
                + "VALUES ({0}, NULL, {1}, {2}, {3}, '', 1, 'Ready', now())",
                Guid.NewGuid(), sourceId, $"doc-{i}.md", path);
        }
    }

    /// <summary>
    /// Hands the service a fixed connector, following <c>FixedConnectorFactory</c> in
    /// SourceSyncIntegrationTests, which exists for exactly this purpose.
    /// </summary>
    private sealed class FixedConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(Source source, Connection connection) => connector;
    }

    private async Task<SourceSyncResult> SyncWithConnectorAsync(
        Source source, IConnector connector, bool applyWithheldDeletions = false)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var connection = (await connections.GetAsync(source.ConnectionId))!;

        var service = new SourceSyncService(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            new FixedConnectorFactory(connector),
            scope.ServiceProvider.GetRequiredService<IIngestionQueue>(),
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());

        return await service.SyncSourceAsync(
            source, connection, CancellationToken.None, applyWithheldDeletions);
    }

    /// <summary>
    /// A connector whose listing is empty, without erroring — the shape a narrowed bucket
    /// policy or an unmounted directory actually takes. Before the guard this deleted every
    /// document the source owned.
    /// </summary>
    private sealed class EmptyListingConnector : IConnector
    {
        public ConnectorType Type => ConnectorType.S3;
        public bool SupportsLiveWatch => false;
        public string ResolveJobPath(string relativePath) => "/" + relativePath.TrimStart('/');
        public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectorFile>>([]);
        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// A connector whose listing is a fixed, non-empty set of files, following
    /// <see cref="EmptyListingConnector"/>'s shape. Used where a test needs the remote to
    /// report specific paths rather than nothing.
    /// </summary>
    private sealed class FixedListingConnector(IReadOnlyList<ConnectorFile> files) : IConnector
    {
        public ConnectorType Type => ConnectorType.S3;
        public bool SupportsLiveWatch => false;
        public string ResolveJobPath(string relativePath) => "/" + relativePath.TrimStart('/');
        public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
            => Task.FromResult(files);
        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task Sync_ListingCollapsesToEmpty_WithholdsDeletionsAndKeepsDocuments()
    {
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 40);

        var result = await SyncWithConnectorAsync(source, new EmptyListingConnector());

        result.Deleted.Should().Be(0, "an empty listing is not evidence that 40 files were deleted");
        result.WithheldDeletions.Should().Be(40);

        using var scope = fixture.Factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        (await documents.ListAsync(source.Id, take: int.MaxValue)).Should().HaveCount(40);

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().Be(40);
    }

    [Fact]
    public async Task Sync_WithOverride_AppliesTheWithheldDeletions()
    {
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 40);

        await SyncWithConnectorAsync(source, new EmptyListingConnector());

        // Reloaded, because the approval path reads the withheld count off the source it is
        // given and the first cycle only wrote it to the database. Both real callers — the
        // sync route and the Sources page — load the source immediately before syncing, so
        // passing the stale object here would test something no caller does.
        var reloaded = await ReloadAsync(source.Id);
        var result = await SyncWithConnectorAsync(reloaded, new EmptyListingConnector(), applyWithheldDeletions: true);

        result.Deleted.Should().Be(40);
        result.WithheldDeletions.Should().Be(0);

        using var scope = fixture.Factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        (await documents.ListAsync(source.Id, take: int.MaxValue)).Should().BeEmpty();

        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(source.Id))!.WithheldDeletions
            .Should().BeNull("the pending decision is resolved, so the button must stop showing");
    }

    [Fact]
    public async Task Sync_WhileWithholding_UpsertsStillApply()
    {
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 40);

        // 5 of the 40 indexed paths are still reported by the remote. The raw-SQL seed above
        // writes no remote signature metadata, so HasRemoteChanged treats every one of them as
        // changed and they are re-enqueued — that is what proves upserts still apply here, not
        // just that the connector returned something. The other 35 paths are absent from the
        // remote and are what trips the guard.
        var remoteFiles = Enumerable.Range(0, 5)
            .Select(i => new ConnectorFile($"/doc-{i}.md", 1, DateTime.UtcNow, "text/markdown"))
            .ToList();

        var result = await SyncWithConnectorAsync(source, new FixedListingConnector(remoteFiles));

        result.Upserted.Should().Be(5,
            "a source that trips the guard must keep ingesting new content, or the safety " +
            "mechanism becomes the outage it exists to prevent");
        result.Deleted.Should().Be(0);
        result.WithheldDeletions.Should().Be(35);
    }

    [Fact]
    public async Task Sync_WithOverride_AfterRemoteRecovered_RecomputesAndDeletesNothing()
    {
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 40);

        // First cycle: the listing collapses to empty and all 40 are withheld.
        await SyncWithConnectorAsync(source, new EmptyListingConnector());

        // Second cycle, with the override: the remote has recovered and reports all 40 files
        // again. An implementation that stored and replayed the vanished set from the first
        // cycle would delete all 40 here; one that recomputes deletes nothing.
        var allFiles = Enumerable.Range(0, 40)
            .Select(i => new ConnectorFile($"/doc-{i}.md", 1, DateTime.UtcNow, "text/markdown"))
            .ToList();

        var result = await SyncWithConnectorAsync(
            source, new FixedListingConnector(allFiles), applyWithheldDeletions: true);

        result.Deleted.Should().Be(0,
            "approving re-runs the sync rather than replaying the earlier list, so a remote " +
            "that recovered in the meantime must delete nothing");
        result.WithheldDeletions.Should().Be(0);

        using var scope = fixture.Factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        (await documents.ListAsync(source.Id, take: int.MaxValue)).Should().HaveCount(40);
    }

    [Fact]
    public async Task Sync_SmallDeletionSet_AppliesWithoutWithholding()
    {
        // Five of five is below the floor: small sources must stay able to tidy themselves.
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 5);

        var result = await SyncWithConnectorAsync(source, new EmptyListingConnector());

        result.Deleted.Should().Be(5);
        result.WithheldDeletions.Should().Be(0);
    }

    [Fact]
    public async Task UpdateWithheldDeletionsAsync_RoundTripsTheCount()
    {
        var source = await SeedSourceAsync();

        using var scope = fixture.Factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        (await sources.GetAsync(source.Id))!.WithheldDeletions
            .Should().BeNull("a source with nothing pending must not claim a count of zero");

        await sources.UpdateWithheldDeletionsAsync(source.Id, 42);
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().Be(42);

        // Clearing must return to null, not zero: the UI distinguishes "nothing pending"
        // from "a decision was made", and zero would leave the button showing forever.
        await sources.UpdateWithheldDeletionsAsync(source.Id, null);
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().BeNull();
    }

    [Fact]
    public async Task GetSource_AfterWithholding_ReportsTheCount()
    {
        var source = await SeedSourceAsync();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
            await sources.UpdateWithheldDeletionsAsync(source.Id, 40);
        }

        var response = await fixture.AdminClient.GetAsync($"/api/sources/{source.Id}");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("withheldDeletions").GetInt32().Should().Be(40);
    }

    [Fact]
    public async Task GetSource_WithNothingWithheld_ReportsNull()
    {
        // Null rather than 0, because the page keys the approval button off "is there a
        // pending decision" — a zero would leave it showing forever.
        var source = await SeedSourceAsync();

        var response = await fixture.AdminClient.GetAsync($"/api/sources/{source.Id}");
        var body = await response.Content.ReadAsStringAsync();

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("withheldDeletions").ValueKind
            .Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Sync_WithOverrideButNothingWithheld_StillAppliesTheGuard()
    {
        // The flag must not lift a guard that never tripped. Otherwise a caller passing
        // applyWithheldDeletions=true on a source's very first sync bypasses the guard
        // entirely, and nobody ever sees a count to approve.
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 40);

        var result = await SyncWithConnectorAsync(
            source, new EmptyListingConnector(), applyWithheldDeletions: true);

        result.Deleted.Should().Be(0, "no approval exists, so the guard still applies");
        result.WithheldDeletions.Should().Be(40);

        using var scope = fixture.Factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        (await documents.ListAsync(source.Id, take: int.MaxValue)).Should().HaveCount(40);
    }

    [Fact]
    public async Task Sync_WithOverride_AfterRemoteDegradedFurther_WithholdsAgain()
    {
        // The asymmetry in "recompute rather than replay": recomputing is safer when the
        // remote recovered, and *more* dangerous when it degraded further. An administrator
        // shown 20 must not have 40 applied because the listing worsened between reading the
        // number and pressing the button. The approval is a ceiling, not a licence.
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 40);

        // First cycle: half the files are still visible, so 20 vanish and are withheld.
        var stillPresent = Enumerable.Range(0, 20)
            .Select(i => new ConnectorFile($"/doc-{i}.md", 1, DateTime.UtcNow, "text/markdown"))
            .ToList();

        var withheld = await SyncWithConnectorAsync(source, new FixedListingConnector(stillPresent));
        withheld.WithheldDeletions.Should().Be(20, "20 of 40 vanished, which trips the guard");

        // The administrator approves those 20 — but by the time the cycle runs, the remote has
        // gone completely dark and all 40 now look deleted.
        var reloaded = await ReloadAsync(source.Id);
        var approval = await SyncWithConnectorAsync(
            reloaded, new EmptyListingConnector(), applyWithheldDeletions: true);

        approval.Deleted.Should().Be(0, "40 exceeds the 20 that were approved");
        approval.WithheldDeletions.Should().Be(40, "the larger set is re-withheld for fresh approval");

        using var scope = fixture.Factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        (await documents.ListAsync(source.Id, take: int.MaxValue)).Should().HaveCount(40);
    }

    [Fact]
    public async Task Sync_WithOverride_AfterRemotePartlyRecovered_AppliesTheSmallerSet()
    {
        // The other side of the ceiling: fewer deletions than approved is within what the
        // administrator sanctioned, so it applies rather than needing a second approval.
        var source = await SeedSourceAsync();
        await SeedDocumentsAsync(source.Id, count: 40);

        var withheld = await SyncWithConnectorAsync(source, new EmptyListingConnector());
        withheld.WithheldDeletions.Should().Be(40);

        var mostReturned = Enumerable.Range(0, 35)
            .Select(i => new ConnectorFile($"/doc-{i}.md", 1, DateTime.UtcNow, "text/markdown"))
            .ToList();

        var reloaded = await ReloadAsync(source.Id);
        var approval = await SyncWithConnectorAsync(
            reloaded, new FixedListingConnector(mostReturned), applyWithheldDeletions: true);

        approval.Deleted.Should().Be(5, "5 is within the 40 approved");
        approval.WithheldDeletions.Should().Be(0);

        using var scope = fixture.Factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(source.Id))!.WithheldDeletions.Should().BeNull();
    }

    /// <summary>
    /// Re-reads a source so its <c>WithheldDeletions</c> reflects the previous cycle. The
    /// approval path reads that count off the passed-in source, and both real callers load it
    /// fresh immediately before syncing.
    /// </summary>
    private async Task<Source> ReloadAsync(Guid sourceId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        return (await sources.GetAsync(sourceId))!;
    }
}
