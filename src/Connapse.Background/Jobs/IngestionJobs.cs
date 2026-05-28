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
    private readonly IContainerStore _containerStore;
    private readonly IConnectorFactory _connectorFactory;
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly IPerDocSummarizer _summarizer;
    private readonly IContainerSettingsResolver _settingsResolver;
    private readonly IBackgroundJobClient _bgClient;
    private readonly IIngestionStateBroadcaster _stateBroadcaster;
    private readonly ILogger<IngestionJobs> _logger;

    public IngestionJobs(
        IKnowledgeIngester ingester,
        IDocumentStore docStore,
        IContainerStore containerStore,
        IConnectorFactory connectorFactory,
        IEnumerable<IDocumentParser> parsers,
        IPerDocSummarizer summarizer,
        IContainerSettingsResolver settingsResolver,
        IBackgroundJobClient bgClient,
        IIngestionStateBroadcaster stateBroadcaster,
        ILogger<IngestionJobs> logger)
    {
        _ingester = ingester;
        _docStore = docStore;
        _containerStore = containerStore;
        _connectorFactory = connectorFactory;
        _parsers = parsers;
        _summarizer = summarizer;
        _settingsResolver = settingsResolver;
        _bgClient = bgClient;
        _stateBroadcaster = stateBroadcaster;
        _logger = logger;
    }

    [Queue(JobQueues.Ingestion)]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task IngestAsync(string documentId, IngestionOptions options, CancellationToken ct)
    {
        try
        {
            // The pipeline encapsulates stream-source resolution via IngestByIdAsync — it looks
            // up the document/container and reads the file through the appropriate connector.
            await _ingester.IngestByIdAsync(documentId, options, ct);

            // Wrap-up uses CancellationToken.None: if the app restarts during these brief
            // (~50ms) DB/SignalR ops, we still want them to complete — otherwise the UI's
            // spinner gets stuck even though ingestion finished. Pattern: "best-effort
            // finalization" — once we've done the real work, commit the resulting state.
            await _docStore.UpdateIngestionStateAsync(documentId, IngestionState.Indexed, CancellationToken.None);
            await _stateBroadcaster.BroadcastIngestionStateChangedAsync(
                documentId, IngestionState.Indexed, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hangfire's AutomaticRetry will re-throw on rethrow; the intermediate Failed
            // state is visible to the UI between attempts and settles back to Indexed on
            // successful retry. Marking Failed only on retry exhaustion would require an
            // IElectStateFilter — deferred to a follow-up.
            _logger.LogWarning(ex,
                "IngestAsync threw {DocumentId}", LogSanitizer.Sanitize(documentId));
            await _docStore.UpdateIngestionStateAsync(documentId, IngestionState.Failed, CancellationToken.None);
            await _stateBroadcaster.BroadcastIngestionStateChangedAsync(
                documentId, IngestionState.Failed, CancellationToken.None);
            throw;
        }
    }

    [Queue(JobQueues.Summarization)]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task PerDocSummaryAsync(string documentId, CancellationToken ct)
    {
        try
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

            if (settings.ContainerSummaryMethod == SummaryStrategy.DocumentClustering)
            {
                // document-clustering mode summarizes K medoids lazily at rollup time;
                // no per-doc summary is generated at ingest. Advance the ingestion state
                // so the UI doesn't show a stuck spinner — "summary processing complete"
                // here means "we decided not to summarize this doc."
                _logger.LogInformation(
                    "PerDocSummarySkipped {DocumentId} reason=document_clustering_mode",
                    LogSanitizer.Sanitize(documentId));

                await _docStore.UpdateIngestionStateAsync(documentId, IngestionState.SummaryIndexed, CancellationToken.None);
                await _stateBroadcaster.BroadcastIngestionStateChangedAsync(
                    documentId, IngestionState.SummaryIndexed, CancellationToken.None);
                return;
            }

            // Re-parse the doc text via the same parser the pipeline used during ingestion.
            // Read through the container's connector (matching IngestionPipeline.IngestByIdAsync) —
            // _fileSystem.OpenFileAsync(doc.Path) would miss the container-id prefix that the
            // ManagedStorage connector adds, so it'd always FileNotFound for managed storage.
            string parsedText;
            try
            {
                Container? container = await _containerStore.GetAsync(containerId, ct);
                if (container is null)
                {
                    _logger.LogWarning(
                        "PerDocSummarySkipped {DocumentId} reason=container_not_found",
                        LogSanitizer.Sanitize(documentId));
                    return;
                }

                IConnector connector = _connectorFactory.Create(container);
                string jobPath = connector.ResolveJobPath(doc.Path.TrimStart('/'));
                await using Stream stream = await connector.ReadFileAsync(jobPath, ct);

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

            // Wrap-up uses CancellationToken.None: see comment in IngestAsync — best-effort
            // finalization so the UI's spinner doesn't stick after a mid-job app restart.
            await _docStore.UpdateIngestionStateAsync(documentId, IngestionState.SummaryIndexed, CancellationToken.None);
            await _stateBroadcaster.BroadcastIngestionStateChangedAsync(
                documentId, IngestionState.SummaryIndexed, CancellationToken.None);

            // Container rollup is triggered by the recurring SweepStaleContainersAsync job
            // (every 5 minutes) rather than per-doc completion. The sweep coalesces N uploads
            // into 1 rollup once the per-doc burst has settled (no in-flight summary jobs for
            // the container), matching what Postgres materialized views and Algolia derived
            // indexes do for expensive aggregates over frequently-changing base data.

            _logger.LogInformation(
                "PerDocSummaryCompleted {DocumentId} model={Model} inTok={InputTokens} outTok={OutputTokens}",
                LogSanitizer.Sanitize(documentId),
                LogSanitizer.Sanitize(result.Model ?? ""),
                result.InputTokens,
                result.OutputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "PerDocSummaryAsync threw {DocumentId}", LogSanitizer.Sanitize(documentId));
            await _docStore.UpdateIngestionStateAsync(documentId, IngestionState.Failed, CancellationToken.None);
            await _stateBroadcaster.BroadcastIngestionStateChangedAsync(
                documentId, IngestionState.Failed, CancellationToken.None);
            throw;
        }
    }
}
