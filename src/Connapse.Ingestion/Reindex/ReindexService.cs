using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace Connapse.Ingestion.Reindex;

/// <summary>
/// Service for reindexing documents in the knowledge base.
/// Compares content hashes and settings to determine which documents need reprocessing.
/// </summary>
public class ReindexService : IReindexService
{
    private readonly KnowledgeDbContext _context;
    private readonly IKnowledgeFileSystem _fileSystem;
    private readonly IIngestionQueue _queue;
    private readonly IOptionsMonitor<ChunkingSettings> _chunkingSettings;
    private readonly IOptionsMonitor<EmbeddingSettings> _embeddingSettings;
    private readonly ILogger<ReindexService> _logger;

    public ReindexService(
        KnowledgeDbContext context,
        IKnowledgeFileSystem fileSystem,
        IIngestionQueue queue,
        IOptionsMonitor<ChunkingSettings> chunkingSettings,
        IOptionsMonitor<EmbeddingSettings> embeddingSettings,
        ILogger<ReindexService> logger)
    {
        _context = context;
        _fileSystem = fileSystem;
        _queue = queue;
        _chunkingSettings = chunkingSettings;
        _embeddingSettings = embeddingSettings;
        _logger = logger;
    }

    public async Task<ReindexResult> ReindexAsync(ReindexOptions options, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Starting reindex operation: CollectionId={CollectionId}, Force={Force}, DetectSettingsChanges={DetectSettingsChanges}",
            options.CollectionId,
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

        foreach (var doc in documents)
        {
            var result = await EvaluateAndEnqueueDocumentAsync(doc, options, batchId, ct);
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

        // Check if file exists
        if (!await _fileSystem.ExistsAsync(doc.VirtualPath, ct))
        {
            return new ReindexCheck(
                documentId,
                NeedsReindex: false,
                Reason: ReindexReason.FileNotFound,
                CurrentHash: null,
                StoredHash: doc.ContentHash);
        }

        // Compute current content hash
        string currentHash;
        try
        {
            using var stream = await _fileSystem.OpenFileAsync(doc.VirtualPath, ct);
            currentHash = await ComputeContentHashAsync(stream, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute hash for document {DocumentId}", documentId);
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

        // Check if never indexed
        if (!doc.LastIndexedAt.HasValue || doc.Status != "Ready")
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

        // Filter by collection if specified
        if (!string.IsNullOrEmpty(options.CollectionId))
        {
            query = query.Where(d => d.CollectionId == options.CollectionId);
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
        CancellationToken ct)
    {
        try
        {
            // If force mode, always enqueue
            if (options.Force)
            {
                return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.Forced, ct);
            }

            // Check if file exists
            if (!await _fileSystem.ExistsAsync(doc.VirtualPath, ct))
            {
                _logger.LogWarning(
                    "Document {DocumentId} file not found at {VirtualPath}",
                    doc.Id,
                    doc.VirtualPath);

                return new ReindexDocumentResult(
                    doc.Id.ToString(),
                    doc.FileName,
                    ReindexAction.Skipped,
                    ReindexReason.FileNotFound);
            }

            // Compute current content hash
            string currentHash;
            try
            {
                using var stream = await _fileSystem.OpenFileAsync(doc.VirtualPath, ct);
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

                return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.ContentChanged, ct);
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

                    return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.ChunkingSettingsChanged, ct);
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

                    return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.EmbeddingSettingsChanged, ct);
                }
            }

            // Check if never indexed successfully
            if (!doc.LastIndexedAt.HasValue || doc.Status != "Ready")
            {
                _logger.LogInformation(
                    "Document {DocumentId} was never successfully indexed (Status={Status})",
                    doc.Id,
                    doc.Status);

                return await EnqueueDocumentAsync(doc, batchId, options, ReindexReason.NeverIndexed, ct);
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

    private async Task<ReindexDocumentResult> EnqueueDocumentAsync(
        DocumentEntity doc,
        string batchId,
        ReindexOptions options,
        ReindexReason reason,
        CancellationToken ct)
    {
        // Delete existing chunks and vectors before reindex
        // The cascade delete will handle chunk_vectors
        var existingChunks = await _context.Chunks
            .Where(c => c.DocumentId == doc.Id)
            .ToListAsync(ct);

        if (existingChunks.Count > 0)
        {
            _context.Chunks.RemoveRange(existingChunks);
            await _context.SaveChangesAsync(ct);

            _logger.LogDebug(
                "Removed {ChunkCount} existing chunks for document {DocumentId}",
                existingChunks.Count,
                doc.Id);
        }

        // Update document status
        var docEntity = await _context.Documents.FindAsync([doc.Id], ct);
        if (docEntity != null)
        {
            docEntity.Status = "Pending";
            docEntity.ChunkCount = 0;
            await _context.SaveChangesAsync(ct);
        }

        // Determine chunking strategy
        var strategy = options.Strategy ?? Enum.Parse<ChunkingStrategy>(
            _chunkingSettings.CurrentValue.Strategy,
            ignoreCase: true);

        // Create and enqueue ingestion job
        var job = new IngestionJob(
            JobId: Guid.NewGuid().ToString(),
            DocumentId: doc.Id.ToString(),
            VirtualPath: doc.VirtualPath,
            Options: new IngestionOptions(
                DocumentId: doc.Id.ToString(),
                FileName: doc.FileName,
                ContentType: doc.ContentType,
                CollectionId: doc.CollectionId,
                Strategy: strategy,
                Metadata: doc.Metadata),
            BatchId: batchId);

        await _queue.EnqueueAsync(job, ct);

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
            JobId: job.JobId);
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

        var currentKey = $"{currentSettings.Strategy}:{currentSettings.MaxChunkSize}:{currentSettings.Overlap}";
        var storedKey = $"{storedStrategy}:{storedMaxSize}:{storedOverlap}";

        return (!string.Equals(currentKey, storedKey, StringComparison.OrdinalIgnoreCase),
            storedKey,
            currentKey);
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

        var currentKey = $"{currentSettings.Provider}:{currentSettings.Model}";
        var storedKey = $"{storedProvider}:{storedModel}";

        return (!string.Equals(currentKey, storedKey, StringComparison.OrdinalIgnoreCase),
            storedKey,
            currentKey);
    }

    private static async Task<string> ComputeContentHashAsync(Stream content, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        content.Position = 0;
        var hashBytes = await sha256.ComputeHashAsync(content, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
