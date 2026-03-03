using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Microsoft.Extensions.Logging;

namespace Connapse.Search.Reranking.Providers;

/// <summary>
/// Cross-encoder provider for Jina Reranker API.
/// Cloud API: POST https://api.jina.ai/v1/rerank
/// </summary>
internal class JinaCrossEncoderProvider : ICrossEncoderProvider
{
    private const string BaseUrl = "https://api.jina.ai";

    private readonly HttpClient _httpClient;
    private readonly SearchSettings _settings;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public JinaCrossEncoderProvider(HttpClient httpClient, SearchSettings settings, ILogger logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(settings.CrossEncoderTimeoutSeconds);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", settings.CrossEncoderApiKey);
    }

    public async Task<IReadOnlyList<CrossEncoderScore>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        int? topN,
        CancellationToken ct = default)
    {
        var model = _settings.CrossEncoderModel ?? "jina-reranker-v3";

        var request = new JinaRerankRequest
        {
            Model = model,
            Query = query,
            Documents = documents
        };

        _logger.LogDebug("Jina rerank: {Count} documents with model {Model}", documents.Count, model);

        var response = await _httpClient.PostAsJsonAsync("/v1/rerank", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JinaRerankResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Jina returned null response");

        var scores = result.Results
            .Select(r => new CrossEncoderScore(r.Index, (float)r.RelevanceScore))
            .OrderByDescending(s => s.Score)
            .ToList();

        if (topN is > 0)
            return scores.Take(topN.Value).ToList();

        return scores;
    }

    private record JinaRerankRequest
    {
        public required string Model { get; init; }
        public required string Query { get; init; }
        public required IReadOnlyList<string> Documents { get; init; }
    }

    private record JinaRerankResponse
    {
        public List<JinaRerankResult> Results { get; init; } = [];
    }

    private record JinaRerankResult
    {
        public int Index { get; init; }
        public double RelevanceScore { get; init; }
    }
}
