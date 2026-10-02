using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Pipeline;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Ingestion.Reindex;

/// <summary>
/// Service for reindexing documents in the knowledge base.
/// Compares content hashes and settings to determine which documents need reprocessing.
/// </summary>
public class ReindexService : IReindexService
{
    private readonly KnowledgeDbContext _context;
    private readonly IKnowledgeFileSystem _fileSystem;
    private readonly IManagedStorageProvider _managedStorage;
    private readonly IContainerStore _containerStore;
    private readonly IIngestionQueue _queue;
    private readonly IOptionsMonitor<ChunkingSettings> _chunkingSettings;
    private readonly IOptionsMonitor<EmbeddingSettings> _embeddingSettings;
    private readonly ILogger<ReindexService> _logger;
    private readonly IReadOnlyList<IDocumentParser> _parsers;

    public ReindexService(
        KnowledgeDbContext context,
        IKnowledgeFileSystem fileSystem,
        IManagedStorageProvider managedStorage,
        IContainerStore containerStore,
        IIngestionQueue queue,
        IOptionsMonitor<ChunkingSettings> chunkingSettings,
        IOptionsMonitor<EmbeddingSettings> embeddingSettings,
        ILogger<ReindexService> logger,
        IEnumerable<IDocumentParser>? parsers = null)
    {
        _parsers = parsers?.ToList() ?? [];
        _context = context;
        _fileSystem = fileSystem;
        _managedStorage = managedStorage;
        _containerStore = containerStore;
        _queue = queue;
        _chunkingSettings = chunkingSettings;
        _embeddingSettings = embeddingSettings;
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the file backing this document exists, using the container's connector when available.
    /// </summary>
    private async Task<bool> FileExistsAsync(DocumentEntity doc, CancellationToken ct)
    {
        // Source-owned documents have no container; they fall through to the legacy
        // file system path until the sync engine gives sources their own connector.
        var container = doc.ContainerId.HasValue
            ? await _containerStore.GetAsync(doc.ContainerId.Value, ct)
            : null;
        if (container is not null)
        {
            try
            {
                return await _managedStorage.CreateConnector(container.Id).ExistsAsync(doc.Path, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Connector ExistsAsync failed for container {ContainerId}; falling back to legacy file system",
                    doc.ContainerId);
            }
        }

        return await _fileSystem.ExistsAsync(doc.Path, ct);
    }

    /// <summary>
    /// Opens the file backing this document, using the container's connector when available.
    /// </summary>
    private async Task<Stream> OpenFileAsync(DocumentEntity doc, CancellationToken ct)
    {
        // Source-owned documents have no container; they fall through to the legacy
        // file system path until the sync engine gives sources their own connector.
        var container = doc.ContainerId.HasValue
            ? await _containerStore.GetAsync(doc.ContainerId.Value, ct)
            : null;
        if (container is not null)
        {
            try
            {
                return await _managedStorage.CreateConnector(container.Id).ReadFileAsync(doc.Path, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Connector ReadFileAsync failed for container {ContainerId}; falling back to legacy file system",
                    doc.ContainerId);
            }
        }

        return await _fileSystem.OpenFileAsync(doc.Path, ct);
    }

    public async Task<ReindexResult> ReindexAsync(ReindexOptions options, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Starting reindex operation: ContainerId={ContainerId}, Force={Force}, DetectSettingsChanges={DetectSettingsChanges}",
            Sanitize(options.ContainerId?.ToString()),
            options.Force,
            options.DetectSettingsChanges);

        var batchId = Guid.NewGuid().ToString();
        var documents = await GetDocumentsToEvaluateAsync(options, ct);

        if (documents.Count == 0)
        {
            _logger.LogInformation("No documents found to evaluate for reindex");
            return new ReindexResult
            {
                BatchId = batchId,
                TotalDocuments = 0,
                EnqueuedCount = 0,
                SkippedCount = 0,
                FailedCount = 0
            };
        }

        var results = new List<ReindexDocumentResult>();
        var reasonCounts = new Dictionary<ReindexReason, int>();

        var run = new ReindexRun(options.DryRun, options.MaxDocuments);
        foreach (var doc in documents)
        {
            var result = await EvaluateAndEnqueueDocumentAsync(doc, options, batchId, run, ct);
            results.Add(result);

            // Track reason counts
            if (!reasonCounts.TryGetValue(result.Reason, out var count))
                count = 0;
            reasonCounts[result.Reason] = count + 1;
        }

        var summary = new ReindexResult
        {
            BatchId = batchId,
            TotalDocuments = results.Count,
            EnqueuedCount = results.Count(r => r.Action == ReindexAction.Enqueued),
            SkippedCount = results.Count(r => r.Action == ReindexAction.Skipped),
            FailedCount = results.Count(r => r.Action == ReindexAction.Failed),
            PlannedCount = results.Count(r => r.Action == ReindexAction.Planned),
            DeferredCount = results.Count(r => r.Action == ReindexAction.Deferred),
            DryRun = options.DryRun,
            ReasonCounts = reasonCounts,
            Documents = results
        };

        _logger.LogInformation(
            "Reindex operation completed: Total={Total}, Enqueued={Enqueued}, Skipped={Skipped}, Failed={Failed}",
            summary.TotalDocuments,
            summary.EnqueuedCount,
            summary.SkippedCount,
            summary.FailedCount);

        return summary;
    }

    public async Task<ReindexCheck> CheckDocumentAsync(string documentId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(documentId, out var guid))
        {
            return new ReindexCheck(
                documentId,
                NeedsReindex: false,
                Reason: ReindexReason.Error,
                CurrentHash: null,
                StoredHash: null);
        }

        var doc = await _context.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == guid, ct);

        if (doc == null)
        {
            return new ReindexCheck(
                documentId,
                NeedsReindex: false,
                Reason: ReindexReason.Error,
                CurrentHash: null,
                StoredHash: null);
        }

        // Source-owned documents: see EvaluateAndEnqueueDocumentAsync. Content changes are the sync
        // engine's to detect, so only settings and the parser are compared.
        string? currentHash = null;
        if (doc.SourceId is null)
        {
            // Check if file exists (via connector for connector-backed containers)
            if (!await FileExistsAsync(doc, ct))
            {
                return new ReindexCheck(
                    documentId,
                    NeedsReindex: false,
                    Reason: ReindexReason.FileNotFound,
                    CurrentHash: null,
                    StoredHash: doc.ContentHash);
            }

            // Compute current content hash (via connector for connector-backed containers)
            try
            {
                using var stream = await OpenFileAsync(doc, ct);
                currentHash = await ComputeContentHashAsync(stream, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute hash for document {DocumentId}", Sanitize(documentId));
                return new ReindexCheck(
                    documentId,
                    NeedsReindex: false,
                    Reason: ReindexReason.Error,
                    CurrentHash: null,
                    StoredHash: doc.ContentHash);
            }

            // Check content hash
            if (!string.Equals(currentHash, doc.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return new ReindexCheck(
                    documentId,
                    NeedsReindex: true,
                    Reason: ReindexReason.ContentChanged,
                    CurrentHash: currentHash,
                    StoredHash: doc.ContentHash);
            }
        }

        // Check chunking settings
        var chunkingCheck = CheckChunkingSettingsChanged(doc);
        if (chunkingCheck.changed)
        {
            return new ReindexCheck(
                documentId,
                NeedsReindex: true,
                Reason: ReindexReason.ChunkingSettingsChanged,
                CurrentHash: currentHash,
                StoredHash: doc.ContentHash,
                CurrentChunkingStrategy: chunkingCheck.current,
                StoredChunkingStrategy: chunkingCheck.stored);
        }

        // Check embedding settings
        var embeddingCheck = CheckEmbeddingSettingsChanged(doc);
        if (embeddingCheck.changed)
        {
            return new ReindexCheck(
                documentId,
                NeedsReindex: true,
                Reason: ReindexReason.EmbeddingSettingsChanged,
                CurrentHash: currentHash,
                StoredHash: doc.ContentHash,
                CurrentEmbeddingModel: embeddingCheck.current,
                StoredEmbeddingModel: embeddingCheck.stored);
        }

        if (CheckParserChanged(doc).changed)
        {
            return new ReindexCheck(
                documentId,
                NeedsReindex: true,
                Reason: ReindexReason.ParserChanged,
                CurrentHash: currentHash,
                StoredHash: doc.ContentHash);
        }

        if (doc.Metadata.ContainsKey(Pipeline.IngestionPipeline.MetadataKeyExtractionIncomplete))
        {
            return new ReindexCheck(
                documentId,
                NeedsReindex: true,
                Reason: ReindexReason.ExtractionIncomplete,
                CurrentHash: currentHash,
                StoredHash: doc.ContentHash);
        }

        // Check if never indexed
        if (!doc.LastIndexedAt.HasValue || doc.IngestionStatus != DocumentStatus.Ready)
        {
            return new ReindexCheck(
                documentId,
                NeedsReindex: true,
                Reason: ReindexReason.NeverIndexed,
                CurrentHash: currentHash,
                StoredHash: doc.ContentHash);
        }

        return new ReindexCheck(
            documentId,
            NeedsReindex: false,
            Reason: ReindexReason.Unchanged,
            CurrentHash: currentHash,
            StoredHash: doc.ContentHash);
    }

    private async Task<List<DocumentEntity>> GetDocumentsToEvaluateAsync(
        ReindexOptions options,
        CancellationToken ct)
    {
        var query = _context.Documents.AsNoTracking().AsQueryable();

        // Filter by container if specified
        if (!string.IsNullOrEmpty(options.ContainerId) && Guid.TryParse(options.ContainerId, out var containerGuid))
        {
            query = query.Where(d => d.ContainerId == containerGuid);
        }

        // Filter by specific document IDs if specified
        if (options.DocumentIds != null && options.DocumentIds.Count > 0)
        {
            var guids = options.DocumentIds
                .Where(id => Guid.TryParse(id, out _))
                .Select(Guid.Parse)
                .ToList();

            query = query.Where(d => guids.Contains(d.Id));
        }

        return await query.ToListAsync(ct);
    }

    private async Task<ReindexDocumentResult> EvaluateAndEnqueueDocumentAsync(
        DocumentEntity doc,
        ReindexOptions options,
        string batchId,
        ReindexRun run,
        CancellationToken ct)
    {
        try
        {
            // If force mode, always enqueue
            if (options.Force)
            {
                return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.Forced, ct, run: run);
            }

            // A document still queued or being ingested is mid-reindex already. Without this a
            // capped rollout's next run re-queued its own previous batch -- not Ready yet, so it
            // read as never indexed -- and the deferred documents never got their turn.
            if (doc.IngestionStatus.IsInFlight())
            {
                return new ReindexDocumentResult(
                    doc.Id.ToString(), doc.FileName, ReindexAction.Skipped, ReindexReason.AlreadyQueued);
            }

            // Source-owned documents are read through their source's connector, which this service
            // does not hold; the legacy file system never has them, so every one used to come back
            // FileNotFound and no settings or parser change could ever reach it. Their content changes
            // are the sync engine's to detect, by remote signature, so here only the recorded
            // settings and parser are compared, and the pipeline reads the file as usual.
            if (doc.SourceId is null)
            {
                // Check if file exists (via connector for connector-backed containers)
                if (!await FileExistsAsync(doc, ct))
                {
                    _logger.LogWarning(
                        "Document {DocumentId} file not found at {Path}",
                        doc.Id,
                        doc.Path);

                    return new ReindexDocumentResult(
                        doc.Id.ToString(),
                        doc.FileName,
                        ReindexAction.Skipped,
                        ReindexReason.FileNotFound);
                }

                // Compute current content hash (via connector for connector-backed containers)
                string currentHash;
                try
                {
                    using var stream = await OpenFileAsync(doc, ct);
                    currentHash = await ComputeContentHashAsync(stream, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to compute hash for document {DocumentId}",
                        doc.Id);

                    return new ReindexDocumentResult(
                        doc.Id.ToString(),
                        doc.FileName,
                        ReindexAction.Failed,
                        ReindexReason.Error,
                        ErrorMessage: $"Hash computation failed: {ex.Message}");
                }

                // Check if content hash changed
                if (!string.Equals(currentHash, doc.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Document {DocumentId} content hash changed (stored={StoredHash}, current={CurrentHash})",
                        doc.Id,
                        doc.ContentHash?[..Math.Min(8, doc.ContentHash?.Length ?? 0)],
                        currentHash[..Math.Min(8, currentHash.Length)]);

                    return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.ContentChanged, ct, run: run);
                }
            }

            // Check settings changes if enabled
            if (options.DetectSettingsChanges)
            {
                // Check chunking settings
                var chunkingCheck = CheckChunkingSettingsChanged(doc);
                if (chunkingCheck.changed)
                {
                    _logger.LogInformation(
                        "Document {DocumentId} chunking settings changed (stored={Stored}, current={Current})",
                        doc.Id,
                        chunkingCheck.stored,
                        chunkingCheck.current);

                    return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.ChunkingSettingsChanged, ct, run: run);
                }

                // Check embedding settings
                var embeddingCheck = CheckEmbeddingSettingsChanged(doc);
                if (embeddingCheck.changed)
                {
                    _logger.LogInformation(
                        "Document {DocumentId} embedding settings changed (stored={Stored}, current={Current})",
                        doc.Id,
                        embeddingCheck.stored,
                        embeddingCheck.current);

                    return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.EmbeddingSettingsChanged, ct, run: run);
                }

                // A parser that now reads the same bytes differently: the content hash cannot
                // see it, so text garbled by an older parser would otherwise stay garbled.
                var parserCheck = CheckParserChanged(doc);
                if (parserCheck.changed)
                {
                    _logger.LogInformation(
                        "Document {DocumentId} was parsed by {Stored}; the current parser is {Current}",
                        doc.Id,
                        parserCheck.stored,
                        parserCheck.current);

                    return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.ParserChanged, ct, run: run);
                }

                if (doc.Metadata.ContainsKey(Pipeline.IngestionPipeline.MetadataKeyExtractionIncomplete))
                {
                    _logger.LogInformation("Document {DocumentId} has pages that failed to extract; retrying", doc.Id);
                    return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.ExtractionIncomplete, ct, run: run);
                }
            }

            // Check if never indexed successfully
            if (!doc.LastIndexedAt.HasValue || doc.IngestionStatus != DocumentStatus.Ready)
            {
                _logger.LogInformation(
                    "Document {DocumentId} was never successfully indexed (Status={Status})",
                    doc.Id,
                    doc.IngestionStatus);

                return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.NeverIndexed, ct, run: run);
            }

            // No reindex needed
            return new ReindexDocumentResult(
                doc.Id.ToString(),
                doc.FileName,
                ReindexAction.Skipped,
                ReindexReason.Unchanged);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error evaluating document {DocumentId} for reindex",
                doc.Id);

            return new ReindexDocumentResult(
                doc.Id.ToString(),
                doc.FileName,
                ReindexAction.Failed,
                ReindexReason.Error,
                ErrorMessage: ex.Message);
        }
    }

    public async Task<int> RetryFailedAsync(Guid ownerId, CancellationToken ct = default)
    {
        var failed = await _context.Documents
            .AsNoTracking()
            .Where(d => d.OwnerId == ownerId && (d.IngestionStatus == DocumentStatus.FailedRetryable || d.IngestionStatus == DocumentStatus.FailedPermanent))
            .Select(d => d.Id)
            .ToListAsync(ct);

        return await RequeueAsync(failed, resetAttempts: true, ct);
    }

    public async Task<int> RequeueAsync(
        IReadOnlyCollection<Guid> documentIds, bool resetAttempts = false, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return 0;

        var docs = await _context.Documents
            .AsNoTracking()
            .Where(d => documentIds.Contains(d.Id))
            .ToListAsync(ct);

        string batchId = Guid.NewGuid().ToString();
        int enqueued = 0;
        foreach (var doc in docs)
        {
            var result = await EnqueueDocumentAsync(
                doc, batchId, new ReindexOptions(), ReindexReason.Forced, ct, resetAttempts);
            if (result.Action == ReindexAction.Enqueued) enqueued++;
        }

        return enqueued;
    }

    private async Task<ReindexDocumentResult> EnqueueDocumentAsync(
        DocumentEntity doc,
        string batchId,
        ReindexOptions options,
        ReindexReason reason,
        CancellationToken ct,
        bool resetAttempts = true,
        ReindexRun? run = null)
    {
        // The one point every reason reaches before a job is enqueued, so a dry run and the cap
        // apply to forced, content, settings and parser reindexes alike.
        if (run is not null)
        {
            if (run.DryRun)
                return new ReindexDocumentResult(doc.Id.ToString(), doc.FileName, ReindexAction.Planned, reason);
            if (run.MaxDocuments is int max && run.Enqueued >= max)
                return new ReindexDocumentResult(doc.Id.ToString(), doc.FileName, ReindexAction.Deferred, reason);
        }

        // The chunks stay where they are. IngestionPipeline purges them itself on a reindex,
        // once the file has been read and the row updated — which is the only moment at which
        // dropping them is safe. Deleting here instead meant every way the work could fail
        // afterwards — the enqueue throwing, Hangfire being down, the SFTP server refusing the
        // connection — left a document with no chunks and no job coming to rebuild them. The
        // document stayed searchable-looking and returned nothing.
        //
        // ChunkCount is left alone for the same reason: it describes chunks that still exist.

        // Determine chunking strategy. A record keeps the strategy its shape was given.
        doc.Metadata.TryGetValue(Pipeline.IngestionPipeline.MetadataKeyChunkingStrategy, out var indexedWith);
        var strategy = options.Strategy ?? Enum.Parse<ChunkingStrategy>(
            IngestionPipelineStrategyResolver.IsContentPinned(indexedWith)
                ? indexedWith!
                : _chunkingSettings.CurrentValue.Strategy,
            ignoreCase: true);

        // The owner the row actually has, read from the row. Nullable<Guid>.ToString() yields
        // "" rather than throwing, so a source-owned document used to be enqueued with a blank
        // ContainerId and no Owner at all — and the pipeline, seeing neither, routed it down
        // the container branch and threw. By then the chunks above were already deleted, and
        // the next sync saw an unchanged remote signature and did not restore them, so a
        // forced reindex quietly removed source documents from search for good.
        var owner = doc.SourceId is Guid sourceId
            ? OwnerRef.ForSource(sourceId)
            : doc.ContainerId is Guid containerId
                ? OwnerRef.ForContainer(containerId)
                : throw new InvalidOperationException(
                    $"Document {doc.Id} has neither a container nor a source and cannot be reindexed.");

        // Enqueuing marks the document Queued. The chunks stay where they are until the new
        // version commits, so the document stays searchable throughout.
        string? jobId = await _queue.EnqueueAsync(new IngestionJob(
            DocumentId: doc.Id.ToString(),
            Options: new IngestionOptions(
                DocumentId: doc.Id.ToString(),
                FileName: doc.FileName,
                ContentType: doc.ContentType,
                ContainerId: owner.ContainerId?.ToString(),
                Path: doc.Path,
                Strategy: strategy,
                Metadata: doc.Metadata)
            {
                Owner = owner,
            },
            BatchId: batchId,
            ResetAttempts: resetAttempts), ct);

        if (run is not null)
            run.Enqueued++;

        _logger.LogInformation(
            "Enqueued document {DocumentId} ({FileName}) for reindex, reason: {Reason}",
            doc.Id,
            doc.FileName,
            reason);

        return new ReindexDocumentResult(
            doc.Id.ToString(),
            doc.FileName,
            ReindexAction.Enqueued,
            reason,
            JobId: jobId);
    }

    private (bool changed, string? stored, string? current) CheckChunkingSettingsChanged(DocumentEntity doc)
    {
        var currentSettings = _chunkingSettings.CurrentValue;

        // Get stored chunking info from metadata
        doc.Metadata.TryGetValue(Pipeline.IngestionPipeline.MetadataKeyChunkingStrategy, out var storedStrategy);
        doc.Metadata.TryGetValue(Pipeline.IngestionPipeline.MetadataKeyChunkingMaxSize, out var storedMaxSize);
        doc.Metadata.TryGetValue(Pipeline.IngestionPipeline.MetadataKeyChunkingOverlap, out var storedOverlap);

        // If no stored metadata, can't compare (might be pre-metadata document)
        if (string.IsNullOrEmpty(storedStrategy))
        {
            return (false, null, null);
        }

        // Resolve the current strategy through the same auto-router used by IngestionPipeline
        // when it writes IndexedWith:ChunkingStrategy. Without this, a .md file ingested with
        // a configured Strategy != DocumentAware stores "DocumentAware" (resolved) but the
        // raw "Recursive" (or other) currentSettings.Strategy here would always mismatch,
        // causing a permanent reindex loop.
        //
        // A content-pinned strategy (a record) is compared against itself, so changing the
        // configured strategy does not mark every record stale and re-chunk it the wrong way.
        string resolvedStrategy = IngestionPipelineStrategyResolver.Resolve(
            fallbackStrategy: IngestionPipelineStrategyResolver.IsContentPinned(storedStrategy)
                ? storedStrategy
                : currentSettings.Strategy,
            fileName: doc.FileName);

        var currentKey = $"{resolvedStrategy}:{currentSettings.MaxChunkSize}:{currentSettings.Overlap}";
        var storedKey = $"{storedStrategy}:{storedMaxSize}:{storedOverlap}";

        return (!string.Equals(currentKey, storedKey, StringComparison.OrdinalIgnoreCase),
            storedKey,
            currentKey);
    }

    /// <summary>
    /// True when the parser that would read this document now is a different parser, or a newer
    /// version, than the one that produced its chunks.
    /// </summary>
    private (bool changed, string? stored, string? current) CheckParserChanged(DocumentEntity doc)
    {
        string extension = Path.GetExtension(doc.FileName).ToLowerInvariant();
        IDocumentParser? parser = _parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));
        if (parser is null)
            return (false, null, null);

        string? storedName = Pipeline.IngestionPipeline.StoredParserName(doc.Metadata, extension);
        int storedVersion = Pipeline.IngestionPipeline.StoredParserVersion(doc.Metadata);

        // Unknown only for an extension no parser handled when versions were introduced; nothing
        // indexed it then, so whatever handles it now is the parser that produced it.
        bool sameParser = storedName is null || storedName == parser.Name;
        bool changed = !sameParser || storedVersion < parser.Version;

        return (changed,
            $"{storedName ?? parser.Name} v{storedVersion}",
            $"{parser.Name} v{parser.Version}");
    }

    private (bool changed, string? stored, string? current) CheckEmbeddingSettingsChanged(DocumentEntity doc)
    {
        var currentSettings = _embeddingSettings.CurrentValue;

        // Get stored embedding info from metadata
        doc.Metadata.TryGetValue(Pipeline.IngestionPipeline.MetadataKeyEmbeddingProvider, out var storedProvider);
        doc.Metadata.TryGetValue(Pipeline.IngestionPipeline.MetadataKeyEmbeddingModel, out var storedModel);

        // If no stored metadata, can't compare (might be pre-metadata document)
        if (string.IsNullOrEmpty(storedModel))
        {
            return (false, null, null);
        }

        var currentKey = $"{currentSettings.Provider}:{EmbeddingIdentity.For(currentSettings)}";
        var storedKey = $"{storedProvider}:{storedModel}";

        return (!string.Equals(currentKey, storedKey, StringComparison.OrdinalIgnoreCase),
            storedKey,
            currentKey);
    }

    private static async Task<string> ComputeContentHashAsync(Stream content, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var hashBytes = await sha256.ComputeHashAsync(content, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>One reindex run's dry-run flag, cap, and how many it has enqueued so far.</summary>
    private sealed class ReindexRun(bool dryRun, int? maxDocuments)
    {
        public bool DryRun { get; } = dryRun;
        public int? MaxDocuments { get; } = maxDocuments;
        public int Enqueued { get; set; }
    }
}
