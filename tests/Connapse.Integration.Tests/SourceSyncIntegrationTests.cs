using System.Globalization;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Pipeline;
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

    private static ConnectorFile Located(string path, string uri, long size = 10) =>
        new(path, size, DateTime.UtcNow, "text/markdown", uri);

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
        public IConnector Create(Source source, Connection connection, string? secret = null) => connector;
    }

    private static SourceSyncService BuildService(IServiceProvider sp, IConnector connector)
    {
        var factory = new FixedConnectorFactory(connector);

        return new SourceSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            factory,
            new RecordingIngestionQueue(),
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
    public async Task SyncSourceAsync_SecondPollBeforeIngestionDrains_DoesNotEnqueueTheSameFilesAgain()
    {
        // "Is this file already being worked on?" used to be answerable only from persisted
        // documents, and the pipeline does not write one until the job actually runs. On a
        // source whose ingestion takes longer than the poll interval — a large SFTP tree is
        // exactly that — every cycle rediscovered the entire backlog as new, minted fresh
        // document ids, and enqueued it all over again. The same files downloaded and embedded
        // repeatedly, and a queue growing faster than it could drain.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var connector = new FakeListConnector(File("/a.md"), File("/b.md"));
        var service = BuildService(scope.ServiceProvider, connector);

        var first = await service.SyncSourceAsync(source, connection, CancellationToken.None);
        first.Upserted.Should().Be(2);

        // Nothing has drained the queue in between — the second poll sees exactly what the
        // first one did.
        var second = await service.SyncSourceAsync(source, connection, CancellationToken.None);

        second.Upserted.Should().Be(0,
            "the files are already claimed and queued; enqueueing them again duplicates the work");

        await using var after = await factory.CreateDbContextAsync();
        (await after.Documents.CountAsync(d => d.SourceId == source.Id))
            .Should().Be(2, "two remote files must not become four document rows");
    }

    [Fact]
    public async Task SyncSourceAsync_ClaimedButNotYetIngested_CarriesNoRemoteSignature()
    {
        // The signature means "indexed at this version of the remote". Writing it on the claim
        // would assert an ingestion that has not happened, so a job that then failed would
        // leave a document the next cycle reads as up to date and never retries.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var service = BuildService(scope.ServiceProvider, new FakeListConnector(File("/claim.md")));
        await service.SyncSourceAsync(source, connection, CancellationToken.None);

        await using var after = await factory.CreateDbContextAsync();
        var claim = await after.Documents.AsNoTracking()
            .SingleAsync(d => d.SourceId == source.Id && d.Path == "/claim.md");

        claim.Status.Should().Be("Pending");
        claim.Metadata.Should().NotContainKey("RemoteLastModified");
        claim.Metadata.Should().NotContainKey("RemoteSize");
    }

    [Fact]
    public async Task SyncSourceAsync_DocumentThatKeepsFailing_StopsBeingRetried()
    {
        // #400 made a Failed document retry regardless of its signature, so a transient fault
        // could not poison it permanently. That assumed the failure was transient. One that is
        // not — wrong credentials, a server that is gone — then re-enqueued every file in the
        // source on every cycle, each carrying Hangfire's own three retries, until the ingestion
        // queue was full of work that could not succeed and ordinary uploads sat behind it.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var file = File("/keeps-failing.md");
        var service = BuildService(scope.ServiceProvider, new FakeListConnector(file));

        // The claim from the first cycle, left as a job that failed and never wrote chunks.
        (await service.SyncSourceAsync(source, connection, CancellationToken.None)).Upserted.Should().Be(1);
        await MarkFailedAsync(dbFactory, source.Id, file);

        var upserts = new List<int>();
        for (int cycle = 0; cycle < 5; cycle++)
        {
            upserts.Add((await service.SyncSourceAsync(source, connection, CancellationToken.None)).Upserted);
            await MarkFailedAsync(dbFactory, source.Id, file, keepAttempts: true);
        }

        upserts.Should().BeEquivalentTo(new[] { 1, 1, 1, 0, 0 }, o => o.WithStrictOrdering(),
            "three fresh starts, then the sync engine leaves it alone");
    }

    [Fact]
    public async Task SyncSourceAsync_FailedDocumentWhoseRemoteChanged_IsRetriedAgain()
    {
        // The bound is per version of the file. Someone who fixes the file upstream should not
        // have to wait, or clear anything by hand.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var file = File("/fixed-upstream.md");
        var service = BuildService(scope.ServiceProvider, new FakeListConnector(file));
        await service.SyncSourceAsync(source, connection, CancellationToken.None);

        // Exhausted: Failed, with the attempts already spent.
        await MarkFailedAsync(dbFactory, source.Id, file, attempts: 3);
        (await service.SyncSourceAsync(source, connection, CancellationToken.None)).Upserted
            .Should().Be(0, "the bound must actually bind before the reset means anything");

        // Same path, edited upstream.
        var edited = new ConnectorFile(file.Path, file.SizeBytes + 500, DateTime.UtcNow, file.ContentType);
        var afterEdit = BuildService(scope.ServiceProvider, new FakeListConnector(edited));

        (await afterEdit.SyncSourceAsync(source, connection, CancellationToken.None)).Upserted
            .Should().Be(1, "a changed file is a new attempt, not a continuation of the old one");
    }

    /// <summary>
    /// Leaves the document the way a failed ingestion does: Failed, with the remote signature
    /// already written — which is what made the signature comparison skip it before #400.
    /// </summary>
    private static async Task MarkFailedAsync(
        IDbContextFactory<KnowledgeDbContext> dbFactory, Guid sourceId, ConnectorFile file,
        int? attempts = null, bool keepAttempts = false)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var doc = await ctx.Documents.SingleAsync(d => d.SourceId == sourceId && d.Path == file.Path);

        var metadata = new Dictionary<string, string>(
            keepAttempts ? doc.Metadata ?? new Dictionary<string, string>() : new Dictionary<string, string>())
        {
            [SourceSyncService.RemoteLastModifiedKey] = file.LastModified.ToString("O"),
            [SourceSyncService.RemoteSizeKey] = file.SizeBytes.ToString(CultureInfo.InvariantCulture),
        };

        if (attempts is int fixedAttempts)
            metadata[IngestionPipeline.SyncFailedAttemptsKey] = fixedAttempts.ToString(CultureInfo.InvariantCulture);

        doc.Status = "Failed";
        doc.Metadata = metadata;
        await ctx.SaveChangesAsync();
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
    // ── Retrying a failed document (#400) ──────────────────────────────────

    /// <summary>
    /// Seeds one document with a given status and a remote signature matching what the connector
    /// will report, so change detection sees an unchanged file and the status is the only thing
    /// that can decide whether it is re-enqueued.
    /// </summary>
    /// <remarks>
    /// Inserted via raw SQL because <c>IDocumentStore.StoreAsync</c> always populates
    /// <c>container_id</c> and so cannot express a source-owned row.
    /// </remarks>
    private static async Task SeedDocumentWithSignatureAsync(
        IServiceProvider sp, Guid sourceId, string path, string status, DateTime lastModified, long size)
    {
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync();

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, status, metadata, created_at) "
            + "VALUES ({0}, NULL, {1}, {2}, {3}, '', {4}, {5}, {6}::jsonb, now())",
            Guid.NewGuid(), sourceId, Path.GetFileName(path), path, size, status,
            $$"""{"RemoteLastModified":"{{lastModified:O}}","RemoteSize":"{{size}}"}""");
    }

    /// <summary>
    /// The regression. A failed document keeps the signature written before it failed, so change
    /// detection saw an unchanged file and skipped it — leaving it Failed with zero chunks for
    /// ever, or until the remote happened to change. Any transient downstream fault was
    /// therefore permanent.
    /// </summary>
    [Fact]
    public async Task Sync_DocumentLeftFailed_IsRetriedEvenThoughTheRemoteIsUnchanged()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);
        var modified = new DateTime(2026, 8, 23, 7, 4, 54, DateTimeKind.Utc);

        await SeedDocumentWithSignatureAsync(
            scope.ServiceProvider, source.Id, "/a.md", "Failed", modified, size: 100);

        var connector = new FakeListConnector(new ConnectorFile("/a.md", 100, modified, null));
        var result = await BuildService(scope.ServiceProvider, connector)
            .SyncSourceAsync(source, connection, CancellationToken.None);

        result.Upserted.Should().Be(1, "a failed document must be retried, not skipped as unchanged");
    }

    /// <summary>
    /// The other half, and why this cannot simply ignore status: an already-indexed document with
    /// a matching signature must still be skipped, or a five-minute poll re-embeds the whole
    /// source on every cycle.
    /// </summary>
    [Fact]
    public async Task Sync_DocumentAlreadyIndexed_IsStillSkippedWhenUnchanged()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);
        var modified = new DateTime(2026, 8, 23, 7, 4, 54, DateTimeKind.Utc);

        await SeedDocumentWithSignatureAsync(
            scope.ServiceProvider, source.Id, "/a.md", "Ready", modified, size: 100);

        var connector = new FakeListConnector(new ConnectorFile("/a.md", 100, modified, null));
        var result = await BuildService(scope.ServiceProvider, connector)
            .SyncSourceAsync(source, connection, CancellationToken.None);

        result.Upserted.Should().Be(0, "change detection is what keeps a poll from re-embedding everything");
    }

    /// <summary>
    /// And the in-flight guard survives: a document mid-ingestion must not be enqueued twice
    /// against the same id.
    /// </summary>
    [Theory]
    [InlineData("Pending")]
    [InlineData("Queued")]
    [InlineData("Processing")]
    public async Task Sync_DocumentInFlight_IsStillSkipped(string status)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);
        var modified = new DateTime(2026, 8, 23, 7, 4, 54, DateTimeKind.Utc);

        await SeedDocumentWithSignatureAsync(
            scope.ServiceProvider, source.Id, "/a.md", status, modified, size: 100);

        var connector = new FakeListConnector(new ConnectorFile("/a.md", 100, modified, null));
        var result = await BuildService(scope.ServiceProvider, connector)
            .SyncSourceAsync(source, connection, CancellationToken.None);

        result.Upserted.Should().Be(0, "enqueueing again would race the in-flight job");
    }

    // -- Document coordinates (#421) ------------------------------------------------

    [Fact]
    public async Task SyncSourceAsync_NewDocument_RecordsWhereItCameFrom()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var connector = new FakeListConnector(
            Located("/q3/report.pdf", "s3://b/team/q3/report.pdf"));

        await BuildService(scope.ServiceProvider, connector)
            .SyncSourceAsync(source, connection, CancellationToken.None);

        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        var document = await db.Documents.AsNoTracking()
            .SingleAsync(d => d.SourceId == source.Id);

        // The connector's answer, stored verbatim. Not rebuilt from bucket + prefix + path, which
        // is the derivation this column exists to avoid.
        document.ResourceUri.Should().Be("s3://b/team/q3/report.pdf");
        document.Path.Should().Be("/q3/report.pdf", "the virtual path is unchanged by this");
    }

    [Fact]
    public async Task SyncSourceAsync_UnchangedDocument_StillRecordsWhereItCameFrom()
    {
        // The case that decides whether this works at all. A stable source skips every file on
        // every cycle -- that skip is what stops a five-minute poll re-embedding everything -- so
        // populating only on ingest would leave a source that never changes without coordinates
        // for ever, and its documents permanently denied once filtering is on.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();

        // First cycle: the connector reports no location, as one built before this column did.
        await BuildService(scope.ServiceProvider, new FakeListConnector(File("/a.md")))
            .SyncSourceAsync(source, connection, CancellationToken.None);

        await using (var before = await factory.CreateDbContextAsync())
        {
            (await before.Documents.AsNoTracking().SingleAsync(d => d.SourceId == source.Id))
                .ResourceUri.Should().BeNull();
        }

        // Second cycle: same file, same size and timestamp, so the sync skips it entirely.
        var located = new FakeListConnector(Located("/a.md", "s3://b/a.md"));
        var result = await BuildService(scope.ServiceProvider, located)
            .SyncSourceAsync(source, connection, CancellationToken.None);

        result.Upserted.Should().Be(0, "nothing changed, so nothing was re-ingested");

        await using var after = await factory.CreateDbContextAsync();
        (await after.Documents.AsNoTracking().SingleAsync(d => d.SourceId == source.Id))
            .ResourceUri.Should().Be("s3://b/a.md", "a skipped file still learns where it is");
    }

    [Fact]
    public async Task SyncSourceAsync_ConnectorReportsNoLocation_LeavesItNull()
    {
        // Filesystem and SFTP have no external address. Null is the honest answer, and once
        // filtering is on it means denied -- never allowed, because a document nothing can locate
        // is a document no permission rule can be checked against.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (source, connection) = await SeedSourceAsync(scope.ServiceProvider);

        await BuildService(scope.ServiceProvider, new FakeListConnector(File("/a.md")))
            .SyncSourceAsync(source, connection, CancellationToken.None);

        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        (await db.Documents.AsNoTracking().SingleAsync(d => d.SourceId == source.Id))
            .ResourceUri.Should().BeNull();
    }
}
