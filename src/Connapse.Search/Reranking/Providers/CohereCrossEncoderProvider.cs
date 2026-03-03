using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Microsoft.Extensions.Logging;

namespace Connapse.Search.Reranking.Providers;

/// <summary>
/// Cross-encoder provider for Cohere Rerank API.
/// Cloud API: POST https://api.cohere.com/v1/rerank
/// </summary>
internal class CohereCrossEncoderProvider : ICrossEncoderProvider
{
    private const string BaseUrl = "https://api.cohere.com";

    private readonly HttpClient _httpClient;
    private readonly SearchSettings _settings;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CohereCrossEncoderProvider(HttpClient httpClient, SearchSettings settings, ILogger logger)
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
        var model = _settings.CrossEncoderModel ?? "rerank-v3.5";

        var request = new CohereRerankRequest
        {
            Model = model,
            Query = query,
            Documents = documents,
            TopN = topN
        };

        _logger.LogDebug("Cohere rerank: {Count} documents with model {Model}", documents.Count, model);

        var response = await _httpClient.PostAsJsonAsync("/v1/rerank", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CohereRerankResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Cohere returned null response");

        return result.Results
            .Select(r => new CrossEncoderScore(r.Index, (float)r.RelevanceScore))
            .OrderByDescending(s => s.Score)
            .ToList();
    }

    private record CohereRerankRequest
    {
        public required string Model { get; init; }
        public required string Query { get; init; }
        public required IReadOnlyList<string> Documents { get; init; }
        [JsonPropertyName("top_n")]
        public int? TopN { get; init; }
    }

    private record CohereRerankResponse
    {
        public List<CohereRerankResult> Results { get; init; } = [];
    }

    private record CohereRerankResult
    {
        public int Index { get; init; }
        public double RelevanceScore { get; init; }
    }
}
