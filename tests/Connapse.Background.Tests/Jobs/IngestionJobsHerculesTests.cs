using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using NSubstitute;

namespace Connapse.Background.Tests.Jobs;

/// <summary>
/// Unit tests for the <see cref="SummaryStrategy.DocumentClustering"/> early-return
/// branch in <see cref="IngestionJobs.PerDocSummaryAsync"/>. In document-clustering
/// mode, per-doc summarization is deferred to rollup time, so the job must short-circuit
/// without calling the summarizer while still advancing ingestion state so the UI
/// doesn't show a stuck spinner.
/// </summary>
[Trait("Category", "Unit")]
public class IngestionJobsHerculesTests
{
    [Fact]
    public async Task PerDocSummaryAsync_DocumentClusteringMode_EarlyReturnsWithoutCallingSummarizer()
    {
        // Arrange
        string documentId = Guid.NewGuid().ToString();
        Guid containerId = Guid.NewGuid();

        var docStore = Substitute.For<IDocumentStore>();
        docStore.GetAsync(documentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Document?>(new Document(
                Id: documentId,
                ContainerId: containerId.ToString(),
                FileName: "doc.txt",
                ContentType: "text/plain",
                Path: "/doc.txt",
                SizeBytes: 100,
                CreatedAt: DateTime.UtcNow,
                Metadata: new Dictionary<string, string>(),
                Summary: null,
                SummaryGeneratedAt: null,
                SummaryContentHash: null,
                IngestionState: IngestionState.Indexed)));

        var summarizer = Substitute.For<IPerDocSummarizer>();
        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        settingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SummarySettings
            {
                Enabled = true,
                ContainerSummaryMethod = SummaryStrategy.DocumentClustering,
            }));

        var stateBroadcaster = Substitute.For<IIngestionStateBroadcaster>();
        var jobs = new IngestionJobs(
            Substitute.For<IKnowledgeIngester>(),
            docStore,
            Substitute.For<IContainerStore>(),
            Substitute.For<IConnectorFactory>(),
            Array.Empty<IDocumentParser>(),
            summarizer,
            settingsResolver,
            Substitute.For<Hangfire.IBackgroundJobClient>(),
            stateBroadcaster,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<IngestionJobs>>());

        // Act
        await jobs.PerDocSummaryAsync(documentId, CancellationToken.None);

        // Assert — summarizer is never invoked.
        await summarizer.DidNotReceiveWithAnyArgs().GenerateAsync(
            default!, default!, default!, default!, default!, default!, default);

        // State is still advanced to SummaryIndexed so the UI doesn't hang.
        await docStore.Received(1).UpdateIngestionStateAsync(
            documentId, IngestionState.SummaryIndexed, Arg.Any<CancellationToken>());
        await stateBroadcaster.Received(1).BroadcastIngestionStateChangedAsync(
            documentId, IngestionState.SummaryIndexed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerDocSummaryAsync_SummariesDisabled_DocumentClusteringStillEarlyReturnsByDisabledFlag()
    {
        // Sanity: Enabled=false short-circuits BEFORE the ContainerSummaryMethod check.
        // No state advance happens; the disabled path is a hard no-op.
        string documentId = Guid.NewGuid().ToString();
        Guid containerId = Guid.NewGuid();

        var docStore = Substitute.For<IDocumentStore>();
        docStore.GetAsync(documentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Document?>(new Document(
                Id: documentId,
                ContainerId: containerId.ToString(),
                FileName: "doc.txt",
                ContentType: "text/plain",
                Path: "/doc.txt",
                SizeBytes: 100,
                CreatedAt: DateTime.UtcNow,
                Metadata: new Dictionary<string, string>(),
                Summary: null,
                SummaryGeneratedAt: null,
                SummaryContentHash: null,
                IngestionState: IngestionState.Indexed)));

        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        settingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SummarySettings
            {
                Enabled = false,
                ContainerSummaryMethod = SummaryStrategy.DocumentClustering,
            }));

        var stateBroadcaster = Substitute.For<IIngestionStateBroadcaster>();
        var summarizer = Substitute.For<IPerDocSummarizer>();
        var jobs = new IngestionJobs(
            Substitute.For<IKnowledgeIngester>(),
            docStore,
            Substitute.For<IContainerStore>(),
            Substitute.For<IConnectorFactory>(),
            Array.Empty<IDocumentParser>(),
            summarizer,
            settingsResolver,
            Substitute.For<Hangfire.IBackgroundJobClient>(),
            stateBroadcaster,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<IngestionJobs>>());

        await jobs.PerDocSummaryAsync(documentId, CancellationToken.None);

        await summarizer.DidNotReceiveWithAnyArgs().GenerateAsync(
            default!, default!, default!, default!, default!, default!, default);
        // Disabled path does NOT advance state — that's the documented behavior.
        await docStore.DidNotReceiveWithAnyArgs().UpdateIngestionStateAsync(
            default!, default, default);
    }
}
