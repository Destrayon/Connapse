using System.ClientModel;
using Azure.AI.OpenAI;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;

namespace Connapse.Storage.Vectors;

/// <summary>
/// Embedding provider using the Azure OpenAI service via the official .NET SDK.
/// Requires an Azure OpenAI resource endpoint, API key, and deployment name.
/// </summary>
public class AzureOpenAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingClient _client;
    private readonly ILogger<AzureOpenAiEmbeddingProvider> _logger;
    private readonly EmbeddingSettings _settings;

    public AzureOpenAiEmbeddingProvider(
        IOptions<EmbeddingSettings> settings,
        ILogger<AzureOpenAiEmbeddingProvider> logger)
    {
        _logger = logger;
        _settings = settings.Value;

        if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
            throw new InvalidOperationException(
                "Azure OpenAI endpoint URL is required. Configure it in Settings > Embedding > Base URL " +
                "(e.g., https://your-resource.openai.azure.com).");

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new InvalidOperationException(
                "Azure OpenAI API key is required. Configure it in Settings > Embedding > API Key.");

        var deploymentName = !string.IsNullOrWhiteSpace(_settings.AzureDeploymentName)
            ? _settings.AzureDeploymentName
            : _settings.Model;

        var azureClient = new AzureOpenAIClient(
            new Uri(_settings.BaseUrl),
            new ApiKeyCredential(_settings.ApiKey));

        _client = azureClient.GetEmbeddingClient(deploymentName);
    }

    public int Dimensions => _settings.Dimensions;

    public string ModelId => !string.IsNullOrWhiteSpace(_settings.AzureDeploymentName)
        ? _settings.AzureDeploymentName
        : _settings.Model;

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
                "Embedded batch {BatchNum}/{TotalBatches} ({Count} texts) via Azure OpenAI",
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
            _logger.LogError(ex, "Azure OpenAI embedding request failed (status {Status})", ex.Status);
            throw new InvalidOperationException(
                $"Azure OpenAI embedding request failed (HTTP {ex.Status}): {ex.Message}. " +
                $"Verify your endpoint URL, API key, and deployment name '{ModelId}' are correct.", ex);
        }
    }

    private EmbeddingGenerationOptions? BuildOptions()
    {
        // Only send dimensions for text-embedding-3-* models
        if (_settings.Model.StartsWith("text-embedding-3", StringComparison.OrdinalIgnoreCase)
            && _settings.Dimensions > 0)
        {
            return new EmbeddingGenerationOptions { Dimensions = _settings.Dimensions };
        }

        return null;
    }
}
