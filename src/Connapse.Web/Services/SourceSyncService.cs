using System.Globalization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
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

        await using var scope = scopeFactory.CreateAsyncScope();
        var sourceStore = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        try
        {
            IConnector connector = connectorFactory.Create(source, connection);

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

        int upserted = await EnqueueAllAsync(source, delta.Upserted, sp, ct);
        int deleted = await DeleteByPathsAsync(source, delta.DeletedPaths, sp, ct);

        // Compare-and-swap: a cycle that started earlier but finished later must not
        // overwrite newer progress with its own stale cursor.
        bool advanced = await sourceStore.TryAdvanceSyncStateAsync(
            source.Id, expectedCursor: source.SyncCursor, newCursor: delta.NextCursor,
            SyncStatus.Succeeded, error: null, DateTime.UtcNow, ct);

        if (!advanced)
        {
            logger.LogWarning(
                "Source {SourceId} was advanced by another cycle; discarding this result", source.Id);
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

        int upserted = await EnqueueAllAsync(source, remote, sp, ct);
        int deleted = await DeleteByPathsAsync(source, vanished, sp, ct);

        // No cursor to advance on this path, so record the outcome directly. The stored
        // cursor stays null, which is what marks this source as fallback-synced.
        await sourceStore.UpdateSyncStateAsync(
            source.Id, source.SyncCursor, SyncStatus.Succeeded, error: null, DateTime.UtcNow, ct);

        return new SourceSyncResult(upserted, deleted, UsedDeltaPath: false, RequiredResync: false, Error: null);
    }

    private async Task<int> EnqueueAllAsync(
        Source source, IReadOnlyList<ConnectorFile> files, IServiceProvider sp, CancellationToken ct)
    {
        if (files.Count == 0) return 0;

        var documentStore = sp.GetRequiredService<IDocumentStore>();
        int count = 0;
        int skipped = 0;

        foreach (var file in files)
        {
            var existing = await documentStore.GetByPathAsync(source.Id, file.Path, ct);

            // Without this the fallback path re-ingests the entire remote on every cycle:
            // the content hash is computed after the download and parse, so it dedupes
            // nothing that costs money. Skipping here is what keeps a five-minute poll from
            // re-embedding every file a source holds.
            if (existing is not null && !HasRemoteChanged(existing, file))
            {
                skipped++;
                continue;
            }

            string documentId = existing?.Id ?? Guid.NewGuid().ToString();
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
    private static bool HasRemoteChanged(Document existing, ConnectorFile file)
    {
        // Already queued or mid-ingestion. Enqueueing again would race the in-flight job for
        // the same document id, and the next cycle will catch it anyway once it settles.
        string? status = existing.Metadata.GetValueOrDefault("Status");
        if (status is "Pending" or "Queued" or "Processing")
            return false;

        string? lastModified = existing.Metadata.GetValueOrDefault(RemoteLastModifiedKey);
        string? size = existing.Metadata.GetValueOrDefault(RemoteSizeKey);

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
        Source source, IReadOnlyList<string> paths, IServiceProvider sp, CancellationToken ct)
    {
        if (paths.Count == 0) return 0;

        var documentStore = sp.GetRequiredService<IDocumentStore>();
        int count = 0;

        foreach (var path in paths)
        {
            var doc = await documentStore.GetByPathAsync(source.Id, path, ct);
            if (doc is null) continue;

            await documentStore.DeleteAsync(doc.Id, ct);
            count++;
        }

        logger.LogInformation(
            "Removed {Count} document(s) no longer present in source {SourceId}", count, source.Id);

        return count;
    }
}
