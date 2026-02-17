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

        try
        {
            // Use provided DocumentId or generate a new one
            var documentId = !string.IsNullOrEmpty(options.DocumentId) && Guid.TryParse(options.DocumentId, out var providedId)
                ? providedId
                : Guid.NewGuid();

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

            // Check if document already exists (e.g., during reindex)
            var documentEntity = await _context.Documents.FindAsync([documentId], ct);
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

            // Store chunks and generate embeddings
            var chunkContents = chunks.Select(c => c.Content).ToArray();
            var embeddings = await _embeddingProvider.EmbedBatchAsync(chunkContents, ct);

            // Store everything in database
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunkInfo = chunks[i];
                var chunkId = Guid.NewGuid();

                // Create chunk entity
                var chunkEntity = new ChunkEntity
                {
                    Id = chunkId,
                    DocumentId = documentId,
                    ContainerId = containerId,
                    Content = chunkInfo.Content,
                    ChunkIndex = chunkInfo.ChunkIndex,
                    TokenCount = chunkInfo.TokenCount,
                    StartOffset = chunkInfo.StartOffset,
                    EndOffset = chunkInfo.EndOffset,
                    Metadata = chunkInfo.Metadata
                };

                _context.Chunks.Add(chunkEntity);

                // Store embedding in vector store
                var chunkMetadata = new Dictionary<string, string>(chunkInfo.Metadata)
                {
                    ["documentId"] = documentId.ToString(),
                    ["containerId"] = containerId.ToString(),
                    ["modelId"] = embedSettings.Model,
                    ["ChunkIndex"] = chunkInfo.ChunkIndex.ToString()
                };

                await _vectorStore.UpsertAsync(
                    chunkId.ToString(),
                    embeddings[i],
                    chunkMetadata,
                    ct);
            }

            // Update document status
            documentEntity.ChunkCount = chunks.Count;
            documentEntity.Status = "Ready";
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

            return new IngestionResult(
                DocumentId: string.Empty,
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

    public Task<IngestionResult> IngestFromPathAsync(
        string path,
        IngestionOptions options,
        CancellationToken ct = default)
    {
        throw new NotImplementedException("IngestFromPathAsync will be implemented in Phase 6");
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
