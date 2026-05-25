using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Summarization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Ingestion.Tests.Summarization;

[Trait("Category", "Unit")]
public class ContainerSummaryWorkerTests
{
    private static Document MakeDoc(string id, string summary) =>
        new(
            Id: id,
            ContainerId: Guid.NewGuid().ToString(),
            FileName: "test.txt",
            ContentType: "text/plain",
            Path: $"/{id}.txt",
            SizeBytes: 100,
            CreatedAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, string>(),
            Summary: summary,
            SummaryGeneratedAt: DateTime.UtcNow,
            SummaryContentHash: null);

    // -----------------------------------------------------------------------
    // ComputeDocSetHash unit tests (static, no IO)
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeDocSetHash_IsDeterministic_ForSameInput()
    {
        List<Document> docs =
        [
            MakeDoc("aaa", "alpha summary"),
            MakeDoc("bbb", "beta summary"),
        ];

        string hash1 = ContainerSummaryWorker.ComputeDocSetHash(docs);
        string hash2 = ContainerSummaryWorker.ComputeDocSetHash(docs);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeDocSetHash_IsOrderIndependent()
    {
        string id1 = Guid.NewGuid().ToString();
        string id2 = Guid.NewGuid().ToString();

        List<Document> docs1 = [MakeDoc(id1, "alpha"), MakeDoc(id2, "beta")];
        List<Document> docs2 = [MakeDoc(id2, "beta"), MakeDoc(id1, "alpha")];

        string hash1 = ContainerSummaryWorker.ComputeDocSetHash(docs1);
        string hash2 = ContainerSummaryWorker.ComputeDocSetHash(docs2);

        hash1.Should().Be(hash2, "hash must be independent of input order");
    }

    [Fact]
    public void ComputeDocSetHash_DiffersWhenSummaryChanges()
    {
        string id = Guid.NewGuid().ToString();
        Document docBefore = MakeDoc(id, "original summary");
        Document docAfter = MakeDoc(id, "updated summary");

        string hashBefore = ContainerSummaryWorker.ComputeDocSetHash([docBefore]);
        string hashAfter = ContainerSummaryWorker.ComputeDocSetHash([docAfter]);

        hashBefore.Should().NotBe(hashAfter);
    }

    [Fact]
    public void ComputeDocSetHash_DiffersWhenDocAdded()
    {
        string id1 = Guid.NewGuid().ToString();
        string id2 = Guid.NewGuid().ToString();

        List<Document> oneDocs = [MakeDoc(id1, "alpha")];
        List<Document> twoDocs = [MakeDoc(id1, "alpha"), MakeDoc(id2, "beta")];

        string hashOne = ContainerSummaryWorker.ComputeDocSetHash(oneDocs);
        string hashTwo = ContainerSummaryWorker.ComputeDocSetHash(twoDocs);

        hashOne.Should().NotBe(hashTwo);
    }

    // -----------------------------------------------------------------------
    // ProcessContainerAsync — hash-gate skip (uses real ServiceProvider)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProcessContainerAsync_SkipsRollup_WhenDocSetHashUnchanged()
    {
        Guid containerId = Guid.NewGuid();
        string docId = Guid.NewGuid().ToString();
        Document doc = MakeDoc(docId, "some summary");
        string existingHash = ContainerSummaryWorker.ComputeDocSetHash([doc]);

        Container container = new(
            Id: containerId.ToString(),
            Name: "Test Container",
            Description: null,
            ConnectorType: ConnectorType.ManagedStorage,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            SummaryDocSetHash: existingHash);

        IContainerStore containerStore = Substitute.For<IContainerStore>();
        IDocumentStore documentStore = Substitute.For<IDocumentStore>();
        IContainerSummarizer summarizer = Substitute.For<IContainerSummarizer>();
        IDocumentSummaryEmbeddingProvider embeddingProvider =
            Substitute.For<IDocumentSummaryEmbeddingProvider>();

        containerStore.GetAsync(containerId, Arg.Any<CancellationToken>()).Returns(container);
        documentStore.ListAsync(containerId, null, 0, 10_000, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Document>)[doc]);

        // Use a real ServiceProvider so CreateAsyncScope() works correctly.
        ServiceCollection services = new();
        services.AddSingleton(containerStore);
        services.AddSingleton(documentStore);
        services.AddSingleton(summarizer);
        services.AddSingleton(embeddingProvider);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        IContainerSummaryQueue queue = Substitute.For<IContainerSummaryQueue>();
        ContainerSummaryWorker worker = new(
            queue,
            scopeFactory,
            NullLogger<ContainerSummaryWorker>.Instance);

        worker.SetDirtyForTest(containerId, 1);

        await worker.ProcessContainerAsync(containerId, "test", CancellationToken.None);

        // Summarizer must NOT be called when hash matches
        await summarizer.DidNotReceiveWithAnyArgs()
            .GenerateAsync(default!, default!, default);
        await containerStore.DidNotReceiveWithAnyArgs()
            .UpdateSummaryAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task ProcessContainerAsync_CallsSummarizer_WhenHashDiffers()
    {
        Guid containerId = Guid.NewGuid();
        string docId = Guid.NewGuid().ToString();
        Document doc = MakeDoc(docId, "some summary");

        Container container = new(
            Id: containerId.ToString(),
            Name: "Test Container",
            Description: null,
            ConnectorType: ConnectorType.ManagedStorage,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            SummaryDocSetHash: "stale_hash_that_will_differ");

        IContainerStore containerStore = Substitute.For<IContainerStore>();
        IDocumentStore documentStore = Substitute.For<IDocumentStore>();
        IContainerSummarizer summarizer = Substitute.For<IContainerSummarizer>();
        IDocumentSummaryEmbeddingProvider embeddingProvider =
            Substitute.For<IDocumentSummaryEmbeddingProvider>();

        containerStore.GetAsync(containerId, Arg.Any<CancellationToken>()).Returns(container);
        documentStore.ListAsync(containerId, null, 0, 10_000, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Document>)[doc]);
        embeddingProvider
            .GetSummaryEmbeddingsAsync(Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DocumentWithSummary>)
                [new DocumentWithSummary(Guid.Parse(docId), "some summary", [0.1f, 0.2f])]);
        summarizer.GenerateAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<DocumentWithSummary>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ContainerSummarizationResult(
                Skipped: false, Summary: "generated summary", Regime: "stuff", NumDocs: 1));

        ServiceCollection services = new();
        services.AddSingleton(containerStore);
        services.AddSingleton(documentStore);
        services.AddSingleton(summarizer);
        services.AddSingleton(embeddingProvider);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        IContainerSummaryQueue queue = Substitute.For<IContainerSummaryQueue>();
        ContainerSummaryWorker worker = new(
            queue,
            scopeFactory,
            NullLogger<ContainerSummaryWorker>.Instance);

        worker.SetDirtyForTest(containerId, 5);

        await worker.ProcessContainerAsync(containerId, "test", CancellationToken.None);

        await containerStore.Received(1).UpdateSummaryAsync(
            containerId,
            "generated summary",
            Arg.Any<DateTime?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
