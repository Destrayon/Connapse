using System.Net.Http.Json;
using System.Text.Json;
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
        IOptionsSnapshot<EmbeddingSettings> settings,
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

    public async Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty", nameof(text));

        var results = await EmbedBatchAsync([text], inputType, ct);
        return results[0];
    }

    /// <summary>
    /// Embeds <paramref name="texts"/> in sub-batches of <see cref="EmbeddingSettings.BatchSize"/>,
    /// one vector per text in the order given. A text longer than the model accepts is split and
    /// its pieces' vectors pooled into one.
    /// </summary>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IEnumerable<string> texts,
        EmbeddingInputType inputType,
        CancellationToken ct = default)
    {
        // Kept as given as well as prepared: a text too long for the model is split before its
        // prompt is added, so every piece carries the prompt.
        List<string> rawList = texts.ToList();

        // The model's instruction for this side, if it has one (nomic's "search_query: "), and the
        // lowercasing an uncased model's tokenizer expects.
        var textList = EmbeddingText.Prepare(_settings, rawList, inputType).ToList();

        if (textList.Count == 0)
            return Array.Empty<float[]>();

        // Split into sub-batches to stay within Ollama's preferred request size.
        // Each sub-batch is ONE HTTP request (POST /api/embed with input array).
        var batchSize = _settings.BatchSize;
        var allEmbeddings = new List<float[]>(textList.Count);

        for (int i = 0; i < textList.Count; i += batchSize)
        {
            int count = Math.Min(batchSize, textList.Count - i);
            var batch = textList.GetRange(i, count);
            IReadOnlyList<float[]> batchEmbeddings;
            try
            {
                batchEmbeddings = await EmbedBatchRequestAsync(batch, ct);
            }
            catch (EmbeddingInputTooLongException)
            {
                // Ollama does not say which input was too long, so each is sent on its own and
                // only the ones it rejects are split.
                batchEmbeddings = await EmbedEachAsync(rawList.GetRange(i, count), inputType, ct);
            }
            allEmbeddings.AddRange(batchEmbeddings);

            _logger.LogDebug(
                "Embedded batch {BatchNum}/{TotalBatches} ({Count} texts)",
                (i / batchSize) + 1,
                (textList.Count + batchSize - 1) / batchSize,
                batch.Count);
        }

        return allEmbeddings;
    }

    /// <summary>
    /// Embeds each text in its own request, splitting any the model rejects, and returns one
    /// pooled vector per text.
    /// </summary>
    private async Task<IReadOnlyList<float[]>> EmbedEachAsync(
        List<string> rawTexts, EmbeddingInputType inputType, CancellationToken ct)
    {
        var embeddings = new List<float[]>(rawTexts.Count);
        foreach (string raw in rawTexts)
        {
            var pieces = new List<(float[] Vector, int Length)>();
            await EmbedPiecesAsync(raw, inputType, pieces, ct);
            if (pieces.Count > 1)
            {
                _logger.LogWarning(
                    "A {Length}-character text was longer than embedding model {Model} accepts, so it was embedded in {Pieces} pieces",
                    raw.Length, _settings.Model, pieces.Count);
            }
            embeddings.Add(OversizedText.Pool(pieces));
        }
        return embeddings;
    }

    /// <summary>
    /// Embeds <paramref name="raw"/> whole if the model accepts it, and otherwise halves it and
    /// embeds each half the same way, so every piece sent is one the model accepted.
    /// </summary>
    private async Task EmbedPiecesAsync(
        string raw, EmbeddingInputType inputType, List<(float[] Vector, int Length)> pieces, CancellationToken ct)
    {
        string prepared = EmbeddingText.Prepare(_settings, [raw], inputType)[0];
        try
        {
            IReadOnlyList<float[]> embedded = await EmbedBatchRequestAsync([prepared], ct);
            pieces.Add((embedded[0], raw.Length));
        }
        catch (EmbeddingInputTooLongException ex)
        {
            if (!OversizedText.TrySplit(raw, out string first, out string second))
            {
                throw new EmbeddingInputTooLongException(
                    $"Embedding model '{_settings.Model}' rejected a {raw.Length}-character piece of text as longer " +
                    $"than its context length, even after splitting. Ollama said: {ex.Reason}", ex.Reason);
            }
            await EmbedPiecesAsync(first, inputType, pieces, ct);
            await EmbedPiecesAsync(second, inputType, pieces, ct);
        }
    }

    /// <summary>
    /// Sends one POST /api/embed. Throws <see cref="EmbeddingInputTooLongException"/> when Ollama
    /// rejects an input as over the context length, and otherwise reports Ollama's own reason;
    /// only a request that never reached Ollama is reported as a connection failure.
    /// </summary>
    private async Task<IReadOnlyList<float[]>> EmbedBatchRequestAsync(
        List<string> texts,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            var request = new OllamaEmbedRequest
            {
                Model = _settings.Model,
                Input = texts
            };

            response = await _httpClient.PostAsJsonAsync("/api/embed", request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama at {BaseUrl}", _httpClient.BaseAddress);
            throw new InvalidOperationException(
                $"Failed to connect to Ollama embedding service at {_httpClient.BaseAddress}. " +
                $"Ensure Ollama is running and the model '{_settings.Model}' is available.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Ollama answered, so it is running: say what it said rather than blaming the connection.
                string reason = await ReadErrorAsync(response, ct);
                if (OversizedText.IsContextLengthError(reason))
                {
                    throw new EmbeddingInputTooLongException(
                        $"Embedding model '{_settings.Model}' rejected the text as longer than its context length. " +
                        $"Ollama said: {reason}", reason);
                }
                throw new InvalidOperationException(
                    $"Ollama rejected the embedding request for model '{_settings.Model}' " +
                    $"({(int)response.StatusCode} {response.StatusCode}): {reason}");
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct);

            if (result?.Embeddings == null || result.Embeddings.Count == 0)
                throw new InvalidOperationException("Ollama /api/embed returned empty embeddings");

            return result.Embeddings;
        }
    }

    /// <summary>Ollama's error body is <c>{"error": "..."}</c>; anything else is reported as sent.</summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.String)
            {
                return error.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Not JSON: the raw body is the best description available.
        }
        return body.Length > 0 ? body : response.ReasonPhrase ?? "no error message";
    }

    // DTOs for /api/embed (batch endpoint, Ollama ≥ 0.3.0)
    private record OllamaEmbedRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("input")]
        public List<string> Input { get; init; } = [];

        // Ollama's default is true: it cuts a too-long input to fit and embeds only its start,
        // without saying so. False makes it refuse instead, so the text is split and embedded whole.
        [JsonPropertyName("truncate")]
        public bool Truncate { get; init; }
    }

    private record OllamaEmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]> Embeddings { get; init; } = [];
    }
}
