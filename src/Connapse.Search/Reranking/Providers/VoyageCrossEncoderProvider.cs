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

    /// <summary>
    /// Initializes a new instance of <see cref="VoyageCrossEncoderProvider"/> and configures the HTTP client with Voyage API defaults (base address, timeout, and bearer authorization).
    /// </summary>
    /// <param name="httpClient">An <see cref="HttpClient"/> instance used to send requests to the Voyage API.</param>
    /// <param name="settings">Search settings containing the cross-encoder API key and timeout values.</param>
    /// <summary>
    /// Initializes a new instance of <see cref="VoyageCrossEncoderProvider"/> and configures the HTTP client to call Voyage AI's rerank API.
    /// </summary>
    /// <param name="httpClient">The HTTP client used to send requests to the Voyage API.</param>
    /// <param name="settings">Search settings that supply the CrossEncoder API key, timeout, and optional model selection.</param>
    /// <param name="logger">An <see cref="ILogger"/> used for diagnostic logging.</param>
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

    /// <summary>
    /// Reranks the provided candidate documents for the given query using Voyage AI's rerank model.
    /// </summary>
    /// <param name="topN">Maximum number of top results to return; pass <c>null</c> to return scores for all documents.</param>
    /// <summary>
    /// Reranks candidate documents for a query using Voyage AI's Cross-Encoder Rerank API.
    /// </summary>
    /// <param name="query">The user query to score documents against.</param>
    /// <param name="documents">The candidate documents to be reranked.</param>
    /// <param name="topN">Optional maximum number of top documents to request from the service; pass null to request all.</param>
    /// <returns>A list of <see cref="CrossEncoderScore"/> items ordered by descending score.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the Voyage API returns a null response.</exception>
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
