using System.ClientModel;
using System.Diagnostics;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Embeddings;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to the OpenAI embeddings API by generating a test embedding.
/// </summary>
public class OpenAiConnectionTester : IConnectionTester
{
    private readonly ILogger<OpenAiConnectionTester> _logger;

    public OpenAiConnectionTester(ILogger<OpenAiConnectionTester> logger)
    {
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var embeddingSettings = settings as EmbeddingSettings;
            if (embeddingSettings == null)
                return ConnectionTestResult.CreateFailure("Invalid settings type");

            var apiKey = embeddingSettings.OpenAiApiKey ?? embeddingSettings.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                return ConnectionTestResult.CreateFailure("API Key is required for OpenAI");

            var model = embeddingSettings.Model;
            if (string.IsNullOrWhiteSpace(model))
                model = "text-embedding-3-small";

            var credential = new ApiKeyCredential(apiKey);
            EmbeddingClient client;
            var baseUrl = embeddingSettings.OpenAiBaseUrl ?? embeddingSettings.BaseUrl;

            if (!string.IsNullOrWhiteSpace(baseUrl) && baseUrl != "http://localhost:11434")
            {
                var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
                var openAiClient = new OpenAIClient(credential, options);
                client = openAiClient.GetEmbeddingClient(model);
            }
            else
            {
                client = new EmbeddingClient(model, credential);
            }

            _logger.LogDebug("Testing OpenAI embedding connection with model {Model}", model);

            var result = await client.GenerateEmbeddingAsync("connection test", cancellationToken: ct);
            var dimensions = result.Value.ToFloats().Length;

            stopwatch.Stop();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to OpenAI (model: {model}, dimensions: {dimensions})",
                new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["dimensions"] = dimensions
                },
                stopwatch.Elapsed);
        }
        catch (ClientResultException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "OpenAI connection test failed (status {Status})", ex.Status);

            return ConnectionTestResult.CreateFailure(
                $"OpenAI API error (HTTP {ex.Status}): {ex.Message}",
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["status"] = ex.Status
                },
                stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "OpenAI connection test failed");

            return ConnectionTestResult.CreateFailure(
                $"Connection failed: {ex.Message}",
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorType"] = ex.GetType().Name
                },
                stopwatch.Elapsed);
        }
    }
}
