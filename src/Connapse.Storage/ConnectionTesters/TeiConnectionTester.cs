using System.Diagnostics;
using System.Net.Http.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to a HuggingFace Text Embeddings Inference (TEI) server
/// by calling the /info endpoint.
/// </summary>
public class TeiConnectionTester(IHttpClientFactory httpClientFactory, ILogger<TeiConnectionTester> logger) : IConnectionTester
{
    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (settings is not SearchSettings searchSettings)
            return ConnectionTestResult.CreateFailure("Expected SearchSettings");

        var baseUrl = searchSettings.CrossEncoderBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return ConnectionTestResult.CreateFailure("Base URL is required for TEI");

        try
        {
            var httpClient = httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(baseUrl);
            httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(10);

            var response = await httpClient.GetAsync("/info", ct);
            response.EnsureSuccessStatusCode();

            var info = await response.Content.ReadFromJsonAsync<TeiInfoResponse>(ct);
            stopwatch.Stop();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to TEI (model: {info?.ModelId ?? "unknown"})",
                new Dictionary<string, object>
                {
                    ["model"] = info?.ModelId ?? "unknown",
                    ["provider"] = "TEI",
                    ["endpoint"] = baseUrl
                },
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            logger.LogWarning(ex, "TEI connection test failed for {BaseUrl}", baseUrl);
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
            logger.LogError(ex, "TEI connection test failed");
            return ConnectionTestResult.CreateFailure(
                $"Unexpected error: {ex.Message}",
                new Dictionary<string, object> { ["error"] = ex.Message },
                stopwatch.Elapsed);
        }
    }

    private record TeiInfoResponse
    {
        public string? ModelId { get; init; }
        public string? ModelType { get; init; }
    }
}
