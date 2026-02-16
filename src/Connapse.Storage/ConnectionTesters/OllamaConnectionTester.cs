using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIKnowledge.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to Ollama service (used for both Embedding and LLM settings).
/// Calls GET /api/tags to verify service availability and list models.
/// </summary>
public class OllamaConnectionTester : IConnectionTester
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OllamaConnectionTester> _logger;

    public OllamaConnectionTester(
        IHttpClientFactory httpClientFactory,
        ILogger<OllamaConnectionTester> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        timeout ??= TimeSpan.FromSeconds(10);

        try
        {
            // Extract BaseUrl from settings (works for both EmbeddingSettings and LlmSettings)
            var baseUrl = GetBaseUrl(settings);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return ConnectionTestResult.CreateFailure(
                    "BaseUrl is required",
                    new Dictionary<string, object> { ["error"] = "Missing BaseUrl in settings" });
            }

            // Create HTTP client with timeout
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(baseUrl);
            httpClient.Timeout = timeout.Value;

            _logger.LogDebug("Testing Ollama connection to {BaseUrl}", baseUrl);

            // Call GET /api/tags to list available models
            var response = await httpClient.GetAsync("/api/tags", ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(ct);

            stopwatch.Stop();

            if (result?.Models == null || result.Models.Count == 0)
            {
                return ConnectionTestResult.CreateSuccess(
                    $"Connected to Ollama at {baseUrl} (no models available)",
                    new Dictionary<string, object>
                    {
                        ["baseUrl"] = baseUrl,
                        ["modelCount"] = 0
                    },
                    stopwatch.Elapsed);
            }

            // Extract model names
            var modelNames = result.Models.Select(m => m.Name).ToList();
            var message = $"Connected to Ollama at {baseUrl} ({modelNames.Count} model{(modelNames.Count != 1 ? "s" : "")} available)";

            return ConnectionTestResult.CreateSuccess(
                message,
                new Dictionary<string, object>
                {
                    ["baseUrl"] = baseUrl,
                    ["modelCount"] = modelNames.Count,
                    ["models"] = modelNames
                },
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Ollama connection test failed");

            return ConnectionTestResult.CreateFailure(
                $"Connection failed: {ex.Message}",
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorType"] = ex.GetType().Name
                },
                stopwatch.Elapsed);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Ollama connection test timed out");

            return ConnectionTestResult.CreateFailure(
                $"Connection timed out after {timeout.Value.TotalSeconds:F1}s",
                new Dictionary<string, object>
                {
                    ["error"] = "Timeout",
                    ["timeoutSeconds"] = timeout.Value.TotalSeconds
                },
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Ollama connection test failed with unexpected error");

            return ConnectionTestResult.CreateFailure(
                $"Unexpected error: {ex.Message}",
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorType"] = ex.GetType().Name
                },
                stopwatch.Elapsed);
        }
    }

    private static string? GetBaseUrl(object settings)
    {
        // Use reflection to get BaseUrl property (works for both EmbeddingSettings and LlmSettings)
        var baseUrlProperty = settings.GetType().GetProperty("BaseUrl");
        return baseUrlProperty?.GetValue(settings)?.ToString();
    }

    // DTOs for Ollama API
    private record OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModel> Models { get; init; } = new();
    }

    private record OllamaModel
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("modified_at")]
        public string ModifiedAt { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }
}
