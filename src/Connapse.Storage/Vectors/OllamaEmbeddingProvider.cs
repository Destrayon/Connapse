using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Vectors;

/// <summary>
/// Embedding provider that uses Ollama's local embedding service.
/// Calls POST /api/embeddings endpoint.
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
        {
            throw new ArgumentException("Text cannot be empty", nameof(text));
        }

        try
        {
            var request = new OllamaEmbeddingRequest
            {
                Model = _settings.Model,
                Prompt = text
            };

            var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(ct);

            if (result?.Embedding == null || result.Embedding.Length == 0)
            {
                throw new InvalidOperationException("Ollama returned empty embedding");
            }

            if (result.Embedding.Length != _settings.Dimensions)
            {
                _logger.LogWarning(
                    "Embedding dimension mismatch: expected {Expected}, got {Actual}. Consider updating settings.",
                    _settings.Dimensions,
                    result.Embedding.Length);
            }

            return result.Embedding;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama at {BaseUrl}", _httpClient.BaseAddress);
            throw new InvalidOperationException(
                $"Failed to connect to Ollama embedding service at {_httpClient.BaseAddress}. " +
                $"Ensure Ollama is running and the model '{_settings.Model}' is available.", ex);
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IEnumerable<string> texts,
        CancellationToken ct = default)
    {
        var textList = texts.ToList();

        if (textList.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        // Ollama's /api/embeddings endpoint only handles single prompts,
        // so we batch by sending multiple requests in parallel
        var batchSize = _settings.BatchSize;
        var results = new List<float[]>();

        for (int i = 0; i < textList.Count; i += batchSize)
        {
            var batch = textList.Skip(i).Take(batchSize);
            var tasks = batch.Select(text => EmbedAsync(text, ct));
            var batchResults = await Task.WhenAll(tasks);
            results.AddRange(batchResults);

            _logger.LogDebug(
                "Embedded batch {BatchNum}/{TotalBatches} ({Count} texts)",
                (i / batchSize) + 1,
                (textList.Count + batchSize - 1) / batchSize,
                batchResults.Length);
        }

        return results;
    }

    // DTOs for Ollama API
    private record OllamaEmbeddingRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; init; } = string.Empty;
    }

    private record OllamaEmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; init; } = Array.Empty<float>();
    }
}
