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

        var stateBroadcaster = Substitute.For<IIngestionStateBroadcaster>();
        var jobs = new IngestionJobs(
            ingester, docStore, fileSystem, parsers, summarizer, settingsResolver, bgClient,
            stateBroadcaster, logger);

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

    [Fact]
    public async Task PerDocSummaryAsync_OnSummaryGenerated_TransitionsToSummaryIndexedAndSchedulesRollup()
    {
        var ingester = Substitute.For<IKnowledgeIngester>();
        var docStore = Substitute.For<IDocumentStore>();
        var fileSystem = Substitute.For<IKnowledgeFileSystem>();
        // Fake parser that produces deterministic text for any extension we use in the test.
        var parser = new FakeParser([".txt"], "Test content");
        var parsers = new IDocumentParser[] { parser };
        var summarizer = Substitute.For<IPerDocSummarizer>();
        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        var bgClient = Substitute.For<Hangfire.IBackgroundJobClient>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<IngestionJobs>>();

        string documentId = Guid.NewGuid().ToString();
        Guid containerId = Guid.NewGuid();

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

        settingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SummarySettings { Enabled = true }));

        fileSystem.OpenFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Test content"))));

        summarizer.GenerateAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
                Arg.Any<SummarySettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PerDocSummarizationResult(
                Skipped: false, Summary: "Test summary", InputTokens: 10, OutputTokens: 5, Model: "test")));

        var stateBroadcaster = Substitute.For<IIngestionStateBroadcaster>();
        var jobs = new IngestionJobs(
            ingester, docStore, fileSystem, parsers, summarizer, settingsResolver, bgClient,
            stateBroadcaster, logger);
        await jobs.PerDocSummaryAsync(documentId, CancellationToken.None);

        await docStore.Received(1).UpdateIngestionStateAsync(
            documentId, IngestionState.SummaryIndexed, Arg.Any<CancellationToken>());

        // Rollup is scheduled with a delay (ScheduledState).
        bgClient.Received(1).Create(
            Arg.Any<Hangfire.Common.Job>(),
            Arg.Is<Hangfire.States.IState>(s => s is Hangfire.States.ScheduledState));
    }

    [Fact]
    public async Task PerDocSummaryAsync_WhenSettingsDisabled_SkipsAndDoesNotScheduleRollup()
    {
        var ingester = Substitute.For<IKnowledgeIngester>();
        var docStore = Substitute.For<IDocumentStore>();
        var fileSystem = Substitute.For<IKnowledgeFileSystem>();
        var parsers = Array.Empty<IDocumentParser>();
        var summarizer = Substitute.For<IPerDocSummarizer>();
        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        var bgClient = Substitute.For<Hangfire.IBackgroundJobClient>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<IngestionJobs>>();

        string documentId = Guid.NewGuid().ToString();
        Guid containerId = Guid.NewGuid();

        docStore.GetAsync(documentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Document?>(new Document(
                Id: documentId, ContainerId: containerId.ToString(),
                FileName: "doc.txt", ContentType: "text/plain", Path: "/doc.txt",
                SizeBytes: 100, CreatedAt: DateTime.UtcNow,
                Metadata: new Dictionary<string, string>(),
                Summary: null, SummaryGeneratedAt: null, SummaryContentHash: null,
                IngestionState: IngestionState.Indexed)));

        settingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SummarySettings { Enabled = false }));

        var stateBroadcaster = Substitute.For<IIngestionStateBroadcaster>();
        var jobs = new IngestionJobs(
            ingester, docStore, fileSystem, parsers, summarizer, settingsResolver, bgClient,
            stateBroadcaster, logger);
        await jobs.PerDocSummaryAsync(documentId, CancellationToken.None);

        await summarizer.DidNotReceive().GenerateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<SummarySettings>(), Arg.Any<CancellationToken>());
        bgClient.DidNotReceive().Create(
            Arg.Any<Hangfire.Common.Job>(), Arg.Any<Hangfire.States.IState>());
    }

    private sealed class FakeParser(string[] extensions, string content) : IDocumentParser
    {
        private readonly string _content = content;

        public IReadOnlySet<string> SupportedExtensions { get; } =
            new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);

        public Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ParsedDocument(
                Content: _content,
                Metadata: new Dictionary<string, string>(),
                Warnings: new List<string>()));
    }
}
