using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using AIKnowledge.Storage.Data;
using AIKnowledge.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace AIKnowledge.Ingestion.Pipeline;

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
    private readonly ILogger<IngestionPipeline> _logger;

    public IngestionPipeline(
        KnowledgeDbContext context,
        IKnowledgeFileSystem fileSystem,
        IEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        IEnumerable<IDocumentParser> parsers,
        IEnumerable<IChunkingStrategy> chunkingStrategies,
        IOptionsMonitor<ChunkingSettings> chunkingSettings,
        ILogger<IngestionPipeline> logger)
    {
        _context = context;
        _fileSystem = fileSystem;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _parsers = parsers;
        _chunkingStrategies = chunkingStrategies;
        _chunkingSettings = chunkingSettings;
        _logger = logger;
    }

    public async Task<IngestionResult> IngestAsync(
        Stream content,
        IngestionOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();

        try
        {
            // Create document entity
            var documentId = Guid.NewGuid();
            var contentHash = await ComputeContentHashAsync(content, ct);

            // Save file to storage
            var virtualPath = options.FileName ?? $"upload-{documentId}";
            content.Position = 0;
            await _fileSystem.SaveFileAsync(virtualPath, content, ct);

            var documentEntity = new DocumentEntity
            {
                Id = documentId,
                FileName = options.FileName ?? "unknown",
                ContentType = options.ContentType,
                CollectionId = options.CollectionId,
                VirtualPath = virtualPath,
                ContentHash = contentHash,
                SizeBytes = content.Length,
                Status = "Processing",
                CreatedAt = DateTime.UtcNow,
                Metadata = options.Metadata ?? new Dictionary<string, string>()
            };

            _context.Documents.Add(documentEntity);
            await _context.SaveChangesAsync(ct);

            // Parse document
            content.Position = 0;
            var parsedDocument = await ParseDocumentAsync(content, options.FileName ?? "", ct);
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
                    Content = chunkInfo.Content,
                    ChunkIndex = chunkInfo.ChunkIndex,
                    TokenCount = chunkInfo.TokenCount,
                    StartOffset = chunkInfo.StartOffset,
                    EndOffset = chunkInfo.EndOffset,
                    Metadata = chunkInfo.Metadata
                };

                _context.Chunks.Add(chunkEntity);

                // Store embedding in vector store
                var metadata = new Dictionary<string, string>(chunkInfo.Metadata)
                {
                    ["DocumentId"] = documentId.ToString(),
                    ["ChunkIndex"] = chunkInfo.ChunkIndex.ToString()
                };

                await _vectorStore.UpsertAsync(
                    chunkId.ToString(),
                    embeddings[i],
                    metadata,
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
        content.Position = 0;
        var hashBytes = await sha256.ComputeHashAsync(content, ct);
        content.Position = 0;

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
