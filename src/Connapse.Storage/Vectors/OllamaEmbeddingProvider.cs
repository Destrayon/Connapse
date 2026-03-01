using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Vectors;

/// <summary>
/// Embedding provider that uses Ollama's local embedding service.
/// Uses POST /api/embed (batch endpoint) for all requests, which sends all texts in a
/// single HTTP round-trip instead of one request per text.
/// </summary>
public class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaEmbeddingProvider> _logger;
    private readonly EmbeddingSettings _settings;

    public OllamaEmbeddingProvider(
        HttpClient httpClient,
        IOptions<EmbeddingSettings> settings,
        ILogger<OllamaEmbeddingProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settings = settings.Value;

        if (!string.IsNullOrEmpty(_settings.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
        }

        _httpClient.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
    }

    public int Dimensions => _settings.Dimensions;

    public string ModelId => _settings.Model;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty", nameof(text));

        var results = await EmbedBatchAsync([text], ct);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IEnumerable<string> texts,
        CancellationToken ct = default)
    {
        var textList = texts.ToList();

        if (textList.Count == 0)
            return Array.Empty<float[]>();

        // Split into sub-batches to stay within Ollama's preferred request size.
        // Each sub-batch is ONE HTTP request (POST /api/embed with input array).
        var batchSize = _settings.BatchSize;
        var allEmbeddings = new List<float[]>(textList.Count);

        for (int i = 0; i < textList.Count; i += batchSize)
        {
            var batch = textList.GetRange(i, Math.Min(batchSize, textList.Count - i));
            var batchEmbeddings = await EmbedBatchRequestAsync(batch, ct);
            allEmbeddings.AddRange(batchEmbeddings);

            _logger.LogDebug(
                "Embedded batch {BatchNum}/{TotalBatches} ({Count} texts)",
                (i / batchSize) + 1,
                (textList.Count + batchSize - 1) / batchSize,
                batch.Count);
        }

        return allEmbeddings;
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchRequestAsync(
        List<string> texts,
        CancellationToken ct)
    {
        try
        {
            var request = new OllamaEmbedRequest
            {
                Model = _settings.Model,
                Input = texts
            };

            var response = await _httpClient.PostAsJsonAsync("/api/embed", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct);

            if (result?.Embeddings == null || result.Embeddings.Count == 0)
                throw new InvalidOperationException("Ollama /api/embed returned empty embeddings");

            return result.Embeddings;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama at {BaseUrl}", _httpClient.BaseAddress);
            throw new InvalidOperationException(
                $"Failed to connect to Ollama embedding service at {_httpClient.BaseAddress}. " +
                $"Ensure Ollama is running and the model '{_settings.Model}' is available.", ex);
        }
    }

    // DTOs for /api/embed (batch endpoint, Ollama ≥ 0.3.0)
    private record OllamaEmbedRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("input")]
        public List<string> Input { get; init; } = [];
    }

    private record OllamaEmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]> Embeddings { get; init; } = [];
    }
}
