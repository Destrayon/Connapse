using System.Collections.Concurrent;
using System.Globalization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Web.Services;

/// <summary>
/// Keeps sources in step with their remote systems.
/// <para>
/// Replaces ConnectorWatcherService, which enumerated <c>containers</c> and filtered to
/// Filesystem/S3/AzureBlob. The Phase 2 backfill (#350) moved every one of those rows into
/// <c>sources</c>, so the watcher found nothing and external sources stopped syncing
/// altogether. This service is keyed on sources instead.
/// </para>
/// <para>
/// Connectors implementing <see cref="ISyncCursorConnector"/> take a delta path; the rest
/// fall back to listing the remote and diffing it against the indexed documents. The
/// fallback diffs against the database rather than an in-memory snapshot, so progress
/// survives a restart — the old watcher's snapshot did not.
/// </para>
/// </summary>
public class SourceSyncService(
    IServiceScopeFactory scopeFactory,
    IConnectorFactory connectorFactory,
    IIngestionQueue queue,
    ILogger<SourceSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultSyncInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// One gate per source, so two cycles for the same source cannot overlap while different
    /// sources still sync concurrently. Never pruned: the entry is a single semaphore, and a
    /// source that syncs once is likely to sync again.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _syncGates = new();

    /// <summary>Document metadata keys holding the remote's signature at last ingestion.</summary>
    internal const string RemoteLastModifiedKey = "RemoteLastModified";
    internal const string RemoteSizeKey = "RemoteSize";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(DefaultSyncInterval);
        do
        {
            try
            {
                await SyncAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad cycle must not kill the loop for every source.
                logger.LogError(ex, "Source sync cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task SyncAllAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sourceStore = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        var connectionStore = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var sources = await sourceStore.ListAsync(take: int.MaxValue, ct: ct);

        foreach (var source in sources.Where(s => s.Enabled))
        {
            var connection = await connectionStore.GetAsync(source.ConnectionId, ct);
            if (connection is null)
            {
                logger.LogWarning(
                    "Source {SourceId} references missing connection {ConnectionId}; skipping",
                    source.Id, source.ConnectionId);
                continue;
            }

            await SyncSourceAsync(source, connection, ct);
        }
    }

    /// <summary>
    /// Runs one sync cycle for one source. Never throws: a remote failure is recorded on the
    /// source and reported, so one unreachable provider cannot stall every other source.
    /// </summary>
    internal async Task<SourceSyncResult> SyncSourceAsync(Source source, Connection connection, CancellationToken ct)
    {
        if (!source.Enabled)
            return new SourceSyncResult(0, 0, UsedDeltaPath: false, RequiredResync: false, Error: null);

        // One sync per source at a time. The timer used to be the only caller and ran its
        // sources sequentially, so overlap was impossible; the sync-now endpoint can now
        // start a second cycle for a source the timer is already working on. The
        // compare-and-swap below keeps the cursor safe either way, but two cycles would
        // still list the same remote twice and enqueue the same files twice — duplicated
        // embedding work, which costs real money.
        //
        // Acquired without waiting: a caller who arrives during an in-flight cycle wants to
        // hear "already running", not to be blocked behind it.
        var gate = _syncGates.GetOrAdd(source.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
        {
            logger.LogInformation(
                "Sync for source {SourceId} ({Name}) is already in progress; skipping this request",
                source.Id, Sanitize(source.Name));

            return new SourceSyncResult(
                0, 0, UsedDeltaPath: false, RequiredResync: false, Error: null, AlreadyRunning: true);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var sourceStore = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        IConnector? connector = null;
        try
        {
            connector = connectorFactory.Create(source, connection);

            return connector is ISyncCursorConnector cursorConnector
                ? await SyncViaDeltaAsync(source, cursorConnector, sourceStore, scope.ServiceProvider, ct)
                : await SyncViaListAndDiffAsync(source, connector, sourceStore, scope.ServiceProvider, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync failed for source {SourceId} ({Name})", source.Id, Sanitize(source.Name));

            // Record the failure without discarding progress: a transient outage must not
            // clear the cursor, or the next cycle would re-list the entire remote.
            //
            // Re-read rather than reusing the Source passed in — that snapshot was taken at
            // the start of the cycle and its cursor may already be stale, so writing it back
            // would silently roll progress backwards.
            var current = await sourceStore.GetAsync(source.Id, ct);

            await sourceStore.UpdateSyncStateAsync(
                source.Id, current?.SyncCursor, SyncStatus.Failed, ex.Message, DateTime.UtcNow, ct);

            return new SourceSyncResult(0, 0, UsedDeltaPath: false, RequiredResync: false, Error: ex.Message);
        }
        finally
        {
            // S3Connector owns an AmazonS3Client and its socket pool. A cycle runs every five
            // minutes per source, so skipping this abandons a client per source per cycle —
            // the watcher this replaced disposed here for the same reason.
            if (connector is IDisposable disposable) disposable.Dispose();

            gate.Release();
        }
    }

    private async Task<SourceSyncResult> SyncViaDeltaAsync(
        Source source, ISyncCursorConnector connector, ISourceStore sourceStore, IServiceProvider sp, CancellationToken ct)
    {
        SyncDelta delta = await connector.GetChangesAsync(source.SyncCursor, ct);

        if (delta.RequiresFullResync)
        {
            // Deliberately the unconditional path. The provider has told us the stored token
            // is no longer valid, so the clear must land regardless of what is stored —
            // gating it behind compare-and-swap would break the recovery it exists for.
            await sourceStore.UpdateSyncStateAsync(
                source.Id, cursor: null, SyncStatus.Failed,
                "Provider requested a full resync; cursor cleared.", DateTime.UtcNow, ct);

            logger.LogInformation(
                "Source {SourceId} requires a full resync; cursor cleared", source.Id);

            return new SourceSyncResult(0, 0, UsedDeltaPath: true, RequiredResync: true, Error: null);
        }

        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        int upserted = await EnqueueAllAsync(source, delta.Upserted, context, sp, ct);
        int deleted = await DeleteByPathsAsync(source, delta.DeletedPaths, context, sp, ct);

        // Compare-and-swap: a cycle that started earlier but finished later must not
        // overwrite newer progress with its own stale cursor.
        bool advanced = await sourceStore.TryAdvanceSyncStateAsync(
            source.Id, expectedCursor: source.SyncCursor, newCursor: delta.NextCursor,
            SyncStatus.Succeeded, error: null, DateTime.UtcNow, ct);

        if (!advanced)
        {
            // Only the cursor write is dropped. The upserts and deletions above have already
            // been applied, so this is not a rollback — say so, or the next person reading
            // this line while chasing duplicate ingestion will rule out the wrong cause.
            logger.LogWarning(
                "Source {SourceId} was advanced by another cycle; keeping the newer cursor. "
                + "This cycle's {Upserted} upsert(s) and {Deleted} deletion(s) were already applied.",
                source.Id, upserted, deleted);
        }

        return new SourceSyncResult(upserted, deleted, UsedDeltaPath: true, RequiredResync: false, Error: null);
    }

    private async Task<SourceSyncResult> SyncViaListAndDiffAsync(
        Source source, IConnector connector, ISourceStore sourceStore, IServiceProvider sp, CancellationToken ct)
    {
        IReadOnlyList<ConnectorFile> remote = await connector.ListFilesAsync(null, ct);

        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var indexedPaths = await context.Documents
            .AsNoTracking()
            .Where(d => d.SourceId == source.Id)
            .Select(d => d.Path)
            .ToListAsync(ct);

        var remotePaths = remote.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var vanished = indexedPaths.Where(p => !remotePaths.Contains(p)).ToList();

        int upserted = await EnqueueAllAsync(source, remote, context, sp, ct);
        int deleted = await DeleteByPathsAsync(source, vanished, context, sp, ct);

        // No cursor to advance on this path, so record the outcome directly. The stored
        // cursor stays null, which is what marks this source as fallback-synced.
        await sourceStore.UpdateSyncStateAsync(
            source.Id, source.SyncCursor, SyncStatus.Succeeded, error: null, DateTime.UtcNow, ct);

        return new SourceSyncResult(upserted, deleted, UsedDeltaPath: false, RequiredResync: false, Error: null);
    }

    /// <summary>
    /// Loads the indexed documents for the given paths in one round trip per batch.
    /// <para>
    /// Querying per file instead would issue one SELECT per remote object every cycle — for a
    /// source holding ten thousand objects, ten thousand queries every five minutes even when
    /// nothing changed.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<string, DocumentEntity>> LoadByPathsAsync(
        KnowledgeDbContext context, Guid ownerId, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var byPath = new Dictionary<string, DocumentEntity>(StringComparer.Ordinal);
        if (paths.Count == 0) return byPath;

        // Batched: every path becomes a parameter, and one statement cannot carry more than
        // PostgreSQL's parameter ceiling.
        foreach (string[] batch in paths.Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            var rows = await context.Documents
                .AsNoTracking()
                .Where(d => d.OwnerId == ownerId && batch.Contains(d.Path))
                .ToListAsync(ct);

            foreach (var row in rows) byPath[row.Path] = row;
        }

        return byPath;
    }

    private async Task<int> EnqueueAllAsync(
        Source source, IReadOnlyList<ConnectorFile> files, KnowledgeDbContext context,
        IServiceProvider sp, CancellationToken ct)
    {
        if (files.Count == 0) return 0;

        var existingByPath = await LoadByPathsAsync(
            context, source.Id, files.Select(f => f.Path).ToList(), ct);

        int count = 0;
        int skipped = 0;

        foreach (var file in files)
        {
            existingByPath.TryGetValue(file.Path, out var existing);

            // Without this the fallback path re-ingests the entire remote on every cycle:
            // the content hash is computed after the download and parse, so it dedupes
            // nothing that costs money. Skipping here is what keeps a five-minute poll from
            // re-embedding every file a source holds.
            if (existing is not null && !HasRemoteChanged(existing, file))
            {
                skipped++;
                continue;
            }

            string documentId = existing?.Id.ToString() ?? Guid.NewGuid().ToString();
            string fileName = Path.GetFileName(file.Path);

            await queue.EnqueueAsync(new IngestionJob(
                JobId: Guid.NewGuid().ToString(),
                DocumentId: documentId,
                Path: file.Path,
                Options: new IngestionOptions(
                    DocumentId: documentId,
                    FileName: fileName,
                    ContentType: file.ContentType,
                    Path: file.Path,
                    Metadata: new Dictionary<string, string>
                    {
                        ["OriginalFileName"] = fileName,
                        ["Source"] = "SourceSync",
                        ["SyncedAt"] = DateTime.UtcNow.ToString("O"),
                        // The remote's own view of the file, recorded so the next cycle can
                        // tell "already indexed" from "changed since". Stored on the document
                        // rather than in memory so it survives a restart — the old watcher
                        // kept this in a dictionary and lost every change made while it
                        // was down.
                        [RemoteLastModifiedKey] = file.LastModified.ToString("O"),
                        [RemoteSizeKey] = file.SizeBytes.ToString(CultureInfo.InvariantCulture),
                    })
                {
                    // The whole reason ownership had to become explicit: a synced document
                    // belongs to the source, not to any container.
                    Owner = OwnerRef.ForSource(source.Id),
                }), ct);

            count++;
        }

        logger.LogInformation(
            "Enqueued {Count} file(s) for source {SourceId} ({Name}); {Skipped} unchanged",
            count, source.Id, Sanitize(source.Name), skipped);

        return count;
    }

    /// <summary>
    /// Decides whether an already-indexed document needs re-ingesting, by comparing the
    /// remote's size and modification time against the signature recorded last time.
    /// </summary>
    private static bool HasRemoteChanged(DocumentEntity existing, ConnectorFile file)
    {
        // Already queued or mid-ingestion. Enqueueing again would race the in-flight job for
        // the same document id, and the next cycle will catch it anyway once it settles.
        if (existing.Status is "Pending" or "Queued" or "Processing")
            return false;

        // A failed document is retried, regardless of the signature (#400). The signature is
        // written as ingestion metadata before the failure, so it matches — which meant the
        // comparison below skipped the document and it stayed Failed with zero chunks for ever,
        // or until the remote file happened to change.
        //
        // That made any transient downstream fault permanent: an embedding service that was
        // briefly unreachable poisoned every document caught in the window, and no amount of
        // re-syncing recovered them.
        //
        // The cost is a genuinely unparseable file being re-fetched every cycle. Bounded, and
        // much the lesser evil: it fails before embedding, which is the part that costs money.
        if (existing.Status is "Failed")
            return true;

        var metadata = existing.Metadata;
        string? lastModified = metadata?.GetValueOrDefault(RemoteLastModifiedKey);
        string? size = metadata?.GetValueOrDefault(RemoteSizeKey);

        // No signature: either indexed by the old watcher, or re-owned by the #350 backfill.
        // Treated as changed so it is re-ingested exactly once, which writes the signature and
        // makes it comparable from then on. The alternative — assuming unchanged — leaves a
        // document that can never be detected as stale, because the baseline it would be
        // compared against is never written.
        if (lastModified is null || size is null)
            return true;

        return lastModified != file.LastModified.ToString("O")
            || size != file.SizeBytes.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<int> DeleteByPathsAsync(
        Source source, IReadOnlyList<string> paths, KnowledgeDbContext context,
        IServiceProvider sp, CancellationToken ct)
    {
        if (paths.Count == 0) return 0;

        var documentStore = sp.GetRequiredService<IDocumentStore>();
        var existingByPath = await LoadByPathsAsync(context, source.Id, paths, ct);
        int count = 0;

        foreach (var path in paths)
        {
            if (!existingByPath.TryGetValue(path, out var doc)) continue;

            // Still routed through the store rather than the context: it also clears the
            // document's stored file and cascades its chunks.
            await documentStore.DeleteAsync(doc.Id.ToString(), ct);
            count++;
        }

        logger.LogInformation(
            "Removed {Count} document(s) no longer present in source {SourceId}", count, source.Id);

        return count;
    }
}
