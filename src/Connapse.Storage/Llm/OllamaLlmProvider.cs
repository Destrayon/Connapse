using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Llm;

/// <summary>
/// LLM provider using Ollama's local chat API (POST /api/chat).
/// </summary>
public class OllamaLlmProvider : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaLlmProvider> _logger;
    private readonly LlmSettings _settings;

    public OllamaLlmProvider(
        HttpClient httpClient,
        IOptions<LlmSettings> settings,
        ILogger<OllamaLlmProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settings = settings.Value;

        if (!string.IsNullOrEmpty(_settings.BaseUrl))
            _httpClient.BaseAddress = new Uri(_settings.BaseUrl);

        _httpClient.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
    }

    public string Provider => "Ollama";
    public string ModelId => _settings.Model;

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        LlmCompletionOptions? options = null,
        CancellationToken ct = default)
    {
        var request = BuildRequest(systemPrompt, userPrompt, options, stream: false);

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/chat", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(ct);

            if (result?.Message?.Content is null)
                throw new InvalidOperationException("Ollama returned empty response");

            _logger.LogDebug("Ollama completion: {TokenCount} chars", result.Message.Content.Length);
            return result.Message.Content;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama at {BaseUrl}", _httpClient.BaseAddress);
            throw new InvalidOperationException(
                $"Failed to connect to Ollama at {_httpClient.BaseAddress}. " +
                $"Ensure Ollama is running and the model '{_settings.Model}' is available.", ex);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string userPrompt,
        LlmCompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = BuildRequest(systemPrompt, userPrompt, options, stream: true);

        HttpResponseMessage response;
        try
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
            {
                Content = JsonContent.Create(request)
            };

            response = await _httpClient.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama at {BaseUrl}", _httpClient.BaseAddress);
            throw new InvalidOperationException(
                $"Failed to connect to Ollama at {_httpClient.BaseAddress}. " +
                $"Ensure Ollama is running and the model '{_settings.Model}' is available.", ex);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var (content, done) = ParseStreamLine(line);
            if (!string.IsNullOrEmpty(content))
                yield return content;

            if (done)
                yield break;
        }
    }

    private (string? Content, bool Done) ParseStreamLine(string line)
    {
        try
        {
            var chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line);
            return (chunk?.Message?.Content, chunk?.Done ?? false);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    private OllamaChatRequest BuildRequest(
        string systemPrompt, string userPrompt,
        LlmCompletionOptions? options, bool stream)
    {
        var messages = new List<OllamaChatMessage>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new OllamaChatMessage { Role = "system", Content = systemPrompt });

        messages.Add(new OllamaChatMessage { Role = "user", Content = userPrompt });

        return new OllamaChatRequest
        {
            Model = _settings.Model,
            Messages = messages,
            Stream = stream,
            Options = new OllamaChatOptions
            {
                Temperature = options?.Temperature ?? (float)_settings.Temperature,
                NumPredict = options?.MaxTokens ?? _settings.MaxTokens
            }
        };
    }

    // DTOs for Ollama /api/chat
    private record OllamaChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;
        [JsonPropertyName("messages")] public List<OllamaChatMessage> Messages { get; init; } = [];
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        [JsonPropertyName("options")] public OllamaChatOptions? Options { get; init; }
    }

    private record OllamaChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; init; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; init; } = string.Empty;
    }

    private record OllamaChatOptions
    {
        [JsonPropertyName("temperature")] public float Temperature { get; init; }
        [JsonPropertyName("num_predict")] public int NumPredict { get; init; }
    }

    private record OllamaChatResponse
    {
        [JsonPropertyName("message")] public OllamaChatMessage? Message { get; init; }
        [JsonPropertyName("done")] public bool Done { get; init; }
    }
}
