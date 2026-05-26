using Connapse.Core;
using Connapse.Core.Interfaces;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Connapse.Background.Jobs;

/// <summary>
/// Hangfire job handlers for the ingestion pipeline + per-doc summarization.
/// Indexing and summarization are split into separate jobs so they can be queued onto
/// different worker pools (bounded-duration ingestion vs variable-duration LLM calls).
/// </summary>
public sealed class IngestionJobs : IIngestionJobs
{
    private readonly IKnowledgeIngester _ingester;
    private readonly IDocumentStore _docStore;
    private readonly IKnowledgeFileSystem _fileSystem;
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly IPerDocSummarizer _summarizer;
    private readonly IContainerSettingsResolver _settingsResolver;
    private readonly IBackgroundJobClient _bgClient;
    private readonly ILogger<IngestionJobs> _logger;

    public IngestionJobs(
        IKnowledgeIngester ingester,
        IDocumentStore docStore,
        IKnowledgeFileSystem fileSystem,
        IEnumerable<IDocumentParser> parsers,
        IPerDocSummarizer summarizer,
        IContainerSettingsResolver settingsResolver,
        IBackgroundJobClient bgClient,
        ILogger<IngestionJobs> logger)
    {
        _ingester = ingester;
        _docStore = docStore;
        _fileSystem = fileSystem;
        _parsers = parsers;
        _summarizer = summarizer;
        _settingsResolver = settingsResolver;
        _bgClient = bgClient;
        _logger = logger;
    }

    [Queue(JobQueues.Ingestion)]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task IngestAsync(string documentId, IngestionOptions options, CancellationToken ct)
    {
        // The pipeline encapsulates stream-source resolution via IngestByIdAsync — it looks up
        // the document/container and reads the file through the appropriate connector.
        await _ingester.IngestByIdAsync(documentId, options, ct);

        // Transition state after pipeline success. PerDocSummary continues as a separate
        // queued job (Onyx-style worker-pool separation).
        await _docStore.UpdateIngestionStateAsync(documentId, IngestionState.Indexed, ct);
    }

    public Task PerDocSummaryAsync(string documentId, CancellationToken ct) =>
        // Implemented in Task 12
        throw new NotImplementedException("Implemented in Task 12");
}
