using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to the Jina Reranker API by sending a minimal rerank request.
/// </summary>
public class JinaConnectionTester(IHttpClientFactory httpClientFactory, ILogger<JinaConnectionTester> logger) : IConnectionTester
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

        if (string.IsNullOrWhiteSpace(searchSettings.CrossEncoderApiKey))
            return ConnectionTestResult.CreateFailure("API key is required for Jina");

        try
        {
            var httpClient = httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri("https://api.jina.ai");
            httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(10);
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", searchSettings.CrossEncoderApiKey);

            var model = searchSettings.CrossEncoderModel ?? "jina-reranker-v3";
            var request = new { model, query = "test", documents = new[] { "test document" } };

            var response = await httpClient.PostAsJsonAsync("/v1/rerank", request, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            stopwatch.Stop();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to Jina Reranker (model: {model})",
                new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["provider"] = "Jina"
                },
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            logger.LogWarning(ex, "Jina connection test failed");
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
            logger.LogError(ex, "Jina connection test failed");
            return ConnectionTestResult.CreateFailure(
                $"Unexpected error: {ex.Message}",
                new Dictionary<string, object> { ["error"] = ex.Message },
                stopwatch.Elapsed);
        }
    }
}
