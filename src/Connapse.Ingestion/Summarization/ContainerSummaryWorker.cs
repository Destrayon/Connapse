using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Connapse.Ingestion.Summarization;

public sealed class ContainerSummaryWorker(
    IContainerSummaryQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ContainerSummaryWorker> logger) : BackgroundService
{
    private const int DebounceCountThreshold = 25;
    private static readonly TimeSpan DebounceTimeThreshold = TimeSpan.FromHours(6);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<Guid, int> _dirtyCount = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _dirtyFirstAt = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task readerTask = ReadEventsAsync(stoppingToken);
        Task scanTask = PeriodicScanAsync(stoppingToken);
        await Task.WhenAll(readerTask, scanTask);
    }

    private async Task ReadEventsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ContainerSummaryDirtyEvent evt;
            try { evt = await queue.DequeueAsync(ct); }
            catch (OperationCanceledException) { break; }

            _dirtyCount.AddOrUpdate(evt.ContainerId, 1, (_, count) => count + 1);
            _dirtyFirstAt.GetOrAdd(evt.ContainerId, _ => DateTime.UtcNow);

            if (_dirtyCount.TryGetValue(evt.ContainerId, out int count) && count >= DebounceCountThreshold)
                await ProcessContainerAsync(evt.ContainerId, "dirty_count_threshold", ct);
        }
    }

    private async Task PeriodicScanAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(ScanInterval, ct); }
            catch (OperationCanceledException) { break; }

            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<Guid, DateTime> kv in _dirtyFirstAt)
            {
                if (now - kv.Value >= DebounceTimeThreshold)
                    await ProcessContainerAsync(kv.Key, "time_threshold", ct);
            }
        }
    }

    internal async Task ProcessContainerAsync(Guid containerId, string triggerReason, CancellationToken ct)
    {
        if (!_dirtyCount.TryRemove(containerId, out int count)) return;
        _dirtyFirstAt.TryRemove(containerId, out _);

        logger.LogInformation(
            "ContainerRollupTriggered {ContainerId} reason={Reason} dirtyCount={Count}",
            LogSanitizer.Sanitize(containerId.ToString()), triggerReason, count);

        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IContainerStore containerStore = scope.ServiceProvider.GetRequiredService<IContainerStore>();
            IDocumentStore documentStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            IContainerSummarizer summarizer = scope.ServiceProvider.GetRequiredService<IContainerSummarizer>();
            IDocumentSummaryEmbeddingProvider embeddingProvider =
                scope.ServiceProvider.GetRequiredService<IDocumentSummaryEmbeddingProvider>();

            Container? container = await containerStore.GetAsync(containerId, ct);
            if (container is null) return;

            IReadOnlyList<Document> docs = await documentStore.ListAsync(
                containerId, pathPrefix: null, skip: 0, take: 10_000, ct);
            List<Document> withSummaries = docs.Where(d => !string.IsNullOrEmpty(d.Summary)).ToList();

            if (withSummaries.Count == 0)
            {
                await containerStore.UpdateSummaryAsync(containerId, null, null, null, ct);
                return;
            }

            string docSetHash = ComputeDocSetHash(withSummaries);
            if (docSetHash == container.SummaryDocSetHash)
            {
                logger.LogInformation(
                    "ContainerRollupSkipped {ContainerId} reason=doc_set_hash_match",
                    LogSanitizer.Sanitize(containerId.ToString()));
                return;
            }

            IReadOnlyList<DocumentWithSummary> docsWithEmbeddings =
                await embeddingProvider.GetSummaryEmbeddingsAsync(withSummaries, ct);

            ContainerSummarizationResult result = await summarizer.GenerateAsync(
                container.Name, docsWithEmbeddings, ct);

            if (result.Skipped)
            {
                logger.LogInformation(
                    "ContainerRollupSkipped {ContainerId} reason={Reason}",
                    LogSanitizer.Sanitize(containerId.ToString()),
                    LogSanitizer.Sanitize(result.SkipReason ?? string.Empty));
                return;
            }

            await containerStore.UpdateSummaryAsync(containerId, result.Summary, DateTime.UtcNow, docSetHash, ct);

            logger.LogInformation(
                "ContainerRollupCompleted {ContainerId} regime={Regime} N={N} k={K} inTok={InTok} outTok={OutTok} usd={Usd}",
                LogSanitizer.Sanitize(containerId.ToString()),
                result.Regime, result.NumDocs, result.KClusters,
                result.InputTokens, result.OutputTokens, result.CostEstimateUsd);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "ContainerRollupFailed {ContainerId}",
                LogSanitizer.Sanitize(containerId.ToString()));
        }
    }

    /// <summary>Seeds the dirty-count dictionary for unit testing without running the queue loop.</summary>
    internal void SetDirtyForTest(Guid containerId, int count)
    {
        _dirtyCount[containerId] = count;
        _dirtyFirstAt.TryAdd(containerId, DateTime.UtcNow);
    }

    internal static string ComputeDocSetHash(IEnumerable<Document> docs)
    {
        IEnumerable<string> parts = docs
            .OrderBy(d => d.Id)
            .Select(d => $"{d.Id}|{ComputeSha256(d.Summary ?? string.Empty)}");
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
        return Convert.ToHexStringLower(bytes);
    }

    private static string ComputeSha256(string s)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexStringLower(bytes);
    }
}
