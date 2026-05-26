using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
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

    [Queue(JobQueues.Summarization)]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task PerDocSummaryAsync(string documentId, CancellationToken ct)
    {
        Document? doc = await _docStore.GetAsync(documentId, ct);
        if (doc is null)
        {
            _logger.LogInformation(
                "PerDocSummarySkipped {DocumentId} reason=document_not_found",
                LogSanitizer.Sanitize(documentId));
            return;
        }

        if (!Guid.TryParse(doc.ContainerId, out Guid containerId))
        {
            _logger.LogWarning(
                "PerDocSummarySkipped {DocumentId} reason=invalid_container_id",
                LogSanitizer.Sanitize(documentId));
            return;
        }

        SummarySettings settings = await _settingsResolver.GetSummarySettingsAsync(containerId, ct);
        if (!settings.Enabled)
        {
            _logger.LogInformation(
                "PerDocSummarySkipped {DocumentId} reason=summaries_disabled",
                LogSanitizer.Sanitize(documentId));
            return;
        }

        // Re-parse the doc text via the same parser the pipeline used during ingestion.
        // The pipeline doesn't cache parsed text on the Document record, so we re-read +
        // re-parse here. This is acceptable because per-doc summarization runs on the
        // summarization worker pool and LLM latency dominates parse cost.
        string parsedText;
        try
        {
            await using Stream stream = await _fileSystem.OpenFileAsync(doc.Path, ct);
            string extension = Path.GetExtension(doc.FileName).ToLowerInvariant();
            IDocumentParser? parser = _parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));
            if (parser is null)
            {
                _logger.LogInformation(
                    "PerDocSummarySkipped {DocumentId} reason=no_parser_for_extension",
                    LogSanitizer.Sanitize(documentId));
                return;
            }

            ParsedDocument parsed = await parser.ParseAsync(stream, doc.FileName, ct);
            parsedText = parsed.Content;
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning(
                "PerDocSummarySkipped {DocumentId} reason=file_not_found",
                LogSanitizer.Sanitize(documentId));
            return;
        }

        if (string.IsNullOrWhiteSpace(parsedText))
        {
            _logger.LogInformation(
                "PerDocSummarySkipped {DocumentId} reason=empty_parsed_content",
                LogSanitizer.Sanitize(documentId));
            return;
        }

        PerDocSummarizationResult result = await _summarizer.GenerateAsync(
            documentId, parsedText, doc.ContentType, doc.FileName, settings, ct);

        if (result.Skipped)
        {
            _logger.LogInformation(
                "PerDocSummarySkipped {DocumentId} reason={Reason}",
                LogSanitizer.Sanitize(documentId),
                LogSanitizer.Sanitize(result.SkipReason ?? ""));
            return;
        }

        await _docStore.UpdateIngestionStateAsync(documentId, IngestionState.SummaryIndexed, ct);

        // Schedule a debounced container rollup. 60s gives a bulk-upload burst time
        // to converge so a single rollup covers many docs.
        _bgClient.Schedule<ISummaryJobs>(
            s => s.RollupContainerAsync(containerId, default),
            TimeSpan.FromSeconds(60));

        _logger.LogInformation(
            "PerDocSummaryCompleted {DocumentId} model={Model} inTok={InputTokens} outTok={OutputTokens}",
            LogSanitizer.Sanitize(documentId),
            LogSanitizer.Sanitize(result.Model ?? ""),
            result.InputTokens,
            result.OutputTokens);
    }
}
