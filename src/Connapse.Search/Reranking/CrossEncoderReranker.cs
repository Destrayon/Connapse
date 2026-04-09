using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Reranking.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Search.Reranking;

/// <summary>
/// Cross-encoder reranker that uses dedicated reranking models (TEI, Cohere, Jina)
/// to score (query, document) pairs. More accurate than RRF for relevance ranking.
/// </summary>
public class CrossEncoderReranker : ISearchReranker
{
    private readonly IOptionsMonitor<SearchSettings> _searchSettingsMonitor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CrossEncoderReranker> _logger;

    public string Name => "CrossEncoder";

    public CrossEncoderReranker(
        IOptionsMonitor<SearchSettings> searchSettings,
        IHttpClientFactory httpClientFactory,
        ILogger<CrossEncoderReranker> logger)
    {
        _searchSettingsMonitor = searchSettings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<SearchHit>> RerankAsync(
        string query,
        List<SearchHit> hits,
        CancellationToken cancellationToken = default)
    {
        if (hits.Count == 0)
            return hits;

        var settings = _searchSettingsMonitor.CurrentValue;

        var isVoyage = string.Equals(settings.CrossEncoderProvider, "Voyage", StringComparison.OrdinalIgnoreCase);
        if (!isVoyage && string.IsNullOrEmpty(settings.CrossEncoderModel))
        {
            _logger.LogWarning("CrossEncoderModel not configured, returning original order");
            return hits;
        }

        _logger.LogInformation(
            "Cross-encoder reranking {Count} hits using {Provider}/{Model}",
            hits.Count,
            settings.CrossEncoderProvider,
            settings.CrossEncoderModel);

        try
        {
            var provider = CreateProvider(settings);
            var documents = hits.Select(h => h.Content).ToList();

            var scores = await provider.RerankAsync(
                query,
                documents,
                settings.CrossEncoderTopN > 0 ? settings.CrossEncoderTopN : null,
                cancellationToken);

            var scoreLookup = scores.ToDictionary(s => s.Index, s => s.Score);

            var scoredHits = new List<(SearchHit hit, float score)>();
            for (var i = 0; i < hits.Count; i++)
            {
                if (scoreLookup.TryGetValue(i, out var score))
                    scoredHits.Add((hits[i], score));
            }

            if (scoredHits.Count == 0)
            {
                _logger.LogWarning("Cross-encoder returned no scores, returning original order");
                return hits;
            }

            // Use provider scores directly. TEI sigmoid (raw_scores=false enforced
            // in TeiCrossEncoderProvider), Cohere, Jina, and AzureAIFoundry all
            // return [0,1] relevance scores.
            var rerankedHits = scoredHits
                .Select(s => s.hit with
                {
                    Score = s.score,
                    Metadata = new Dictionary<string, string>(s.hit.Metadata)
                    {
                        ["crossEncoderScore"] = s.score.ToString("F4"),
                        ["reranker"] = "CrossEncoder",
                        ["crossEncoderProvider"] = settings.CrossEncoderProvider
                    }
                })
                .OrderByDescending(h => h.Score)
                .ToList();

            _logger.LogInformation(
                "Cross-encoder reranking complete: {InputCount} -> {OutputCount} hits, score range [{Min:F4}, {Max:F4}]",
                hits.Count,
                rerankedHits.Count,
                scoredHits.Min(s => s.score),
                scoredHits.Max(s => s.score));

            return rerankedHits;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cross-encoder reranking failed, returning original order");
            return hits;
        }
    }

    private ICrossEncoderProvider CreateProvider(SearchSettings settings)
    {
        var httpClient = _httpClientFactory.CreateClient("CrossEncoder");

        var provider = settings.CrossEncoderProvider?.Trim().ToLowerInvariant();
        return provider switch
        {
            "cohere" => new CohereCrossEncoderProvider(httpClient, settings, _logger),
            "jina" => new JinaCrossEncoderProvider(httpClient, settings, _logger),
            "azureaifoundry" => new AzureAIFoundryCrossEncoderProvider(httpClient, settings, _logger),
            "voyage" => new VoyageCrossEncoderProvider(httpClient, settings, _logger),
            _ => new TeiCrossEncoderProvider(httpClient, settings, _logger)
        };
    }
}
