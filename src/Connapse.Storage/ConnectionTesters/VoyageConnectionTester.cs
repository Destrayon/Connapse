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
/// Tests connectivity to the Voyage AI Rerank API by sending a minimal rerank request.
/// </summary>
public class VoyageConnectionTester(IHttpClientFactory httpClientFactory, ILogger<VoyageConnectionTester> logger) : IConnectionTester
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Tests connectivity to the Voyage AI Rerank API using the provided search settings.
    /// </summary>
    /// <param name="settings">A <see cref="SearchSettings"/> instance containing at minimum <c>CrossEncoderApiKey</c> and optional <c>CrossEncoderModel</c>.</param>
    /// <param name="timeout">Optional request timeout; defaults to 10 seconds when not provided.</param>
    /// <param name="ct">Cancellation token that, when signaled, cancels the test and results in an <see cref="OperationCanceledException"/> being thrown.</param>
    /// <returns>
    /// A <see cref="ConnectionTestResult"/> representing success or failure. On success the result includes metadata entries:
    /// <c>model</c> (the rerank model used) and <c>provider</c> set to "Voyage".
    /// On failure the result includes an <c>error</c> metadata entry with a descriptive message.
    /// </returns>
    /// <summary>
    /// Tests connectivity to the Voyage AI Rerank API using the provided search settings.
    /// </summary>
    /// <remarks>
    /// Performs a minimal rerank request to verify the API key and model are usable and reports timing and provider metadata on success; returns a failure result with error metadata on error or invalid input.
    /// </remarks>
    /// <param name="settings">A <see cref="SearchSettings"/> instance (provided as object) containing <c>CrossEncoderApiKey</c> and optional <c>CrossEncoderModel</c>. If <c>CrossEncoderApiKey</c> is missing or empty, a failure result is returned.</param>
    /// <param name="timeout">Optional request timeout; if not provided a default timeout is used.</param>
    /// <param name="ct">Cancellation token used to cancel the operation.</param>
    /// <returns>
    /// A <see cref="ConnectionTestResult"/> indicating success or failure. On success the result includes metadata entries for the chosen model and provider and the elapsed time; on failure the result includes an <c>error</c> metadata entry and the elapsed time.
    /// </returns>
    /// <exception cref="OperationCanceledException">Thrown when the provided <paramref name="ct"/> is canceled during the operation.</exception>
    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (settings is not SearchSettings searchSettings)
            return ConnectionTestResult.CreateFailure("Expected SearchSettings");

        if (string.IsNullOrWhiteSpace(searchSettings.CrossEncoderApiKey))
            return ConnectionTestResult.CreateFailure("API key is required for Voyage");

        try
        {
            var httpClient = httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri("https://api.voyageai.com");
            httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(10);
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", searchSettings.CrossEncoderApiKey);

            var model = string.IsNullOrWhiteSpace(searchSettings.CrossEncoderModel)
                ? "rerank-2.5-lite"
                : searchSettings.CrossEncoderModel;
            var request = new { model, query = "test", documents = new[] { "test document" }, top_k = 1 };

            using var response = await httpClient.PostAsJsonAsync("/v1/rerank", request, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            stopwatch.Stop();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to Voyage AI Rerank (model: {model})",
                new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["provider"] = "Voyage"
                },
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            logger.LogWarning(ex, "Voyage connection test failed");
            return ConnectionTestResult.CreateFailure(
                $"Connection failed: {ex.Message}",
                new Dictionary<string, object> { ["error"] = ex.Message },
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            throw;
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
            logger.LogError(ex, "Voyage connection test failed");
            return ConnectionTestResult.CreateFailure(
                $"Unexpected error: {ex.Message}",
                new Dictionary<string, object> { ["error"] = ex.Message },
                stopwatch.Elapsed);
        }
    }
}
