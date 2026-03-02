using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to an Azure AI Foundry reranking model deployment.
/// </summary>
public class AzureAIFoundryConnectionTester(IHttpClientFactory httpClientFactory, ILogger<AzureAIFoundryConnectionTester> logger) : IConnectionTester
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (settings is not SearchSettings searchSettings)
            return ConnectionTestResult.CreateFailure("Expected SearchSettings");

        if (string.IsNullOrWhiteSpace(searchSettings.CrossEncoderBaseUrl))
            return ConnectionTestResult.CreateFailure("Base URL is required for Azure AI Foundry");

        if (string.IsNullOrWhiteSpace(searchSettings.CrossEncoderApiKey))
            return ConnectionTestResult.CreateFailure("API key is required for Azure AI Foundry");

        try
        {
            var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(10);
            httpClient.DefaultRequestHeaders.Add("api-key", searchSettings.CrossEncoderApiKey);

            var rerankUrl = BuildRerankUrl(searchSettings.CrossEncoderBaseUrl, searchSettings.CrossEncoderModel);
            var model = searchSettings.CrossEncoderModel;
            var request = new { model, query = "test", documents = new[] { "test document" }, top_n = 1 };

            logger.LogDebug("Testing Azure AI Foundry rerank at {Url}", rerankUrl);
            var response = await httpClient.PostAsJsonAsync(rerankUrl, request, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            stopwatch.Stop();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to Azure AI Foundry{(model is not null ? $" (model: {model})" : "")}",
                new Dictionary<string, object>
                {
                    ["provider"] = "AzureAIFoundry",
                    ["baseUrl"] = searchSettings.CrossEncoderBaseUrl
                },
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            logger.LogWarning(ex, "Azure AI Foundry connection test failed");
            return ConnectionTestResult.CreateFailure(
                $"Connection failed: {ex.Message}",
                new Dictionary<string, object> { ["error"] = ex.Message },
                stopwatch.Elapsed);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ConnectionTestResult.CreateFailure(
                $"Connection timed out after {(timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds:F1}s",
                new Dictionary<string, object> { ["error"] = "Timeout" },
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "Azure AI Foundry connection test failed");
            return ConnectionTestResult.CreateFailure(
                $"Unexpected error: {ex.Message}",
                new Dictionary<string, object> { ["error"] = ex.Message },
                stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Builds the full rerank URL from the base URL.
    /// - If the URL already contains a rerank path, uses it as-is.
    /// - For Azure AI Services gateway (*.services.ai.azure.com), uses /providers/{provider}/v2/rerank.
    /// - For model-specific deployments and others, uses /v1/rerank.
    /// </summary>
    internal static string BuildRerankUrl(string baseUrl, string? model = null)
    {
        var trimmed = baseUrl.TrimEnd('/');

        if (trimmed.Contains("/rerank", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        if (trimmed.Contains(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var provider = InferProviderSlug(model);
            return $"{trimmed}/providers/{provider}/v2/rerank";
        }

        return $"{trimmed}/v1/rerank";
    }

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
