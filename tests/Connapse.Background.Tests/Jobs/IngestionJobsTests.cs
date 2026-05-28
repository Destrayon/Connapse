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

        var containerStore = Substitute.For<IContainerStore>();
        var connectorFactory = Substitute.For<IConnectorFactory>();
        var stateBroadcaster = Substitute.For<IIngestionStateBroadcaster>();
        var jobs = new IngestionJobs(
            ingester, docStore, containerStore, connectorFactory, parsers, summarizer,
            settingsResolver, bgClient, stateBroadcaster, logger);

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
    public async Task PerDocSummaryAsync_OnSummaryGenerated_TransitionsToSummaryIndexedAndDoesNotScheduleRollup()
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
            .Returns(Task.FromResult(new SummarySettings
            {
                Enabled = true,
                // Explicit: this test exercises the eager summary-clustering path, where
                // PerDocSummaryAsync actually invokes the summarizer. The new default
                // (document-clustering) takes a separate early-return path covered by
                // IngestionJobsHerculesTests.
                ContainerSummaryMethod = SummaryStrategy.SummaryClustering,
            }));

        // Container/connector chain: GetAsync → connector → ReadFileAsync
        var container = new Container(
            Id: containerId.ToString(),
            Name: "test",
            Description: null,
            ConnectorType: ConnectorType.ManagedStorage,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);
        var connector = Substitute.For<IConnector>();
        connector.ResolveJobPath(Arg.Any<string>()).Returns(call => call.Arg<string>());
        connector.ReadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Test content"))));

        summarizer.GenerateAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
                Arg.Any<SummarySettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PerDocSummarizationResult(
                Skipped: false, Summary: "Test summary", InputTokens: 10, OutputTokens: 5, Model: "test")));

        var containerStore = Substitute.For<IContainerStore>();
        containerStore.GetAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Container?>(container));
        var connectorFactory = Substitute.For<IConnectorFactory>();
        connectorFactory.Create(Arg.Any<Container>()).Returns(connector);
        var stateBroadcaster = Substitute.For<IIngestionStateBroadcaster>();
        var jobs = new IngestionJobs(
            ingester, docStore, containerStore, connectorFactory, parsers, summarizer,
            settingsResolver, bgClient, stateBroadcaster, logger);
        await jobs.PerDocSummaryAsync(documentId, CancellationToken.None);

        await docStore.Received(1).UpdateIngestionStateAsync(
            documentId, IngestionState.SummaryIndexed, Arg.Any<CancellationToken>());

        // Rollup is NOT scheduled per-doc anymore — the recurring SweepStaleContainersAsync
        // job (every 5 min) coalesces N per-doc completions into 1 rollup once the burst
        // settles. This avoids the "1 rollup per upload" dashboard noise + LLM waste.
        bgClient.DidNotReceive().Create(
            Arg.Any<Hangfire.Common.Job>(),
            Arg.Any<Hangfire.States.IState>());
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

        var containerStore = Substitute.For<IContainerStore>();
        var connectorFactory = Substitute.For<IConnectorFactory>();
        var stateBroadcaster = Substitute.For<IIngestionStateBroadcaster>();
        var jobs = new IngestionJobs(
            ingester, docStore, containerStore, connectorFactory, parsers, summarizer,
            settingsResolver, bgClient, stateBroadcaster, logger);
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
