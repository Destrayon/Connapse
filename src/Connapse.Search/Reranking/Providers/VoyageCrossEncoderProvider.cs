using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Microsoft.Extensions.Logging;

namespace Connapse.Search.Reranking.Providers;

/// <summary>
/// Cross-encoder provider for Voyage AI Rerank API.
/// Cloud API: POST https://api.voyageai.com/v1/rerank
/// </summary>
internal class VoyageCrossEncoderProvider : ICrossEncoderProvider
{
    private const string BaseUrl = "https://api.voyageai.com";

    private readonly HttpClient _httpClient;
    private readonly SearchSettings _settings;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public VoyageCrossEncoderProvider(HttpClient httpClient, SearchSettings settings, ILogger logger)
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
        var model = _settings.CrossEncoderModel ?? "rerank-2.5-lite";

        var request = new VoyageRerankRequest
        {
            Model = model,
            Query = query,
            Documents = documents,
            TopK = topN
        };

        _logger.LogDebug("Voyage rerank: {Count} documents with model {Model}", documents.Count, model);

        using var response = await _httpClient.PostAsJsonAsync("/v1/rerank", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<VoyageRerankResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Voyage returned null response");

        return result.Data
            .Select(r => new CrossEncoderScore(r.Index, (float)r.RelevanceScore))
            .OrderByDescending(s => s.Score)
            .ToList();
    }

    private record VoyageRerankRequest
    {
        public required string Model { get; init; }
        public required string Query { get; init; }
        public required IReadOnlyList<string> Documents { get; init; }
        [JsonPropertyName("top_k")]
        public int? TopK { get; init; }
    }

    private record VoyageRerankResponse
    {
        public List<VoyageRerankResult> Data { get; init; } = [];
    }

    private record VoyageRerankResult
    {
        public int Index { get; init; }
        public double RelevanceScore { get; init; }
    }
}
