using System.ClientModel;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;

namespace Connapse.Storage.Vectors;

/// <summary>
/// Embedding provider using the OpenAI REST API via the official .NET SDK.
/// Supports text-embedding-3-small, text-embedding-3-large, and text-embedding-ada-002.
/// </summary>
public class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingClient _client;
    private readonly ILogger<OpenAiEmbeddingProvider> _logger;
    private readonly EmbeddingSettings _settings;

    public OpenAiEmbeddingProvider(
        IOptionsSnapshot<EmbeddingSettings> settings,
        ILogger<OpenAiEmbeddingProvider> logger)
    {
        _logger = logger;
        _settings = settings.Value;

        var apiKey = _settings.OpenAiApiKey ?? _settings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "OpenAI API key is required. Configure it in Settings > Embedding > API Key.");

        var credential = new ApiKeyCredential(apiKey);
        var baseUrl = _settings.OpenAiBaseUrl ?? _settings.BaseUrl;

        if (!string.IsNullOrWhiteSpace(baseUrl) && baseUrl != "http://localhost:11434")
        {
            var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
            var client = new OpenAIClient(credential, options);
            _client = client.GetEmbeddingClient(_settings.Model);
        }
        else
        {
            _client = new EmbeddingClient(_settings.Model, credential);
        }
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

        var batchSize = _settings.BatchSize;
        var allEmbeddings = new List<float[]>(textList.Count);

        for (int i = 0; i < textList.Count; i += batchSize)
        {
            var batch = textList.GetRange(i, Math.Min(batchSize, textList.Count - i));
            var batchEmbeddings = await EmbedBatchRequestAsync(batch, ct);
            allEmbeddings.AddRange(batchEmbeddings);

            _logger.LogDebug(
                "Embedded batch {BatchNum}/{TotalBatches} ({Count} texts) via OpenAI",
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
            var options = BuildOptions();

            ClientResult<OpenAIEmbeddingCollection> result =
                await _client.GenerateEmbeddingsAsync(texts, options, ct);

            var embeddings = result.Value;
            var output = new float[embeddings.Count][];

            foreach (var embedding in embeddings)
            {
                output[embedding.Index] = embedding.ToFloats().ToArray();
            }

            return output;
        }
        catch (ClientResultException ex)
        {
            _logger.LogError(ex, "OpenAI embedding request failed (status {Status})", ex.Status);
            throw new InvalidOperationException(
                $"OpenAI embedding request failed (HTTP {ex.Status}): {ex.Message}. " +
                $"Verify your API key and model '{_settings.Model}' are correct.", ex);
        }
    }

    private EmbeddingGenerationOptions? BuildOptions()
    {
        // Only send dimensions for text-embedding-3-* models (supports Matryoshka truncation)
        if (_settings.Model.StartsWith("text-embedding-3", StringComparison.OrdinalIgnoreCase)
            && _settings.Dimensions > 0)
        {
            return new EmbeddingGenerationOptions { Dimensions = _settings.Dimensions };
        }

        return null;
    }
}
