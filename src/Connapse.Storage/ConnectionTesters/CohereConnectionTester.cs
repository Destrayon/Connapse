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
/// Tests connectivity to the Cohere Rerank API by sending a minimal rerank request.
/// </summary>
public class CohereConnectionTester(IHttpClientFactory httpClientFactory, ILogger<CohereConnectionTester> logger) : IConnectionTester
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
            return ConnectionTestResult.CreateFailure("API key is required for Cohere");

        try
        {
            var httpClient = httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri("https://api.cohere.com");
            httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(10);
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", searchSettings.CrossEncoderApiKey);

            var model = searchSettings.CrossEncoderModel ?? "rerank-v3.5";
            var request = new { model, query = "test", documents = new[] { "test document" }, top_n = 1 };

            var response = await httpClient.PostAsJsonAsync("/v1/rerank", request, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            stopwatch.Stop();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to Cohere Rerank (model: {model})",
                new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["provider"] = "Cohere"
                },
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            logger.LogWarning(ex, "Cohere connection test failed");
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
            logger.LogError(ex, "Cohere connection test failed");
            return ConnectionTestResult.CreateFailure(
                $"Unexpected error: {ex.Message}",
                new Dictionary<string, object> { ["error"] = ex.Message },
                stopwatch.Elapsed);
        }
    }
}
