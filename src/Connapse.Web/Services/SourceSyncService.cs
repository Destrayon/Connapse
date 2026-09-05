using System.Collections.Concurrent;
using System.Globalization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Pipeline;
using Connapse.Core.Utilities;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Web.Services;

/// <summary>
/// Keeps sources in step with their remote systems.
/// <para>
/// Replaces ConnectorWatcherService, which enumerated <c>containers</c> and filtered to
/// Filesystem/S3. The Phase 2 backfill (#350) moved every one of those rows into
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
    /// <param name="applyWithheldDeletions">
    /// Applies deletions an administrator has already approved. The vanished set is recomputed
    /// rather than replayed, so a source whose remote recovered in the meantime deletes
    /// nothing — but it is capped at the count that was withheld, so a remote that degraded
    /// further cannot have the larger set applied on the strength of the smaller approval.
    /// Inert when nothing was withheld: the flag cannot lift a guard that never tripped.
    /// </param>
    internal async Task<SourceSyncResult> SyncSourceAsync(
        Source source, Connection connection, CancellationToken ct, bool applyWithheldDeletions = false)
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
            // Fetched here rather than in SyncAllAsync because the sync-now endpoint calls
            // this method directly, and a credential that only the timer supplied would make
            // "sync now" behave differently from the poll.
            //
            // Skipped entirely unless the connection actually stores one: HasSecret is on the
            // read model, so the common providers cost no query and no decrypt per cycle. A
            // key ring that cannot decrypt throws, which the catch below records as a sync
            // failure — the right outcome, since retrying will not help.
            string? secret = connection.HasSecret
                ? await scope.ServiceProvider.GetRequiredService<IConnectionStore>()
                    .GetSecretAsync(connection.Id, ct)
                : null;

            connector = connectorFactory.Create(source, connection, secret);

            return connector is ISyncCursorConnector cursorConnector
                ? await SyncViaDeltaAsync(source, cursorConnector, sourceStore, scope.ServiceProvider, ct)
                : await SyncViaListAndDiffAsync(
                    source, connector, sourceStore, scope.ServiceProvider, ct, applyWithheldDeletions);
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
        Source source, IConnector connector, ISourceStore sourceStore, IServiceProvider sp, CancellationToken ct,
        bool applyWithheldDeletions = false)
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

        // Upserts apply regardless. A source that trips the guard must keep ingesting new
        // content, or the safety mechanism becomes the outage it exists to prevent.
        int upserted = await EnqueueAllAsync(source, remote, context, sp, ct);

        // An approval authorises the deletion set the administrator was shown a count of, not
        // whatever the next listing happens to produce. Recomputing stays — a remote that
        // recovered must still delete nothing — but the recomputed set may not *exceed* what
        // was approved. Without that ceiling the override is unbounded in precisely the
        // situation it is most likely to be used: a remote that is still degrading. Approve 40
        // and the cycle could apply 1,000.
        //
        // Bound by count rather than by identity. The administrator never sees which paths —
        // the page shows a number and a source is never browsable — so the consent being given
        // is "delete what is missing, about this many", and a count expresses that honestly.
        // Hashing the path set would invalidate approval on any ordinary churn, and would mean
        // storing path-derived data this design deliberately keeps out of the database.
        //
        // Null when nothing was withheld, so the flag cannot lift a guard that never tripped:
        // a first-ever sync carrying applyWithheldDeletions=true is still guarded.
        int? approved = applyWithheldDeletions ? source.WithheldDeletions : null;

        bool withhold = approved is null
            ? DeletionGuard.ShouldWithhold(vanished.Count, indexedPaths.Count)
            : vanished.Count > approved.Value;

        int deleted = 0;
        if (withhold)
        {
            if (approved is { } ceiling)
            {
                logger.LogWarning(
                    "Source {SourceId} approval superseded: {Approved} deletion(s) were approved but "
                    + "the listing now shows {Vanished} of {Indexed}; withholding pending fresh approval",
                    source.Id, ceiling, vanished.Count, indexedPaths.Count);
            }
            else
            {
                logger.LogWarning(
                    "Source {SourceId} reconcile would delete {Vanished} of {Indexed} document(s); "
                    + "withholding pending administrator approval",
                    source.Id, vanished.Count, indexedPaths.Count);
            }
        }
        else
        {
            deleted = await DeleteByPathsAsync(source, vanished, context, sp, ct);
        }

        await sourceStore.UpdateWithheldDeletionsAsync(
            source.Id, withhold ? vanished.Count : null, ct);

        // No cursor to advance on this path, so record the outcome directly. The stored
        // cursor stays null, which is what marks this source as fallback-synced.
        await sourceStore.UpdateSyncStateAsync(
            source.Id, source.SyncCursor, SyncStatus.Succeeded, error: null, DateTime.UtcNow, ct);

        return new SourceSyncResult(
            upserted, deleted, UsedDeltaPath: false, RequiredResync: false, Error: null,
            WithheldDeletions: withhold ? vanished.Count : 0);
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

        var due = new List<ConnectorFile>();
        var claims = new Dictionary<string, Guid>(StringComparer.Ordinal);

        // Counted separately from claims and due, because a coordinate update is neither. On a
        // source where nothing has changed it is the only pending write, and the save below is
        // conditional -- so without this the update is built and then silently discarded, which
        // is exactly the case that matters: a stable source is the one that would otherwise never
        // acquire coordinates at all.
        int located = 0;

        foreach (var file in files)
        {
            existingByPath.TryGetValue(file.Path, out var existing);

            // Before the skip below, deliberately. A source that has not changed still needs its
            // documents to learn where they came from, and the skip is the common case -- leaving
            // it until a file changes would mean a stable source never acquires coordinates at
            // all. One listing pass therefore populates a whole source.
            //
            // Rows arrive AsNoTracking, so a stub carries the single changed column rather than
            // the whole entity, and rides the SaveChangesAsync already at the end of this method.
            if (existing is not null &&
                file.ResourceUri is not null &&
                !string.Equals(existing.ResourceUri, file.ResourceUri, StringComparison.Ordinal))
            {
                var stub = new DocumentEntity { Id = existing.Id };
                context.Documents.Attach(stub);
                stub.ResourceUri = file.ResourceUri;
                located++;
            }

            // Without this the fallback path re-ingests the entire remote on every cycle:
            // the content hash is computed after the download and parse, so it dedupes
            // nothing that costs money. Skipping here is what keeps a five-minute poll from
            // re-embedding every file a source holds.
            if (existing is not null && !HasRemoteChanged(existing, file))
            {
                skipped++;
                continue;
            }

            due.Add(file);

            if (existing is { Status: "Failed" })
            {
                // Counted here rather than in the pipeline, because the pipeline may never run
                // — a connector that cannot reach its remote throws before it. That is exactly
                // the failure this bound exists to stop repeating, so the attempt has to be
                // recorded at the moment it is made.
                // Reset by a changed file, not merely incremented: the budget is per version.
                // Carrying a spent one across an edit would let the fall-through above hand the
                // new version a single attempt and then refuse it for ever.
                int attempt = SignatureChanged(existing, file) ? 1 : FailedAttempts(existing) + 1;

                var carried = new Dictionary<string, string>(existing.Metadata ?? [])
                {
                    [IngestionPipeline.SyncFailedAttemptsKey] =
                        attempt.ToString(CultureInfo.InvariantCulture),
                };

                var tracked = await context.Documents.FirstOrDefaultAsync(d => d.Id == existing.Id, ct);
                if (tracked is not null) tracked.Metadata = carried;
            }

            if (existing is null)
            {
                // A row staking this path, written before anything is enqueued. Until one
                // exists, "is this file already being worked on?" can only be answered from
                // persisted documents — and the pipeline does not write one until the job
                // actually runs. On a source whose ingestion outlasts the poll interval, every
                // cycle therefore rediscovered the whole backlog as new, minted fresh ids, and
                // enqueued it all again: the same files downloaded and embedded repeatedly,
                // and a Hangfire queue growing faster than it drained.
                //
                // Status "Pending" is what the next cycle reads: HasRemoteChanged skips it.
                claims[file.Path] = Guid.NewGuid();
                context.Documents.Add(new DocumentEntity
                {
                    Id = claims[file.Path],
                    SourceId = source.Id,
                    FileName = Path.GetFileName(file.Path),
                    ContentType = file.ContentType,
                    Path = file.Path,
                    ResourceUri = file.ResourceUri,
                    ContentHash = string.Empty,
                    SizeBytes = file.SizeBytes,
                    Status = "Pending",
                    CreatedAt = DateTime.UtcNow,

                    // Deliberately no remote signature yet. It is what "already indexed at this
                    // version" means, and writing it here would claim an ingestion that has not
                    // happened — so a job that then failed would leave a document the next
                    // cycle reads as up to date and never retries. The pipeline writes it once
                    // it has the file.
                    Metadata = [],
                });
            }
        }

        // One round trip, and before any enqueue: a claim that did not persist must not have a
        // job pointing at it, and a retry that was not counted would not be bounded.
        if (claims.Count > 0 || due.Count > 0 || located > 0)
            await context.SaveChangesAsync(ct);

        foreach (var file in due)
        {
            existingByPath.TryGetValue(file.Path, out var existing);

            string documentId = (existing?.Id ?? claims[file.Path]).ToString();
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
    /// <summary>
    /// How many times a failing document is re-enqueued before the sync engine leaves it alone.
    /// Hangfire retries each of those attempts three times itself, so this is not the whole
    /// budget — it is the number of fresh starts.
    /// </summary>
    private const int MaxFailedSyncAttempts = 3;

    private static int FailedAttempts(DocumentEntity existing) =>
        existing.Metadata is not null
        && existing.Metadata.TryGetValue(IngestionPipeline.SyncFailedAttemptsKey, out string? raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int attempts)
            ? attempts
            : 0;

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
        // Bounded, though (#404). "Retry regardless" assumed the failure was transient, and a
        // failure that is not — credentials that are wrong, a server that is gone — then
        // re-enqueued every file in the source on every cycle. Those jobs each carry Hangfire's
        // own three retries, so the ingestion queue fills with work that cannot succeed and
        // documents that would have ingested fine sit behind it. A source cannot be allowed to
        // deny service to the rest of the instance by failing.
        //
        // Note the fall-through once the attempts are spent, rather than a flat refusal: an
        // exhausted document is still re-ingested when the file itself changes. Someone who
        // fixes the file upstream should not have to wait out a budget, or clear one by hand.
        if (existing.Status is "Failed" && FailedAttempts(existing) < MaxFailedSyncAttempts)
            return true;

        return SignatureChanged(existing, file);
    }

    /// <summary>
    /// True when the remote's own view of the file differs from the one recorded at the last
    /// ingestion — or when there is nothing recorded to compare against.
    /// </summary>
    private static bool SignatureChanged(DocumentEntity existing, ConnectorFile file)
    {
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
