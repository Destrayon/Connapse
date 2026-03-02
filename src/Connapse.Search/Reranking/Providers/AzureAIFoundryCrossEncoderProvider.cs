using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Microsoft.Extensions.Logging;

namespace Connapse.Search.Reranking.Providers;

/// <summary>
/// Cross-encoder provider for Azure AI Foundry (serverless model deployments).
/// Supports Cohere, Jina, and other reranking models hosted on Azure AI.
/// Uses api-key header authentication.
/// Handles both model-specific endpoints (*.models.ai.azure.com → /v1/rerank)
/// and Azure AI Services gateway (*.services.ai.azure.com → /v2/rerank).
/// </summary>
internal class AzureAIFoundryCrossEncoderProvider : ICrossEncoderProvider
{
    private readonly HttpClient _httpClient;
    private readonly SearchSettings _settings;
    private readonly ILogger _logger;
    private readonly string _rerankUrl;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AzureAIFoundryCrossEncoderProvider(HttpClient httpClient, SearchSettings settings, ILogger logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;

        if (string.IsNullOrEmpty(settings.CrossEncoderBaseUrl))
            throw new InvalidOperationException("Base URL is required for Azure AI Foundry");

        _rerankUrl = BuildRerankUrl(settings.CrossEncoderBaseUrl, settings.CrossEncoderModel);
        _httpClient.Timeout = TimeSpan.FromSeconds(settings.CrossEncoderTimeoutSeconds);
        _httpClient.DefaultRequestHeaders.Add("api-key", settings.CrossEncoderApiKey);
    }

    public async Task<IReadOnlyList<CrossEncoderScore>> RerankAsync(
        string query,
        IReadOnlyList<string> documents,
        int? topN,
        CancellationToken ct = default)
    {
        var model = _settings.CrossEncoderModel;

        var request = new FoundryRerankRequest
        {
            Model = model,
            Query = query,
            Documents = documents,
            TopN = topN
        };

        _logger.LogDebug("Azure AI Foundry rerank: {Count} documents with model {Model} at {Url}",
            documents.Count, model ?? "(default)", _rerankUrl);

        var response = await _httpClient.PostAsJsonAsync(_rerankUrl, request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<FoundryRerankResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Azure AI Foundry returned null response");

        return result.Results
            .Select(r => new CrossEncoderScore(r.Index, (float)r.RelevanceScore))
            .OrderByDescending(s => s.Score)
            .ToList();
    }

    private record FoundryRerankRequest
    {
        public string? Model { get; init; }
        public required string Query { get; init; }
        public required IReadOnlyList<string> Documents { get; init; }
        [JsonPropertyName("top_n")]
        public int? TopN { get; init; }
    }

    private record FoundryRerankResponse
    {
        public List<FoundryRerankResult> Results { get; init; } = [];
    }

    private record FoundryRerankResult
    {
        public int Index { get; init; }
        public double RelevanceScore { get; init; }
    }

    /// <summary>
    /// Builds the full rerank URL from the base URL.
    /// - If the URL already contains a rerank path, uses it as-is (advanced override).
    /// - For Azure AI Services gateway (*.services.ai.azure.com), uses /providers/{provider}/v2/rerank.
    /// - For model-specific deployments (*.models.ai.azure.com) and others, uses /v1/rerank.
    /// </summary>
    internal static string BuildRerankUrl(string baseUrl, string? model = null)
    {
        var trimmed = baseUrl.TrimEnd('/');

        // User provided the full Target URI from Azure — use as-is
        if (trimmed.Contains("/rerank", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        // Azure AI Services gateway routes through /providers/{provider}/v2/rerank
        if (trimmed.Contains(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var provider = InferProviderSlug(model);
            return $"{trimmed}/providers/{provider}/v2/rerank";
        }

        // Model-specific deployments (*.models.ai.azure.com) use Cohere-native /v1/rerank
        return $"{trimmed}/v1/rerank";
    }

    /// <summary>
    /// Infers the Azure AI Foundry provider path slug from the model name.
    /// e.g. "Cohere-rerank-v4.0-fast" → "cohere", "jina-reranker-v3" → "jina".
    /// </summary>
    private static string InferProviderSlug(string? model)
    {
        if (string.IsNullOrEmpty(model))
            return "cohere";

        if (model.StartsWith("Cohere", StringComparison.OrdinalIgnoreCase))
            return "cohere";

        if (model.StartsWith("jina", StringComparison.OrdinalIgnoreCase))
            return "jina";

        return "cohere";
    }
}
