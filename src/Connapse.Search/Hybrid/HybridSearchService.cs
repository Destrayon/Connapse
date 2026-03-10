using System.Diagnostics;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Keyword;
using Connapse.Search.Vector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Search.Hybrid;

/// <summary>
/// Hybrid search service that combines vector and keyword search with configurable reranking.
/// Implements IKnowledgeSearch as the main search entry point.
/// </summary>
public class HybridSearchService : IKnowledgeSearch
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEnumerable<ISearchReranker> _rerankers;
    private readonly ILogger<HybridSearchService> _logger;
    private readonly IOptionsMonitor<SearchSettings> _searchSettingsMonitor;

    public HybridSearchService(
        IServiceScopeFactory scopeFactory,
        IEnumerable<ISearchReranker> rerankers,
        IOptionsMonitor<SearchSettings> searchSettings,
        ILogger<HybridSearchService> logger)
    {
        _scopeFactory = scopeFactory;
        _rerankers = rerankers;
        _logger = logger;
        _searchSettingsMonitor = searchSettings;
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

        // Read settings at search time (not constructor time) so changes propagate immediately
        var searchSettings = _searchSettingsMonitor.CurrentValue;

        // Determine search mode (from options or settings)
        var mode = options.Mode;

        // Cross-model search: override Semantic to Hybrid so keyword results can
        // surface documents embedded with previous models (keyword search is model-agnostic).
        if (searchSettings.EnableCrossModelSearch && mode == SearchMode.Semantic)
        {
            mode = SearchMode.Hybrid;
            _logger.LogInformation(
                "Cross-model search active: overriding Semantic to Hybrid for legacy vector coverage");
        }

        _logger.LogInformation(
            "Starting {Mode} search for query: '{Query}' (topK={TopK})",
            mode,
            query,
            options.TopK);

        // Create a scope to get search services
        await using var scope = _scopeFactory.CreateAsyncScope();
        var vectorSearch = scope.ServiceProvider.GetRequiredService<VectorSearchService>();
        var keywordSearch = scope.ServiceProvider.GetRequiredService<KeywordSearchService>();

        switch (mode)
        {
            case SearchMode.Semantic:
                hits = await vectorSearch.SearchAsync(query, options, ct);
                break;

            case SearchMode.Keyword:
                hits = await keywordSearch.SearchAsync(query, options, ct);
                break;

            case SearchMode.Hybrid:
            default:
                hits = await PerformHybridSearchAsync(query, options, ct);
                break;
        }

        // Apply reranking if configured
        var rerankerName = searchSettings.Reranker;
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

        // Apply final score threshold, auto-cut, and limit
        var filtered = hits
            .Where(h => h.Score >= options.MinScore)
            .OrderByDescending(h => h.Score)
            .ToList();

        if (searchSettings.AutoCut)
            filtered = ApplyAutoCut(filtered);

        var finalHits = filtered.Take(options.TopK).ToList();

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
    /// Each search uses its own scope to avoid DbContext threading issues.
    /// </summary>
    private async Task<List<SearchHit>> PerformHybridSearchAsync(
        string query,
        SearchOptions options,
        CancellationToken ct)
    {
        // Run both searches in parallel, each with its own scope (and thus separate DbContext)
        var vectorTask = Task.Run(async () =>
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var vectorSearch = scope.ServiceProvider.GetRequiredService<VectorSearchService>();
            var results = await vectorSearch.SearchAsync(query, options, ct);
            return results;
        }, ct);

        var keywordTask = Task.Run(async () =>
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var keywordSearch = scope.ServiceProvider.GetRequiredService<KeywordSearchService>();
            var results = await keywordSearch.SearchAsync(query, options, ct);
            return results;
        }, ct);

        await Task.WhenAll(vectorTask, keywordTask);

        var vectorResults = await vectorTask;
        var keywordResults = await keywordTask;

        _logger.LogDebug(
            "Hybrid search: vector={VectorCount}, keyword={KeywordCount}",
            vectorResults.Count,
            keywordResults.Count);

        var settings = _searchSettingsMonitor.CurrentValue;

        if (string.Equals(settings.FusionMethod, "DBSF", StringComparison.OrdinalIgnoreCase))
            return FuseResultsDbsf(vectorResults, keywordResults, settings.FusionAlpha);

        if (!string.Equals(settings.FusionMethod, "ConvexCombination", StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning("Unknown FusionMethod '{Method}', defaulting to ConvexCombination", settings.FusionMethod);

        return FuseResults(vectorResults, keywordResults, settings.FusionAlpha);
    }

    /// <summary>
    /// Fuses vector and keyword results using Convex Combination.
    /// Min-max normalizes each input list independently, then combines:
    /// score = alpha * normalizedVector + (1 - alpha) * normalizedKeyword.
    /// Produces meaningful [0,1] scores without post-fusion normalization.
    /// </summary>
    internal static List<SearchHit> FuseResults(
        List<SearchHit> vectorResults,
        List<SearchHit> keywordResults,
        float alpha)
    {
        if (vectorResults.Count == 0 && keywordResults.Count == 0)
            return [];

        // Min-max normalize INPUT scores independently (puts both on [0,1] scale)
        var normVector = MinMaxNormalize(vectorResults);
        var normKeyword = MinMaxNormalize(keywordResults);

        // Build lookup: ChunkId -> (Hit, vectorScore, keywordScore, source flags)
        var fused = new Dictionary<string, (SearchHit Hit, float VectorScore, float KeywordScore, bool InVector, bool InKeyword)>();

        foreach (var (hit, score) in normVector)
            fused[hit.ChunkId] = (hit, score, 0f, true, false);

        foreach (var (hit, score) in normKeyword)
        {
            if (fused.TryGetValue(hit.ChunkId, out var existing))
                fused[hit.ChunkId] = (existing.Hit, existing.VectorScore, score, true, true);
            else
                fused[hit.ChunkId] = (hit, 0f, score, false, true);
        }

        // Convex combination: alpha * vector + (1 - alpha) * keyword
        var clampedAlpha = Math.Clamp(alpha, 0f, 1f);
        return fused.Values
            .Select(entry =>
            {
                var fusedScore = clampedAlpha * entry.VectorScore + (1f - clampedAlpha) * entry.KeywordScore;

                var source = (entry.InVector, entry.InKeyword) switch
                {
                    (true, true) => "both",
                    (true, false) => "vector",
                    _ => "keyword"
                };

                var metadata = new Dictionary<string, string>(entry.Hit.Metadata)
                {
                    ["source"] = source,
                    ["vectorScore"] = entry.VectorScore.ToString("F4"),
                    ["keywordScore"] = entry.KeywordScore.ToString("F4")
                };

                return entry.Hit with { Score = fusedScore, Metadata = metadata };
            })
            .OrderByDescending(h => h.Score)
            .ToList();
    }

    /// <summary>
    /// Fuses vector and keyword results using Distribution-Based Score Fusion (DBSF).
    /// Normalizes each input list using mean ± 3σ, then combines with alpha weighting.
    /// More robust to outliers than min-max normalization.
    /// </summary>
    internal static List<SearchHit> FuseResultsDbsf(
        List<SearchHit> vectorResults,
        List<SearchHit> keywordResults,
        float alpha)
    {
        if (vectorResults.Count == 0 && keywordResults.Count == 0)
            return [];

        var normVector = DbsfNormalize(vectorResults);
        var normKeyword = DbsfNormalize(keywordResults);

        var fused = new Dictionary<string, (SearchHit Hit, float VectorScore, float KeywordScore, bool InVector, bool InKeyword)>();

        foreach (var (hit, score) in normVector)
            fused[hit.ChunkId] = (hit, score, 0f, true, false);

        foreach (var (hit, score) in normKeyword)
        {
            if (fused.TryGetValue(hit.ChunkId, out var existing))
                fused[hit.ChunkId] = (existing.Hit, existing.VectorScore, score, true, true);
            else
                fused[hit.ChunkId] = (hit, 0f, score, false, true);
        }

        var clampedAlpha = Math.Clamp(alpha, 0f, 1f);
        return fused.Values
            .Select(entry =>
            {
                var fusedScore = clampedAlpha * entry.VectorScore + (1f - clampedAlpha) * entry.KeywordScore;

                var source = (entry.InVector, entry.InKeyword) switch
                {
                    (true, true) => "both",
                    (true, false) => "vector",
                    _ => "keyword"
                };

                var metadata = new Dictionary<string, string>(entry.Hit.Metadata)
                {
                    ["source"] = source,
                    ["vectorScore"] = entry.VectorScore.ToString("F4"),
                    ["keywordScore"] = entry.KeywordScore.ToString("F4")
                };

                return entry.Hit with { Score = fusedScore, Metadata = metadata };
            })
            .OrderByDescending(h => h.Score)
            .ToList();
    }

    /// <summary>
    /// DBSF normalization: maps scores to [0,1] using (score - (mean - 3σ)) / (6σ).
    /// More robust to outliers than min-max — a single extreme score doesn't compress the rest.
    /// Scores outside [mean-3σ, mean+3σ] are clamped to [0,1].
    /// </summary>
    private static List<(SearchHit Hit, float NormalizedScore)> DbsfNormalize(List<SearchHit> hits)
    {
        if (hits.Count == 0) return [];
        if (hits.Count == 1) return [(hits[0], 1f)];

        var scores = hits.Select(h => (double)h.Score).ToArray();
        var mean = scores.Average();
        var stdDev = Math.Sqrt(scores.Average(s => (s - mean) * (s - mean)));

        if (stdDev < 1e-9)
            return hits.Select(h => (h, 1f)).ToList();

        var lower = mean - 3 * stdDev;
        var range = 6 * stdDev;

        return hits.Select(h =>
        {
            var normalized = (float)Math.Clamp((h.Score - lower) / range, 0, 1);
            return (h, normalized);
        }).ToList();
    }

    private static List<(SearchHit Hit, float NormalizedScore)> MinMaxNormalize(List<SearchHit> hits)
    {
        if (hits.Count == 0) return [];

        var max = hits.Max(h => h.Score);
        var min = hits.Min(h => h.Score);
        var range = max - min;

        return hits.Select(h => (h, range > 0 ? (h.Score - min) / range : 1f)).ToList();
    }

    /// <summary>
    /// Trims results after the largest relative score gap.
    /// Expects hits sorted by score descending. Keeps the top cluster.
    /// Only cuts if the largest gap is both meaningful (> 10% of range)
    /// and dominant (> 2x the second-largest gap), preventing false cuts
    /// on evenly-spaced score distributions.
    /// </summary>
    internal static List<SearchHit> ApplyAutoCut(List<SearchHit> hits)
    {
        if (hits.Count <= 2)
            return hits;

        // Find the two largest gaps between consecutive scores
        var maxGap = 0f;
        var secondGap = 0f;
        var cutIndex = hits.Count;

        for (var i = 1; i < hits.Count; i++)
        {
            var gap = hits[i - 1].Score - hits[i].Score;
            if (gap > maxGap)
            {
                secondGap = maxGap;
                maxGap = gap;
                cutIndex = i;
            }
            else if (gap > secondGap)
            {
                secondGap = gap;
            }
        }

        // Only cut if the gap is meaningful (> 10% of range) AND dominant (> 2x second-largest gap)
        var scoreRange = hits[0].Score - hits[^1].Score;
        if (scoreRange > 0 && maxGap / scoreRange > 0.1f && maxGap > secondGap * 2)
            return hits.Take(cutIndex).ToList();

        return hits;
    }

}
