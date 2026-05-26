using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Llm;
using NSubstitute;

namespace Connapse.Background.Tests.Jobs;

[Trait("Category", "Unit")]
public class SummaryJobsTests
{
    [Fact]
    public async Task RollupContainerAsync_WhenEnabledFalse_ShortCircuits()
    {
        var containerStore = Substitute.For<IContainerStore>();
        var docStore = Substitute.For<IDocumentStore>();
        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        var embeddingProvider = Substitute.For<IDocumentSummaryEmbeddingProvider>();
        SummaryLlmResolver llmResolver = CreateLlmResolverSubstitute();
        var tokenCounter = Substitute.For<ITokenCounter>();
        var bgClient = Substitute.For<Hangfire.IBackgroundJobClient>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<SummaryJobs>>();

        Guid containerId = Guid.NewGuid();

        containerStore.GetAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Container?>(new Container(
                Id: containerId.ToString(),
                Name: "test",
                Description: null,
                ConnectorType: ConnectorType.Filesystem,
                CreatedAt: DateTime.UtcNow,
                UpdatedAt: DateTime.UtcNow,
                DocumentCount: 5,
                SettingsOverrides: null,
                ConnectorConfig: null,
                Summary: null,
                SummaryGeneratedAt: null,
                SummaryDocSetHash: null)));

        settingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SummarySettings { Enabled = false }));

        var jobs = new SummaryJobs(
            containerStore, docStore, settingsResolver, embeddingProvider,
            llmResolver, tokenCounter, bgClient, logger);

        await jobs.RollupContainerAsync(containerId, CancellationToken.None);

        await docStore.DidNotReceive().ListAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RollupContainerAsync_WhenDocSetHashMatches_SkipsLlmCall()
    {
        var containerStore = Substitute.For<IContainerStore>();
        var docStore = Substitute.For<IDocumentStore>();
        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        var embeddingProvider = Substitute.For<IDocumentSummaryEmbeddingProvider>();
        SummaryLlmResolver llmResolver = CreateLlmResolverSubstitute();
        var tokenCounter = Substitute.For<ITokenCounter>();
        var bgClient = Substitute.For<Hangfire.IBackgroundJobClient>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<SummaryJobs>>();

        Guid containerId = Guid.NewGuid();
        string docId = Guid.NewGuid().ToString();
        const string docSummary = "Summary text";
        string expectedHash = ComputeExpectedHash(docId, docSummary);

        containerStore.GetAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Container?>(new Container(
                Id: containerId.ToString(),
                Name: "test",
                Description: null,
                ConnectorType: ConnectorType.Filesystem,
                CreatedAt: DateTime.UtcNow,
                UpdatedAt: DateTime.UtcNow,
                DocumentCount: 1,
                SettingsOverrides: null,
                ConnectorConfig: null,
                Summary: "Old summary",
                SummaryGeneratedAt: DateTime.UtcNow.AddHours(-1),
                SummaryDocSetHash: expectedHash)));

        settingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SummarySettings { Enabled = true }));

        docStore.ListAsync(containerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Document>>(new List<Document>
            {
                new Document(
                    Id: docId,
                    ContainerId: containerId.ToString(),
                    FileName: "a.txt",
                    ContentType: "text/plain",
                    Path: "/a.txt",
                    SizeBytes: 100,
                    CreatedAt: DateTime.UtcNow,
                    Metadata: new Dictionary<string, string>(),
                    Summary: docSummary,
                    SummaryGeneratedAt: DateTime.UtcNow,
                    SummaryContentHash: "abc",
                    IngestionState: IngestionState.SummaryIndexed)
            }));

        var jobs = new SummaryJobs(
            containerStore, docStore, settingsResolver, embeddingProvider,
            llmResolver, tokenCounter, bgClient, logger);

        await jobs.RollupContainerAsync(containerId, CancellationToken.None);

        // No embedding fetch, no LLM-call attempt — short-circuited on doc_set_hash_match.
        await embeddingProvider.DidNotReceive().GetSummaryEmbeddingsAsync(
            Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>());
        await containerStore.DidNotReceive().UpdateSummaryAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTime?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepStaleContainersAsync_EnqueuesRollupForEachStaleContainer()
    {
        var containerStore = Substitute.For<IContainerStore>();
        var docStore = Substitute.For<IDocumentStore>();
        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        var embeddingProvider = Substitute.For<IDocumentSummaryEmbeddingProvider>();
        SummaryLlmResolver llmResolver = CreateLlmResolverSubstitute();
        var tokenCounter = Substitute.For<ITokenCounter>();
        var bgClient = Substitute.For<Hangfire.IBackgroundJobClient>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<SummaryJobs>>();

        var staleIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        docStore.FindContainersWithStaleSummariesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(staleIds));

        var jobs = new SummaryJobs(
            containerStore, docStore, settingsResolver, embeddingProvider,
            llmResolver, tokenCounter, bgClient, logger);

        await jobs.SweepStaleContainersAsync(CancellationToken.None);

        // One Enqueue per stale container.
        bgClient.Received(3).Create(
            Arg.Any<Hangfire.Common.Job>(),
            Arg.Is<Hangfire.States.IState>(s => s is Hangfire.States.EnqueuedState));
    }

    private static SummaryLlmResolver CreateLlmResolverSubstitute()
    {
        var optionsMonitor = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<LlmSettings>>();
        optionsMonitor.CurrentValue.Returns(new LlmSettings { Provider = "" });
        var serviceProvider = Substitute.For<IServiceProvider>();
        return new SummaryLlmResolver(optionsMonitor, serviceProvider);
    }

    private static string ComputeExpectedHash(string docId, string summary) =>
        // Must match SummaryJobs.ComputeDocSetHash logic exactly.
        HexHash.Sha256(string.Join("\n", new[] { $"{docId}|{HexHash.Sha256(summary)}" }));
}
