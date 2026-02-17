using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Search.Reranking;

/// <summary>
/// Reciprocal Rank Fusion (RRF) reranker.
/// Combines multiple ranked lists using the formula: score = sum(1 / (k + rank))
/// where k is a constant (default: 60) and rank is the 1-indexed position.
/// </summary>
public class RrfReranker : ISearchReranker
{
    private readonly ILogger<RrfReranker> _logger;
    private readonly int _k;

    public string Name => "RRF";

    public RrfReranker(
        IOptionsMonitor<SearchSettings> searchSettings,
        ILogger<RrfReranker> logger)
    {
        _logger = logger;
        _k = searchSettings.CurrentValue.RrfK;
    }

    /// <summary>
    /// Reranks hits using Reciprocal Rank Fusion.
    /// The input hits should have metadata indicating which "list" they came from
    /// (e.g., "source" = "vector" or "keyword").
    /// </summary>
    public Task<List<SearchHit>> RerankAsync(
        string query,
        List<SearchHit> hits,
        CancellationToken cancellationToken = default)
    {
        if (hits.Count == 0)
        {
            return Task.FromResult(hits);
        }

        // Group hits by source (vector vs keyword)
        var sources = hits
            .GroupBy(h => h.Metadata.GetValueOrDefault("source", "unknown"))
            .ToList();

        if (sources.Count <= 1)
        {
            // Only one source, no fusion needed
            _logger.LogDebug(
                "Only one search source found, skipping RRF fusion");
            return Task.FromResult(hits);
        }

        // Build ranked lists per source
        var rankedLists = new Dictionary<string, List<(SearchHit hit, int rank)>>();

        foreach (var sourceGroup in sources)
        {
            var ranked = sourceGroup
                .OrderByDescending(h => h.Score)
                .Select((hit, index) => (hit, rank: index + 1)) // 1-indexed ranks
                .ToList();

            rankedLists[sourceGroup.Key] = ranked;
        }

        // Calculate RRF scores for each unique chunk
        var rrfScores = new Dictionary<string, (SearchHit hit, double rrfScore)>();

        foreach (var (source, rankedHits) in rankedLists)
        {
            foreach (var (hit, rank) in rankedHits)
            {
                var rrfScore = 1.0 / (_k + rank);

                if (rrfScores.TryGetValue(hit.ChunkId, out var existing))
                {
                    // Chunk appears in multiple lists, accumulate scores
                    rrfScores[hit.ChunkId] = (existing.hit, existing.rrfScore + rrfScore);
                }
                else
                {
                    rrfScores[hit.ChunkId] = (hit, rrfScore);
                }
            }
        }

        // Normalize RRF scores to 0-1 range
        var maxScore = rrfScores.Values.Max(v => v.rrfScore);
        var minScore = rrfScores.Values.Min(v => v.rrfScore);
        var scoreRange = maxScore - minScore;

        var rerankedHits = rrfScores.Values
            .Select(v =>
            {
                var normalizedScore = scoreRange > 0
                    ? (float)((v.rrfScore - minScore) / scoreRange)
                    : 1.0f;

                // Create new hit with updated score
                return v.hit with
                {
                    Score = normalizedScore,
                    Metadata = new Dictionary<string, string>(v.hit.Metadata)
                    {
                        ["rrfScore"] = v.rrfScore.ToString("F6"),
                        ["reranker"] = "RRF"
                    }
                };
            })
            .OrderByDescending(h => h.Score)
            .ToList();

        _logger.LogInformation(
            "RRF reranking with k={K} merged {InputCount} hits from {SourceCount} sources into {OutputCount} unique results",
            _k,
            hits.Count,
            sources.Count,
            rerankedHits.Count);

        return Task.FromResult(rerankedHits);
    }
}
