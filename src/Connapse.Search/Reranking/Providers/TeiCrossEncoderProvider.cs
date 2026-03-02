using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Microsoft.Extensions.Logging;

namespace Connapse.Search.Reranking.Providers;

/// <summary>
/// Cross-encoder provider for HuggingFace Text Embeddings Inference (TEI).
/// Self-hosted via Docker: docker run ghcr.io/huggingface/text-embeddings-inference --model-id BAAI/bge-reranker-large
/// </summary>
internal class TeiCrossEncoderProvider : ICrossEncoderProvider
{
    private readonly HttpClient _httpClient;
    private readonly SearchSettings _settings;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TeiCrossEncoderProvider(HttpClient httpClient, SearchSettings settings, ILogger logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;

        if (!string.IsNullOrEmpty(settings.CrossEncoderBaseUrl))
            _httpClient.BaseAddress = new Uri(settings.CrossEncoderBaseUrl);

        _httpClient.Timeout = TimeSpan.FromSeconds(settings.CrossEncoderTimeoutSeconds);
    }

    public async Task<IReadOnlyList<CrossEncoderScore>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        int? topN,
        CancellationToken ct = default)
    {
        var request = new TeiRerankRequest
        {
            Query = query,
            Texts = documents,
            RawScores = false
        };

        _logger.LogDebug("TEI rerank: {Count} documents via {BaseUrl}", documents.Count, _settings.CrossEncoderBaseUrl);

        var response = await _httpClient.PostAsJsonAsync("/rerank", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<List<TeiRerankResult>>(JsonOptions, ct)
            ?? throw new InvalidOperationException("TEI returned null response");

        var scores = results
            .Select(r => new CrossEncoderScore(r.Index, (float)r.Score))
            .OrderByDescending(s => s.Score)
            .ToList();

        if (topN is > 0)
            return scores.Take(topN.Value).ToList();

        return scores;
    }

    private record TeiRerankRequest
    {
        public required string Query { get; init; }
        public required IReadOnlyList<string> Texts { get; init; }
        public bool RawScores { get; init; }
    }

    private record TeiRerankResult
    {
        public int Index { get; init; }
        public double Score { get; init; }
    }
}
