using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Data;
using Connapse.Storage.Llm;
using Microsoft.EntityFrameworkCore;
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
    private readonly ConcurrentDictionary<Guid, byte> _inProgress = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconstructDirtyStateAsync(stoppingToken);

        Task readerTask = ReadEventsAsync(stoppingToken);
        Task scanTask = PeriodicScanAsync(stoppingToken);
        await Task.WhenAll(readerTask, scanTask);
    }

    private async Task ReconstructDirtyStateAsync(CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IDbContextFactory<KnowledgeDbContext> ctxFactory = scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
            await using KnowledgeDbContext ctx = await ctxFactory.CreateDbContextAsync(ct);

            // Find containers where any document has a newer summary timestamp than the container itself,
            // OR where the container has any documents with summaries but no container summary at all.
            List<Guid> staleContainerIds = await ctx.Documents
                .Where(d => d.Summary != null && d.SummaryGeneratedAt != null)
                .GroupBy(d => d.ContainerId)
                .Select(g => new { ContainerId = g.Key, LatestDocSummary = g.Max(d => d.SummaryGeneratedAt) })
                .Join(ctx.Containers, x => x.ContainerId, c => c.Id, (x, c) => new { x.ContainerId, x.LatestDocSummary, ContainerSummaryAt = c.SummaryGeneratedAt })
                .Where(x => x.ContainerSummaryAt == null || x.LatestDocSummary > x.ContainerSummaryAt)
                .Select(x => x.ContainerId)
                .ToListAsync(ct);

            DateTime now = DateTime.UtcNow;
            foreach (Guid cid in staleContainerIds)
            {
                _dirtyCount[cid] = 1;
                _dirtyFirstAt[cid] = now;
            }

            if (staleContainerIds.Count > 0)
            {
                logger.LogInformation(
                    "ContainerSummaryWorker startup recovery: seeded {Count} stale containers for re-rollup",
                    staleContainerIds.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ContainerSummaryWorker startup recovery failed; will rely on incoming events");
        }
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
        if (!_inProgress.TryAdd(containerId, 0))
        {
            // Another worker loop is already processing this container; skip to avoid duplicate LLM calls.
            return;
        }

        try
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
                IDocumentSummaryEmbeddingProvider embeddingProvider =
                    scope.ServiceProvider.GetRequiredService<IDocumentSummaryEmbeddingProvider>();

                Container? container = await containerStore.GetAsync(containerId, ct);
                if (container is null) return;

                // Resolve the LLM provider for this container, applying any per-container override.
                IContainerSettingsResolver settingsResolver =
                    scope.ServiceProvider.GetRequiredService<IContainerSettingsResolver>();
                SummarySettings summarySettings = await settingsResolver.GetSummarySettingsAsync(containerId, ct);

                SummaryLlmResolver llmResolver = scope.ServiceProvider.GetRequiredService<SummaryLlmResolver>();
                ILlmProvider? llmProvider = llmResolver.Resolve(summarySettings);

                ITokenCounter tokenCounter = scope.ServiceProvider.GetRequiredService<ITokenCounter>();
                IContainerSummarizer summarizer = new ContainerSummarizer(llmProvider, tokenCounter);

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
                    // doc_set_hash_match: the set of summarized docs hasn't changed since the last rollup.
                    // Note: this means content_hash changes within already-summarized docs are not detected;
                    // that is an accepted trade-off for v1 (hash covers doc identity + summary text).
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
                    "ContainerRollupCompleted {ContainerId} regime={Regime} N={N} k={K} inTok={InTok} outTok={OutTok}",
                    LogSanitizer.Sanitize(containerId.ToString()),
                    result.Regime, result.NumDocs, result.KClusters,
                    result.InputTokens, result.OutputTokens);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "ContainerRollupFailed {ContainerId}",
                    LogSanitizer.Sanitize(containerId.ToString()));
            }
        }
        finally
        {
            _inProgress.TryRemove(containerId, out _);
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
