using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Reranking.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace Connapse.Search.Reranking;

/// <summary>
/// Cross-encoder reranker that uses dedicated reranking models (TEI, Cohere, Jina)
/// to score (query, document) pairs. More accurate than RRF for relevance ranking.
/// </summary>
public class CrossEncoderReranker : ISearchReranker
{
    private readonly IOptionsMonitor<SearchSettings> _searchSettingsMonitor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RerankerAvailability _availability;
    private readonly ILogger<CrossEncoderReranker> _logger;

    public string Name => "CrossEncoder";

    /// <summary>
    /// Initializes a new instance of <see cref="CrossEncoderReranker"/> with the required settings monitor, HTTP client factory, and logger.
    /// </summary>
    public CrossEncoderReranker(
        IOptionsMonitor<SearchSettings> searchSettings,
        IHttpClientFactory httpClientFactory,
        RerankerAvailability availability,
        ILogger<CrossEncoderReranker> logger)
    {
        _searchSettingsMonitor = searchSettings;
        _httpClientFactory = httpClientFactory;
        _availability = availability;
        _logger = logger;
    }

    /// <inheritdoc />
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

        string endpoint = $"{settings.CrossEncoderProvider}|{settings.CrossEncoderBaseUrl}";
        if (_availability.IsSkipped(endpoint))
        {
            _logger.LogDebug("Cross-encoder {Provider} was unreachable moments ago, returning original order",
                settings.CrossEncoderProvider);
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

            if (_availability.MarkReachable(endpoint))
                _logger.LogInformation("Cross-encoder {Provider} is reachable again", settings.CrossEncoderProvider);

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
        catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
        {
            if (_availability.MarkUnreachable(endpoint))
                _logger.LogWarning(ex,
                    "Cross-encoder {Provider} at {BaseUrl} is unreachable; searches return the un-reranked order and retry it every {Cooldown}",
                    settings.CrossEncoderProvider, settings.CrossEncoderBaseUrl, RerankerAvailability.Cooldown);
            else
                _logger.LogDebug(ex, "Cross-encoder {Provider} still unreachable", settings.CrossEncoderProvider);
            return hits;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cross-encoder reranking failed, returning original order");
            return hits;
        }
    }

    /// <summary>
    /// Failures that say the service can't be reached: no connection or no answer in time (the
    /// request was not cancelled by the caller), or the client's circuit breaker already open. An
    /// error response is an answer, so it doesn't count.
    /// </summary>
    private static bool IsUnreachable(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        BrokenCircuitException => true,
        _ => false,
    };

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
