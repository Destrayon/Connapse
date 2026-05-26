using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using NSubstitute;

namespace Connapse.Background.Tests.Jobs;

[Trait("Category", "Unit")]
public class IngestionJobsTests
{
    [Fact]
    public async Task IngestAsync_RunsPipelineAndTransitionsStateToIndexed()
    {
        var ingester = Substitute.For<IKnowledgeIngester>();
        var docStore = Substitute.For<IDocumentStore>();
        var fileSystem = Substitute.For<IKnowledgeFileSystem>();
        var parsers = Array.Empty<IDocumentParser>();
        var summarizer = Substitute.For<IPerDocSummarizer>();
        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        var bgClient = Substitute.For<Hangfire.IBackgroundJobClient>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<IngestionJobs>>();

        var jobs = new IngestionJobs(
            ingester, docStore, fileSystem, parsers, summarizer, settingsResolver, bgClient, logger);

        string documentId = Guid.NewGuid().ToString();
        var options = new IngestionOptions(
            DocumentId: documentId,
            FileName: "test.txt",
            ContentType: "text/plain",
            ContainerId: Guid.NewGuid().ToString());

        await jobs.IngestAsync(documentId, options, CancellationToken.None);

        await ingester.Received(1).IngestByIdAsync(
            documentId, options, Arg.Any<CancellationToken>());

        await docStore.Received(1).UpdateIngestionStateAsync(
            documentId, IngestionState.Indexed, Arg.Any<CancellationToken>());
    }
}
