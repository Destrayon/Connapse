using System.Globalization;
using System.Text.Json;
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

/// <summary>
/// Sync behaviour for sources. External sources stopped syncing entirely when #359 moved
/// them out of the containers table that ConnectorWatcherService enumerated; these tests
/// cover the replacement.
/// <para>
/// The seam is <see cref="IConnectorFactory"/>: each test substitutes it so the service
/// resolves a fake remote instead of reaching S3 or Azure.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceSyncIntegrationTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string p) => $"{p}-{Guid.NewGuid():N}"[..20];

    /// <summary>A connector with no delta support, forcing the list-and-diff fallback.</summary>
    private sealed class FakeListConnector(params ConnectorFile[] files) : IConnector
    {
        public ConnectorType Type => ConnectorType.S3;
        public bool SupportsLiveWatch => false;
        public int ListCalls { get; private set; }

        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream("remote content"u8.ToArray()));

        public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<ConnectorFile>>(files);
        }

        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
        public string ResolveJobPath(string relativePath) => relativePath;
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>A connector that reports changes against a cursor.</summary>
    private sealed class FakeDeltaConnector(SyncDelta delta) : ISyncCursorConnector
    {
        public ConnectorType Type => ConnectorType.S3;
        public bool SupportsLiveWatch => false;
        public string? LastCursorSeen { get; private set; }

        public Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct = default)
        {
            LastCursorSeen = cursor;
            return Task.FromResult(delta);
        }

        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream("remote content"u8.ToArray()));
        public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectorFile>>([]);
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
        public string ResolveJobPath(string relativePath) => relativePath;
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>A connector that records whether it was disposed.</summary>
    private sealed class DisposableConnector : IConnector, IDisposable
    {
        public bool Disposed { get; private set; }
        public ConnectorType Type => ConnectorType.S3;
        public bool SupportsLiveWatch => false;

        public void Dispose() => Disposed = true;

        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConnectorFile>>([]);
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
        public string ResolveJobPath(string relativePath) => relativePath;
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>A connector that records disposal and fails every remote call.</summary>
    private sealed class ThrowingDisposableConnector : IConnector, IDisposable
    {
        public bool Disposed { get; private set; }
        public ConnectorType Type => ConnectorType.S3;
        public bool SupportsLiveWatch => false;

        public void Dispose() => Disposed = true;

        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
            => throw new IOException("remote unavailable");
        public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
            => throw new IOException("remote unavailable");
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public string ResolveJobPath(string relativePath) => relativePath;
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>A connector that blocks inside ListFilesAsync until released.</summary>
    private sealed class BlockingConnector : IConnector
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the connector is inside the remote call.</summary>
        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public ConnectorType Type => ConnectorType.S3;
        public bool SupportsLiveWatch => false;

        public async Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            await _release.Task;
            return [];
        }

        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
        public string ResolveJobPath(string relativePath) => relativePath;
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>A connector whose remote calls fail.</summary>
    private sealed class ThrowingConnector : IConnector
    {
        public ConnectorType Type => ConnectorType.S3;
        public bool SupportsLiveWatch => false;
        public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
            => throw new IOException("remote unavailable");
        public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
            => throw new IOException("remote unavailable");
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public string ResolveJobPath(string relativePath) => relativePath;
        public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static ConnectorFile File(string path, long size = 10) =>
        new(path, size, DateTime.UtcNow, "text/markdown");

    private async Task<(Source Source, Connection Connection)> SeedSourceAsync(IServiceProvider sp)
    {
        var connections = sp.GetRequiredService<IConnectionStore>();
        var sources = sp.GetRequiredService<ISourceStore>();

        var connection = await connections.CreateAsync(
            new CreateConnectionRequest(ShortName("c"), ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);
        var source = await sources.CreateAsync(
            new CreateSourceRequest(ShortName("s"), connection.Id, """{"bucketName":"b"}"""));

        return (source, connection);
    }

    /// <summary>
    /// Hands the service a fixed connector. Written by hand rather than mocked because the
    /// integration test project does not reference NSubstitute.
    /// </summary>
    private sealed class FixedConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(Container container) => connector;
        public IConnector Create(Source source, Connection connection) => connector;
    }

    private static SourceSyncService BuildService(IServiceProvider sp, IConnector connector)
    {
        var factory = new FixedConnectorFactory(connector);

        return new SourceSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            factory,
            sp.GetRequiredService<IIngestionQueue>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());
    }

    [Fact]
    public async Task SyncSourceAsync_FallbackPath_EnqueuesNewRemoteFiles()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var connector = new FakeListConnector(File("/a.md"), File("/b.md"));
        var service = BuildService(scope.ServiceProvider, connector);

        var result = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        result.UsedDeltaPath.Should().BeFalse("this connector has no delta API");
        result.Upserted.Should().Be(2);
        connector.ListCalls.Should().Be(1);
    }

    [Fact]
    public async Task SyncSourceAsync_FallbackPath_DeletesDocumentsGoneFromRemote()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        // A document that exists locally but no longer exists remotely.
        var documentId = Guid.NewGuid();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at) VALUES ({0}, NULL, {1}, 'gone.md', '/gone.md', '', 1, now())",
                documentId, source.Id);
        }

        var service = BuildService(scope.ServiceProvider, new FakeListConnector(File("/still-here.md")));

        var result = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        result.Deleted.Should().Be(1);
        await using var after = await factory.CreateDbContextAsync();
        (await after.Documents.AnyAsync(d => d.Id == documentId))
            .Should().BeFalse("a file removed remotely must be removed from the index");
    }

    [Fact]
    public async Task SyncSourceAsync_DeltaPath_AdvancesTheStoredCursor()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var delta = new SyncDelta([File("/new.md")], [], "cursor-1", RequiresFullResync: false);
        var service = BuildService(scope.ServiceProvider, new FakeDeltaConnector(delta));

        var result = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        result.UsedDeltaPath.Should().BeTrue();
        (await sources.GetAsync(source.Id))!.SyncCursor.Should().Be("cursor-1");
    }

    [Fact]
    public async Task SyncSourceAsync_ConcurrentCompletion_DoesNotRegressTheCursor()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        // Another cycle finished first and already moved the cursor forward.
        await sources.UpdateSyncStateAsync(source.Id, "cursor-B", SyncStatus.Succeeded, null, DateTime.UtcNow);

        // This cycle still holds the original (null) cursor, so its write must lose.
        var stale = new SyncDelta([], [], "cursor-A", RequiresFullResync: false);
        var service = BuildService(scope.ServiceProvider, new FakeDeltaConnector(stale));

        await service.SyncSourceAsync(source, connection, CancellationToken.None);

        (await sources.GetAsync(source.Id))!.SyncCursor
            .Should().Be("cursor-B", "compare-and-swap must reject a late finisher");
    }

    [Fact]
    public async Task SyncSourceAsync_RequiresFullResync_ClearsCursorUnconditionally()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        await sources.UpdateSyncStateAsync(source.Id, "stale-token", SyncStatus.Succeeded, null, DateTime.UtcNow);

        // Graph answers a stale delta token with 410 Gone, Dropbox with a 409 reset. The
        // clear must land even though compare-and-swap against the caller's expected cursor
        // would reject it — gating this would break the recovery it exists for.
        var resync = new SyncDelta([], [], NextCursor: null, RequiresFullResync: true);
        var service = BuildService(scope.ServiceProvider, new FakeDeltaConnector(resync));

        var result = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        result.RequiredResync.Should().BeTrue();
        (await sources.GetAsync(source.Id))!.SyncCursor.Should().BeNull();
    }

    [Fact]
    public async Task SyncSourceAsync_ConnectorThrows_RecordsFailureAndKeepsCursor()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        await sources.UpdateSyncStateAsync(source.Id, "good-cursor", SyncStatus.Succeeded, null, DateTime.UtcNow);

        var service = BuildService(scope.ServiceProvider, new ThrowingConnector());

        var result = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        result.Error.Should().NotBeNull();
        var reloaded = await sources.GetAsync(source.Id);
        reloaded!.LastSyncStatus.Should().Be(SyncStatus.Failed);
        reloaded.SyncCursor.Should().Be("good-cursor",
            "a transient remote error must not discard progress — the next cycle resumes from it");
    }

    /// <summary>
    /// The background loop calls SyncAllAsync, not SyncSourceAsync — everything else here
    /// enters one level below it, so the enumeration and the enabled filter would otherwise
    /// ship untested.
    /// </summary>
    [Fact]
    public async Task SyncAllAsync_SyncsEnabledSourcesAndLeavesDisabledOnesAlone()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        var (enabled, _) = await SeedSourceAsync(scope.ServiceProvider);
        var (disabled, _) = await SeedSourceAsync(scope.ServiceProvider);
        await sources.UpdateAsync(disabled.Id, new UpdateSourceRequest(Enabled: false));

        var service = BuildService(scope.ServiceProvider, new FakeListConnector(File("/a.md")));

        await service.SyncAllAsync(CancellationToken.None);

        // Asserted per-source rather than by counting connector calls: the suite shares one
        // database, so SyncAllAsync legitimately picks up sources other tests created.
        (await sources.GetAsync(enabled.Id))!.LastSyncStatus.Should().Be(SyncStatus.Succeeded);
        (await sources.GetAsync(disabled.Id))!.LastSyncStatus.Should().Be(
            SyncStatus.Never, "a disabled source must not be contacted at all");
    }

    [Fact]
    public async Task SyncSourceAsync_UnchangedRemoteFile_IsNotReIngested()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var file = File("/stable.md");
        var connector = new FakeListConnector(file);
        var service = BuildService(scope.ServiceProvider, connector);

        // First cycle indexes it and records the remote's signature.
        (await service.SyncSourceAsync(source, connection, CancellationToken.None))
            .Upserted.Should().Be(1);
        await DrainQueueAsync(scope.ServiceProvider, source, file);

        // Second cycle sees the same size and timestamp, so it must do nothing. Without this
        // the poll re-downloads and re-embeds the whole remote every five minutes.
        var second = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        second.Upserted.Should().Be(0, "an unchanged remote file must not be re-ingested");
    }

    [Fact]
    public async Task SyncSourceAsync_ChangedRemoteFile_IsReIngested()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var original = File("/report.md", size: 10);
        var service = BuildService(scope.ServiceProvider, new FakeListConnector(original));

        await service.SyncSourceAsync(source, connection, CancellationToken.None);
        await DrainQueueAsync(scope.ServiceProvider, source, original);

        // Same path, different size and timestamp — the remote changed.
        var updated = original with { SizeBytes = 99, LastModified = original.LastModified.AddHours(1) };
        var changedService = BuildService(scope.ServiceProvider, new FakeListConnector(updated));

        var result = await changedService.SyncSourceAsync(source, connection, CancellationToken.None);

        result.Upserted.Should().Be(1, "a file whose remote signature moved must be re-ingested");
    }

    /// <summary>
    /// Stands in for the ingestion worker, which does not run in these tests: writes the
    /// document the pipeline would have written, carrying the remote signature the sync
    /// service put on the job. Change detection reads that signature back on the next cycle.
    /// <para>
    /// Written as raw SQL because <c>IDocumentStore.StoreAsync</c> always populates
    /// <c>container_id</c> and so cannot express a source-owned row at all.
    /// </para>
    /// </summary>
    private static async Task DrainQueueAsync(IServiceProvider sp, Source source, ConnectorFile file)
    {
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync();

        string metadata = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [SourceSyncService.RemoteLastModifiedKey] = file.LastModified.ToString("O"),
            [SourceSyncService.RemoteSizeKey] = file.SizeBytes.ToString(CultureInfo.InvariantCulture),
        });

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, status, created_at, metadata)
            VALUES ({0}, NULL, {1}, {2}, {3}, '', {4}, 'Ready', now(), {5}::jsonb)
            ON CONFLICT (owner_id, path) DO UPDATE SET
                size_bytes = EXCLUDED.size_bytes,
                status     = 'Ready',
                metadata   = EXCLUDED.metadata
            """,
            Guid.NewGuid(), source.Id, Path.GetFileName(file.Path), file.Path, file.SizeBytes, metadata);
    }

    /// <summary>
    /// S3Connector owns an AmazonS3Client and its socket pool, and a cycle runs every five
    /// minutes per source — so failing to dispose abandons one client per source per cycle.
    /// </summary>
    [Fact]
    public async Task SyncSourceAsync_DisposesTheConnectorItCreated()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var connector = new DisposableConnector();
        await BuildService(scope.ServiceProvider, connector)
            .SyncSourceAsync(source, connection, CancellationToken.None);

        connector.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task SyncSourceAsync_DisposesTheConnectorEvenWhenTheRemoteFails()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        // The failure path is the one that matters: an unreachable remote is exactly when
        // cycles repeat, so a leak here compounds fastest.
        var connector = new ThrowingDisposableConnector();
        var result = await BuildService(scope.ServiceProvider, connector)
            .SyncSourceAsync(source, connection, CancellationToken.None);

        result.Error.Should().NotBeNull();
        connector.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task SyncSourceAsync_WhileAnotherCycleIsRunning_ReportsAlreadyRunning()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var connector = new BlockingConnector();
        var service = BuildService(scope.ServiceProvider, connector);

        // The timer was once the only caller and ran sources sequentially, so overlap could
        // not happen. The sync-now endpoint can start a second cycle for a source the timer
        // already holds — two cycles would list the same remote twice and enqueue the same
        // files twice, which is duplicated embedding work rather than a correctness bug, but
        // a costly one.
        var firstCycle = service.SyncSourceAsync(source, connection, CancellationToken.None);
        await connector.Entered; // the first cycle is now inside the remote call

        var second = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        second.AlreadyRunning.Should().BeTrue();
        second.Error.Should().BeNull("nothing went wrong — the work belongs to the cycle in flight");

        connector.Release();
        (await firstCycle).AlreadyRunning.Should().BeFalse("the first cycle held the gate and ran");
    }

    [Fact]
    public async Task SyncSourceAsync_AfterACycleCompletes_TheGateIsReleased()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var service = BuildService(scope.ServiceProvider, new FakeListConnector());

        await service.SyncSourceAsync(source, connection, CancellationToken.None);
        var second = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        // A gate that is never released would wedge the source permanently after one cycle.
        second.AlreadyRunning.Should().BeFalse();
    }

    [Fact]
    public async Task SyncSourceAsync_WhenTheRemoteThrows_TheGateIsStillReleased()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var service = BuildService(scope.ServiceProvider, new ThrowingConnector());

        await service.SyncSourceAsync(source, connection, CancellationToken.None);
        var second = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        // The failure path is where a missed release would hurt most: an unreachable remote
        // is exactly when cycles repeat.
        second.AlreadyRunning.Should().BeFalse();
        second.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncSourceAsync_DisabledSource_MakesNoRemoteCalls()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        await sources.UpdateAsync(source.Id, new UpdateSourceRequest(Enabled: false));
        var disabled = (await sources.GetAsync(source.Id))!;

        var connector = new FakeListConnector(File("/a.md"));
        var service = BuildService(scope.ServiceProvider, connector);

        var result = await service.SyncSourceAsync(disabled, connection, CancellationToken.None);

        result.Upserted.Should().Be(0);
        connector.ListCalls.Should().Be(0, "a disabled source must not touch the remote at all");
    }
}
