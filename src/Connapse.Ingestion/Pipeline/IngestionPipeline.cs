using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Connapse.Ingestion.Pipeline;

/// <summary>
/// Orchestrates the complete document ingestion pipeline:
/// Parse → Chunk → Embed → Store
/// </summary>
public class IngestionPipeline : IKnowledgeIngester
{
    private readonly KnowledgeDbContext _context;
    private readonly IKnowledgeFileSystem _fileSystem;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly IEnumerable<IChunkingStrategy> _chunkingStrategies;
    private readonly IOptionsMonitor<ChunkingSettings> _chunkingSettings;
    private readonly IOptionsMonitor<EmbeddingSettings> _embeddingSettings;
    private readonly EmbeddingCache _embeddingCache;
    private readonly ILogger<IngestionPipeline> _logger;

    // Metadata keys for tracking indexing settings
    public const string MetadataKeyChunkingStrategy = "IndexedWith:ChunkingStrategy";
    public const string MetadataKeyChunkingMaxSize = "IndexedWith:ChunkingMaxSize";
    public const string MetadataKeyChunkingOverlap = "IndexedWith:ChunkingOverlap";
    public const string MetadataKeyEmbeddingProvider = "IndexedWith:EmbeddingProvider";
    public const string MetadataKeyEmbeddingModel = "IndexedWith:EmbeddingModel";
    public const string MetadataKeyEmbeddingDimensions = "IndexedWith:EmbeddingDimensions";

    public IngestionPipeline(
        KnowledgeDbContext context,
        IKnowledgeFileSystem fileSystem,
        IEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        IEnumerable<IDocumentParser> parsers,
        IEnumerable<IChunkingStrategy> chunkingStrategies,
        IOptionsMonitor<ChunkingSettings> chunkingSettings,
        IOptionsMonitor<EmbeddingSettings> embeddingSettings,
        EmbeddingCache embeddingCache,
        ILogger<IngestionPipeline> logger)
    {
        _context = context;
        _fileSystem = fileSystem;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _parsers = parsers;
        _chunkingStrategies = chunkingStrategies;
        _chunkingSettings = chunkingSettings;
        _embeddingSettings = embeddingSettings;
        _embeddingCache = embeddingCache;
        _logger = logger;
    }

    public async Task<IngestionResult> IngestAsync(
        Stream content,
        IngestionOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        Stream? workingStream = null;
        bool createdMemoryStream = false;

        // Hoisted so the catch block can persist "Failed" status to DB.
        var documentId = !string.IsNullOrEmpty(options.DocumentId) && Guid.TryParse(options.DocumentId, out var providedId)
            ? providedId
            : Guid.NewGuid();
        DocumentEntity? documentEntity = null;

        try
        {
            // Handle non-seekable streams (e.g., from MinIO)
            if (!content.CanSeek)
            {
                // Copy to MemoryStream for seekable operations
                var ms = new MemoryStream();
                await content.CopyToAsync(ms, ct);
                ms.Position = 0;
                workingStream = ms;
                createdMemoryStream = true;
            }
            else
            {
                workingStream = content;
            }

            var contentHash = await ComputeContentHashAsync(workingStream, ct);

            // Save file to storage (only if not already saved)
            // Note: When called from IngestionWorker, file is already saved
            var virtualPath = options.Path ?? options.FileName ?? $"upload-{documentId}";

            // Build metadata including indexing settings for reindex detection
            var metadata = new Dictionary<string, string>(options.Metadata ?? new Dictionary<string, string>());
            var chunkSettings = _chunkingSettings.CurrentValue;
            var embedSettings = _embeddingSettings.CurrentValue;

            // Store chunking settings used
            metadata[MetadataKeyChunkingStrategy] = options.Strategy.ToString();
            metadata[MetadataKeyChunkingMaxSize] = chunkSettings.MaxChunkSize.ToString();
            metadata[MetadataKeyChunkingOverlap] = chunkSettings.Overlap.ToString();

            // Store embedding settings used
            metadata[MetadataKeyEmbeddingProvider] = embedSettings.Provider;
            metadata[MetadataKeyEmbeddingModel] = embedSettings.Model;
            metadata[MetadataKeyEmbeddingDimensions] = embedSettings.Dimensions.ToString();

            var containerId = !string.IsNullOrEmpty(options.ContainerId) && Guid.TryParse(options.ContainerId, out var cId)
                ? cId
                : Guid.Empty;

            // Check if document already exists (created eagerly by UploadService)
            documentEntity = await _context.Documents.FindAsync([documentId], ct);
            bool isReindex = documentEntity is not null;

            // Generation check: skip stale jobs early (before any expensive work)
            int jobGeneration = options.Generation;
            if (isReindex && !await IsCurrentGenerationAsync(documentId, jobGeneration, ct))
            {
                _logger.LogInformation(
                    "Skipping stale job for document {DocumentId}: job generation {JobGen} != current generation",
                    documentId, jobGeneration);
                return new IngestionResult(
                    DocumentId: documentId.ToString(),
                    ChunkCount: 0,
                    Duration: stopwatch.Elapsed,
                    Warnings: ["Stale job skipped — document was re-uploaded"]);
            }

            if (documentEntity != null)
            {
                // Update existing document for reindex
                documentEntity.ContainerId = containerId;
                documentEntity.FileName = options.FileName ?? "unknown";
                documentEntity.ContentType = options.ContentType;
                documentEntity.Path = virtualPath;
                documentEntity.ContentHash = contentHash;
                documentEntity.SizeBytes = workingStream.Length;
                documentEntity.Status = "Processing";
                documentEntity.Metadata = metadata;
            }
            else
            {
                documentEntity = new DocumentEntity
                {
                    Id = documentId,
                    ContainerId = containerId,
                    FileName = options.FileName ?? "unknown",
                    ContentType = options.ContentType,
                    Path = virtualPath,
                    ContentHash = contentHash,
                    SizeBytes = workingStream.Length,
                    Status = "Processing",
                    CreatedAt = DateTime.UtcNow,
                    Metadata = metadata
                };

                _context.Documents.Add(documentEntity);
            }

            await _context.SaveChangesAsync(ct);

            // For reindex: purge stale chunks and their vectors (cascade) before adding new ones.
            // Without this, every re-index doubles the chunk count, polluting search results.
            if (isReindex)
            {
                await _context.Chunks
                    .Where(c => c.DocumentId == documentEntity.Id)
                    .ExecuteDeleteAsync(ct);
            }

            // Parse document
            workingStream.Position = 0;
            var parsedDocument = await ParseDocumentAsync(workingStream, options.FileName ?? "", ct);
            warnings.AddRange(parsedDocument.Warnings);

            // Chunk document
            var chunks = await ChunkDocumentAsync(parsedDocument, options.Strategy, ct);

            if (chunks.Count == 0)
            {
                warnings.Add("No chunks generated from document");
                documentEntity.Status = "Failed";
                documentEntity.ErrorMessage = "No extractable content";
                await _context.SaveChangesAsync(ct);

                return new IngestionResult(
                    DocumentId: documentId.ToString(),
                    ChunkCount: 0,
                    Duration: stopwatch.Elapsed,
                    Warnings: warnings);
            }

            // Embed chunks — skip if the chunker already produced precomputed embeddings
            // (SemanticChunker mean-pools sentence embeddings, avoiding a second API call).
            IReadOnlyList<float[]> embeddings;
            if (chunks.All(c => c.PrecomputedEmbedding != null))
            {
                embeddings = chunks.Select(c => c.PrecomputedEmbedding!).ToList();
                _logger.LogDebug("Using precomputed embeddings from chunker for {Count} chunks", chunks.Count);
            }
            else
            {
                var chunkContents = chunks.Select(c => c.Content).ToArray();
                string modelId = embedSettings.Model;
                int dimensions = embedSettings.Dimensions;

                // Check content-hash cache before calling the embedding API
                var cached = await _embeddingCache.GetCachedEmbeddingsAsync(
                    chunkContents, modelId, dimensions, ct);

                // Only embed chunks that don't have a cached vector
                var missIndices = cached
                    .Select((v, i) => (v, i))
                    .Where(x => x.v == null)
                    .Select(x => x.i)
                    .ToList();

                float[]?[] result = new float[]?[chunkContents.Length];

                // Copy cache hits
                for (int i = 0; i < cached.Count; i++)
                    if (cached[i] != null) result[i] = cached[i];

                if (missIndices.Count > 0)
                {
                    var missContents = missIndices.Select(i => chunkContents[i]).ToArray();
                    var freshEmbeddings = await _embeddingProvider.EmbedBatchAsync(missContents, ct);
                    for (int k = 0; k < missIndices.Count; k++)
                        result[missIndices[k]] = freshEmbeddings[k];

                    _logger.LogDebug(
                        "Embedding cache: {Hits} hits, {Misses} misses for {Total} chunks",
                        cached.Count - missIndices.Count, missIndices.Count, chunkContents.Length);
                }
                else
                {
                    _logger.LogDebug("Embedding cache: all {Count} chunks were cache hits", chunkContents.Length);
                }

                embeddings = result.Select(v => v!).ToList();
            }

            // Second generation check: the document may have been re-uploaded during the
            // expensive embedding call. If so, skip chunk insertion — the newer job will handle it.
            if (!await IsCurrentGenerationAsync(documentEntity.Id, jobGeneration, ct))
            {
                _logger.LogInformation(
                    "Document {DocumentId} was re-uploaded during ingestion (generation changed) — skipping chunk insertion",
                    documentEntity.Id);
                return new IngestionResult(
                    DocumentId: documentEntity.Id.ToString(),
                    ChunkCount: 0,
                    Duration: stopwatch.Elapsed,
                    Warnings: ["Document was re-uploaded during ingestion"]);
            }

            // Also verify the row still exists (handles deletion during ingestion)
            var docStillExists = await _context.Documents
                .AnyAsync(d => d.Id == documentEntity.Id, ct);
            if (!docStillExists)
            {
                _logger.LogWarning(
                    "Document {DocumentId} was deleted during ingestion — skipping chunk insertion",
                    documentEntity.Id);
                return new IngestionResult(
                    DocumentId: documentEntity.Id.ToString(),
                    ChunkCount: 0,
                    Duration: stopwatch.Elapsed,
                    Warnings: ["Document was deleted during ingestion"]);
            }

            // Stage all chunk entities and vector items, then flush in two SaveChangesAsync calls
            // (one inside UpsertBatchAsync for chunks+vectors, one for the document status update).
            var vectorItems = new List<(string Id, float[] Vector, Dictionary<string, string> Metadata)>(chunks.Count);

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunkInfo = chunks[i];
                var chunkId = Guid.NewGuid();

                _context.Chunks.Add(new ChunkEntity
                {
                    Id = chunkId,
                    DocumentId = documentEntity.Id,
                    ContainerId = containerId,
                    Content = chunkInfo.Content,
                    ChunkIndex = chunkInfo.ChunkIndex,
                    TokenCount = chunkInfo.TokenCount,
                    StartOffset = chunkInfo.StartOffset,
                    EndOffset = chunkInfo.EndOffset,
                    Metadata = chunkInfo.Metadata
                });

                vectorItems.Add((chunkId.ToString(), embeddings[i], new Dictionary<string, string>(chunkInfo.Metadata)
                {
                    ["documentId"] = documentEntity.Id.ToString(),
                    ["containerId"] = containerId.ToString(),
                    ["modelId"] = embedSettings.Model,
                    ["ChunkIndex"] = chunkInfo.ChunkIndex.ToString(),
                    ["contentHash"] = EmbeddingCache.ComputeHash(chunkInfo.Content),
                    ["dimensions"] = embedSettings.Dimensions.ToString(),
                }));
            }

            // Single round-trip: saves all staged chunk entities + all vector rows.
            await _vectorStore.UpsertBatchAsync(vectorItems, ct);

            // Update document status
            documentEntity.ChunkCount = chunks.Count;
            documentEntity.Status = "Ready";
            documentEntity.ErrorMessage = null;
            documentEntity.LastIndexedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);

            stopwatch.Stop();

            _logger.LogInformation(
                "Successfully ingested document {DocumentId}: {ChunkCount} chunks in {Duration}ms",
                documentId,
                chunks.Count,
                stopwatch.ElapsedMilliseconds);

            return new IngestionResult(
                DocumentId: documentId.ToString(),
                ChunkCount: chunks.Count,
                Duration: stopwatch.Elapsed,
                Warnings: warnings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during document ingestion");
            warnings.Add($"Ingestion failed: {ex.Message}");

            // Persist "Failed" so the 5-minute rescan doesn't keep re-enqueuing a document
            // that's stuck at "Processing" — which would cause status to flicker endlessly.
            if (documentEntity is not null)
            {
                try
                {
                    documentEntity.Status = "Failed";
                    documentEntity.ErrorMessage = ex.Message;
                    await _context.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception dbEx)
                {
                    _logger.LogWarning(dbEx, "Failed to persist Failed status for document {DocumentId}", documentId);
                }
            }

            return new IngestionResult(
                DocumentId: documentId.ToString(),
                ChunkCount: 0,
                Duration: stopwatch.Elapsed,
                Warnings: warnings);
        }
        finally
        {
            // Dispose working stream if we created a MemoryStream
            if (createdMemoryStream && workingStream != null)
            {
                await workingStream.DisposeAsync();
            }
        }
    }

    public async IAsyncEnumerable<IngestionProgress> IngestWithProgressAsync(
        Stream content,
        IngestionOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new IngestionProgress(IngestionPhase.Parsing, 0, "Starting ingestion");

        // Parse
        content.Position = 0;
        var parsedDocument = await ParseDocumentAsync(content, options.FileName ?? "", ct);
        yield return new IngestionProgress(IngestionPhase.Parsing, 100, "Document parsed");

        // Chunk
        yield return new IngestionProgress(IngestionPhase.Chunking, 0, "Chunking document");
        var chunks = await ChunkDocumentAsync(parsedDocument, options.Strategy, ct);
        yield return new IngestionProgress(IngestionPhase.Chunking, 100, $"{chunks.Count} chunks created");

        // Embed
        yield return new IngestionProgress(IngestionPhase.Embedding, 0, "Generating embeddings");
        var chunkContents = chunks.Select(c => c.Content).ToArray();
        await _embeddingProvider.EmbedBatchAsync(chunkContents, ct);
        yield return new IngestionProgress(IngestionPhase.Embedding, 100, "Embeddings generated");

        // Store
        yield return new IngestionProgress(IngestionPhase.Storing, 0, "Storing in database");
        await IngestAsync(content, options, ct);
        yield return new IngestionProgress(IngestionPhase.Complete, 100, "Ingestion complete");
    }

    private async Task<ParsedDocument> ParseDocumentAsync(
        Stream content,
        string fileName,
        CancellationToken ct)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        var parser = _parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));

        if (parser == null)
        {
            _logger.LogWarning("No parser found for extension: {Extension}", extension);
            return new ParsedDocument(
                Content: string.Empty,
                Metadata: new Dictionary<string, string>(),
                Warnings: [$"Unsupported file type: {extension}"]);
        }

        return await parser.ParseAsync(content, fileName, ct);
    }

    private async Task<IReadOnlyList<ChunkInfo>> ChunkDocumentAsync(
        ParsedDocument parsedDocument,
        ChunkingStrategy strategyType,
        CancellationToken ct)
    {
        var settings = _chunkingSettings.CurrentValue;
        var strategyName = strategyType.ToString();

        var strategy = _chunkingStrategies.FirstOrDefault(s =>
            s.Name.Equals(strategyName, StringComparison.OrdinalIgnoreCase));

        if (strategy == null)
        {
            _logger.LogWarning("Chunking strategy not found: {Strategy}, using FixedSize", strategyName);
            strategy = _chunkingStrategies.First(s => s.Name == "FixedSize");
        }

        return await strategy.ChunkAsync(parsedDocument, settings, ct);
    }

    private async Task<bool> IsCurrentGenerationAsync(Guid documentId, int jobGeneration, CancellationToken ct)
    {
        if (jobGeneration == 0)
            return true;

        int currentGen = await _context.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.Generation)
            .FirstOrDefaultAsync(ct);

        return currentGen == jobGeneration;
    }

    private static async Task<string> ComputeContentHashAsync(Stream content, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();

        // Stream must be seekable - caller should ensure this
        if (!content.CanSeek)
        {
            throw new InvalidOperationException("Stream must be seekable to compute content hash");
        }

        content.Position = 0;
        var hashBytes = await sha256.ComputeHashAsync(content, ct);
        content.Position = 0;

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
