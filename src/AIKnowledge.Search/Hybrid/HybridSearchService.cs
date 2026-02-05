using System.Diagnostics;
using System.Runtime.CompilerServices;
using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using AIKnowledge.Search.Keyword;
using AIKnowledge.Search.Vector;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIKnowledge.Search.Hybrid;

/// <summary>
/// Hybrid search service that combines vector and keyword search with configurable reranking.
/// Implements IKnowledgeSearch as the main search entry point.
/// </summary>
public class HybridSearchService : IKnowledgeSearch
{
    private readonly VectorSearchService _vectorSearch;
    private readonly KeywordSearchService _keywordSearch;
    private readonly IEnumerable<ISearchReranker> _rerankers;
    private readonly ILogger<HybridSearchService> _logger;
    private readonly SearchSettings _searchSettings;

    public HybridSearchService(
        VectorSearchService vectorSearch,
        KeywordSearchService keywordSearch,
        IEnumerable<ISearchReranker> rerankers,
        IOptionsMonitor<SearchSettings> searchSettings,
        ILogger<HybridSearchService> logger)
    {
        _vectorSearch = vectorSearch;
        _keywordSearch = keywordSearch;
        _rerankers = rerankers;
        _logger = logger;
        _searchSettings = searchSettings.CurrentValue;
    }

    /// <summary>
    /// Performs hybrid search combining vector and keyword results.
    /// </summary>
    public async Task<SearchResult> SearchAsync(
        string query,
        SearchOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogWarning("Empty query provided to search");
            return new SearchResult([], 0, stopwatch.Elapsed);
        }

        List<SearchHit> hits;

        // Determine search mode (from options or settings)
        var mode = options.Mode;

        _logger.LogInformation(
            "Starting {Mode} search for query: '{Query}' (topK={TopK})",
            mode,
            query,
            options.TopK);

        switch (mode)
        {
            case SearchMode.Semantic:
                hits = await _vectorSearch.SearchAsync(query, options, ct);
                break;

            case SearchMode.Keyword:
                hits = await _keywordSearch.SearchAsync(query, options, ct);
                break;

            case SearchMode.Hybrid:
            default:
                hits = await PerformHybridSearchAsync(query, options, ct);
                break;
        }

        // Apply reranking if configured
        var rerankerName = _searchSettings.Reranker;
        if (!string.IsNullOrEmpty(rerankerName) && rerankerName != "None")
        {
            var reranker = _rerankers.FirstOrDefault(r =>
                r.Name.Equals(rerankerName, StringComparison.OrdinalIgnoreCase));

            if (reranker != null)
            {
                _logger.LogDebug("Applying {Reranker} reranker", rerankerName);
                hits = await reranker.RerankAsync(query, hits, ct);
            }
            else
            {
                _logger.LogWarning(
                    "Configured reranker '{Reranker}' not found, using original ranking",
                    rerankerName);
            }
        }

        // Apply final score threshold and limit
        var finalHits = hits
            .Where(h => h.Score >= options.MinScore)
            .Take(options.TopK)
            .ToList();

        stopwatch.Stop();

        _logger.LogInformation(
            "Search completed in {Duration}ms, returned {Count} results",
            stopwatch.ElapsedMilliseconds,
            finalHits.Count);

        return new SearchResult(
            finalHits,
            finalHits.Count,
            stopwatch.Elapsed);
    }

    /// <summary>
    /// Performs hybrid search by running vector and keyword search in parallel and merging results.
    /// </summary>
    private async Task<List<SearchHit>> PerformHybridSearchAsync(
        string query,
        SearchOptions options,
        CancellationToken ct)
    {
        // Run both searches in parallel
        var vectorTask = Task.Run(async () =>
        {
            var results = await _vectorSearch.SearchAsync(query, options, ct);
            // Tag results with source
            return results.Select(h => h with
            {
                Metadata = new Dictionary<string, string>(h.Metadata) { ["source"] = "vector" }
            }).ToList();
        }, ct);

        var keywordTask = Task.Run(async () =>
        {
            var results = await _keywordSearch.SearchAsync(query, options, ct);
            // Tag results with source
            return results.Select(h => h with
            {
                Metadata = new Dictionary<string, string>(h.Metadata) { ["source"] = "keyword" }
            }).ToList();
        }, ct);

        await Task.WhenAll(vectorTask, keywordTask);

        var vectorResults = await vectorTask;
        var keywordResults = await keywordTask;

        _logger.LogDebug(
            "Hybrid search: vector={VectorCount}, keyword={KeywordCount}",
            vectorResults.Count,
            keywordResults.Count);

        // Combine results
        // The reranker will handle deduplication and fusion
        var combinedHits = new List<SearchHit>();
        combinedHits.AddRange(vectorResults);
        combinedHits.AddRange(keywordResults);

        return combinedHits;
    }

    /// <summary>
    /// Streams search results as they become available.
    /// </summary>
    public async IAsyncEnumerable<SearchHit> SearchStreamAsync(
        string query,
        SearchOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // For now, stream results from the batch search
        // In a future enhancement, this could stream results as they're found
        var result = await SearchAsync(query, options, ct);

        foreach (var hit in result.Hits)
        {
            if (ct.IsCancellationRequested)
            {
                yield break;
            }

            yield return hit;
        }
    }
}
