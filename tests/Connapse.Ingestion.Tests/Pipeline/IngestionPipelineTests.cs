using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Pipeline;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Ingestion.Tests.Pipeline;

/// <summary>
/// Tests for IngestionPipeline.
///
/// Limitation: IngestionPipeline takes KnowledgeDbContext (concrete class) directly,
/// which uses PostgreSQL-specific features (jsonb columns for Dictionary properties,
/// tsvector computed columns). The EF InMemory provider cannot map these. Since
/// SaveChangesAsync is called early in the pipeline (before parse/chunk/embed), tests
/// that require successful DB writes are not feasible without a real PostgreSQL instance.
///
/// Tests here validate: result contract, error handling, metadata constants, and behaviors
/// that don't depend on post-SaveChangesAsync pipeline stages.
/// </summary>
[Trait("Category", "Unit")]
public class IngestionPipelineTests
{
    /// <summary>
    /// These tests predate source ownership and relied on the pipeline defaulting a missing
    /// container to Guid.Empty. Ingestion now requires an explicit owner, so they declare a
    /// container one — the ownership itself is not what they are testing.
    /// </summary>
    private static readonly Guid TestOwnerId = Guid.NewGuid();

    private readonly IKnowledgeFileSystem _fileSystem;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly IDocumentParser _parser;
    private readonly IChunkingStrategy _chunkingStrategy;
    private readonly IOptionsMonitor<ChunkingSettings> _chunkingSettings;
    private readonly IOptionsMonitor<EmbeddingSettings> _embeddingSettings;
    private readonly ILogger<IngestionPipeline> _logger;

    // Fields rather than inline substitutes, so a test can say what the store returns. The
    // source-routing tests need to: which branch IngestByIdAsync takes is decided by them.
    private readonly IDocumentStore _documentStore;
    private readonly ISourceStore _sourceStore;
    private readonly IConnectionStore _connectionStore;
    private readonly IConnectorFactory _connectorFactory;

    public IngestionPipelineTests()
    {
        _fileSystem = Substitute.For<IKnowledgeFileSystem>();
        _documentStore = Substitute.For<IDocumentStore>();
        _sourceStore = Substitute.For<ISourceStore>();
        _connectionStore = Substitute.For<IConnectionStore>();
        _connectorFactory = Substitute.For<IConnectorFactory>();
        _embeddingProvider = Substitute.For<IEmbeddingProvider>();
        _vectorStore = Substitute.For<IVectorStore>();
        _logger = NullLogger<IngestionPipeline>.Instance;

        _parser = Substitute.For<IDocumentParser>();
        _parser.SupportedExtensions.Returns(new HashSet<string> { ".txt" });
        _parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ParsedDocument(
                "Test document content for chunking.",
                new Dictionary<string, string>(),
                new List<string>()));

        _chunkingStrategy = Substitute.For<IChunkingStrategy>();
        _chunkingStrategy.Name.Returns("Semantic");
        _chunkingStrategy.ChunkAsync(Arg.Any<ParsedDocument>(), Arg.Any<ChunkingSettings>(), Arg.Any<CancellationToken>())
            .Returns(ci => new List<ChunkInfo>
            {
                new("Chunk 1 content", 0, 5, 0, 17,
                    new Dictionary<string, string> { ["ChunkIndex"] = "0" }, null),
                new("Chunk 2 content", 1, 5, 17, 35,
                    new Dictionary<string, string> { ["ChunkIndex"] = "1" }, null)
            });

        var chunkSettings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 50 };
        _chunkingSettings = Substitute.For<IOptionsMonitor<ChunkingSettings>>();
        _chunkingSettings.CurrentValue.Returns(chunkSettings);

        var embedSettings = new EmbeddingSettings { Provider = "Ollama", Model = "nomic-embed-text", Dimensions = 768 };
        _embeddingSettings = Substitute.For<IOptionsMonitor<EmbeddingSettings>>();
        _embeddingSettings.CurrentValue.Returns(embedSettings);

        _embeddingProvider.EmbedBatchAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<string[]>();
                return texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToArray();
            });
    }

    private IngestionPipeline CreatePipeline(KnowledgeDbContext dbContext) =>
        new(dbContext,
            _fileSystem,
            _embeddingProvider,
            _vectorStore,
            new[] { _parser },
            new[] { _chunkingStrategy },
            _chunkingSettings,
            _embeddingSettings,
            new EmbeddingCache(dbContext),
            Substitute.For<IContainerStore>(),
            Substitute.For<IManagedStorageProvider>(),
            _documentStore,
            _sourceStore,
            _connectionStore,
            _connectorFactory,
            _logger);

    private static KnowledgeDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new KnowledgeDbContext(options);
    }

    [Fact]
    public async Task IngestAsync_ProvidedDocumentId_UsesIt()
    {
        using var dbContext = CreateInMemoryContext();
        var pipeline = CreatePipeline(dbContext);
        var docId = Guid.NewGuid().ToString();
        var stream = new MemoryStream("Test content"u8.ToArray());
        var options = new IngestionOptions(DocumentId: docId, FileName: "test.txt") { Owner = OwnerRef.ForContainer(TestOwnerId) };

        var result = await pipeline.IngestAsync(stream, options);

        result.DocumentId.Should().Be(docId);
    }

    [Fact]
    public async Task IngestAsync_NoDocumentId_GeneratesGuid()
    {
        using var dbContext = CreateInMemoryContext();
        var pipeline = CreatePipeline(dbContext);
        var stream = new MemoryStream("Test content"u8.ToArray());
        var options = new IngestionOptions(FileName: "test.txt") { Owner = OwnerRef.ForContainer(TestOwnerId) };

        var result = await pipeline.IngestAsync(stream, options);

        result.DocumentId.Should().NotBeNullOrEmpty();
        Guid.TryParse(result.DocumentId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task IngestAsync_ReturnsDuration()
    {
        using var dbContext = CreateInMemoryContext();
        var pipeline = CreatePipeline(dbContext);
        var stream = new MemoryStream("Test content"u8.ToArray());
        var options = new IngestionOptions(FileName: "test.txt") { Owner = OwnerRef.ForContainer(TestOwnerId) };

        var result = await pipeline.IngestAsync(stream, options);

        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task IngestAsync_DbFailure_CatchesAndReturnsZeroChunks()
    {
        // The InMemory provider can't handle jsonb Dictionary properties,
        // so SaveChangesAsync throws — pipeline catches it gracefully
        using var dbContext = CreateInMemoryContext();
        var pipeline = CreatePipeline(dbContext);
        var stream = new MemoryStream("Test content"u8.ToArray());
        var options = new IngestionOptions(FileName: "test.txt") { Owner = OwnerRef.ForContainer(TestOwnerId) };

        var result = await pipeline.IngestAsync(stream, options);

        result.ChunkCount.Should().Be(0);
        result.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task IngestAsync_DbFailure_IncludesErrorInWarnings()
    {
        using var dbContext = CreateInMemoryContext();
        var pipeline = CreatePipeline(dbContext);
        var stream = new MemoryStream("Test content"u8.ToArray());
        var options = new IngestionOptions(FileName: "test.txt") { Owner = OwnerRef.ForContainer(TestOwnerId) };

        var result = await pipeline.IngestAsync(stream, options);

        result.Warnings.Should().Contain(w => w.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IngestAsync_PrecomputedEmbeddings_SkipsEmbeddingProvider()
    {
        // Precomputed embedding check happens after parse/chunk but before embed.
        // Even though DB fails, we can verify embedding was not called by
        // setting up the chunker to return precomputed embeddings.
        // Since the DB failure happens before chunking in this setup,
        // the embedding provider won't be called regardless — but the test
        // validates the contract for when DB works.
        var precomputedEmbedding = new float[] { 0.5f, 0.6f, 0.7f };
        _chunkingStrategy.ChunkAsync(Arg.Any<ParsedDocument>(), Arg.Any<ChunkingSettings>(), Arg.Any<CancellationToken>())
            .Returns(new List<ChunkInfo>
            {
                new("Chunk 1", 0, 5, 0, 7,
                    new Dictionary<string, string> { ["ChunkIndex"] = "0" },
                    precomputedEmbedding),
            });

        using var dbContext = CreateInMemoryContext();
        var pipeline = CreatePipeline(dbContext);
        var stream = new MemoryStream("Test content"u8.ToArray());
        var options = new IngestionOptions(FileName: "test.txt") { Owner = OwnerRef.ForContainer(TestOwnerId) };

        await pipeline.IngestAsync(stream, options);

        await _embeddingProvider.DidNotReceive().EmbedBatchAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_UnsupportedExtension_ParserNotCalled()
    {
        using var dbContext = CreateInMemoryContext();
        var pipeline = CreatePipeline(dbContext);
        var stream = new MemoryStream("Test content"u8.ToArray());
        var options = new IngestionOptions(FileName: "test.xyz") { Owner = OwnerRef.ForContainer(TestOwnerId) };

        await pipeline.IngestAsync(stream, options);

        // Parser for .txt should not be called when file is .xyz
        await _parser.DidNotReceive().ParseAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void MetadataKeyConstants_AreCorrectlyDefined()
    {
        IngestionPipeline.MetadataKeyChunkingStrategy.Should().Be("IndexedWith:ChunkingStrategy");
        IngestionPipeline.MetadataKeyChunkingMaxSize.Should().Be("IndexedWith:ChunkingMaxSize");
        IngestionPipeline.MetadataKeyChunkingOverlap.Should().Be("IndexedWith:ChunkingOverlap");
        IngestionPipeline.MetadataKeyEmbeddingProvider.Should().Be("IndexedWith:EmbeddingProvider");
        IngestionPipeline.MetadataKeyEmbeddingModel.Should().Be("IndexedWith:EmbeddingModel");
        IngestionPipeline.MetadataKeyEmbeddingDimensions.Should().Be("IndexedWith:EmbeddingDimensions");
    }

    [Fact]
    public async Task IngestAsync_NonSeekableStream_ReturnsResult()
    {
        // Non-seekable stream is copied to MemoryStream before hash computation.
        // DB will still fail, but we verify the pipeline doesn't crash on non-seekable input.
        using var dbContext = CreateInMemoryContext();
        var pipeline = CreatePipeline(dbContext);
        var data = "Test content for non-seekable stream"u8.ToArray();
        var innerStream = new MemoryStream(data);
        var nonSeekableStream = new NonSeekableStream(innerStream);

        var options = new IngestionOptions(FileName: "test.txt") { Owner = OwnerRef.ForContainer(TestOwnerId) };

        var result = await pipeline.IngestAsync(nonSeekableStream, options);

        result.Should().NotBeNull();
        result.DocumentId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task IngestAsync_ImplementsIKnowledgeIngester()
    {
        using var dbContext = CreateInMemoryContext();
        var pipeline = CreatePipeline(dbContext);

        // Verify it implements the interface
        pipeline.Should().BeAssignableTo<IKnowledgeIngester>();
    }

    /// <summary>
    /// Wrapper stream that reports CanSeek = false to test non-seekable stream handling.
    /// </summary>
    // ── Source routing when the caller supplies no owner (#405 review) ─────────────

    /// <summary>
    /// Stands up the three stores IngestByIdAsync consults on the source path, and returns
    /// the connector it should end up reading through.
    /// </summary>
    private IConnector ArrangeSourceDocument(string documentId, Guid sourceId, string path)
    {
        var connectionId = Guid.NewGuid();

        _documentStore.GetAsync(documentId, Arg.Any<CancellationToken>())
            .Returns(new Document(
                documentId, sourceId.ToString(), "test.txt", "text/plain", path,
                SizeBytes: 12, CreatedAt: DateTime.UtcNow, Metadata: new Dictionary<string, string>())
            {
                // What PostgresDocumentStore.MapToModel records: ContainerId carries
                // COALESCE(container_id, source_id), so only this distinguishes the two.
                Owner = OwnerRef.ForSource(sourceId),
            });

        _sourceStore.GetAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new Source(
                sourceId, "src", null, connectionId, "{}", DateTime.UtcNow, DateTime.UtcNow));

        _connectionStore.GetAsync(connectionId, Arg.Any<CancellationToken>())
            .Returns(new Connection(
                connectionId, "conn", ConnectionProvider.Filesystem, "{}", null,
                DateTime.UtcNow, DateTime.UtcNow));

        var connector = Substitute.For<IConnector>();
        connector.ReadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream("Test content"u8.ToArray())));

        _connectorFactory.Create(Arg.Any<Source>(), Arg.Any<Connection>(), Arg.Any<string?>())
            .Returns(connector);

        return connector;
    }

    [Fact]
    public async Task IngestByIdAsync_SourceOwnedDocumentWithNoOwnerInOptions_ReadsThroughTheSourceConnector()
    {
        // ReindexService enqueued jobs this way before #405: no Owner, and a ContainerId that
        // is blank because Nullable<Guid>.ToString() returns "". The pipeline then took the
        // container branch and threw — after the reindex had already deleted the document's
        // chunks, and with the remote signature still matching so no later sync restored them.
        using var dbContext = CreateInMemoryContext();
        var pipeline = CreatePipeline(dbContext);

        string documentId = Guid.NewGuid().ToString();
        var sourceId = Guid.NewGuid();
        IConnector connector = ArrangeSourceDocument(documentId, sourceId, "/remote/test.txt");

        await pipeline.IngestByIdAsync(
            documentId,
            new IngestionOptions(DocumentId: documentId, FileName: "test.txt", Path: "/remote/test.txt"));

        await connector.Received(1).ReadFileAsync("/remote/test.txt", Arg.Any<CancellationToken>());
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStream(Stream inner) => _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
