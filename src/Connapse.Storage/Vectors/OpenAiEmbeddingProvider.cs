using System.ClientModel;
using System.ClientModel.Primitives;
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
        : this(settings, logger, transport: null)
    {
    }

    /// <summary>Sends requests through <paramref name="transport"/>, so tests can answer them.</summary>
    internal OpenAiEmbeddingProvider(
        IOptionsSnapshot<EmbeddingSettings> settings,
        ILogger<OpenAiEmbeddingProvider> logger,
        PipelineTransport? transport)
    {
        _logger = logger;
        _settings = settings.Value;

        var apiKey = _settings.OpenAiApiKey ?? _settings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "OpenAI API key is required. Configure it in Settings > Embedding > API Key.");

        var credential = new ApiKeyCredential(apiKey);
        var baseUrl = _settings.OpenAiBaseUrl ?? _settings.BaseUrl;

        var options = new OpenAIClientOptions();
        if (transport is not null)
            options.Transport = transport;
        if (!string.IsNullOrWhiteSpace(baseUrl) && !IsOllamaDefault(baseUrl))
            options.Endpoint = new Uri(baseUrl);
        _client = new EmbeddingClient(_settings.Model, credential, options);
    }

    /// <summary>An Ollama address left over from the default or from Compose, which must not receive the OpenAI key.</summary>
    private static bool IsOllamaDefault(string baseUrl)
    {
        string address = baseUrl.TrimEnd('/');
        return string.Equals(address, "http://localhost:11434", StringComparison.OrdinalIgnoreCase)
            || string.Equals(address, LocalModelOptions.BundledAddress.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    public int Dimensions => _settings.Dimensions;

    public string ModelId => _settings.Model;

    public async Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty", nameof(text));

        var results = await EmbedBatchAsync([text], inputType, ct);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IEnumerable<string> texts,
        EmbeddingInputType inputType,
        CancellationToken ct = default)
    {
        // The model's instruction for this side, if it has one (nomic's "search_query: "), and the
        // lowercasing an uncased model's tokenizer expects.
        var textList = EmbeddingText.Prepare(_settings, texts, inputType).ToList();

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
            var options = EmbeddingDimensions.Options(_settings);

            ClientResult<OpenAIEmbeddingCollection> result =
                await _client.GenerateEmbeddingsAsync(texts, options, ct);

            var embeddings = result.Value;
            var output = new float[embeddings.Count][];

            foreach (var embedding in embeddings)
            {
                output[embedding.Index] = embedding.ToFloats().ToArray();
            }

            foreach (var vector in output)
                EmbeddingDimensions.Check(_settings, ModelId, vector);

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
}
