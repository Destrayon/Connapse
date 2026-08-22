using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Llm;
using NSubstitute;

namespace Connapse.Background.Tests.Jobs;

/// <summary>
/// Routing tests for <see cref="SummaryJobs.RollupContainerAsync"/> branching on
/// <see cref="SummarySettings.ContainerSummaryMethod"/>. Each test exercises one
/// branch by setting up just enough state to prove the right collaborator was
/// (or wasn't) called.
/// </summary>
[Trait("Category", "Unit")]
public class SummaryJobsHerculesTests
{
    [Fact]
    public async Task RollupContainerAsync_DocumentClusteringMode_QueriesPooledEmbeddingsNotSummaryEmbeddings()
    {
        // Arrange — 35 docs (above stuff threshold) in document-clustering mode.
        Guid containerId = Guid.NewGuid();

        var docs = Enumerable.Range(0, 35).Select(i => new Document(
            Id: Guid.NewGuid().ToString(),
            ContainerId: containerId.ToString(),
            FileName: $"f{i}.txt",
            ContentType: "text/plain",
            Path: $"/f{i}.txt",
            SizeBytes: 1,
            CreatedAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, string> { ["ContentHash"] = $"hash-{i}" },
            Summary: null,
            SummaryGeneratedAt: null,
            SummaryContentHash: null,
            IngestionState: IngestionState.SummaryIndexed)).ToList();

        var (jobs, mocks) = BuildJobs(out var collected);
        collected.SetupContainer(containerId, summaryDocSetHash: null);
        collected.SetupSettings(containerId, SummaryStrategy.DocumentClustering);
        collected.DocStore.ListAsync(containerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Document>>(docs));
        // Return empty pooled embeddings so the rollup exits early after the call — we only
        // need to prove WHICH collaborator was invoked.
        collected.VectorStore.GetPooledDocumentEmbeddingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Guid DocumentId, float[] Embedding)>>(
                Array.Empty<(Guid, float[])>()));

        // Act
        await jobs.RollupContainerAsync(containerId, CancellationToken.None);

        // Assert: document-clustering path called the pooled-embeddings query, NOT the summary
        // embedding provider. The PerDocSummarizer wasn't called either (no medoids selected).
        await collected.VectorStore.Received(1).GetPooledDocumentEmbeddingsAsync(
            containerId, Arg.Any<CancellationToken>());
        await collected.EmbeddingProvider.DidNotReceiveWithAnyArgs().GetSummaryEmbeddingsAsync(
            default!, default);
        await collected.PerDocSummarizer.DidNotReceiveWithAnyArgs().GenerateAsync(
            default!, default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task RollupContainerAsync_SummaryClusteringMode_DoesNotQueryPooledEmbeddings()
    {
        // Arrange — empty container in summary-clustering mode. Just need to prove the
        // pooled-embeddings query was NOT called.
        Guid containerId = Guid.NewGuid();

        var (jobs, mocks) = BuildJobs(out var collected);
        collected.SetupContainer(containerId, summaryDocSetHash: null);
        collected.SetupSettings(containerId, SummaryStrategy.SummaryClustering);
        collected.DocStore.ListAsync(containerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Document>>(Array.Empty<Document>()));

        // Act
        await jobs.RollupContainerAsync(containerId, CancellationToken.None);

        // Assert: summary-clustering path did NOT touch the pooled-embeddings query.
        await collected.VectorStore.DidNotReceiveWithAnyArgs().GetPooledDocumentEmbeddingsAsync(
            default, default);
    }

    [Fact]
    public async Task RollupDocumentClusteringAsync_CacheHit_SkipsPerDocSummarizerCall()
    {
        // Arrange — 1 doc (≤ StuffThreshold, so we take the stuff regime path and skip the
        // pooled-embeddings query entirely), cache state matches its content hash.
        Guid containerId = Guid.NewGuid();
        string docId = Guid.NewGuid().ToString();
        const string contentHash = "matching-hash";

        var doc = new Document(
            Id: docId,
            ContainerId: containerId.ToString(),
            FileName: "cached.txt",
            ContentType: "text/plain",
            Path: "/cached.txt",
            SizeBytes: 1,
            CreatedAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, string> { ["ContentHash"] = contentHash },
            Summary: "Cached summary text",
            SummaryGeneratedAt: DateTime.UtcNow.AddDays(-1),
            SummaryContentHash: contentHash,
            IngestionState: IngestionState.SummaryIndexed);

        var (jobs, mocks) = BuildJobs(out var collected);
        collected.SetupContainer(containerId, summaryDocSetHash: null);
        collected.SetupSettings(containerId, SummaryStrategy.DocumentClustering);
        collected.DocStore.ListAsync(containerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Document>>(new[] { doc }));

        // Act
        await jobs.RollupContainerAsync(containerId, CancellationToken.None);

        // Assert — cache hit means PerDocSummarizer is NEVER invoked. The reduce step runs
        // and would call the LLM provider, but we don't stub one so it'd return null /
        // produce a skipped result — either way, the summarizer assertion holds.
        await collected.PerDocSummarizer.DidNotReceiveWithAnyArgs().GenerateAsync(
            default!, default!, default!, default!, default!, default!, default);
    }

    // ── Test-only helpers ──────────────────────────────────────────────────

    private static (SummaryJobs Jobs, object _) BuildJobs(out Mocks collected)
    {
        var containerStore = Substitute.For<IContainerStore>();
        var docStore = Substitute.For<IDocumentStore>();
        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        var embeddingProvider = Substitute.For<IDocumentSummaryEmbeddingProvider>();
        var vectorStore = Substitute.For<IVectorStore>();
        var perDocSummarizer = Substitute.For<IPerDocSummarizer>();
        var managedStorage = Substitute.For<IManagedStorageProvider>();
        var parsers = Array.Empty<IDocumentParser>();
        SummaryLlmResolver llmResolver = CreateLlmResolverSubstitute();
        var tokenCounter = Substitute.For<ITokenCounter>();
        var bgClient = Substitute.For<Hangfire.IBackgroundJobClient>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<SummaryJobs>>();

        var jobs = new SummaryJobs(
            containerStore, docStore, settingsResolver, embeddingProvider,
            vectorStore, perDocSummarizer, managedStorage, parsers,
            llmResolver, tokenCounter, bgClient, logger);

        collected = new Mocks(
            containerStore, docStore, settingsResolver, embeddingProvider,
            vectorStore, perDocSummarizer);
        return (jobs, new object());
    }

    private static SummaryLlmResolver CreateLlmResolverSubstitute()
    {
        var optionsMonitor = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<LlmSettings>>();
        optionsMonitor.CurrentValue.Returns(new LlmSettings { Provider = "" });
        var serviceProvider = Substitute.For<IServiceProvider>();
        return new SummaryLlmResolver(optionsMonitor, serviceProvider);
    }

    private sealed record Mocks(
        IContainerStore ContainerStore,
        IDocumentStore DocStore,
        IContainerSettingsResolver SettingsResolver,
        IDocumentSummaryEmbeddingProvider EmbeddingProvider,
        IVectorStore VectorStore,
        IPerDocSummarizer PerDocSummarizer)
    {
        public void SetupContainer(Guid id, string? summaryDocSetHash)
        {
            ContainerStore.GetAsync(id, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Container?>(new Container(
                    Id: id.ToString(),
                    Name: "test",
                    Description: null,
                    CreatedAt: DateTime.UtcNow,
                    UpdatedAt: DateTime.UtcNow,
                    DocumentCount: 0,
                    SettingsOverrides: null,
                    Summary: null,
                    SummaryGeneratedAt: null,
                    SummaryDocSetHash: summaryDocSetHash)));
        }

        public void SetupSettings(Guid containerId, string method)
        {
            SettingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SummarySettings
                {
                    Enabled = true,
                    ContainerSummaryMethod = method,
                }));
        }
    }
}
