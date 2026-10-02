using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Ingestion.Parsers;
using Connapse.Ingestion.Validation;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;
using Npgsql;
using System.Diagnostics;
using System.Globalization;
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
    private readonly IContainerStore _containerStore;
    private readonly IManagedStorageProvider _managedStorage;
    private readonly IDocumentStore _documentStore;
    private readonly ISourceStore _sourceStore;
    private readonly IConnectionStore _connectionStore;
    private readonly IConnectorFactory _connectorFactory;
    private readonly IDocumentLifecycle _lifecycle;
    private readonly IOptionsMonitor<UploadSettings> _uploadSettings;
    private readonly ILogger<IngestionPipeline> _logger;

    // Metadata keys for tracking indexing settings
    public const string MetadataKeyChunkingStrategy = "IndexedWith:ChunkingStrategy";
    public const string MetadataKeyChunkingMaxSize = "IndexedWith:ChunkingMaxSize";
    public const string MetadataKeyChunkingOverlap = "IndexedWith:ChunkingOverlap";
    public const string MetadataKeyEmbeddingProvider = "IndexedWith:EmbeddingProvider";
    public const string MetadataKeyEmbeddingModel = "IndexedWith:EmbeddingModel";
    public const string MetadataKeyEmbeddingDimensions = "IndexedWith:EmbeddingDimensions";

    /// <summary>Which parser produced the document's chunks, and which version of it (#596).</summary>
    public const string MetadataKeyParser = "IndexedWith:Parser";
    public const string MetadataKeyParserVersion = "IndexedWith:ParserVersion";

    /// <summary>
    /// The parser version a document was indexed with. Documents indexed before versions were
    /// recorded carry none; they were parsed by what is now version 1.
    /// </summary>
    public static int StoredParserVersion(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue(MetadataKeyParserVersion, out var stored) && int.TryParse(stored, out int version)
            ? version
            : 1;

    /// <summary>
    /// The parser a document was indexed with. Documents indexed before names were recorded were
    /// read by whichever parser owned their extension at the time; treating a missing name as a
    /// match instead would hide a different parser taking that extension over.
    /// </summary>
    public static string? StoredParserName(IReadOnlyDictionary<string, string> metadata, string extension) =>
        metadata.TryGetValue(MetadataKeyParser, out var stored) && !string.IsNullOrEmpty(stored)
            ? stored
            : LegacyParserByExtension.GetValueOrDefault(extension);

    /// <summary>The parser that owned each extension when parser identities started being recorded (#596).</summary>
    private static readonly Dictionary<string, string> LegacyParserByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "TextParser", [".md"] = "TextParser", [".markdown"] = "TextParser", [".csv"] = "TextParser",
        [".log"] = "TextParser", [".json"] = "TextParser", [".xml"] = "TextParser", [".yaml"] = "TextParser",
        [".yml"] = "TextParser",
        [".pdf"] = "PdfParser",
        [".docx"] = "OfficeParser", [".pptx"] = "OfficeParser",
    };

    /// <summary>
    /// Set when pages failed to extract: the document's chunks are partial, or are an older
    /// parse kept in place of a partial one. Reindex retries any document carrying it.
    /// </summary>
    public const string MetadataKeyExtractionIncomplete = "ExtractionIncomplete";

    /// <summary>The parser's warnings from the last ingestion, one per line.</summary>
    public const string MetadataKeyParserWarnings = "ParserWarnings";

    /// <summary>Keeps a page-by-page warning list from a long PDF from bloating the metadata column.</summary>
    private const int MaxParserWarningsLength = 4000;

    internal static string TruncateWarnings(IReadOnlyList<string> warnings)
    {
        string joined = string.Join("\n", warnings);
        return joined.Length <= MaxParserWarningsLength
            ? joined
            : joined[..MaxParserWarningsLength] + $"\n… ({warnings.Count} warnings in total)";
    }

    /// <summary>
    /// Written by SourceSyncService to record the remote's state at last ingestion, and
    /// preserved across a reindex so its change detection keeps a baseline to compare against.
    /// </summary>
    private static readonly string[] RemoteSignatureKeys = ["RemoteLastModified", "RemoteSize"];

    /// <summary>
    /// Initializes a new instance of <see cref="IngestionPipeline"/> with the required services and configuration providers.
    /// <summary>
    /// Initializes a new IngestionPipeline with the dependencies required to parse documents, split them into chunks, produce embeddings, and persist vectors and document state.
    /// </summary>
    /// <param name="context">Database context used to persist documents, chunks, and related entities.</param>
    /// <param name="fileSystem">Abstraction for file and stream access.</param>
    /// <param name="embeddingProvider">Service that produces embeddings for text content.</param>
    /// <param name="vectorStore">Store used to upsert and manage vector data.</param>
    /// <param name="parsers">Collection of document parsers supporting different file types.</param>
    /// <param name="chunkingStrategies">Collection of chunking strategies available for splitting documents.</param>
    /// <param name="chunkingSettings">Runtime monitor providing current chunking configuration.</param>
    /// <param name="embeddingSettings">Runtime monitor providing current embedding configuration.</param>
    /// <param name="embeddingCache">Cache used to retrieve or compute embeddings to avoid redundant work.</param>
    /// <param name="logger">Logger used for recording pipeline diagnostics and errors.</param>
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
        IContainerStore containerStore,
        IManagedStorageProvider managedStorage,
        IDocumentStore documentStore,
        ISourceStore sourceStore,
        IConnectionStore connectionStore,
        IConnectorFactory connectorFactory,
        IDocumentLifecycle lifecycle,
        IOptionsMonitor<UploadSettings> uploadSettings,
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
        _containerStore = containerStore;
        _managedStorage = managedStorage;
        _documentStore = documentStore;
        _sourceStore = sourceStore;
        _connectionStore = connectionStore;
        _connectorFactory = connectorFactory;
        _lifecycle = lifecycle;
        _uploadSettings = uploadSettings;
        _logger = logger;
    }

    /// <summary>
    /// Parses, chunks and embeds a document, then swaps its chunks and marks it Ready in one
    /// transaction.
    /// </summary>
    /// <remarks>
    /// All the expensive and fallible work happens before anything is written, so the document's
    /// previous chunks stay searchable until the new ones commit, and a failure leaves them
    /// untouched.
    /// <para>
    /// Failures propagate. <see cref="PermanentIngestionException"/> marks one that retrying cannot
    /// fix; anything else is treated as transient by the job that called this. A ChunkCount of 0
    /// means the work was superseded — a newer version of the document, or its deletion — and
    /// nothing was written.
    /// </para>
    /// </remarks>
    public async Task<IngestionResult> IngestAsync(
        Stream content,
        IngestionOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        Stream? workingStream = null;
        bool createdMemoryStream = false;

        var documentId = !string.IsNullOrEmpty(options.DocumentId) && Guid.TryParse(options.DocumentId, out var providedId)
            ? providedId
            : Guid.NewGuid();

        // Resolve ownership first. Everything below writes through this, so a source-owned
        // document cannot be recorded against container_id — which would violate the
        // ck_documents_single_owner CHECK — and no chunk or vector can be written with a zero
        // owner, which no owner-scoped query would ever match. An ownerless call is a
        // programming error.
        var owner = options.Owner
            ?? (!string.IsNullOrEmpty(options.ContainerId) && Guid.TryParse(options.ContainerId, out var cId)
                ? OwnerRef.ForContainer(cId)
                : throw new ArgumentException(
                    "Ingestion requires an owner: set Owner, or supply a parseable ContainerId.", nameof(options)));

        try
        {
            // Handle non-seekable streams (e.g., from MinIO)
            long maxFileBytes = _uploadSettings.CurrentValue.MaxFileBytes;
            if (!content.CanSeek)
            {
                // Bounded while copying: a connector stream of unknown length would otherwise be
                // buffered whole, and hashed, before the size limit could refuse it.
                var ms = new MemoryStream();
                await CopyBoundedAsync(content, ms, maxFileBytes, options.FileName, ct);
                ms.Position = 0;
                workingStream = ms;
                createdMemoryStream = true;
            }
            else
            {
                if (content.Length > maxFileBytes)
                    throw new PermanentIngestionException(FileTooLarge(options.FileName, content.Length, maxFileBytes));
                workingStream = content;
            }

            var contentHash = await ComputeContentHashAsync(workingStream, ct);
            var virtualPath = options.Path ?? options.FileName ?? $"upload-{documentId}";

            // Parsing and chunking first: they need nothing from the database, and a file that
            // cannot be read is known to be a permanent failure before any row is touched.
            workingStream.Position = 0;
            var parsedDocument = await ParseDocumentAsync(workingStream, options.FileName ?? "", ct);

            // Text that came out of a PDF's glyph mappings can be junk end to end. Indexed, it is a
            // document that shows as Ready and matches nothing anyone types. Judged before the
            // cleaning below, which would erase the NUL glyphs it needs to count.
            string extension = Path.GetExtension(options.FileName ?? "").ToLowerInvariant();
            UploadSettings limits = _uploadSettings.CurrentValue;
            var quality = TextQuality.Measure(parsedDocument.Content);
            if (TextQuality.DescribeGarbled(extension, quality, limits) is { } garbled)
                throw new PermanentIngestionException($"Could not parse {Path.GetFileName(options.FileName)}: {garbled}");
            string? partlyGarbled = TextQuality.DescribePartlyGarbled(extension, quality, limits);

            // Every chunk, and any failure message quoting a warning, ends up in a text column,
            // so the parser's output is made storable once here rather than in each parser.
            parsedDocument = parsedDocument with
            {
                Content = StorableText.Clean(parsedDocument.Content),
                Warnings = parsedDocument.Warnings.Select(StorableText.Clean).ToList(),
                Metadata = CleanMetadata(parsedDocument.Metadata),
            };
            warnings.AddRange(parsedDocument.Warnings);
            if (partlyGarbled is not null)
                warnings.Add(partlyGarbled);

            // A re-parse of unchanged bytes that lost pages to exceptions would swap a complete
            // index for a partial one -- or, when every page threw, fail a document whose old
            // chunks are still right. Decided before chunking so that second case reaches it too.
            // The row is read early only then: otherwise a file that cannot be read is still known
            // to be a permanent failure before the database is touched.
            parsedDocument.Metadata.TryGetValue(PdfParser.MetadataKeyPageErrors, out var pageErrors);
            DocumentEntity? existing = null;
            bool keepPreviousIndex = false;
            if (pageErrors is not null)
            {
                existing = await LoadExistingAsync(documentId, ct);
                keepPreviousIndex = existing is { ChunkCount: > 0 } &&
                    string.Equals(existing.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase);
            }

            IReadOnlyList<ChunkInfo> chunks = [];
            if (!keepPreviousIndex)
            {
                chunks = await ChunkDocumentAsync(parsedDocument, options.Strategy, options.FileName, options.StrategyIsExplicit, ct);

                // Cleaned again per chunk: a chunker cutting at a token or character offset can split a
                // surrogate pair, and the half left at either edge breaks both the embedder's Unicode
                // normalisation and the database write (#595).
                chunks = chunks.Select(c => c with { Content = StorableText.Clean(c.Content) }).ToList();
                if (chunks.Count == 0)
                {
                    throw new PermanentIngestionException(warnings.Count > 0
                        ? $"No extractable content ({string.Join("; ", warnings)}) [no_text]"
                        : "No extractable content [no_text]");
                }
            }

            if (pageErrors is null)
                existing = await LoadExistingAsync(documentId, ct);

            if (existing is not null)
            {
                // Ownership is immutable. Writing both columns from the incoming options would
                // let a reindex move a document between a source and a container while still
                // satisfying ck_documents_single_owner — exactly one column stays set, so the
                // database cannot catch it — silently carrying content across an authorization
                // boundary.
                var existingOwner = existing.SourceId is Guid existingSourceId
                    ? OwnerRef.ForSource(existingSourceId)
                    : OwnerRef.ForContainer(existing.ContainerId!.Value);

                if (existingOwner != owner)
                    throw new DocumentOwnershipChangedException(documentId, existingOwner, owner);
            }

            // A job claimed the document before calling here, so it is already Processing. A
            // direct caller has not, and takes it the same way a job would: as a new generation,
            // so that anything already working on the document can no longer complete it.
            int generation = options.Generation;
            if (existing is not null && existing.IngestionStatus != DocumentStatus.Processing)
            {
                int? claimed = await _lifecycle.EnqueuedAsync(documentId, resetAttempts: false, ct);
                if (claimed is null || !await _lifecycle.TryClaimAsync(documentId, claimed.Value, ct))
                    return Superseded(documentId, stopwatch, "Document changed before ingestion started");
                generation = claimed.Value;
            }
            else if (existing is not null && generation != 0 && existing.Generation != generation)
            {
                return Superseded(documentId, stopwatch, "Stale job skipped — document was re-uploaded");
            }

            // The old chunks stay; the document returns to Ready marked incomplete, with its previous
            // parser and index time, so it reads as what it is and the next reindex tries again.
            // Changed bytes are indexed partially instead, marked the same way: stale text is worse
            // than a missing page.
            if (keepPreviousIndex)
                return await KeepPreviousIndexAsync(existing!, generation, pageErrors!, warnings, stopwatch, ct);

            var metadata = BuildMetadata(options, existing);

            // Parser warnings used to reach only the log, or a failure message. A document that
            // indexed with half its pages empty now says so where the API and UI can show it.
            if (_parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension)) is { } usedParser)
            {
                metadata[MetadataKeyParser] = usedParser.Name;
                metadata[MetadataKeyParserVersion] = usedParser.Version.ToString(CultureInfo.InvariantCulture);
            }

            if (warnings.Count > 0)
                metadata[MetadataKeyParserWarnings] = TruncateWarnings(warnings);
            else
                metadata.Remove(MetadataKeyParserWarnings); // a reindex may carry the last run's forward

            if (pageErrors is not null)
                metadata[MetadataKeyExtractionIncomplete] = pageErrors;
            else
                metadata.Remove(MetadataKeyExtractionIncomplete);

            var embedSettings = _embeddingSettings.CurrentValue;
            IReadOnlyList<float[]> embeddings = await EmbedChunksAsync(chunks, embedSettings, ct);

            // The swap. Old chunks go and new ones arrive in the same transaction as the move to
            // Ready, guarded on the generation: if the document was re-uploaded or reindexed while
            // this was embedding, the guard fails, the transaction rolls back, and the newer job's
            // work is the one that lands.
            await using var transaction = await _context.Database.BeginTransactionAsync(ct);

            DocumentEntity documentEntity;
            if (existing is null)
            {
                // Only direct callers reach here; every job's document row exists before the job
                // is enqueued. Nothing else can be working on a row that does not exist yet, so
                // it is created already Ready.
                documentEntity = new DocumentEntity
                {
                    Id = documentId,
                    CreatedAt = DateTime.UtcNow,
                    IngestionStatus = DocumentStatus.Ready,
                    StatusChangedAt = DateTime.UtcNow,
                    LastIndexedAt = DateTime.UtcNow,
                };
                _context.Documents.Add(documentEntity);
            }
            else
            {
                documentEntity = await _context.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct)
                    ?? throw new PermanentIngestionException("Document was deleted during ingestion");

                await _context.Chunks
                    .Where(c => c.DocumentId == documentId)
                    .ExecuteDeleteAsync(ct);
            }

            documentEntity.ContainerId = owner.ContainerId;
            documentEntity.SourceId = owner.SourceId;
            documentEntity.FileName = options.FileName ?? "unknown";
            documentEntity.ContentType = options.ContentType;
            documentEntity.Path = virtualPath;
            documentEntity.ContentHash = contentHash;
            documentEntity.SizeBytes = workingStream.Length;
            documentEntity.ChunkCount = chunks.Count;
            documentEntity.Metadata = metadata;

            var vectorItems = StageChunks(documentId, owner, chunks, embeddings, embedSettings);

            // Saves the document fields, the staged chunks and the vectors in one round trip.
            try
            {
                await _vectorStore.UpsertBatchAsync(vectorItems, ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: var state }
                                                && state.StartsWith("22", StringComparison.Ordinal))
            {
                // Class 22 is PostgreSQL rejecting the data itself — text holding NUL characters,
                // say, from a UTF-16 file read as UTF-8. The same file is rejected on every retry.
                throw new PermanentIngestionException(
                    $"The extracted text could not be stored: {ex.InnerException.Message}", ex);
            }

            if (existing is not null &&
                (await DocumentLifecycle.CompleteAsync(_context, documentId, generation, ct)).Count == 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _logger.LogInformation(
                    "Document {DocumentId} changed during ingestion (generation {Generation} superseded); discarding this result",
                    documentId, generation);
                return Superseded(documentId, stopwatch, "Document was re-uploaded during ingestion");
            }

            await transaction.CommitAsync(CancellationToken.None);
            await _lifecycle.NotifyAsync(documentId, CancellationToken.None);

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
        finally
        {
            // Dispose working stream if we created a MemoryStream
            if (createdMemoryStream && workingStream != null)
            {
                await workingStream.DisposeAsync();
            }
        }
    }

    private Task<DocumentEntity?> LoadExistingAsync(Guid documentId, CancellationToken ct) =>
        _context.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);

    private async Task<IngestionResult> KeepPreviousIndexAsync(
        DocumentEntity existing, int generation, string pageErrors, List<string> warnings, Stopwatch stopwatch,
        CancellationToken ct)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        if ((await DocumentLifecycle.CompleteAsync(_context, existing.Id, generation, ct)).Count == 0)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Superseded(existing.Id, stopwatch, "Document was re-uploaded during ingestion");
        }

        // Completing marks the document freshly indexed. It was not: the chunks are the old ones,
        // so the old index time stands, and the failure is recorded where the API shows it and
        // where reindex looks for documents to retry.
        var metadata = new Dictionary<string, string>(existing.Metadata)
        {
            [MetadataKeyExtractionIncomplete] = pageErrors,
            [MetadataKeyParserWarnings] = TruncateWarnings(
                [.. warnings, $"{pageErrors} page(s) failed to extract, so the previous index was kept"]),
        };
        await _context.Documents
            .Where(d => d.Id == existing.Id && d.Generation == generation)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Metadata, metadata)
                .SetProperty(d => d.LastIndexedAt, existing.LastIndexedAt), ct);

        await transaction.CommitAsync(CancellationToken.None);
        await _lifecycle.NotifyAsync(existing.Id, CancellationToken.None);

        _logger.LogWarning(
            "ReindexKeptPreviousIndex {DocumentId}: {PageErrors} page(s) failed to extract; its {ChunkCount} existing chunks were kept",
            existing.Id, pageErrors, existing.ChunkCount);

        return new IngestionResult(
            DocumentId: existing.Id.ToString(),
            ChunkCount: existing.ChunkCount,
            Duration: stopwatch.Elapsed,
            Warnings: [.. warnings, $"{pageErrors} page(s) failed to extract, so the previous index was kept"]);
    }

    private static IngestionResult Superseded(Guid documentId, Stopwatch stopwatch, string reason) =>
        new(DocumentId: documentId.ToString(), ChunkCount: 0, Duration: stopwatch.Elapsed, Warnings: [reason]);

    private Dictionary<string, string> BuildMetadata(IngestionOptions options, DocumentEntity? existing)
    {
        var metadata = new Dictionary<string, string>(options.Metadata ?? new Dictionary<string, string>());
        var chunkSettings = _chunkingSettings.CurrentValue;
        var embedSettings = _embeddingSettings.CurrentValue;

        // IMPORTANT: record the resolved strategy name (what ChunkDocumentAsync will actually
        // dispatch to via the auto-router), NOT the raw user-configured option. Otherwise a
        // .md file ingested with options.Strategy=Recursive would store "Recursive" while
        // DocumentAware actually ran — breaking reindex-detection and misleading consumers.
        metadata[MetadataKeyChunkingStrategy] = IngestionPipelineStrategyResolver.Resolve(
            fallbackStrategy: options.Strategy.ToString(),
            fileName: options.FileName,
            strategyIsExplicit: options.StrategyIsExplicit);
        metadata[MetadataKeyChunkingMaxSize] = chunkSettings.MaxChunkSize.ToString();
        metadata[MetadataKeyChunkingOverlap] = chunkSettings.Overlap.ToString();
        metadata[MetadataKeyEmbeddingProvider] = embedSettings.Provider;
        // The vector-space id (model plus its prompts), so changing prompts marks documents for reindex.
        metadata[MetadataKeyEmbeddingModel] = EmbeddingIdentity.For(embedSettings);
        metadata[MetadataKeyEmbeddingDimensions] = embedSettings.Dimensions.ToString();

        // Carry forward the sync layer's record of what the remote looked like when this
        // document was last ingested. Only SourceSyncService sets these, so any other caller —
        // a reindex, in particular — would otherwise replace the metadata wholesale and erase
        // the baseline. The next sync would then find no signature, treat every file as
        // changed, and re-download and re-embed the entire source.
        foreach (string key in RemoteSignatureKeys)
        {
            if (!metadata.ContainsKey(key) &&
                existing?.Metadata?.TryGetValue(key, out var carried) == true)
            {
                metadata[key] = carried;
            }
        }

        return metadata;
    }

    private async Task<IReadOnlyList<float[]>> EmbedChunksAsync(
        IReadOnlyList<ChunkInfo> chunks, EmbeddingSettings embedSettings, CancellationToken ct)
    {
        // Skip if the chunker already produced precomputed embeddings (SemanticChunker
        // mean-pools sentence embeddings, avoiding a second API call).
        if (chunks.All(c => c.PrecomputedEmbedding != null))
        {
            _logger.LogDebug("Using precomputed embeddings from chunker for {Count} chunks", chunks.Count);
            return chunks.Select(c => c.PrecomputedEmbedding!).ToList();
        }

        var chunkContents = chunks.Select(c => c.Content).ToArray();

        // Check content-hash cache before calling the embedding API
        var cached = await _embeddingCache.GetCachedEmbeddingsAsync(
            chunkContents, EmbeddingIdentity.For(embedSettings), embedSettings.Dimensions, ct);

        var missIndices = cached
            .Select((v, i) => (v, i))
            .Where(x => x.v == null)
            .Select(x => x.i)
            .ToList();

        float[]?[] result = new float[]?[chunkContents.Length];
        for (int i = 0; i < cached.Count; i++)
            if (cached[i] != null) result[i] = cached[i];

        if (missIndices.Count > 0)
        {
            var missContents = missIndices.Select(i => chunkContents[i]).ToArray();
            var freshEmbeddings = await _embeddingProvider.EmbedBatchAsync(missContents, EmbeddingInputType.Document, ct);
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

        return result.Select(v => v!).ToList();
    }

    private List<(string Id, float[] Vector, Dictionary<string, string> Metadata)> StageChunks(
        Guid documentId,
        OwnerRef owner,
        IReadOnlyList<ChunkInfo> chunks,
        IReadOnlyList<float[]> embeddings,
        EmbeddingSettings embedSettings)
    {
        var vectorItems = new List<(string Id, float[] Vector, Dictionary<string, string> Metadata)>(chunks.Count);

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunkInfo = chunks[i];
            var chunkId = Guid.NewGuid();

            _context.Chunks.Add(new ChunkEntity
            {
                Id = chunkId,
                DocumentId = documentId,
                OwnerId = owner.Id,
                Content = chunkInfo.Content,
                ChunkIndex = chunkInfo.ChunkIndex,
                TokenCount = chunkInfo.TokenCount,
                StartOffset = chunkInfo.StartOffset,
                EndOffset = chunkInfo.EndOffset,
                Metadata = chunkInfo.Metadata
            });

            vectorItems.Add((chunkId.ToString(), embeddings[i], new Dictionary<string, string>(chunkInfo.Metadata)
            {
                ["documentId"] = documentId.ToString(),
                ["ownerId"] = owner.Id.ToString(),
                ["modelId"] = EmbeddingIdentity.For(embedSettings),
                ["ChunkIndex"] = chunkInfo.ChunkIndex.ToString(),
                ["contentHash"] = EmbeddingCache.ComputeHash(chunkInfo.Content),
                ["dimensions"] = embedSettings.Dimensions.ToString(),
            }));
        }

        return vectorItems;
    }

    /// <summary>
    /// Resolves the source stream for a document via the container's connector and delegates
    /// to <see cref="IngestAsync(Stream, IngestionOptions, CancellationToken)"/>. Used by the
    /// Hangfire ingestion job class, which receives only a documentId at invocation time.
    /// </summary>
    public async Task<IngestionResult> IngestByIdAsync(
        string documentId,
        IngestionOptions options,
        CancellationToken ct = default)
    {
        // A source-owned document is read through its connection's connector, not through
        // managed storage — checked first, because everything below this resolves a container
        // and a source does not have one (#398).
        //
        // Only this entry point was left container-shaped by the connector/source split.
        // IngestAsync already honours OwnerRef.ForSource; the gap was getting the bytes.
        if (options.Owner is { IsSource: true } sourceOwner)
            return await IngestSourceDocumentAsync(documentId, sourceOwner.Id, options, ct);

        // No owner in the options at all: the row is the only thing that knows which it is.
        // Callers that omit it would otherwise be routed down the container branch and throw
        // — and a reindex throws only after it has already deleted the document's chunks, so
        // the cost of guessing wrong here is a document that is gone from search and that no
        // later sync restores, because its remote signature still matches.
        if (options.Owner is null)
        {
            Document? owned = await _documentStore.GetAsync(documentId, ct);
            if (owned?.Owner is { IsSource: true } inferredOwner)
            {
                // Carried on the options, not just used for routing: IngestAsync writes the
                // document through options.Owner, and a source-owned row recorded against
                // container_id would violate ck_documents_single_owner.
                return await IngestSourceDocumentAsync(
                    documentId, inferredOwner.Id, options with { Owner = inferredOwner }, ct);
            }
        }

        // Resolve the container ID — prefer options.ContainerId, fall back to looking up the doc.
        Guid containerId;
        string virtualPath;
        if (!string.IsNullOrEmpty(options.ContainerId) && Guid.TryParse(options.ContainerId, out var cid))
        {
            containerId = cid;
            virtualPath = options.Path ?? options.FileName ?? throw new InvalidOperationException(
                $"IngestByIdAsync: options must provide Path or FileName for document {documentId}");
        }
        else
        {
            Document? doc = await _documentStore.GetAsync(documentId, ct)
                ?? throw new InvalidOperationException(
                    $"IngestByIdAsync: document {documentId} not found and no ContainerId in options");

            // Document.ContainerId carries COALESCE(container_id, source_id), so it parses
            // even for a source-owned row — which is why source ownership is settled above,
            // from doc.Owner, before this point. Reaching here with an unparseable value means
            // the row has no usable owner at all.
            if (string.IsNullOrEmpty(doc.ContainerId) || !Guid.TryParse(doc.ContainerId, out var docContainerId))
                throw new InvalidOperationException(
                    $"IngestByIdAsync: document {documentId} is not owned by a container. " +
                    "Source-owned documents are re-ingested by the source sync engine.");

            containerId = docContainerId;
            virtualPath = doc.Path;
        }

        Container? container = await _containerStore.GetAsync(containerId, ct)
            ?? throw new InvalidOperationException(
                $"IngestByIdAsync: container {containerId} not found for document {documentId}");

        IConnector connector = _managedStorage.CreateConnector(container.Id);
        string jobPath = connector.ResolveJobPath(virtualPath.TrimStart('/'));

        await using Stream stream = await ReadSourceFileAsync(connector, jobPath, ct);
        return await IngestAsync(stream, options, ct);
    }

    /// <summary>
    /// Reads a source-owned document through its connection's connector and ingests it.
    /// </summary>
    /// <remarks>
    /// The path is used as the sync engine recorded it, without <c>ResolveJobPath</c>. That
    /// method exists to turn a virtual container path into a real one; a source's listing
    /// already yields the connector's own absolute form — an OS path for Filesystem, a remote
    /// path for SFTP — and putting it through resolution again would rebase a path that is
    /// already rooted.
    /// </remarks>
    private async Task<IngestionResult> IngestSourceDocumentAsync(
        string documentId, Guid sourceId, IngestionOptions options, CancellationToken ct)
    {
        string path = options.Path ?? options.FileName
            ?? throw new InvalidOperationException(
                $"IngestByIdAsync: options must provide Path or FileName for document {documentId}");

        Source source = await _sourceStore.GetAsync(sourceId, ct)
            ?? throw new InvalidOperationException(
                $"IngestByIdAsync: source {sourceId} not found for document {documentId}");

        IConnector connector = await CreateSourceConnectorAsync(source, documentId, ct);
        try
        {
            await using Stream stream = await ReadSourceFileAsync(connector, path, ct);
            return await IngestAsync(stream, options, ct);
        }
        finally
        {
            // SftpConnector holds an SSH session and S3Connector a socket pool. One job runs
            // per file, so skipping this abandons a connection per document.
            if (connector is IDisposable disposable) disposable.Dispose();
        }
    }

    /// <summary>
    /// Builds the connector a source-owned document is read through: from its connection when
    /// it has one, otherwise from its own provider (a connection-less public GitHub source).
    /// </summary>
    private async Task<IConnector> CreateSourceConnectorAsync(Source source, string documentId, CancellationToken ct)
    {
        if (source.ConnectionId is not Guid connectionId)
        {
            return source.Provider is not null
                ? _connectorFactory.Create(source)
                : throw new InvalidOperationException(
                    $"IngestByIdAsync: source {source.Id} has neither a connection nor a provider for document {documentId}");
        }

        Connection connection = await _connectionStore.GetAsync(connectionId, ct)
            ?? throw new InvalidOperationException(
                $"IngestByIdAsync: connection {connectionId} not found for source {source.Id}");

        // Only fetched when there is one to fetch, matching SourceSyncService. A key ring that
        // cannot decrypt throws, and retrying will not help — so it surfaces as a failed job
        // rather than being swallowed.
        string? secret = connection.HasSecret
            ? await _connectionStore.GetSecretAsync(connection.Id, ct)
            : null;

        return _connectorFactory.Create(source, connection, secret);
    }

    /// <summary>
    /// Reads the file a job points at. A file that is gone will not come back on a retry, so its
    /// absence is permanent; every other read failure — a refused connection, a timeout — is not.
    /// </summary>
    private static async Task<Stream> ReadSourceFileAsync(IConnector connector, string path, CancellationToken ct)
    {
        try
        {
            return await connector.ReadFileAsync(path, ct);
        }
        catch (FileNotFoundException ex)
        {
            throw new PermanentIngestionException($"File not found: {path}", ex);
        }
    }

    private async Task<ParsedDocument> ParseDocumentAsync(
        Stream content,
        string fileName,
        CancellationToken ct)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        var parser = _parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));

        if (parser == null)
            throw new PermanentIngestionException($"Unsupported file type: {extension}");

        string? mismatch = await SniffMismatchAsync(content, extension, ct);
        if (mismatch is not null)
            throw new PermanentIngestionException(
                $"Could not parse {Path.GetFileName(fileName)}: {mismatch}");

        UploadSettings limits = _uploadSettings.CurrentValue;
        string? tooLarge = ParseLimits.CheckInput(content, extension, limits);
        if (tooLarge is not null)
            throw new PermanentIngestionException($"Could not parse {Path.GetFileName(fileName)}: {tooLarge}");

        var timeout = TimeSpan.FromSeconds(Math.Max(1, limits.ParseTimeoutSeconds));
        ParsedDocument parsed = await ParseWithDeadlineAsync(parser, content, fileName, timeout, ct, _logger);

        if (parsed.Content.Length > limits.MaxExtractedCharacters)
            throw new PermanentIngestionException(
                $"Could not parse {Path.GetFileName(fileName)}: it yielded {parsed.Content.Length:N0} characters, " +
                $"over the {limits.MaxExtractedCharacters:N0} limit [output_too_large]");

        return parsed;
    }

    /// <summary>
    /// Runs the parser on the thread pool and stops waiting for it at the deadline.
    /// <para>
    /// The parsers are synchronous underneath and do not all observe the token: PdfPig stuck in a
    /// malformed content stream never checks it. The abandoned parse keeps its thread until it
    /// returns, but the document fails with <c>parse_timeout</c> instead of sitting in Processing
    /// for good. Cancellation by the caller still surfaces as cancellation.
    /// </para>
    /// </summary>
    internal static async Task<ParsedDocument> ParseWithDeadlineAsync(
        IDocumentParser parser, Stream content, string fileName, TimeSpan timeout, CancellationToken ct,
        ILogger? logger = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        Task<ParsedDocument> parse = Task.Run(() => parser.ParseAsync(content, fileName, deadline.Token), deadline.Token);
        try
        {
            return await parse.WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
                                   && (ex is TimeoutException || (ex is OperationCanceledException && deadline.IsCancellationRequested)))
        {
            if (!parse.IsCompleted)
                TrackAbandonedParse(parse, parser, fileName, timeout, logger);

            throw new PermanentIngestionException(
                $"Could not parse {Path.GetFileName(fileName)}: parsing did not finish within {timeout.TotalSeconds:0} seconds [parse_timeout]", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PermanentIngestionException)
        {
            // The file is already in memory, so nothing here is waiting on a network: a parser
            // that throws has met content it cannot read, and will meet it again on every retry.
            throw new PermanentIngestionException($"Could not parse {Path.GetFileName(fileName)}: {ex.Message}", ex);
        }
    }

    private static int s_abandonedParses;

    /// <summary>Parses that outran their deadline and are still running on a thread-pool thread.</summary>
    internal static int AbandonedParses => Volatile.Read(ref s_abandonedParses);

    /// <summary>
    /// .NET cannot stop a thread, so a parser that ignores its token runs on after the deadline.
    /// Each one is counted and logged as an error, so an operator can see them pile up; the
    /// caller then disposes the stream, which ends any parser still reading it. Real isolation
    /// needs a separate process.
    /// </summary>
    private static void TrackAbandonedParse(
        Task<ParsedDocument> parse, IDocumentParser parser, string fileName, TimeSpan timeout, ILogger? logger)
    {
        int running = Interlocked.Increment(ref s_abandonedParses);
        logger?.LogError(
            "ParseAbandoned {Parser} on {FileName} after {TimeoutSeconds} s; {Running} abandoned parse(s) still running",
            parser.GetType().Name, LogSanitizer.Sanitize(Path.GetFileName(fileName)), timeout.TotalSeconds, running);

        _ = parse.ContinueWith(t =>
        {
            _ = t.Exception; // observed, so a late failure is not reported as unobserved
            Interlocked.Decrement(ref s_abandonedParses);
        }, TaskScheduler.Default);
    }

    internal static async Task CopyBoundedAsync(
        Stream source, Stream destination, long maxBytes, string? fileName, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new PermanentIngestionException(FileTooLarge(fileName, total, maxBytes));
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static string FileTooLarge(string? fileName, long bytes, long maxBytes) =>
        $"Could not parse {Path.GetFileName(fileName)}: the file is at least {bytes / (1024 * 1024)} MB, " +
        $"over the {maxBytes / (1024 * 1024)} MB limit [file_too_large]";

    /// <summary>
    /// Parser metadata (a PDF's title, author) is copied into every chunk's JSONB metadata, which
    /// rejects NUL just as text columns do.
    /// </summary>
    private static Dictionary<string, string> CleanMetadata(Dictionary<string, string> metadata)
    {
        var cleaned = new Dictionary<string, string>(metadata.Count);
        foreach (var (key, value) in metadata)
            cleaned[StorableText.Clean(key)] = StorableText.Clean(value);
        return cleaned;
    }

    /// <summary>
    /// Reads the leading bytes, rewinds, and reports whether they contradict the extension.
    /// The caller has already buffered the content, so the stream is seekable here.
    /// </summary>
    private static async Task<string?> SniffMismatchAsync(Stream content, string extension, CancellationToken ct)
    {
        long start = content.Position;
        byte[] head = new byte[ContentSniffer.HeaderLength];
        int read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        content.Position = start;

        return ContentSniffer.DescribeMismatch(head.AsSpan(0, read), extension);
    }

    private async Task<IReadOnlyList<ChunkInfo>> ChunkDocumentAsync(
        ParsedDocument parsedDocument,
        ChunkingStrategy strategyType,
        string? fileName,
        bool strategyIsExplicit,
        CancellationToken ct)
    {
        ChunkingSettings settings = _chunkingSettings.CurrentValue;
        string strategyName = IngestionPipelineStrategyResolver.Resolve(
            fallbackStrategy: strategyType.ToString(),
            fileName: fileName,
            strategyIsExplicit: strategyIsExplicit);

        IChunkingStrategy? strategy = _chunkingStrategies.FirstOrDefault(s =>
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

internal static class IngestionPipelineStrategyResolver
{
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdx",
    };

    /// <summary>
    /// Formats whose parsers write Markdown -- headings, tables, slide sections (#597, #599) --
    /// which the configured Semantic chunker re-joins with spaces, flattening the tables. Routed
    /// to the heading-aware chunker unless the caller chose a strategy themselves (#633).
    /// </summary>
    private static readonly HashSet<string> ParsedMarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".pptx", ".html", ".htm",
    };

    public static string Resolve(string fallbackStrategy, string? fileName, bool strategyIsExplicit = false)
    {
        // A record is markdown too, but its shape is the reason it was given this strategy.
        if (IsContentPinned(fallbackStrategy)) return fallbackStrategy;
        if (string.IsNullOrEmpty(fileName)) return fallbackStrategy;
        string ext = System.IO.Path.GetExtension(fileName);
        if (MarkdownExtensions.Contains(ext)) return "DocumentAware";
        return ParsedMarkdownExtensions.Contains(ext) && !strategyIsExplicit ? "DocumentAware" : fallbackStrategy;
    }

    /// <summary>
    /// A strategy chosen for what the content is, not configured: it is kept on reindex and never
    /// counted as stale when the instance's configured strategy changes.
    /// </summary>
    public static bool IsContentPinned(string? strategy) =>
        string.Equals(strategy, nameof(ChunkingStrategy.Record), StringComparison.OrdinalIgnoreCase);
}


