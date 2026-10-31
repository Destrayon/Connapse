using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Llm;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Background.Tests.Jobs;

/// <summary>
/// CON-176: a document-clustering rollup looks at the whole folder, however large, and tells the
/// model how many documents the folder really holds.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class SummaryJobsWholeFolderTests
{
    private readonly Guid _containerId = Guid.NewGuid();
    private readonly IContainerStore _containers = Substitute.For<IContainerStore>();
    private readonly IDocumentStore _docs = Substitute.For<IDocumentStore>();
    private readonly IVectorStore _vectors = Substitute.For<IVectorStore>();
    private readonly IPerDocSummarizer _perDoc = Substitute.For<IPerDocSummarizer>();
    private readonly ILlmProvider _llm = Substitute.For<ILlmProvider>();
    private readonly List<string> _userMessages = [];
    private readonly SummaryJobs _jobs;

    public SummaryJobsWholeFolderTests()
    {
        _containers.GetAsync(_containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Container?>(new Container(
                Id: _containerId.ToString(), Name: "big folder", Description: null,
                CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow, DocumentCount: 0,
                SettingsOverrides: null, Summary: null, SummaryGeneratedAt: null,
                SummaryDocSetHash: "previous-hash")));

        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        settingsResolver.GetSummarySettingsAsync(_containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SummarySettings
            {
                Enabled = true,
                ContainerSummaryMethod = SummaryStrategy.DocumentClustering,
            }));

        _llm.ModelId.Returns("test-model");
        _llm.CompleteAsync(Arg.Any<string>(), Arg.Do<string>(_userMessages.Add),
                Arg.Any<LlmCompletionOptions?>(), Arg.Any<CancellationToken>())
            .Returns("folder summary");

        var optionsMonitor = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<LlmSettings>>();
        optionsMonitor.CurrentValue.Returns(new LlmSettings { Provider = "" });
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ILlmProvider)).Returns(_llm);

        _jobs = new SummaryJobs(
            _containers, _docs, settingsResolver, Substitute.For<IDocumentSummaryEmbeddingProvider>(),
            _vectors, _perDoc, Substitute.For<IManagedStorageProvider>(), Array.Empty<IDocumentParser>(),
            new SummaryLlmResolver(optionsMonitor, serviceProvider), Substitute.For<ITokenCounter>(),
            Substitute.For<Hangfire.IBackgroundJobClient>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<SummaryJobs>>());
    }

    [Fact]
    public async Task RollupContainerAsync_FolderOf50000Docs_SamplesWholeFolderAndTellsModelRealCount()
    {
        SetupFingerprint(50_000, "new-hash");
        IReadOnlyList<(Guid DocumentId, float[] Embedding)> sample = PooledSample(60);
        _vectors.GetPooledDocumentEmbeddingsAsync(_containerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(sample));
        _docs.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<Document?>(CachedDoc(call.Arg<string>())));

        await _jobs.RollupContainerAsync(_containerId, CancellationToken.None);

        await _vectors.Received(1).GetPooledDocumentEmbeddingsAsync(
            _containerId, SummaryJobs.PooledSampleSize, Arg.Any<CancellationToken>());
        await _docs.DidNotReceiveWithAnyArgs().ListAsync(default, default, default, default, default);
        await _docs.Received(20).GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        _userMessages.Should().ContainSingle();
        _userMessages[0].Should().Contain("50000 documents total")
            .And.Contain("SAMPLE of 20 documents drawn from across the whole container")
            .And.NotContain("all documents are shown");

        await _containers.Received(1).UpdateSummaryAsync(
            _containerId, "folder summary", Arg.Any<DateTime?>(), "new-hash", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RollupContainerAsync_FingerprintMatchesStoredHash_SkipsWithoutSampling()
    {
        SetupFingerprint(50_000, "previous-hash");

        await _jobs.RollupContainerAsync(_containerId, CancellationToken.None);

        await _vectors.DidNotReceiveWithAnyArgs().GetPooledDocumentEmbeddingsAsync(default, default, default);
        await _docs.DidNotReceiveWithAnyArgs().ListAsync(default, default, default, default, default);
        await _containers.DidNotReceiveWithAnyArgs().UpdateSummaryAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task RollupContainerAsync_EmptyFolder_ClearsContainerSummary()
    {
        SetupFingerprint(0, "empty-hash");

        await _jobs.RollupContainerAsync(_containerId, CancellationToken.None);

        await _containers.Received(1).UpdateSummaryAsync(_containerId, null, null, null, Arg.Any<CancellationToken>());
        _userMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task RollupContainerAsync_SmallFolder_ListsUpToStuffThresholdAndSaysAllShown()
    {
        SetupFingerprint(3, "new-hash");
        IReadOnlyList<Document> three = Enumerable.Range(0, 3)
            .Select(_ => CachedDoc(Guid.NewGuid().ToString())).ToList();
        _docs.ListAsync(_containerId, null, 0, 30, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(three));

        await _jobs.RollupContainerAsync(_containerId, CancellationToken.None);

        await _vectors.DidNotReceiveWithAnyArgs().GetPooledDocumentEmbeddingsAsync(default, default, default);
        _userMessages.Should().ContainSingle();
        _userMessages[0].Should().Contain("3 documents total; all documents are shown below");
    }

    [Fact]
    public async Task RollupContainerAsync_MedoidsDeletedMeanwhile_SkipsWithoutWritingSummary()
    {
        SetupFingerprint(50_000, "new-hash");
        IReadOnlyList<(Guid DocumentId, float[] Embedding)> sample = PooledSample(60);
        _vectors.GetPooledDocumentEmbeddingsAsync(_containerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(sample));
        _docs.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Document?>(null));

        await _jobs.RollupContainerAsync(_containerId, CancellationToken.None);

        _userMessages.Should().BeEmpty();
        await _containers.DidNotReceiveWithAnyArgs().UpdateSummaryAsync(default, default, default, default, default);
    }

    private void SetupFingerprint(int count, string hash) =>
        _docs.GetDocumentSetFingerprintAsync(_containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DocumentSetFingerprint(count, hash)));

    private static IReadOnlyList<(Guid DocumentId, float[] Embedding)> PooledSample(int n)
    {
        Random rng = new(176);
        return Enumerable.Range(0, n)
            .Select(_ => (Guid.NewGuid(), Enumerable.Range(0, 8).Select(_ => (float)rng.NextDouble()).ToArray()))
            .ToList();
    }

    // A document whose summary is already cached for its content, so the rollup never re-parses it.
    private Document CachedDoc(string id) => new(
        Id: id,
        ContainerId: _containerId.ToString(),
        FileName: $"{id}.txt",
        ContentType: "text/plain",
        Path: $"/{id}.txt",
        SizeBytes: 1,
        CreatedAt: DateTime.UtcNow,
        Metadata: new Dictionary<string, string> { ["ContentHash"] = $"hash-{id}" },
        Summary: $"Summary of {id}",
        SummaryGeneratedAt: DateTime.UtcNow,
        SummaryContentHash: $"hash-{id}",
        Status: DocumentStatus.Ready, SummaryStatus: SummaryStatus.Done);
}
